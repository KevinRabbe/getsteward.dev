from __future__ import annotations

from pathlib import Path
from typing import Any

from .deployment import deployment_readiness
from .events import summarize_events
from .runtime import pipeline_lock_status, recovery_plan


def monitor_report(
    data_root: Path,
    *,
    snapshot_id: str | None = None,
    release_root: Path | None = None,
    product_root: Path | None = None,
    replica_root: Path | None = None,
    event_limit: int = 10,
) -> dict[str, Any]:
    readiness = deployment_readiness(
        data_root,
        snapshot_id=snapshot_id,
        release_root=release_root,
        product_root=product_root,
        replica_root=replica_root,
    )
    lock = pipeline_lock_status(data_root)
    recovery = recovery_plan(data_root)
    alerts: list[dict[str, Any]] = []

    if readiness["status"] != "READY":
        alerts.append({"code": "DEPLOYMENT_NOT_READY", "details": readiness["checks"]})
    if lock["status"] != "CLEAR":
        alerts.append({"code": "PIPELINE_LOCK_" + lock["status"], "details": lock})
    if recovery["temporary_artifact_count"]:
        alerts.append(
            {
                "code": "INTERRUPTED_ARTIFACTS_PRESENT",
                "details": recovery["temporary_artifacts"],
            }
        )

    return {
        "status": "HEALTHY" if not alerts else "ALERT",
        "exit_code": 0 if not alerts else 3,
        "alerts": alerts,
        "deployment": readiness,
        "pipeline_lock": lock,
        "recovery": recovery,
        "events": summarize_events(data_root / "events" / "events.jsonl", limit=event_limit),
    }
