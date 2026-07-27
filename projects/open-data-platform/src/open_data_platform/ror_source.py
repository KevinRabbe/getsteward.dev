from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .archive import archive_staged_file, replicate_content
from .errors import AcquisitionError, IntegrityError
from .events import append_event
from .http_client import get_json, stream_download, validate_https_url
from .ingest import _snapshot_already_archived
from .registry import load_admission, load_source


_VERSION_RE = re.compile(r"v\d+(?:\.\d+)+(?:\.\d+)?", re.IGNORECASE)
_EXPECTED_SCHEMA = "ROR Schema 2.1"


@dataclass(frozen=True)
class RemoteRorSnapshot:
    # archive_staged_file maps this attribute to manifest.source_version.
    publication_date: str
    download_url: str
    cdf_version: str
    record_count: None
    metadata: Any
    release_date: str
    zenodo_record_id: int
    version_doi: str
    concept_doi: str
    filename: str
    source_checksum: str


def _release_version(metadata: dict[str, Any], filename: str) -> str:
    nested = metadata.get("metadata")
    if isinstance(nested, dict):
        value = nested.get("version")
        if isinstance(value, str) and _VERSION_RE.fullmatch(value.strip()):
            return value.strip()
    match = _VERSION_RE.search(filename)
    if not match:
        raise AcquisitionError("Could not determine ROR release version from Zenodo metadata")
    return match.group(0)


def discover_latest_ror(source: dict[str, Any]) -> RemoteRorSnapshot:
    metadata = get_json(source["metadata_url"], source["allowed_hosts"])
    if not isinstance(metadata, dict):
        raise AcquisitionError("Zenodo ROR metadata must be a JSON object")
    nested = metadata.get("metadata")
    if not isinstance(nested, dict):
        raise AcquisitionError("Zenodo ROR metadata is missing metadata object")
    release_date = nested.get("publication_date")
    if not isinstance(release_date, str) or not release_date:
        raise AcquisitionError("Zenodo ROR metadata is missing publication_date")

    files = metadata.get("files")
    if not isinstance(files, list):
        raise AcquisitionError("Zenodo ROR metadata is missing files")
    candidates: list[dict[str, Any]] = []
    for item in files:
        if not isinstance(item, dict):
            continue
        key = item.get("key")
        if isinstance(key, str) and key.lower().endswith(".zip") and "ror-data" in key.lower():
            candidates.append(item)
    if len(candidates) != 1:
        raise AcquisitionError(
            f"Expected exactly one ROR data ZIP in Zenodo metadata, found {len(candidates)}"
        )
    file_info = candidates[0]
    filename = str(file_info["key"])
    links = file_info.get("links")
    if not isinstance(links, dict):
        raise AcquisitionError("Zenodo ROR file is missing links")
    download_url = links.get("self") or links.get("content")
    if not isinstance(download_url, str):
        raise AcquisitionError("Zenodo ROR file is missing a download URL")
    validate_https_url(download_url, source["allowed_hosts"])

    checksum = file_info.get("checksum")
    if not isinstance(checksum, str) or not checksum.lower().startswith("md5:"):
        raise AcquisitionError("Zenodo ROR file is missing its expected MD5 checksum")
    expected_md5 = checksum.split(":", 1)[1].lower()
    if len(expected_md5) != 32 or any(ch not in "0123456789abcdef" for ch in expected_md5):
        raise AcquisitionError("Zenodo ROR file has an invalid MD5 checksum value")

    version = _release_version(metadata, filename)
    record_id = metadata.get("id")
    if not isinstance(record_id, int):
        raise AcquisitionError("Zenodo ROR metadata is missing numeric record id")
    doi = metadata.get("doi")
    concept_doi = metadata.get("conceptdoi")
    if not isinstance(doi, str) or not isinstance(concept_doi, str):
        raise AcquisitionError("Zenodo ROR metadata is missing DOI identity")

    return RemoteRorSnapshot(
        publication_date=version,
        download_url=download_url,
        cdf_version=_EXPECTED_SCHEMA,
        record_count=None,
        metadata=metadata,
        release_date=release_date,
        zenodo_record_id=record_id,
        version_doi=doi,
        concept_doi=concept_doi,
        filename=filename,
        source_checksum=f"md5:{expected_md5}",
    )


def _md5(path: Path) -> str:
    digest = hashlib.md5(usedforsecurity=False)
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ingest_latest_ror(
    *,
    source_config: Path,
    admission_config: Path,
    data_root: Path,
    replica_root: Path | None = None,
) -> dict[str, Any]:
    source = load_source(source_config)
    admission = load_admission(admission_config, source)
    event_log = data_root / "events" / "events.jsonl"
    append_event(event_log, event="SOURCE_REGISTERED", payload={"source_dataset_id": source["source_dataset_id"]})
    append_event(event_log, event="ADMITTED", payload={"admission_decision_id": admission["admission_decision_id"]})

    remote = discover_latest_ror(source)
    append_event(
        event_log,
        event="REMOTE_VERSION_DISCOVERED",
        payload={
            "source_dataset_id": source["source_dataset_id"],
            "source_version": remote.publication_date,
            "publication_date": remote.release_date,
            "zenodo_record_id": remote.zenodo_record_id,
        },
    )
    existing = _snapshot_already_archived(data_root, source["source_dataset_id"], remote.publication_date)
    if existing is not None:
        append_event(
            event_log,
            event="NO_CHANGE",
            payload={"snapshot_id": existing["snapshot_id"], "source_version": remote.publication_date},
        )
        return {"status": "NO_CHANGE", "snapshot": existing}

    staging = data_root / "staging"
    staging.mkdir(parents=True, exist_ok=True)
    staged_part = staging / f"{source['source_dataset_id']}_{remote.publication_date}.zip.part"
    append_event(event_log, event="DOWNLOAD_TO_STAGING", payload={"url": remote.download_url})
    response_meta = stream_download(remote.download_url, staged_part, source["allowed_hosts"])
    actual_md5 = _md5(staged_part)
    expected_md5 = remote.source_checksum.split(":", 1)[1]
    if actual_md5 != expected_md5:
        staged_part.unlink(missing_ok=True)
        raise IntegrityError(
            f"ROR Zenodo MD5 mismatch: expected {expected_md5}, got {actual_md5}"
        )
    append_event(
        event_log,
        event="DOWNLOAD_COMPLETE",
        payload={"bytes": response_meta["bytes_downloaded"], "source_md5_verified": True},
    )

    final_staged = staged_part.with_suffix("")
    staged_part.replace(final_staged)
    response_meta.update(
        {
            "source_md5": actual_md5,
            "source_md5_verified": True,
            "zenodo_record_id": remote.zenodo_record_id,
            "version_doi": remote.version_doi,
            "concept_doi": remote.concept_doi,
            "publication_date": remote.release_date,
            "filename": remote.filename,
        }
    )
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
