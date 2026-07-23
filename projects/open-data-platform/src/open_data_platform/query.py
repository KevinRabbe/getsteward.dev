from __future__ import annotations

import json
import sqlite3
from pathlib import Path
from typing import Any

from .errors import ProductError, QueryError
from .product import verify_product
from .util import load_json

_SELECT_COLUMNS = (
    "lei",
    "legal_name",
    "legal_name_language",
    "entity_status",
    "legal_jurisdiction",
    "entity_category",
    "entity_subcategory",
    "entity_creation_date",
    "legal_form_json",
    "registration_authority_json",
    "legal_address_json",
    "headquarters_address_json",
    "registration_json",
)


def _product_root(data_root: Path, product_root: Path | None) -> Path:
    return product_root if product_root is not None else data_root / "products"


def _latest_snapshot_id(data_root: Path, product_root: Path | None) -> str:
    root = _product_root(data_root, product_root)
    candidates: list[tuple[str, str]] = []
    for manifest_path in root.glob("*/*/product.json"):
        try:
            manifest = load_json(manifest_path)
        except (OSError, ValueError):
            continue
        snapshot_id = manifest.get("source_snapshot_id")
        source_version = manifest.get("source_version")
        if isinstance(snapshot_id, str) and isinstance(source_version, str):
            candidates.append((source_version, snapshot_id))
    if not candidates:
        raise QueryError(f"No built products found under {root}")

    for _, snapshot_id in sorted(candidates, reverse=True):
        try:
            verify_product(data_root, snapshot_id, output_root=root)
        except (ProductError, OSError, ValueError):
            continue
        return snapshot_id
    raise QueryError("No verified product is available for read-only querying")


def _record_from_row(row: sqlite3.Row) -> dict[str, Any]:
    record: dict[str, Any] = {
        "lei": row["lei"],
        "legal_name": row["legal_name"],
        "entity_status": row["entity_status"],
        "legal_address": json.loads(row["legal_address_json"]),
        "headquarters_address": json.loads(row["headquarters_address_json"]),
        "registration": json.loads(row["registration_json"]),
    }
    for output_name, column_name in (
        ("legal_name_language", "legal_name_language"),
        ("legal_jurisdiction", "legal_jurisdiction"),
        ("entity_category", "entity_category"),
        ("entity_subcategory", "entity_subcategory"),
        ("entity_creation_date", "entity_creation_date"),
    ):
        if row[column_name] is not None:
            record[output_name] = row[column_name]
    for output_name, column_name in (
        ("legal_form", "legal_form_json"),
        ("registration_authority", "registration_authority_json"),
    ):
        if row[column_name] is not None:
            record[output_name] = json.loads(row[column_name])
    return record


def _provenance(verified: dict[str, Any]) -> dict[str, Any]:
    product = verified["product"]
    return {
        "snapshot_id": verified["snapshot_id"],
        "source_version": product["source_version"],
        "source_content_id": product["source_content_id"],
        "product_sha256": product["product_sha256"],
        "record_count": product["record_count"],
    }


class QueryStore:
    """A verified, read-only view of one immutable SQLite product."""

    def __init__(
        self,
        data_root: Path,
        *,
        snapshot_id: str | None = None,
        product_root: Path | None = None,
    ) -> None:
        self.data_root = data_root
        self.root = _product_root(data_root, product_root)
        self.snapshot_id = snapshot_id or _latest_snapshot_id(data_root, self.root)
        try:
            self.verified = verify_product(data_root, self.snapshot_id, output_root=self.root)
        except (ProductError, OSError, ValueError) as exc:
            raise QueryError(f"Product verification failed for {self.snapshot_id}: {exc}") from exc

    def _connection(self) -> sqlite3.Connection:
        database_path = Path(self.verified["database_path"])
        try:
            connection = sqlite3.connect(database_path.resolve().as_uri() + "?mode=ro", uri=True)
        except sqlite3.Error as exc:
            raise QueryError(f"Could not open product read-only: {database_path}") from exc
        connection.row_factory = sqlite3.Row
        return connection

    def health(self) -> dict[str, Any]:
        return {
            "status": "OK",
            "service": "open-data-platform",
            "provenance": _provenance(self.verified),
        }

    def lookup(self, lei: str) -> dict[str, Any]:
        normalized_lei = lei.strip().upper()
        if len(normalized_lei) != 20:
            raise QueryError("LEI must contain exactly 20 characters")
        connection = self._connection()
        try:
            row = connection.execute(
                f"SELECT {', '.join(_SELECT_COLUMNS)} FROM lei WHERE lei = ?",
                (normalized_lei,),
            ).fetchone()
        finally:
            connection.close()
        result: dict[str, Any] = {
            "status": "FOUND" if row is not None else "NOT_FOUND",
            "query": normalized_lei,
            "provenance": _provenance(self.verified),
        }
        if row is not None:
            result["record"] = _record_from_row(row)
        return result

    def search(self, query: str, *, limit: int = 20) -> dict[str, Any]:
        normalized_query = query.strip()
        if not normalized_query:
            raise QueryError("Name query must not be empty")
        if limit < 1 or limit > 1000:
            raise QueryError("limit must be between 1 and 1000")

        escaped = normalized_query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
        connection = self._connection()
        try:
            rows = connection.execute(
                f"SELECT {', '.join(_SELECT_COLUMNS)} FROM lei "
                "WHERE legal_name LIKE ? ESCAPE '\\' "
                "ORDER BY legal_name COLLATE NOCASE, lei LIMIT ?",
                (f"%{escaped}%", limit),
            ).fetchall()
        finally:
            connection.close()

        return {
            "status": "OK",
            "query": normalized_query,
            "limit": limit,
            "count": len(rows),
            "records": [_record_from_row(row) for row in rows],
            "provenance": _provenance(self.verified),
        }


def lookup_lei(
    data_root: Path,
    lei: str,
    *,
    snapshot_id: str | None = None,
    product_root: Path | None = None,
) -> dict[str, Any]:
    return QueryStore(data_root, snapshot_id=snapshot_id, product_root=product_root).lookup(lei)


def search_name(
    data_root: Path,
    query: str,
    *,
    snapshot_id: str | None = None,
    product_root: Path | None = None,
    limit: int = 20,
) -> dict[str, Any]:
    return QueryStore(data_root, snapshot_id=snapshot_id, product_root=product_root).search(query, limit=limit)
