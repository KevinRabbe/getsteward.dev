from __future__ import annotations

from pathlib import Path
from time import perf_counter
from typing import Any, Callable, TypeVar

from .events import append_event
from .ingest import ingest_latest
from .parser import parse_snapshot
from .product import build_product
from .production import data_footprint_bytes, persist_pipeline_metrics
from .release import build_release
from .runtime import pipeline_lock
from .util import utc_now_iso


_T = TypeVar("_T")


def _timed(stage_seconds: dict[str, float], name: str, operation: Callable[[], _T]) -> _T:
    started = perf_counter()
    try:
        return operation()
    finally:
        stage_seconds[name] = perf_counter() - started


def run_gleif_pipeline(
    *,
    source_config: Path,
    admission_config: Path,
    data_root: Path,
    replica_root: Path | None = None,
    normalized_root: Path | None = None,
    product_root: Path | None = None,
    release_root: Path | None = None,
) -> dict[str, Any]:
    with pipeline_lock(data_root) as lock_metadata:
        event_log = data_root / "events" / "events.jsonl"
        run_id = str(lock_metadata["run_id"])
        started_at = utc_now_iso()
        pipeline_started = perf_counter()
        stage_seconds: dict[str, float] = {}
        physical_bytes_before = data_footprint_bytes(data_root)
        result: dict[str, Any] | None = None

        append_event(
            event_log,
            event="PIPELINE_STARTED",
            payload={"source": str(source_config), "run_id": run_id},
        )
        try:
            ingest = _timed(
                stage_seconds,
                "ingest",
                lambda: ingest_latest(
                    source_config=source_config,
                    admission_config=admission_config,
                    data_root=data_root,
                    replica_root=replica_root,
                ),
            )
            snapshot = ingest["snapshot"]
            snapshot_id = snapshot["snapshot_id"]
            parsed = _timed(
                stage_seconds,
                "parse",
                lambda: parse_snapshot(
                    data_root,
                    snapshot_id,
                    output_root=normalized_root,
                    event_log=event_log,
                ),
            )
            product = _timed(
                stage_seconds,
                "product",
                lambda: build_product(
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
                lambda: build_release(
                    data_root,
                    snapshot_id,
                    product_root=product_root,
                    normalized_root=normalized_root,
                    output_root=release_root,
                    event_log=event_log,
                ),
            )
            result = {
                "status": "COMPLETED",
                "snapshot_id": snapshot_id,
                "ingest": ingest,
                "parse": parsed,
                "product": product,
                "release": release,
            }
            physical_bytes_after = data_footprint_bytes(data_root)
            metrics = persist_pipeline_metrics(
                data_root=data_root,
                run_id=run_id,
                started_at=started_at,
                finished_at=utc_now_iso(),
                status="COMPLETED",
                stage_seconds=stage_seconds,
                total_seconds=perf_counter() - pipeline_started,
                physical_bytes_before=physical_bytes_before,
                physical_bytes_after=physical_bytes_after,
                result=result,
            )
            result["metrics"] = metrics
            append_event(
                event_log,
                event="PIPELINE_COMPLETED",
                payload={
                    "snapshot_id": snapshot_id,
                    "status": result["status"],
                    "run_id": run_id,
                    "metrics_path": metrics["metrics_path"],
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
                    physical_bytes_before=physical_bytes_before,
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
                event="PIPELINE_FAILED",
                payload={"run_id": run_id, "error": str(exc)},
            )
            raise
