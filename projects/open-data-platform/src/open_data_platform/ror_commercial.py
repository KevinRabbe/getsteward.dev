from __future__ import annotations

import hashlib
import json
import shutil
import uuid
import zipfile
from pathlib import Path
from typing import Any

from .analytics import quality_timeline, verify_quality_profile
from .commercial import (
    _copy_verified_file,
    _sqlite_schema,
    _verify_checksum,
    _write_checksum,
    _write_json_file,
    _write_text_file,
    commercial_path,
)
from .distributions import (
    build_csv_distribution,
    build_parquet_distribution,
    verify_distribution,
)
from .errors import ProductError
from .release import _deterministic_zip
from .ror_changes import verify_ror_changes
from .ror_product import verify_ror_product
from .source_order import source_order_key
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_BUILDER_VERSION = "0.1.0"
_PRODUCT_ID = "prod_global_research_organization_history"
_DATASET_ID = "ds_ror_organizations"
_EXCLUDED_SOURCE_FIELDS = ["locations"]


def _product_definition(catalog_path: Path | None) -> dict[str, Any]:
    path = catalog_path or Path(
        "catalog/products/global_research_organization_history.json"
    )
    if not path.is_file():
        raise ProductError(f"ROR commercial product definition not found: {path}")
    value = load_json(path)
    if value.get("product_id") != _PRODUCT_ID:
        raise ProductError("ROR commercial product definition has unexpected product_id")
    if value.get("customer_terms_status") != "LEGAL_REVIEW_REQUIRED":
        raise ProductError("ROR commercial product definition lost the legal-review gate")
    if value.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ProductError("ROR commercial product definition lost the locations exclusion")
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


def _snapshot_manifest(data_root: Path, snapshot_id: str) -> dict[str, Any]:
    manifest = load_json(
        data_root / "archive" / "snapshots" / snapshot_id / "manifest.json"
    )
    if manifest.get("snapshot_id") != snapshot_id:
        raise ProductError("ROR commercial snapshot manifest identity mismatch")
    if manifest.get("source_dataset_id") != _DATASET_ID:
        raise ProductError("ROR commercial snapshot belongs to a different dataset")
    return manifest


def _history_inputs(
    data_root: Path,
    snapshot_id: str,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    target_snapshot = _snapshot_manifest(data_root, snapshot_id)
    target_order = source_order_key(target_snapshot)
    timeline = quality_timeline(data_root, _DATASET_ID)
    entries = [
        entry
        for entry in timeline["entries"]
        if (
            str(entry["source_publication_date"]),
            str(entry["source_version"]),
        )
        <= target_order
    ]
    entries.sort(
        key=lambda entry: (
            str(entry["source_publication_date"]),
            str(entry["source_version"]),
            str(entry["source_snapshot_id"]),
        )
    )
    if not entries or entries[-1]["source_snapshot_id"] != snapshot_id:
        raise ProductError("ROR commercial timeline does not end at the selected snapshot")
    if len(entries) < 2:
        raise ProductError(
            "ROR history product candidate requires at least two verified source states"
        )

    history: list[dict[str, Any]] = []
    for before, after in zip(entries, entries[1:]):
        verified = verify_ror_changes(
            data_root,
            str(before["source_snapshot_id"]),
            str(after["source_snapshot_id"]),
        )
        artifact = verified["artifact"]
        if artifact.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
            raise ProductError("ROR commercial history lost the locations exclusion")
        history.append(
            {
                "from_snapshot_id": verified["from_snapshot_id"],
                "to_snapshot_id": verified["to_snapshot_id"],
                "from_source_version": artifact["from_source_version"],
                "to_source_version": artifact["to_source_version"],
                "from_source_publication_date": artifact[
                    "from_source_publication_date"
                ],
                "to_source_publication_date": artifact["to_source_publication_date"],
                "changes_path": verified["changes_path"],
                "changes_sha256": artifact["changes_sha256"],
                "change_event_count": artifact["change_event_count"],
                "change_type_counts": artifact["change_type_counts"],
                "artifact_sha256": artifact["artifact_sha256"],
            }
        )
    return history, {**timeline, "entries": entries, "snapshot_count": len(entries)}


def build_ror_commercial_product(
    data_root: Path,
    snapshot_id: str,
    *,
    include_parquet: bool = False,
    catalog_path: Path | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    definition = _product_definition(catalog_path)
    product = verify_ror_product(data_root, snapshot_id)
    product_manifest = product["product"]
    if product_manifest.get("source_dataset_id") != _DATASET_ID:
        raise ProductError("ROR commercial product requires the ROR organizations dataset")
    if product_manifest.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ProductError("ROR commercial source product lost the locations exclusion")
    quality = verify_quality_profile(data_root, snapshot_id)
    csv_distribution = _ensure_distribution(
        data_root, snapshot_id, "csv", include_parquet=include_parquet
    )
    parquet_distribution = _ensure_distribution(
        data_root, snapshot_id, "parquet", include_parquet=include_parquet
    )
    history, timeline = _history_inputs(data_root, snapshot_id)

    final_dir = commercial_path(data_root, _PRODUCT_ID, snapshot_id, output_root)
    if final_dir.exists():
        verified = verify_ror_commercial_product(
            data_root,
            snapshot_id,
            output_root=output_root,
        )
        verified["status"] = "NO_CHANGE"
        return verified

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{snapshot_id}.tmp-{uuid.uuid4().hex}"
    payload = temporary_dir / "payload"
    payload.mkdir(parents=True, exist_ok=False)
    files: list[dict[str, Any]] = []
    try:
        files.append(
            _copy_verified_file(
                Path(product["database_path"]),
                payload / "data" / "organizations.sqlite",
                relative_to=payload,
            )
        )
        files.append(
            _copy_verified_file(
                Path(csv_distribution["data_path"]),
                payload / "data" / "organizations.csv",
                relative_to=payload,
            )
        )
        if parquet_distribution is not None:
            files.append(
                _copy_verified_file(
                    Path(parquet_distribution["data_path"]),
                    payload / "data" / "organizations.parquet",
                    relative_to=payload,
                )
            )
        files.append(
            _write_json_file(
                payload / "quality" / "organizations-quality.json",
                quality["quality"],
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
                payload / "schema" / "organizations.json",
                _sqlite_schema(Path(product["database_path"]), "organization"),
                relative_to=payload,
            )
        )

        history_manifest: list[dict[str, Any]] = []
        for item in history:
            filename = (
                f"{item['from_source_version']}__{item['to_source_version']}.jsonl"
            )
            copied = _copy_verified_file(
                Path(item["changes_path"]),
                payload / "history" / filename,
                relative_to=payload,
            )
            files.append(copied)
            history_manifest.append({**item, "package_path": copied["path"]})

        source_licenses = {
            "sources": [
                {
                    "publisher": "Research Organization Registry (ROR)",
                    "dataset": _DATASET_ID,
                    "source_data_license": "CC0-1.0",
                    "cc0_url": "https://creativecommons.org/publicdomain/zero/1.0/",
                    "ror_terms_url": "https://ror.org/about/terms/",
                    "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
                    "geonames_derived_location_data_included": False,
                    "ror_logo_or_trademark_rights_included": False,
                }
            ]
        }
        field_policy = {
            "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
            "raw_archive_preserves_source_locations": True,
            "normalized_product_contains_locations": False,
            "commercial_bundle_contains_locations": False,
            "reason": (
                "ROR location data is derived from GeoNames and carries a separate "
                "attribution/licensing boundary. This candidate intentionally does not "
                "redistribute those fields."
            ),
        }
        legal_status = {
            "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
            "source_data_rights": "CC0-1.0",
            "claims_exclusive_rights_over_source_ror_ids_or_metadata": False,
            "commercial_packaging_complete": True,
            "sale_terms_approved": False,
            "reason": (
                "No restrictive customer licence is generated automatically. "
                "Vendor-specific terms for protectable packaging/software/services "
                "require legal review and must preserve the source-data rights boundary."
            ),
        }
        files.append(
            _write_json_file(
                payload / "SOURCE_LICENSES.json", source_licenses, relative_to=payload
            )
        )
        files.append(
            _write_json_file(
                payload / "FIELD_POLICY.json", field_policy, relative_to=payload
            )
        )
        files.append(
            _write_json_file(
                payload / "LEGAL_STATUS.json", legal_status, relative_to=payload
            )
        )
        files.append(
            _write_text_file(
                payload / "NON_AFFILIATION.txt",
                (
                    "This is an independent data product. It is not provided, supported, "
                    "authorized, endorsed, or otherwise affiliated with the Research "
                    "Organization Registry (ROR) or its governing organizations.\n"
                    "No ROR logo is included or licensed by this package.\n"
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
                    f"ROR source version: {product_manifest['source_version']}\n"
                    f"ROR snapshot: {snapshot_id}\n"
                    f"Historical change files: {len(history_manifest)}\n\n"
                    "Contents are assembled only from checksum-verified Open Data Platform artifacts.\n"
                    "ROR IDs and included metadata are supplied under CC0 1.0.\n"
                    "Source locations are deliberately excluded from this product.\n"
                    "See SOURCE_LICENSES.json, FIELD_POLICY.json, NON_AFFILIATION.txt, "
                    "LEGAL_STATUS.json, schema/, quality/, and history/.\n"
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
            "organization_component": {
                "snapshot_id": snapshot_id,
                "source_version": product_manifest["source_version"],
                "product_manifest_sha256": product_manifest["manifest_sha256"],
                "product_database_sha256": product_manifest["product_sha256"],
                "record_count": product["record_count"],
                "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
            },
            "history": history_manifest,
            "history_coverage": {
                "first_source_version": history_manifest[0]["from_source_version"],
                "first_source_publication_date": history_manifest[0][
                    "from_source_publication_date"
                ],
                "latest_source_version": product_manifest["source_version"],
                "latest_source_publication_date": timeline[
                    "latest_source_publication_date"
                ],
                "adjacent_pair_count": len(history_manifest),
                "complete_across_packaged_states": True,
            },
            "formats": {
                "sqlite": True,
                "csv": True,
                "parquet": include_parquet,
            },
            "excluded_source_fields": _EXCLUDED_SOURCE_FIELDS,
            "files": sorted(files, key=lambda item: item["path"]),
            "created_at": utc_now_iso(),
        }
        inner_manifest = payload / "product.json"
        atomic_write_json(inner_manifest, manifest)
        manifest_hash, _ = sha256_file(inner_manifest)

        bundle_path = temporary_dir / "product.zip"
        _deterministic_zip(payload, bundle_path)
        bundle_hash, bundle_size = sha256_file(bundle_path)
        make_read_only(bundle_path)
        _write_checksum(bundle_path)

        outer_manifest = temporary_dir / "product.json"
        atomic_write_json(outer_manifest, manifest)
        make_read_only(outer_manifest)
        if _write_checksum(outer_manifest) != manifest_hash:
            raise ProductError("ROR commercial inner/outer manifest identity mismatch")
        shutil.rmtree(payload)
        temporary_dir.rename(final_dir)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        raise

    return {
        "status": "BUILT",
        "product_id": _PRODUCT_ID,
        "snapshot_id": snapshot_id,
        "commercial_dir": str(final_dir),
        "bundle_path": str(final_dir / "product.zip"),
        "bundle_size_bytes": bundle_size,
        "product": {
            **manifest,
            "manifest_sha256": manifest_hash,
            "bundle_sha256": bundle_hash,
        },
    }


def verify_ror_commercial_product(
    data_root: Path,
    snapshot_id: str,
    *,
    output_root: Path | None = None,
) -> dict[str, Any]:
    product = verify_ror_product(data_root, snapshot_id)
    verify_quality_profile(data_root, snapshot_id)
    csv_distribution = verify_distribution(data_root, snapshot_id, "csv")

    final_dir = commercial_path(data_root, _PRODUCT_ID, snapshot_id, output_root)
    manifest_path = final_dir / "product.json"
    bundle_path = final_dir / "product.zip"
    manifest_hash = _verify_checksum(manifest_path)
    bundle_hash = _verify_checksum(bundle_path)
    manifest = load_json(manifest_path)

    if manifest.get("product_id") != _PRODUCT_ID:
        raise ProductError("ROR commercial package product identity mismatch")
    if manifest.get("commercial_status") != "PRODUCT_CANDIDATE":
        raise ProductError("ROR commercial package status mismatch")
    if manifest.get("customer_terms_status") != "LEGAL_REVIEW_REQUIRED":
        raise ProductError("ROR commercial package lost required legal-review status")
    if manifest.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ProductError("ROR commercial package lost the locations exclusion")
    component = manifest.get("organization_component") or {}
    product_manifest = product["product"]
    if component.get("snapshot_id") != snapshot_id:
        raise ProductError("ROR commercial package snapshot mismatch")
    if component.get("product_manifest_sha256") != product_manifest["manifest_sha256"]:
        raise ProductError("ROR commercial package product manifest mismatch")
    if component.get("product_database_sha256") != product_manifest["product_sha256"]:
        raise ProductError("ROR commercial package product database mismatch")
    if component.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
        raise ProductError("ROR commercial organization component lost field exclusions")

    if manifest.get("formats", {}).get("parquet"):
        verify_distribution(data_root, snapshot_id, "parquet")
    if manifest.get("formats", {}).get("csv") is not True:
        raise ProductError("ROR commercial package must contain CSV")
    if csv_distribution["distribution"].get("source_snapshot_id") != snapshot_id:
        raise ProductError("ROR commercial CSV distribution identity mismatch")

    history = manifest.get("history")
    if not isinstance(history, list) or not history:
        raise ProductError("ROR commercial package has no verified history")
    for item in history:
        verified = verify_ror_changes(
            data_root,
            str(item["from_snapshot_id"]),
            str(item["to_snapshot_id"]),
        )
        if item.get("changes_sha256") != verified["artifact"]["changes_sha256"]:
            raise ProductError("ROR commercial history hash mismatch")
        if verified["artifact"].get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
            raise ProductError("ROR commercial history lost the locations exclusion")

    with zipfile.ZipFile(bundle_path, "r") as bundle:
        names = set(bundle.namelist())
        if "product.json" not in names:
            raise ProductError("ROR commercial ZIP is missing product.json")
        inner_manifest = bundle.read("product.json")
        if hashlib.sha256(inner_manifest).hexdigest() != manifest_hash:
            raise ProductError("ROR commercial ZIP product manifest differs from outer manifest")
        for file_entry in manifest.get("files", []):
            path = str(file_entry["path"])
            if path not in names:
                raise ProductError(f"ROR commercial ZIP is missing declared file: {path}")
            payload = bundle.read(path)
            if len(payload) != file_entry["size_bytes"]:
                raise ProductError(f"ROR commercial ZIP size mismatch for {path}")
            if hashlib.sha256(payload).hexdigest() != file_entry["sha256"]:
                raise ProductError(f"ROR commercial ZIP checksum mismatch for {path}")

        source_licenses = json.loads(bundle.read("SOURCE_LICENSES.json"))
        field_policy = json.loads(bundle.read("FIELD_POLICY.json"))
        legal_status = json.loads(bundle.read("LEGAL_STATUS.json"))
        non_affiliation = bundle.read("NON_AFFILIATION.txt").decode("utf-8")
        source = source_licenses["sources"][0]
        if source.get("source_data_license") != "CC0-1.0":
            raise ProductError("ROR commercial source license notice changed")
        if source.get("geonames_derived_location_data_included") is not False:
            raise ProductError("ROR commercial package must not include GeoNames-derived locations")
        if field_policy.get("commercial_bundle_contains_locations") is not False:
            raise ProductError("ROR commercial field policy unexpectedly includes locations")
        if field_policy.get("excluded_source_fields") != _EXCLUDED_SOURCE_FIELDS:
            raise ProductError("ROR commercial field policy lost excluded fields")
        if legal_status.get("sale_terms_approved") is not False:
            raise ProductError("ROR commercial package must not claim unreviewed sale terms are approved")
        if legal_status.get("claims_exclusive_rights_over_source_ror_ids_or_metadata") is not False:
            raise ProductError("ROR commercial package cannot claim exclusive source-data rights")
        if "not provided, supported, authorized" not in non_affiliation:
            raise ProductError("ROR commercial non-affiliation notice is incomplete")

    return {
        "status": "VERIFIED",
        "product_id": _PRODUCT_ID,
        "snapshot_id": snapshot_id,
        "commercial_dir": str(final_dir),
        "bundle_path": str(bundle_path),
        "product": {
            **manifest,
            "manifest_sha256": manifest_hash,
            "bundle_sha256": bundle_hash,
        },
    }
