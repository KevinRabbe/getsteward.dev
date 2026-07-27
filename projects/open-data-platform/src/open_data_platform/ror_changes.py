from __future__ import annotations

import json
import shutil
import sqlite3
import uuid
from collections import Counter
from pathlib import Path
from typing import Any, Iterator

from .changes import changes_path
from .errors import ChangeError
from .events import append_event
from .ror_product import verify_ror_product
from .source_order import source_order_key
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_CHANGE_ENGINE_VERSION = "0.1.0"
_DATASET_ID = "ds_ror_organizations"
_EXCLUDED_SOURCE_FIELDS = ["locations"]
_JSON_COLUMNS = {
    "types_json": "types",
    "domains_json": "domains",
    "names_json": "names",
    "external_ids_json": "external_ids",
    "links_json": "links",
    "relationships_json": "relationships",
    "admin_json": "admin",
}
_COMPARE_FIELDS = (
    ("display_name", "DISPLAY_NAME_CHANGED"),
    ("status", "STATUS_CHANGED"),
    ("established", "ESTABLISHED_CHANGED"),
    ("types_json", "TYPES_CHANGED"),
    ("domains_json", "DOMAINS_CHANGED"),
    ("names_json", "NAMES_CHANGED"),
    ("external_ids_json", "EXTERNAL_IDS_CHANGED"),
    ("links_json", "LINKS_CHANGED"),
    ("relationships_json", "RELATIONSHIPS_CHANGED"),
    ("admin_json", "ADMIN_CHANGED"),
)
_SELECT_COLUMNS = (
    "ror_id",
    "display_name",
    "status",
    "established",
    "types_json",
    "domains_json",
    "names_json",
    "external_ids_json",
    "links_json",
    "relationships_json",
    "admin_json",
)


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def _verify_checksum(path: Path) -> str:
    sidecar = path.with_name(path.name + ".sha256")
    if not path.exists() or not sidecar.exists():
        raise ChangeError(f"Missing ROR change artifact or checksum: {path}")
    try:
        expected = sidecar.read_text(encoding="ascii").split()[0]
    except (OSError, IndexError) as exc:
        raise ChangeError(f"Invalid ROR change checksum sidecar: {sidecar}") from exc
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ChangeError(f"ROR change artifact checksum mismatch: {path}")
    return actual


def _snapshot_manifest(data_root: Path, snapshot_id: str) -> dict[str, Any]:
    path = data_root / "archive" / "snapshots" / snapshot_id / "manifest.json"
    manifest = load_json(path)
    if manifest.get("snapshot_id") != snapshot_id:
        raise ChangeError(f"ROR snapshot manifest identity mismatch: {snapshot_id}")
    if manifest.get("source_dataset_id") != _DATASET_ID:
        raise ChangeError(f"Snapshot {snapshot_id} is not the ROR organizations dataset")
    return manifest


def _open_product(verification: dict[str, Any]) -> sqlite3.Connection:
    path = Path(verification["database_path"])
    connection = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    return connection


def _rows(connection: sqlite3.Connection) -> Iterator[sqlite3.Row]:
    columns = ", ".join(_SELECT_COLUMNS)
    yield from connection.execute(
        f"SELECT {columns} FROM organization ORDER BY ror_id"
    )


def _next_or_none(iterator: Iterator[sqlite3.Row]) -> sqlite3.Row | None:
    try:
        return next(iterator)
    except StopIteration:
        return None


def _row_payload(row: sqlite3.Row) -> dict[str, Any]:
    payload: dict[str, Any] = {}
    for column in _SELECT_COLUMNS:
        value = row[column]
        output_name = _JSON_COLUMNS.get(column, column)
        if column in _JSON_COLUMNS:
            try:
                payload[output_name] = json.loads(value)
            except json.JSONDecodeError as exc:
                raise ChangeError(
                    f"ROR product contains invalid canonical JSON in {column}"
                ) from exc
        else:
            payload[output_name] = value
    return payload


def _changed_event(before: sqlite3.Row, after: sqlite3.Row) -> dict[str, Any] | None:
    ror_id = str(before["ror_id"])
    if ror_id != str(after["ror_id"]):
        raise ChangeError("ROR change comparison received mismatched identifiers")
    changed_fields: list[str] = []
    change_types: list[str] = []
    for column, change_type in _COMPARE_FIELDS:
        if before[column] != after[column]:
            changed_fields.append(_JSON_COLUMNS.get(column, column))
            change_types.append(change_type)
    if not changed_fields:
        return None
    return {
        "ror_id": ror_id,
        "change_types": change_types,
        "changed_fields": changed_fields,
        "before": _row_payload(before),
        "after": _row_payload(after),
    }


def _compare_databases(
    before_connection: sqlite3.Connection,
    after_connection: sqlite3.Connection,
    changes_file: Path,
) -> dict[str, Any]:
    before_iter = _rows(before_connection)
    after_iter = _rows(after_connection)
    before = _next_or_none(before_iter)
    after = _next_or_none(after_iter)
    type_counts: Counter[str] = Counter()
    event_count = 0
    unchanged_count = 0

    with changes_file.open("w", encoding="utf-8", newline="\n") as output:
        while before is not None or after is not None:
            event: dict[str, Any] | None
            if before is None:
                event = {
                    "ror_id": str(after["ror_id"]),
                    "change_types": ["NEW"],
                    "changed_fields": [],
                    "before": None,
                    "after": _row_payload(after),
                }
                after = _next_or_none(after_iter)
            elif after is None:
                event = {
                    "ror_id": str(before["ror_id"]),
                    "change_types": ["REMOVED"],
                    "changed_fields": [],
                    "before": _row_payload(before),
                    "after": None,
                }
                before = _next_or_none(before_iter)
            elif str(before["ror_id"]) < str(after["ror_id"]):
                event = {
                    "ror_id": str(before["ror_id"]),
                    "change_types": ["REMOVED"],
                    "changed_fields": [],
                    "before": _row_payload(before),
                    "after": None,
                }
                before = _next_or_none(before_iter)
            elif str(after["ror_id"]) < str(before["ror_id"]):
                event = {
                    "ror_id": str(after["ror_id"]),
                    "change_types": ["NEW"],
                    "changed_fields": [],
                    "before": None,
                    "after": _row_payload(after),
                }
                after = _next_or_none(after_iter)
            else:
                event = _changed_event(before, after)
                before = _next_or_none(before_iter)
                after = _next_or_none(after_iter)

            if event is None:
                unchanged_count += 1
                continue
            if "locations" in (event.get("before") or {}) or "locations" in (event.get("after") or {}):
                raise ChangeError("Excluded ROR locations field leaked into historical changes")
            output.write(
                json.dumps(event, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
            )
            output.write("\n")
            event_count += 1
            type_counts.update(event["change_types"])

    return {
        "change_event_count": event_count,
        "unchanged_ror_id_count": unchanged_count,
        "change_type_counts": dict(sorted(type_counts.items())),
    }


def build_ror_changes(
    data_root: Path,
    from_snapshot_id: str,
    to_snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    if from_snapshot_id == to_snapshot_id:
        raise ChangeError("ROR historical change input snapshots must be different")

    before_product = verify_ror_product(
        data_root, from_snapshot_id, output_root=product_root
    )
    after_product = verify_ror_product(
        data_root, to_snapshot_id, output_root=product_root
    )
    before_manifest = before_product["product"]
    after_manifest = after_product["product"]
    if before_manifest.get("source_dataset_id") != _DATASET_ID or after_manifest.get("source_dataset_id") != _DATASET_ID:
        raise ChangeError("ROR historical change inputs have the wrong dataset identity")
    if before_manifest.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS or after_manifest.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ChangeError("ROR historical change inputs lost the locations exclusion boundary")

    before_snapshot = _snapshot_manifest(data_root, from_snapshot_id)
    after_snapshot = _snapshot_manifest(data_root, to_snapshot_id)
    before_order = source_order_key(before_snapshot)
    after_order = source_order_key(after_snapshot)
    if before_order >= after_order:
        raise ChangeError("ROR historical change inputs must be ordered older to newer")

    final_dir = changes_path(
        data_root,
        _DATASET_ID,
        from_snapshot_id,
        to_snapshot_id,
        output_root,
    )
    if final_dir.exists():
        verified = verify_ror_changes(
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
            event="ROR_HISTORICAL_CHANGE_STARTED",
            payload={"from_snapshot_id": from_snapshot_id, "to_snapshot_id": to_snapshot_id},
        )

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{final_dir.name}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    before_connection = _open_product(before_product)
    after_connection = _open_product(after_product)
    try:
        changes_file = temporary_dir / "changes.jsonl"
        counts = _compare_databases(before_connection, after_connection, changes_file)
        changes_hash, changes_size = sha256_file(changes_file)
        make_read_only(changes_file)
        _write_checksum(changes_file)

        artifact = {
            "artifact_version": 1,
            "artifact_type": "ror_organizations_historical_changes",
            "engine_version": _CHANGE_ENGINE_VERSION,
            "source_dataset_id": _DATASET_ID,
            "from_snapshot_id": from_snapshot_id,
            "to_snapshot_id": to_snapshot_id,
            "from_source_version": before_manifest["source_version"],
            "to_source_version": after_manifest["source_version"],
            "from_source_publication_date": before_order[0],
            "to_source_publication_date": after_order[0],
            "from_product_manifest_sha256": before_manifest["manifest_sha256"],
            "to_product_manifest_sha256": after_manifest["manifest_sha256"],
            "from_product_database_sha256": before_manifest["product_sha256"],
            "to_product_database_sha256": after_manifest["product_sha256"],
            "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
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
                event="ROR_HISTORICAL_CHANGE_FAILED",
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
            event="ROR_HISTORICAL_CHANGE_COMPLETED",
            payload={
                "from_snapshot_id": from_snapshot_id,
                "to_snapshot_id": to_snapshot_id,
                "change_event_count": artifact["change_event_count"],
                "changes_sha256": changes_hash,
            },
        )
    return result


def _validate_event_payload(payload: Any, *, ordinal: int, side: str) -> None:
    if payload is None:
        return
    if not isinstance(payload, dict):
        raise ChangeError(f"ROR change line #{ordinal} has invalid {side} payload")
    expected_fields = {"ror_id", *(_JSON_COLUMNS.get(column, column) for column, _ in _COMPARE_FIELDS)}
    if set(payload) != expected_fields:
        raise ChangeError(f"ROR change line #{ordinal} has unexpected {side} fields")
    if "locations" in payload:
        raise ChangeError("Excluded ROR locations field leaked into historical changes")


def verify_ror_changes(
    data_root: Path,
    from_snapshot_id: str,
    to_snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    before_product = verify_ror_product(
        data_root, from_snapshot_id, output_root=product_root
    )
    after_product = verify_ror_product(
        data_root, to_snapshot_id, output_root=product_root
    )
    before_snapshot = _snapshot_manifest(data_root, from_snapshot_id)
    after_snapshot = _snapshot_manifest(data_root, to_snapshot_id)
    before_order = source_order_key(before_snapshot)
    after_order = source_order_key(after_snapshot)
    if before_order >= after_order:
        raise ChangeError("ROR historical change inputs are no longer chronologically ordered")

    final_dir = changes_path(
        data_root,
        _DATASET_ID,
        from_snapshot_id,
        to_snapshot_id,
        output_root,
    )
    artifact_path = final_dir / "artifact.json"
    changes_file = final_dir / "changes.jsonl"
    artifact_hash = _verify_checksum(artifact_path)
    changes_hash = _verify_checksum(changes_file)
    artifact = load_json(artifact_path)

    if artifact.get("artifact_type") != "ror_organizations_historical_changes":
        raise ChangeError("ROR historical change artifact type mismatch")
    if artifact.get("from_snapshot_id") != from_snapshot_id or artifact.get("to_snapshot_id") != to_snapshot_id:
        raise ChangeError("ROR historical change artifact references different snapshots")
    if artifact.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ChangeError("ROR historical change artifact lost the locations exclusion")
    if artifact.get("from_source_publication_date") != before_order[0] or artifact.get("to_source_publication_date") != after_order[0]:
        raise ChangeError("ROR historical change chronology mismatch")
    before_manifest = before_product["product"]
    after_manifest = after_product["product"]
    if artifact.get("from_product_manifest_sha256") != before_manifest["manifest_sha256"]:
        raise ChangeError("ROR historical change from-product manifest mismatch")
    if artifact.get("to_product_manifest_sha256") != after_manifest["manifest_sha256"]:
        raise ChangeError("ROR historical change to-product manifest mismatch")
    if artifact.get("from_product_database_sha256") != before_manifest["product_sha256"]:
        raise ChangeError("ROR historical change from-product database mismatch")
    if artifact.get("to_product_database_sha256") != after_manifest["product_sha256"]:
        raise ChangeError("ROR historical change to-product database mismatch")
    if artifact.get("changes_sha256") != changes_hash:
        raise ChangeError("ROR historical change file hash mismatch")

    event_count = 0
    type_counts: Counter[str] = Counter()
    previous_id: str | None = None
    with changes_file.open("r", encoding="utf-8") as source:
        for ordinal, line in enumerate(source, start=1):
            if not line.strip():
                raise ChangeError(f"ROR changes contain an empty line at #{ordinal}")
            try:
                event = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ChangeError(f"ROR change line #{ordinal} is invalid JSON") from exc
            if not isinstance(event, dict) or not isinstance(event.get("ror_id"), str):
                raise ChangeError(f"ROR change line #{ordinal} has no valid ror_id")
            ror_id = event["ror_id"]
            if previous_id is not None and ror_id <= previous_id:
                raise ChangeError("ROR historical change events are not strictly ordered by ror_id")
            previous_id = ror_id
            change_types = event.get("change_types")
            if not isinstance(change_types, list) or not change_types or not all(isinstance(item, str) for item in change_types):
                raise ChangeError(f"ROR change line #{ordinal} has invalid change_types")
            changed_fields = event.get("changed_fields")
            if not isinstance(changed_fields, list) or not all(isinstance(item, str) for item in changed_fields):
                raise ChangeError(f"ROR change line #{ordinal} has invalid changed_fields")
            _validate_event_payload(event.get("before"), ordinal=ordinal, side="before")
            _validate_event_payload(event.get("after"), ordinal=ordinal, side="after")
            type_counts.update(change_types)
            event_count += 1

    if event_count != artifact.get("change_event_count"):
        raise ChangeError("ROR historical change event count does not match artifact manifest")
    if dict(sorted(type_counts.items())) != artifact.get("change_type_counts"):
        raise ChangeError("ROR historical change type counts do not match artifact manifest")

    return {
        "status": "VERIFIED",
        "from_snapshot_id": from_snapshot_id,
        "to_snapshot_id": to_snapshot_id,
        "change_dir": str(final_dir),
        "changes_path": str(changes_file),
        "artifact": {**artifact, "artifact_sha256": artifact_hash},
    }


def _product_snapshot_orders(
    data_root: Path,
    *,
    product_root: Path | None,
) -> list[tuple[tuple[str, str], str]]:
    products_root = product_root if product_root is not None else data_root / "products"
    dataset_root = products_root / _DATASET_ID
    if not dataset_root.exists():
        return []
    candidates: list[tuple[tuple[str, str], str]] = []
    for product_dir in dataset_root.iterdir():
        if not product_dir.is_dir() or product_dir.name.startswith("."):
            continue
        snapshot = _snapshot_manifest(data_root, product_dir.name)
        candidates.append((source_order_key(snapshot), product_dir.name))
    candidates.sort()
    return candidates


def build_ror_changes_around(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
    event_log: Path | None = None,
) -> dict[str, Any]:
    verify_ror_product(data_root, snapshot_id, output_root=product_root)
    target_snapshot = _snapshot_manifest(data_root, snapshot_id)
    target_order = source_order_key(target_snapshot)
    candidates = _product_snapshot_orders(data_root, product_root=product_root)

    previous = [item for item in candidates if item[0] < target_order]
    following = [item for item in candidates if item[0] > target_order]
    pairs: list[dict[str, Any]] = []
    if previous:
        _, previous_snapshot_id = previous[-1]
        pairs.append(
            build_ror_changes(
                data_root,
                previous_snapshot_id,
                snapshot_id,
                product_root=product_root,
                output_root=output_root,
                event_log=event_log,
            )
        )
    if following:
        _, next_snapshot_id = following[0]
        pairs.append(
            build_ror_changes(
                data_root,
                snapshot_id,
                next_snapshot_id,
                product_root=product_root,
                output_root=output_root,
                event_log=event_log,
            )
        )

    return {
        "status": "BUILT" if pairs else "NO_ADJACENT_SNAPSHOT",
        "snapshot_id": snapshot_id,
        "pair_count": len(pairs),
        "pairs": pairs,
    }
