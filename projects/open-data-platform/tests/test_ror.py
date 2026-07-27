from __future__ import annotations

import csv
import json
import tempfile
import unittest
import zipfile
from dataclasses import dataclass
from pathlib import Path
from unittest.mock import patch

from open_data_platform.archive import archive_staged_file
from open_data_platform.distributions import build_csv_distribution, verify_distribution
from open_data_platform.errors import ParseError
from open_data_platform.release import verify_release
from open_data_platform.ror_parser import parse_ror_snapshot, verify_ror_artifact
from open_data_platform.ror_product import build_ror_product, verify_ror_product
from open_data_platform.ror_query import lookup_ror, search_ror_name
from open_data_platform.ror_release import build_ror_release
from open_data_platform.ror_source import discover_latest_ror


@dataclass(frozen=True)
class RemoteRor:
    publication_date: str = "v2.10"
    download_url: str = "https://zenodo.org/api/records/21458494/files/v2.10-2026-07-20-ror-data.zip/content"
    cdf_version: str = "ROR Schema 2.1"
    record_count: None = None
    metadata: object = None


def _record(
    ror_id: str,
    display_name: str,
    *,
    status: str = "active",
    relationship_id: str | None = None,
    relationship_type: str = "parent",
) -> dict:
    relationships = []
    if relationship_id is not None:
        relationships.append(
            {
                "id": relationship_id,
                "label": "Related Organization",
                "type": relationship_type,
            }
        )
    return {
        "admin": {
            "created": {"date": "2018-11-14", "schema_version": "1.0"},
            "last_modified": {"date": "2026-07-20", "schema_version": "2.1"},
        },
        "domains": ["example.org"],
        "established": 1900,
        "external_ids": [
            {"all": ["grid.123.4"], "preferred": "grid.123.4", "type": "grid"}
        ],
        "id": ror_id,
        "links": [{"type": "website", "value": "https://example.org"}],
        "locations": [
            {
                "geonames_id": 2950159,
                "geonames_details": {"name": "Berlin", "country_name": "Germany"},
                "location_name": "Berlin, Germany",
            }
        ],
        "names": [
            {"lang": "en", "types": ["ror_display", "label"], "value": display_name},
            {"lang": None, "types": ["alias"], "value": display_name + " Alias"},
        ],
        "relationships": relationships,
        "status": status,
        "types": ["education"],
    }


def _archive_ror(root: Path, records: list[dict]) -> str:
    staged = root / "staging" / "ror.zip"
    staged.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(staged, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        bundle.writestr(
            "v2.10-2026-07-20-ror-data.json",
            json.dumps(records, ensure_ascii=False),
        )
        bundle.writestr("v2.10-2026-07-20-ror-data.csv", "id,name\n")
    source = {
        "source_id": "src_ror",
        "source_dataset_id": "ds_ror_organizations",
        "publisher": "ROR",
        "dataset_name": "ROR Registry Data Dump",
        "acquisition_method": "test",
    }
    admission = {
        "admission_decision_id": "adm_ror",
        "license_id": "CC0-1.0",
        "terms_url": "https://ror.org/about/terms/",
    }
    manifest = archive_staged_file(
        staged_path=staged,
        data_root=root,
        source=source,
        admission=admission,
        remote=RemoteRor(),
        acquisition_metadata={"bytes_downloaded": staged.stat().st_size, "source_md5_verified": True},
    )
    return str(manifest["snapshot_id"])


class RorDatabaseTests(unittest.TestCase):
    def test_zenodo_discovery_selects_one_current_data_zip(self):
        metadata = {
            "id": 21458494,
            "doi": "10.5281/zenodo.21458494",
            "conceptdoi": "10.5281/zenodo.6347574",
            "metadata": {"publication_date": "2026-07-20", "version": "v2.10"},
            "files": [
                {
                    "key": "v2.10-2026-07-20-ror-data.zip",
                    "checksum": "md5:0123456789abcdef0123456789abcdef",
                    "links": {
                        "self": "https://zenodo.org/api/records/21458494/files/v2.10-2026-07-20-ror-data.zip/content"
                    },
                }
            ],
        }
        source = {
            "metadata_url": "https://zenodo.org/api/records/6347574",
            "allowed_hosts": ["zenodo.org"],
        }
        with patch("open_data_platform.ror_source.get_json", return_value=metadata):
            remote = discover_latest_ror(source)
        self.assertEqual(remote.publication_date, "v2.10")
        self.assertEqual(remote.release_date, "2026-07-20")
        self.assertEqual(remote.zenodo_record_id, 21458494)
        self.assertEqual(remote.source_checksum, "md5:0123456789abcdef0123456789abcdef")

    def test_ror_parse_product_release_distribution_and_queries_exclude_locations(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            first_id = "https://ror.org/03yrm5c26"
            second_id = "https://ror.org/05dxps055"
            snapshot_id = _archive_ror(
                root,
                [
                    _record(first_id, "Example University", relationship_id=second_id),
                    _record(second_id, "Example Research Institute", status="inactive"),
                ],
            )

            parsed = parse_ror_snapshot(root, snapshot_id)
            self.assertEqual(parsed["status"], "PARSED")
            self.assertEqual(parsed["quality"]["records_written"], 2)
            self.assertEqual(parsed["quality"]["records_with_excluded_locations"], 2)
            self.assertEqual(parsed["artifact"]["excluded_source_fields"], ["locations"])

            normalized = verify_ror_artifact(root, snapshot_id)
            first_line = json.loads(Path(normalized["records_path"]).read_text(encoding="utf-8").splitlines()[0])
            self.assertNotIn("locations", first_line)
            self.assertEqual(first_line["display_name"], "Example University")

            product = build_ror_product(root, snapshot_id)
            self.assertEqual(product["record_count"], 2)
            self.assertEqual(product["product"]["excluded_source_fields"], ["locations"])
            self.assertEqual(verify_ror_product(root, snapshot_id)["status"], "VERIFIED")

            distribution = build_csv_distribution(root, snapshot_id)
            self.assertEqual(distribution["status"], "BUILT")
            verified_distribution = verify_distribution(root, snapshot_id, "csv")
            self.assertEqual(verified_distribution["status"], "VERIFIED")
            with Path(distribution["data_path"]).open("r", encoding="utf-8", newline="") as handle:
                rows = list(csv.reader(handle))
            header = rows[0]
            self.assertIn("ror_id", header)
            self.assertIn("relationships_json", header)
            self.assertNotIn("locations", header)
            self.assertNotIn("locations_json", header)
            self.assertEqual(len(rows) - 1, 2)

            lookup = lookup_ror(root, snapshot_id, "03yrm5c26")
            self.assertEqual(lookup["status"], "FOUND")
            self.assertEqual(lookup["organization"]["display_name"], "Example University")
            self.assertNotIn("locations", lookup["organization"])
            self.assertEqual(lookup["excluded_source_fields"], ["locations"])

            search = search_ror_name(root, snapshot_id, "Research")
            self.assertEqual(search["count"], 1)
            self.assertEqual(search["organizations"][0]["ror_id"], second_id)

            release = build_ror_release(root, snapshot_id)
            self.assertEqual(release["status"], "BUILT")
            verified_release = verify_release(root, snapshot_id)
            self.assertEqual(verified_release["release"]["release_type"], "ror_organizations_sqlite_bundle")
            self.assertEqual(verified_release["release"]["excluded_source_fields"], ["locations"])

            self.assertEqual(parse_ror_snapshot(root, snapshot_id)["status"], "NO_CHANGE")
            self.assertEqual(build_ror_product(root, snapshot_id)["status"], "NO_CHANGE")
            self.assertEqual(build_csv_distribution(root, snapshot_id)["status"], "NO_CHANGE")
            self.assertEqual(build_ror_release(root, snapshot_id)["status"], "NO_CHANGE")

    def test_unknown_schema_field_fails_instead_of_being_silently_dropped(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            record = _record("https://ror.org/03yrm5c26", "Example University")
            record["future_field"] = "unexpected"
            snapshot_id = _archive_ror(root, [record])
            with self.assertRaises(ParseError):
                parse_ror_snapshot(root, snapshot_id)


if __name__ == "__main__":
    unittest.main()
