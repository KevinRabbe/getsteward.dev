from __future__ import annotations

import csv
import importlib.util
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from open_data_platform.distributions import (
    build_csv_distribution,
    build_parquet_distribution,
    verify_distribution,
)


HAS_PYARROW = importlib.util.find_spec("pyarrow") is not None


def _source_product(root: Path) -> dict:
    database = root / "product.sqlite"
    connection = sqlite3.connect(database)
    try:
        connection.executescript(
            """
            CREATE TABLE lei (
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                lei TEXT NOT NULL,
                legal_name TEXT NOT NULL,
                entity_status TEXT NOT NULL,
                extra_json TEXT
            );
            INSERT INTO lei(lei, legal_name, entity_status, extra_json)
            VALUES
              ('529900T8BM49AURSDO55', 'Alpha AG', 'ACTIVE', '{"a":1}'),
              ('5493001KJTIIGC8Y1R12', 'Beta GmbH', 'INACTIVE', NULL);
            """
        )
        connection.commit()
    finally:
        connection.close()
    return {
        "dataset_id": "ds_gleif_lei_level1_concat",
        "snapshot_id": "snp_test",
        "source_version": "2026-07-27",
        "product_type": "gleif_level1_sqlite",
        "product_manifest_sha256": "manifest-hash",
        "product_database_sha256": "database-hash",
        "database_path": str(database),
        "table": "lei",
        "record_count": 2,
    }


class DistributionTests(unittest.TestCase):
    def test_builds_and_verifies_deterministic_csv_distribution(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = _source_product(root)
            with patch("open_data_platform.distributions._verified_source_product", return_value=source):
                first = build_csv_distribution(root, "snp_test")
                self.assertEqual(first["status"], "BUILT")
                data_path = Path(first["data_path"])
                with data_path.open("r", encoding="utf-8", newline="") as handle:
                    rows = list(csv.reader(handle))
                self.assertEqual(rows[0], ["row_id", "lei", "legal_name", "entity_status", "extra_json"])
                self.assertEqual(rows[1][1:4], ["529900T8BM49AURSDO55", "Alpha AG", "ACTIVE"])
                self.assertEqual(rows[2][-1], "")

                verified = verify_distribution(root, "snp_test", "csv")
                self.assertEqual(verified["status"], "VERIFIED")
                self.assertEqual(verified["distribution"]["record_count"], 2)
                self.assertEqual(build_csv_distribution(root, "snp_test")["status"], "NO_CHANGE")

    @unittest.skipUnless(HAS_PYARROW, "optional pyarrow distribution dependency not installed")
    def test_builds_and_verifies_streamed_parquet_distribution(self):
        import pyarrow.parquet as pq

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = _source_product(root)
            with patch("open_data_platform.distributions._verified_source_product", return_value=source):
                first = build_parquet_distribution(root, "snp_test", batch_size=1)
                self.assertEqual(first["status"], "BUILT")
                table = pq.read_table(first["data_path"])
                self.assertEqual(table.num_rows, 2)
                self.assertEqual(table.column_names, ["row_id", "lei", "legal_name", "entity_status", "extra_json"])
                self.assertEqual(table.column("lei").to_pylist()[0], "529900T8BM49AURSDO55")

                verified = verify_distribution(root, "snp_test", "parquet")
                self.assertEqual(verified["status"], "VERIFIED")
                self.assertEqual(
                    verified["distribution"]["engine"],
                    {"name": "pyarrow", "version": "25.0.0"},
                )
                self.assertEqual(build_parquet_distribution(root, "snp_test")["status"], "NO_CHANGE")


if __name__ == "__main__":
    unittest.main()
