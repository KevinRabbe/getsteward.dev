from __future__ import annotations

import ctypes
import json
import os
import statistics
import sys
from datetime import date
from pathlib import Path
from typing import Any

from .util import atomic_write_json


_METRICS_VERSION = 2
_DEFAULT_ACCEPTANCE_DATASET = "ds_gleif_lei_level1_concat"


def process_peak_rss_bytes() -> int | None:
    """Return the process peak resident/working-set size using only the stdlib."""
    try:
        if os.name == "nt":
            class ProcessMemoryCounters(ctypes.Structure):
                _fields_ = [
                    ("cb", ctypes.c_ulong),
                    ("PageFaultCount", ctypes.c_ulong),
                    ("PeakWorkingSetSize", ctypes.c_size_t),
                    ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t),
                    ("PeakPagefileUsage", ctypes.c_size_t),
                ]

            counters = ProcessMemoryCounters()
            counters.cb = ctypes.sizeof(counters)
            handle = ctypes.windll.kernel32.GetCurrentProcess()
            ok = ctypes.windll.psapi.GetProcessMemoryInfo(
                handle,
                ctypes.byref(counters),
                counters.cb,
            )
            return int(counters.PeakWorkingSetSize) if ok else None

        import resource

        value = int(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss)
        return value if sys.platform == "darwin" else value * 1024
    except (AttributeError, OSError, ValueError):
        return None


def data_footprint_bytes(data_root: Path) -> int:
    """Measure physical runtime bytes without counting metrics about themselves."""
    if not data_root.exists():
        return 0
    total = 0
    metrics_root = (data_root / "metrics").resolve()
    for root, dirs, files in os.walk(data_root):
        root_path = Path(root)
        dirs[:] = [
            name
            for name in dirs
            if (root_path / name).resolve() != metrics_root
        ]
        for name in files:
            path = root_path / name
            try:
                if path.is_symlink():
                    continue
                total += path.stat().st_size
            except FileNotFoundError:
                continue
    return total


def _safe_file_size(value: Any) -> int | None:
    if not value:
        return None
    try:
        path = Path(str(value))
        return path.stat().st_size if path.is_file() else None
    except OSError:
        return None


def persist_pipeline_metrics(
    *,
    data_root: Path,
    run_id: str,
    started_at: str,
    finished_at: str,
    status: str,
    stage_seconds: dict[str, float],
    total_seconds: float,
    physical_bytes_before: int,
    physical_bytes_after: int,
    result: dict[str, Any] | None = None,
    error: str | None = None,
) -> dict[str, Any]:
    result = result or {}
    ingest = result.get("ingest") or {}
    snapshot = ingest.get("snapshot") or {}
    parsed = result.get("parse") or {}
    artifact = parsed.get("artifact") or {}
    product_result = result.get("product") or {}
    product = product_result.get("product") or {}
    release_result = result.get("release") or {}

    bundle_size = release_result.get("bundle_size_bytes")
    if bundle_size is None:
        bundle_size = _safe_file_size(release_result.get("bundle_path"))

    acquisition = snapshot.get("acquisition_response") or {}
    metrics = {
        "metrics_version": _METRICS_VERSION,
        "run_id": run_id,
        "status": status,
        "started_at": started_at,
        "finished_at": finished_at,
        "snapshot_id": result.get("snapshot_id"),
        "source_dataset_id": snapshot.get("source_dataset_id") or product.get("source_dataset_id") or artifact.get("source_dataset_id"),
        "source_version": snapshot.get("source_version"),
        "stage_seconds": {key: round(float(value), 6) for key, value in stage_seconds.items()},
        "total_seconds": round(float(total_seconds), 6),
        "peak_rss_bytes": process_peak_rss_bytes(),
        "download_bytes": acquisition.get("bytes_downloaded"),
        "raw_size_bytes": (snapshot.get("content") or {}).get("original_size_bytes"),
        "normalized_records_bytes": artifact.get("records_size_bytes"),
        "product_database_bytes": product.get("database_size_bytes"),
        "release_bundle_bytes": bundle_size,
        "record_count": product_result.get("record_count") or product.get("record_count") or artifact.get("record_count"),
        "unique_lei_count": product.get("unique_lei_count"),
        "duplicate_lei_count": product.get("duplicate_lei_count"),
        "physical_data_bytes_before": int(physical_bytes_before),
        "physical_data_bytes_after": int(physical_bytes_after),
        "physical_growth_bytes": int(physical_bytes_after - physical_bytes_before),
        "error": error,
    }
    metrics_path = data_root / "metrics" / "runs" / f"{run_id}.json"
    atomic_write_json(metrics_path, metrics)
    return {**metrics, "metrics_path": str(metrics_path)}


def _load_metrics(data_root: Path) -> tuple[list[dict[str, Any]], list[str]]:
    runs_root = data_root / "metrics" / "runs"
    if not runs_root.exists():
        return [], []
    runs: list[dict[str, Any]] = []
    errors: list[str] = []
    for path in sorted(runs_root.glob("*.json")):
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(value, dict):
                raise ValueError("expected JSON object")
            runs.append(value)
        except (OSError, json.JSONDecodeError, ValueError) as exc:
            errors.append(f"{path}: {exc}")
    return runs, errors


def _mean(values: list[float | int | None]) -> float | None:
    usable = [float(value) for value in values if value is not None]
    return round(statistics.fmean(usable), 3) if usable else None


def production_acceptance_report(
    data_root: Path,
    *,
    required_versions: int = 7,
    source_dataset_id: str = _DEFAULT_ACCEPTANCE_DATASET,
) -> dict[str, Any]:
    if required_versions < 1:
        raise ValueError("required_versions must be at least 1")

    runs, load_errors = _load_metrics(data_root)
    dataset_runs = [run for run in runs if run.get("source_dataset_id") in (None, source_dataset_id)]
    completed = [
        run for run in dataset_runs
        if run.get("status") == "COMPLETED" and run.get("source_version")
    ]

    # Metrics v1 predates source_dataset_id and therefore remains eligible only for
    # the original Level-1 acceptance dataset. Metrics v2+ are dataset-scoped.
    if source_dataset_id != _DEFAULT_ACCEPTANCE_DATASET:
        completed = [run for run in completed if run.get("source_dataset_id") == source_dataset_id]

    by_version: dict[str, dict[str, Any]] = {}
    for run in completed:
        source_version = str(run["source_version"])
        previous = by_version.get(source_version)
        if previous is None or str(run.get("finished_at", "")) > str(previous.get("finished_at", "")):
            by_version[source_version] = run
    versions = sorted(by_version)
    selected = [by_version[version] for version in versions]

    span_days = None
    if len(versions) >= 2:
        try:
            span_days = (date.fromisoformat(versions[-1]) - date.fromisoformat(versions[0])).days + 1
        except ValueError:
            span_days = None

    physical_sizes = [run.get("physical_data_bytes_after") for run in selected]
    storage_growth = None
    if len(physical_sizes) >= 2 and physical_sizes[0] is not None and physical_sizes[-1] is not None:
        storage_growth = int(physical_sizes[-1]) - int(physical_sizes[0])

    status = "PASS" if len(versions) >= required_versions else "IN_PROGRESS"
    if load_errors:
        status = "ALERT"

    return {
        "status": status,
        "source_dataset_id": source_dataset_id,
        "required_distinct_source_versions": required_versions,
        "observed_distinct_source_versions": len(versions),
        "remaining_source_versions": max(0, required_versions - len(versions)),
        "source_versions": versions,
        "first_source_version": versions[0] if versions else None,
        "latest_source_version": versions[-1] if versions else None,
        "calendar_span_days": span_days,
        "completed_run_count": len(completed),
        "failed_run_count": sum(1 for run in dataset_runs if run.get("status") == "FAILED"),
        "metrics_load_errors": load_errors,
        "performance": {
            "average_total_seconds": _mean([run.get("total_seconds") for run in selected]),
            "average_download_seconds": _mean([(run.get("stage_seconds") or {}).get("ingest") for run in selected]),
            "average_parse_seconds": _mean([(run.get("stage_seconds") or {}).get("parse") for run in selected]),
            "average_product_seconds": _mean([(run.get("stage_seconds") or {}).get("product") for run in selected]),
            "average_release_seconds": _mean([(run.get("stage_seconds") or {}).get("release") for run in selected]),
            "max_peak_rss_bytes": max(
                (int(run["peak_rss_bytes"]) for run in selected if run.get("peak_rss_bytes") is not None),
                default=None,
            ),
        },
        "storage": {
            "latest_physical_data_bytes": physical_sizes[-1] if physical_sizes else None,
            "growth_across_observed_versions_bytes": storage_growth,
            "average_growth_per_new_version_bytes": (
                round(storage_growth / (len(physical_sizes) - 1), 3)
                if storage_growth is not None and len(physical_sizes) >= 2
                else None
            ),
            "average_raw_snapshot_bytes": _mean([run.get("raw_size_bytes") for run in selected]),
            "average_release_bundle_bytes": _mean([run.get("release_bundle_bytes") for run in selected]),
        },
        "latest_run": selected[-1] if selected else None,
        "acceptance_rule": (
            "PASS requires the requested number of distinct successful source versions for this dataset; "
            "reruns of one source version do not advance the production window."
        ),
    }
