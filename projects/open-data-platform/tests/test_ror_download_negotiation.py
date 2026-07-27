from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.ror_source import RemoteRorSnapshot, _ingest_remote_ror


class RorDownloadNegotiationTests(unittest.TestCase):
    def test_zenodo_download_uses_unrestricted_accept_but_keeps_checksum_gate(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = {
                "source_id": "src_ror",
                "source_dataset_id": "ds_ror_organizations",
                "allowed_hosts": ["zenodo.org"],
            }
            admission = {"admission_decision_id": "adm_ror"}
            remote = RemoteRorSnapshot(
                publication_date="v2.10",
                download_url=(
                    "https://zenodo.org/api/records/21458494/files/"
                    "v2.10-2026-07-20-ror-data.zip"
                ),
                cdf_version="ROR Schema 2.1",
                record_count=None,
                metadata={},
                release_date="2026-07-20",
                zenodo_record_id=21458494,
                version_doi="10.5281/zenodo.21458494",
                concept_doi="10.5281/zenodo.6347574",
                filename="v2.10-2026-07-20-ror-data.zip",
                source_checksum="md5:0123456789abcdef0123456789abcdef",
            )

            def fake_download(url, destination, allowed_hosts, timeout=120, *, accept):
                self.assertEqual(url, remote.download_url)
                self.assertEqual(allowed_hosts, ["zenodo.org"])
                self.assertEqual(accept, "*/*")
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(b"publisher-zip")
                return {
                    "bytes_downloaded": len(b"publisher-zip"),
                    "content_type": "application/octet-stream",
                    "etag": None,
                    "last_modified": None,
                }

            archived = {
                "snapshot_id": "snp_ror",
                "source_version": "v2.10",
                "content": {"content_id": "sha256:test"},
            }
            with (
                patch("open_data_platform.ror_source._snapshot_already_archived", return_value=None),
                patch("open_data_platform.ror_source.stream_download", side_effect=fake_download) as download,
                patch(
                    "open_data_platform.ror_source._md5",
                    return_value="0123456789abcdef0123456789abcdef",
                ),
                patch("open_data_platform.ror_source.archive_staged_file", return_value=archived) as archive,
            ):
                result = _ingest_remote_ror(
                    source=source,
                    admission=admission,
                    remote=remote,
                    data_root=root,
                    replica_root=None,
                    discovery_mode="explicit_record",
                )

            self.assertEqual(result["status"], "ARCHIVED")
            download.assert_called_once()
            archive.assert_called_once()


if __name__ == "__main__":
    unittest.main()
