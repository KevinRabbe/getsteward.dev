from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from .archive import archive_staged_file, replicate_content
from .discovery import discover_latest
from .events import append_event
from .http_client import stream_download
from .registry import load_admission, load_source


def _event_log(data_root: Path) -> Path:
    return data_root / "events" / "events.jsonl"


def _snapshot_already_archived(data_root: Path, source_dataset_id: str, publication_date: str) -> dict[str, Any] | None:
    snapshots = data_root / "archive" / "snapshots"
    if not snapshots.exists():
        return None
    for path in snapshots.glob("snp_*/manifest.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if data.get("source_dataset_id") == source_dataset_id and data.get("source_version") == publication_date:
            return data
    return None


def ingest_latest(
    *,
    source_config: Path,
    admission_config: Path,
    data_root: Path,
    replica_root: Path | None = None,
) -> dict[str, Any]:
    source = load_source(source_config)
    admission = load_admission(admission_config, source)
    event_log = _event_log(data_root)

    append_event(event_log, event="SOURCE_REGISTERED", payload={"source_dataset_id": source["source_dataset_id"]})
    append_event(event_log, event="ADMITTED", payload={"admission_decision_id": admission["admission_decision_id"]})

    remote = discover_latest(source)
    append_event(event_log, event="REMOTE_VERSION_DISCOVERED", payload={"source_version": remote.publication_date})

    existing = _snapshot_already_archived(data_root, source["source_dataset_id"], remote.publication_date)
    if existing is not None:
        append_event(event_log, event="NO_CHANGE", payload={"snapshot_id": existing["snapshot_id"], "source_version": remote.publication_date})
        return {"status": "NO_CHANGE", "snapshot": existing}

    staging = data_root / "staging"
    staging.mkdir(parents=True, exist_ok=True)
    staged_path = staging / f"{source['source_dataset_id']}_{remote.publication_date}.zip.part"

    append_event(event_log, event="DOWNLOAD_TO_STAGING", payload={"url": remote.download_url})
    response_meta = stream_download(remote.download_url, staged_path, source["allowed_hosts"])
    append_event(event_log, event="DOWNLOAD_COMPLETE", payload={"bytes": response_meta["bytes_downloaded"]})

    # Preserve .zip suffix for archived representation.
    final_staged = staged_path.with_suffix("")
    staged_path.replace(final_staged)

    manifest = archive_staged_file(
        staged_path=final_staged,
        data_root=data_root,
        source=source,
        admission=admission,
        remote=remote,
        acquisition_metadata=response_meta,
    )
    append_event(
        event_log,
        event="RAW_ARCHIVED",
        payload={
            "snapshot_id": manifest["snapshot_id"],
            "content_id": manifest["content"]["content_id"],
            "source_version": manifest["source_version"],
        },
    )

    replica_path = None
    if replica_root is not None:
        replica_path = replicate_content(data_root=data_root, manifest=manifest, replica_root=replica_root)
        append_event(event_log, event="REDUNDANT_COPY_VERIFIED", payload={"replica_path": str(replica_path)})

    return {
        "status": "ARCHIVED",
        "snapshot": manifest,
        "replica_path": str(replica_path) if replica_path else None,
    }
