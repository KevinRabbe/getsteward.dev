from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.analytics import (
    _ror_profile,
    build_quality_profile,
    quality_timeline,
    verify_quality_profile,
)


DATASET = "ds_ror_organizations"


def _write_snapshot(
    root: Path,
    snapshot_id: str,
    source_version: str,
    publication_date: str,
) -> None:
    directory = root / "archive" / "snapshots" / snapshot_id
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "manifest.json").write_text(
        json.dumps(
            {
                "snapshot_id": snapshot_id,
                "source_dataset_id": DATASET,
                "source_version": source_version,
                "acquisition_response": {"publication_date": publication_date},
            }
        ),
        encoding="utf-8",
    )


def _profile(source_version: str, records: int) -> dict:
    return {
        "source_dataset_id": DATASET,
        "source_version": source_version,
        "record_count": records,
        "status_counts": {"active": records},
        "organization_type_counts": {"education": records},
        "relationship_type_counts": {"parent": 3},
        "records_with_excluded_locations": records,
        "excluded_source_fields": ["locations"],
        "product_manifest_sha256": f"manifest-{source_version}",
        "product_database_sha256": f"database-{source_version}",
        "normalized_artifact_sha256": f"normalized-{source_version}",
    }


class RorAnalyticsTests(unittest.TestCase):
    def test_ror_history_uses_publication_dates_not_lexical_version_order(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _write_snapshot(root, "snp_29", "v2.9", "2026-06-23")
            _write_snapshot(root, "snp_210", "v2.10", "2026-07-20")
            profiles = {
                "snp_29": _profile("v2.9", 129_046),
                "snp_210": _profile("v2.10", 132_537),
            }

            with patch(
                "open_data_platform.analytics._ror_profile",
                side_effect=lambda _root, snapshot_id, **_kwargs: profiles[snapshot_id],
            ):
                first = build_quality_profile(root, "snp_29")
                second = build_quality_profile(root, "snp_210")

            self.assertEqual(first["quality"]["source_publication_date"], "2026-06-23")
            comparison = second["quality"]["previous_comparison"]
            self.assertEqual(comparison["previous_source_version"], "v2.9")
            self.assertEqual(comparison["previous_source_publication_date"], "2026-06-23")
            self.assertEqual(comparison["record_count_delta"], 3_491)
            self.assertEqual(verify_quality_profile(root, "snp_210")["status"], "VERIFIED")

            timeline = quality_timeline(root, DATASET)
            self.assertEqual(
                [entry["source_version"] for entry in timeline["entries"]],
                ["v2.9", "v2.10"],
            )
            self.assertEqual(timeline["first_source_publication_date"], "2026-06-23")
            self.assertEqual(timeline["latest_source_publication_date"], "2026-07-20")
            self.assertEqual(
                timeline["entries"][-1]["organization_type_counts"],
                {"education": 132_537},
            )

    def test_ror_profile_preserves_field_exclusion_and_quality_counts(self):
        product = {
            "record_count": 132_537,
            "product": {
                "source_version": "v2.10",
                "manifest_sha256": "manifest-hash",
                "product_sha256": "database-hash",
                "excluded_source_fields": ["locations"],
            },
        }
        normalized = {
            "quality": {
                "status_counts": {"active": 130_000, "inactive": 2_537},
                "organization_type_counts": {"education": 50_000},
                "relationship_type_counts": {"parent": 12_000},
                "records_with_excluded_locations": 132_000,
            },
            "artifact": {"artifact_sha256": "normalized-hash"},
        }
        with (
            patch("open_data_platform.analytics.verify_ror_product", return_value=product),
            patch("open_data_platform.analytics.verify_ror_artifact", return_value=normalized),
        ):
            profile = _ror_profile(
                Path("data"),
                "snp_ror",
                product_root=None,
                normalized_root=None,
            )

        self.assertEqual(profile["record_count"], 132_537)
        self.assertEqual(profile["status_counts"]["inactive"], 2_537)
        self.assertEqual(profile["records_with_excluded_locations"], 132_000)
        self.assertEqual(profile["excluded_source_fields"], ["locations"])
        self.assertEqual(profile["product_database_sha256"], "database-hash")


if __name__ == "__main__":
    unittest.main()
