from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.analytics import verify_quality_profile  # noqa: E402
from open_data_platform.ror_changes import verify_ror_changes  # noqa: E402
from open_data_platform.ror_commercial import (  # noqa: E402
    build_ror_commercial_product,
    verify_ror_commercial_product,
)
from open_data_platform.ror_pipeline import run_ror_pipeline  # noqa: E402
from open_data_platform.ror_product import verify_ror_product  # noqa: E402


V29_RECORD_ID = 20818161
V210_RECORD_ID = 21458494
EXPECTED_V29_COUNT = 129_046
EXPECTED_V210_COUNT = 132_537
EXPECTED_NEW_V210 = 3_491
PUBLISHED_UPDATED_V210 = 2_920


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def _run_record(data_root: Path, record_id: int) -> dict:
    return run_ror_pipeline(
        source_config=ROOT / "config" / "sources" / "ror_registry.json",
        admission_config=ROOT / "config" / "admissions" / "ror_registry.json",
        data_root=data_root,
        zenodo_record_id=record_id,
    )


def main() -> None:
    parser = argparse.ArgumentParser(prog="ror_live_acceptance")
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    data_root = args.data_root.resolve()
    if data_root.exists():
        shutil.rmtree(data_root)
    data_root.mkdir(parents=True)

    # Deliberately process newer first. The historical backfill must then create
    # the missing v2.9 -> v2.10 adjacent change asset without rewriting v2.10.
    v210_run = _run_record(data_root, V210_RECORD_ID)
    v29_run = _run_record(data_root, V29_RECORD_ID)
    v210_snapshot = str(v210_run["snapshot_id"])
    v29_snapshot = str(v29_run["snapshot_id"])

    v29_product = verify_ror_product(data_root, v29_snapshot)
    v210_product = verify_ror_product(data_root, v210_snapshot)
    v29_quality = verify_quality_profile(data_root, v29_snapshot)
    v210_quality = verify_quality_profile(data_root, v210_snapshot)
    changes = verify_ror_changes(data_root, v29_snapshot, v210_snapshot)
    change_artifact = changes["artifact"]

    _require(v29_product["product"]["source_version"] == "v2.9", "v2.9 source version mismatch")
    _require(v210_product["product"]["source_version"] == "v2.10", "v2.10 source version mismatch")
    _require(v29_product["record_count"] == EXPECTED_V29_COUNT, "v2.9 record count mismatch")
    _require(v210_product["record_count"] == EXPECTED_V210_COUNT, "v2.10 record count mismatch")
    _require(
        v29_product["product"].get("excluded_source_fields") == ["locations"],
        "v2.9 lost locations exclusion",
    )
    _require(
        v210_product["product"].get("excluded_source_fields") == ["locations"],
        "v2.10 lost locations exclusion",
    )
    _require(
        change_artifact["from_source_publication_date"] == "2026-06-23",
        "v2.9 publication date mismatch",
    )
    _require(
        change_artifact["to_source_publication_date"] == "2026-07-20",
        "v2.10 publication date mismatch",
    )

    type_counts = change_artifact["change_type_counts"]
    observed_new = int(type_counts.get("NEW", 0))
    observed_removed = int(type_counts.get("REMOVED", 0))
    changed_existing = int(change_artifact["change_event_count"]) - observed_new - observed_removed
    _require(observed_new == EXPECTED_NEW_V210, "v2.10 NEW count does not match published additions")
    _require(observed_removed == 0, "unexpected ROR removals between v2.9 and v2.10")

    commercial = build_ror_commercial_product(
        data_root,
        v210_snapshot,
        catalog_path=ROOT
        / "catalog"
        / "products"
        / "global_research_organization_history.json",
    )
    commercial_verified = verify_ror_commercial_product(data_root, v210_snapshot)
    _require(commercial_verified["status"] == "VERIFIED", "commercial candidate did not verify")
    _require(
        commercial_verified["product"]["customer_terms_status"] == "LEGAL_REVIEW_REQUIRED",
        "commercial candidate lost legal-review gate",
    )

    evidence = {
        "status": "PASS",
        "execution_order": ["v2.10", "v2.9"],
        "source_records": {
            "v2.9": V29_RECORD_ID,
            "v2.10": V210_RECORD_ID,
        },
        "snapshots": {
            "v2.9": v29_snapshot,
            "v2.10": v210_snapshot,
        },
        "products": {
            "v2.9": {
                "record_count": v29_product["record_count"],
                "product_sha256": v29_product["product"]["product_sha256"],
                "quality_sha256": v29_quality["quality"]["quality_sha256"],
            },
            "v2.10": {
                "record_count": v210_product["record_count"],
                "product_sha256": v210_product["product"]["product_sha256"],
                "quality_sha256": v210_quality["quality"]["quality_sha256"],
            },
        },
        "history": {
            "from_source_version": change_artifact["from_source_version"],
            "to_source_version": change_artifact["to_source_version"],
            "change_event_count": change_artifact["change_event_count"],
            "unchanged_ror_id_count": change_artifact["unchanged_ror_id_count"],
            "change_type_counts": type_counts,
            "observed_new": observed_new,
            "observed_removed": observed_removed,
            "observed_changed_existing": changed_existing,
            "published_updated_existing": PUBLISHED_UPDATED_V210,
            "matches_published_updated_existing": changed_existing == PUBLISHED_UPDATED_V210,
            "changes_sha256": change_artifact["changes_sha256"],
            "artifact_sha256": change_artifact["artifact_sha256"],
        },
        "commercial_candidate": {
            "status": commercial_verified["product"]["commercial_status"],
            "customer_terms_status": commercial_verified["product"]["customer_terms_status"],
            "bundle_sha256": commercial_verified["product"]["bundle_sha256"],
            "bundle_size_bytes": commercial["bundle_size_bytes"],
            "adjacent_pair_count": commercial_verified["product"]["history_coverage"][
                "adjacent_pair_count"
            ],
        },
        "run_metrics": {
            "v2.10": v210_run["metrics"],
            "v2.9_backfill": v29_run["metrics"],
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(evidence, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(evidence, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
