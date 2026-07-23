from __future__ import annotations

from pathlib import Path
from typing import Any

from .parser import verify_normalized_artifact
from .product import verify_product
from .release import verify_release
from .util import load_json, sha256_file


def _release_manifests(release_root: Path, snapshot_id: str | None) -> list[tuple[str, str, Path, dict[str, Any]]]:
    pattern = f"*/{snapshot_id}/release.json" if snapshot_id else "*/snp_*/release.json"
    found: list[tuple[str, str, Path, dict[str, Any]]] = []
    for manifest_path in release_root.glob(pattern):
        try:
            manifest = load_json(manifest_path)
            found.append(
                (
                    str(manifest.get("source_version", "")),
                    str(manifest["source_snapshot_id"]),
                    manifest_path,
                    manifest,
                )
            )
        except (OSError, ValueError, KeyError):
            continue
    found.sort(key=lambda item: (item[0], item[1]), reverse=True)
    return found


def _replica_check(
    release_dir: Path,
    replica_root: Path,
    source_dataset_id: str,
    snapshot_id: str,
) -> dict[str, Any]:
    replica_dir = replica_root / source_dataset_id / snapshot_id
    files = ("release.json", "release.json.sha256", "release.zip", "release.zip.sha256")
    missing = [name for name in files if not (replica_dir / name).exists()]
    if missing:
        return {
            "name": "verified_replica",
            "status": "FAILED",
            "replica_dir": str(replica_dir),
            "error": "Missing replica files: " + ", ".join(missing),
        }

    mismatches = []
    for name in files:
        source_path = release_dir / name
        replica_path = replica_dir / name
        if sha256_file(source_path) != sha256_file(replica_path):
            mismatches.append(name)
    if mismatches:
        return {
            "name": "verified_replica",
            "status": "FAILED",
            "replica_dir": str(replica_dir),
            "error": "Replica differs from the verified release: " + ", ".join(mismatches),
        }
    return {
        "name": "verified_replica",
        "status": "VERIFIED",
        "replica_dir": str(replica_dir),
    }


def deployment_readiness(
    data_root: Path,
    *,
    snapshot_id: str | None = None,
    release_root: Path | None = None,
    product_root: Path | None = None,
    replica_root: Path | None = None,
) -> dict[str, Any]:
    """Report whether a verified release is ready for downstream deployment.

    This function is intentionally report-only. It never starts a service, copies
    files, or changes retention state.
    """

    selected_release_root = release_root if release_root is not None else data_root / "releases"
    selected_product_root = product_root if product_root is not None else data_root / "products"
    entries = _release_manifests(selected_release_root, snapshot_id)
    checks: list[dict[str, Any]] = []
    selected: dict[str, Any] | None = None

    if not entries:
        checks.append(
            {
                "name": "release",
                "status": "FAILED",
                "error": "No release manifest found",
                "release_root": str(selected_release_root),
            }
        )
    else:
        _, selected_snapshot_id, manifest_path, release_manifest = entries[0]
        source_dataset_id = str(release_manifest["source_dataset_id"])
        release_dir = manifest_path.parent
        selected = {
            "snapshot_id": selected_snapshot_id,
            "source_dataset_id": source_dataset_id,
            "source_version": release_manifest.get("source_version"),
            "release_dir": str(release_dir),
            "bundle_sha256": release_manifest.get("bundle_sha256"),
        }
        try:
            verified_release = verify_release(
                data_root,
                selected_snapshot_id,
                output_root=selected_release_root,
            )
            checks.append(
                {
                    "name": "release",
                    "status": "VERIFIED",
                    "snapshot_id": selected_snapshot_id,
                    "bundle_sha256": verified_release["release"]["bundle_sha256"],
                }
            )
            selected["bundle_sha256"] = verified_release["release"]["bundle_sha256"]
        except Exception as exc:
            checks.append(
                {
                    "name": "release",
                    "status": "FAILED",
                    "snapshot_id": selected_snapshot_id,
                    "error": str(exc),
                }
            )

        try:
            verified_product = verify_product(
                data_root,
                selected_snapshot_id,
                output_root=selected_product_root,
            )
            checks.append(
                {
                    "name": "query_product",
                    "status": "VERIFIED",
                    "snapshot_id": selected_snapshot_id,
                    "record_count": verified_product["record_count"],
                }
            )
        except Exception as exc:
            checks.append(
                {
                    "name": "query_product",
                    "status": "FAILED",
                    "snapshot_id": selected_snapshot_id,
                    "error": str(exc),
                }
            )

        try:
            verified_normalized = verify_normalized_artifact(data_root, selected_snapshot_id)
            checks.append(
                {
                    "name": "normalized_artifact",
                    "status": "VERIFIED",
                    "snapshot_id": selected_snapshot_id,
                    "record_count": verified_normalized["record_count"],
                    "file_content": verified_normalized["quality"].get("file_content"),
                }
            )
        except Exception as exc:
            checks.append(
                {
                    "name": "normalized_artifact",
                    "status": "FAILED",
                    "snapshot_id": selected_snapshot_id,
                    "error": str(exc),
                }
            )

        if replica_root is not None:
            checks.append(
                _replica_check(
                    release_dir,
                    replica_root,
                    source_dataset_id,
                    selected_snapshot_id,
                )
            )

    ready = bool(checks) and all(check["status"] == "VERIFIED" for check in checks)
    selected_snapshot = selected["snapshot_id"] if selected else snapshot_id
    return {
        "status": "READY" if ready else "NOT_READY",
        "mode": "REPORT_ONLY",
        "checks": checks,
        "selected_release": selected,
        "service": {
            "read_only": True,
            "health_endpoint": "/healthz",
            "status_endpoint": "/v1/status",
            "suggested_command": (
                "python .\\odp.py serve"
                + (f" --snapshot-id {selected_snapshot}" if selected_snapshot else "")
            ),
        },
        "scheduled_run": {
            "wrapper": "scripts/run_gleif_pipeline.ps1",
            "suggested_command": ".\\scripts\\run_gleif_pipeline.ps1",
        },
    }
