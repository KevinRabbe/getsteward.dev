from __future__ import annotations

import json
import shutil
import sqlite3
import uuid
from pathlib import Path
from typing import Any

from .errors import ProductError
from .parser import validate_normalized_record, verify_normalized_artifact
from .events import append_event
from .util import atomic_write_json, load_json, make_read_only, sha256_file

_BUILDER_VERSION = "0.3.0"

_SCHEMA = """
CREATE TABLE metadata (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
) WITHOUT ROWID;

CREATE TABLE lei (
    lei TEXT PRIMARY KEY NOT NULL,
    legal_name TEXT NOT NULL,
    legal_name_language TEXT,
    entity_status TEXT NOT NULL,
    legal_jurisdiction TEXT,
    entity_category TEXT,
    entity_subcategory TEXT,
    entity_creation_date TEXT,
    legal_form_json TEXT,
    registration_authority_json TEXT,
    legal_address_json TEXT NOT NULL,
    headquarters_address_json TEXT NOT NULL,
    registration_json TEXT NOT NULL
) WITHOUT ROWID;

CREATE INDEX idx_lei_entity_status ON lei(entity_status, lei);
CREATE INDEX idx_lei_legal_jurisdiction ON lei(legal_jurisdiction, lei);
"""


def product_path(
    data_root: Path,
    snapshot_id: str,
    source_dataset_id: str,
    output_root: Path | None = None,
) -> Path:
    root = output_root if output_root is not None else data_root / "products"
    return root / source_dataset_id / snapshot_id


def _canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _optional_string(record: dict[str, Any], key: str) -> str | None:
    value = record.get(key)
    if value is None:
        return None
    if not isinstance(value, str):
        raise ProductError(f"Normalized field {key} must be a string or null")
    return value


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    checksum_path = path.with_name(path.name + ".sha256")
    checksum_path.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(checksum_path)
    return digest


def _verify_checksum(path: Path) -> str:
    checksum_path = path.with_name(path.name + ".sha256")
    if not checksum_path.exists():
        raise ProductError(f"Missing product checksum sidecar: {checksum_path}")
    expected = checksum_path.read_text(encoding="ascii").split()[0]
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ProductError(f"Product checksum mismatch: {path}")
    return actual


def _metadata_rows(metadata: dict[str, Any]) -> list[tuple[str, str]]:
    return [(key, _canonical_json(metadata[key])) for key in sorted(metadata)]


def _build_database(
    db_path: Path,
    records_path: Path,
    *,
    metadata: dict[str, Any],
    expected_record_count: int,
    expected_records_hash: str,
) -> int:
    connection = sqlite3.connect(str(db_path))
    records_written = 0
    try:
        connection.execute("PRAGMA auto_vacuum = NONE")
        connection.execute("PRAGMA page_size = 4096")
        connection.execute("PRAGMA journal_mode = DELETE")
        connection.execute("PRAGMA synchronous = FULL")
        connection.executescript(_SCHEMA)
        connection.executemany(
            "INSERT INTO metadata(key, value) VALUES (?, ?)",
            _metadata_rows(metadata),
        )

        insert_sql = """
            INSERT INTO lei(
                lei,
                legal_name,
                legal_name_language,
                entity_status,
                legal_jurisdiction,
                entity_category,
                entity_subcategory,
                entity_creation_date,
                legal_form_json,
                registration_authority_json,
                legal_address_json,
                headquarters_address_json,
                registration_json
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """
        with records_path.open("r", encoding="utf-8") as records_file:
            for ordinal, line in enumerate(records_file, start=1):
                if not line.strip():
                    raise ProductError(f"Normalized records contain an empty line at #{ordinal}")
                try:
                    record = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise ProductError(f"Normalized record #{ordinal} is not valid JSON") from exc
                validate_normalized_record(record, ordinal)

                try:
                    connection.execute(
                        insert_sql,
                        (
                            record["lei"],
                            record["legal_name"],
                            _optional_string(record, "legal_name_language"),
                            record["entity_status"],
                            _optional_string(record, "legal_jurisdiction"),
                            _optional_string(record, "entity_category"),
                            _optional_string(record, "entity_subcategory"),
                            _optional_string(record, "entity_creation_date"),
                            _canonical_json(record.get("legal_form")) if record.get("legal_form") is not None else None,
                            _canonical_json(record.get("registration_authority"))
                            if record.get("registration_authority") is not None
                            else None,
                            _canonical_json(record["legal_address"]),
                            _canonical_json(record["headquarters_address"]),
                            _canonical_json(record["registration"]),
                        ),
                    )
                except sqlite3.IntegrityError as exc:
                    raise ProductError(f"Duplicate LEI in normalized records: {record['lei']}") from exc
                records_written += 1

        if records_written != expected_record_count:
            raise ProductError(
                "Product record count mismatch: "
                f"artifact declares {expected_record_count}, inserted {records_written}"
            )
        actual_records_hash, _ = sha256_file(records_path)
        if actual_records_hash != expected_records_hash:
            raise ProductError("Normalized records changed while product was being built")

        connection.commit()
        connection.execute("ANALYZE")
        connection.commit()
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise ProductError(f"SQLite integrity check failed: {integrity}")
        connection.execute("VACUUM")
    finally:
        connection.close()
    return records_written


def verify_product(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    snapshot_manifest = load_json(snapshot_dir / "manifest.json")
    product_dir = product_path(
        data_root,
        snapshot_id,
        str(snapshot_manifest["source_dataset_id"]),
        output_root,
    )
    product_path_value = product_dir / "product.json"
    database_path = product_dir / "lei.sqlite"
    if not product_path_value.exists() or not database_path.exists():
        raise ProductError(f"Product not found for snapshot {snapshot_id}: {product_dir}")

    product_hash = _verify_checksum(product_path_value)
    database_hash, database_size = sha256_file(database_path)
    _verify_checksum(database_path)
    product_manifest = load_json(product_path_value)
    if product_manifest.get("database_sha256") != database_hash:
        raise ProductError("Product manifest database checksum mismatch")
    if product_manifest.get("database_size_bytes") != database_size:
        raise ProductError("Product manifest database size mismatch")
    if product_manifest.get("source_snapshot_id") != snapshot_id:
        raise ProductError("Product belongs to a different snapshot")

    connection = sqlite3.connect(database_path.resolve().as_uri() + "?mode=ro", uri=True)
    try:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise ProductError(f"SQLite integrity check failed: {integrity}")
        records_in_db = connection.execute("SELECT COUNT(*) FROM lei").fetchone()[0]
        metadata = dict(connection.execute("SELECT key, value FROM metadata"))
    finally:
        connection.close()

    if records_in_db != product_manifest.get("record_count"):
        raise ProductError(
            "Product record count mismatch: "
            f"manifest declares {product_manifest.get('record_count')}, database contains {records_in_db}"
        )
    if json.loads(metadata.get("source_snapshot_id", "null")) != snapshot_id:
        raise ProductError("Product metadata snapshot mismatch")

    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "product_dir": str(product_dir),
        "database_path": str(database_path),
        "record_count": records_in_db,
        "product": {**product_manifest, "product_sha256": product_hash},
    }


def build_product(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
    normalized_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    snapshot_manifest = load_json(snapshot_dir / "manifest.json")
    source_dataset_id = str(snapshot_manifest["source_dataset_id"])
    final_dir = product_path(data_root, snapshot_id, source_dataset_id, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log("PRODUCT_BUILD_STARTED", {"snapshot_id": snapshot_id})
    try:
        normalized = verify_normalized_artifact(
            data_root,
            snapshot_id,
            output_root=normalized_root,
        )
        if final_dir.exists():
            verified = verify_product(data_root, snapshot_id, output_root=output_root)
            verified["status"] = "NO_CHANGE"
            log("PRODUCT_BUILD_NO_CHANGE", {"snapshot_id": snapshot_id})
            return verified

        final_dir.parent.mkdir(parents=True, exist_ok=True)
        temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
        temporary_dir.mkdir(parents=False, exist_ok=False)
        try:
            database_path = temporary_dir / "lei.sqlite"
            metadata = {
                "artifact_type": "gleif_level1_sqlite",
                "builder_version": _BUILDER_VERSION,
                "source_snapshot_id": snapshot_id,
                "source_dataset_id": source_dataset_id,
                "source_version": snapshot_manifest["source_version"],
                "source_content_id": snapshot_manifest["content"]["content_id"],
                "normalized_artifact_sha256": normalized["artifact"]["artifact_sha256"],
                "normalized_records_sha256": normalized["artifact"]["records_sha256"],
            }
            record_count = _build_database(
                database_path,
                Path(normalized["records_path"]),
                metadata=metadata,
                expected_record_count=normalized["record_count"],
                expected_records_hash=normalized["artifact"]["records_sha256"],
            )
            database_hash, database_size = sha256_file(database_path)
            make_read_only(database_path)
            _write_checksum(database_path)

            product_manifest = {
                "product_version": 1,
                "product_type": "gleif_level1_sqlite",
                "builder_version": _BUILDER_VERSION,
                "source_snapshot_id": snapshot_id,
                "source_dataset_id": source_dataset_id,
                "source_version": snapshot_manifest["source_version"],
                "source_content_id": snapshot_manifest["content"]["content_id"],
                "normalized_artifact_sha256": normalized["artifact"]["artifact_sha256"],
                "normalized_records_sha256": normalized["artifact"]["records_sha256"],
                "database_file": "lei.sqlite",
                "database_sha256": database_hash,
                "database_size_bytes": database_size,
                "record_count": record_count,
                "schema": "gleif-level1-normalized-v1",
            }
            product_manifest_path = temporary_dir / "product.json"
            atomic_write_json(product_manifest_path, product_manifest)
            make_read_only(product_manifest_path)
            product_hash = _write_checksum(product_manifest_path)
            temporary_dir.rename(final_dir)
        except Exception:
            shutil.rmtree(temporary_dir, ignore_errors=True)
            raise

        result = {
            "status": "BUILT",
            "snapshot_id": snapshot_id,
            "product_dir": str(final_dir),
            "database_path": str(final_dir / "lei.sqlite"),
            "record_count": record_count,
            "product": {**product_manifest, "product_sha256": product_hash},
        }
        log(
            "PRODUCT_BUILD_COMPLETED",
            {"snapshot_id": snapshot_id, "record_count": record_count, "database_sha256": database_hash},
        )
        return result
    except Exception as exc:
        log("PRODUCT_BUILD_FAILED", {"snapshot_id": snapshot_id, "error": str(exc)})
        raise
