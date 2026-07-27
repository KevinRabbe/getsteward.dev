from __future__ import annotations

import hashlib
import json
import os
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.errors import SellabilityError
from open_data_platform.sellability import (
    _GLEIF_PRODUCT,
    _ROR_PRODUCT,
    _operational_acceptance,
    build_sale_envelope,
    legal_review_packet,
    sale_envelope_path,
    sellability_report,
    verify_sale_envelope,
    verify_terms_approval,
)


def _write_product_definition(root: Path, product_id: str) -> None:
    catalog = root / "catalog" / "products"
    catalog.mkdir(parents=True, exist_ok=True)
    if product_id == _GLEIF_PRODUCT:
        name = "global_legal_entity_history.json"
        primary_dataset_id = "ds_gleif_lei_level1_concat"
        excluded = None
    else:
        name = "global_research_organization_history.json"
        primary_dataset_id = "ds_ror_organizations"
        excluded = ["locations"]
    payload = {
        "product_id": product_id,
        "name": "Test Product",
        "status": "PRODUCT_CANDIDATE",
        "primary_dataset_id": primary_dataset_id,
        "source_rights": "CC0-1.0",
        "source_terms_url": "https://example.test/terms",
        "cc0_url": "https://creativecommons.org/publicdomain/zero/1.0/",
        "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
        "delivery_model": "immutable_downloadable_bundle",
    }
    if excluded is not None:
        payload["excluded_source_fields"] = excluded
    (catalog / name).write_text(json.dumps(payload), encoding="utf-8")


def _write_approval(
    root: Path,
    *,
    product_id: str,
    terms_id: str = "internal-commercial-v1",
    status: str = "APPROVED",
    document_name: str = "customer-terms-v1.txt",
    document_text: str = "Reviewed customer terms placeholder for test only.\n",
    license_plan: str = "INTERNAL_COMMERCIAL",
) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    document = root / document_name
    if "/" not in document_name and "\\" not in document_name:
        document.write_text(document_text, encoding="utf-8")
        digest = hashlib.sha256(document.read_bytes()).hexdigest()
    else:
        digest = hashlib.sha256(document_text.encode("utf-8")).hexdigest()
    approval = {
        "approval_record_version": 1,
        "status": status,
        "terms_id": terms_id,
        "license_plan": license_plan,
        "applies_to_product_ids": [product_id],
        "approved_at": "2026-07-27T12:00:00+02:00",
        "review_reference": "legal-review-test-reference",
        "terms_document_file": document_name,
        "terms_document_sha256": digest,
        "source_rights_preserved": True,
        "no_exclusive_source_data_claim": True,
        "non_affiliation_preserved": True,
        "usage_metering_required": False,
        "revenue_share_required": False,
        "telemetry_required": False,
    }
    path = root / "approval-source.json"
    path.write_text(json.dumps(approval), encoding="utf-8")
    return path


def _candidate(bundle_hash: str = "b" * 64) -> tuple[dict, dict]:
    manifest = {
        "product_id": _ROR_PRODUCT,
        "excluded_source_fields": ["locations"],
    }
    verified = {
        "bundle_path": "/tmp/product.zip",
        "product": {
            "commercial_status": "PRODUCT_CANDIDATE",
            "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
            "manifest_sha256": "a" * 64,
            "bundle_sha256": bundle_hash,
        },
    }
    return manifest, verified


def _ror_operational(bundle_hash: str = "b" * 64) -> dict:
    return {
        "status": "PASS",
        "evidence_type": "ror_real_v2.9_v2.10_out_of_order_acceptance",
        "candidate_bundle_sha256": bundle_hash,
        "evidence": {
            "status": "PASS",
            "exit_code": 0,
            "evidence": {
                "status": "PASS",
                "history": {"matches_published_updated_existing": True},
                "commercial_candidate": {
                    "status": "PRODUCT_CANDIDATE",
                    "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
                    "adjacent_pair_count": 1,
                    "bundle_sha256": bundle_hash,
                },
            },
        },
    }


class SellabilityTests(unittest.TestCase):
    def test_legal_review_packet_is_factual_not_approval(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _write_product_definition(root, _ROR_PRODUCT)
            packet = legal_review_packet(root, _ROR_PRODUCT)
            self.assertEqual(packet["packet_type"], "customer_terms_legal_review_handoff")
            self.assertEqual(packet["product"]["source_rights"], "CC0-1.0")
            self.assertEqual(packet["product"]["excluded_source_fields"], ["locations"])
            self.assertIn("It cannot verify legal sufficiency", packet["important_boundary"])
            self.assertEqual(packet["approval_record_contract"]["status"], "APPROVED")

    def test_repository_example_is_deliberately_rejected(self):
        project_root = Path(__file__).resolve().parents[1]
        example = project_root / "catalog" / "legal" / "customer_terms_approval.example.json"
        with self.assertRaises(SellabilityError):
            verify_terms_approval(example, product_id=_GLEIF_PRODUCT)

    def test_valid_approval_binds_exact_terms_document_and_product(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            approval_path = _write_approval(root, product_id=_ROR_PRODUCT)
            verified = verify_terms_approval(approval_path, product_id=_ROR_PRODUCT)
            self.assertEqual(verified["status"], "VERIFIED_APPROVAL_ARTIFACT")
            self.assertEqual(verified["terms_id"], "internal-commercial-v1")
            self.assertEqual(verified["license_plan"], "INTERNAL_COMMERCIAL")
            self.assertEqual(len(verified["terms_document_sha256"]), 64)

            with self.assertRaises(SellabilityError):
                verify_terms_approval(approval_path, product_id=_GLEIF_PRODUCT)

            Path(verified["terms_document_path"]).write_text("changed\n", encoding="utf-8")
            with self.assertRaises(SellabilityError):
                verify_terms_approval(approval_path, product_id=_ROR_PRODUCT)

    def test_approval_rejects_path_traversal_reserved_files_and_unsafe_terms_id(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for terms_id in ("..", ".", "../terms", "UpperCase", "bad space"):
                approval_path = _write_approval(
                    root / terms_id.replace("/", "_"),
                    product_id=_ROR_PRODUCT,
                    terms_id=terms_id,
                )
                with self.subTest(terms_id=terms_id):
                    with self.assertRaises(SellabilityError):
                        verify_terms_approval(approval_path, product_id=_ROR_PRODUCT)

            reserved = _write_approval(
                root / "reserved",
                product_id=_ROR_PRODUCT,
                document_name="sale.json",
            )
            with self.assertRaises(SellabilityError):
                verify_terms_approval(reserved, product_id=_ROR_PRODUCT)

            traversal_dir = root / "traversal"
            traversal_dir.mkdir()
            outside = root / "outside.txt"
            outside.write_text("reviewed\n", encoding="utf-8")
            digest = hashlib.sha256(outside.read_bytes()).hexdigest()
            approval = {
                "approval_record_version": 1,
                "status": "APPROVED",
                "terms_id": "safe-v1",
                "license_plan": "INTERNAL_COMMERCIAL",
                "applies_to_product_ids": [_ROR_PRODUCT],
                "approved_at": "2026-07-27T12:00:00+02:00",
                "review_reference": "test",
                "terms_document_file": "../outside.txt",
                "terms_document_sha256": digest,
                "source_rights_preserved": True,
                "no_exclusive_source_data_claim": True,
                "non_affiliation_preserved": True,
                "usage_metering_required": False,
                "revenue_share_required": False,
                "telemetry_required": False,
            }
            path = traversal_dir / "approval-source.json"
            path.write_text(json.dumps(approval), encoding="utf-8")
            with self.assertRaises(SellabilityError):
                verify_terms_approval(path, product_id=_ROR_PRODUCT)

    def test_ror_operational_gate_requires_exact_candidate_bundle_hash(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            evidence_dir = root / "docs" / "live-acceptance"
            evidence_dir.mkdir(parents=True)
            evidence = _ror_operational("b" * 64)["evidence"]
            (evidence_dir / "ror-v2.9-v2.10.json").write_text(
                json.dumps(evidence),
                encoding="utf-8",
            )

            matching = _operational_acceptance(
                project_root=root,
                data_root=root / "data",
                product_id=_ROR_PRODUCT,
                candidate_bundle_sha256="b" * 64,
            )
            self.assertEqual(matching["status"], "PASS")
            self.assertTrue(matching["candidate_bundle_match"])

            mismatching = _operational_acceptance(
                project_root=root,
                data_root=root / "data",
                product_id=_ROR_PRODUCT,
                candidate_bundle_sha256="c" * 64,
            )
            self.assertEqual(mismatching["status"], "ALERT")
            self.assertFalse(mismatching["candidate_bundle_match"])

    def test_sellability_statuses_do_not_confuse_legal_and_operational_gates(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _write_product_definition(root, _ROR_PRODUCT)
            approval_path = _write_approval(root / "approval", product_id=_ROR_PRODUCT)
            candidate = _candidate()

            with (
                patch("open_data_platform.sellability._candidate_manifest", return_value=candidate),
                patch("open_data_platform.sellability._operational_acceptance", return_value=_ror_operational()),
            ):
                blocked_legal = sellability_report(
                    project_root=root,
                    data_root=root / "data",
                    product_id=_ROR_PRODUCT,
                    snapshot_id="snp_210",
                )
                ready = sellability_report(
                    project_root=root,
                    data_root=root / "data",
                    product_id=_ROR_PRODUCT,
                    snapshot_id="snp_210",
                    terms_approval_path=approval_path,
                )
            self.assertEqual(blocked_legal["status"], "BLOCKED_LEGAL_REVIEW")
            self.assertEqual(ready["status"], "TECHNICALLY_READY_FOR_SALE")
            self.assertIn("not an independent legal opinion", ready["boundary"])

            with (
                patch("open_data_platform.sellability._candidate_manifest", return_value=candidate),
                patch(
                    "open_data_platform.sellability._operational_acceptance",
                    return_value={"status": "IN_PROGRESS", "evidence_type": "test"},
                ),
            ):
                blocked_operational = sellability_report(
                    project_root=root,
                    data_root=root / "data",
                    product_id=_ROR_PRODUCT,
                    snapshot_id="snp_210",
                    terms_approval_path=approval_path,
                )
            self.assertEqual(blocked_operational["status"], "BLOCKED_OPERATIONAL_ACCEPTANCE")

    def test_gleif_incomplete_seven_version_gate_blocks_sale(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _write_product_definition(root, _GLEIF_PRODUCT)
            approval_path = _write_approval(root / "approval", product_id=_GLEIF_PRODUCT)
            manifest, verified = _candidate()
            manifest["product_id"] = _GLEIF_PRODUCT
            with (
                patch("open_data_platform.sellability._candidate_manifest", return_value=(manifest, verified)),
                patch(
                    "open_data_platform.sellability.production_acceptance_report",
                    return_value={
                        "status": "IN_PROGRESS",
                        "required_distinct_source_versions": 7,
                        "observed_distinct_source_versions": 3,
                    },
                ),
            ):
                report = sellability_report(
                    project_root=root,
                    data_root=root / "data",
                    product_id=_GLEIF_PRODUCT,
                    snapshot_id="snp_gleif",
                    terms_approval_path=approval_path,
                )
            self.assertEqual(report["status"], "BLOCKED_OPERATIONAL_ACCEPTANCE")

    def test_sale_envelope_binds_candidate_terms_and_operational_evidence(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data_root = root / "data"
            _write_product_definition(root, _ROR_PRODUCT)
            approval_path = _write_approval(root / "approval", product_id=_ROR_PRODUCT)
            approval = verify_terms_approval(approval_path, product_id=_ROR_PRODUCT)
            candidate = _candidate()
            ready_report = {
                "status": "TECHNICALLY_READY_FOR_SALE",
                "candidate": {
                    "manifest_sha256": candidate[1]["product"]["manifest_sha256"],
                    "bundle_sha256": candidate[1]["product"]["bundle_sha256"],
                },
                "operational_acceptance": _ror_operational(),
                "terms_approval": approval,
            }

            with (
                patch("open_data_platform.sellability.sellability_report", return_value=ready_report),
                patch("open_data_platform.sellability._candidate_manifest", return_value=candidate),
            ):
                built = build_sale_envelope(
                    project_root=root,
                    data_root=data_root,
                    product_id=_ROR_PRODUCT,
                    snapshot_id="snp_210",
                    terms_approval_path=approval_path,
                )
                self.assertEqual(built["status"], "BUILT")
                self.assertEqual(built["sale"]["candidate_bundle_sha256"], "b" * 64)
                self.assertEqual(built["sale"]["terms_document_sha256"], approval["terms_document_sha256"])
                verified = verify_sale_envelope(
                    project_root=root,
                    data_root=data_root,
                    product_id=_ROR_PRODUCT,
                    snapshot_id="snp_210",
                    terms_id="internal-commercial-v1",
                )
                self.assertEqual(verified["status"], "VERIFIED")
                self.assertEqual(
                    build_sale_envelope(
                        project_root=root,
                        data_root=data_root,
                        product_id=_ROR_PRODUCT,
                        snapshot_id="snp_210",
                        terms_approval_path=approval_path,
                    )["status"],
                    "NO_CHANGE",
                )

                sale_path = Path(built["sale_envelope_dir"]) / "sale.json"
                os.chmod(sale_path, stat.S_IRUSR | stat.S_IWUSR)
                sale_path.write_text(sale_path.read_text(encoding="utf-8") + " ", encoding="utf-8")
                with self.assertRaises(SellabilityError):
                    verify_sale_envelope(
                        project_root=root,
                        data_root=data_root,
                        product_id=_ROR_PRODUCT,
                        snapshot_id="snp_210",
                        terms_id="internal-commercial-v1",
                    )

    def test_sale_envelope_path_rejects_traversal_terms_id(self):
        with self.assertRaises(SellabilityError):
            sale_envelope_path(Path("data"), _ROR_PRODUCT, "snp", "..")


if __name__ == "__main__":
    unittest.main()
