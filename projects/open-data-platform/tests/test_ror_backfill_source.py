from __future__ import annotations

import unittest
from unittest.mock import patch

from open_data_platform.errors import AcquisitionError
from open_data_platform.ror_source import discover_ror_record


class RorBackfillSourceTests(unittest.TestCase):
    def _metadata(self, *, record_id: int = 20818161) -> dict:
        return {
            "id": record_id,
            "doi": "10.5281/zenodo.20818161",
            "conceptdoi": "10.5281/zenodo.6347574",
            "metadata": {"publication_date": "2026-06-23", "version": "v2.9"},
            "files": [
                {
                    "key": "v2.9-2026-06-23-ror-data.zip",
                    "checksum": "md5:f42792cae72dd6c223c2b792ccf77169",
                    "links": {
                        "self": "https://zenodo.org/api/records/20818161/files/v2.9-2026-06-23-ror-data.zip/content"
                    },
                }
            ],
        }

    def test_explicit_zenodo_record_is_resolved_without_latest_discovery(self):
        source = {
            "record_metadata_url_template": "https://zenodo.org/api/records/{record_id}",
            "allowed_hosts": ["zenodo.org"],
        }
        with patch(
            "open_data_platform.ror_source.get_json",
            return_value=self._metadata(),
        ) as get_json:
            remote = discover_ror_record(source, 20818161)

        get_json.assert_called_once_with(
            "https://zenodo.org/api/records/20818161",
            ["zenodo.org"],
        )
        self.assertEqual(remote.publication_date, "v2.9")
        self.assertEqual(remote.release_date, "2026-06-23")
        self.assertEqual(remote.zenodo_record_id, 20818161)
        self.assertEqual(remote.source_checksum, "md5:f42792cae72dd6c223c2b792ccf77169")

    def test_explicit_record_identity_mismatch_fails_closed(self):
        source = {
            "record_metadata_url_template": "https://zenodo.org/api/records/{record_id}",
            "allowed_hosts": ["zenodo.org"],
        }
        with patch(
            "open_data_platform.ror_source.get_json",
            return_value=self._metadata(record_id=999),
        ):
            with self.assertRaises(AcquisitionError):
                discover_ror_record(source, 20818161)


if __name__ == "__main__":
    unittest.main()
