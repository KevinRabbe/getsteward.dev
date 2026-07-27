from __future__ import annotations

from pathlib import Path
from typing import Any

from .parser import verify_normalized_artifact
from .product import verify_product
from .release import verify_release
from .util import load_json
from .verify import verify_snapshot


def _tree_size(path: Path) -> int:
    if not path.exists():
        return 0
    if path.is_file():
        return path.stat().st_size
    return sum(item.stat().st_size for item in path.rglob("*") if item.is_file())


def storage_inventory(data_root: Path) -> dict[str, Any]:
    categories = (
        "staging",
        "archive",
        "normalized",
        "products",
        "releases",
        "events",
    )
    sizes = {category: _tree_size(data_root / category) for category in categories}
    return {"bytes_by_area": sizes, "total_bytes": sum(sizes.values())}


def retention_plan(data_root: Path, *, keep_latest: int = 7) -> dict[str, Any]:
    if keep_latest < 1:
        raise ValueError("keep_latest must be at least 1")

    manifests = []
    snapshots_root = data_root / "archive" / "snapshots"
    for manifest_path in snapshots_root.glob("snp_*/manifest.json"):
        try:
            manifest = load_json(manifest_path)
            manifests.append((str(manifest.get("source_version", "")), str(manifest["snapshot_id"]), manifest))
        except (OSError, ValueError, KeyError):
            continue
    manifests.sort(key=lambda item: (item[0], item[1]), reverse=True)

    retained = []
    candidates = []
    protected = []
    for index, (source_version, snapshot_id, manifest) in enumerate(manifests):
        entry = {
            "snapshot_id": snapshot_id,
            "source_version": source_version,
            "raw_bytes": _tree_size(snapshots_root / snapshot_id),
        }
        if index < keep_latest:
            entry["reason"] = "within_keep_latest_window"
            retained.append(entry)
            continue

        failures: list[str] = []
        for label, verifier in (
            ("raw", lambda: verify_snapshot(data_root, snapshot_id)),
            ("normalized", lambda: verify_normalized_artifact(data_root, snapshot_id)),
            ("product", lambda: verify_product(data_root, snapshot_id)),
            ("release", lambda: verify_release(data_root, snapshot_id)),
        ):
            try:
                verifier()
            except Exception as exc:
                failures.append(f"{label}: {exc}")
        if failures:
            entry["reason"] = "protected_incomplete_or_failed_boundary"
            entry["failures"] = failures
            protected.append(entry)
        else:
            entry["reason"] = "eligible_for_external_archive_review"
            entry["artifact_bytes"] = sum(
                _tree_size(data_root / area / str(manifest["source_dataset_id"]) / snapshot_id)
                for area in ("normalized", "products", "releases")
            )
            candidates.append(entry)

    return {
        "mode": "REPORT_ONLY",
        "policy": {
            "keep_latest": keep_latest,
            "deletion_enabled": False,
            "requires_external_archive_confirmation": True,
        },
        "snapshot_count": len(manifests),
        "retained": retained,
        "eligible_for_archive_review": candidates,
        "protected": protected,
        "storage": storage_inventory(data_root),
    }
