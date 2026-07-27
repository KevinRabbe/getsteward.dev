from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.errors import ParseError, QueryError
from open_data_platform.ror_parser import canonical_ror_id, normalize_ror_record
from open_data_platform.ror_query import lookup_ror


def _record(ror_id: str, relationship_id: str) -> dict:
    return {
        "admin": {
            "created": {"date": "2018-11-14", "schema_version": "1.0"},
            "last_modified": {"date": "2026-07-20", "schema_version": "2.1"},
        },
        "domains": ["example.org"],
        "established": 1900,
        "external_ids": [],
        "id": ror_id,
        "links": [],
        "locations": [],
        "names": [
            {"lang": "en", "types": ["ror_display", "label"], "value": "Example Organization"}
        ],
        "relationships": [
            {"id": relationship_id, "label": "Parent Organization", "type": "parent"}
        ],
        "status": "active",
        "types": ["education"],
    }


class RorIdentifierExpansionTests(unittest.TestCase):
    def test_accepts_legacy_and_expanded_canonical_ids(self):
        legacy = "https://ror.org/03yrm5c26"
        expanded = "https://ror.org/0108w9x786"
        self.assertEqual(canonical_ror_id(legacy), legacy)
        self.assertEqual(canonical_ror_id("03yrm5c26"), legacy)
        self.assertEqual(canonical_ror_id("03YRM5C26"), legacy)
        self.assertEqual(canonical_ror_id(expanded), expanded)
        self.assertEqual(canonical_ror_id("0108w9x786"), expanded)
        self.assertEqual(canonical_ror_id("0108W9X786"), expanded)

        normalized = normalize_ror_record(_record(expanded, legacy), 1)
        self.assertEqual(normalized["ror_id"], expanded)
        self.assertEqual(normalized["relationships"][0]["id"], legacy)

    def test_source_records_remain_strictly_canonical(self):
        legacy = "https://ror.org/03yrm5c26"
        record = _record("https://ror.org/03YRM5C26", legacy)
        with self.assertRaises(ParseError):
            normalize_ror_record(record, 1)

    def test_rejects_noncanonical_ror_identifiers(self):
        for value in (
            "https://ror.org/not-an-id",
            "https://example.org/03yrm5c26",
            "13yrm5c26",
            "03yrm5c2",
            "0108w9x7860",
        ):
            with self.subTest(value=value):
                with self.assertRaises(ParseError):
                    canonical_ror_id(value)

    def test_lookup_uses_same_expanded_identifier_rule(self):
        expanded = "https://ror.org/0108w9x786"
        row = {
            "ror_id": expanded,
            "display_name": "Expanded ID Organization",
            "status": "active",
            "established": 2026,
            "types_json": "[]",
            "domains_json": "[]",
            "names_json": "[]",
            "external_ids_json": "[]",
            "links_json": "[]",
            "relationships_json": "[]",
            "admin_json": "{}",
        }

        class FakeCursor:
            def fetchone(self):
                return row

        class FakeConnection:
            row_factory = None

            def execute(self, _sql, params):
                self.params = params
                return FakeCursor()

            def close(self):
                return None

        fake_connection = FakeConnection()
        verified = {
            "database_path": str(Path(tempfile.gettempdir()) / "unused.sqlite"),
            "product": {
                "source_version": "v2.10",
                "excluded_source_fields": ["locations"],
                "product_sha256": "hash",
            },
        }
        with (
            patch("open_data_platform.ror_query.verify_ror_product", return_value=verified),
            patch("open_data_platform.ror_query.sqlite3.connect", return_value=fake_connection),
        ):
            result = lookup_ror(Path("data"), "snp_test", "0108W9X786")
        self.assertEqual(result["status"], "FOUND")
        self.assertEqual(result["ror_id"], expanded)
        self.assertEqual(fake_connection.params, (expanded,))

        with self.assertRaises(QueryError):
            lookup_ror(Path("data"), "snp_test", "invalid")


if __name__ == "__main__":
    unittest.main()
