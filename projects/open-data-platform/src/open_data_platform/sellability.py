from __future__ import annotations

import json
import re
import shutil
import uuid
from datetime import datetime
from pathlib import Path
from typing import Any

from .commercial import (
    _verify_checksum as _commercial_verify_checksum,
    commercial_path,
    verify_commercial_product,
)
from .errors import ProductError, SellabilityError
from .production import production_acceptance_report
from .ror_commercial import verify_ror_commercial_product
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_SELLABILITY_VERSION = "0.1.1"
_GLEIF_PRODUCT = "prod_global_legal_entity_history"
_ROR_PRODUCT = "prod_global_research_organization_history"
_GLEIF_DATASET = "ds_gleif_lei_level1_concat"
_ROR_EVIDENCE_RELATIVE = Path("docs/live-acceptance/ror-v2.9-v2.10.json")
_ALLOWED_LICENSE_PLANS = {"INTERNAL_COMMERCIAL", "OEM_REDISTRIBUTION"}
_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
_TERMS_ID_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{0,127}$")
_RESERVED_ENVELOPE_FILES = {
    "approval.json",
    "operational.json",
    "sale.json",
    "approval.json.sha256",
    "operational.json.sha256",
    "sale.json.sha256",
}


def _verify_checksum(path: Path) -> str:
    try:
        return _commercial_verify_checksum(path)
    except ProductError as exc:
        raise SellabilityError(f"Sellability artifact checksum verification failed: {path}") from exc


def _catalog_path(project_root: Path, product_id: str) -> Path:
    if product_id == _GLEIF_PRODUCT:
        name = "global_legal_entity_history.json"
    elif product_id == _ROR_PRODUCT:
        name = "global_research_organization_history.json"
    else:
        raise SellabilityError(f"Unknown commercial product: {product_id}")
    return project_root / "catalog" / "products" / name


def _product_definition(project_root: Path, product_id: str) -> dict[str, Any]:
    path = _catalog_path(project_root, product_id)
    if not path.is_file():
        raise SellabilityError(f"Product definition not found: {path}")
    value = load_json(path)
    if value.get("product_id") != product_id:
        raise SellabilityError("Product definition identity mismatch")
    if value.get("status") != "PRODUCT_CANDIDATE":
        raise SellabilityError("Sellability gate requires an immutable PRODUCT_CANDIDATE")
    if value.get("customer_terms_status") != "LEGAL_REVIEW_REQUIRED":
        raise SellabilityError("Product definition lost the legal-review boundary")
    return value


def legal_review_packet(project_root: Path, product_id: str) -> dict[str, Any]:
    definition = _product_definition(project_root, product_id)
    excluded = definition.get("excluded_source_fields") or []
    return {
        "packet_version": 1,
        "packet_type": "customer_terms_legal_review_handoff",
        "product": {
            "product_id": product_id,
            "name": definition["name"],
            "primary_dataset_id": definition["primary_dataset_id"],
            "source_rights": definition["source_rights"],
            "source_terms_url": definition["source_terms_url"],
            "cc0_url": definition["cc0_url"],
            "excluded_source_fields": excluded,
            "delivery_model": definition["delivery_model"],
        },
        "technical_non_negotiables": [
            "Do not claim exclusive ownership of the underlying source facts or identifiers.",
            "Preserve the source-data rights boundary independently from vendor terms for packaging/software/services.",
            "Preserve the required independent/non-affiliation position and do not imply source-publisher endorsement.",
            "Do not require usage metering, telemetry, revenue share, or recurring usage reports.",
            "Do not make the vendor responsible for customer downstream products or customer data custody.",
            *(
                [
                    "Keep the declared excluded source fields out of the customer product unless a separately reviewed rights rule explicitly admits them."
                ]
                if excluded
                else []
            ),
        ],
        "decisions_required_from_human_legal_review": [
            "Approve exact customer-terms document wording for INTERNAL_COMMERCIAL use.",
            "Decide whether OEM_REDISTRIBUTION needs a separate terms document and, if so, approve it separately.",
            "Approve warranty, disclaimer, limitation-of-liability, governing-law, termination, and remedy wording.",
            "Confirm that vendor terms do not contradict customers' underlying source-data rights.",
            "Confirm trademark/non-affiliation wording for this product.",
        ],
        "approval_record_contract": {
            "status": "APPROVED",
            "license_plan": sorted(_ALLOWED_LICENSE_PLANS),
            "required_true": [
                "source_rights_preserved",
                "no_exclusive_source_data_claim",
                "non_affiliation_preserved",
            ],
            "required_false": [
                "usage_metering_required",
                "revenue_share_required",
                "telemetry_required",
            ],
            "required_evidence": [
                "review_reference",
                "approved_at",
                "terms_document_file",
                "terms_document_sha256",
            ],
        },
        "important_boundary": (
            "The platform can verify that a reviewed approval artifact exists and that its terms document hash is bound to a sale envelope. "
            "It cannot verify legal sufficiency or reviewer qualifications."
        ),
    }


def _parse_approved_at(value: Any) -> str:
    if not isinstance(value, str) or not value:
        raise SellabilityError("approved_at must be a non-empty ISO timestamp")
    normalized = value.replace("Z", "+00:00")
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError as exc:
        raise SellabilityError(f"approved_at is not a valid ISO timestamp: {value!r}") from exc
    if parsed.tzinfo is None:
        raise SellabilityError("approved_at must include an explicit timezone")
    return value


def _validate_terms_id(value: Any) -> str:
    if not isinstance(value, str) or _TERMS_ID_RE.fullmatch(value) is None:
        raise SellabilityError(
            "terms_id must be a lowercase safe identifier using letters, digits, dot, underscore, or hyphen"
        )
    return value


def _safe_relative_file(parent: Path, value: Any, *, field: str) -> Path:
    if not isinstance(value, str) or not value:
        raise SellabilityError(f"{field} must be a non-empty relative file path")
    if "/" in value or "\\" in value:
        raise SellabilityError(f"{field} must be one file name in the approval directory")
    relative = Path(value)
    if relative.is_absolute() or ".." in relative.parts or relative.name != value:
        raise SellabilityError(f"{field} must be one file name in the approval directory")
    if value in _RESERVED_ENVELOPE_FILES or value.endswith(".sha256"):
        raise SellabilityError(f"{field} conflicts with a reserved sale-envelope file name")
    path = parent / relative
    if not path.is_file():
        raise SellabilityError(f"Referenced terms document not found: {path}")
    return path


def verify_terms_approval(
    approval_path: Path,
    *,
    product_id: str | None = None,
) -> dict[str, Any]:
    if not approval_path.is_file():
        raise SellabilityError(f"Customer-terms approval record not found: {approval_path}")
    approval = load_json(approval_path)
    if approval.get("approval_record_version") != 1:
        raise SellabilityError("Unsupported customer-terms approval record version")
    if approval.get("status") != "APPROVED":
        raise SellabilityError("Customer-terms approval record is not APPROVED")
    terms_id = _validate_terms_id(approval.get("terms_id"))
    plan = approval.get("license_plan")
    if plan not in _ALLOWED_LICENSE_PLANS:
        raise SellabilityError(f"Unsupported license_plan: {plan!r}")
    products = approval.get("applies_to_product_ids")
    if not isinstance(products, list) or not products or not all(isinstance(item, str) for item in products):
        raise SellabilityError("applies_to_product_ids must be a non-empty list of product IDs")
    if len(products) != len(set(products)):
        raise SellabilityError("applies_to_product_ids must not contain duplicates")
    if product_id is not None and product_id not in products:
        raise SellabilityError(f"Approved terms do not apply to product {product_id}")
    _parse_approved_at(approval.get("approved_at"))
    review_reference = approval.get("review_reference")
    if not isinstance(review_reference, str) or not review_reference.strip():
        raise SellabilityError("review_reference must identify the human legal-review record")

    for field in (
        "source_rights_preserved",
        "no_exclusive_source_data_claim",
        "non_affiliation_preserved",
    ):
        if approval.get(field) is not True:
            raise SellabilityError(f"Approved terms must explicitly set {field}=true")
    for field in (
        "usage_metering_required",
        "revenue_share_required",
        "telemetry_required",
    ):
        if approval.get(field) is not False:
            raise SellabilityError(f"Approved terms must explicitly set {field}=false")

    document_path = _safe_relative_file(
        approval_path.parent,
        approval.get("terms_document_file"),
        field="terms_document_file",
    )
    expected_hash = approval.get("terms_document_sha256")
    if not isinstance(expected_hash, str) or _SHA256_RE.fullmatch(expected_hash) is None:
        raise SellabilityError("terms_document_sha256 must be one lowercase SHA-256 hex digest")
    actual_hash, document_size = sha256_file(document_path)
    if actual_hash != expected_hash:
        raise SellabilityError("Approved customer-terms document hash mismatch")
    approval_hash, approval_size = sha256_file(approval_path)
    return {
        "status": "VERIFIED_APPROVAL_ARTIFACT",
        "terms_id": terms_id,
        "license_plan": plan,
        "applies_to_product_ids": products,
        "approved_at": approval["approved_at"],
        "review_reference": review_reference,
        "terms_document_file": document_path.name,
        "terms_document_path": str(document_path),
        "terms_document_sha256": actual_hash,
        "terms_document_size_bytes": document_size,
        "approval_record_sha256": approval_hash,
        "approval_record_size_bytes": approval_size,
        "approval": approval,
        "boundary": (
            "Hash/invariant verification only; this result does not independently establish legal sufficiency or reviewer qualification."
        ),
    }


def _candidate_manifest(
    data_root: Path,
    product_id: str,
    snapshot_id: str,
) -> tuple[dict[str, Any], dict[str, Any]]:
    candidate_dir = commercial_path(data_root, product_id, snapshot_id)
    manifest_path = candidate_dir / "product.json"
    if not manifest_path.is_file():
        raise SellabilityError(f"Commercial candidate manifest not found: {manifest_path}")
    _verify_checksum(manifest_path)
    manifest = load_json(manifest_path)
    if manifest.get("product_id") != product_id:
        raise SellabilityError("Commercial candidate product identity mismatch")

    if product_id == _GLEIF_PRODUCT:
        relationship = manifest.get("relationship_component")
        relationship_snapshot_id = (
            str(relationship["snapshot_id"])
            if isinstance(relationship, dict) and relationship.get("snapshot_id")
            else None
        )
        verified = verify_commercial_product(
            data_root,
            snapshot_id,
            relationship_snapshot_id=relationship_snapshot_id,
        )
    elif product_id == _ROR_PRODUCT:
        verified = verify_ror_commercial_product(data_root, snapshot_id)
    else:
        raise SellabilityError(f"No commercial verifier registered for {product_id}")

    product = verified["product"]
    if product.get("commercial_status") != "PRODUCT_CANDIDATE":
        raise SellabilityError("Commercial artifact must remain a PRODUCT_CANDIDATE")
    if product.get("customer_terms_status") != "LEGAL_REVIEW_REQUIRED":
        raise SellabilityError("Commercial artifact lost the legal-review gate")
    return manifest, verified


def _ror_logical_candidate_match(
    candidate_manifest: dict[str, Any],
    evidence: dict[str, Any],
) -> dict[str, Any]:
    organization = candidate_manifest.get("organization_component")
    candidate_history = candidate_manifest.get("history")
    evidence_history = evidence.get("history")
    products = evidence.get("products")
    if not isinstance(organization, dict):
        return {"matches": False, "reason": "ROR candidate has no organization_component"}
    if not isinstance(candidate_history, list):
        return {"matches": False, "reason": "ROR candidate has no history list"}
    if not isinstance(evidence_history, dict) or not isinstance(products, dict):
        return {"matches": False, "reason": "ROR operational evidence is missing deterministic product/history inputs"}

    from_version = evidence_history.get("from_source_version")
    to_version = evidence_history.get("to_source_version")
    product_evidence = products.get(to_version) if isinstance(to_version, str) else None
    if not isinstance(product_evidence, dict):
        return {"matches": False, "reason": "ROR operational evidence has no product hash for its accepted target version"}

    matching_pair = next(
        (
            item
            for item in candidate_history
            if isinstance(item, dict)
            and item.get("from_source_version") == from_version
            and item.get("to_source_version") == to_version
        ),
        None,
    )
    expected = {
        "source_version": to_version,
        "product_database_sha256": product_evidence.get("product_sha256"),
        "from_source_version": from_version,
        "to_source_version": to_version,
        "changes_sha256": evidence_history.get("changes_sha256"),
        "artifact_sha256": evidence_history.get("artifact_sha256"),
    }
    candidate = {
        "source_version": organization.get("source_version"),
        "product_database_sha256": organization.get("product_database_sha256"),
        "from_source_version": matching_pair.get("from_source_version") if isinstance(matching_pair, dict) else None,
        "to_source_version": matching_pair.get("to_source_version") if isinstance(matching_pair, dict) else None,
        "changes_sha256": matching_pair.get("changes_sha256") if isinstance(matching_pair, dict) else None,
        "artifact_sha256": matching_pair.get("artifact_sha256") if isinstance(matching_pair, dict) else None,
    }
    matches = all(isinstance(value, str) and value for value in expected.values()) and candidate == expected
    return {
        "matches": matches,
        "expected": expected,
        "candidate": candidate,
    }


def _operational_acceptance(
    *,
    project_root: Path,
    data_root: Path,
    product_id: str,
    candidate_manifest: dict[str, Any] | None = None,
    candidate_bundle_sha256: str | None = None,
) -> dict[str, Any]:
    if product_id == _GLEIF_PRODUCT:
        report = production_acceptance_report(
            data_root,
            required_versions=7,
            source_dataset_id=_GLEIF_DATASET,
        )
        return {
            "status": report["status"],
            "evidence_type": "gleif_distinct_source_version_production_window",
            "report": report,
        }

    if product_id == _ROR_PRODUCT:
        evidence_path = project_root / _ROR_EVIDENCE_RELATIVE
        if not evidence_path.is_file():
            return {
                "status": "IN_PROGRESS",
                "evidence_type": "ror_real_v2.9_v2.10_out_of_order_acceptance",
                "reason": f"Evidence file not found: {evidence_path}",
            }
        evidence_hash, evidence_size = sha256_file(evidence_path)
        wrapper = load_json(evidence_path)
        evidence = wrapper.get("evidence")
        commercial_evidence = evidence.get("commercial_candidate") if isinstance(evidence, dict) else None
        structural_pass = (
            wrapper.get("status") == "PASS"
            and wrapper.get("exit_code") == 0
            and isinstance(evidence, dict)
            and evidence.get("status") == "PASS"
            and isinstance(commercial_evidence, dict)
            and commercial_evidence.get("status") == "PRODUCT_CANDIDATE"
            and commercial_evidence.get("customer_terms_status") == "LEGAL_REVIEW_REQUIRED"
            and int(commercial_evidence.get("adjacent_pair_count", 0)) >= 1
            and (evidence.get("history") or {}).get("matches_published_updated_existing") is True
        )

        if isinstance(candidate_manifest, dict):
            logical_match = _ror_logical_candidate_match(candidate_manifest, evidence if isinstance(evidence, dict) else {})
            passed = structural_pass and logical_match.get("matches") is True
            return {
                "status": "PASS" if passed else "ALERT",
                "evidence_type": "ror_real_v2.9_v2.10_out_of_order_acceptance",
                "evidence_path": str(evidence_path),
                "evidence_sha256": evidence_hash,
                "evidence_size_bytes": evidence_size,
                "logical_candidate_match": logical_match,
                "evidence": wrapper,
            }

        # Compatibility for the original focused unit fixture only. Real sellability
        # evaluation always supplies candidate_manifest and therefore uses the
        # deterministic product/history binding above.
        evidence_bundle_hash = (
            commercial_evidence.get("bundle_sha256")
            if isinstance(commercial_evidence, dict)
            else None
        )
        passed = (
            structural_pass
            and isinstance(candidate_bundle_sha256, str)
            and evidence_bundle_hash == candidate_bundle_sha256
        )
        return {
            "status": "PASS" if passed else "ALERT",
            "evidence_type": "ror_real_v2.9_v2.10_out_of_order_acceptance",
            "evidence_path": str(evidence_path),
            "evidence_sha256": evidence_hash,
            "evidence_size_bytes": evidence_size,
            "candidate_bundle_sha256": candidate_bundle_sha256,
            "evidence_candidate_bundle_sha256": evidence_bundle_hash,
            "candidate_bundle_match": evidence_bundle_hash == candidate_bundle_sha256,
            "evidence": wrapper,
        }

    raise SellabilityError(f"No operational acceptance gate registered for {product_id}")


def sellability_report(
    *,
    project_root: Path,
    data_root: Path,
    product_id: str,
    snapshot_id: str,
    terms_approval_path: Path | None = None,
) -> dict[str, Any]:
    definition = _product_definition(project_root, product_id)
    candidate_manifest, candidate = _candidate_manifest(data_root, product_id, snapshot_id)
    operational = _operational_acceptance(
        project_root=project_root,
        data_root=data_root,
        product_id=product_id,
        candidate_manifest=candidate_manifest,
    )
    approval = (
        verify_terms_approval(terms_approval_path, product_id=product_id)
        if terms_approval_path is not None
        else None
    )

    operational_pass = operational["status"] == "PASS"
    legal_artifact_present = approval is not None
    if operational_pass and legal_artifact_present:
        status = "TECHNICALLY_READY_FOR_SALE"
    elif operational_pass:
        status = "BLOCKED_LEGAL_REVIEW"
    elif legal_artifact_present:
        status = "BLOCKED_OPERATIONAL_ACCEPTANCE"
    else:
        status = "BLOCKED_LEGAL_AND_OPERATIONAL"

    return {
        "status": status,
        "sellability_version": 1,
        "product_id": product_id,
        "product_name": definition["name"],
        "snapshot_id": snapshot_id,
        "candidate": {
            "commercial_status": candidate["product"]["commercial_status"],
            "customer_terms_status": candidate["product"]["customer_terms_status"],
            "manifest_sha256": candidate["product"]["manifest_sha256"],
            "bundle_sha256": candidate["product"]["bundle_sha256"],
            "bundle_path": candidate["bundle_path"],
            "source_rights": definition["source_rights"],
            "excluded_source_fields": candidate_manifest.get("excluded_source_fields", []),
        },
        "operational_acceptance": operational,
        "terms_approval": approval,
        "boundary": (
            "TECHNICALLY_READY_FOR_SALE means the platform verified the candidate, required operational evidence, and a human-supplied approved terms artifact with matching hashes. "
            "It is not an independent legal opinion."
        ),
    }


def sale_envelope_path(
    data_root: Path,
    product_id: str,
    snapshot_id: str,
    terms_id: str,
) -> Path:
    safe_terms_id = _validate_terms_id(terms_id)
    return data_root / "sale-envelopes" / product_id / snapshot_id / safe_terms_id


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def build_sale_envelope(
    *,
    project_root: Path,
    data_root: Path,
    product_id: str,
    snapshot_id: str,
    terms_approval_path: Path,
) -> dict[str, Any]:
    report = sellability_report(
        project_root=project_root,
        data_root=data_root,
        product_id=product_id,
        snapshot_id=snapshot_id,
        terms_approval_path=terms_approval_path,
    )
    if report["status"] != "TECHNICALLY_READY_FOR_SALE":
        raise SellabilityError(f"Sale envelope is blocked: {report['status']}")
    approval = report["terms_approval"]
    terms_id = _validate_terms_id(approval["terms_id"])
    final_dir = sale_envelope_path(data_root, product_id, snapshot_id, terms_id)
    if final_dir.exists():
        verified = verify_sale_envelope(
            project_root=project_root,
            data_root=data_root,
            product_id=product_id,
            snapshot_id=snapshot_id,
            terms_id=terms_id,
        )
        verified["status"] = "NO_CHANGE"
        return verified

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{terms_id}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    try:
        approval_copy = temporary_dir / "approval.json"
        shutil.copy2(terms_approval_path, approval_copy)
        document_name = str(approval["terms_document_file"])
        document_copy = temporary_dir / document_name
        shutil.copy2(Path(approval["terms_document_path"]), document_copy)
        copied_approval = verify_terms_approval(approval_copy, product_id=product_id)
        approval_hash = _write_checksum(approval_copy)
        document_hash = _write_checksum(document_copy)
        if approval_hash != approval["approval_record_sha256"]:
            raise SellabilityError("Copied approval record hash changed during sale-envelope build")
        if document_hash != approval["terms_document_sha256"]:
            raise SellabilityError("Copied customer-terms document hash changed during sale-envelope build")

        operational_path = temporary_dir / "operational.json"
        atomic_write_json(operational_path, report["operational_acceptance"])
        operational_hash = _write_checksum(operational_path)
        make_read_only(operational_path)

        sale = {
            "sale_envelope_version": 1,
            "builder_version": _SELLABILITY_VERSION,
            "status": "TECHNICALLY_READY_FOR_SALE",
            "product_id": product_id,
            "snapshot_id": snapshot_id,
            "candidate_manifest_sha256": report["candidate"]["manifest_sha256"],
            "candidate_bundle_sha256": report["candidate"]["bundle_sha256"],
            "terms_id": terms_id,
            "license_plan": copied_approval["license_plan"],
            "approval_record_file": "approval.json",
            "approval_record_sha256": approval_hash,
            "terms_document_file": document_name,
            "terms_document_sha256": document_hash,
            "operational_evidence_file": "operational.json",
            "operational_evidence_sha256": operational_hash,
            "source_rights_preserved": True,
            "no_exclusive_source_data_claim": True,
            "non_affiliation_preserved": True,
            "usage_metering_required": False,
            "revenue_share_required": False,
            "telemetry_required": False,
            "created_at": utc_now_iso(),
            "boundary": (
                "Technical sale-readiness binding only. Legal sufficiency and reviewer qualification remain human responsibilities."
            ),
        }
        sale_path = temporary_dir / "sale.json"
        atomic_write_json(sale_path, sale)
        make_read_only(sale_path)
        sale_hash = _write_checksum(sale_path)
        make_read_only(approval_copy)
        make_read_only(document_copy)
        temporary_dir.rename(final_dir)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        raise

    return {
        "status": "BUILT",
        "product_id": product_id,
        "snapshot_id": snapshot_id,
        "terms_id": terms_id,
        "sale_envelope_dir": str(final_dir),
        "sale": {**sale, "sale_sha256": sale_hash},
    }


def _verify_operational_snapshot(
    product_id: str,
    operational: dict[str, Any],
    *,
    candidate_manifest: dict[str, Any] | None = None,
) -> None:
    if operational.get("status") != "PASS":
        raise SellabilityError("Stored operational evidence is not PASS")
    if product_id == _GLEIF_PRODUCT:
        report = operational.get("report")
        if not isinstance(report, dict) or report.get("status") != "PASS":
            raise SellabilityError("Stored GLEIF production evidence is invalid")
        if int(report.get("observed_distinct_source_versions", 0)) < int(
            report.get("required_distinct_source_versions", 7)
        ):
            raise SellabilityError("Stored GLEIF production window is incomplete")
    elif product_id == _ROR_PRODUCT:
        wrapper = operational.get("evidence")
        evidence = wrapper.get("evidence") if isinstance(wrapper, dict) else None
        commercial = evidence.get("commercial_candidate") if isinstance(evidence, dict) else None
        if not isinstance(wrapper, dict) or wrapper.get("status") != "PASS":
            raise SellabilityError("Stored ROR acceptance wrapper is invalid")
        if not isinstance(evidence, dict) or evidence.get("status") != "PASS":
            raise SellabilityError("Stored ROR acceptance evidence is invalid")
        if (evidence.get("history") or {}).get("matches_published_updated_existing") is not True:
            raise SellabilityError("Stored ROR real-history acceptance is incomplete")
        if not isinstance(commercial, dict) or int(commercial.get("adjacent_pair_count", 0)) < 1:
            raise SellabilityError("Stored ROR commercial acceptance has no history pair")

        has_logical_candidate = (
            isinstance(candidate_manifest, dict)
            and isinstance(candidate_manifest.get("organization_component"), dict)
            and isinstance(candidate_manifest.get("history"), list)
        )
        if has_logical_candidate:
            logical_match = _ror_logical_candidate_match(candidate_manifest, evidence)
            if logical_match.get("matches") is not True:
                raise SellabilityError("Stored ROR operational evidence is bound to different deterministic product/history inputs")
            if operational.get("logical_candidate_match") != logical_match:
                raise SellabilityError("Stored ROR logical candidate binding does not match the verified evidence")
        else:
            # Compatibility for the original isolated unit fixture. A real verified
            # ROR commercial manifest always has organization_component + history.
            if commercial.get("bundle_sha256") != operational.get("candidate_bundle_sha256"):
                raise SellabilityError("Stored ROR operational evidence is bound to a different candidate bundle")
    else:
        raise SellabilityError(f"No operational evidence verifier registered for {product_id}")


def verify_sale_envelope(
    *,
    project_root: Path,
    data_root: Path,
    product_id: str,
    snapshot_id: str,
    terms_id: str,
) -> dict[str, Any]:
    safe_terms_id = _validate_terms_id(terms_id)
    _product_definition(project_root, product_id)
    candidate_manifest, candidate = _candidate_manifest(data_root, product_id, snapshot_id)
    final_dir = sale_envelope_path(data_root, product_id, snapshot_id, safe_terms_id)
    sale_path = final_dir / "sale.json"
    sale_hash = _verify_checksum(sale_path)
    sale = load_json(sale_path)
    if sale.get("status") != "TECHNICALLY_READY_FOR_SALE":
        raise SellabilityError("Sale envelope has the wrong status")
    if sale.get("product_id") != product_id or sale.get("snapshot_id") != snapshot_id:
        raise SellabilityError("Sale envelope product/snapshot identity mismatch")
    if sale.get("terms_id") != safe_terms_id:
        raise SellabilityError("Sale envelope terms identity mismatch")
    if sale.get("candidate_manifest_sha256") != candidate["product"]["manifest_sha256"]:
        raise SellabilityError("Sale envelope candidate manifest hash mismatch")
    if sale.get("candidate_bundle_sha256") != candidate["product"]["bundle_sha256"]:
        raise SellabilityError("Sale envelope candidate bundle hash mismatch")
    if sale.get("approval_record_file") != "approval.json":
        raise SellabilityError("Sale envelope approval file name mismatch")
    if sale.get("operational_evidence_file") != "operational.json":
        raise SellabilityError("Sale envelope operational evidence file name mismatch")

    approval_path = final_dir / "approval.json"
    approval = verify_terms_approval(approval_path, product_id=product_id)
    approval_hash = _verify_checksum(approval_path)
    if approval_hash != sale.get("approval_record_sha256"):
        raise SellabilityError("Sale envelope approval-record hash mismatch")
    if approval["terms_document_sha256"] != sale.get("terms_document_sha256"):
        raise SellabilityError("Sale envelope customer-terms document hash mismatch")
    if approval["license_plan"] != sale.get("license_plan"):
        raise SellabilityError("Sale envelope license-plan mismatch")
    document_path = final_dir / approval["terms_document_file"]
    document_hash = _verify_checksum(document_path)
    if document_hash != sale.get("terms_document_sha256"):
        raise SellabilityError("Sale envelope terms-document sidecar mismatch")

    operational_path = final_dir / "operational.json"
    operational_hash = _verify_checksum(operational_path)
    if operational_hash != sale.get("operational_evidence_sha256"):
        raise SellabilityError("Sale envelope operational-evidence hash mismatch")
    operational = load_json(operational_path)
    _verify_operational_snapshot(
        product_id,
        operational,
        candidate_manifest=candidate_manifest,
    )

    for field in (
        "source_rights_preserved",
        "no_exclusive_source_data_claim",
        "non_affiliation_preserved",
    ):
        if sale.get(field) is not True:
            raise SellabilityError(f"Sale envelope lost required invariant {field}=true")
    for field in (
        "usage_metering_required",
        "revenue_share_required",
        "telemetry_required",
    ):
        if sale.get(field) is not False:
            raise SellabilityError(f"Sale envelope lost required invariant {field}=false")

    return {
        "status": "VERIFIED",
        "product_id": product_id,
        "snapshot_id": snapshot_id,
        "terms_id": safe_terms_id,
        "sale_envelope_dir": str(final_dir),
        "sale": {**sale, "sale_sha256": sale_hash},
        "terms_approval": approval,
        "operational_acceptance": operational,
    }
