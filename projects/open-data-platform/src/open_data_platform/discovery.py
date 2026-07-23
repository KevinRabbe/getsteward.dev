from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from .errors import AcquisitionError
from .http_client import find_download_url, find_publication_date, find_scalar, get_json


@dataclass(frozen=True)
class RemoteSnapshot:
    publication_date: str
    download_url: str
    cdf_version: str | None
    record_count: int | str | None
    metadata: Any


def discover_latest(source: dict) -> RemoteSnapshot:
    metadata = get_json(source["metadata_url"], source["allowed_hosts"])
    publication_date = find_publication_date(metadata)
    if not publication_date:
        raise AcquisitionError("Could not determine publication date from GLEIF metadata")

    download_url = find_download_url(metadata, source["allowed_hosts"])
    if not download_url:
        yyyymmdd = publication_date.replace("-", "")
        download_url = source["download_url_template"].format(yyyymmdd=yyyymmdd)

    cdf_version = find_scalar(metadata, ("cdf_version", "cdfversion"))
    record_count = find_scalar(metadata, ("record_count", "recordcount", "records"))

    return RemoteSnapshot(
        publication_date=publication_date,
        download_url=download_url,
        cdf_version=str(cdf_version) if cdf_version is not None else None,
        record_count=record_count,
        metadata=metadata,
    )
