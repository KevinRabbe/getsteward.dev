from __future__ import annotations

import json
import shutil
import sqlite3
import uuid
from pathlib import Path
from typing import Any

from .errors import ProductError
from .events import append_event
from .parser import normalized_path
from .product import product_path
from .relationship_parser import validate_relationship_record, verify_relationship_artifact
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_BUILDER_VERSION = "0.1.0"
_EXPECTED_DATASET_ID = "ds_gleif_rr_level2_concat"


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def _verify_checksum(path: Path) -> str:
    sidecar = path.with_name(path.name + ".sha256")
    if not path.exists() or not sidecar.exists():
        raise ProductError(f"Missing relationship product file or checksum: {path}")
    expected = sidecar.read_text(encoding="ascii").split()[0]
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ProductError(f"Relationship product checksum mismatch: {path}")
    return actual


def _canonical(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def verify_relationship_product(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
    normalized_root: Path | None = None,
) -> dict[str, Any]:
    normalized = verify_relationship_artifact(data_root, snapshot_id, output_root=normalized_root)
    source_dataset_id = str(normalized["artifact"]["source_dataset_id"])
    if source_dataset_id != _EXPECTED_DATASET_ID:
        raise ProductError("Relationship product source dataset mismatch")
    final_dir = product_path(data_root, snapshot_id, source_dataset_id, output_root)
    manifest_path = final_dir / "product.json"
    database_path = final_dir / "relationships.sqlite"
    manifest_hash = _verify_checksum(manifest_path)
    database_hash = _verify_checksum(database_path)
    manifest = load_json(manifest_path)
    if manifest.get("source_snapshot_id") != snapshot_id or manifest.get("product_type") != "gleif_rr_level2_sqlite":
        raise ProductError("Relationship product identity mismatch")
    if manifest.get("normalized_artifact_sha256") != normalized["artifact"]["artifact_sha256"]:
        raise ProductError("Relationship product does not match normalized RR artifact")
    actual_hash, actual_size = sha256_file(database_path)
    if actual_hash != manifest.get("product_sha256") or actual_size != manifest.get("database_size_bytes"):
        raise ProductError("Relationship SQLite bytes do not match product manifest")

    uri = database_path.resolve().as_uri() + "?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    try:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()
        if not integrity or integrity[0] != "ok":
            raise ProductError(f"Relationship SQLite integrity_check failed: {integrity}")
        record_count = int(connection.execute("SELECT COUNT(*) FROM relationship").fetchone()[0])
        metadata = dict(connection.execute("SELECT key, value FROM metadata"))
    finally:
        connection.close()
    if record_count != manifest.get("record_count"):
        raise ProductError("Relationship SQLite record count mismatch")
    if metadata.get("source_snapshot_id") != snapshot_id:
        raise ProductError("Relationship SQLite metadata snapshot mismatch")

    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "product_dir": str(final_dir),
        "database_path": str(database_path),
        "record_count": record_count,
        "product": {**manifest, "manifest_sha256": manifest_hash, "verified_database_sha256": database_hash},
    }


def build_relationship_product(
    data_root: Path,
    snapshot_id: str,
    *,
    normalized_root: Path | None = None,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    normalized = verify_relationship_artifact(data_root, snapshot_id, output_root=normalized_root)
    artifact = normalized["artifact"]
    source_dataset_id = str(artifact["source_dataset_id"])
    final_dir = product_path(data_root, snapshot_id, source_dataset_id, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log("RELATIONSHIP_PRODUCT_BUILD_STARTED", {"snapshot_id": snapshot_id})
    if final_dir.exists():
        verified = verify_relationship_product(
            data_root,
            snapshot_id,
            output_root=output_root,
            normalized_root=normalized_root,
        )
        verified["status"] = "NO_CHANGE"
        log("RELATIONSHIP_PRODUCT_BUILD_NO_CHANGE", {"snapshot_id": snapshot_id})
        return verified

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    try:
        database_path = temporary_dir / "relationships.sqlite"
        connection = sqlite3.connect(database_path)
        try:
            connection.executescript(
                """
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=FULL;
                CREATE TABLE relationship (
                    row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    start_node_id TEXT NOT NULL,
                    start_node_type TEXT NOT NULL,
                    end_node_id TEXT NOT NULL,
                    end_node_type TEXT NOT NULL,
                    relationship_type TEXT NOT NULL,
                    relationship_status TEXT NOT NULL,
                    relationship_periods_json TEXT NOT NULL,
                    relationship_qualifiers_json TEXT NOT NULL,
                    relationship_quantifiers_json TEXT NOT NULL,
                    registration_json TEXT NOT NULL
                );
                CREATE INDEX idx_relationship_start ON relationship(start_node_id, relationship_type);
                CREATE INDEX idx_relationship_end ON relationship(end_node_id, relationship_type);
                CREATE INDEX idx_relationship_type ON relationship(relationship_type);
                CREATE INDEX idx_relationship_status ON relationship(relationship_status);
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """
            )
            records_path = Path(normalized["records_path"])
            rows = []
            count = 0
            with records_path.open("r", encoding="utf-8") as source:
                for ordinal, line in enumerate(source, start=1):
                    if not line.strip():
                        raise ProductError(f"Normalized RR records contain empty line #{ordinal}")
                    try:
                        record = json.loads(line)
                    except json.JSONDecodeError as exc:
                        raise ProductError(f"Normalized RR record #{ordinal} is invalid JSON") from exc
                    validate_relationship_record(record, ordinal)
                    rows.append(
                        (
                            record["start_node"]["id"],
                            record["start_node"]["type"],
                            record["end_node"]["id"],
                            record["end_node"]["type"],
                            record["relationship_type"],
                            record["relationship_status"],
                            _canonical(record["relationship_periods"]),
                            _canonical(record["relationship_qualifiers"]),
                            _canonical(record["relationship_quantifiers"]),
                            _canonical(record["registration"]),
                        )
                    )
                    count += 1
                    if len(rows) >= 5000:
                        connection.executemany(
                            """INSERT INTO relationship(
                                start_node_id,start_node_type,end_node_id,end_node_type,
                                relationship_type,relationship_status,relationship_periods_json,
                                relationship_qualifiers_json,relationship_quantifiers_json,registration_json
                            ) VALUES (?,?,?,?,?,?,?,?,?,?)""",
                            rows,
                        )
                        rows.clear()
                if rows:
                    connection.executemany(
                        """INSERT INTO relationship(
                            start_node_id,start_node_type,end_node_id,end_node_type,
                            relationship_type,relationship_status,relationship_periods_json,
                            relationship_qualifiers_json,relationship_quantifiers_json,registration_json
                        ) VALUES (?,?,?,?,?,?,?,?,?,?)""",
                        rows,
                    )
            metadata = {
                "source_snapshot_id": snapshot_id,
                "source_dataset_id": source_dataset_id,
                "source_version": str(artifact["source_version"]),
                "normalized_artifact_sha256": str(artifact["artifact_sha256"]),
                "builder_version": _BUILDER_VERSION,
            }
            connection.executemany("INSERT INTO metadata(key,value) VALUES (?,?)", sorted(metadata.items()))
            connection.commit()
            integrity = connection.execute("PRAGMA integrity_check").fetchone()
            if not integrity or integrity[0] != "ok":
                raise ProductError(f"Relationship SQLite integrity_check failed: {integrity}")
        finally:
            connection.close()

        if count != normalized["record_count"]:
            raise ProductError(
                f"Relationship product row count mismatch: normalized {normalized['record_count']}, built {count}"
            )
        product_hash, product_size = sha256_file(database_path)
        make_read_only(database_path)
        _write_checksum(database_path)
        manifest = {
            "product_version": 1,
            "product_type": "gleif_rr_level2_sqlite",
            "builder_version": _BUILDER_VERSION,
            "source_snapshot_id": snapshot_id,
            "source_dataset_id": source_dataset_id,
            "source_version": artifact["source_version"],
            "normalized_artifact_sha256": artifact["artifact_sha256"],
            "database_file": "relationships.sqlite",
            "product_sha256": product_hash,
            "database_size_bytes": product_size,
            "record_count": count,
            "created_at": utc_now_iso(),
        }
        manifest_path = temporary_dir / "product.json"
        atomic_write_json(manifest_path, manifest)
        make_read_only(manifest_path)
        manifest_hash = _write_checksum(manifest_path)
        temporary_dir.rename(final_dir)
    except Exception as exc:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        log("RELATIONSHIP_PRODUCT_BUILD_FAILED", {"snapshot_id": snapshot_id, "error": str(exc)})
        raise

    log("RELATIONSHIP_PRODUCT_BUILD_COMPLETED", {"snapshot_id": snapshot_id, "record_count": count})
    return {
        "status": "BUILT",
        "snapshot_id": snapshot_id,
        "product_dir": str(final_dir),
        "database_path": str(final_dir / "relationships.sqlite"),
        "record_count": count,
        "product": {**manifest, "manifest_sha256": manifest_hash},
    }
