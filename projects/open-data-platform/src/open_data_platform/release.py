from __future__ import annotations

import json
import shutil
import uuid
import zipfile
from pathlib import Path
from typing import BinaryIO, Any

from .errors import ReleaseError
from .events import append_event
from .parser import verify_normalized_artifact
from .product import verify_product
from .util import atomic_write_json, load_json, make_read_only, sha256_file
from .verify import verify_snapshot

_RELEASE_VERSION = "0.5.0"


def release_path(
    data_root: Path,
    snapshot_id: str,
    source_dataset_id: str,
    output_root: Path | None = None,
) -> Path:
    root = output_root if output_root is not None else data_root / "releases"
    return root / source_dataset_id / snapshot_id


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    checksum_path = path.with_name(path.name + ".sha256")
    checksum_path.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    return digest


def _verify_checksum(path: Path) -> str:
    checksum_path = path.with_name(path.name + ".sha256")
    if not checksum_path.exists():
        raise ReleaseError(f"Missing release checksum sidecar: {checksum_path}")
    expected = checksum_path.read_text(encoding="ascii").split()[0]
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ReleaseError(f"Release checksum mismatch: {path}")
    return actual


def _copy_with_checksum(source: Path, destination: Path, *, relative_to: Path) -> dict[str, Any]:
    if not source.exists():
        raise ReleaseError(f"Required release input is missing: {source}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    # copy2 preserves source read-only bits; the temporary payload must remain
    # removable on Windows until the final release directory is published.
    destination.chmod(0o666)
    digest, size = sha256_file(destination)
    _write_checksum(destination)
    return {
        "path": destination.relative_to(relative_to).as_posix(),
        "sha256": digest,
        "size_bytes": size,
    }


def _deterministic_zip(source_dir: Path, destination: Path) -> None:
    with zipfile.ZipFile(
        destination,
        "w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
    ) as bundle:
        for path in sorted(source_dir.rglob("*")):
            if not path.is_file():
                continue
            arcname = path.relative_to(source_dir).as_posix()
            info = zipfile.ZipInfo(arcname, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            info.external_attr = 0o644 << 16
            info.extra = b""
            info.comment = b""
            with path.open("rb") as source, bundle.open(info, "w", force_zip64=True) as destination:
                shutil.copyfileobj(source, destination, length=1024 * 1024)


def _hash_stream(stream: BinaryIO) -> tuple[str, int]:
    import hashlib

    digest = hashlib.sha256()
    size = 0
    while chunk := stream.read(1024 * 1024):
        digest.update(chunk)
        size += len(chunk)
    return digest.hexdigest(), size


def verify_release(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    snapshot_manifest = load_json(snapshot_dir / "manifest.json")
    final_dir = release_path(
        data_root,
        snapshot_id,
        str(snapshot_manifest["source_dataset_id"]),
        output_root,
    )
    release_manifest_path = final_dir / "release.json"
    bundle_path = final_dir / "release.zip"
    if not release_manifest_path.exists() or not bundle_path.exists():
        raise ReleaseError(f"Release not found for snapshot {snapshot_id}: {final_dir}")

    release_hash = _verify_checksum(release_manifest_path)
    bundle_hash = _verify_checksum(bundle_path)
    release_manifest = load_json(release_manifest_path)
    if release_manifest.get("source_snapshot_id") != snapshot_id:
        raise ReleaseError("Release belongs to a different snapshot")

    expected_files = {
        entry["path"]: entry
        for entry in release_manifest.get("files", [])
        if isinstance(entry, dict) and isinstance(entry.get("path"), str)
    }
    if not expected_files:
        raise ReleaseError("Release manifest contains no payload files")

    with zipfile.ZipFile(bundle_path, "r") as bundle:
        if bundle.testzip() is not None:
            raise ReleaseError("Release ZIP CRC verification failed")
        names = set(bundle.namelist())
        required_names = set(expected_files) | {
            f"{name}.sha256" for name in expected_files
        } | {"release.json", "release.json.sha256"}
        missing = sorted(required_names - names)
        if missing:
            raise ReleaseError(f"Release ZIP is missing files: {', '.join(missing)}")
        for name, expected in expected_files.items():
            with bundle.open(name, "r") as stream:
                actual_hash, actual_size = _hash_stream(stream)
            if actual_hash != expected["sha256"] or actual_size != expected["size_bytes"]:
                raise ReleaseError(f"Release payload checksum mismatch: {name}")
            with bundle.open(f"{name}.sha256", "r") as sidecar_stream:
                sidecar = sidecar_stream.read().decode("ascii").strip()
            expected_sidecar = f"{expected['sha256']}  {Path(name).name}"
            if sidecar != expected_sidecar:
                raise ReleaseError(f"Release payload checksum sidecar mismatch: {name}")

    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "release_dir": str(final_dir),
        "bundle_path": str(bundle_path),
        "release": {**release_manifest, "release_sha256": release_hash, "bundle_sha256": bundle_hash},
    }


def build_release(
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

    log("RELEASE_BUILD_STARTED", {"snapshot_id": snapshot_id})
    try:
        verify_snapshot(data_root, snapshot_id)
        product = verify_product(data_root, snapshot_id, output_root=product_root)
        normalized = verify_normalized_artifact(data_root, snapshot_id, output_root=normalized_root)
        if product["product"]["normalized_artifact_sha256"] != normalized["artifact"]["artifact_sha256"]:
            raise ReleaseError("Product does not match the normalized artifact")

        if final_dir.exists():
            verified = verify_release(data_root, snapshot_id, output_root=output_root)
            verified["status"] = "NO_CHANGE"
            log("RELEASE_BUILD_NO_CHANGE", {"snapshot_id": snapshot_id})
            return verified

        final_dir.parent.mkdir(parents=True, exist_ok=True)
        temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
        temporary_dir.mkdir(parents=False, exist_ok=False)
        try:
            payload_dir = temporary_dir / "payload"
            product_dir = Path(product["product_dir"])
            normalized_dir = Path(normalized["artifact_dir"])
            source_manifest_dir = snapshot_dir

            copied = []
            copied.append(_copy_with_checksum(
                product_dir / "lei.sqlite",
                payload_dir / "product" / "lei.sqlite",
                relative_to=payload_dir,
            ))
            copied.append(_copy_with_checksum(
                product_dir / "product.json",
                payload_dir / "product" / "product.json",
                relative_to=payload_dir,
            ))
            copied.append(_copy_with_checksum(
                source_manifest_dir / "manifest.json",
                payload_dir / "source" / "manifest.json",
                relative_to=payload_dir,
            ))
            copied.append(_copy_with_checksum(
                normalized_dir / "quality.json",
                payload_dir / "normalized" / "quality.json",
                relative_to=payload_dir,
            ))

            release_manifest = {
                "release_version": 1,
                "release_type": "gleif_level1_sqlite_bundle",
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
            payload_release_manifest = payload_dir / "release.json"
            atomic_write_json(payload_release_manifest, release_manifest)
            _write_checksum(payload_release_manifest)

            bundle_path = temporary_dir / "release.zip"
            _deterministic_zip(payload_dir, bundle_path)
            bundle_hash, bundle_size = sha256_file(bundle_path)
            _write_checksum(bundle_path)

            final_release_manifest = temporary_dir / "release.json"
            shutil.copy2(payload_release_manifest, final_release_manifest)
            _write_checksum(final_release_manifest)
            shutil.rmtree(payload_dir)
            temporary_dir.rename(final_dir)
            for published_file in final_dir.rglob("*"):
                if published_file.is_file():
                    make_read_only(published_file)
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
        log("RELEASE_BUILD_COMPLETED", {"snapshot_id": snapshot_id, "bundle_sha256": bundle_hash})
        return result
    except Exception as exc:
        log("RELEASE_BUILD_FAILED", {"snapshot_id": snapshot_id, "error": str(exc)})
        raise
