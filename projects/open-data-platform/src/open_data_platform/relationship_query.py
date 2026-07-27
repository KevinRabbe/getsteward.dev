from __future__ import annotations

import json
import re
import sqlite3
from pathlib import Path
from typing import Any

from .errors import QueryError
from .relationship_product import verify_relationship_product


_LEI_RE = re.compile(r"^[A-Z0-9]{18}[0-9]{2}$")


def _decode_row(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "start_node": {"id": row["start_node_id"], "type": row["start_node_type"]},
        "end_node": {"id": row["end_node_id"], "type": row["end_node_type"]},
        "relationship_type": row["relationship_type"],
        "relationship_status": row["relationship_status"],
        "relationship_periods": json.loads(row["relationship_periods_json"]),
        "relationship_qualifiers": json.loads(row["relationship_qualifiers_json"]),
        "relationship_quantifiers": json.loads(row["relationship_quantifiers_json"]),
        "registration": json.loads(row["registration_json"]),
    }


def relationships_for_lei(
    data_root: Path,
    snapshot_id: str,
    lei: str,
    *,
    product_root: Path | None = None,
    direction: str = "both",
    relationship_type: str | None = None,
    limit: int = 500,
) -> dict[str, Any]:
    lei = lei.strip().upper()
    if _LEI_RE.fullmatch(lei) is None:
        raise QueryError(f"Invalid LEI: {lei!r}")
    if direction not in {"outgoing", "incoming", "both"}:
        raise QueryError("direction must be outgoing, incoming, or both")
    if limit < 1 or limit > 5000:
        raise QueryError("limit must be between 1 and 5000")

    verified = verify_relationship_product(data_root, snapshot_id, output_root=product_root)
    database_path = Path(verified["database_path"])
    uri = database_path.resolve().as_uri() + "?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    connection.row_factory = sqlite3.Row
    try:
        clauses: list[str] = []
        params: list[Any] = []
        if direction == "outgoing":
            clauses.append("start_node_id = ?")
            params.append(lei)
        elif direction == "incoming":
            clauses.append("end_node_id = ?")
            params.append(lei)
        else:
            clauses.append("(start_node_id = ? OR end_node_id = ?)")
            params.extend([lei, lei])
        if relationship_type:
            clauses.append("relationship_type = ?")
            params.append(relationship_type)
        params.append(limit)
        rows = connection.execute(
            "SELECT * FROM relationship WHERE " + " AND ".join(clauses) +
            " ORDER BY relationship_type, start_node_id, end_node_id, row_id LIMIT ?",
            params,
        ).fetchall()
    finally:
        connection.close()

    return {
        "status": "FOUND" if rows else "NOT_FOUND",
        "lei": lei,
        "direction": direction,
        "relationship_type": relationship_type,
        "snapshot_id": snapshot_id,
        "source_version": verified["product"]["source_version"],
        "count": len(rows),
        "relationships": [_decode_row(row) for row in rows],
        "product_sha256": verified["product"]["product_sha256"],
    }
