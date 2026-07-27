from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.ror_pipeline import run_ror_pipeline


class RorPipelineTests(unittest.TestCase):
    def _fixtures(self, root: Path) -> dict[str, dict]:
        snapshot_id = "snp_ror"
        return {
            "ingest": {
                "status": "ARCHIVED",
                "snapshot": {
                    "snapshot_id": snapshot_id,
                    "source_dataset_id": "ds_ror_organizations",
                    "source_version": "v2.10",
                    "acquisition_response": {
                        "publication_date": "2026-07-20",
                        "bytes_downloaded": 100,
                    },
                    "content": {"original_size_bytes": 100},
                },
            },
            "parse": {
                "status": "PARSED",
                "artifact": {
                    "source_dataset_id": "ds_ror_organizations",
                    "records_size_bytes": 200,
                    "record_count": 10,
                },
            },
            "product": {
                "status": "BUILT",
                "record_count": 10,
                "product": {
                    "source_dataset_id": "ds_ror_organizations",
                    "source_version": "v2.10",
                    "database_size_bytes": 300,
                    "record_count": 10,
                },
            },
            "release": {
                "status": "BUILT",
                "bundle_path": str(root / "release.zip"),
                "bundle_size_bytes": 400,
            },
            "changes": {
                "status": "NO_ADJACENT_SNAPSHOT",
                "snapshot_id": snapshot_id,
                "pair_count": 0,
                "pairs": [],
            },
            "analytics": {
                "status": "BUILT",
                "snapshot_id": snapshot_id,
                "quality": {
                    "source_dataset_id": "ds_ror_organizations",
                    "source_version": "v2.10",
                },
            },
        }

    def test_pipeline_builds_changes_quality_and_records_both_stages(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fixture = self._fixtures(root)
            with (
                patch("open_data_platform.ror_pipeline.ingest_latest_ror", return_value=fixture["ingest"]),
                patch("open_data_platform.ror_pipeline.parse_ror_snapshot", return_value=fixture["parse"]),
                patch("open_data_platform.ror_pipeline.build_ror_product", return_value=fixture["product"]),
                patch("open_data_platform.ror_pipeline.build_ror_release", return_value=fixture["release"]),
                patch("open_data_platform.ror_pipeline.build_ror_changes_around", return_value=fixture["changes"]),
                patch("open_data_platform.ror_pipeline.build_quality_profile", return_value=fixture["analytics"]),
            ):
                result = run_ror_pipeline(
                    source_config=root / "source.json",
                    admission_config=root / "admission.json",
                    data_root=root,
                )

            self.assertEqual(result["status"], "COMPLETED")
            self.assertEqual(result["changes"]["status"], "NO_ADJACENT_SNAPSHOT")
            self.assertEqual(result["analytics"]["status"], "BUILT")
            self.assertEqual(result["metrics"]["source_dataset_id"], "ds_ror_organizations")
            self.assertEqual(result["metrics"]["source_version"], "v2.10")
            self.assertEqual(
                set(result["metrics"]["stage_seconds"]),
                {"ingest", "parse", "product", "release", "changes", "analytics"},
            )

    def test_explicit_backfill_uses_requested_zenodo_record(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fixture = self._fixtures(root)
            with (
                patch("open_data_platform.ror_pipeline.ingest_latest_ror") as latest,
                patch("open_data_platform.ror_pipeline.ingest_ror_record", return_value=fixture["ingest"]) as explicit,
                patch("open_data_platform.ror_pipeline.parse_ror_snapshot", return_value=fixture["parse"]),
                patch("open_data_platform.ror_pipeline.build_ror_product", return_value=fixture["product"]),
                patch("open_data_platform.ror_pipeline.build_ror_release", return_value=fixture["release"]),
                patch("open_data_platform.ror_pipeline.build_ror_changes_around", return_value=fixture["changes"]),
                patch("open_data_platform.ror_pipeline.build_quality_profile", return_value=fixture["analytics"]),
            ):
                result = run_ror_pipeline(
                    source_config=root / "source.json",
                    admission_config=root / "admission.json",
                    data_root=root,
                    zenodo_record_id=20818161,
                )

            self.assertEqual(result["status"], "COMPLETED")
            latest.assert_not_called()
            explicit.assert_called_once_with(
                source_config=root / "source.json",
                admission_config=root / "admission.json",
                record_id=20818161,
                data_root=root,
                replica_root=None,
            )


if __name__ == "__main__":
    unittest.main()
