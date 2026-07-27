from __future__ import annotations

import shutil
import uuid
from pathlib import Path
from typing import Any

from .events import summarize_events
from .errors import PlatformError
from .parser import verify_normalized_artifact
from .product import verify_product
from .release import verify_release
from .util import load_json, make_read_only, sha256_file
from .verify import verify_snapshot


def _replica_file(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    destination.chmod(0o666)


def _verified_file_hash(path: Path) -> tuple[str, int]:
    if not path.exists():
        raise PlatformError(f"Replicated file is missing: {path}")
    return sha256_file(path)


def replicate_release(
    data_root: Path,
    snapshot_id: str,
    replica_root: Path,
    *,
    release_root: Path | None = None,
) -> dict[str, Any]:
    verified = verify_release(data_root, snapshot_id, output_root=release_root)
    source_dir = Path(verified["release_dir"])
    source_manifest = load_json(source_dir / "release.json")
    source_dataset_id = str(source_manifest["source_dataset_id"])
    destination_dir = replica_root / source_dataset_id / snapshot_id
    if destination_dir.exists():
        for filename in ("release.json", "release.json.sha256", "release.zip", "release.zip.sha256"):
            source_hash, source_size = _verified_file_hash(source_dir / filename)
            destination_hash, destination_size = _verified_file_hash(destination_dir / filename)
            if (source_hash, source_size) != (destination_hash, destination_size):
                raise PlatformError(f"Replica differs from verified release: {destination_dir / filename}")
        return {"status": "NO_CHANGE", "snapshot_id": snapshot_id, "replica_dir": str(destination_dir)}

    destination_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = destination_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    try:
        for filename in ("release.json", "release.json.sha256", "release.zip", "release.zip.sha256"):
            _replica_file(source_dir / filename, temporary_dir / filename)
        temporary_dir.rename(destination_dir)
        for replicated_file in destination_dir.rglob("*"):
            if replicated_file.is_file():
                make_read_only(replicated_file)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        raise

    return {
        "status": "REPLICATED",
        "snapshot_id": snapshot_id,
        "replica_dir": str(destination_dir),
        "bundle_sha256": sha256_file(source_dir / "release.zip")[0],
    }


def status_report(data_root: Path, *, event_limit: int = 20) -> dict[str, Any]:
    snapshots_root = data_root / "archive" / "snapshots"
    snapshots: list[dict[str, Any]] = []
    if snapshots_root.exists():
        for manifest_path in sorted(snapshots_root.glob("snp_*/manifest.json")):
            try:
                manifest = load_json(manifest_path)
                snapshot_id = str(manifest["snapshot_id"])
                entry: dict[str, Any] = {
                    "snapshot_id": snapshot_id,
                    "source_version": manifest.get("source_version"),
                    "raw": "VERIFIED",
                }
                try:
                    verify_snapshot(data_root, snapshot_id)
                except Exception as exc:
                    entry["raw"] = f"FAILED: {exc}"
                try:
                    verify_normalized_artifact(data_root, snapshot_id)
                    entry["normalized"] = "VERIFIED"
                except Exception as exc:
                    entry["normalized"] = "MISSING_OR_FAILED: " + str(exc)
                try:
                    verify_product(data_root, snapshot_id)
                    entry["product"] = "VERIFIED"
                except Exception as exc:
                    entry["product"] = "MISSING_OR_FAILED: " + str(exc)
                try:
                    verify_release(data_root, snapshot_id)
                    entry["release"] = "VERIFIED"
                except Exception as exc:
                    entry["release"] = "MISSING_OR_FAILED: " + str(exc)
                snapshots.append(entry)
            except (OSError, ValueError, KeyError) as exc:
                snapshots.append({"manifest": str(manifest_path), "status": f"FAILED: {exc}"})

    snapshots.sort(key=lambda item: (str(item.get("source_version", "")), str(item.get("snapshot_id", ""))))
    return {
        "snapshot_count": len(snapshots),
        "snapshots": snapshots,
        "events": summarize_events(data_root / "events" / "events.jsonl", limit=event_limit),
    }
