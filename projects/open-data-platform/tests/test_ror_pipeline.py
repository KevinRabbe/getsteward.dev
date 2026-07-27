from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.ror_pipeline import run_ror_pipeline


class RorPipelineTests(unittest.TestCase):
    def test_pipeline_builds_quality_profile_and_records_analytics_stage(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            snapshot_id = "snp_ror"
            ingest = {
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
            }
            parsed = {
                "status": "PARSED",
                "artifact": {
                    "source_dataset_id": "ds_ror_organizations",
                    "records_size_bytes": 200,
                    "record_count": 10,
                },
            }
            product = {
                "status": "BUILT",
                "record_count": 10,
                "product": {
                    "source_dataset_id": "ds_ror_organizations",
                    "source_version": "v2.10",
                    "database_size_bytes": 300,
                    "record_count": 10,
                },
            }
            release = {
                "status": "BUILT",
                "bundle_path": str(root / "release.zip"),
                "bundle_size_bytes": 400,
            }
            analytics = {
                "status": "BUILT",
                "snapshot_id": snapshot_id,
                "quality": {
                    "source_dataset_id": "ds_ror_organizations",
                    "source_version": "v2.10",
                },
            }

            with (
                patch("open_data_platform.ror_pipeline.ingest_latest_ror", return_value=ingest),
                patch("open_data_platform.ror_pipeline.parse_ror_snapshot", return_value=parsed),
                patch("open_data_platform.ror_pipeline.build_ror_product", return_value=product),
                patch("open_data_platform.ror_pipeline.build_ror_release", return_value=release),
                patch("open_data_platform.ror_pipeline.build_quality_profile", return_value=analytics),
            ):
                result = run_ror_pipeline(
                    source_config=root / "source.json",
                    admission_config=root / "admission.json",
                    data_root=root,
                )

            self.assertEqual(result["status"], "COMPLETED")
            self.assertEqual(result["analytics"]["status"], "BUILT")
            self.assertEqual(result["metrics"]["source_dataset_id"], "ds_ror_organizations")
            self.assertEqual(result["metrics"]["source_version"], "v2.10")
            self.assertEqual(
                set(result["metrics"]["stage_seconds"]),
                {"ingest", "parse", "product", "release", "analytics"},
            )


if __name__ == "__main__":
    unittest.main()
