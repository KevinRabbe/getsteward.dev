from __future__ import annotations

import json
from pathlib import Path

from .errors import IntegrityError
from .util import sha256_file


def verify_snapshot(data_root: Path, snapshot_id: str) -> dict:
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    manifest_path = snapshot_dir / "manifest.json"
    checksum_path = snapshot_dir / "manifest.json.sha256"
    if not manifest_path.exists() or not checksum_path.exists():
        raise IntegrityError(f"Snapshot manifest not found: {snapshot_id}")

    expected_manifest_hash = checksum_path.read_text(encoding="ascii").split()[0]
    actual_manifest_hash, _ = sha256_file(manifest_path)
    if expected_manifest_hash != actual_manifest_hash:
        raise IntegrityError("Snapshot manifest checksum mismatch")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    content_path = data_root / manifest["content"]["archive_path"]
    actual_hash, actual_size = sha256_file(content_path)
    if actual_hash != manifest["content"]["original_content_hash"]:
        raise IntegrityError("Archived source content checksum mismatch")
    if actual_size != manifest["content"]["original_size_bytes"]:
        raise IntegrityError("Archived source content size mismatch")

    return {
        "snapshot_id": snapshot_id,
        "manifest_ok": True,
        "content_ok": True,
        "content_hash": actual_hash,
        "size_bytes": actual_size,
    }
