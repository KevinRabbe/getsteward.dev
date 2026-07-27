from __future__ import annotations

import json
import os
import socket
import time
import uuid
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Iterator

from .errors import PlatformError
from .util import utc_now_iso


def _lock_path(data_root: Path) -> Path:
    return data_root / "events" / "pipeline.lock"


def _tree_size(path: Path) -> int:
    if not path.exists():
        return 0
    if path.is_file():
        return path.stat().st_size
    return sum(item.stat().st_size for item in path.rglob("*") if item.is_file())


def _pid_is_active(pid: int) -> bool:
    if pid == os.getpid():
        return True
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def pipeline_lock_status(data_root: Path) -> dict[str, Any]:
    path = _lock_path(data_root)
    if not path.exists():
        return {"status": "CLEAR", "lock_path": str(path)}
    try:
        metadata = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        return {
            "status": "CORRUPT",
            "lock_path": str(path),
            "error": str(exc),
        }
    pid = metadata.get("pid")
    if not isinstance(pid, int):
        return {
            "status": "CORRUPT",
            "lock_path": str(path),
            "metadata": metadata,
            "error": "Lock metadata has no integer pid",
        }
    return {
        "status": "ACTIVE" if _pid_is_active(pid) else "STALE",
        "lock_path": str(path),
        "metadata": metadata,
    }


@contextmanager
def pipeline_lock(data_root: Path) -> Iterator[dict[str, Any]]:
    path = _lock_path(data_root)
    path.parent.mkdir(parents=True, exist_ok=True)
    metadata = {
        "lock_version": 1,
        "run_id": f"run_{uuid.uuid4()}",
        "pid": os.getpid(),
        "host": socket.gethostname(),
        "started_at": utc_now_iso(),
    }
    payload = (json.dumps(metadata, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")
    try:
        descriptor = os.open(str(path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError as exc:
        state = pipeline_lock_status(data_root)
        raise PlatformError(
            f"A pipeline lock already exists ({state['status']}): {path}"
        ) from exc
    try:
        with os.fdopen(descriptor, "wb") as lock_file:
            lock_file.write(payload)
            lock_file.flush()
            os.fsync(lock_file.fileno())
        yield metadata
    finally:
        try:
            current = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            current = None
        if isinstance(current, dict) and current.get("run_id") == metadata["run_id"]:
            try:
                path.unlink()
            except FileNotFoundError:
                pass


def recovery_plan(data_root: Path) -> dict[str, Any]:
    """Report interrupted runtime artifacts without deleting anything."""

    artifacts: list[dict[str, Any]] = []
    for area in ("normalized", "products", "releases"):
        root = data_root / area
        if not root.exists():
            continue
        for path in root.rglob(".snp_*.tmp-*"):
            if not path.is_dir():
                continue
            stat = path.stat()
            artifacts.append(
                {
                    "area": area,
                    "kind": "published_artifact_temp",
                    "path": str(path),
                    "size_bytes": _tree_size(path),
                    "last_modified_epoch": stat.st_mtime,
                }
            )
    staging = data_root / "staging"
    for path in staging.glob("*.part") if staging.exists() else ():
        if path.is_file():
            artifacts.append(
                {
                    "area": "staging",
                    "kind": "partial_download",
                    "path": str(path),
                    "size_bytes": path.stat().st_size,
                    "last_modified_epoch": path.stat().st_mtime,
                }
            )

    lock = pipeline_lock_status(data_root)
    if lock["status"] in {"STALE", "CORRUPT"}:
        action = "REVIEW_LOCK_AND_TEMP_ARTIFACTS"
    elif lock["status"] == "ACTIVE":
        action = "WAIT_FOR_ACTIVE_PIPELINE"
    elif artifacts:
        action = "REVIEW_TEMP_ARTIFACTS_BEFORE_RETRY"
    else:
        action = "NO_RECOVERY_ACTION_REQUIRED"
    return {
        "mode": "REPORT_ONLY",
        "status": "REVIEW_REQUIRED" if lock["status"] != "CLEAR" or artifacts else "CLEAR",
        "recommended_action": action,
        "pipeline_lock": lock,
        "temporary_artifacts": artifacts,
        "temporary_artifact_count": len(artifacts),
        "generated_at_epoch": time.time(),
    }
