from __future__ import annotations

import json
import os
import sqlite3
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.changes import build_changes, verify_changes
from open_data_platform.errors import ChangeError


_COLUMNS = (
    "lei",
    "legal_name",
    "legal_name_language",
    "entity_status",
    "legal_jurisdiction",
    "entity_category",
    "entity_subcategory",
    "entity_creation_date",
    "legal_form_json",
    "registration_authority_json",
    "legal_address_json",
    "headquarters_address_json",
    "registration_json",
)


def _row(
    lei: str,
    name: str,
    *,
    status: str = "ACTIVE",
    jurisdiction: str = "DE",
    legal_form: str = "FORM-A",
    registration_status: str = "ISSUED",
) -> tuple:
    return (
        lei,
        name,
        "en",
        status,
        jurisdiction,
        "GENERAL",
        None,
        "2000-01-01T00:00:00Z",
        json.dumps({"code": legal_form}, sort_keys=True, separators=(",", ":")),
        json.dumps({"authority_id": "RA1", "entity_id": "REG1"}, sort_keys=True, separators=(",", ":")),
        json.dumps({"country": jurisdiction, "city": "City"}, sort_keys=True, separators=(",", ":")),
        json.dumps({"country": jurisdiction, "city": "City"}, sort_keys=True, separators=(",", ":")),
        json.dumps({"status": registration_status, "managing_lou": "LOU1"}, sort_keys=True, separators=(",", ":")),
    )


def _write_product_db(path: Path, rows: list[tuple]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    try:
        connection.execute(
            """
            CREATE TABLE lei (
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                lei TEXT NOT NULL,
                legal_name TEXT NOT NULL,
                legal_name_language TEXT,
                entity_status TEXT NOT NULL,
                legal_jurisdiction TEXT,
                entity_category TEXT,
                entity_subcategory TEXT,
                entity_creation_date TEXT,
                legal_form_json TEXT,
                registration_authority_json TEXT,
                legal_address_json TEXT NOT NULL,
                headquarters_address_json TEXT NOT NULL,
                registration_json TEXT NOT NULL
            )
            """
        )
        placeholders = ",".join("?" for _ in _COLUMNS)
        connection.executemany(
            f"INSERT INTO lei({','.join(_COLUMNS)}) VALUES ({placeholders})",
            rows,
        )
        connection.commit()
    finally:
        connection.close()


def _verification(path: Path, snapshot_id: str, source_version: str, product_hash: str) -> dict:
    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "database_path": str(path),
        "product": {
            "source_snapshot_id": snapshot_id,
            "source_dataset_id": "ds_gleif_lei_level1_concat",
            "source_version": source_version,
            "product_sha256": product_hash,
        },
    }


class HistoricalChangeTests(unittest.TestCase):
    def test_streaming_change_engine_preserves_semantics_and_ambiguity(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            before_db = root / "before.sqlite"
            after_db = root / "after.sqlite"
            _write_product_db(
                before_db,
                [
                    _row("A", "Alpha GmbH"),
                    _row("B", "Removed AG"),
                    _row("C", "Duplicate One"),
                    _row("C", "Duplicate Two"),
                ],
            )
            _write_product_db(
                after_db,
                [
                    _row("A", "Alpha Technologies GmbH", status="INACTIVE"),
                    _row("C", "Duplicate One"),
                    _row("C", "Duplicate Three"),
                    _row("D", "New SE", jurisdiction="FR"),
                ],
            )
            verifications = {
                "snp_old": _verification(before_db, "snp_old", "2026-07-26", "old-product-hash"),
                "snp_new": _verification(after_db, "snp_new", "2026-07-27", "new-product-hash"),
            }

            with patch(
                "open_data_platform.changes.verify_product",
                side_effect=lambda _root, snapshot_id, output_root=None: verifications[snapshot_id],
            ):
                result = build_changes(root, "snp_old", "snp_new")
                self.assertEqual(result["status"], "BUILT")
                events = [
                    json.loads(line)
                    for line in Path(result["changes_path"]).read_text(encoding="utf-8").splitlines()
                ]
                self.assertEqual([event["lei"] for event in events], ["A", "B", "C", "D"])
                self.assertEqual(
                    events[0]["change_types"],
                    ["LEGAL_NAME_CHANGED", "STATUS_CHANGED"],
                )
                self.assertEqual(events[1]["change_types"], ["REMOVED"])
                self.assertEqual(events[2]["change_types"], ["AMBIGUOUS_VARIANTS_CHANGED"])
                self.assertEqual(events[2]["before_variant_count"], 2)
                self.assertEqual(events[2]["after_variant_count"], 2)
                self.assertEqual(events[3]["change_types"], ["NEW"])

                verified = verify_changes(root, "snp_old", "snp_new")
                self.assertEqual(verified["status"], "VERIFIED")
                self.assertEqual(verified["artifact"]["change_event_count"], 4)
                self.assertEqual(
                    build_changes(root, "snp_old", "snp_new")["status"],
                    "NO_CHANGE",
                )

    def test_change_artifact_tampering_is_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            before_db = root / "before.sqlite"
            after_db = root / "after.sqlite"
            _write_product_db(before_db, [_row("A", "Alpha")])
            _write_product_db(after_db, [_row("A", "Beta")])
            verifications = {
                "snp_old": _verification(before_db, "snp_old", "2026-07-26", "old-product-hash"),
                "snp_new": _verification(after_db, "snp_new", "2026-07-27", "new-product-hash"),
            }

            with patch(
                "open_data_platform.changes.verify_product",
                side_effect=lambda _root, snapshot_id, output_root=None: verifications[snapshot_id],
            ):
                result = build_changes(root, "snp_old", "snp_new")
                changes_path = Path(result["changes_path"])
                os.chmod(changes_path, stat.S_IRUSR | stat.S_IWUSR)
                changes_path.write_text(changes_path.read_text(encoding="utf-8") + "{}\n", encoding="utf-8")
                with self.assertRaises(ChangeError):
                    verify_changes(root, "snp_old", "snp_new")

    def test_reverse_or_equal_source_versions_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            first_db = root / "first.sqlite"
            second_db = root / "second.sqlite"
            _write_product_db(first_db, [_row("A", "Alpha")])
            _write_product_db(second_db, [_row("A", "Beta")])
            verifications = {
                "snp_old": _verification(first_db, "snp_old", "2026-07-27", "old-product-hash"),
                "snp_new": _verification(second_db, "snp_new", "2026-07-26", "new-product-hash"),
            }
            with patch(
                "open_data_platform.changes.verify_product",
                side_effect=lambda _root, snapshot_id, output_root=None: verifications[snapshot_id],
            ):
                with self.assertRaises(ChangeError):
                    build_changes(root, "snp_old", "snp_new")


if __name__ == "__main__":
    unittest.main()
