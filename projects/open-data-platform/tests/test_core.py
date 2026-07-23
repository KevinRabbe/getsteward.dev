from __future__ import annotations

import json
import tempfile
import unittest
from dataclasses import dataclass
from pathlib import Path

from open_data_platform.archive import archive_staged_file
from open_data_platform.http_client import find_download_url, find_publication_date
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


if __name__ == "__main__":
    unittest.main()
