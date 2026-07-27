from __future__ import annotations

import json
import os
import sqlite3
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.analytics import (
    _level1_profile,
    build_quality_profile,
    quality_timeline,
    verify_quality_profile,
)
from open_data_platform.errors import ProductError


DATASET = "ds_gleif_lei_level1_concat"


def _write_snapshot(root: Path, snapshot_id: str, source_version: str) -> None:
    directory = root / "archive" / "snapshots" / snapshot_id
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "manifest.json").write_text(
        json.dumps(
            {
                "snapshot_id": snapshot_id,
                "source_dataset_id": DATASET,
                "source_version": source_version,
            }
        ),
        encoding="utf-8",
    )


def _profile(source_version: str, *, records: int, unique: int, duplicates: int) -> dict:
    return {
        "source_dataset_id": DATASET,
        "source_version": source_version,
        "record_count": records,
        "unique_lei_count": unique,
        "duplicate_lei_count": duplicates,
        "duplicate_rate": duplicates / records if records else 0.0,
        "entity_status_counts": {"ACTIVE": records},
        "registration_status_counts": {"ISSUED": records},
        "missing_optional_field_counts": {},
        "legal_jurisdiction_counts": {"DE": records},
        "entity_category_counts": {"GENERAL": records},
        "legal_form_code_counts": {"GMBH": records},
        "product_manifest_sha256": f"manifest-{source_version}",
        "product_database_sha256": f"database-{source_version}",
        "normalized_artifact_sha256": f"normalized-{source_version}",
    }


class AnalyticsTests(unittest.TestCase):
    def test_quality_profiles_are_immutable_and_form_a_timeline(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _write_snapshot(root, "snp_1", "2026-07-26")
            _write_snapshot(root, "snp_2", "2026-07-27")
            profiles = {
                "snp_1": _profile("2026-07-26", records=100, unique=99, duplicates=1),
                "snp_2": _profile("2026-07-27", records=110, unique=108, duplicates=2),
            }

            with (
                patch("open_data_platform.analytics._level1_profile", side_effect=lambda _r, sid, **_k: profiles[sid]),
                patch("open_data_platform.analytics._find_incoming_change_summary", return_value=None),
            ):
                first = build_quality_profile(root, "snp_1")
                second = build_quality_profile(root, "snp_2")

            self.assertEqual(first["status"], "BUILT")
            self.assertEqual(second["quality"]["previous_comparison"]["record_count_delta"], 10)
            self.assertEqual(second["quality"]["previous_comparison"]["unique_lei_count_delta"], 9)
            self.assertEqual(second["quality"]["previous_comparison"]["duplicate_lei_count_delta"], 1)
            self.assertEqual(verify_quality_profile(root, "snp_2")["status"], "VERIFIED")

            timeline = quality_timeline(root, DATASET)
            self.assertEqual(timeline["snapshot_count"], 2)
            self.assertEqual([entry["source_version"] for entry in timeline["entries"]], ["2026-07-26", "2026-07-27"])
            self.assertEqual(build_quality_profile(root, "snp_2")["status"], "NO_CHANGE")

    def test_level1_profile_counts_jurisdiction_category_and_legal_form(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            database = root / "lei.sqlite"
            connection = sqlite3.connect(database)
            try:
                connection.executescript(
                    """
                    CREATE TABLE lei (
                        row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        legal_jurisdiction TEXT,
                        entity_category TEXT,
                        legal_form_json TEXT
                    );
                    INSERT INTO lei(legal_jurisdiction, entity_category, legal_form_json) VALUES
                      ('DE', 'GENERAL', '{"entity_legal_form_code":"GMBH"}'),
                      ('DE', 'GENERAL', '{"entity_legal_form_code":"AG"}'),
                      ('FR', NULL, NULL);
                    """
                )
                connection.commit()
            finally:
                connection.close()

            product = {
                "database_path": str(database),
                "record_count": 3,
                "product": {
                    "source_version": "2026-07-27",
                    "unique_lei_count": 2,
                    "duplicate_lei_count": 1,
                    "product_sha256": "manifest-hash",
                    "database_sha256": "database-hash",
                },
            }
            normalized = {
                "quality": {
                    "entity_status_counts": {"ACTIVE": 3},
                    "registration_status_counts": {"ISSUED": 3},
                    "missing_optional_field_counts": {"legal_form": 1},
                },
                "artifact": {"artifact_sha256": "normalized-hash"},
            }
            with (
                patch("open_data_platform.analytics.verify_product", return_value=product),
                patch("open_data_platform.analytics.verify_normalized_artifact", return_value=normalized),
            ):
                profile = _level1_profile(root, "snp_test", product_root=None, normalized_root=None)

            self.assertEqual(profile["legal_jurisdiction_counts"], {"DE": 2, "FR": 1})
            self.assertEqual(profile["entity_category_counts"], {"<NULL>": 1, "GENERAL": 2})
            self.assertEqual(profile["legal_form_code_counts"], {"<NULL>": 1, "AG": 1, "GMBH": 1})
            self.assertEqual(profile["duplicate_rate"], 1 / 3)

    def test_quality_tampering_is_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _write_snapshot(root, "snp_1", "2026-07-27")
            with (
                patch("open_data_platform.analytics._level1_profile", return_value=_profile("2026-07-27", records=10, unique=10, duplicates=0)),
                patch("open_data_platform.analytics._find_incoming_change_summary", return_value=None),
            ):
                built = build_quality_profile(root, "snp_1")
            path = Path(built["analytics_dir"]) / "quality.json"
            os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)
            path.write_text(path.read_text(encoding="utf-8") + " ", encoding="utf-8")
            with self.assertRaises(ProductError):
                verify_quality_profile(root, "snp_1")


if __name__ == "__main__":
    unittest.main()
