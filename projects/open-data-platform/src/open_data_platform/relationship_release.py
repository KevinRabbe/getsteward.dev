from __future__ import annotations

import shutil
import uuid
from pathlib import Path
from typing import Any

from .errors import ReleaseError
from .events import append_event
from .relationship_parser import verify_relationship_artifact
from .relationship_product import verify_relationship_product
from .release import _copy_with_checksum, _deterministic_zip, _write_checksum, release_path, verify_release
from .util import atomic_write_json, load_json, make_read_only, sha256_file
from .verify import verify_snapshot


_RELEASE_VERSION = "0.1.0"


def build_relationship_release(
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
    source_dataset_id = str(snapshot_manifest["source_dataset_id"])
    final_dir = release_path(data_root, snapshot_id, source_dataset_id, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log("RELATIONSHIP_RELEASE_BUILD_STARTED", {"snapshot_id": snapshot_id})
    try:
        verify_snapshot(data_root, snapshot_id)
        normalized = verify_relationship_artifact(data_root, snapshot_id, output_root=normalized_root)
        product = verify_relationship_product(
            data_root,
            snapshot_id,
            output_root=product_root,
            normalized_root=normalized_root,
        )
        if product["product"]["normalized_artifact_sha256"] != normalized["artifact"]["artifact_sha256"]:
            raise ReleaseError("Relationship product does not match normalized RR artifact")

        if final_dir.exists():
            verified = verify_release(data_root, snapshot_id, output_root=output_root)
            if verified["release"].get("release_type") != "gleif_rr_level2_sqlite_bundle":
                raise ReleaseError("Existing release has the wrong product type")
            verified["status"] = "NO_CHANGE"
            log("RELATIONSHIP_RELEASE_BUILD_NO_CHANGE", {"snapshot_id": snapshot_id})
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
                    product_dir / "relationships.sqlite",
                    payload_dir / "product" / "relationships.sqlite",
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
                "release_type": "gleif_rr_level2_sqlite_bundle",
                "builder_version": _RELEASE_VERSION,
                "source_snapshot_id": snapshot_id,
                "source_dataset_id": source_dataset_id,
                "source_version": snapshot_manifest["source_version"],
                "source_content_id": snapshot_manifest["content"]["content_id"],
                "record_count": product["record_count"],
                "product_sha256": product["product"]["product_sha256"],
                "normalized_artifact_sha256": normalized["artifact"]["artifact_sha256"],
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
            final_manifest = temporary_dir / "release.json"
            shutil.copy2(payload_manifest, final_manifest)
            _write_checksum(final_manifest)
            shutil.rmtree(payload_dir)
            temporary_dir.rename(final_dir)
            for published in final_dir.rglob("*"):
                if published.is_file():
                    make_read_only(published)
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
        log("RELATIONSHIP_RELEASE_BUILD_COMPLETED", {"snapshot_id": snapshot_id, "bundle_sha256": bundle_hash})
        return result
    except Exception as exc:
        log("RELATIONSHIP_RELEASE_BUILD_FAILED", {"snapshot_id": snapshot_id, "error": str(exc)})
        raise
