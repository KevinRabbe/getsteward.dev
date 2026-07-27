from __future__ import annotations

from datetime import date
from typing import Any

from .errors import ProductError


def _iso_date(value: Any, *, field: str) -> str | None:
    if not isinstance(value, str):
        return None
    try:
        return date.fromisoformat(value).isoformat()
    except ValueError as exc:
        raise ProductError(f"{field} is not an ISO date: {value!r}") from exc


def source_publication_date(snapshot: dict[str, Any]) -> str:
    acquisition = snapshot.get("acquisition_response")
    if isinstance(acquisition, dict):
        publication = acquisition.get("publication_date")
        if publication is not None:
            normalized = _iso_date(publication, field="Snapshot publication_date")
            if normalized is not None:
                return normalized

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
    if isinstance(publication_date, str):
        normalized_date = _iso_date(
            publication_date,
            field="Analytics source_publication_date",
        )
        assert normalized_date is not None
        return normalized_date, str(profile.get("source_version") or "")

    # Backward compatibility for immutable Level-1/RR profiles created before
    # publication-date ordering was introduced. Their source_version is itself
    # an ISO source date. Non-date version labels (for example ROR v2.10) must
    # carry source_publication_date and are never ordered lexically.
    source_version = profile.get("source_version")
    if isinstance(source_version, str):
        try:
            normalized_date = date.fromisoformat(source_version).isoformat()
        except ValueError:
            pass
        else:
            return normalized_date, source_version

    raise ProductError(
        "Analytics profile has no trustworthy chronological publication date"
    )
