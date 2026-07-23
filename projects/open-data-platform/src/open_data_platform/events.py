from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from .util import utc_now_iso


def append_event(event_log: Path, *, event: str, payload: dict[str, Any] | None = None) -> None:
    event_log.parent.mkdir(parents=True, exist_ok=True)
    record = {
        "timestamp": utc_now_iso(),
        "event": event,
        "payload": payload or {},
    }
    with event_log.open("a", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(record, ensure_ascii=False, sort_keys=True) + "\n")
        f.flush()
        os.fsync(f.fileno())
