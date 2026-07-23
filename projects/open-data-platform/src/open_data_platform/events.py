from __future__ import annotations

import json
import os
from collections import Counter
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


def read_events(event_log: Path, *, limit: int | None = None) -> list[dict[str, Any]]:
    if not event_log.exists():
        return []
    events: list[dict[str, Any]] = []
    with event_log.open("r", encoding="utf-8") as source:
        for line_number, line in enumerate(source, start=1):
            if not line.strip():
                continue
            try:
                event = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ValueError(f"Invalid event JSON at line {line_number}: {event_log}") from exc
            if not isinstance(event, dict) or not isinstance(event.get("event"), str):
                raise ValueError(f"Invalid event record at line {line_number}: {event_log}")
            events.append(event)
    if limit is not None and limit > 0:
        return events[-limit:]
    return events


def summarize_events(event_log: Path, *, limit: int = 20) -> dict[str, Any]:
    events = read_events(event_log)
    counts = Counter(event["event"] for event in events)
    return {
        "event_log": str(event_log),
        "event_count": len(events),
        "event_counts": dict(sorted(counts.items())),
        "recent_events": events[-limit:] if limit > 0 else [],
    }
