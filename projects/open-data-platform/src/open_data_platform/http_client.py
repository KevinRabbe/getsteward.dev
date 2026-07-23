from __future__ import annotations

import json
import os
import re
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import urlparse

from .errors import AcquisitionError

_USER_AGENT = "OpenDataPlatform-v0.1 (+local archival research)"
_DATE_RE = re.compile(r"(?<!\d)(20\d{2})[-/]?(\d{2})[-/]?(\d{2})(?!\d)")


class _AllowlistedRedirectHandler(urllib.request.HTTPRedirectHandler):
    def __init__(self, allowed_hosts: Iterable[str]):
        super().__init__()
        self.allowed_hosts = tuple(allowed_hosts)

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_https_url(newurl, self.allowed_hosts)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def _opener(allowed_hosts: Iterable[str]) -> urllib.request.OpenerDirector:
    return urllib.request.build_opener(_AllowlistedRedirectHandler(allowed_hosts))


def validate_https_url(url: str, allowed_hosts: Iterable[str]) -> None:
    parsed = urlparse(url)
    if parsed.scheme != "https":
        raise AcquisitionError(f"Only HTTPS acquisition is allowed: {url}")
    if parsed.hostname not in set(allowed_hosts):
        raise AcquisitionError(f"Host is not allowlisted: {parsed.hostname}")


def get_json(url: str, allowed_hosts: Iterable[str], timeout: int = 60) -> Any:
    validate_https_url(url, allowed_hosts)
    req = urllib.request.Request(url, headers={"User-Agent": _USER_AGENT, "Accept": "application/json"})
    try:
        with _opener(allowed_hosts).open(req, timeout=timeout) as response:
            raw = response.read()
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise AcquisitionError(f"Failed to retrieve metadata: {exc}") from exc
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AcquisitionError("Metadata endpoint did not return valid UTF-8 JSON") from exc


def _walk(value: Any):
    if isinstance(value, dict):
        for key, child in value.items():
            yield key, child
            yield from _walk(child)
    elif isinstance(value, list):
        for child in value:
            yield "", child
            yield from _walk(child)


def find_download_url(metadata: Any, allowed_hosts: Iterable[str]) -> str | None:
    allowed = set(allowed_hosts)
    candidates: list[str] = []
    for key, value in _walk(metadata):
        if not isinstance(value, str):
            continue
        if not value.startswith("https://"):
            continue
        parsed = urlparse(value)
        if parsed.hostname not in allowed:
            continue
        lowered_key = key.lower()
        lowered_value = value.lower()
        if "zip" in lowered_key or lowered_value.endswith("/zip") or ".zip" in lowered_value:
            candidates.append(value)
    return candidates[0] if candidates else None


def find_publication_date(metadata: Any) -> str | None:
    preferred: list[str] = []
    fallback: list[str] = []
    for key, value in _walk(metadata):
        if not isinstance(value, str):
            continue
        match = _DATE_RE.search(value)
        if not match:
            continue
        iso = f"{match.group(1)}-{match.group(2)}-{match.group(3)}"
        if any(token in key.lower() for token in ("publish", "date", "content", "created")):
            preferred.append(iso)
        else:
            fallback.append(iso)
    return preferred[0] if preferred else (fallback[0] if fallback else None)


def find_scalar(metadata: Any, key_tokens: tuple[str, ...]) -> Any | None:
    for key, value in _walk(metadata):
        if any(token in key.lower() for token in key_tokens) and not isinstance(value, (dict, list)):
            return value
    return None


def stream_download(url: str, destination: Path, allowed_hosts: Iterable[str], timeout: int = 120) -> dict[str, Any]:
    validate_https_url(url, allowed_hosts)
    destination.parent.mkdir(parents=True, exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": _USER_AGENT, "Accept": "application/zip,application/octet-stream"})
    try:
        with _opener(allowed_hosts).open(req, timeout=timeout) as response, destination.open("wb") as out:
            total = 0
            content_type = response.headers.get("Content-Type")
            etag = response.headers.get("ETag")
            last_modified = response.headers.get("Last-Modified")
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                out.write(chunk)
                total += len(chunk)
            out.flush()
            os.fsync(out.fileno())
        return {
            "bytes_downloaded": total,
            "content_type": content_type,
            "etag": etag,
            "last_modified": last_modified,
        }
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        try:
            destination.unlink(missing_ok=True)
        except OSError:
            pass
        raise AcquisitionError(f"Download failed: {exc}") from exc
