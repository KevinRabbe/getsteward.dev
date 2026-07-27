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

from open_data_platform.errors import ProductError
from open_data_platform.ror_commercial import (
    _history_inputs,
    build_ror_commercial_product,
    verify_ror_commercial_product,
)


DATASET = "ds_ror_organizations"
PRODUCT_ID = "prod_global_research_organization_history"


def _catalog(root: Path) -> Path:
    path = root / "catalog.json"
    path.write_text(
        json.dumps(
            {
                "product_id": PRODUCT_ID,
                "name": "Global Research Organization History",
                "customer_terms_status": "LEGAL_REVIEW_REQUIRED",
                "excluded_source_fields": ["locations"],
            }
        ),
        encoding="utf-8",
    )
    return path


def _database(root: Path) -> Path:
    path = root / "organizations.sqlite"
    connection = sqlite3.connect(path)
    try:
        connection.executescript(
            """
            CREATE TABLE organization (
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                ror_id TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                status TEXT NOT NULL
            );
            INSERT INTO organization(ror_id, display_name, status) VALUES
              ('https://ror.org/03yrm5c26', 'Example University', 'active'),
              ('https://ror.org/05dxps055', 'Example Institute', 'inactive');
            """
        )
        connection.commit()
    finally:
        connection.close()
    return path


def _fixtures(root: Path) -> tuple[dict, dict, dict, list[dict], dict, dict]:
    database = _database(root)
    csv_path = root / "organizations.csv"
    csv_path.write_text(
        "row_id,ror_id,display_name,status\n"
        "1,https://ror.org/03yrm5c26,Example University,active\n"
        "2,https://ror.org/05dxps055,Example Institute,inactive\n",
        encoding="utf-8",
    )
    changes_path = root / "v2.9__v2.10.jsonl"
    changes_path.write_text(
        '{"ror_id":"https://ror.org/03yrm5c26","change_types":["DISPLAY_NAME_CHANGED"]}\n',
        encoding="utf-8",
    )
    product = {
        "database_path": str(database),
        "record_count": 2,
        "product": {
            "source_dataset_id": DATASET,
            "source_version": "v2.10",
            "product_type": "ror_organizations_sqlite",
            "manifest_sha256": "ror-manifest-hash",
            "product_sha256": "ror-database-hash",
            "excluded_source_fields": ["locations"],
        },
    }
    quality = {
        "quality": {
            "source_snapshot_id": "snp_210",
            "source_dataset_id": DATASET,
            "source_version": "v2.10",
            "source_publication_date": "2026-07-20",
            "quality_sha256": "quality-hash",
        }
    }
    distribution = {
        "data_path": str(csv_path),
        "distribution": {"source_snapshot_id": "snp_210"},
    }
    history = [
        {
            "from_snapshot_id": "snp_29",
            "to_snapshot_id": "snp_210",
            "from_source_version": "v2.9",
            "to_source_version": "v2.10",
            "from_source_publication_date": "2026-06-23",
            "to_source_publication_date": "2026-07-20",
            "changes_path": str(changes_path),
            "changes_sha256": "changes-hash",
            "change_event_count": 3,
            "change_type_counts": {"NEW": 1, "DISPLAY_NAME_CHANGED": 2},
            "artifact_sha256": "change-artifact-hash",
        }
    ]
    timeline = {
        "status": "OK",
        "source_dataset_id": DATASET,
        "snapshot_count": 2,
        "first_source_version": "v2.9",
        "latest_source_version": "v2.10",
        "first_source_publication_date": "2026-06-23",
        "latest_source_publication_date": "2026-07-20",
        "entries": [],
    }
    change_verification = {
        "artifact": {
            "changes_sha256": "changes-hash",
            "excluded_source_fields": ["locations"],
        }
    }
    return product, quality, distribution, history, timeline, change_verification


class RorCommercialTests(unittest.TestCase):
    def test_builds_verified_history_candidate_with_cc0_and_field_policy(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            product, quality, distribution, history, timeline, change_verification = _fixtures(root)
            with (
                patch("open_data_platform.ror_commercial.verify_ror_product", return_value=product),
                patch("open_data_platform.ror_commercial.verify_quality_profile", return_value=quality),
                patch(
                    "open_data_platform.ror_commercial._ensure_distribution",
                    side_effect=lambda _r, _s, fmt, **_k: distribution if fmt == "csv" else None,
                ),
                patch("open_data_platform.ror_commercial._history_inputs", return_value=(history, timeline)),
                patch("open_data_platform.ror_commercial.verify_distribution", return_value=distribution),
                patch("open_data_platform.ror_commercial.verify_ror_changes", return_value=change_verification),
            ):
                built = build_ror_commercial_product(
                    root,
                    "snp_210",
                    catalog_path=_catalog(root),
                )
                self.assertEqual(built["status"], "BUILT")
                self.assertEqual(built["product"]["commercial_status"], "PRODUCT_CANDIDATE")
                self.assertEqual(built["product"]["customer_terms_status"], "LEGAL_REVIEW_REQUIRED")
                self.assertEqual(built["product"]["excluded_source_fields"], ["locations"])
                self.assertEqual(built["product"]["history_coverage"]["adjacent_pair_count"], 1)

                verified = verify_ror_commercial_product(root, "snp_210")
                self.assertEqual(verified["status"], "VERIFIED")
                self.assertEqual(
                    build_ror_commercial_product(
                        root,
                        "snp_210",
                        catalog_path=_catalog(root),
                    )["status"],
                    "NO_CHANGE",
                )

            with zipfile.ZipFile(built["bundle_path"], "r") as bundle:
                names = set(bundle.namelist())
                self.assertIn("SOURCE_LICENSES.json", names)
                self.assertIn("FIELD_POLICY.json", names)
                self.assertIn("LEGAL_STATUS.json", names)
                self.assertIn("NON_AFFILIATION.txt", names)
                self.assertIn("history/v2.9__v2.10.jsonl", names)
                source = json.loads(bundle.read("SOURCE_LICENSES.json"))["sources"][0]
                field_policy = json.loads(bundle.read("FIELD_POLICY.json"))
                legal_status = json.loads(bundle.read("LEGAL_STATUS.json"))
                notice = bundle.read("NON_AFFILIATION.txt").decode("utf-8")
                self.assertEqual(source["source_data_license"], "CC0-1.0")
                self.assertFalse(source["geonames_derived_location_data_included"])
                self.assertEqual(source["excluded_source_fields"], ["locations"])
                self.assertFalse(field_policy["commercial_bundle_contains_locations"])
                self.assertFalse(legal_status["sale_terms_approved"])
                self.assertFalse(legal_status["claims_exclusive_rights_over_source_ror_ids_or_metadata"])
                self.assertIn("not provided, supported, authorized", notice)

    def test_commercial_bundle_tampering_is_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            product, quality, distribution, history, timeline, change_verification = _fixtures(root)
            with (
                patch("open_data_platform.ror_commercial.verify_ror_product", return_value=product),
                patch("open_data_platform.ror_commercial.verify_quality_profile", return_value=quality),
                patch(
                    "open_data_platform.ror_commercial._ensure_distribution",
                    side_effect=lambda _r, _s, fmt, **_k: distribution if fmt == "csv" else None,
                ),
                patch("open_data_platform.ror_commercial._history_inputs", return_value=(history, timeline)),
                patch("open_data_platform.ror_commercial.verify_distribution", return_value=distribution),
                patch("open_data_platform.ror_commercial.verify_ror_changes", return_value=change_verification),
            ):
                built = build_ror_commercial_product(root, "snp_210", catalog_path=_catalog(root))
                bundle = Path(built["bundle_path"])
                os.chmod(bundle, stat.S_IRUSR | stat.S_IWUSR)
                with bundle.open("ab") as handle:
                    handle.write(b"tamper")
                with self.assertRaises(ProductError):
                    verify_ror_commercial_product(root, "snp_210")

    def test_history_input_requires_complete_adjacent_chain(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            snapshot_dir = root / "archive" / "snapshots" / "snp_210"
            snapshot_dir.mkdir(parents=True)
            (snapshot_dir / "manifest.json").write_text(
                json.dumps(
                    {
                        "snapshot_id": "snp_210",
                        "source_dataset_id": DATASET,
                        "source_version": "v2.10",
                        "acquisition_response": {"publication_date": "2026-07-20"},
                    }
                ),
                encoding="utf-8",
            )
            timeline = {
                "entries": [
                    {
                        "source_snapshot_id": "snp_29",
                        "source_version": "v2.9",
                        "source_publication_date": "2026-06-23",
                    },
                    {
                        "source_snapshot_id": "snp_210",
                        "source_version": "v2.10",
                        "source_publication_date": "2026-07-20",
                    },
                ]
            }
            verified_change = {
                "from_snapshot_id": "snp_29",
                "to_snapshot_id": "snp_210",
                "changes_path": str(root / "changes.jsonl"),
                "artifact": {
                    "from_source_version": "v2.9",
                    "to_source_version": "v2.10",
                    "from_source_publication_date": "2026-06-23",
                    "to_source_publication_date": "2026-07-20",
                    "changes_sha256": "hash",
                    "change_event_count": 1,
                    "change_type_counts": {"NEW": 1},
                    "artifact_sha256": "artifact-hash",
                    "excluded_source_fields": ["locations"],
                },
            }
            with (
                patch("open_data_platform.ror_commercial.quality_timeline", return_value=timeline),
                patch("open_data_platform.ror_commercial.verify_ror_changes", return_value=verified_change) as verify_change,
            ):
                history, packaged_timeline = _history_inputs(root, "snp_210")
            self.assertEqual(len(history), 1)
            self.assertEqual(packaged_timeline["snapshot_count"], 2)
            verify_change.assert_called_once_with(root, "snp_29", "snp_210")

            with patch(
                "open_data_platform.ror_commercial.quality_timeline",
                return_value={
                    "entries": [
                        {
                            "source_snapshot_id": "snp_210",
                            "source_version": "v2.10",
                            "source_publication_date": "2026-07-20",
                        }
                    ]
                },
            ):
                with self.assertRaises(ProductError):
                    _history_inputs(root, "snp_210")


if __name__ == "__main__":
    unittest.main()
