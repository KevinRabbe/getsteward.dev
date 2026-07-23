from __future__ import annotations

import json
import os
import sqlite3
import stat
import tempfile
import unittest
import zipfile
from dataclasses import dataclass
from pathlib import Path
from unittest.mock import patch

from open_data_platform.archive import archive_staged_file
from open_data_platform.errors import ParseError
from open_data_platform.http_client import find_download_url, find_publication_date
from open_data_platform.parser import parse_snapshot, verify_normalized_artifact
from open_data_platform.pipeline import run_gleif_pipeline
from open_data_platform.product import build_product, verify_product
from open_data_platform.query import lookup_lei, search_name
from open_data_platform.release import build_release, verify_release
from open_data_platform.operations import replicate_release, status_report
from open_data_platform.verify import verify_snapshot


@dataclass(frozen=True)
class Remote:
    publication_date: str = "2026-07-22"
    download_url: str = "https://leidata.gleif.org/api/v1/concatenated-files/lei2/20260722/zip"
    cdf_version: str = "LEI_3_1"
    record_count: int = 3380454


class MetadataTests(unittest.TestCase):
    def test_extracts_date_and_download_url(self):
        metadata = {
            "publish_date": "2026-07-22T12:00:00Z",
            "cdf_version": "LEI_3_1",
            "download": {
                "zip": "https://leidata.gleif.org/api/v1/concatenated-files/lei2/20260722/zip"
            },
        }
        self.assertEqual(find_publication_date(metadata), "2026-07-22")
        self.assertEqual(
            find_download_url(metadata, ["leidata.gleif.org"]),
            metadata["download"]["zip"],
        )


class ArchiveTests(unittest.TestCase):
    def test_archive_and_verify(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            staged = root / "staging" / "source.zip"
            staged.parent.mkdir(parents=True)
            staged.write_bytes(b"fake zip bytes for deterministic unit test")

            source = {
                "source_id": "src_gleif",
                "source_dataset_id": "ds_gleif_lei_level1_concat",
                "publisher": "GLEIF",
                "dataset_name": "Level 1",
                "acquisition_method": "test",
            }
            admission = {
                "admission_decision_id": "adm1",
                "license_id": "CC0-1.0",
                "terms_url": "https://www.gleif.org/en/meta/lei-data-terms-of-use",
            }

            manifest = archive_staged_file(
                staged_path=staged,
                data_root=root,
                source=source,
                admission=admission,
                remote=Remote(),
                acquisition_metadata={"bytes_downloaded": 42},
            )
            result = verify_snapshot(root, manifest["snapshot_id"])
            self.assertTrue(result["manifest_ok"])
            self.assertTrue(result["content_ok"])
            self.assertFalse(staged.exists())

    def test_identical_bytes_are_physically_deduplicated(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = {
                "source_id": "src_gleif",
                "source_dataset_id": "ds_gleif_lei_level1_concat",
                "publisher": "GLEIF",
                "dataset_name": "Level 1",
                "acquisition_method": "test",
            }
            admission = {
                "admission_decision_id": "adm1",
                "license_id": "CC0-1.0",
                "terms_url": "https://www.gleif.org/en/meta/lei-data-terms-of-use",
            }
            manifests = []
            for i in range(2):
                staged = root / "staging" / f"source{i}.zip"
                staged.parent.mkdir(parents=True, exist_ok=True)
                staged.write_bytes(b"same bytes")
                manifests.append(archive_staged_file(
                    staged_path=staged,
                    data_root=root,
                    source=source,
                    admission=admission,
                    remote=Remote(publication_date=f"2026-07-2{2+i}"),
                    acquisition_metadata={},
                ))
            self.assertEqual(manifests[0]["content"]["content_id"], manifests[1]["content"]["content_id"])
            self.assertTrue(manifests[1]["content"]["deduplicated_existing_content"])


def _cdf_xml(*, record_count: int = 1, include_second_record: bool = False) -> str:
    second_record = """
    <LEIRecord>
      <LEI>529900T8BM49AURSDO55</LEI>
      <Entity>
        <LegalName xml:lang="en">Example Trading AG</LegalName>
        <LegalAddress xml:lang="en">
          <FirstAddressLine>Market Street 2</FirstAddressLine>
          <City>Zurich</City>
          <Country>CH</Country>
        </LegalAddress>
        <HeadquartersAddress xml:lang="en">
          <FirstAddressLine>Market Street 2</FirstAddressLine>
          <City>Zurich</City>
          <Country>CH</Country>
        </HeadquartersAddress>
        <EntityStatus>ACTIVE</EntityStatus>
      </Entity>
      <Registration>
        <InitialRegistrationDate>2018-02-03T00:00:00Z</InitialRegistrationDate>
        <LastUpdateDate>2026-07-20T00:00:00Z</LastUpdateDate>
        <RegistrationStatus>ISSUED</RegistrationStatus>
        <NextRenewalDate>2027-07-20T00:00:00Z</NextRenewalDate>
        <ManagingLOU>5299000J2N45DDNE4Y28</ManagingLOU>
      </Registration>
    </LEIRecord>
    """ if include_second_record else ""
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<LEIData xmlns="http://www.gleif.org/data/schema/lei/common/2016">
  <LEIHeader>
    <ContentDate>2026-07-20T00:00:00Z</ContentDate>
    <FileContent>GLEIF_FULL_PUBLISHED</FileContent>
    <RecordCount>{record_count}</RecordCount>
  </LEIHeader>
  <LEIRecord>
    <LEI>5493001KJTIIGC8Y1R12</LEI>
    <Entity>
      <LegalName xml:lang="en">Example Holdings GmbH</LegalName>
      <LegalAddress xml:lang="de">
        <FirstAddressLine>Main Street 1</FirstAddressLine>
        <AdditionalAddressLine>Building A</AdditionalAddressLine>
        <City>Berlin</City>
        <Region>BE</Region>
        <Country>DE</Country>
        <PostalCode>10115</PostalCode>
      </LegalAddress>
      <HeadquartersAddress xml:lang="de">
        <FirstAddressLine>Main Street 1</FirstAddressLine>
        <City>Berlin</City>
        <Country>DE</Country>
      </HeadquartersAddress>
      <RegistrationAuthority>
        <RegistrationAuthorityID>RA000123</RegistrationAuthorityID>
        <RegistrationAuthorityEntityID>HRB12345</RegistrationAuthorityEntityID>
      </RegistrationAuthority>
      <LegalJurisdiction>DE</LegalJurisdiction>
      <EntityCategory>GENERAL</EntityCategory>
      <LegalForm>
        <EntityLegalFormCode>2HBR</EntityLegalFormCode>
      </LegalForm>
      <EntityStatus>ACTIVE</EntityStatus>
      <EntityCreationDate>2000-01-01T00:00:00Z</EntityCreationDate>
    </Entity>
    <Registration>
      <InitialRegistrationDate>2012-02-03T00:00:00Z</InitialRegistrationDate>
      <LastUpdateDate>2026-07-20T00:00:00Z</LastUpdateDate>
      <RegistrationStatus>ISSUED</RegistrationStatus>
      <NextRenewalDate>2027-07-20T00:00:00Z</NextRenewalDate>
      <ManagingLOU>5299000J2N45DDNE4Y28</ManagingLOU>
      <ValidationSources>FULLY_CORROBORATED</ValidationSources>
    </Registration>
  </LEIRecord>
  {second_record}
</LEIData>
"""


def _archive_cdf(root: Path, *, record_count: int = 1, include_second_record: bool = False) -> dict:
    staged = root / "staging" / "gleif.zip"
    staged.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(staged, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("lei_20260720.xml", _cdf_xml(
            record_count=record_count,
            include_second_record=include_second_record,
        ))

    source = {
        "source_id": "src_gleif",
        "source_dataset_id": "ds_gleif_lei_level1_concat",
        "publisher": "GLEIF",
        "dataset_name": "Level 1",
        "acquisition_method": "test",
    }
    admission = {
        "admission_decision_id": "adm1",
        "license_id": "CC0-1.0",
        "terms_url": "https://www.gleif.org/en/meta/lei-data-terms-of-use",
    }
    return archive_staged_file(
        staged_path=staged,
        data_root=root,
        source=source,
        admission=admission,
        remote=Remote(),
        acquisition_metadata={"bytes_downloaded": staged.stat().st_size},
    )


class ParserTests(unittest.TestCase):
    def test_streams_cdf_into_immutable_normalized_artifact(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = _archive_cdf(root, record_count=2, include_second_record=True)

            result = parse_snapshot(root, manifest["snapshot_id"])

            self.assertEqual(result["status"], "PARSED")
            artifact_dir = root / "normalized" / manifest["source_dataset_id"] / manifest["snapshot_id"]
            artifact = json.loads((artifact_dir / "artifact.json").read_text(encoding="utf-8"))
            records = [
                json.loads(line)
                for line in (artifact_dir / "records.jsonl").read_text(encoding="utf-8").splitlines()
            ]
            self.assertEqual(artifact["record_count"], 2)
            self.assertEqual(artifact["cdf_header"]["file_content"], "GLEIF_FULL_PUBLISHED")
            self.assertEqual(records[0]["legal_name"], "Example Holdings GmbH")
            self.assertEqual(records[0]["legal_address"]["country"], "DE")
            self.assertEqual(records[0]["registration_authority"]["entity_id"], "HRB12345")
            self.assertEqual(records[1]["lei"], "529900T8BM49AURSDO55")
            self.assertEqual(result["quality"]["records_written"], 2)
            self.assertTrue((artifact_dir / "records.jsonl.sha256").exists())
            self.assertTrue((artifact_dir / "artifact.json.sha256").exists())
            verified = verify_normalized_artifact(root, manifest["snapshot_id"])
            self.assertEqual(verified["status"], "VERIFIED")
            self.assertEqual(verified["record_count"], 2)

            no_change = parse_snapshot(root, manifest["snapshot_id"])
            self.assertEqual(no_change["status"], "NO_CHANGE")

    def test_rejects_header_record_count_mismatch_without_publishing_partial_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = _archive_cdf(root, record_count=2)

            with self.assertRaises(ParseError):
                parse_snapshot(root, manifest["snapshot_id"])

            artifact_dir = root / "normalized" / manifest["source_dataset_id"] / manifest["snapshot_id"]
            self.assertFalse(artifact_dir.exists())


class ProductTests(unittest.TestCase):
    def test_builds_and_verifies_sqlite_product(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = _archive_cdf(root, record_count=2, include_second_record=True)
            parse_snapshot(root, manifest["snapshot_id"])

            result = build_product(root, manifest["snapshot_id"])

            self.assertEqual(result["status"], "BUILT")
            self.assertEqual(result["record_count"], 2)
            self.assertTrue((Path(result["product_dir"]) / "lei.sqlite.sha256").exists())
            connection = sqlite3.connect(result["database_path"])
            try:
                rows = connection.execute(
                    "SELECT lei, legal_name, entity_status FROM lei ORDER BY lei"
                ).fetchall()
            finally:
                connection.close()
            self.assertEqual(rows, [
                ("529900T8BM49AURSDO55", "Example Trading AG", "ACTIVE"),
                ("5493001KJTIIGC8Y1R12", "Example Holdings GmbH", "ACTIVE"),
            ])

            verified = verify_product(root, manifest["snapshot_id"])
            self.assertEqual(verified["status"], "VERIFIED")
            self.assertEqual(verified["record_count"], 2)
            self.assertEqual(
                build_product(root, manifest["snapshot_id"])["status"],
                "NO_CHANGE",
            )

    def test_rejects_tampered_normalized_checksum_before_build(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = _archive_cdf(root)
            parse_snapshot(root, manifest["snapshot_id"])
            checksum_path = (
                root
                / "normalized"
                / manifest["source_dataset_id"]
                / manifest["snapshot_id"]
                / "records.jsonl.sha256"
            )
            os.chmod(checksum_path, stat.S_IRUSR | stat.S_IWUSR)
            checksum_path.write_text("0" * 64 + "  records.jsonl\n", encoding="ascii")

            with self.assertRaises(ParseError):
                build_product(root, manifest["snapshot_id"])


class ReleaseAndQueryTests(unittest.TestCase):
    def test_builds_release_and_serves_verified_read_only_queries(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = _archive_cdf(root, record_count=2, include_second_record=True)
            parse_snapshot(root, manifest["snapshot_id"])
            build_product(root, manifest["snapshot_id"])

            release = build_release(root, manifest["snapshot_id"])

            self.assertEqual(release["status"], "BUILT")
            self.assertTrue(Path(release["bundle_path"]).exists())
            verified = verify_release(root, manifest["snapshot_id"])
            self.assertEqual(verified["status"], "VERIFIED")
            with zipfile.ZipFile(release["bundle_path"], "r") as bundle:
                self.assertIn("product/lei.sqlite", bundle.namelist())
                self.assertIn("source/manifest.json", bundle.namelist())

            found = lookup_lei(root, "5493001KJTIIGC8Y1R12", snapshot_id=manifest["snapshot_id"])
            self.assertEqual(found["status"], "FOUND")
            self.assertEqual(found["record"]["legal_name"], "Example Holdings GmbH")
            self.assertEqual(found["provenance"]["snapshot_id"], manifest["snapshot_id"])

            search = search_name(root, "Example", snapshot_id=manifest["snapshot_id"], limit=10)
            self.assertEqual(search["count"], 2)
            self.assertEqual(search["records"][0]["legal_name"], "Example Holdings GmbH")

            missing = lookup_lei(root, "00000000000000000000", snapshot_id=manifest["snapshot_id"])
            self.assertEqual(missing["status"], "NOT_FOUND")

            replica = root / "release-replica"
            replicated = replicate_release(root, manifest["snapshot_id"], replica)
            self.assertEqual(replicated["status"], "REPLICATED")
            self.assertTrue((replica / manifest["source_dataset_id"] / manifest["snapshot_id"] / "release.zip").exists())
            self.assertEqual(
                replicate_release(root, manifest["snapshot_id"], replica)["status"],
                "NO_CHANGE",
            )

            status = status_report(root)
            self.assertEqual(status["snapshot_count"], 1)
            snapshot_status = status["snapshots"][0]
            self.assertEqual(snapshot_status["raw"], "VERIFIED")
            self.assertEqual(snapshot_status["normalized"], "VERIFIED")
            self.assertEqual(snapshot_status["product"], "VERIFIED")
            self.assertEqual(snapshot_status["release"], "VERIFIED")

    def test_end_to_end_pipeline_records_completion_events(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = _archive_cdf(root)
            fake_ingest_result = {"status": "ARCHIVED", "snapshot": manifest, "replica_path": None}

            with patch("open_data_platform.pipeline.ingest_latest", return_value=fake_ingest_result):
                result = run_gleif_pipeline(
                    source_config=root / "source.json",
                    admission_config=root / "admission.json",
                    data_root=root,
                )

            self.assertEqual(result["status"], "COMPLETED")
            self.assertEqual(result["release"]["status"], "BUILT")
            events = (root / "events" / "events.jsonl").read_text(encoding="utf-8")
            self.assertIn('"event": "PIPELINE_COMPLETED"', events)


if __name__ == "__main__":
    unittest.main()
