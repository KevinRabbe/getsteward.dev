from __future__ import annotations

import hashlib
import json
import shutil
import sqlite3
import uuid
import zipfile
from pathlib import Path
from typing import Any

from .analytics import quality_timeline, verify_quality_profile
from .changes import verify_changes
from .distributions import (
    build_csv_distribution,
    build_parquet_distribution,
    verify_distribution,
)
from .errors import ProductError
from .product import verify_product
from .relationship_product import verify_relationship_product
from .release import _deterministic_zip
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_BUILDER_VERSION = "0.1.0"
_PRODUCT_ID = "prod_global_legal_entity_history"
_LEVEL1_DATASET = "ds_gleif_lei_level1_concat"
_RR_DATASET = "ds_gleif_rr_level2_concat"


def commercial_path(
    data_root: Path,
    product_id: str,
    entity_snapshot_id: str,
    output_root: Path | None = None,
) -> Path:
    root = output_root if output_root is not None else data_root / "commercial"
    return root / product_id / entity_snapshot_id


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def _verify_checksum(path: Path) -> str:
    sidecar = path.with_name(path.name + ".sha256")
    if not path.exists() or not sidecar.exists():
        raise ProductError(f"Missing commercial artifact or checksum: {path}")
    try:
        expected = sidecar.read_text(encoding="ascii").split()[0]
    except (OSError, IndexError) as exc:
        raise ProductError(f"Invalid commercial checksum sidecar: {sidecar}") from exc
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ProductError(f"Commercial artifact checksum mismatch: {path}")
    return actual


def _copy_verified_file(source: Path, destination: Path, *, relative_to: Path) -> dict[str, Any]:
    if not source.is_file():
        raise ProductError(f"Commercial package input does not exist: {source}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    digest, size = sha256_file(destination)
    return {
        "path": destination.relative_to(relative_to).as_posix(),
        "sha256": digest,
        "size_bytes": size,
    }


def _write_text_file(destination: Path, text: str, *, relative_to: Path) -> dict[str, Any]:
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(text, encoding="utf-8", newline="\n")
    digest, size = sha256_file(destination)
    return {
        "path": destination.relative_to(relative_to).as_posix(),
        "sha256": digest,
        "size_bytes": size,
    }


def _write_json_file(destination: Path, value: Any, *, relative_to: Path) -> dict[str, Any]:
    atomic_write_json(destination, value)
    digest, size = sha256_file(destination)
    return {
        "path": destination.relative_to(relative_to).as_posix(),
        "sha256": digest,
        "size_bytes": size,
    }


def _sqlite_schema(database_path: Path, table: str) -> list[dict[str, Any]]:
    connection = sqlite3.connect(database_path.resolve().as_uri() + "?mode=ro", uri=True)
    try:
        rows = connection.execute(f"PRAGMA table_info({table})").fetchall()
    finally:
        connection.close()
    if not rows:
        raise ProductError(f"Commercial source product table missing: {table}")
    return [
        {
            "ordinal": int(row[0]),
            "name": str(row[1]),
            "type": str(row[2]),
            "not_null": bool(row[3]),
            "default": row[4],
            "primary_key": bool(row[5]),
        }
        for row in rows
    ]


def _history_inputs(
    data_root: Path,
    entity_snapshot_id: str,
    entity_source_version: str,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    timeline = quality_timeline(data_root, _LEVEL1_DATASET)
    history: list[dict[str, Any]] = []
    for entry in timeline["entries"]:
        if str(entry["source_version"]) > entity_source_version:
            continue
        profile = verify_quality_profile(data_root, str(entry["source_snapshot_id"]))["quality"]
        incoming = profile.get("incoming_historical_changes")
        if not incoming:
            continue
        verified = verify_changes(
            data_root,
            str(incoming["from_snapshot_id"]),
            str(entry["source_snapshot_id"]),
        )
        artifact = verified["artifact"]
        if str(artifact["to_source_version"]) > entity_source_version:
            continue
        history.append(
            {
                "from_snapshot_id": verified["from_snapshot_id"],
                "to_snapshot_id": verified["to_snapshot_id"],
                "from_source_version": artifact["from_source_version"],
                "to_source_version": artifact["to_source_version"],
                "changes_path": verified["changes_path"],
                "changes_sha256": artifact["changes_sha256"],
                "change_event_count": artifact["change_event_count"],
                "change_type_counts": artifact["change_type_counts"],
            }
        )
    history.sort(key=lambda item: (item["to_source_version"], item["from_source_version"]))
    return history, timeline


def _product_definition(catalog_path: Path | None) -> dict[str, Any]:
    path = catalog_path or Path("catalog/products/global_legal_entity_history.json")
    if not path.is_file():
        raise ProductError(f"Commercial product definition not found: {path}")
    value = load_json(path)
    if value.get("product_id") != _PRODUCT_ID:
        raise ProductError("Commercial product definition has unexpected product_id")
    if value.get("customer_terms_status") != "LEGAL_REVIEW_REQUIRED":
        raise ProductError("Commercial product definition must preserve the legal-review gate")
    return value


def _ensure_distribution(
    data_root: Path,
    snapshot_id: str,
    format_name: str,
    *,
    include_parquet: bool,
) -> dict[str, Any] | None:
    if format_name == "csv":
        build_csv_distribution(data_root, snapshot_id)
        return verify_distribution(data_root, snapshot_id, "csv")
    if format_name == "parquet" and include_parquet:
        build_parquet_distribution(data_root, snapshot_id)
        return verify_distribution(data_root, snapshot_id, "parquet")
    return None


def build_commercial_product(
    data_root: Path,
    entity_snapshot_id: str,
    *,
    relationship_snapshot_id: str | None = None,
    include_parquet: bool = False,
    catalog_path: Path | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    definition = _product_definition(catalog_path)
    entity_product = verify_product(data_root, entity_snapshot_id)
    entity_manifest = entity_product["product"]
    if entity_manifest.get("source_dataset_id") != _LEVEL1_DATASET:
        raise ProductError("Commercial product requires a GLEIF Level 1 entity snapshot")
    entity_quality = verify_quality_profile(data_root, entity_snapshot_id)
    entity_csv = _ensure_distribution(data_root, entity_snapshot_id, "csv", include_parquet=include_parquet)
    entity_parquet = _ensure_distribution(
        data_root,
        entity_snapshot_id,
        "parquet",
        include_parquet=include_parquet,
    )
    entity_source_version = str(entity_manifest["source_version"])
    history, timeline = _history_inputs(data_root, entity_snapshot_id, entity_source_version)

    relationship_product = None
    relationship_quality = None
    relationship_csv = None
    relationship_parquet = None
    if relationship_snapshot_id is not None:
        relationship_product = verify_relationship_product(data_root, relationship_snapshot_id)
        relationship_manifest = relationship_product["product"]
        if relationship_manifest.get("source_dataset_id") != _RR_DATASET:
            raise ProductError("Commercial relationship snapshot is not GLEIF RR-CDF")
        if str(relationship_manifest["source_version"]) > entity_source_version:
            raise ProductError("Relationship snapshot cannot be newer than the entity product state")
        relationship_quality = verify_quality_profile(data_root, relationship_snapshot_id)
        relationship_csv = _ensure_distribution(
            data_root,
            relationship_snapshot_id,
            "csv",
            include_parquet=include_parquet,
        )
        relationship_parquet = _ensure_distribution(
            data_root,
            relationship_snapshot_id,
            "parquet",
            include_parquet=include_parquet,
        )

    final_dir = commercial_path(data_root, _PRODUCT_ID, entity_snapshot_id, output_root)
    if final_dir.exists():
        verified = verify_commercial_product(
            data_root,
            entity_snapshot_id,
            relationship_snapshot_id=relationship_snapshot_id,
            output_root=output_root,
        )
        verified["status"] = "NO_CHANGE"
        return verified

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{entity_snapshot_id}.tmp-{uuid.uuid4().hex}"
    payload = temporary_dir / "payload"
    payload.mkdir(parents=True, exist_ok=False)
    files: list[dict[str, Any]] = []
    try:
        files.append(
            _copy_verified_file(
                Path(entity_product["database_path"]),
                payload / "data" / "entities.sqlite",
                relative_to=payload,
            )
        )
        files.append(
            _copy_verified_file(
                Path(entity_csv["data_path"]),
                payload / "data" / "entities.csv",
                relative_to=payload,
            )
        )
        if entity_parquet is not None:
            files.append(
                _copy_verified_file(
                    Path(entity_parquet["data_path"]),
                    payload / "data" / "entities.parquet",
                    relative_to=payload,
                )
            )
        files.append(
            _write_json_file(
                payload / "quality" / "entities-quality.json",
                entity_quality["quality"],
                relative_to=payload,
            )
        )
        files.append(
            _write_json_file(
                payload / "quality" / "timeline.json",
                timeline,
                relative_to=payload,
            )
        )
        files.append(
            _write_json_file(
                payload / "schema" / "entities.json",
                _sqlite_schema(Path(entity_product["database_path"]), "lei"),
                relative_to=payload,
            )
        )

        history_manifest: list[dict[str, Any]] = []
        for item in history:
            filename = f"{item['from_source_version']}__{item['to_source_version']}.jsonl"
            copied = _copy_verified_file(
                Path(item["changes_path"]),
                payload / "history" / filename,
                relative_to=payload,
            )
            files.append(copied)
            history_manifest.append({**item, "package_path": copied["path"]})

        relationship_component = None
        if relationship_product is not None and relationship_quality is not None and relationship_csv is not None:
            files.append(
                _copy_verified_file(
                    Path(relationship_product["database_path"]),
                    payload / "relationships" / "relationships.sqlite",
                    relative_to=payload,
                )
            )
            files.append(
                _copy_verified_file(
                    Path(relationship_csv["data_path"]),
                    payload / "relationships" / "relationships.csv",
                    relative_to=payload,
                )
            )
            if relationship_parquet is not None:
                files.append(
                    _copy_verified_file(
                        Path(relationship_parquet["data_path"]),
                        payload / "relationships" / "relationships.parquet",
                        relative_to=payload,
                    )
                )
            files.append(
                _write_json_file(
                    payload / "quality" / "relationships-quality.json",
                    relationship_quality["quality"],
                    relative_to=payload,
                )
            )
            files.append(
                _write_json_file(
                    payload / "schema" / "relationships.json",
                    _sqlite_schema(Path(relationship_product["database_path"]), "relationship"),
                    relative_to=payload,
                )
            )
            relationship_component = {
                "snapshot_id": relationship_snapshot_id,
                "source_version": relationship_product["product"]["source_version"],
                "product_manifest_sha256": relationship_product["product"]["manifest_sha256"],
                "product_database_sha256": relationship_product["product"]["product_sha256"],
                "record_count": relationship_product["record_count"],
            }

        source_licenses = {
            "sources": [
                {
                    "publisher": "Global Legal Entity Identifier Foundation (GLEIF)",
                    "datasets": [
                        _LEVEL1_DATASET,
                        *([_RR_DATASET] if relationship_component is not None else []),
                    ],
                    "source_data_license": "CC0-1.0",
                    "cc0_url": "https://creativecommons.org/publicdomain/zero/1.0/",
                    "gleif_terms_url": "https://www.gleif.org/en/meta/lei-data-terms-of-use",
                    "trademark_rights_included": False,
                }
            ]
        }
        legal_status = {
            "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
            "source_data_rights": "CC0-1.0",
            "claims_exclusive_rights_over_source_lei_or_le_rd": False,
            "commercial_packaging_complete": True,
            "sale_terms_approved": False,
            "reason": (
                "No restrictive customer licence is generated automatically. "
                "Any vendor-specific terms for protectable packaging/software/services require legal review."
            ),
        }
        files.append(
            _write_json_file(
                payload / "SOURCE_LICENSES.json",
                source_licenses,
                relative_to=payload,
            )
        )
        files.append(
            _write_json_file(
                payload / "LEGAL_STATUS.json",
                legal_status,
                relative_to=payload,
            )
        )
        files.append(
            _write_text_file(
                payload / "NON_AFFILIATION.txt",
                (
                    "This is an independent data product. It is not provided, supported, authorized, "
                    "endorsed, or otherwise affiliated with GLEIF or any Local Operating Unit (LOU).\n"
                    "No GLEIF trademark or logo is included or licensed by this package.\n"
                ),
                relative_to=payload,
            )
        )
        files.append(
            _write_text_file(
                payload / "README.txt",
                (
                    f"{definition['name']}\n\n"
                    f"Product ID: {_PRODUCT_ID}\n"
                    f"Entity source version: {entity_source_version}\n"
                    f"Entity snapshot: {entity_snapshot_id}\n"
                    f"Relationship snapshot: {relationship_snapshot_id or 'not included'}\n"
                    f"Historical change files: {len(history_manifest)}\n\n"
                    "Contents are assembled only from checksum-verified Open Data Platform artifacts.\n"
                    "GLEIF source LEI/LE-RD data are supplied under CC0 1.0.\n"
                    "See SOURCE_LICENSES.json, NON_AFFILIATION.txt, LEGAL_STATUS.json, schema/, quality/, and history/.\n"
                ),
                relative_to=payload,
            )
        )

        manifest = {
            "commercial_package_version": 1,
            "builder_version": _BUILDER_VERSION,
            "product_id": _PRODUCT_ID,
            "product_name": definition["name"],
            "commercial_status": "PRODUCT_CANDIDATE",
            "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
            "entity_component": {
                "snapshot_id": entity_snapshot_id,
                "source_version": entity_source_version,
                "product_manifest_sha256": entity_manifest["product_sha256"],
                "product_database_sha256": entity_manifest["database_sha256"],
                "record_count": entity_product["record_count"],
            },
            "relationship_component": relationship_component,
            "history": history_manifest,
            "formats": {
                "sqlite": True,
                "csv": True,
                "parquet": include_parquet,
            },
            "files": sorted(files, key=lambda item: item["path"]),
            "created_at": utc_now_iso(),
        }
        product_manifest_path = payload / "product.json"
        atomic_write_json(product_manifest_path, manifest)
        manifest_hash, _ = sha256_file(product_manifest_path)

        bundle_path = temporary_dir / "product.zip"
        _deterministic_zip(payload, bundle_path)
        bundle_hash, bundle_size = sha256_file(bundle_path)
        make_read_only(bundle_path)
        _write_checksum(bundle_path)

        outer_manifest = temporary_dir / "product.json"
        atomic_write_json(outer_manifest, manifest)
        make_read_only(outer_manifest)
        outer_manifest_hash = _write_checksum(outer_manifest)
        if outer_manifest_hash != manifest_hash:
            raise ProductError("Commercial inner/outer product manifest identity mismatch")
        shutil.rmtree(payload)
        temporary_dir.rename(final_dir)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        raise

    return {
        "status": "BUILT",
        "product_id": _PRODUCT_ID,
        "entity_snapshot_id": entity_snapshot_id,
        "relationship_snapshot_id": relationship_snapshot_id,
        "commercial_dir": str(final_dir),
        "bundle_path": str(final_dir / "product.zip"),
        "bundle_size_bytes": bundle_size,
        "product": {
            **manifest,
            "manifest_sha256": manifest_hash,
            "bundle_sha256": bundle_hash,
        },
    }


def verify_commercial_product(
    data_root: Path,
    entity_snapshot_id: str,
    *,
    relationship_snapshot_id: str | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    entity_product = verify_product(data_root, entity_snapshot_id)
    verify_quality_profile(data_root, entity_snapshot_id)
    final_dir = commercial_path(data_root, _PRODUCT_ID, entity_snapshot_id, output_root)
    manifest_path = final_dir / "product.json"
    bundle_path = final_dir / "product.zip"
    manifest_hash = _verify_checksum(manifest_path)
    bundle_hash = _verify_checksum(bundle_path)
    manifest = load_json(manifest_path)

    if manifest.get("product_id") != _PRODUCT_ID:
        raise ProductError("Commercial package product identity mismatch")
    if manifest.get("commercial_status") != "PRODUCT_CANDIDATE":
        raise ProductError("Commercial package status mismatch")
    if manifest.get("customer_terms_status") != "LEGAL_REVIEW_REQUIRED":
        raise ProductError("Commercial package lost required legal-review status")
    entity = manifest.get("entity_component") or {}
    if entity.get("snapshot_id") != entity_snapshot_id:
        raise ProductError("Commercial package entity snapshot mismatch")
    if entity.get("product_manifest_sha256") != entity_product["product"]["product_sha256"]:
        raise ProductError("Commercial package entity product manifest mismatch")
    if entity.get("product_database_sha256") != entity_product["product"]["database_sha256"]:
        raise ProductError("Commercial package entity database mismatch")

    relationship = manifest.get("relationship_component")
    if relationship_snapshot_id is None:
        if relationship is not None:
            raise ProductError("Commercial package unexpectedly contains a relationship component")
    else:
        verified_relationship = verify_relationship_product(data_root, relationship_snapshot_id)
        verify_quality_profile(data_root, relationship_snapshot_id)
        if not relationship or relationship.get("snapshot_id") != relationship_snapshot_id:
            raise ProductError("Commercial package relationship snapshot mismatch")
        if relationship.get("product_manifest_sha256") != verified_relationship["product"]["manifest_sha256"]:
            raise ProductError("Commercial package relationship manifest mismatch")
        if relationship.get("product_database_sha256") != verified_relationship["product"]["product_sha256"]:
            raise ProductError("Commercial package relationship database mismatch")

    with zipfile.ZipFile(bundle_path, "r") as bundle:
        names = set(bundle.namelist())
        if "product.json" not in names:
            raise ProductError("Commercial ZIP is missing product.json")
        inner_manifest_bytes = bundle.read("product.json")
        if hashlib.sha256(inner_manifest_bytes).hexdigest() != manifest_hash:
            raise ProductError("Commercial ZIP product manifest differs from outer manifest")
        for file_entry in manifest.get("files", []):
            path = str(file_entry["path"])
            if path not in names:
                raise ProductError(f"Commercial ZIP is missing declared file: {path}")
            payload = bundle.read(path)
            if len(payload) != file_entry["size_bytes"]:
                raise ProductError(f"Commercial ZIP size mismatch for {path}")
            if hashlib.sha256(payload).hexdigest() != file_entry["sha256"]:
                raise ProductError(f"Commercial ZIP checksum mismatch for {path}")

        source_licenses = json.loads(bundle.read("SOURCE_LICENSES.json"))
        legal_status = json.loads(bundle.read("LEGAL_STATUS.json"))
        non_affiliation = bundle.read("NON_AFFILIATION.txt").decode("utf-8")
        if source_licenses["sources"][0]["source_data_license"] != "CC0-1.0":
            raise ProductError("Commercial package source license notice changed")
        if legal_status.get("sale_terms_approved") is not False:
            raise ProductError("Commercial package must not claim unreviewed sale terms are approved")
        if "not provided, supported, authorized" not in non_affiliation:
            raise ProductError("Commercial package non-affiliation notice is incomplete")

    return {
        "status": "VERIFIED",
        "product_id": _PRODUCT_ID,
        "entity_snapshot_id": entity_snapshot_id,
        "relationship_snapshot_id": relationship_snapshot_id,
        "commercial_dir": str(final_dir),
        "bundle_path": str(bundle_path),
        "product": {
            **manifest,
            "manifest_sha256": manifest_hash,
            "bundle_sha256": bundle_hash,
        },
    }
