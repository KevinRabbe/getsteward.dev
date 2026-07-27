from __future__ import annotations

import json
import tempfile
import unittest
import zipfile
from dataclasses import dataclass
from pathlib import Path
from unittest.mock import patch

from open_data_platform.archive import archive_staged_file
from open_data_platform.discovery import discover_latest
from open_data_platform.relationship_parser import parse_relationship_snapshot, verify_relationship_artifact
from open_data_platform.relationship_product import build_relationship_product, verify_relationship_product
from open_data_platform.relationship_query import relationships_for_lei
from open_data_platform.relationship_release import build_relationship_release
from open_data_platform.release import verify_release


@dataclass(frozen=True)
class RemoteRelationship:
    publication_date: str = "2026-07-27"
    download_url: str = "https://leidata.gleif.org/api/v1/concatenated-files/rr/20260727/zip"
    cdf_version: str = "RR_2_1"
    record_count: int = 2


def _rr_xml() -> str:
    return """<?xml version="1.0" encoding="UTF-8"?>
<RelationshipData xmlns="http://www.gleif.org/data/schema/rr/2016">
  <Header>
    <ContentDate>2026-07-27T12:00:00Z</ContentDate>
    <Originator>5493001KJTIIGC8Y1R12</Originator>
    <FileContent>GLEIF_FULL_PUBLISHED</FileContent>
    <RecordCount>2</RecordCount>
  </Header>
  <RelationshipRecords>
    <RelationshipRecord>
      <Relationship>
        <StartNode><NodeID>529900T8BM49AURSDO55</NodeID><NodeIDType>LEI</NodeIDType></StartNode>
        <EndNode><NodeID>5493001KJTIIGC8Y1R12</NodeID><NodeIDType>LEI</NodeIDType></EndNode>
        <RelationshipType>IS_DIRECTLY_CONSOLIDATED_BY</RelationshipType>
        <RelationshipPeriods>
          <RelationshipPeriod>
            <StartDate>2020-01-01T00:00:00Z</StartDate>
            <PeriodType>RELATIONSHIP_PERIOD</PeriodType>
          </RelationshipPeriod>
        </RelationshipPeriods>
        <RelationshipStatus>ACTIVE</RelationshipStatus>
        <RelationshipQualifiers>
          <RelationshipQualifier>
            <QualifierDimension>ACCOUNTING_STANDARD</QualifierDimension>
            <QualifierCategory>IFRS</QualifierCategory>
          </RelationshipQualifier>
        </RelationshipQualifiers>
      </Relationship>
      <Registration>
        <InitialRegistrationDate>2020-01-02T00:00:00Z</InitialRegistrationDate>
        <LastUpdateDate>2026-07-27T00:00:00Z</LastUpdateDate>
        <RegistrationStatus>PUBLISHED</RegistrationStatus>
        <NextRenewalDate>2027-07-27T00:00:00Z</NextRenewalDate>
        <ManagingLOU>5493001KJTIIGC8Y1R12</ManagingLOU>
        <ValidationSources>FULLY_CORROBORATED</ValidationSources>
        <ValidationDocuments>ACCOUNTS_FILING</ValidationDocuments>
        <ValidationReference>Annual report</ValidationReference>
      </Registration>
    </RelationshipRecord>
    <RelationshipRecord>
      <Relationship>
        <StartNode><NodeID>213800D1EI4B9WTWWD28</NodeID><NodeIDType>LEI</NodeIDType></StartNode>
        <EndNode><NodeID>529900T8BM49AURSDO55</NodeID><NodeIDType>LEI</NodeIDType></EndNode>
        <RelationshipType>IS_FUND-MANAGED_BY</RelationshipType>
        <RelationshipStatus>ACTIVE</RelationshipStatus>
        <RelationshipQuantifiers>
          <RelationshipQuantifier>
            <MeasurementMethod>PERCENTAGE</MeasurementMethod>
            <QuantifierAmount>100</QuantifierAmount>
            <QuantifierUnits>PERCENTAGE</QuantifierUnits>
          </RelationshipQuantifier>
        </RelationshipQuantifiers>
      </Relationship>
      <Registration>
        <InitialRegistrationDate>2021-01-02T00:00:00Z</InitialRegistrationDate>
        <LastUpdateDate>2026-07-27T00:00:00Z</LastUpdateDate>
        <RegistrationStatus>PUBLISHED</RegistrationStatus>
        <ManagingLOU>5493001KJTIIGC8Y1R12</ManagingLOU>
        <ValidationSources>ENTITY_SUPPLIED_ONLY</ValidationSources>
        <ValidationDocuments>SUPPORTING_DOCUMENTS</ValidationDocuments>
      </Registration>
    </RelationshipRecord>
  </RelationshipRecords>
</RelationshipData>
"""


def _archive_rr(root: Path) -> str:
    staged = root / "staging" / "rr.zip"
    staged.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(staged, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        bundle.writestr("rr.xml", _rr_xml())
    source = {
        "source_id": "src_gleif",
        "source_dataset_id": "ds_gleif_rr_level2_concat",
        "publisher": "GLEIF",
        "dataset_name": "Level 2 RR-CDF",
        "acquisition_method": "test",
    }
    admission = {
        "admission_decision_id": "adm_rr",
        "license_id": "CC0-1.0",
        "terms_url": "https://www.gleif.org/en/meta/lei-data-terms-of-use",
    }
    manifest = archive_staged_file(
        staged_path=staged,
        data_root=root,
        source=source,
        admission=admission,
        remote=RemoteRelationship(),
        acquisition_metadata={"bytes_downloaded": staged.stat().st_size},
    )
    return str(manifest["snapshot_id"])


class RelationshipPipelineTests(unittest.TestCase):
    def test_rr_discovery_uses_rr_endpoint(self):
        metadata = {
            "data": {
                "content_date": "2026-07-27 12:00:00",
                "record_count": 660700,
                "cdf_version": "RR_2.1",
            }
        }
        source = {
            "metadata_url": "https://leidata.gleif.org/api/v1/concatenated-files/rr/latest",
            "allowed_hosts": ["leidata.gleif.org"],
            "download_url_template": "https://leidata.gleif.org/api/v1/concatenated-files/rr/{yyyymmdd}/zip",
        }
        with patch("open_data_platform.discovery.get_json", return_value=metadata):
            remote = discover_latest(source)
        self.assertEqual(
            remote.download_url,
            "https://leidata.gleif.org/api/v1/concatenated-files/rr/20260727/zip",
        )
        self.assertEqual(remote.record_count, 660700)

    def test_rr_parse_product_release_and_query(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            snapshot_id = _archive_rr(root)

            parsed = parse_relationship_snapshot(root, snapshot_id)
            self.assertEqual(parsed["status"], "PARSED")
            self.assertEqual(parsed["quality"]["records_written"], 2)
            self.assertEqual(parsed["quality"]["relationship_type_counts"]["IS_DIRECTLY_CONSOLIDATED_BY"], 1)
            self.assertEqual(verify_relationship_artifact(root, snapshot_id)["status"], "VERIFIED")

            product = build_relationship_product(root, snapshot_id)
            self.assertEqual(product["record_count"], 2)
            self.assertEqual(verify_relationship_product(root, snapshot_id)["status"], "VERIFIED")

            outgoing = relationships_for_lei(
                root,
                snapshot_id,
                "529900T8BM49AURSDO55",
                direction="outgoing",
            )
            self.assertEqual(outgoing["status"], "FOUND")
            self.assertEqual(outgoing["relationships"][0]["end_node"]["id"], "5493001KJTIIGC8Y1R12")

            incoming = relationships_for_lei(
                root,
                snapshot_id,
                "529900T8BM49AURSDO55",
                direction="incoming",
            )
            self.assertEqual(incoming["count"], 1)
            self.assertEqual(incoming["relationships"][0]["relationship_type"], "IS_FUND-MANAGED_BY")

            release = build_relationship_release(root, snapshot_id)
            self.assertEqual(release["status"], "BUILT")
            verified_release = verify_release(root, snapshot_id)
            self.assertEqual(verified_release["release"]["release_type"], "gleif_rr_level2_sqlite_bundle")

            self.assertEqual(parse_relationship_snapshot(root, snapshot_id)["status"], "NO_CHANGE")
            self.assertEqual(build_relationship_product(root, snapshot_id)["status"], "NO_CHANGE")
            self.assertEqual(build_relationship_release(root, snapshot_id)["status"], "NO_CHANGE")


if __name__ == "__main__":
    unittest.main()
