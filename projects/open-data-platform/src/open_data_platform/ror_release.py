from __future__ import annotations

import shutil
import uuid
from pathlib import Path
from typing import Any

from .errors import ReleaseError
from .events import append_event
from .release import _copy_with_checksum, _deterministic_zip, _write_checksum, release_path, verify_release
from .ror_parser import verify_ror_artifact
from .ror_product import verify_ror_product
from .util import atomic_write_json, load_json, make_read_only, sha256_file
from .verify import verify_snapshot


_RELEASE_VERSION = "0.1.0"
_EXPECTED_DATASET_ID = "ds_ror_organizations"


def build_ror_release(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None = None,
    normalized_root: Path | None = None,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    snapshot_manifest = load_json(snapshot_dir / "manifest.json")
    if snapshot_manifest.get("source_dataset_id") != _EXPECTED_DATASET_ID:
        raise ReleaseError("ROR release source dataset mismatch")
    final_dir = release_path(data_root, snapshot_id, _EXPECTED_DATASET_ID, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log("ROR_RELEASE_BUILD_STARTED", {"snapshot_id": snapshot_id})
    try:
        verify_snapshot(data_root, snapshot_id)
        normalized = verify_ror_artifact(data_root, snapshot_id, output_root=normalized_root)
        product = verify_ror_product(
            data_root,
            snapshot_id,
            output_root=product_root,
            normalized_root=normalized_root,
        )
        if product["product"]["normalized_artifact_sha256"] != normalized["artifact"]["artifact_sha256"]:
            raise ReleaseError("ROR product does not match normalized artifact")
        if product["product"].get("excluded_source_fields") != ["locations"]:
            raise ReleaseError("ROR product lost excluded locations boundary")

        if final_dir.exists():
            verified = verify_release(data_root, snapshot_id, output_root=output_root)
            if verified["release"].get("release_type") != "ror_organizations_sqlite_bundle":
                raise ReleaseError("Existing ROR release has the wrong type")
            if verified["release"].get("excluded_source_fields") != ["locations"]:
                raise ReleaseError("Existing ROR release lost field-exclusion boundary")
            verified["status"] = "NO_CHANGE"
            log("ROR_RELEASE_BUILD_NO_CHANGE", {"snapshot_id": snapshot_id})
            return verified

        final_dir.parent.mkdir(parents=True, exist_ok=True)
        temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
        temporary_dir.mkdir(parents=False, exist_ok=False)
        try:
            payload_dir = temporary_dir / "payload"
            product_dir = Path(product["product_dir"])
            normalized_dir = Path(normalized["artifact_dir"])
            copied = [
                _copy_with_checksum(
                    product_dir / "organizations.sqlite",
                    payload_dir / "product" / "organizations.sqlite",
                    relative_to=payload_dir,
                ),
                _copy_with_checksum(
                    product_dir / "product.json",
                    payload_dir / "product" / "product.json",
                    relative_to=payload_dir,
                ),
                _copy_with_checksum(
                    snapshot_dir / "manifest.json",
                    payload_dir / "source" / "manifest.json",
                    relative_to=payload_dir,
                ),
                _copy_with_checksum(
                    normalized_dir / "quality.json",
                    payload_dir / "normalized" / "quality.json",
                    relative_to=payload_dir,
                ),
            ]
            release_manifest = {
                "release_version": 1,
                "release_type": "ror_organizations_sqlite_bundle",
                "builder_version": _RELEASE_VERSION,
                "source_snapshot_id": snapshot_id,
                "source_dataset_id": _EXPECTED_DATASET_ID,
                "source_version": snapshot_manifest["source_version"],
                "source_content_id": snapshot_manifest["content"]["content_id"],
                "record_count": product["record_count"],
                "product_sha256": product["product"]["product_sha256"],
                "normalized_artifact_sha256": normalized["artifact"]["artifact_sha256"],
                "excluded_source_fields": ["locations"],
                "files": copied,
                "bundle_file": "release.zip",
            }
            payload_manifest = payload_dir / "release.json"
            atomic_write_json(payload_manifest, release_manifest)
            _write_checksum(payload_manifest)

            bundle_path = temporary_dir / "release.zip"
            _deterministic_zip(payload_dir, bundle_path)
            bundle_hash, bundle_size = sha256_file(bundle_path)
            _write_checksum(bundle_path)
            outer_manifest = temporary_dir / "release.json"
            shutil.copy2(payload_manifest, outer_manifest)
            _write_checksum(outer_manifest)
            shutil.rmtree(payload_dir)
            temporary_dir.rename(final_dir)
            for path in final_dir.rglob("*"):
                if path.is_file():
                    make_read_only(path)
        except Exception:
            shutil.rmtree(temporary_dir, ignore_errors=True)
            raise

        result = {
            "status": "BUILT",
            "snapshot_id": snapshot_id,
            "release_dir": str(final_dir),
            "bundle_path": str(final_dir / "release.zip"),
            "bundle_size_bytes": bundle_size,
            "release": {**release_manifest, "bundle_sha256": bundle_hash},
        }
        log("ROR_RELEASE_BUILD_COMPLETED", {"snapshot_id": snapshot_id, "bundle_sha256": bundle_hash})
        return result
    except Exception as exc:
        log("ROR_RELEASE_BUILD_FAILED", {"snapshot_id": snapshot_id, "error": str(exc)})
        raise
