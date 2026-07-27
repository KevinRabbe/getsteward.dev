from __future__ import annotations

from datetime import date
from typing import Any

from .errors import ProductError


def source_publication_date(snapshot: dict[str, Any]) -> str:
    acquisition = snapshot.get("acquisition_response")
    if isinstance(acquisition, dict):
        value = acquisition.get("publication_date")
        if isinstance(value, str):
            try:
                return date.fromisoformat(value).isoformat()
            except ValueError as exc:
                raise ProductError(
                    f"Snapshot publication_date is not an ISO date: {value!r}"
                ) from exc

    source_version = snapshot.get("source_version")
    if isinstance(source_version, str):
        try:
            return date.fromisoformat(source_version).isoformat()
        except ValueError:
            pass

    raise ProductError(
        "Snapshot has no trustworthy chronological publication date; "
        "version labels are identity and are not used as lexical ordering."
    )


def source_order_key(snapshot: dict[str, Any]) -> tuple[str, str]:
    publication_date = source_publication_date(snapshot)
    source_version = snapshot.get("source_version")
    return publication_date, str(source_version or "")


def analytics_order_key(profile: dict[str, Any]) -> tuple[str, str]:
    publication_date = profile.get("source_publication_date")
    if not isinstance(publication_date, str):
        raise ProductError("Analytics profile is missing source_publication_date")
    try:
        normalized_date = date.fromisoformat(publication_date).isoformat()
    except ValueError as exc:
        raise ProductError(
            f"Analytics source_publication_date is not an ISO date: {publication_date!r}"
        ) from exc
    return normalized_date, str(profile.get("source_version") or "")
