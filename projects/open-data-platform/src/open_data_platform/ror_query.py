from __future__ import annotations

import json
import sqlite3
from pathlib import Path
from typing import Any

from .errors import ParseError, QueryError
from .ror_parser import canonical_ror_id
from .ror_product import verify_ror_product


def _decode(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "ror_id": row["ror_id"],
        "display_name": row["display_name"],
        "status": row["status"],
        "established": row["established"],
        "types": json.loads(row["types_json"]),
        "domains": json.loads(row["domains_json"]),
        "names": json.loads(row["names_json"]),
        "external_ids": json.loads(row["external_ids_json"]),
        "links": json.loads(row["links_json"]),
        "relationships": json.loads(row["relationships_json"]),
        "admin": json.loads(row["admin_json"]),
    }


def lookup_ror(
    data_root: Path,
    snapshot_id: str,
    ror_id: str,
    *,
    product_root: Path | None = None,
) -> dict[str, Any]:
    try:
        value = canonical_ror_id(ror_id)
    except ParseError as exc:
        raise QueryError(str(exc)) from exc

    verified = verify_ror_product(data_root, snapshot_id, output_root=product_root)
    database = Path(verified["database_path"])
    connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    try:
        row = connection.execute("SELECT * FROM organization WHERE ror_id = ?", (value,)).fetchone()
    finally:
        connection.close()
    return {
        "status": "FOUND" if row is not None else "NOT_FOUND",
        "ror_id": value,
        "snapshot_id": snapshot_id,
        "source_version": verified["product"]["source_version"],
        "excluded_source_fields": verified["product"]["excluded_source_fields"],
        "organization": _decode(row) if row is not None else None,
        "product_sha256": verified["product"]["product_sha256"],
    }


def search_ror_name(
    data_root: Path,
    snapshot_id: str,
    query: str,
    *,
    product_root: Path | None = None,
    limit: int = 20,
) -> dict[str, Any]:
    query = query.strip()
    if not query:
        raise QueryError("ROR name query must not be empty")
    if len(query) > 500:
        raise QueryError("ROR name query exceeds 500 characters")
    if limit < 1 or limit > 500:
        raise QueryError("limit must be between 1 and 500")

    verified = verify_ror_product(data_root, snapshot_id, output_root=product_root)
    database = Path(verified["database_path"])
    connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    try:
        rows = connection.execute(
            """
            SELECT * FROM organization
            WHERE display_name LIKE ? ESCAPE '\\'
            ORDER BY display_name COLLATE NOCASE, ror_id
            LIMIT ?
            """,
            ("%" + query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%", limit),
        ).fetchall()
    finally:
        connection.close()
    return {
        "status": "FOUND" if rows else "NOT_FOUND",
        "query": query,
        "snapshot_id": snapshot_id,
        "source_version": verified["product"]["source_version"],
        "count": len(rows),
        "organizations": [_decode(row) for row in rows],
        "excluded_source_fields": verified["product"]["excluded_source_fields"],
        "product_sha256": verified["product"]["product_sha256"],
    }
