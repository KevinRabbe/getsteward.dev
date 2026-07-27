from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.pipeline import run_gleif_pipeline
from open_data_platform.production import persist_pipeline_metrics, production_acceptance_report


LEVEL1 = "ds_gleif_lei_level1_concat"
RR = "ds_gleif_rr_level2_concat"


def _fake_result(
    root: Path,
    *,
    source_version: str,
    snapshot_id: str,
    source_dataset_id: str = LEVEL1,
) -> dict:
    bundle = root / f"{snapshot_id}.zip"
    bundle.write_bytes(b"release-bytes")
    return {
        "status": "COMPLETED",
        "snapshot_id": snapshot_id,
        "ingest": {
            "status": "ARCHIVED",
            "snapshot": {
                "snapshot_id": snapshot_id,
                "source_dataset_id": source_dataset_id,
                "source_version": source_version,
                "acquisition_response": {"bytes_downloaded": 101},
                "content": {"original_size_bytes": 101},
            },
        },
        "parse": {
            "status": "PARSED",
            "artifact": {"source_dataset_id": source_dataset_id, "records_size_bytes": 202, "record_count": 10},
        },
        "product": {
            "status": "BUILT",
            "record_count": 10,
            "product": {
                "source_dataset_id": source_dataset_id,
                "database_size_bytes": 303,
                "record_count": 10,
                "unique_lei_count": 9 if source_dataset_id == LEVEL1 else None,
                "duplicate_lei_count": 1 if source_dataset_id == LEVEL1 else None,
            },
        },
        "release": {
            "status": "BUILT",
            "bundle_path": str(bundle),
            "bundle_size_bytes": bundle.stat().st_size,
        },
        "changes": {"status": "NO_PREVIOUS_SNAPSHOT", "to_snapshot_id": snapshot_id},
        "analytics": {"status": "BUILT", "snapshot_id": snapshot_id},
    }


class ProductionAcceptanceTests(unittest.TestCase):
    def test_pipeline_persists_phase17_metrics(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fake = _fake_result(root, source_version="2026-07-27", snapshot_id="snp_test")

            with (
                patch("open_data_platform.pipeline.ingest_latest", return_value=fake["ingest"]),
                patch("open_data_platform.pipeline.parse_snapshot", return_value=fake["parse"]),
                patch("open_data_platform.pipeline.build_product", return_value=fake["product"]),
                patch("open_data_platform.pipeline.build_release", return_value=fake["release"]),
                patch("open_data_platform.pipeline.build_changes_from_previous", return_value=fake["changes"]),
                patch("open_data_platform.pipeline.build_quality_profile", return_value=fake["analytics"]),
            ):
                result = run_gleif_pipeline(
                    source_config=root / "source.json",
                    admission_config=root / "admission.json",
                    data_root=root,
                )

            self.assertEqual(result["status"], "COMPLETED")
            metrics = result["metrics"]
            self.assertEqual(metrics["source_dataset_id"], LEVEL1)
            self.assertEqual(metrics["source_version"], "2026-07-27")
            self.assertEqual(metrics["record_count"], 10)
            self.assertEqual(
                set(metrics["stage_seconds"]),
                {"ingest", "parse", "product", "release", "changes", "analytics"},
            )
            self.assertTrue(Path(metrics["metrics_path"]).exists())

            report = production_acceptance_report(root, required_versions=7)
            self.assertEqual(report["status"], "IN_PROGRESS")
            self.assertEqual(report["source_dataset_id"], LEVEL1)
            self.assertEqual(report["observed_distinct_source_versions"], 1)
            self.assertEqual(report["remaining_source_versions"], 6)

    def test_seven_distinct_versions_pass_but_reruns_do_not_double_count(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            versions = [f"2026-07-{day:02d}" for day in range(21, 28)]
            for index, source_version in enumerate(versions):
                result = _fake_result(root, source_version=source_version, snapshot_id=f"snp_{index}")
                persist_pipeline_metrics(
                    data_root=root,
                    run_id=f"run_{index}",
                    started_at=f"{source_version}T02:00:00Z",
                    finished_at=f"{source_version}T02:10:00Z",
                    status="COMPLETED",
                    stage_seconds={"ingest": 1, "parse": 2, "product": 3, "release": 4, "changes": 5, "analytics": 1},
                    total_seconds=16,
                    physical_bytes_before=index * 1000,
                    physical_bytes_after=(index + 1) * 1000,
                    result=result,
                )

            duplicate = _fake_result(root, source_version=versions[-1], snapshot_id="snp_repeat")
            persist_pipeline_metrics(
                data_root=root,
                run_id="run_repeat",
                started_at="2026-07-27T03:00:00Z",
                finished_at="2026-07-27T03:10:00Z",
                status="COMPLETED",
                stage_seconds={"ingest": 1, "parse": 1, "product": 1, "release": 1, "changes": 1, "analytics": 1},
                total_seconds=6,
                physical_bytes_before=7000,
                physical_bytes_after=7000,
                result=duplicate,
            )

            report = production_acceptance_report(root, required_versions=7)
            self.assertEqual(report["status"], "PASS")
            self.assertEqual(report["observed_distinct_source_versions"], 7)
            self.assertEqual(report["completed_run_count"], 8)
            self.assertEqual(report["remaining_source_versions"], 0)
            self.assertEqual(report["calendar_span_days"], 7)

    def test_level2_runs_do_not_advance_level1_acceptance(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            level1 = _fake_result(root, source_version="2026-07-27", snapshot_id="snp_l1")
            rr = _fake_result(root, source_version="2026-07-28", snapshot_id="snp_rr", source_dataset_id=RR)
            for run_id, result in (("l1", level1), ("rr", rr)):
                persist_pipeline_metrics(
                    data_root=root,
                    run_id=run_id,
                    started_at="2026-07-28T00:00:00Z",
                    finished_at="2026-07-28T00:01:00Z",
                    status="COMPLETED",
                    stage_seconds={"ingest": 1},
                    total_seconds=1,
                    physical_bytes_before=0,
                    physical_bytes_after=1,
                    result=result,
                )
            level1_report = production_acceptance_report(root, required_versions=2)
            rr_report = production_acceptance_report(root, required_versions=1, source_dataset_id=RR)
            self.assertEqual(level1_report["observed_distinct_source_versions"], 1)
            self.assertEqual(level1_report["status"], "IN_PROGRESS")
            self.assertEqual(rr_report["observed_distinct_source_versions"], 1)
            self.assertEqual(rr_report["status"], "PASS")

    def test_corrupt_metrics_raise_alert_instead_of_being_ignored(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            metrics_dir = root / "metrics" / "runs"
            metrics_dir.mkdir(parents=True)
            (metrics_dir / "broken.json").write_text("{not-json", encoding="utf-8")

            report = production_acceptance_report(root)
            self.assertEqual(report["status"], "ALERT")
            self.assertEqual(len(report["metrics_load_errors"]), 1)


if __name__ == "__main__":
    unittest.main()
