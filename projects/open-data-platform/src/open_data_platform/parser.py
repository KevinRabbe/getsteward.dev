from __future__ import annotations

import json
import os
import re
import shutil
import uuid
import zipfile
from collections import Counter
from pathlib import Path
from typing import Any, BinaryIO
from xml.etree import ElementTree as ET

from .errors import ParseError
from .events import append_event
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso
from .verify import verify_snapshot

_XML_LANG = "{http://www.w3.org/XML/1998/namespace}lang"
_LEI_RE = re.compile(r"^[A-Z0-9]{18}[0-9]{2}$")
_PARSER_VERSION = "0.2.0"
_REQUIRED_NORMALIZED_FIELDS = {
    "lei",
    "legal_name",
    "legal_address",
    "headquarters_address",
    "entity_status",
    "registration",
}


def normalized_path(
    data_root: Path,
    snapshot_id: str,
    source_dataset_id: str,
    output_root: Path | None = None,
) -> Path:
    root = output_root if output_root is not None else data_root / "normalized"
    return root / source_dataset_id / snapshot_id


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _text(element: ET.Element | None) -> str | None:
    if element is None:
        return None
    value = "".join(element.itertext()).strip()
    return value or None


def _language(element: ET.Element) -> str | None:
    return element.attrib.get(_XML_LANG) or element.attrib.get("lang")


def _direct_child(parent: ET.Element, name: str) -> ET.Element | None:
    for child in parent:
        if _local_name(child.tag) == name:
            return child
    return None


def _descendant(parent: ET.Element, name: str) -> ET.Element | None:
    for child in parent.iter():
        if child is not parent and _local_name(child.tag) == name:
            return child
    return None


def _required(parent: ET.Element, name: str, context: str) -> str:
    value = _text(_direct_child(parent, name))
    if value is None:
        raise ParseError(f"{context} is missing required element {name}")
    return value


def _optional(parent: ET.Element, name: str) -> str | None:
    return _text(_direct_child(parent, name))


def _required_descendant(parent: ET.Element, name: str, context: str) -> ET.Element:
    element = _descendant(parent, name)
    if element is None:
        raise ParseError(f"{context} is missing required element {name}")
    return element


def _parse_header(element: ET.Element) -> dict[str, Any]:
    context = "LEIHeader"
    record_count_text = _required(element, "RecordCount", context)
    try:
        record_count = int(record_count_text)
    except ValueError as exc:
        raise ParseError("LEIHeader RecordCount must be a non-negative integer") from exc
    if record_count < 0:
        raise ParseError("LEIHeader RecordCount must be a non-negative integer")

    return {
        "content_date": _required(element, "ContentDate", context),
        "file_content": _required(element, "FileContent", context),
        "record_count": record_count,
    }


def _parse_address(element: ET.Element, context: str) -> dict[str, Any]:
    address: dict[str, Any] = {
        "first_address_line": _required(element, "FirstAddressLine", context),
        "city": _required(element, "City", context),
        "country": _required(element, "Country", context),
        "additional_address_lines": [],
    }
    language = _language(element)
    if language is not None:
        address["language"] = language

    for output_name, source_name in (
        ("address_number", "AddressNumber"),
        ("address_number_within_building", "AddressNumberWithinBuilding"),
        ("mail_routing", "MailRouting"),
        ("region", "Region"),
        ("postal_code", "PostalCode"),
    ):
        value = _optional(element, source_name)
        if value is not None:
            address[output_name] = value

    for child in element:
        if _local_name(child.tag) == "AdditionalAddressLine":
            value = _text(child)
            if value is not None:
                address["additional_address_lines"].append(value)
    return address


def _parse_registration_authority(entity: ET.Element) -> dict[str, str] | None:
    element = _direct_child(entity, "RegistrationAuthority")
    if element is None:
        return None
    values = {
        "id": _optional(element, "RegistrationAuthorityID"),
        "other_id": _optional(element, "OtherRegistrationAuthorityID"),
        "entity_id": _optional(element, "RegistrationAuthorityEntityID"),
    }
    return {key: value for key, value in values.items() if value is not None}


def _parse_legal_form(entity: ET.Element) -> dict[str, str] | None:
    element = _direct_child(entity, "LegalForm")
    if element is None:
        return None
    values = {
        "entity_legal_form_code": _optional(element, "EntityLegalFormCode"),
        "other_legal_form": _optional(element, "OtherLegalForm"),
    }
    return {key: value for key, value in values.items() if value is not None}


def _parse_record(element: ET.Element, ordinal: int) -> dict[str, Any]:
    context = f"LEIRecord #{ordinal}"
    lei = _required(element, "LEI", context)
    if _LEI_RE.fullmatch(lei) is None:
        raise ParseError(f"{context} contains an invalid LEI: {lei!r}")

    entity = _required_descendant(element, "Entity", context)
    legal_name_element = _required_descendant(entity, "LegalName", context)
    legal_name = _text(legal_name_element)
    if legal_name is None:
        raise ParseError(f"{context} LegalName must not be empty")

    legal_address = _parse_address(
        _required_descendant(entity, "LegalAddress", context),
        f"{context} LegalAddress",
    )
    headquarters_address = _parse_address(
        _required_descendant(entity, "HeadquartersAddress", context),
        f"{context} HeadquartersAddress",
    )
    registration = _required_descendant(element, "Registration", context)

    normalized: dict[str, Any] = {
        "lei": lei,
        "legal_name": legal_name,
        "legal_address": legal_address,
        "headquarters_address": headquarters_address,
        "entity_status": _required(entity, "EntityStatus", context),
        "registration": {
            "initial_registration_date": _required(registration, "InitialRegistrationDate", context),
            "last_update_date": _required(registration, "LastUpdateDate", context),
            "registration_status": _required(registration, "RegistrationStatus", context),
            "next_renewal_date": _required(registration, "NextRenewalDate", context),
            "managing_lou": _required(registration, "ManagingLOU", context),
        },
    }

    language = _language(legal_name_element)
    if language is not None:
        normalized["legal_name_language"] = language

    for output_name, source_name in (
        ("legal_jurisdiction", "LegalJurisdiction"),
        ("entity_category", "EntityCategory"),
        ("entity_subcategory", "EntitySubCategory"),
        ("entity_creation_date", "EntityCreationDate"),
    ):
        value = _optional(entity, source_name)
        if value is not None:
            normalized[output_name] = value

    legal_form = _parse_legal_form(entity)
    if legal_form:
        normalized["legal_form"] = legal_form

    registration_authority = _parse_registration_authority(entity)
    if registration_authority:
        normalized["registration_authority"] = registration_authority

    validation_sources = _optional(registration, "ValidationSources")
    if validation_sources is not None:
        normalized["registration"]["validation_sources"] = validation_sources
    return normalized


def validate_normalized_record(record: Any, ordinal: int) -> dict[str, Any]:
    if not isinstance(record, dict):
        raise ParseError(f"Normalized record #{ordinal} must be a JSON object")
    missing = sorted(_REQUIRED_NORMALIZED_FIELDS - record.keys())
    if missing:
        raise ParseError(f"Normalized record #{ordinal} is missing fields: {', '.join(missing)}")
    lei = record["lei"]
    if not isinstance(lei, str) or _LEI_RE.fullmatch(lei) is None:
        raise ParseError(f"Normalized record #{ordinal} contains an invalid LEI: {lei!r}")
    if not isinstance(record["legal_name"], str) or not record["legal_name"].strip():
        raise ParseError(f"Normalized record #{ordinal} has an empty legal_name")
    if not isinstance(record["legal_address"], dict) or not isinstance(record["headquarters_address"], dict):
        raise ParseError(f"Normalized record #{ordinal} has invalid address objects")
    if not isinstance(record["registration"], dict):
        raise ParseError(f"Normalized record #{ordinal} has an invalid registration object")
    return record


def _parse_xml_member(xml_stream: BinaryIO, records_path: Path) -> tuple[dict[str, Any], dict[str, Any]]:
    record_count = 0
    status_counts: Counter[str] = Counter()
    registration_status_counts: Counter[str] = Counter()
    missing_optional_counts: Counter[str] = Counter()
    header: dict[str, Any] | None = None
    root: ET.Element | None = None
    stack: list[ET.Element] = []

    try:
        context = ET.iterparse(xml_stream, events=("start", "end"))
        with records_path.open("w", encoding="utf-8", newline="\n") as output:
            for event, element in context:
                if event == "start":
                    if root is None:
                        root = element
                        if _local_name(element.tag) != "LEIData":
                            raise ParseError(f"Expected LEIData root, got {_local_name(element.tag)}")
                    stack.append(element)
                    continue

                name = _local_name(element.tag)
                if name == "LEIHeader":
                    header = _parse_header(element)
                elif name == "LEIRecord":
                    record_count += 1
                    normalized = _parse_record(element, record_count)
                    output.write(json.dumps(normalized, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
                    output.write("\n")
                    status_counts[normalized["entity_status"]] += 1
                    registration_status_counts[normalized["registration"]["registration_status"]] += 1
                    for field in (
                        "legal_jurisdiction",
                        "entity_category",
                        "entity_subcategory",
                        "entity_creation_date",
                        "legal_form",
                        "registration_authority",
                    ):
                        if field not in normalized:
                            missing_optional_counts[field] += 1

                    if len(stack) >= 2:
                        stack[-2].remove(element)
                    element.clear()
                stack.pop()
            output.flush()
            os.fsync(output.fileno())
    except ET.ParseError as exc:
        raise ParseError(f"GLEIF XML is not well-formed: {exc}") from exc

    if root is None:
        raise ParseError("GLEIF XML is empty")
    if header is None:
        raise ParseError("GLEIF XML is missing LEIHeader")
    if header["record_count"] != record_count:
        raise ParseError(
            "GLEIF XML RecordCount does not match parsed records: "
            f"declared {header['record_count']}, parsed {record_count}"
        )

    quality = {
        "quality_report_version": 1,
        "status": "PASS",
        "records_seen": record_count,
        "records_written": record_count,
        "invalid_records": 0,
        "missing_optional_field_counts": dict(sorted(missing_optional_counts.items())),
        "entity_status_counts": dict(sorted(status_counts.items())),
        "registration_status_counts": dict(sorted(registration_status_counts.items())),
    }
    return header, quality


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    checksum_path = path.with_name(path.name + ".sha256")
    checksum_path.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(checksum_path)
    return digest


def _verify_checksum(path: Path) -> str:
    checksum_path = path.with_name(path.name + ".sha256")
    if not checksum_path.exists():
        raise ParseError(f"Missing checksum sidecar: {checksum_path}")
    expected = checksum_path.read_text(encoding="ascii").split()[0]
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ParseError(f"Checksum mismatch for normalized artifact file: {path}")
    return actual


def _load_existing_artifact(output_dir: Path, snapshot_id: str) -> dict[str, Any]:
    artifact_path = output_dir / "artifact.json"
    if not artifact_path.exists():
        raise ParseError(f"Normalized artifact directory already exists but is incomplete: {output_dir}")
    artifact = load_json(artifact_path)
    if artifact.get("source_snapshot_id") != snapshot_id:
        raise ParseError(f"Normalized artifact does not belong to snapshot {snapshot_id}")
    artifact_hash = _verify_checksum(artifact_path)

    records_path = output_dir / str(artifact["records_file"])
    records_hash, records_size = sha256_file(records_path)
    if records_hash != artifact["records_sha256"] or records_size != artifact["records_size_bytes"]:
        raise ParseError(f"Normalized records failed integrity verification: {records_path}")
    _verify_checksum(records_path)

    quality_path = output_dir / str(artifact["quality_report_file"])
    quality_hash, quality_size = sha256_file(quality_path)
    if quality_hash != artifact["quality_report_sha256"] or quality_size != artifact["quality_report_size_bytes"]:
        raise ParseError(f"Normalized quality report failed integrity verification: {quality_path}")
    _verify_checksum(quality_path)
    return {**artifact, "artifact_sha256": artifact_hash}


def verify_normalized_artifact(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    """Verify normalized sidecars and every JSONL record after publication."""
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    snapshot_manifest = load_json(snapshot_dir / "manifest.json")
    artifact_dir = normalized_path(
        data_root,
        snapshot_id,
        str(snapshot_manifest["source_dataset_id"]),
        output_root,
    )
    artifact = _load_existing_artifact(artifact_dir, snapshot_id)
    records_path = artifact_dir / str(artifact["records_file"])
    quality = load_json(artifact_dir / str(artifact["quality_report_file"]))

    records_seen = 0
    with records_path.open("r", encoding="utf-8") as records_file:
        for ordinal, line in enumerate(records_file, start=1):
            if not line.strip():
                raise ParseError(f"Normalized records contain an empty line at #{ordinal}")
            try:
                record = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ParseError(f"Normalized record #{ordinal} is not valid JSON") from exc
            validate_normalized_record(record, ordinal)
            records_seen += 1

    expected_count = artifact.get("record_count")
    if expected_count != records_seen:
        raise ParseError(
            "Normalized artifact record count mismatch: "
            f"manifest declares {expected_count}, file contains {records_seen}"
        )
    if quality.get("records_written") != records_seen:
        raise ParseError(
            "Quality report record count mismatch: "
            f"report declares {quality.get('records_written')}, file contains {records_seen}"
        )

    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "artifact_dir": str(artifact_dir),
        "records_path": str(records_path),
        "record_count": records_seen,
        "artifact": artifact,
        "quality": quality,
    }


def parse_snapshot(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    """Stream one verified GLEIF CDF ZIP into an immutable normalized artifact."""
    verification = verify_snapshot(data_root, snapshot_id)
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    manifest = load_json(snapshot_dir / "manifest.json")
    source_dataset_id = str(manifest["source_dataset_id"])
    final_dir = normalized_path(data_root, snapshot_id, source_dataset_id, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log(
        "NORMALIZATION_STARTED",
        {"snapshot_id": snapshot_id, "source_content_id": manifest["content"]["content_id"]},
    )

    try:
        if final_dir.exists():
            artifact = _load_existing_artifact(final_dir, snapshot_id)
            result = {
                "status": "NO_CHANGE",
                "snapshot_id": snapshot_id,
                "artifact": artifact,
                "verification": verification,
            }
            log("NORMALIZATION_NO_CHANGE", {"snapshot_id": snapshot_id})
            return result

        archive_path = data_root / manifest["content"]["archive_path"]
        parent_dir = final_dir.parent
        parent_dir.mkdir(parents=True, exist_ok=True)
        temporary_dir = parent_dir / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
        temporary_dir.mkdir(parents=False, exist_ok=False)

        try:
            records_path = temporary_dir / "records.jsonl"
            try:
                with zipfile.ZipFile(archive_path, "r") as archive:
                    xml_members = [
                        info
                        for info in archive.infolist()
                        if not info.is_dir() and info.filename.lower().endswith(".xml")
                    ]
                    if len(xml_members) != 1:
                        names = ", ".join(info.filename for info in xml_members) or "none"
                        raise ParseError(f"Expected exactly one XML member in GLEIF ZIP; found {names}")
                    xml_member = xml_members[0]
                    with archive.open(xml_member, "r") as xml_stream:
                        header, quality = _parse_xml_member(xml_stream, records_path)
            except zipfile.BadZipFile as exc:
                raise ParseError(f"Raw snapshot is not a valid ZIP archive: {archive_path}") from exc

            records_hash, records_size = sha256_file(records_path)
            make_read_only(records_path)
            _write_checksum(records_path)

            quality.update(
                {
                    "snapshot_id": snapshot_id,
                    "source_record_count": header["record_count"],
                    "content_date": header["content_date"],
                    "file_content": header["file_content"],
                    "generated_at": utc_now_iso(),
                }
            )
            quality_path = temporary_dir / "quality.json"
            atomic_write_json(quality_path, quality)
            quality_hash, quality_size = sha256_file(quality_path)
            make_read_only(quality_path)
            _write_checksum(quality_path)

            artifact = {
                "artifact_version": 1,
                "artifact_type": "gleif_level1_normalized",
                "parser_version": _PARSER_VERSION,
                "source_snapshot_id": snapshot_id,
                "source_dataset_id": source_dataset_id,
                "source_version": manifest["source_version"],
                "source_cdf_version": manifest.get("cdf_version"),
                "source_content_id": manifest["content"]["content_id"],
                "source_archive_path": manifest["content"]["archive_path"],
                "xml_member": xml_member.filename,
                "cdf_header": header,
                "records_file": "records.jsonl",
                "records_sha256": records_hash,
                "records_size_bytes": records_size,
                "record_count": quality["records_written"],
                "quality_report_file": "quality.json",
                "quality_report_sha256": quality_hash,
                "quality_report_size_bytes": quality_size,
                "created_at": utc_now_iso(),
            }
            artifact_path = temporary_dir / "artifact.json"
            atomic_write_json(artifact_path, artifact)
            make_read_only(artifact_path)
            artifact_checksum = _write_checksum(artifact_path)

            # The checksum is intentionally adjacent, not embedded in artifact.json.
            # Return the checksum to callers without changing the immutable file.
            final_dir.parent.mkdir(parents=True, exist_ok=True)
            temporary_dir.rename(final_dir)
        except Exception:
            shutil.rmtree(temporary_dir, ignore_errors=True)
            raise

        result = {
            "status": "PARSED",
            "snapshot_id": snapshot_id,
            "artifact": {**artifact, "artifact_sha256": artifact_checksum},
            "quality": quality,
            "verification": verification,
        }
        log(
            "NORMALIZATION_COMPLETED",
            {
                "snapshot_id": snapshot_id,
                "record_count": quality["records_written"],
                "records_sha256": records_hash,
            },
        )
        return result
    except Exception as exc:
        log("NORMALIZATION_FAILED", {"snapshot_id": snapshot_id, "error": str(exc)})
        raise
