from __future__ import annotations

import json
import shutil
import sqlite3
import uuid
from collections import Counter
from datetime import date
from pathlib import Path
from typing import Any, Iterator

from .errors import ChangeError
from .events import append_event
from .product import verify_product
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_CHANGE_ENGINE_VERSION = "0.1.0"
_JSON_COLUMNS = {
    "legal_form_json": "legal_form",
    "registration_authority_json": "registration_authority",
    "legal_address_json": "legal_address",
    "headquarters_address_json": "headquarters_address",
    "registration_json": "registration",
}
_COMPARE_FIELDS = (
    ("legal_name", "LEGAL_NAME_CHANGED"),
    ("legal_name_language", "LEGAL_NAME_LANGUAGE_CHANGED"),
    ("entity_status", "STATUS_CHANGED"),
    ("legal_jurisdiction", "JURISDICTION_CHANGED"),
    ("entity_category", "ENTITY_CATEGORY_CHANGED"),
    ("entity_subcategory", "ENTITY_SUBCATEGORY_CHANGED"),
    ("entity_creation_date", "ENTITY_CREATION_DATE_CHANGED"),
    ("legal_form_json", "LEGAL_FORM_CHANGED"),
    ("registration_authority_json", "REGISTRATION_AUTHORITY_CHANGED"),
    ("legal_address_json", "LEGAL_ADDRESS_CHANGED"),
    ("headquarters_address_json", "HEADQUARTERS_ADDRESS_CHANGED"),
    ("registration_json", "REGISTRATION_CHANGED"),
)
_SELECT_COLUMNS = (
    "lei",
    "legal_name",
    "legal_name_language",
    "entity_status",
    "legal_jurisdiction",
    "entity_category",
    "entity_subcategory",
    "entity_creation_date",
    "legal_form_json",
    "registration_authority_json",
    "legal_address_json",
    "headquarters_address_json",
    "registration_json",
)


def changes_path(
    data_root: Path,
    source_dataset_id: str,
    from_snapshot_id: str,
    to_snapshot_id: str,
    output_root: Path | None = None,
) -> Path:
    root = output_root if output_root is not None else data_root / "changes"
    return root / source_dataset_id / f"{from_snapshot_id}__{to_snapshot_id}"


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    checksum_path = path.with_name(path.name + ".sha256")
    checksum_path.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(checksum_path)
    return digest


def _verify_checksum(path: Path) -> str:
    checksum_path = path.with_name(path.name + ".sha256")
    if not path.exists() or not checksum_path.exists():
        raise ChangeError(f"Missing change artifact or checksum: {path}")
    try:
        expected = checksum_path.read_text(encoding="ascii").split()[0]
    except (OSError, IndexError) as exc:
        raise ChangeError(f"Invalid checksum sidecar: {checksum_path}") from exc
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ChangeError(f"Change artifact checksum mismatch: {path}")
    return actual


def _read_product_manifest_light(product_dir: Path) -> dict[str, Any]:
    manifest_path = product_dir / "product.json"
    _verify_checksum(manifest_path)
    return load_json(manifest_path)


def _open_product_database(verification: dict[str, Any]) -> sqlite3.Connection:
    uri = Path(verification["database_path"]).resolve().as_uri() + "?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    connection.row_factory = sqlite3.Row
    return connection


def _row_payload(row: sqlite3.Row) -> dict[str, Any]:
    payload: dict[str, Any] = {}
    for column in _SELECT_COLUMNS:
        value = row[column]
        output_name = _JSON_COLUMNS.get(column, column)
        if column in _JSON_COLUMNS:
            payload[output_name] = json.loads(value) if value is not None else None
        else:
            payload[output_name] = value
    return payload


def _canonical_payload(payload: dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _grouped_rows(connection: sqlite3.Connection) -> Iterator[tuple[str, list[sqlite3.Row]]]:
    columns = ", ".join(_SELECT_COLUMNS)
    cursor = connection.execute(f"SELECT {columns} FROM lei ORDER BY lei, row_id")
    current_lei: str | None = None
    group: list[sqlite3.Row] = []
    for row in cursor:
        lei = str(row["lei"])
        if current_lei is None:
            current_lei = lei
        if lei != current_lei:
            yield current_lei, group
            current_lei = lei
            group = []
        group.append(row)
    if current_lei is not None:
        yield current_lei, group


def _next_or_none(iterator: Iterator[tuple[str, list[sqlite3.Row]]]) -> tuple[str, list[sqlite3.Row]] | None:
    try:
        return next(iterator)
    except StopIteration:
        return None


def _unique_change(lei: str, before: sqlite3.Row, after: sqlite3.Row) -> dict[str, Any] | None:
    changed_fields: list[str] = []
    change_types: list[str] = []
    for column, change_type in _COMPARE_FIELDS:
        if before[column] != after[column]:
            changed_fields.append(_JSON_COLUMNS.get(column, column))
            change_types.append(change_type)
    if not changed_fields:
        return None
    return {
        "lei": lei,
        "change_types": change_types,
        "changed_fields": changed_fields,
        "before": _row_payload(before),
        "after": _row_payload(after),
    }


def _ambiguous_change(
    lei: str,
    before_rows: list[sqlite3.Row],
    after_rows: list[sqlite3.Row],
) -> dict[str, Any] | None:
    before_payloads = [_row_payload(row) for row in before_rows]
    after_payloads = [_row_payload(row) for row in after_rows]
    before_signatures = sorted(_canonical_payload(payload) for payload in before_payloads)
    after_signatures = sorted(_canonical_payload(payload) for payload in after_payloads)
    if before_signatures == after_signatures:
        return None
    return {
        "lei": lei,
        "change_types": ["AMBIGUOUS_VARIANTS_CHANGED"],
        "changed_fields": [],
        "before_variant_count": len(before_payloads),
        "after_variant_count": len(after_payloads),
        "before_variants": sorted(before_payloads, key=_canonical_payload),
        "after_variants": sorted(after_payloads, key=_canonical_payload),
    }


def _event_for_groups(
    lei: str,
    before_rows: list[sqlite3.Row] | None,
    after_rows: list[sqlite3.Row] | None,
) -> dict[str, Any] | None:
    if before_rows is None:
        if after_rows is None:
            return None
        if len(after_rows) == 1:
            return {
                "lei": lei,
                "change_types": ["NEW"],
                "changed_fields": [],
                "before": None,
                "after": _row_payload(after_rows[0]),
            }
        return {
            "lei": lei,
            "change_types": ["NEW_AMBIGUOUS_VARIANTS"],
            "changed_fields": [],
            "before": None,
            "after_variant_count": len(after_rows),
            "after_variants": sorted((_row_payload(row) for row in after_rows), key=_canonical_payload),
        }

    if after_rows is None:
        if len(before_rows) == 1:
            return {
                "lei": lei,
                "change_types": ["REMOVED"],
                "changed_fields": [],
                "before": _row_payload(before_rows[0]),
                "after": None,
            }
        return {
            "lei": lei,
            "change_types": ["REMOVED_AMBIGUOUS_VARIANTS"],
            "changed_fields": [],
            "before_variant_count": len(before_rows),
            "before_variants": sorted((_row_payload(row) for row in before_rows), key=_canonical_payload),
            "after": None,
        }

    if len(before_rows) == 1 and len(after_rows) == 1:
        return _unique_change(lei, before_rows[0], after_rows[0])
    return _ambiguous_change(lei, before_rows, after_rows)


def _compare_databases(
    from_connection: sqlite3.Connection,
    to_connection: sqlite3.Connection,
    changes_file: Path,
) -> dict[str, Any]:
    before_iter = _grouped_rows(from_connection)
    after_iter = _grouped_rows(to_connection)
    before = _next_or_none(before_iter)
    after = _next_or_none(after_iter)
    type_counts: Counter[str] = Counter()
    event_count = 0
    unchanged_lei_count = 0

    with changes_file.open("w", encoding="utf-8", newline="\n") as output:
        while before is not None or after is not None:
            if before is None:
                lei = after[0]
                event = _event_for_groups(lei, None, after[1])
                after = _next_or_none(after_iter)
            elif after is None:
                lei = before[0]
                event = _event_for_groups(lei, before[1], None)
                before = _next_or_none(before_iter)
            elif before[0] < after[0]:
                lei = before[0]
                event = _event_for_groups(lei, before[1], None)
                before = _next_or_none(before_iter)
            elif after[0] < before[0]:
                lei = after[0]
                event = _event_for_groups(lei, None, after[1])
                after = _next_or_none(after_iter)
            else:
                lei = before[0]
                event = _event_for_groups(lei, before[1], after[1])
                before = _next_or_none(before_iter)
                after = _next_or_none(after_iter)

            if event is None:
                unchanged_lei_count += 1
                continue
            output.write(json.dumps(event, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
            output.write("\n")
            event_count += 1
            type_counts.update(event["change_types"])

    return {
        "change_event_count": event_count,
        "unchanged_lei_count": unchanged_lei_count,
        "change_type_counts": dict(sorted(type_counts.items())),
    }


def _source_date(value: Any, *, field: str) -> date:
    try:
        return date.fromisoformat(str(value))
    except ValueError as exc:
        raise ChangeError(f"{field} is not an ISO source date: {value!r}") from exc


def build_changes(
    data_root: Path,
    from_snapshot_id: str,
    to_snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    if from_snapshot_id == to_snapshot_id:
        raise ChangeError("Historical change input snapshots must be different")

    before_product = verify_product(data_root, from_snapshot_id, output_root=product_root)
    after_product = verify_product(data_root, to_snapshot_id, output_root=product_root)
    before_manifest = before_product["product"]
    after_manifest = after_product["product"]

    source_dataset_id = str(before_manifest["source_dataset_id"])
    if after_manifest.get("source_dataset_id") != source_dataset_id:
        raise ChangeError("Historical change inputs belong to different source datasets")

    before_version = str(before_manifest["source_version"])
    after_version = str(after_manifest["source_version"])
    if _source_date(before_version, field="from source_version") >= _source_date(after_version, field="to source_version"):
        raise ChangeError("Historical change inputs must be ordered from older to newer source version")

    final_dir = changes_path(
        data_root,
        source_dataset_id,
        from_snapshot_id,
        to_snapshot_id,
        output_root,
    )
    if final_dir.exists():
        verified = verify_changes(
            data_root,
            from_snapshot_id,
            to_snapshot_id,
            product_root=product_root,
            output_root=output_root,
        )
        verified["status"] = "NO_CHANGE"
        return verified

    if event_log is not None:
        append_event(
            event_log,
            event="HISTORICAL_CHANGE_STARTED",
            payload={"from_snapshot_id": from_snapshot_id, "to_snapshot_id": to_snapshot_id},
        )

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{final_dir.name}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    before_connection = _open_product_database(before_product)
    after_connection = _open_product_database(after_product)
    try:
        changes_file = temporary_dir / "changes.jsonl"
        counts = _compare_databases(before_connection, after_connection, changes_file)
        changes_hash, changes_size = sha256_file(changes_file)
        make_read_only(changes_file)
        _write_checksum(changes_file)

        artifact = {
            "artifact_version": 1,
            "artifact_type": "gleif_level1_historical_changes",
            "engine_version": _CHANGE_ENGINE_VERSION,
            "source_dataset_id": source_dataset_id,
            "from_snapshot_id": from_snapshot_id,
            "to_snapshot_id": to_snapshot_id,
            "from_source_version": before_version,
            "to_source_version": after_version,
            "from_product_sha256": before_manifest["product_sha256"],
            "to_product_sha256": after_manifest["product_sha256"],
            "changes_file": "changes.jsonl",
            "changes_sha256": changes_hash,
            "changes_size_bytes": changes_size,
            **counts,
            "created_at": utc_now_iso(),
        }
        artifact_path = temporary_dir / "artifact.json"
        atomic_write_json(artifact_path, artifact)
        make_read_only(artifact_path)
        artifact_hash = _write_checksum(artifact_path)
        temporary_dir.rename(final_dir)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        if event_log is not None:
            append_event(
                event_log,
                event="HISTORICAL_CHANGE_FAILED",
                payload={"from_snapshot_id": from_snapshot_id, "to_snapshot_id": to_snapshot_id},
            )
        raise
    finally:
        before_connection.close()
        after_connection.close()

    result = {
        "status": "BUILT",
        "from_snapshot_id": from_snapshot_id,
        "to_snapshot_id": to_snapshot_id,
        "change_dir": str(final_dir),
        "changes_path": str(final_dir / "changes.jsonl"),
        "artifact": {**artifact, "artifact_sha256": artifact_hash},
    }
    if event_log is not None:
        append_event(
            event_log,
            event="HISTORICAL_CHANGE_COMPLETED",
            payload={
                "from_snapshot_id": from_snapshot_id,
                "to_snapshot_id": to_snapshot_id,
                "change_event_count": artifact["change_event_count"],
                "changes_sha256": changes_hash,
            },
        )
    return result


def verify_changes(
    data_root: Path,
    from_snapshot_id: str,
    to_snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    before_product = verify_product(data_root, from_snapshot_id, output_root=product_root)
    after_product = verify_product(data_root, to_snapshot_id, output_root=product_root)
    source_dataset_id = str(before_product["product"]["source_dataset_id"])
    final_dir = changes_path(data_root, source_dataset_id, from_snapshot_id, to_snapshot_id, output_root)
    artifact_path = final_dir / "artifact.json"
    changes_file = final_dir / "changes.jsonl"
    artifact_hash = _verify_checksum(artifact_path)
    changes_hash = _verify_checksum(changes_file)
    artifact = load_json(artifact_path)

    if artifact.get("from_snapshot_id") != from_snapshot_id or artifact.get("to_snapshot_id") != to_snapshot_id:
        raise ChangeError("Historical change artifact references different snapshots")
    if artifact.get("from_product_sha256") != before_product["product"]["product_sha256"]:
        raise ChangeError("Historical change artifact from-product identity mismatch")
    if artifact.get("to_product_sha256") != after_product["product"]["product_sha256"]:
        raise ChangeError("Historical change artifact to-product identity mismatch")
    if artifact.get("changes_sha256") != changes_hash:
        raise ChangeError("Historical change artifact file hash mismatch")

    event_count = 0
    type_counts: Counter[str] = Counter()
    previous_lei: str | None = None
    with changes_file.open("r", encoding="utf-8") as changes:
        for ordinal, line in enumerate(changes, start=1):
            if not line.strip():
                raise ChangeError(f"Historical changes contain an empty line at #{ordinal}")
            try:
                event = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ChangeError(f"Historical change line #{ordinal} is invalid JSON") from exc
            if not isinstance(event, dict) or not isinstance(event.get("lei"), str):
                raise ChangeError(f"Historical change line #{ordinal} has no valid LEI")
            lei = event["lei"]
            if previous_lei is not None and lei <= previous_lei:
                raise ChangeError("Historical change events are not strictly ordered by LEI")
            previous_lei = lei
            change_types = event.get("change_types")
            if not isinstance(change_types, list) or not change_types or not all(isinstance(x, str) for x in change_types):
                raise ChangeError(f"Historical change line #{ordinal} has invalid change_types")
            type_counts.update(change_types)
            event_count += 1

    if event_count != artifact.get("change_event_count"):
        raise ChangeError("Historical change event count does not match artifact manifest")
    if dict(sorted(type_counts.items())) != artifact.get("change_type_counts"):
        raise ChangeError("Historical change type counts do not match artifact manifest")

    return {
        "status": "VERIFIED",
        "from_snapshot_id": from_snapshot_id,
        "to_snapshot_id": to_snapshot_id,
        "change_dir": str(final_dir),
        "changes_path": str(changes_file),
        "artifact": {**artifact, "artifact_sha256": artifact_hash},
    }


def build_changes_from_previous(
    data_root: Path,
    to_snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    target = verify_product(data_root, to_snapshot_id, output_root=product_root)
    target_manifest = target["product"]
    source_dataset_id = str(target_manifest["source_dataset_id"])
    target_date = _source_date(target_manifest["source_version"], field="target source_version")

    products_root = product_root if product_root is not None else data_root / "products"
    dataset_root = products_root / source_dataset_id
    candidates: list[tuple[date, str]] = []
    if dataset_root.exists():
        for product_dir in dataset_root.iterdir():
            if not product_dir.is_dir() or product_dir.name == to_snapshot_id:
                continue
            try:
                manifest = _read_product_manifest_light(product_dir)
                if manifest.get("source_dataset_id") != source_dataset_id:
                    continue
                candidate_date = _source_date(manifest.get("source_version"), field="candidate source_version")
            except (ChangeError, OSError, ValueError):
                continue
            if candidate_date < target_date:
                candidates.append((candidate_date, product_dir.name))

    if not candidates:
        return {
            "status": "NO_PREVIOUS_SNAPSHOT",
            "to_snapshot_id": to_snapshot_id,
            "to_source_version": str(target_manifest["source_version"]),
        }

    _, previous_snapshot_id = max(candidates)
    return build_changes(
        data_root,
        previous_snapshot_id,
        to_snapshot_id,
        product_root=product_root,
        output_root=output_root,
        event_log=event_log,
    )
