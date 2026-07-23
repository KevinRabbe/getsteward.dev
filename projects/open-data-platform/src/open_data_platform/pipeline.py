from __future__ import annotations

from pathlib import Path
from typing import Any

from .events import append_event
from .ingest import ingest_latest
from .parser import parse_snapshot
from .product import build_product
from .release import build_release


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
    event_log = data_root / "events" / "events.jsonl"
    append_event(event_log, event="PIPELINE_STARTED", payload={"source": str(source_config)})
    try:
        ingest = ingest_latest(
            source_config=source_config,
            admission_config=admission_config,
            data_root=data_root,
            replica_root=replica_root,
        )
        snapshot = ingest["snapshot"]
        snapshot_id = snapshot["snapshot_id"]
        parsed = parse_snapshot(
            data_root,
            snapshot_id,
            output_root=normalized_root,
            event_log=event_log,
        )
        product = build_product(
            data_root,
            snapshot_id,
            normalized_root=normalized_root,
            output_root=product_root,
            event_log=event_log,
        )
        release = build_release(
            data_root,
            snapshot_id,
            product_root=product_root,
            normalized_root=normalized_root,
            output_root=release_root,
            event_log=event_log,
        )
        result = {
            "status": "COMPLETED",
            "snapshot_id": snapshot_id,
            "ingest": ingest,
            "parse": parsed,
            "product": product,
            "release": release,
        }
        append_event(
            event_log,
            event="PIPELINE_COMPLETED",
            payload={"snapshot_id": snapshot_id, "status": result["status"]},
        )
        return result
    except Exception as exc:
        append_event(event_log, event="PIPELINE_FAILED", payload={"error": str(exc)})
        raise
