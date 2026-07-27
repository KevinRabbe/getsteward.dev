from __future__ import annotations

import json
import shutil
import sqlite3
import uuid
from pathlib import Path
from typing import Any

from .errors import ProductError
from .events import append_event
from .product import product_path
from .ror_parser import validate_normalized_ror_record, verify_ror_artifact
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_BUILDER_VERSION = "0.1.0"
_EXPECTED_DATASET_ID = "ds_ror_organizations"
_EXCLUDED_SOURCE_FIELDS = ["locations"]


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def _verify_checksum(path: Path) -> str:
    sidecar = path.with_name(path.name + ".sha256")
    if not path.exists() or not sidecar.exists():
        raise ProductError(f"Missing ROR product file or checksum: {path}")
    try:
        expected = sidecar.read_text(encoding="ascii").split()[0]
    except (OSError, IndexError) as exc:
        raise ProductError(f"Invalid ROR product checksum sidecar: {sidecar}") from exc
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ProductError(f"ROR product checksum mismatch: {path}")
    return actual


def _canonical(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def verify_ror_product(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
    normalized_root: Path | None = None,
) -> dict[str, Any]:
    normalized = verify_ror_artifact(data_root, snapshot_id, output_root=normalized_root)
    artifact = normalized["artifact"]
    if artifact.get("source_dataset_id") != _EXPECTED_DATASET_ID:
        raise ProductError("ROR product source dataset mismatch")
    final_dir = product_path(data_root, snapshot_id, _EXPECTED_DATASET_ID, output_root)
    manifest_path = final_dir / "product.json"
    database_path = final_dir / "organizations.sqlite"
    manifest_hash = _verify_checksum(manifest_path)
    database_hash = _verify_checksum(database_path)
    manifest = load_json(manifest_path)
    if manifest.get("source_snapshot_id") != snapshot_id or manifest.get("product_type") != "ror_organizations_sqlite":
        raise ProductError("ROR product identity mismatch")
    if manifest.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ProductError("ROR product lost its excluded-field boundary")
    if manifest.get("normalized_artifact_sha256") != artifact.get("artifact_sha256"):
        raise ProductError("ROR product does not match normalized artifact")
    actual_hash, actual_size = sha256_file(database_path)
    if actual_hash != manifest.get("product_sha256") or actual_size != manifest.get("database_size_bytes"):
        raise ProductError("ROR SQLite bytes do not match product manifest")

    uri = database_path.resolve().as_uri() + "?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    try:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()
        if not integrity or integrity[0] != "ok":
            raise ProductError(f"ROR SQLite integrity_check failed: {integrity}")
        record_count = int(connection.execute("SELECT COUNT(*) FROM organization").fetchone()[0])
        distinct_ids = int(connection.execute("SELECT COUNT(DISTINCT ror_id) FROM organization").fetchone()[0])
        metadata = dict(connection.execute("SELECT key, value FROM metadata"))
    finally:
        connection.close()
    if record_count != manifest.get("record_count") or distinct_ids != record_count:
        raise ProductError("ROR SQLite record/identifier count mismatch")
    if metadata.get("source_snapshot_id") != snapshot_id:
        raise ProductError("ROR SQLite metadata snapshot mismatch")
    if metadata.get("excluded_source_fields") != "locations":
        raise ProductError("ROR SQLite metadata lost excluded locations boundary")

    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "product_dir": str(final_dir),
        "database_path": str(database_path),
        "record_count": record_count,
        "product": {
            **manifest,
            "manifest_sha256": manifest_hash,
            "verified_database_sha256": database_hash,
        },
    }


def build_ror_product(
    data_root: Path,
    snapshot_id: str,
    *,
    normalized_root: Path | None = None,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    normalized = verify_ror_artifact(data_root, snapshot_id, output_root=normalized_root)
    artifact = normalized["artifact"]
    final_dir = product_path(data_root, snapshot_id, _EXPECTED_DATASET_ID, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log("ROR_PRODUCT_BUILD_STARTED", {"snapshot_id": snapshot_id})
    if final_dir.exists():
        verified = verify_ror_product(
            data_root,
            snapshot_id,
            output_root=output_root,
            normalized_root=normalized_root,
        )
        verified["status"] = "NO_CHANGE"
        log("ROR_PRODUCT_BUILD_NO_CHANGE", {"snapshot_id": snapshot_id})
        return verified

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    try:
        database_path = temporary_dir / "organizations.sqlite"
        connection = sqlite3.connect(database_path)
        try:
            connection.executescript(
                """
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=FULL;
                CREATE TABLE organization (
                    row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ror_id TEXT NOT NULL UNIQUE,
                    display_name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    established INTEGER,
                    types_json TEXT NOT NULL,
                    domains_json TEXT NOT NULL,
                    names_json TEXT NOT NULL,
                    external_ids_json TEXT NOT NULL,
                    links_json TEXT NOT NULL,
                    relationships_json TEXT NOT NULL,
                    admin_json TEXT NOT NULL
                );
                CREATE INDEX idx_organization_display_name ON organization(display_name);
                CREATE INDEX idx_organization_status ON organization(status);
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """
            )
            rows: list[tuple[Any, ...]] = []
            count = 0
            records_path = Path(normalized["records_path"])
            with records_path.open("r", encoding="utf-8") as source:
                for ordinal, line in enumerate(source, start=1):
                    if not line.strip():
                        raise ProductError(f"Normalized ROR records contain empty line #{ordinal}")
                    try:
                        record = json.loads(line)
                    except json.JSONDecodeError as exc:
                        raise ProductError(f"Normalized ROR record #{ordinal} is invalid JSON") from exc
                    validate_normalized_ror_record(record, ordinal)
                    rows.append(
                        (
                            record["ror_id"],
                            record["display_name"],
                            record["status"],
                            record["established"],
                            _canonical(record["types"]),
                            _canonical(record["domains"]),
                            _canonical(record["names"]),
                            _canonical(record["external_ids"]),
                            _canonical(record["links"]),
                            _canonical(record["relationships"]),
                            _canonical(record["admin"]),
                        )
                    )
                    count += 1
                    if len(rows) >= 5000:
                        try:
                            connection.executemany(
                                """INSERT INTO organization(
                                    ror_id,display_name,status,established,types_json,domains_json,
                                    names_json,external_ids_json,links_json,relationships_json,admin_json
                                ) VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
                                rows,
                            )
                        except sqlite3.IntegrityError as exc:
                            raise ProductError("ROR source contains duplicate organization identifiers") from exc
                        rows.clear()
                if rows:
                    try:
                        connection.executemany(
                            """INSERT INTO organization(
                                ror_id,display_name,status,established,types_json,domains_json,
                                names_json,external_ids_json,links_json,relationships_json,admin_json
                            ) VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
                            rows,
                        )
                    except sqlite3.IntegrityError as exc:
                        raise ProductError("ROR source contains duplicate organization identifiers") from exc

            metadata = {
                "source_snapshot_id": snapshot_id,
                "source_dataset_id": _EXPECTED_DATASET_ID,
                "source_version": str(artifact["source_version"]),
                "normalized_artifact_sha256": str(artifact["artifact_sha256"]),
                "builder_version": _BUILDER_VERSION,
                "excluded_source_fields": "locations",
            }
            connection.executemany("INSERT INTO metadata(key,value) VALUES (?,?)", sorted(metadata.items()))
            connection.commit()
            integrity = connection.execute("PRAGMA integrity_check").fetchone()
            if not integrity or integrity[0] != "ok":
                raise ProductError(f"ROR SQLite integrity_check failed: {integrity}")
        finally:
            connection.close()

        if count != normalized["record_count"]:
            raise ProductError(
                f"ROR product row count mismatch: normalized {normalized['record_count']}, built {count}"
            )
        product_hash, product_size = sha256_file(database_path)
        make_read_only(database_path)
        _write_checksum(database_path)
        manifest = {
            "product_version": 1,
            "product_type": "ror_organizations_sqlite",
            "builder_version": _BUILDER_VERSION,
            "source_snapshot_id": snapshot_id,
            "source_dataset_id": _EXPECTED_DATASET_ID,
            "source_version": artifact["source_version"],
            "normalized_artifact_sha256": artifact["artifact_sha256"],
            "database_file": "organizations.sqlite",
            "product_sha256": product_hash,
            "database_size_bytes": product_size,
            "record_count": count,
            "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
            "created_at": utc_now_iso(),
        }
        manifest_path = temporary_dir / "product.json"
        atomic_write_json(manifest_path, manifest)
        make_read_only(manifest_path)
        manifest_hash = _write_checksum(manifest_path)
        temporary_dir.rename(final_dir)
    except Exception as exc:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        log("ROR_PRODUCT_BUILD_FAILED", {"snapshot_id": snapshot_id, "error": str(exc)})
        raise

    log("ROR_PRODUCT_BUILD_COMPLETED", {"snapshot_id": snapshot_id, "record_count": count})
    return {
        "status": "BUILT",
        "snapshot_id": snapshot_id,
        "product_dir": str(final_dir),
        "database_path": str(final_dir / "organizations.sqlite"),
        "record_count": count,
        "product": {**manifest, "manifest_sha256": manifest_hash},
    }
