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
from .parser import normalized_path
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso
from .verify import verify_snapshot


_LEI_RE = re.compile(r"^[A-Z0-9]{18}[0-9]{2}$")
_PARSER_VERSION = "0.1.0"
_EXPECTED_DATASET_ID = "ds_gleif_rr_level2_concat"


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _text(element: ET.Element | None) -> str | None:
    if element is None:
        return None
    value = "".join(element.itertext()).strip()
    return value or None


def _direct_child(parent: ET.Element, name: str) -> ET.Element | None:
    for child in parent:
        if _local_name(child.tag) == name:
            return child
    return None


def _children(parent: ET.Element, name: str) -> list[ET.Element]:
    return [child for child in parent if _local_name(child.tag) == name]


def _required(parent: ET.Element, name: str, context: str) -> str:
    value = _text(_direct_child(parent, name))
    if value is None:
        raise ParseError(f"{context} is missing required element {name}")
    return value


def _optional(parent: ET.Element, name: str) -> str | None:
    return _text(_direct_child(parent, name))


def _required_element(parent: ET.Element, name: str, context: str) -> ET.Element:
    element = _direct_child(parent, name)
    if element is None:
        raise ParseError(f"{context} is missing required element {name}")
    return element


def _parse_header(element: ET.Element) -> dict[str, Any]:
    context = "RR Header"
    count_text = _required(element, "RecordCount", context)
    try:
        record_count = int(count_text)
    except ValueError as exc:
        raise ParseError("RR Header RecordCount must be a non-negative integer") from exc
    if record_count < 0:
        raise ParseError("RR Header RecordCount must be a non-negative integer")
    result: dict[str, Any] = {
        "content_date": _required(element, "ContentDate", context),
        "file_content": _required(element, "FileContent", context),
        "record_count": record_count,
    }
    originator = _optional(element, "Originator")
    if originator is not None:
        result["originator"] = originator
    delta_start = _optional(element, "DeltaStart")
    if delta_start is not None:
        result["delta_start"] = delta_start
    return result


def _parse_node(element: ET.Element, context: str) -> dict[str, str]:
    node_id = _required(element, "NodeID", context)
    node_type = _required(element, "NodeIDType", context)
    if node_type == "LEI" and _LEI_RE.fullmatch(node_id) is None:
        raise ParseError(f"{context} contains an invalid LEI node: {node_id!r}")
    return {"id": node_id, "type": node_type}


def _parse_periods(relationship: ET.Element, context: str) -> list[dict[str, str]]:
    container = _direct_child(relationship, "RelationshipPeriods")
    if container is None:
        return []
    result: list[dict[str, str]] = []
    for index, period in enumerate(_children(container, "RelationshipPeriod"), start=1):
        item_context = f"{context} RelationshipPeriod #{index}"
        item = {
            "start_date": _required(period, "StartDate", item_context),
            "type": _required(period, "PeriodType", item_context),
        }
        end_date = _optional(period, "EndDate")
        if end_date is not None:
            item["end_date"] = end_date
        result.append(item)
    return result


def _parse_qualifiers(relationship: ET.Element, context: str) -> list[dict[str, str]]:
    container = _direct_child(relationship, "RelationshipQualifiers")
    if container is None:
        return []
    result: list[dict[str, str]] = []
    for index, qualifier in enumerate(_children(container, "RelationshipQualifier"), start=1):
        item_context = f"{context} RelationshipQualifier #{index}"
        result.append(
            {
                "dimension": _required(qualifier, "QualifierDimension", item_context),
                "category": _required(qualifier, "QualifierCategory", item_context),
            }
        )
    return result


def _parse_quantifiers(relationship: ET.Element, context: str) -> list[dict[str, str]]:
    container = _direct_child(relationship, "RelationshipQuantifiers")
    if container is None:
        return []
    result: list[dict[str, str]] = []
    for index, quantifier in enumerate(_children(container, "RelationshipQuantifier"), start=1):
        item_context = f"{context} RelationshipQuantifier #{index}"
        item = {
            "measurement_method": _required(quantifier, "MeasurementMethod", item_context),
            "amount": _required(quantifier, "QuantifierAmount", item_context),
        }
        units = _optional(quantifier, "QuantifierUnits")
        if units is not None:
            item["units"] = units
        result.append(item)
    return result


def _parse_registration(element: ET.Element, context: str) -> dict[str, Any]:
    result: dict[str, Any] = {
        "initial_registration_date": _required(element, "InitialRegistrationDate", context),
        "last_update_date": _required(element, "LastUpdateDate", context),
        "registration_status": _required(element, "RegistrationStatus", context),
        "managing_lou": _required(element, "ManagingLOU", context),
        "validation_sources": _required(element, "ValidationSources", context),
        "validation_documents": _required(element, "ValidationDocuments", context),
    }
    next_renewal = _optional(element, "NextRenewalDate")
    if next_renewal is not None:
        result["next_renewal_date"] = next_renewal
    validation_reference = _optional(element, "ValidationReference")
    if validation_reference is not None:
        result["validation_reference"] = validation_reference
    return result


def _parse_record(element: ET.Element, ordinal: int) -> dict[str, Any]:
    context = f"RelationshipRecord #{ordinal}"
    relationship = _required_element(element, "Relationship", context)
    registration = _required_element(element, "Registration", context)
    result = {
        "start_node": _parse_node(_required_element(relationship, "StartNode", context), f"{context} StartNode"),
        "end_node": _parse_node(_required_element(relationship, "EndNode", context), f"{context} EndNode"),
        "relationship_type": _required(relationship, "RelationshipType", context),
        "relationship_status": _required(relationship, "RelationshipStatus", context),
        "relationship_periods": _parse_periods(relationship, context),
        "relationship_qualifiers": _parse_qualifiers(relationship, context),
        "relationship_quantifiers": _parse_quantifiers(relationship, context),
        "registration": _parse_registration(registration, f"{context} Registration"),
    }
    return result


def validate_relationship_record(record: Any, ordinal: int) -> dict[str, Any]:
    if not isinstance(record, dict):
        raise ParseError(f"Normalized relationship #{ordinal} must be a JSON object")
    for key in (
        "start_node",
        "end_node",
        "relationship_type",
        "relationship_status",
        "relationship_periods",
        "relationship_qualifiers",
        "relationship_quantifiers",
        "registration",
    ):
        if key not in record:
            raise ParseError(f"Normalized relationship #{ordinal} is missing {key}")
    for key in ("start_node", "end_node"):
        node = record[key]
        if not isinstance(node, dict) or not isinstance(node.get("id"), str) or not isinstance(node.get("type"), str):
            raise ParseError(f"Normalized relationship #{ordinal} has invalid {key}")
        if node["type"] == "LEI" and _LEI_RE.fullmatch(node["id"]) is None:
            raise ParseError(f"Normalized relationship #{ordinal} has invalid LEI in {key}")
    if not isinstance(record["registration"], dict):
        raise ParseError(f"Normalized relationship #{ordinal} has invalid registration")
    return record


def _parse_xml_member(xml_stream: BinaryIO, records_path: Path) -> tuple[dict[str, Any], dict[str, Any]]:
    header: dict[str, Any] | None = None
    root: ET.Element | None = None
    stack: list[ET.Element] = []
    record_count = 0
    relationship_types: Counter[str] = Counter()
    relationship_statuses: Counter[str] = Counter()
    registration_statuses: Counter[str] = Counter()
    start_node_types: Counter[str] = Counter()
    end_node_types: Counter[str] = Counter()

    try:
        context = ET.iterparse(xml_stream, events=("start", "end"))
        with records_path.open("w", encoding="utf-8", newline="\n") as output:
            for event, element in context:
                if event == "start":
                    if root is None:
                        root = element
                        if _local_name(element.tag) != "RelationshipData":
                            raise ParseError(f"Expected RelationshipData root, got {_local_name(element.tag)}")
                    stack.append(element)
                    continue

                name = _local_name(element.tag)
                if name == "Header":
                    header = _parse_header(element)
                elif name == "RelationshipRecord":
                    record_count += 1
                    normalized = _parse_record(element, record_count)
                    output.write(json.dumps(normalized, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
                    output.write("\n")
                    relationship_types[normalized["relationship_type"]] += 1
                    relationship_statuses[normalized["relationship_status"]] += 1
                    registration_statuses[normalized["registration"]["registration_status"]] += 1
                    start_node_types[normalized["start_node"]["type"]] += 1
                    end_node_types[normalized["end_node"]["type"]] += 1
                    if len(stack) >= 2:
                        stack[-2].remove(element)
                    element.clear()
                stack.pop()
            output.flush()
            os.fsync(output.fileno())
    except ET.ParseError as exc:
        raise ParseError(f"GLEIF RR-CDF XML is not well-formed: {exc}") from exc

    if root is None:
        raise ParseError("GLEIF RR-CDF XML is empty")
    if header is None:
        raise ParseError("GLEIF RR-CDF XML is missing Header")
    if header["record_count"] != record_count:
        raise ParseError(
            "GLEIF RR-CDF RecordCount does not match parsed records: "
            f"declared {header['record_count']}, parsed {record_count}"
        )

    return header, {
        "quality_report_version": 1,
        "status": "PASS",
        "records_seen": record_count,
        "records_written": record_count,
        "invalid_records": 0,
        "relationship_type_counts": dict(sorted(relationship_types.items())),
        "relationship_status_counts": dict(sorted(relationship_statuses.items())),
        "registration_status_counts": dict(sorted(registration_statuses.items())),
        "start_node_type_counts": dict(sorted(start_node_types.items())),
        "end_node_type_counts": dict(sorted(end_node_types.items())),
    }


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    checksum_path = path.with_name(path.name + ".sha256")
    checksum_path.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(checksum_path)
    return digest


def _verify_checksum(path: Path) -> str:
    checksum_path = path.with_name(path.name + ".sha256")
    if not path.exists() or not checksum_path.exists():
        raise ParseError(f"Missing normalized relationship artifact or checksum: {path}")
    expected = checksum_path.read_text(encoding="ascii").split()[0]
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ParseError(f"Checksum mismatch for normalized relationship artifact: {path}")
    return actual


def verify_relationship_artifact(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    manifest = load_json(snapshot_dir / "manifest.json")
    source_dataset_id = str(manifest["source_dataset_id"])
    if source_dataset_id != _EXPECTED_DATASET_ID:
        raise ParseError(f"Snapshot {snapshot_id} is not the GLEIF RR-CDF dataset")
    artifact_dir = normalized_path(data_root, snapshot_id, source_dataset_id, output_root)
    artifact_path = artifact_dir / "artifact.json"
    artifact_hash = _verify_checksum(artifact_path)
    artifact = load_json(artifact_path)
    if artifact.get("source_snapshot_id") != snapshot_id or artifact.get("artifact_type") != "gleif_rr_level2_normalized":
        raise ParseError("Normalized relationship artifact identity mismatch")

    records_path = artifact_dir / str(artifact["records_file"])
    records_hash = _verify_checksum(records_path)
    records_size = records_path.stat().st_size
    if records_hash != artifact.get("records_sha256") or records_size != artifact.get("records_size_bytes"):
        raise ParseError("Normalized relationship records do not match artifact manifest")

    quality_path = artifact_dir / str(artifact["quality_report_file"])
    quality_hash = _verify_checksum(quality_path)
    quality = load_json(quality_path)
    if quality_hash != artifact.get("quality_report_sha256"):
        raise ParseError("Normalized relationship quality report hash mismatch")

    count = 0
    with records_path.open("r", encoding="utf-8") as source:
        for ordinal, line in enumerate(source, start=1):
            if not line.strip():
                raise ParseError(f"Normalized relationship records contain empty line #{ordinal}")
            try:
                record = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ParseError(f"Normalized relationship #{ordinal} is not valid JSON") from exc
            validate_relationship_record(record, ordinal)
            count += 1
    if count != artifact.get("record_count") or count != quality.get("records_written"):
        raise ParseError("Normalized relationship record count mismatch")
    declared = manifest.get("declared_record_count")
    if declared is not None and int(declared) != count:
        raise ParseError("RR source metadata record count does not match normalized records")

    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "artifact_dir": str(artifact_dir),
        "records_path": str(records_path),
        "record_count": count,
        "artifact": {**artifact, "artifact_sha256": artifact_hash},
        "quality": quality,
    }


def parse_relationship_snapshot(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    verification = verify_snapshot(data_root, snapshot_id)
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    manifest = load_json(snapshot_dir / "manifest.json")
    source_dataset_id = str(manifest["source_dataset_id"])
    if source_dataset_id != _EXPECTED_DATASET_ID:
        raise ParseError(f"Snapshot {snapshot_id} is not the GLEIF RR-CDF dataset")
    final_dir = normalized_path(data_root, snapshot_id, source_dataset_id, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log("RELATIONSHIP_NORMALIZATION_STARTED", {"snapshot_id": snapshot_id})
    if final_dir.exists():
        verified = verify_relationship_artifact(data_root, snapshot_id, output_root=output_root)
        log("RELATIONSHIP_NORMALIZATION_NO_CHANGE", {"snapshot_id": snapshot_id})
        return {"status": "NO_CHANGE", "snapshot_id": snapshot_id, "artifact": verified["artifact"]}

    archive_path = data_root / manifest["content"]["archive_path"]
    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    try:
        records_path = temporary_dir / "records.jsonl"
        try:
            with zipfile.ZipFile(archive_path, "r") as archive:
                xml_members = [
                    info for info in archive.infolist()
                    if not info.is_dir() and info.filename.lower().endswith(".xml")
                ]
                if len(xml_members) != 1:
                    names = ", ".join(info.filename for info in xml_members) or "none"
                    raise ParseError(f"Expected exactly one XML member in GLEIF RR ZIP; found {names}")
                xml_member = xml_members[0]
                with archive.open(xml_member, "r") as xml_stream:
                    header, quality = _parse_xml_member(xml_stream, records_path)
        except zipfile.BadZipFile as exc:
            raise ParseError(f"RR raw snapshot is not a valid ZIP archive: {archive_path}") from exc

        declared = manifest.get("declared_record_count")
        if declared is not None and int(declared) != header["record_count"]:
            raise ParseError("RR source metadata record count does not match CDF header")

        records_hash, records_size = sha256_file(records_path)
        make_read_only(records_path)
        _write_checksum(records_path)
        quality.update(
            {
                "snapshot_id": snapshot_id,
                "source_record_count": header["record_count"],
                "declared_metadata_record_count": int(declared) if declared is not None else None,
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
            "artifact_type": "gleif_rr_level2_normalized",
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
        artifact_hash = _write_checksum(artifact_path)
        temporary_dir.rename(final_dir)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        log("RELATIONSHIP_NORMALIZATION_FAILED", {"snapshot_id": snapshot_id})
        raise

    log(
        "RELATIONSHIP_NORMALIZATION_COMPLETED",
        {"snapshot_id": snapshot_id, "record_count": quality["records_written"]},
    )
    return {
        "status": "PARSED",
        "snapshot_id": snapshot_id,
        "artifact": {**artifact, "artifact_sha256": artifact_hash},
        "quality": quality,
        "verification": verification,
    }
