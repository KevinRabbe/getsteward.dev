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


def _open_verified_product(
    data_root: Path,
    snapshot_id: str | None,
    product_root: Path | None,
) -> tuple[sqlite3.Connection, dict[str, Any]]:
    root = _product_root(data_root, product_root)
    selected_snapshot = snapshot_id or _latest_snapshot_id(data_root, root)
    try:
        verified = verify_product(data_root, selected_snapshot, output_root=root)
    except (ProductError, OSError, ValueError) as exc:
        raise QueryError(f"Product verification failed for {selected_snapshot}: {exc}") from exc
    database_path = Path(verified["database_path"])
    try:
        connection = sqlite3.connect(database_path.as_uri() + "?mode=ro", uri=True)
    except sqlite3.Error as exc:
        raise QueryError(f"Could not open product read-only: {database_path}") from exc
    connection.row_factory = sqlite3.Row
    return connection, verified


def lookup_lei(
    data_root: Path,
    lei: str,
    *,
    snapshot_id: str | None = None,
    product_root: Path | None = None,
) -> dict[str, Any]:
    normalized_lei = lei.strip().upper()
    if len(normalized_lei) != 20:
        raise QueryError("LEI must contain exactly 20 characters")
    connection, verified = _open_verified_product(data_root, snapshot_id, product_root)
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
        "provenance": _provenance(verified),
    }
    if row is not None:
        result["record"] = _record_from_row(row)
    return result


def search_name(
    data_root: Path,
    query: str,
    *,
    snapshot_id: str | None = None,
    product_root: Path | None = None,
    limit: int = 20,
) -> dict[str, Any]:
    normalized_query = query.strip()
    if not normalized_query:
        raise QueryError("Name query must not be empty")
    if limit < 1 or limit > 1000:
        raise QueryError("limit must be between 1 and 1000")

    escaped = normalized_query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
    connection, verified = _open_verified_product(data_root, snapshot_id, product_root)
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
        "provenance": _provenance(verified),
    }
