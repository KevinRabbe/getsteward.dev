from __future__ import annotations

import os
import shutil
import uuid
from pathlib import Path
from typing import Any

from .errors import IntegrityError
from .util import atomic_write_json, make_read_only, sha256_file, utc_now_iso


def content_path(root: Path, sha256: str, suffix: str = ".zip") -> Path:
    return root / "archive" / "content" / "sha256" / sha256[:2] / f"{sha256}{suffix}"


def _relative(path: Path, root: Path) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


def archive_staged_file(
    *,
    staged_path: Path,
    data_root: Path,
    source: dict[str, Any],
    admission: dict[str, Any],
    remote: Any,
    acquisition_metadata: dict[str, Any],
) -> dict[str, Any]:
    original_hash, original_size = sha256_file(staged_path)
    target = content_path(data_root, original_hash, staged_path.suffix or ".bin")
    target.parent.mkdir(parents=True, exist_ok=True)

    if target.exists():
        existing_hash, existing_size = sha256_file(target)
        if existing_hash != original_hash or existing_size != original_size:
            raise IntegrityError(f"Existing content object failed integrity check: {target}")
        staged_path.unlink(missing_ok=True)
        deduplicated = True
    else:
        os.replace(staged_path, target)
        deduplicated = False
        make_read_only(target)

    verify_hash, verify_size = sha256_file(target)
    if verify_hash != original_hash or verify_size != original_size:
        raise IntegrityError("Read-back verification failed after archival")

    snapshot_id = f"snp_{uuid.uuid4()}"
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    snapshot_dir.mkdir(parents=True, exist_ok=False)

    manifest = {
        "manifest_version": 1,
        "snapshot_id": snapshot_id,
        "source_id": source["source_id"],
        "source_dataset_id": source["source_dataset_id"],
        "publisher": source["publisher"],
        "dataset_name": source["dataset_name"],
        "source_version": remote.publication_date,
        "retrieved_at": utc_now_iso(),
        "acquisition_method": source["acquisition_method"],
        "download_url": remote.download_url,
        "cdf_version": remote.cdf_version,
        "declared_record_count": remote.record_count,
        "admission_decision_id": admission["admission_decision_id"],
        "license_id": admission["license_id"],
        "terms_url": admission["terms_url"],
        "content": {
            "content_id": f"sha256:{original_hash}",
            "hash_algorithm": "SHA-256",
            "original_content_hash": original_hash,
            "original_size_bytes": original_size,
            "archive_path": _relative(target, data_root),
            "deduplicated_existing_content": deduplicated,
        },
        "acquisition_response": acquisition_metadata,
    }

    manifest_path = snapshot_dir / "manifest.json"
    # The manifest cannot contain its own final hash without recursion.
    # Its byte identity is fixed by the adjacent manifest.json.sha256 file.
    atomic_write_json(manifest_path, manifest)
    final_manifest_hash, _ = sha256_file(manifest_path)
    (snapshot_dir / "manifest.json.sha256").write_text(final_manifest_hash + "  manifest.json\n", encoding="ascii")
    make_read_only(manifest_path)
    make_read_only(snapshot_dir / "manifest.json.sha256")

    return manifest


def replicate_content(*, data_root: Path, manifest: dict[str, Any], replica_root: Path) -> Path:
    source_path = data_root / manifest["content"]["archive_path"]
    digest = manifest["content"]["original_content_hash"]
    destination = replica_root / "content" / "sha256" / digest[:2] / source_path.name
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists():
        shutil.copy2(source_path, destination)
    check_hash, check_size = sha256_file(destination)
    if check_hash != digest or check_size != manifest["content"]["original_size_bytes"]:
        raise IntegrityError("Replica verification failed")
    make_read_only(destination)
    return destination
