from __future__ import annotations

import re
from pathlib import Path
from typing import Any

_TIME_RE = re.compile(r"^(?:[01]\d|2[0-3]):[0-5]\d$")


def schedule_plan(
    *,
    project_root: Path,
    python_exe: str = "python",
    data_root: Path = Path("data"),
    replica_root: Path | None = None,
    frequency: str = "daily",
    start_time: str = "02:00",
) -> dict[str, Any]:
    frequency = frequency.lower()
    if frequency not in {"daily", "weekly"}:
        raise ValueError("frequency must be daily or weekly")
    if not _TIME_RE.fullmatch(start_time):
        raise ValueError("start_time must use HH:MM in 24-hour format")

    wrapper = project_root / "scripts" / "run_gleif_pipeline.ps1"
    command = f'PowerShell.exe -NoProfile -ExecutionPolicy Bypass -File "{wrapper}"'
    command += f' -PythonExe "{python_exe}" -DataRoot "{data_root}"'
    if replica_root is not None:
        command += f' -ReplicaRoot "{replica_root}"'
    return {
        "mode": "PLAN_ONLY",
        "scheduler": "Windows Task Scheduler",
        "frequency": frequency,
        "start_time": start_time,
        "project_root": str(project_root),
        "data_root": str(data_root),
        "replica_root": str(replica_root) if replica_root is not None else None,
        "wrapper": str(wrapper),
        "command": command,
        "lock_required": True,
        "recovery_command": f'{python_exe} .\\odp.py recovery-plan --data-root "{data_root}"',
        "registration": "Use scripts/register_task_scheduler.ps1; no task is created by this command.",
    }
