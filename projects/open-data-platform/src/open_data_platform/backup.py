from __future__ import annotations

import json
import shutil
import uuid
import zipfile
from pathlib import Path
from typing import Any

from .errors import PlatformError
from .events import append_event
from .product import verify_product
from .util import load_json, make_read_only, sha256_file

_RELEASE_FILES = ("release.json", "release.json.sha256", "release.zip", "release.zip.sha256")


def _find_replica_dir(replica_root: Path, snapshot_id: str) -> Path:
    candidates = list(replica_root.glob(f"*/{snapshot_id}"))
    if len(candidates) != 1:
        raise PlatformError(f"Expected exactly one replica for {snapshot_id} under {replica_root}")
    return candidates[0]


def _verify_bundle_directory(release_dir: Path, snapshot_id: str) -> dict[str, Any]:
    manifest_path = release_dir / "release.json"
    if not manifest_path.exists():
        raise PlatformError(f"Replica release manifest is missing: {manifest_path}")
    manifest = load_json(manifest_path)
    if manifest.get("source_snapshot_id") != snapshot_id:
        raise PlatformError("Replica release belongs to a different snapshot")

    hashes: dict[str, str] = {}
    for filename in _RELEASE_FILES:
        path = release_dir / filename
        if not path.exists():
            raise PlatformError(f"Replica release file is missing: {path}")
        if filename.endswith(".sha256"):
            continue
        sidecar = (release_dir / f"{filename}.sha256").read_text(encoding="ascii").split()[0]
        actual, _ = sha256_file(path)
        if sidecar != actual:
            raise PlatformError(f"Replica release checksum mismatch: {path}")
        hashes[filename] = actual

    bundle_path = release_dir / "release.zip"
    with zipfile.ZipFile(bundle_path, "r") as bundle:
        if bundle.testzip() is not None:
            raise PlatformError("Replica release ZIP CRC verification failed")
        expected_files = {
            entry["path"]: entry
            for entry in manifest.get("files", [])
            if isinstance(entry, dict) and isinstance(entry.get("path"), str)
        }
        for name, expected in expected_files.items():
            try:
                with bundle.open(name, "r") as stream:
                    digest, size = _hash_stream(stream)
            except KeyError as exc:
                raise PlatformError(f"Replica ZIP is missing payload: {name}") from exc
            if digest != expected["sha256"] or size != expected["size_bytes"]:
                raise PlatformError(f"Replica payload checksum mismatch: {name}")
    return {
        "release_dir": str(release_dir),
        "source_dataset_id": str(manifest["source_dataset_id"]),
        "snapshot_id": snapshot_id,
        "source_version": manifest.get("source_version"),
        "bundle_sha256": hashes["release.zip"],
        "manifest": manifest,
    }


def _hash_stream(stream: Any) -> tuple[str, int]:
    import hashlib

    digest = hashlib.sha256()
    size = 0
    while chunk := stream.read(1024 * 1024):
        digest.update(chunk)
        size += len(chunk)
    return digest.hexdigest(), size


def verify_replica_release(replica_root: Path, snapshot_id: str) -> dict[str, Any]:
    verified = _verify_bundle_directory(_find_replica_dir(replica_root, snapshot_id), snapshot_id)
    return {"status": "VERIFIED", **{key: value for key, value in verified.items() if key != "manifest"}}


def _copy_stream_from_zip(bundle: zipfile.ZipFile, name: str, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with bundle.open(name, "r") as source, destination.open("wb") as target:
        shutil.copyfileobj(source, target, length=1024 * 1024)
    make_read_only(destination)


def restore_release(replica_root: Path, snapshot_id: str, restore_root: Path) -> dict[str, Any]:
    """Restore a verified release into a new query-capable data root.

    The restored root contains the source manifest, product, and release bundle.
    It is sufficient for read-only failover queries; the raw and normalized
    payloads remain at the primary site and are not fabricated during restore.
    """

    verified = _verify_bundle_directory(_find_replica_dir(replica_root, snapshot_id), snapshot_id)
    dataset_id = verified["source_dataset_id"]
    release_dir = Path(verified["release_dir"])
    target_release = restore_root / "releases" / dataset_id / snapshot_id
    target_product = restore_root / "products" / dataset_id / snapshot_id
    target_snapshot = restore_root / "archive" / "snapshots" / snapshot_id

    expected_targets = (target_release, target_product, target_snapshot)
    if any(path.exists() for path in expected_targets):
        if all(path.exists() for path in expected_targets):
            verify_product(restore_root, snapshot_id)
            return {
                "status": "NO_CHANGE",
                "snapshot_id": snapshot_id,
                "restore_root": str(restore_root),
                "bundle_sha256": verified["bundle_sha256"],
            }
        raise PlatformError("Restore target contains a partial existing snapshot; refusing to overwrite")

    temporary_root = restore_root.parent / f".{restore_root.name}.restore-{uuid.uuid4().hex}"
    try:
        temporary_release = temporary_root / "releases" / dataset_id / snapshot_id
        for filename in _RELEASE_FILES:
            temporary_release.mkdir(parents=True, exist_ok=True)
            shutil.copy2(release_dir / filename, temporary_release / filename)
            make_read_only(temporary_release / filename)

        with zipfile.ZipFile(release_dir / "release.zip", "r") as bundle:
            _copy_stream_from_zip(bundle, "source/manifest.json", temporary_root / "archive" / "snapshots" / snapshot_id / "manifest.json")
            _copy_stream_from_zip(bundle, "source/manifest.json.sha256", temporary_root / "archive" / "snapshots" / snapshot_id / "manifest.json.sha256")
            for name in ("product/lei.sqlite", "product/lei.sqlite.sha256", "product/product.json", "product/product.json.sha256"):
                _copy_stream_from_zip(bundle, name, temporary_root / "products" / dataset_id / snapshot_id / Path(name).name)

        restore_root.mkdir(parents=True, exist_ok=True)
        for area in ("archive", "products", "releases"):
            source_area = temporary_root / area
            if source_area.exists():
                target_area = restore_root / area
                target_area.mkdir(parents=True, exist_ok=True)
                for child in source_area.iterdir():
                    target = target_area / child.name
                    if target.exists():
                        raise PlatformError(f"Restore would overwrite existing path: {target}")
                    child.rename(target)
        verify_product(restore_root, snapshot_id)
        append_event(
            restore_root / "events" / "events.jsonl",
            event="RESTORE_COMPLETED",
            payload={"snapshot_id": snapshot_id, "source_replica": str(replica_root)},
        )
    except Exception:
        shutil.rmtree(temporary_root, ignore_errors=True)
        raise
    else:
        shutil.rmtree(temporary_root, ignore_errors=True)

    return {
        "status": "RESTORED",
        "snapshot_id": snapshot_id,
        "restore_root": str(restore_root),
        "bundle_sha256": verified["bundle_sha256"],
        "query_failover_ready": True,
    }
