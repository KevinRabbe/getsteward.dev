from __future__ import annotations

from pathlib import Path
from typing import Any

from .errors import AdmissionError, ConfigurationError
from .util import load_json

_REQUIRED_SOURCE = {
    "source_id",
    "source_dataset_id",
    "publisher",
    "dataset_name",
    "metadata_url",
    "download_url_template",
    "allowed_hosts",
    "acquisition_method",
    "terms_url",
}

_REQUIRED_ADMISSION = {
    "admission_decision_id",
    "source_dataset_id",
    "status",
    "reviewed_on",
    "license_id",
    "terms_url",
}


def load_source(path: Path) -> dict[str, Any]:
    source = load_json(path)
    missing = sorted(_REQUIRED_SOURCE - source.keys())
    if missing:
        raise ConfigurationError(f"Source config missing fields: {', '.join(missing)}")
    if not isinstance(source["allowed_hosts"], list) or not source["allowed_hosts"]:
        raise ConfigurationError("allowed_hosts must be a non-empty list")
    return source


def load_admission(path: Path, source: dict[str, Any]) -> dict[str, Any]:
    admission = load_json(path)
    missing = sorted(_REQUIRED_ADMISSION - admission.keys())
    if missing:
        raise ConfigurationError(f"Admission config missing fields: {', '.join(missing)}")
    if admission["source_dataset_id"] != source["source_dataset_id"]:
        raise AdmissionError("Admission decision does not match source_dataset_id")
    if admission["status"] != "ALLOW":
        raise AdmissionError(f"Source is not admitted: {admission['status']}")
    return admission
