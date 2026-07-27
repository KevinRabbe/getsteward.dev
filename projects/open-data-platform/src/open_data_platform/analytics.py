from __future__ import annotations

import json
import sqlite3
import uuid
from collections import Counter
from pathlib import Path
from typing import Any

from .changes import verify_changes
from .errors import ProductError
from .parser import verify_normalized_artifact
from .product import verify_product
from .relationship_parser import verify_relationship_artifact
from .relationship_product import verify_relationship_product
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_ANALYTICS_VERSION = "0.1.0"
_LEVEL1_DATASET = "ds_gleif_lei_level1_concat"
_RR_DATASET = "ds_gleif_rr_level2_concat"


def analytics_path(
    data_root: Path,
    source_dataset_id: str,
    snapshot_id: str,
    output_root: Path | None = None,
) -> Path:
    root = output_root if output_root is not None else data_root / "analytics"
    return root / source_dataset_id / snapshot_id


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def _verify_checksum(path: Path) -> str:
    sidecar = path.with_name(path.name + ".sha256")
    if not path.exists() or not sidecar.exists():
        raise ProductError(f"Missing analytics artifact or checksum: {path}")
    try:
        expected = sidecar.read_text(encoding="ascii").split()[0]
    except (OSError, IndexError) as exc:
        raise ProductError(f"Invalid analytics checksum sidecar: {sidecar}") from exc
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ProductError(f"Analytics checksum mismatch: {path}")
    return actual


def _snapshot_manifest(data_root: Path, snapshot_id: str) -> dict[str, Any]:
    path = data_root / "archive" / "snapshots" / snapshot_id / "manifest.json"
    value = load_json(path)
    if value.get("snapshot_id") != snapshot_id:
        raise ProductError("Snapshot manifest identity mismatch while building analytics")
    return value


def _group_counts(connection: sqlite3.Connection, table: str, column: str) -> dict[str, int]:
    rows = connection.execute(
        f"SELECT {column}, COUNT(*) FROM {table} GROUP BY {column} ORDER BY {column}"
    ).fetchall()
    return {
        ("<NULL>" if value is None else str(value)): int(count)
        for value, count in rows
    }


def _json_key_counts(
    connection: sqlite3.Connection,
    table: str,
    json_column: str,
    key: str,
) -> dict[str, int]:
    counts: Counter[str] = Counter()
    for (raw,) in connection.execute(f"SELECT {json_column} FROM {table} ORDER BY row_id"):
        if raw is None:
            counts["<NULL>"] += 1
            continue
        try:
            value = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise ProductError(f"Stored product JSON is invalid in {json_column}") from exc
        selected = value.get(key) if isinstance(value, dict) else None
        counts["<NULL>" if selected is None else str(selected)] += 1
    return dict(sorted(counts.items()))


def _level1_profile(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None,
    normalized_root: Path | None,
) -> dict[str, Any]:
    product = verify_product(data_root, snapshot_id, output_root=product_root)
    normalized = verify_normalized_artifact(data_root, snapshot_id, output_root=normalized_root)
    database = Path(product["database_path"])
    connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
    try:
        jurisdiction_counts = _group_counts(connection, "lei", "legal_jurisdiction")
        category_counts = _group_counts(connection, "lei", "entity_category")
        legal_form_counts = _json_key_counts(
            connection,
            "lei",
            "legal_form_json",
            "entity_legal_form_code",
        )
    finally:
        connection.close()

    manifest = product["product"]
    record_count = int(product["record_count"])
    unique_count = int(manifest.get("unique_lei_count", record_count))
    duplicate_count = int(manifest.get("duplicate_lei_count", record_count - unique_count))
    return {
        "source_dataset_id": _LEVEL1_DATASET,
        "source_version": str(manifest["source_version"]),
        "record_count": record_count,
        "unique_lei_count": unique_count,
        "duplicate_lei_count": duplicate_count,
        "duplicate_rate": round(duplicate_count / record_count, 12) if record_count else 0.0,
        "entity_status_counts": normalized["quality"].get("entity_status_counts", {}),
        "registration_status_counts": normalized["quality"].get("registration_status_counts", {}),
        "missing_optional_field_counts": normalized["quality"].get("missing_optional_field_counts", {}),
        "legal_jurisdiction_counts": jurisdiction_counts,
        "entity_category_counts": category_counts,
        "legal_form_code_counts": legal_form_counts,
        "product_manifest_sha256": manifest["product_sha256"],
        "product_database_sha256": manifest["database_sha256"],
        "normalized_artifact_sha256": normalized["artifact"]["artifact_sha256"],
    }


def _rr_profile(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None,
    normalized_root: Path | None,
) -> dict[str, Any]:
    product = verify_relationship_product(
        data_root,
        snapshot_id,
        output_root=product_root,
        normalized_root=normalized_root,
    )
    normalized = verify_relationship_artifact(data_root, snapshot_id, output_root=normalized_root)
    quality = normalized["quality"]
    manifest = product["product"]
    return {
        "source_dataset_id": _RR_DATASET,
        "source_version": str(manifest["source_version"]),
        "record_count": int(product["record_count"]),
        "relationship_type_counts": quality.get("relationship_type_counts", {}),
        "relationship_status_counts": quality.get("relationship_status_counts", {}),
        "registration_status_counts": quality.get("registration_status_counts", {}),
        "start_node_type_counts": quality.get("start_node_type_counts", {}),
        "end_node_type_counts": quality.get("end_node_type_counts", {}),
        "product_manifest_sha256": manifest["manifest_sha256"],
        "product_database_sha256": manifest["product_sha256"],
        "normalized_artifact_sha256": normalized["artifact"]["artifact_sha256"],
    }


def _find_previous_analytics(
    data_root: Path,
    dataset_id: str,
    source_version: str,
    *,
    output_root: Path | None,
) -> dict[str, Any] | None:
    root = output_root if output_root is not None else data_root / "analytics"
    dataset_root = root / dataset_id
    candidates: list[tuple[str, dict[str, Any]]] = []
    if not dataset_root.exists():
        return None
    for directory in dataset_root.iterdir():
        if not directory.is_dir():
            continue
        artifact_path = directory / "quality.json"
        if not artifact_path.exists():
            continue
        _verify_checksum(artifact_path)
        artifact = load_json(artifact_path)
        version = artifact.get("source_version")
        if isinstance(version, str) and version < source_version:
            candidates.append((version, artifact))
    if not candidates:
        return None
    return max(candidates, key=lambda item: item[0])[1]


def _find_incoming_change_summary(
    data_root: Path,
    dataset_id: str,
    snapshot_id: str,
) -> dict[str, Any] | None:
    if dataset_id != _LEVEL1_DATASET:
        return None
    root = data_root / "changes" / dataset_id
    if not root.exists():
        return None
    verified_candidates: list[dict[str, Any]] = []
    suffix = f"__{snapshot_id}"
    for directory in root.iterdir():
        if not directory.is_dir() or not directory.name.endswith(suffix):
            continue
        from_snapshot_id = directory.name[: -len(suffix)]
        if not from_snapshot_id:
            continue
        verified_candidates.append(
            verify_changes(data_root, from_snapshot_id, snapshot_id)
        )
    if not verified_candidates:
        return None
    selected = max(
        verified_candidates,
        key=lambda value: str(value["artifact"].get("from_source_version", "")),
    )
    artifact = selected["artifact"]
    return {
        "from_snapshot_id": selected["from_snapshot_id"],
        "from_source_version": artifact["from_source_version"],
        "change_event_count": artifact["change_event_count"],
        "unchanged_lei_count": artifact["unchanged_lei_count"],
        "change_type_counts": artifact["change_type_counts"],
        "changes_sha256": artifact["changes_sha256"],
        "artifact_sha256": artifact["artifact_sha256"],
    }


def _delta(current: int | float | None, previous: int | float | None) -> int | float | None:
    if current is None or previous is None:
        return None
    return current - previous


def build_quality_profile(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None = None,
    normalized_root: Path | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    snapshot = _snapshot_manifest(data_root, snapshot_id)
    dataset_id = str(snapshot["source_dataset_id"])
    final_dir = analytics_path(data_root, dataset_id, snapshot_id, output_root)
    if final_dir.exists():
        verified = verify_quality_profile(data_root, snapshot_id, output_root=output_root)
        verified["status"] = "NO_CHANGE"
        return verified

    if dataset_id == _LEVEL1_DATASET:
        profile = _level1_profile(
            data_root,
            snapshot_id,
            product_root=product_root,
            normalized_root=normalized_root,
        )
    elif dataset_id == _RR_DATASET:
        profile = _rr_profile(
            data_root,
            snapshot_id,
            product_root=product_root,
            normalized_root=normalized_root,
        )
    else:
        raise ProductError(f"No quality profile registered for dataset {dataset_id}")

    previous = _find_previous_analytics(
        data_root,
        dataset_id,
        profile["source_version"],
        output_root=output_root,
    )
    changes = _find_incoming_change_summary(data_root, dataset_id, snapshot_id)
    comparison = None
    if previous is not None:
        comparison = {
            "previous_snapshot_id": previous["source_snapshot_id"],
            "previous_source_version": previous["source_version"],
            "record_count_delta": _delta(profile.get("record_count"), previous.get("record_count")),
            "unique_lei_count_delta": _delta(profile.get("unique_lei_count"), previous.get("unique_lei_count")),
            "duplicate_lei_count_delta": _delta(profile.get("duplicate_lei_count"), previous.get("duplicate_lei_count")),
            "duplicate_rate_delta": _delta(profile.get("duplicate_rate"), previous.get("duplicate_rate")),
        }

    artifact = {
        "analytics_version": 1,
        "builder_version": _ANALYTICS_VERSION,
        "source_snapshot_id": snapshot_id,
        **profile,
        "previous_comparison": comparison,
        "incoming_historical_changes": changes,
        "generated_at": utc_now_iso(),
    }
    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    try:
        artifact_path = temporary_dir / "quality.json"
        atomic_write_json(artifact_path, artifact)
        make_read_only(artifact_path)
        artifact_hash = _write_checksum(artifact_path)
        temporary_dir.rename(final_dir)
    except Exception:
        import shutil

        shutil.rmtree(temporary_dir, ignore_errors=True)
        raise
    return {
        "status": "BUILT",
        "snapshot_id": snapshot_id,
        "analytics_dir": str(final_dir),
        "quality": {**artifact, "quality_sha256": artifact_hash},
    }


def verify_quality_profile(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    snapshot = _snapshot_manifest(data_root, snapshot_id)
    dataset_id = str(snapshot["source_dataset_id"])
    final_dir = analytics_path(data_root, dataset_id, snapshot_id, output_root)
    artifact_path = final_dir / "quality.json"
    artifact_hash = _verify_checksum(artifact_path)
    artifact = load_json(artifact_path)
    if artifact.get("source_snapshot_id") != snapshot_id:
        raise ProductError("Quality profile belongs to a different snapshot")
    if artifact.get("source_dataset_id") != dataset_id:
        raise ProductError("Quality profile dataset identity mismatch")
    if artifact.get("source_version") != snapshot.get("source_version"):
        raise ProductError("Quality profile source version mismatch")
    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "analytics_dir": str(final_dir),
        "quality": {**artifact, "quality_sha256": artifact_hash},
    }


def quality_timeline(
    data_root: Path,
    source_dataset_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    root = output_root if output_root is not None else data_root / "analytics"
    dataset_root = root / source_dataset_id
    entries: list[dict[str, Any]] = []
    if dataset_root.exists():
        for directory in dataset_root.iterdir():
            if not directory.is_dir():
                continue
            artifact_path = directory / "quality.json"
            if not artifact_path.exists():
                continue
            artifact_hash = _verify_checksum(artifact_path)
            artifact = load_json(artifact_path)
            if artifact.get("source_dataset_id") != source_dataset_id:
                raise ProductError("Timeline encountered analytics from a different dataset")
            entries.append(
                {
                    "source_snapshot_id": artifact["source_snapshot_id"],
                    "source_version": artifact["source_version"],
                    "record_count": artifact.get("record_count"),
                    "unique_lei_count": artifact.get("unique_lei_count"),
                    "duplicate_lei_count": artifact.get("duplicate_lei_count"),
                    "duplicate_rate": artifact.get("duplicate_rate"),
                    "change_event_count": (
                        (artifact.get("incoming_historical_changes") or {}).get("change_event_count")
                    ),
                    "change_type_counts": (
                        (artifact.get("incoming_historical_changes") or {}).get("change_type_counts")
                    ),
                    "quality_sha256": artifact_hash,
                }
            )
    entries.sort(key=lambda value: (str(value["source_version"]), str(value["source_snapshot_id"])))
    return {
        "status": "OK",
        "source_dataset_id": source_dataset_id,
        "snapshot_count": len(entries),
        "first_source_version": entries[0]["source_version"] if entries else None,
        "latest_source_version": entries[-1]["source_version"] if entries else None,
        "entries": entries,
    }
