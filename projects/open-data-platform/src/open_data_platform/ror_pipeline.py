from __future__ import annotations

from pathlib import Path
from time import perf_counter
from typing import Any, Callable, TypeVar

from .analytics import build_quality_profile
from .events import append_event
from .production import data_footprint_bytes, persist_pipeline_metrics
from .ror_changes import build_ror_changes_around
from .ror_parser import parse_ror_snapshot
from .ror_product import build_ror_product
from .ror_release import build_ror_release
from .ror_source import ingest_latest_ror, ingest_ror_record
from .runtime import pipeline_lock
from .util import utc_now_iso


_T = TypeVar("_T")


def _timed(stage_seconds: dict[str, float], name: str, operation: Callable[[], _T]) -> _T:
    started = perf_counter()
    try:
        return operation()
    finally:
        stage_seconds[name] = perf_counter() - started


def run_ror_pipeline(
    *,
    source_config: Path,
    admission_config: Path,
    data_root: Path,
    replica_root: Path | None = None,
    normalized_root: Path | None = None,
    product_root: Path | None = None,
    release_root: Path | None = None,
    changes_root: Path | None = None,
    analytics_root: Path | None = None,
    zenodo_record_id: int | None = None,
) -> dict[str, Any]:
    with pipeline_lock(data_root) as lock_metadata:
        event_log = data_root / "events" / "events.jsonl"
        run_id = str(lock_metadata["run_id"])
        started_at = utc_now_iso()
        pipeline_started = perf_counter()
        stage_seconds: dict[str, float] = {}
        physical_before = data_footprint_bytes(data_root)
        result: dict[str, Any] | None = None
        append_event(
            event_log,
            event="ROR_PIPELINE_STARTED",
            payload={"run_id": run_id, "zenodo_record_id": zenodo_record_id},
        )
        try:
            if zenodo_record_id is None:
                ingest_operation = lambda: ingest_latest_ror(
                    source_config=source_config,
                    admission_config=admission_config,
                    data_root=data_root,
                    replica_root=replica_root,
                )
            else:
                ingest_operation = lambda: ingest_ror_record(
                    source_config=source_config,
                    admission_config=admission_config,
                    record_id=zenodo_record_id,
                    data_root=data_root,
                    replica_root=replica_root,
                )
            ingest = _timed(stage_seconds, "ingest", ingest_operation)
            snapshot_id = ingest["snapshot"]["snapshot_id"]
            parsed = _timed(
                stage_seconds,
                "parse",
                lambda: parse_ror_snapshot(
                    data_root,
                    snapshot_id,
                    output_root=normalized_root,
                    event_log=event_log,
                ),
            )
            product = _timed(
                stage_seconds,
                "product",
                lambda: build_ror_product(
                    data_root,
                    snapshot_id,
                    normalized_root=normalized_root,
                    output_root=product_root,
                    event_log=event_log,
                ),
            )
            release = _timed(
                stage_seconds,
                "release",
                lambda: build_ror_release(
                    data_root,
                    snapshot_id,
                    normalized_root=normalized_root,
                    product_root=product_root,
                    output_root=release_root,
                    event_log=event_log,
                ),
            )
            changes = _timed(
                stage_seconds,
                "changes",
                lambda: build_ror_changes_around(
                    data_root,
                    snapshot_id,
                    product_root=product_root,
                    output_root=changes_root,
                    event_log=event_log,
                ),
            )
            analytics = _timed(
                stage_seconds,
                "analytics",
                lambda: build_quality_profile(
                    data_root,
                    snapshot_id,
                    product_root=product_root,
                    normalized_root=normalized_root,
                    output_root=analytics_root,
                ),
            )
            result = {
                "status": "COMPLETED",
                "snapshot_id": snapshot_id,
                "ingest": ingest,
                "parse": parsed,
                "product": product,
                "release": release,
                "changes": changes,
                "analytics": analytics,
            }
            metrics = persist_pipeline_metrics(
                data_root=data_root,
                run_id=run_id,
                started_at=started_at,
                finished_at=utc_now_iso(),
                status="COMPLETED",
                stage_seconds=stage_seconds,
                total_seconds=perf_counter() - pipeline_started,
                physical_bytes_before=physical_before,
                physical_bytes_after=data_footprint_bytes(data_root),
                result=result,
            )
            result["metrics"] = metrics
            append_event(
                event_log,
                event="ROR_PIPELINE_COMPLETED",
                payload={
                    "run_id": run_id,
                    "snapshot_id": snapshot_id,
                    "metrics_path": metrics["metrics_path"],
                    "changes_status": changes["status"],
                    "analytics_status": analytics["status"],
                },
            )
            return result
        except Exception as exc:
            try:
                persist_pipeline_metrics(
                    data_root=data_root,
                    run_id=run_id,
                    started_at=started_at,
                    finished_at=utc_now_iso(),
                    status="FAILED",
                    stage_seconds=stage_seconds,
                    total_seconds=perf_counter() - pipeline_started,
                    physical_bytes_before=physical_before,
                    physical_bytes_after=data_footprint_bytes(data_root),
                    result=result,
                    error=str(exc),
                )
            except OSError as metrics_error:
                append_event(
                    event_log,
                    event="PIPELINE_METRICS_FAILED",
                    payload={"run_id": run_id, "error": str(metrics_error)},
                )
            append_event(
                event_log,
                event="ROR_PIPELINE_FAILED",
                payload={"run_id": run_id, "error": str(exc)},
            )
            raise
