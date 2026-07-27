from __future__ import annotations

import json
import os
import sqlite3
import stat
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from open_data_platform.commercial import build_commercial_product, verify_commercial_product
from open_data_platform.errors import ProductError


LEVEL1 = "ds_gleif_lei_level1_concat"


def _catalog(root: Path) -> Path:
    path = root / "product.json"
    path.write_text(
        json.dumps(
            {
                "product_id": "prod_global_legal_entity_history",
                "name": "Global Legal Entity History",
                "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
            }
        ),
        encoding="utf-8",
    )
    return path


def _database(root: Path) -> Path:
    path = root / "lei.sqlite"
    connection = sqlite3.connect(path)
    try:
        connection.executescript(
            """
            CREATE TABLE lei (
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                lei TEXT NOT NULL,
                legal_name TEXT NOT NULL
            );
            INSERT INTO lei(lei, legal_name) VALUES
              ('529900T8BM49AURSDO55', 'Alpha AG'),
              ('5493001KJTIIGC8Y1R12', 'Beta GmbH');
            """
        )
        connection.commit()
    finally:
        connection.close()
    return path


class CommercialProductTests(unittest.TestCase):
    def test_builds_verified_product_candidate_with_cc0_and_non_affiliation(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            database = _database(root)
            csv_path = root / "entities.csv"
            csv_path.write_text("row_id,lei,legal_name\n1,529900T8BM49AURSDO55,Alpha AG\n2,5493001KJTIIGC8Y1R12,Beta GmbH\n", encoding="utf-8")
            changes_path = root / "changes.jsonl"
            changes_path.write_text('{"lei":"529900T8BM49AURSDO55","change_types":["LEGAL_NAME_CHANGED"]}\n', encoding="utf-8")

            product = {
                "database_path": str(database),
                "record_count": 2,
                "product": {
                    "source_dataset_id": LEVEL1,
                    "source_version": "2026-07-27",
                    "product_sha256": "entity-manifest-hash",
                    "database_sha256": "entity-database-hash",
                },
            }
            quality = {
                "quality": {
                    "source_snapshot_id": "snp_entity",
                    "source_dataset_id": LEVEL1,
                    "source_version": "2026-07-27",
                    "record_count": 2,
                    "quality_sha256": "quality-hash",
                }
            }
            distribution = {"data_path": str(csv_path)}
            history = [
                {
                    "from_snapshot_id": "snp_old",
                    "to_snapshot_id": "snp_entity",
                    "from_source_version": "2026-07-26",
                    "to_source_version": "2026-07-27",
                    "changes_path": str(changes_path),
                    "changes_sha256": "changes-hash",
                    "change_event_count": 1,
                    "change_type_counts": {"LEGAL_NAME_CHANGED": 1},
                }
            ]
            timeline = {
                "status": "OK",
                "source_dataset_id": LEVEL1,
                "snapshot_count": 2,
                "entries": [],
            }

            with (
                patch("open_data_platform.commercial.verify_product", return_value=product),
                patch("open_data_platform.commercial.verify_quality_profile", return_value=quality),
                patch(
                    "open_data_platform.commercial._ensure_distribution",
                    side_effect=lambda _r, _s, fmt, **_k: distribution if fmt == "csv" else None,
                ),
                patch("open_data_platform.commercial._history_inputs", return_value=(history, timeline)),
            ):
                built = build_commercial_product(
                    root,
                    "snp_entity",
                    catalog_path=_catalog(root),
                )
                self.assertEqual(built["status"], "BUILT")
                self.assertEqual(built["product"]["commercial_status"], "PRODUCT_CANDIDATE")
                self.assertEqual(built["product"]["customer_terms_status"], "LEGAL_REVIEW_REQUIRED")
                self.assertEqual(len(built["product"]["history"]), 1)

                verified = verify_commercial_product(root, "snp_entity")
                self.assertEqual(verified["status"], "VERIFIED")
                self.assertEqual(
                    build_commercial_product(root, "snp_entity", catalog_path=_catalog(root))["status"],
                    "NO_CHANGE",
                )

            with zipfile.ZipFile(built["bundle_path"], "r") as bundle:
                names = set(bundle.namelist())
                self.assertIn("SOURCE_LICENSES.json", names)
                self.assertIn("LEGAL_STATUS.json", names)
                self.assertIn("NON_AFFILIATION.txt", names)
                self.assertIn("history/2026-07-26__2026-07-27.jsonl", names)
                source_licenses = json.loads(bundle.read("SOURCE_LICENSES.json"))
                legal_status = json.loads(bundle.read("LEGAL_STATUS.json"))
                notice = bundle.read("NON_AFFILIATION.txt").decode("utf-8")
                self.assertEqual(source_licenses["sources"][0]["source_data_license"], "CC0-1.0")
                self.assertFalse(source_licenses["sources"][0]["trademark_rights_included"])
                self.assertFalse(legal_status["sale_terms_approved"])
                self.assertIn("not provided, supported, authorized", notice)

    def test_bundle_tampering_is_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            database = _database(root)
            csv_path = root / "entities.csv"
            csv_path.write_text("row_id,lei,legal_name\n1,529900T8BM49AURSDO55,Alpha AG\n2,5493001KJTIIGC8Y1R12,Beta GmbH\n", encoding="utf-8")
            product = {
                "database_path": str(database),
                "record_count": 2,
                "product": {
                    "source_dataset_id": LEVEL1,
                    "source_version": "2026-07-27",
                    "product_sha256": "entity-manifest-hash",
                    "database_sha256": "entity-database-hash",
                },
            }
            quality = {"quality": {"source_snapshot_id": "snp_entity"}}
            with (
                patch("open_data_platform.commercial.verify_product", return_value=product),
                patch("open_data_platform.commercial.verify_quality_profile", return_value=quality),
                patch(
                    "open_data_platform.commercial._ensure_distribution",
                    side_effect=lambda _r, _s, fmt, **_k: {"data_path": str(csv_path)} if fmt == "csv" else None,
                ),
                patch(
                    "open_data_platform.commercial._history_inputs",
                    return_value=([], {"status": "OK", "source_dataset_id": LEVEL1, "snapshot_count": 1, "entries": []}),
                ),
            ):
                built = build_commercial_product(root, "snp_entity", catalog_path=_catalog(root))
                bundle = Path(built["bundle_path"])
                os.chmod(bundle, stat.S_IRUSR | stat.S_IWUSR)
                with bundle.open("ab") as handle:
                    handle.write(b"tamper")
                with self.assertRaises(ProductError):
                    verify_commercial_product(root, "snp_entity")

    def test_relationship_state_cannot_be_newer_than_entity_state(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            database = _database(root)
            csv_path = root / "entities.csv"
            csv_path.write_text("row_id,lei,legal_name\n1,529900T8BM49AURSDO55,Alpha AG\n2,5493001KJTIIGC8Y1R12,Beta GmbH\n", encoding="utf-8")
            entity = {
                "database_path": str(database),
                "record_count": 2,
                "product": {
                    "source_dataset_id": LEVEL1,
                    "source_version": "2026-07-27",
                    "product_sha256": "entity-manifest-hash",
                    "database_sha256": "entity-database-hash",
                },
            }
            relationship = {
                "database_path": str(root / "relationships.sqlite"),
                "record_count": 1,
                "product": {
                    "source_dataset_id": "ds_gleif_rr_level2_concat",
                    "source_version": "2026-07-28",
                    "manifest_sha256": "rr-manifest",
                    "product_sha256": "rr-database",
                },
            }
            with (
                patch("open_data_platform.commercial.verify_product", return_value=entity),
                patch("open_data_platform.commercial.verify_quality_profile", return_value={"quality": {}}),
                patch(
                    "open_data_platform.commercial._ensure_distribution",
                    side_effect=lambda _r, _s, fmt, **_k: {"data_path": str(csv_path)} if fmt == "csv" else None,
                ),
                patch("open_data_platform.commercial._history_inputs", return_value=([], {"entries": []})),
                patch("open_data_platform.commercial.verify_relationship_product", return_value=relationship),
            ):
                with self.assertRaises(ProductError):
                    build_commercial_product(
                        root,
                        "snp_entity",
                        relationship_snapshot_id="snp_rr",
                        catalog_path=_catalog(root),
                    )


if __name__ == "__main__":
    unittest.main()
