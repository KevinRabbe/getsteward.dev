from __future__ import annotations

import io
import json
import os
import shutil
import uuid
import zipfile
from collections import Counter
from pathlib import Path
from typing import Any, Iterator, TextIO

from .errors import ParseError
from .events import append_event
from .parser import normalized_path
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso
from .verify import verify_snapshot


_PARSER_VERSION = "0.1.0"
_EXPECTED_DATASET_ID = "ds_ror_organizations"
_EXPECTED_SCHEMA = "ROR Schema 2.1"
_EXPECTED_FIELDS = {
    "id",
    "admin",
    "domains",
    "established",
    "external_ids",
    "links",
    "locations",
    "names",
    "relationships",
    "status",
    "types",
}
_ALLOWED_STATUS = {"active", "inactive", "withdrawn"}
_ALLOWED_RELATIONSHIP_TYPES = {"related", "parent", "child", "predecessor", "successor"}
_EXCLUDED_SOURCE_FIELDS = ["locations"]


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def _verify_checksum(path: Path) -> str:
    sidecar = path.with_name(path.name + ".sha256")
    if not path.exists() or not sidecar.exists():
        raise ParseError(f"Missing normalized ROR artifact or checksum: {path}")
    try:
        expected = sidecar.read_text(encoding="ascii").split()[0]
    except (OSError, IndexError) as exc:
        raise ParseError(f"Invalid checksum sidecar: {sidecar}") from exc
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ParseError(f"Checksum mismatch for normalized ROR artifact: {path}")
    return actual


def _iter_json_array(stream: TextIO, *, chunk_size: int = 1024 * 1024) -> Iterator[Any]:
    decoder = json.JSONDecoder()
    buffer = ""
    position = 0
    eof = False
    started = False

    def compact_and_read() -> None:
        nonlocal buffer, position, eof
        if position:
            buffer = buffer[position:]
            position = 0
        chunk = stream.read(chunk_size)
        if chunk == "":
            eof = True
        else:
            buffer += chunk

    while True:
        while position >= len(buffer) and not eof:
            compact_and_read()
        while position < len(buffer) and buffer[position].isspace():
            position += 1
        if not started:
            if position >= len(buffer):
                if eof:
                    raise ParseError("ROR JSON dump is empty")
                compact_and_read()
                continue
            if buffer[position] != "[":
                raise ParseError("ROR JSON dump must be a top-level array")
            position += 1
            started = True
            continue

        while True:
            while position < len(buffer) and buffer[position].isspace():
                position += 1
            if position >= len(buffer):
                if eof:
                    raise ParseError("ROR JSON dump ended before closing array")
                compact_and_read()
                continue
            if buffer[position] == ",":
                position += 1
                continue
            if buffer[position] == "]":
                position += 1
                while True:
                    while position < len(buffer) and buffer[position].isspace():
                        position += 1
                    if position < len(buffer):
                        raise ParseError("ROR JSON dump contains trailing content after top-level array")
                    if eof:
                        return
                    compact_and_read()
                
            break

        while True:
            try:
                value, end = decoder.raw_decode(buffer, position)
                position = end
                yield value
                break
            except json.JSONDecodeError as exc:
                if eof:
                    raise ParseError(f"ROR JSON record is malformed near character {exc.pos}") from exc
                compact_and_read()

        if position > chunk_size:
            buffer = buffer[position:]
            position = 0


def _string_list(value: Any, field: str, ordinal: int) -> list[str]:
    if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
        raise ParseError(f"ROR record #{ordinal} has invalid {field}")
    return value


def _object_list(value: Any, field: str, ordinal: int) -> list[dict[str, Any]]:
    if not isinstance(value, list) or not all(isinstance(item, dict) for item in value):
        raise ParseError(f"ROR record #{ordinal} has invalid {field}")
    return value


def _ror_display_name(names: list[dict[str, Any]], ordinal: int) -> str:
    values: list[str] = []
    for item in names:
        value = item.get("value")
        types = item.get("types")
        if not isinstance(value, str) or not value:
            raise ParseError(f"ROR record #{ordinal} contains a name without a value")
        if not isinstance(types, list) or not all(isinstance(entry, str) for entry in types):
            raise ParseError(f"ROR record #{ordinal} contains invalid name types")
        lang = item.get("lang")
        if lang is not None and not isinstance(lang, str):
            raise ParseError(f"ROR record #{ordinal} contains invalid name language")
        if "ror_display" in types:
            values.append(value)
    if len(values) != 1:
        raise ParseError(
            f"ROR record #{ordinal} must contain exactly one ror_display name; found {len(values)}"
        )
    return values[0]


def normalize_ror_record(record: Any, ordinal: int) -> dict[str, Any]:
    if not isinstance(record, dict):
        raise ParseError(f"ROR record #{ordinal} must be a JSON object")
    fields = set(record)
    missing = sorted(_EXPECTED_FIELDS - fields)
    unknown = sorted(fields - _EXPECTED_FIELDS)
    if missing:
        raise ParseError(f"ROR record #{ordinal} is missing schema fields: {', '.join(missing)}")
    if unknown:
        raise ParseError(
            f"ROR record #{ordinal} contains unknown schema fields: {', '.join(unknown)}"
        )

    ror_id = record["id"]
    if not isinstance(ror_id, str) or not ror_id.startswith("https://ror.org/") or len(ror_id.rsplit("/", 1)[-1]) != 9:
        raise ParseError(f"ROR record #{ordinal} has invalid id: {ror_id!r}")
    status = record["status"]
    if status not in _ALLOWED_STATUS:
        raise ParseError(f"ROR record #{ordinal} has unknown status: {status!r}")
    established = record["established"]
    if established is not None and (not isinstance(established, int) or established < 0 or established > 9999):
        raise ParseError(f"ROR record #{ordinal} has invalid established year")

    names = _object_list(record["names"], "names", ordinal)
    relationships = _object_list(record["relationships"], "relationships", ordinal)
    for relationship in relationships:
        relationship_id = relationship.get("id")
        relationship_type = relationship.get("type")
        label = relationship.get("label")
        if not isinstance(relationship_id, str) or not relationship_id.startswith("https://ror.org/"):
            raise ParseError(f"ROR record #{ordinal} contains invalid relationship id")
        if relationship_type not in _ALLOWED_RELATIONSHIP_TYPES:
            raise ParseError(f"ROR record #{ordinal} contains unknown relationship type: {relationship_type!r}")
        if not isinstance(label, str) or not label:
            raise ParseError(f"ROR record #{ordinal} contains invalid relationship label")

    admin = record["admin"]
    if not isinstance(admin, dict):
        raise ParseError(f"ROR record #{ordinal} has invalid admin metadata")
    locations = record["locations"]
    if not isinstance(locations, list):
        raise ParseError(f"ROR record #{ordinal} has invalid locations field")

    return {
        "ror_id": ror_id,
        "display_name": _ror_display_name(names, ordinal),
        "status": status,
        "established": established,
        "types": _string_list(record["types"], "types", ordinal),
        "domains": _string_list(record["domains"], "domains", ordinal),
        "names": names,
        "external_ids": _object_list(record["external_ids"], "external_ids", ordinal),
        "links": _object_list(record["links"], "links", ordinal),
        "relationships": relationships,
        "admin": admin,
    }


def validate_normalized_ror_record(record: Any, ordinal: int) -> dict[str, Any]:
    if not isinstance(record, dict):
        raise ParseError(f"Normalized ROR record #{ordinal} must be an object")
    expected = {
        "ror_id",
        "display_name",
        "status",
        "established",
        "types",
        "domains",
        "names",
        "external_ids",
        "links",
        "relationships",
        "admin",
    }
    if set(record) != expected:
        raise ParseError(f"Normalized ROR record #{ordinal} schema mismatch")
    if not isinstance(record["ror_id"], str) or not record["ror_id"].startswith("https://ror.org/"):
        raise ParseError(f"Normalized ROR record #{ordinal} has invalid ror_id")
    if not isinstance(record["display_name"], str) or not record["display_name"]:
        raise ParseError(f"Normalized ROR record #{ordinal} has invalid display_name")
    if record["status"] not in _ALLOWED_STATUS:
        raise ParseError(f"Normalized ROR record #{ordinal} has invalid status")
    return record


def _parse_json_member(stream: TextIO, records_path: Path) -> dict[str, Any]:
    status_counts: Counter[str] = Counter()
    type_counts: Counter[str] = Counter()
    relationship_type_counts: Counter[str] = Counter()
    records_with_locations = 0
    count = 0
    with records_path.open("w", encoding="utf-8", newline="\n") as output:
        for count, source_record in enumerate(_iter_json_array(stream), start=1):
            if isinstance(source_record, dict) and source_record.get("locations"):
                records_with_locations += 1
            normalized = normalize_ror_record(source_record, count)
            output.write(
                json.dumps(normalized, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
            )
            output.write("\n")
            status_counts[normalized["status"]] += 1
            type_counts.update(normalized["types"])
            relationship_type_counts.update(
                relationship["type"] for relationship in normalized["relationships"]
            )
        output.flush()
        os.fsync(output.fileno())
    return {
        "quality_report_version": 1,
        "status": "PASS",
        "records_seen": count,
        "records_written": count,
        "invalid_records": 0,
        "status_counts": dict(sorted(status_counts.items())),
        "organization_type_counts": dict(sorted(type_counts.items())),
        "relationship_type_counts": dict(sorted(relationship_type_counts.items())),
        "records_with_excluded_locations": records_with_locations,
        "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
    }


def verify_ror_artifact(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    snapshot = load_json(data_root / "archive" / "snapshots" / snapshot_id / "manifest.json")
    if snapshot.get("source_dataset_id") != _EXPECTED_DATASET_ID:
        raise ParseError(f"Snapshot {snapshot_id} is not the ROR organizations dataset")
    artifact_dir = normalized_path(data_root, snapshot_id, _EXPECTED_DATASET_ID, output_root)
    artifact_path = artifact_dir / "artifact.json"
    artifact_hash = _verify_checksum(artifact_path)
    artifact = load_json(artifact_path)
    if artifact.get("artifact_type") != "ror_organizations_normalized" or artifact.get("source_snapshot_id") != snapshot_id:
        raise ParseError("Normalized ROR artifact identity mismatch")
    if artifact.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ParseError("Normalized ROR artifact lost its field-exclusion boundary")

    records_path = artifact_dir / str(artifact["records_file"])
    records_hash = _verify_checksum(records_path)
    records_size = records_path.stat().st_size
    if records_hash != artifact.get("records_sha256") or records_size != artifact.get("records_size_bytes"):
        raise ParseError("Normalized ROR records do not match artifact manifest")
    quality_path = artifact_dir / str(artifact["quality_report_file"])
    quality_hash = _verify_checksum(quality_path)
    quality = load_json(quality_path)
    if quality_hash != artifact.get("quality_report_sha256"):
        raise ParseError("Normalized ROR quality hash mismatch")
    if quality.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ParseError("ROR quality report lost its excluded-field declaration")

    count = 0
    with records_path.open("r", encoding="utf-8") as source:
        for ordinal, line in enumerate(source, start=1):
            if not line.strip():
                raise ParseError(f"Normalized ROR records contain empty line #{ordinal}")
            try:
                record = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ParseError(f"Normalized ROR record #{ordinal} is invalid JSON") from exc
            validate_normalized_ror_record(record, ordinal)
            if "locations" in record:
                raise ParseError("Excluded ROR locations field leaked into normalized product")
            count += 1
    if count != artifact.get("record_count") or count != quality.get("records_written"):
        raise ParseError("Normalized ROR record count mismatch")
    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "artifact_dir": str(artifact_dir),
        "records_path": str(records_path),
        "record_count": count,
        "artifact": {**artifact, "artifact_sha256": artifact_hash},
        "quality": quality,
    }


def parse_ror_snapshot(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    verification = verify_snapshot(data_root, snapshot_id)
    snapshot_dir = data_root / "archive" / "snapshots" / snapshot_id
    manifest = load_json(snapshot_dir / "manifest.json")
    if manifest.get("source_dataset_id") != _EXPECTED_DATASET_ID:
        raise ParseError(f"Snapshot {snapshot_id} is not the ROR organizations dataset")
    if manifest.get("cdf_version") != _EXPECTED_SCHEMA:
        raise ParseError(
            f"ROR source schema boundary changed: expected {_EXPECTED_SCHEMA!r}, got {manifest.get('cdf_version')!r}"
        )
    final_dir = normalized_path(data_root, snapshot_id, _EXPECTED_DATASET_ID, output_root)

    def log(event: str, payload: dict[str, Any]) -> None:
        if event_log is not None:
            append_event(event_log, event=event, payload=payload)

    log("ROR_NORMALIZATION_STARTED", {"snapshot_id": snapshot_id})
    if final_dir.exists():
        verified = verify_ror_artifact(data_root, snapshot_id, output_root=output_root)
        log("ROR_NORMALIZATION_NO_CHANGE", {"snapshot_id": snapshot_id})
        return {"status": "NO_CHANGE", "snapshot_id": snapshot_id, "artifact": verified["artifact"]}

    archive_path = data_root / manifest["content"]["archive_path"]
    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    try:
        records_path = temporary_dir / "records.jsonl"
        try:
            with zipfile.ZipFile(archive_path, "r") as archive:
                json_members = [
                    info
                    for info in archive.infolist()
                    if not info.is_dir()
                    and info.filename.lower().endswith("-ror-data.json")
                    and "schema_v1" not in info.filename.lower()
                ]
                if len(json_members) != 1:
                    names = ", ".join(info.filename for info in json_members) or "none"
                    raise ParseError(
                        f"Expected exactly one current ROR JSON format-of-record member; found {names}"
                    )
                json_member = json_members[0]
                with archive.open(json_member, "r") as raw_stream:
                    with io.TextIOWrapper(raw_stream, encoding="utf-8", newline="") as text_stream:
                        quality = _parse_json_member(text_stream, records_path)
        except zipfile.BadZipFile as exc:
            raise ParseError(f"ROR raw snapshot is not a valid ZIP archive: {archive_path}") from exc

        records_hash, records_size = sha256_file(records_path)
        make_read_only(records_path)
        _write_checksum(records_path)
        quality.update(
            {
                "snapshot_id": snapshot_id,
                "source_version": manifest["source_version"],
                "source_publication_date": (manifest.get("acquisition_response") or {}).get("publication_date"),
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
            "artifact_type": "ror_organizations_normalized",
            "parser_version": _PARSER_VERSION,
            "source_snapshot_id": snapshot_id,
            "source_dataset_id": _EXPECTED_DATASET_ID,
            "source_version": manifest["source_version"],
            "source_schema": _EXPECTED_SCHEMA,
            "source_content_id": manifest["content"]["content_id"],
            "source_archive_path": manifest["content"]["archive_path"],
            "json_member": json_member.filename,
            "records_file": "records.jsonl",
            "records_sha256": records_hash,
            "records_size_bytes": records_size,
            "record_count": quality["records_written"],
            "quality_report_file": "quality.json",
            "quality_report_sha256": quality_hash,
            "quality_report_size_bytes": quality_size,
            "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
            "created_at": utc_now_iso(),
        }
        artifact_path = temporary_dir / "artifact.json"
        atomic_write_json(artifact_path, artifact)
        make_read_only(artifact_path)
        artifact_hash = _write_checksum(artifact_path)
        temporary_dir.rename(final_dir)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        log("ROR_NORMALIZATION_FAILED", {"snapshot_id": snapshot_id})
        raise

    log(
        "ROR_NORMALIZATION_COMPLETED",
        {"snapshot_id": snapshot_id, "record_count": quality["records_written"]},
    )
    return {
        "status": "PARSED",
        "snapshot_id": snapshot_id,
        "artifact": {**artifact, "artifact_sha256": artifact_hash},
        "quality": quality,
        "verification": verification,
    }
