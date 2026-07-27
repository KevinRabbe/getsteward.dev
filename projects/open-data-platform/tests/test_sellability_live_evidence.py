from __future__ import annotations

import json
import unittest
from pathlib import Path

from open_data_platform.sellability import _ROR_PRODUCT, _operational_acceptance


class SellabilityLiveEvidenceTests(unittest.TestCase):
    def test_committed_real_ror_evidence_matches_its_deterministic_product_inputs(self):
        project_root = Path(__file__).resolve().parents[1]
        evidence_path = (
            project_root / "docs" / "live-acceptance" / "ror-v2.9-v2.10.json"
        )
        wrapper = json.loads(evidence_path.read_text(encoding="utf-8"))
        evidence = wrapper["evidence"]
        history = evidence["history"]
        latest_version = history["to_source_version"]
        product_hash = evidence["products"][latest_version]["product_sha256"]

        candidate_manifest = {
            "organization_component": {
                "source_version": latest_version,
                "product_database_sha256": product_hash,
            },
            "history": [
                {
                    "from_source_version": history["from_source_version"],
                    "to_source_version": latest_version,
                    "changes_sha256": history["changes_sha256"],
                    "artifact_sha256": history["artifact_sha256"],
                }
            ],
        }
        result = _operational_acceptance(
            project_root=project_root,
            data_root=project_root / "data",
            product_id=_ROR_PRODUCT,
            candidate_manifest=candidate_manifest,
        )
        self.assertEqual(result["status"], "PASS")
        self.assertTrue(result["logical_candidate_match"]["matches"])


if __name__ == "__main__":
    unittest.main()
