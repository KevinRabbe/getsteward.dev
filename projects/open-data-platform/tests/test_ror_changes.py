from __future__ import annotations

import json
import os
import sqlite3
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.errors import ChangeError
from open_data_platform.ror_changes import (
    build_ror_changes,
    build_ror_changes_around,
    verify_ror_changes,
)


DATASET = "ds_ror_organizations"
_COLUMNS = (
    "ror_id",
    "display_name",
    "status",
    "established",
    "types_json",
    "domains_json",
    "names_json",
    "external_ids_json",
    "links_json",
    "relationships_json",
    "admin_json",
)


def _canonical(value) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"))


def _row(
    ror_id: str,
    name: str,
    *,
    status: str = "active",
    established: int = 1900,
    types: list[str] | None = None,
    domains: list[str] | None = None,
    relationships: list[dict] | None = None,
    modified: str = "2026-06-23",
) -> tuple:
    return (
        ror_id,
        name,
        status,
        established,
        _canonical(types or ["education"]),
        _canonical(domains or ["example.org"]),
        _canonical([{"value": name, "types": ["ror_display"]}]),
        _canonical([]),
        _canonical([{"type": "website", "value": "https://example.org"}]),
        _canonical(relationships or []),
        _canonical({"last_modified": {"date": modified, "schema_version": "2.1"}}),
    )


def _write_db(path: Path, rows: list[tuple]) -> None:
    connection = sqlite3.connect(path)
    try:
        connection.execute(
            """
            CREATE TABLE organization (
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                ror_id TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                status TEXT NOT NULL,
                established INTEGER,
                types_json TEXT NOT NULL,
                domains_json TEXT NOT NULL,
                names_json TEXT NOT NULL,
                external_ids_json TEXT NOT NULL,
                links_json TEXT NOT NULL,
                relationships_json TEXT NOT NULL,
                admin_json TEXT NOT NULL
            )
            """
        )
        placeholders = ",".join("?" for _ in _COLUMNS)
        connection.executemany(
            f"INSERT INTO organization({','.join(_COLUMNS)}) VALUES ({placeholders})",
            rows,
        )
        connection.commit()
    finally:
        connection.close()


def _write_snapshot(root: Path, snapshot_id: str, version: str, publication_date: str) -> None:
    directory = root / "archive" / "snapshots" / snapshot_id
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "manifest.json").write_text(
        json.dumps(
            {
                "snapshot_id": snapshot_id,
                "source_dataset_id": DATASET,
                "source_version": version,
                "acquisition_response": {"publication_date": publication_date},
            }
        ),
        encoding="utf-8",
    )


def _verification(path: Path, snapshot_id: str, version: str) -> dict:
    return {
        "status": "VERIFIED",
        "snapshot_id": snapshot_id,
        "database_path": str(path),
        "record_count": 0,
        "product": {
            "source_snapshot_id": snapshot_id,
            "source_dataset_id": DATASET,
            "source_version": version,
            "product_type": "ror_organizations_sqlite",
            "manifest_sha256": f"manifest-{snapshot_id}",
            "product_sha256": f"database-{snapshot_id}",
            "excluded_source_fields": ["locations"],
        },
    }


class RorChangeTests(unittest.TestCase):
    def test_semantic_ror_changes_are_streamed_and_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            before_db = root / "before.sqlite"
            after_db = root / "after.sqlite"
            _write_db(
                before_db,
                [
                    _row("https://ror.org/01aaaaaaa", "Unchanged University"),
                    _row("https://ror.org/02bbbbbbb", "Removed Institute"),
                    _row("https://ror.org/03ccccccc", "Old Name", status="active"),
                ],
            )
            _write_db(
                after_db,
                [
                    _row("https://ror.org/01aaaaaaa", "Unchanged University"),
                    _row(
                        "https://ror.org/03ccccccc",
                        "New Name",
                        status="inactive",
                        types=["education", "facility"],
                        relationships=[
                            {
                                "id": "https://ror.org/01aaaaaaa",
                                "label": "Parent",
                                "type": "parent",
                            }
                        ],
                        modified="2026-07-20",
                    ),
                    _row("https://ror.org/04ddddddd", "New Laboratory"),
                ],
            )
            _write_snapshot(root, "snp_29", "v2.9", "2026-06-23")
            _write_snapshot(root, "snp_210", "v2.10", "2026-07-20")
            verifications = {
                "snp_29": _verification(before_db, "snp_29", "v2.9"),
                "snp_210": _verification(after_db, "snp_210", "v2.10"),
            }

            with patch(
                "open_data_platform.ror_changes.verify_ror_product",
                side_effect=lambda _root, snapshot_id, output_root=None: verifications[snapshot_id],
            ):
                built = build_ror_changes(root, "snp_29", "snp_210")
                self.assertEqual(built["status"], "BUILT")
                events = [
                    json.loads(line)
                    for line in Path(built["changes_path"]).read_text(encoding="utf-8").splitlines()
                ]
                self.assertEqual(
                    [event["ror_id"] for event in events],
                    [
                        "https://ror.org/02bbbbbbb",
                        "https://ror.org/03ccccccc",
                        "https://ror.org/04ddddddd",
                    ],
                )
                self.assertEqual(events[0]["change_types"], ["REMOVED"])
                self.assertEqual(
                    events[1]["change_types"],
                    [
                        "DISPLAY_NAME_CHANGED",
                        "STATUS_CHANGED",
                        "TYPES_CHANGED",
                        "NAMES_CHANGED",
                        "RELATIONSHIPS_CHANGED",
                        "ADMIN_CHANGED",
                    ],
                )
                self.assertEqual(events[2]["change_types"], ["NEW"])
                self.assertNotIn("locations", events[1]["before"])
                self.assertNotIn("locations", events[1]["after"])
                self.assertEqual(built["artifact"]["unchanged_ror_id_count"], 1)
                self.assertEqual(built["artifact"]["from_source_publication_date"], "2026-06-23")
                self.assertEqual(built["artifact"]["to_source_publication_date"], "2026-07-20")

                verified = verify_ror_changes(root, "snp_29", "snp_210")
                self.assertEqual(verified["status"], "VERIFIED")
                self.assertEqual(build_ror_changes(root, "snp_29", "snp_210")["status"], "NO_CHANGE")

    def test_backfill_builds_change_to_existing_newer_product(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for snapshot_id, version, publication in (
                ("snp_29", "v2.9", "2026-06-23"),
                ("snp_210", "v2.10", "2026-07-20"),
            ):
                _write_snapshot(root, snapshot_id, version, publication)
                (root / "products" / DATASET / snapshot_id).mkdir(parents=True)

            target_verification = {
                "status": "VERIFIED",
                "product": {"source_dataset_id": DATASET},
            }
            pair = {
                "status": "BUILT",
                "from_snapshot_id": "snp_29",
                "to_snapshot_id": "snp_210",
            }
            with (
                patch("open_data_platform.ror_changes.verify_ror_product", return_value=target_verification),
                patch("open_data_platform.ror_changes.build_ror_changes", return_value=pair) as build_pair,
            ):
                result = build_ror_changes_around(root, "snp_29")

            self.assertEqual(result["status"], "BUILT")
            self.assertEqual(result["pair_count"], 1)
            build_pair.assert_called_once()
            args = build_pair.call_args.args
            self.assertEqual(args[1:3], ("snp_29", "snp_210"))

    def test_change_tampering_and_reverse_chronology_fail(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            before_db = root / "before.sqlite"
            after_db = root / "after.sqlite"
            _write_db(before_db, [_row("https://ror.org/01aaaaaaa", "Alpha")])
            _write_db(after_db, [_row("https://ror.org/01aaaaaaa", "Beta")])
            _write_snapshot(root, "snp_29", "v2.9", "2026-06-23")
            _write_snapshot(root, "snp_210", "v2.10", "2026-07-20")
            verifications = {
                "snp_29": _verification(before_db, "snp_29", "v2.9"),
                "snp_210": _verification(after_db, "snp_210", "v2.10"),
            }
            with patch(
                "open_data_platform.ror_changes.verify_ror_product",
                side_effect=lambda _root, snapshot_id, output_root=None: verifications[snapshot_id],
            ):
                built = build_ror_changes(root, "snp_29", "snp_210")
                path = Path(built["changes_path"])
                os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)
                path.write_text(path.read_text(encoding="utf-8") + "{}\n", encoding="utf-8")
                with self.assertRaises(ChangeError):
                    verify_ror_changes(root, "snp_29", "snp_210")
                with self.assertRaises(ChangeError):
                    build_ror_changes(root, "snp_210", "snp_29")


if __name__ == "__main__":
    unittest.main()
