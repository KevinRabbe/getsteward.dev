from __future__ import annotations

import csv
import json
import shutil
import sqlite3
import uuid
from pathlib import Path
from typing import Any, Iterator

from .errors import ProductError
from .product import verify_product
from .relationship_product import verify_relationship_product
from .util import atomic_write_json, load_json, make_read_only, sha256_file, utc_now_iso


_DISTRIBUTION_VERSION = "0.1.0"
_PARQUET_ENGINE_VERSION = "25.0.0"
_LEVEL1_DATASET = "ds_gleif_lei_level1_concat"
_RR_DATASET = "ds_gleif_rr_level2_concat"


def distribution_path(
    data_root: Path,
    source_dataset_id: str,
    snapshot_id: str,
    format_name: str,
    output_root: Path | None = None,
) -> Path:
    root = output_root if output_root is not None else data_root / "distributions"
    return root / source_dataset_id / snapshot_id / format_name.lower()


def _write_checksum(path: Path) -> str:
    digest, _ = sha256_file(path)
    sidecar = path.with_name(path.name + ".sha256")
    sidecar.write_text(f"{digest}  {path.name}\n", encoding="ascii")
    make_read_only(sidecar)
    return digest


def _verify_checksum(path: Path) -> str:
    sidecar = path.with_name(path.name + ".sha256")
    if not path.exists() or not sidecar.exists():
        raise ProductError(f"Missing distribution file or checksum: {path}")
    try:
        expected = sidecar.read_text(encoding="ascii").split()[0]
    except (OSError, IndexError) as exc:
        raise ProductError(f"Invalid distribution checksum sidecar: {sidecar}") from exc
    actual, _ = sha256_file(path)
    if expected != actual:
        raise ProductError(f"Distribution checksum mismatch: {path}")
    return actual


def _snapshot_dataset_id(data_root: Path, snapshot_id: str) -> str:
    manifest_path = data_root / "archive" / "snapshots" / snapshot_id / "manifest.json"
    manifest = load_json(manifest_path)
    value = manifest.get("source_dataset_id")
    if not isinstance(value, str) or not value:
        raise ProductError("Snapshot manifest has no source_dataset_id")
    return value


def _verified_source_product(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None = None,
) -> dict[str, Any]:
    dataset_id = _snapshot_dataset_id(data_root, snapshot_id)
    if dataset_id == _LEVEL1_DATASET:
        verified = verify_product(data_root, snapshot_id, output_root=product_root)
        product = verified["product"]
        return {
            "dataset_id": dataset_id,
            "snapshot_id": snapshot_id,
            "source_version": product["source_version"],
            "product_type": product["product_type"],
            "product_manifest_sha256": product["product_sha256"],
            "product_database_sha256": product["database_sha256"],
            "database_path": verified["database_path"],
            "table": "lei",
            "record_count": verified["record_count"],
        }
    if dataset_id == _RR_DATASET:
        verified = verify_relationship_product(data_root, snapshot_id, output_root=product_root)
        product = verified["product"]
        return {
            "dataset_id": dataset_id,
            "snapshot_id": snapshot_id,
            "source_version": product["source_version"],
            "product_type": product["product_type"],
            "product_manifest_sha256": product["manifest_sha256"],
            "product_database_sha256": product["product_sha256"],
            "database_path": verified["database_path"],
            "table": "relationship",
            "record_count": verified["record_count"],
        }
    raise ProductError(f"No distribution builder registered for dataset {dataset_id}")


def _open_read_only(path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    return connection


def _table_columns(connection: sqlite3.Connection, table: str) -> list[tuple[str, str]]:
    rows = connection.execute(f"PRAGMA table_info({table})").fetchall()
    if not rows:
        raise ProductError(f"Source product table not found: {table}")
    return [(str(row[1]), str(row[2]).upper()) for row in rows]


def _ordered_rows(connection: sqlite3.Connection, table: str) -> Iterator[sqlite3.Row]:
    yield from connection.execute(f"SELECT * FROM {table} ORDER BY row_id")


def _publish_manifest(
    temporary_dir: Path,
    *,
    source: dict[str, Any],
    format_name: str,
    data_file: str,
    data_sha256: str,
    data_size_bytes: int,
    columns: list[str],
    engine: dict[str, str],
) -> tuple[dict[str, Any], str]:
    manifest = {
        "distribution_version": 1,
        "builder_version": _DISTRIBUTION_VERSION,
        "format": format_name,
        "source_dataset_id": source["dataset_id"],
        "source_snapshot_id": source["snapshot_id"],
        "source_version": source["source_version"],
        "source_product_type": source["product_type"],
        "source_product_manifest_sha256": source["product_manifest_sha256"],
        "source_product_database_sha256": source["product_database_sha256"],
        "record_count": source["record_count"],
        "columns": columns,
        "data_file": data_file,
        "data_sha256": data_sha256,
        "data_size_bytes": data_size_bytes,
        "engine": engine,
        "created_at": utc_now_iso(),
    }
    manifest_path = temporary_dir / "distribution.json"
    atomic_write_json(manifest_path, manifest)
    make_read_only(manifest_path)
    manifest_hash = _write_checksum(manifest_path)
    return manifest, manifest_hash


def build_csv_distribution(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    source = _verified_source_product(data_root, snapshot_id, product_root=product_root)
    final_dir = distribution_path(data_root, source["dataset_id"], snapshot_id, "csv", output_root)
    if final_dir.exists():
        verified = verify_distribution(
            data_root,
            snapshot_id,
            "csv",
            product_root=product_root,
            output_root=output_root,
        )
        verified["status"] = "NO_CHANGE"
        return verified

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{final_dir.name}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    connection = _open_read_only(Path(source["database_path"]))
    try:
        columns_with_types = _table_columns(connection, source["table"])
        columns = [name for name, _ in columns_with_types]
        csv_path = temporary_dir / "data.csv"
        count = 0
        with csv_path.open("w", encoding="utf-8", newline="") as output:
            writer = csv.writer(output, lineterminator="\n")
            writer.writerow(columns)
            for row in _ordered_rows(connection, source["table"]):
                writer.writerow([row[column] if row[column] is not None else "" for column in columns])
                count += 1
            output.flush()
        if count != source["record_count"]:
            raise ProductError(
                f"CSV distribution row count mismatch: product {source['record_count']}, wrote {count}"
            )
        data_hash, data_size = sha256_file(csv_path)
        make_read_only(csv_path)
        _write_checksum(csv_path)
        manifest, manifest_hash = _publish_manifest(
            temporary_dir,
            source=source,
            format_name="csv",
            data_file="data.csv",
            data_sha256=data_hash,
            data_size_bytes=data_size,
            columns=columns,
            engine={"name": "python-csv", "version": "stdlib"},
        )
        temporary_dir.rename(final_dir)
    except Exception:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        raise
    finally:
        connection.close()

    return {
        "status": "BUILT",
        "format": "csv",
        "snapshot_id": snapshot_id,
        "distribution_dir": str(final_dir),
        "data_path": str(final_dir / "data.csv"),
        "distribution": {**manifest, "manifest_sha256": manifest_hash},
    }


def _import_pyarrow() -> tuple[Any, Any, str]:
    try:
        import pyarrow as pa
        import pyarrow.parquet as pq
    except ImportError as exc:
        raise ProductError(
            "Parquet distribution requires the optional dependency: "
            "python -m pip install '.[parquet]'"
        ) from exc
    version = str(pa.__version__)
    if version != _PARQUET_ENGINE_VERSION:
        raise ProductError(
            f"Parquet engine version mismatch: expected {_PARQUET_ENGINE_VERSION}, got {version}"
        )
    return pa, pq, version


def _arrow_type(pa: Any, sqlite_type: str) -> Any:
    if "INT" in sqlite_type:
        return pa.int64()
    if any(token in sqlite_type for token in ("REAL", "FLOA", "DOUB")):
        return pa.float64()
    if "BLOB" in sqlite_type:
        return pa.binary()
    return pa.string()


def build_parquet_distribution(
    data_root: Path,
    snapshot_id: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
    batch_size: int = 10_000,
) -> dict[str, Any]:
    if batch_size < 1:
        raise ValueError("batch_size must be at least 1")
    pa, pq, pyarrow_version = _import_pyarrow()
    source = _verified_source_product(data_root, snapshot_id, product_root=product_root)
    final_dir = distribution_path(data_root, source["dataset_id"], snapshot_id, "parquet", output_root)
    if final_dir.exists():
        verified = verify_distribution(
            data_root,
            snapshot_id,
            "parquet",
            product_root=product_root,
            output_root=output_root,
        )
        verified["status"] = "NO_CHANGE"
        return verified

    final_dir.parent.mkdir(parents=True, exist_ok=True)
    temporary_dir = final_dir.parent / f".{final_dir.name}.tmp-{uuid.uuid4().hex}"
    temporary_dir.mkdir(parents=False, exist_ok=False)
    connection = _open_read_only(Path(source["database_path"]))
    writer = None
    try:
        columns_with_types = _table_columns(connection, source["table"])
        columns = [name for name, _ in columns_with_types]
        schema = pa.schema([(name, _arrow_type(pa, sqlite_type)) for name, sqlite_type in columns_with_types])
        parquet_path = temporary_dir / "data.parquet"
        writer = pq.ParquetWriter(
            parquet_path,
            schema,
            compression="zstd",
            use_dictionary=True,
            write_statistics=True,
        )
        cursor = connection.execute(f"SELECT * FROM {source['table']} ORDER BY row_id")
        count = 0
        while True:
            rows = cursor.fetchmany(batch_size)
            if not rows:
                break
            records = [{column: row[column] for column in columns} for row in rows]
            table = pa.Table.from_pylist(records, schema=schema)
            writer.write_table(table)
            count += len(rows)
        writer.close()
        writer = None
        if count != source["record_count"]:
            raise ProductError(
                f"Parquet distribution row count mismatch: product {source['record_count']}, wrote {count}"
            )
        data_hash, data_size = sha256_file(parquet_path)
        make_read_only(parquet_path)
        _write_checksum(parquet_path)
        manifest, manifest_hash = _publish_manifest(
            temporary_dir,
            source=source,
            format_name="parquet",
            data_file="data.parquet",
            data_sha256=data_hash,
            data_size_bytes=data_size,
            columns=columns,
            engine={"name": "pyarrow", "version": pyarrow_version},
        )
        temporary_dir.rename(final_dir)
    except Exception:
        if writer is not None:
            writer.close()
        shutil.rmtree(temporary_dir, ignore_errors=True)
        raise
    finally:
        connection.close()

    return {
        "status": "BUILT",
        "format": "parquet",
        "snapshot_id": snapshot_id,
        "distribution_dir": str(final_dir),
        "data_path": str(final_dir / "data.parquet"),
        "distribution": {**manifest, "manifest_sha256": manifest_hash},
    }


def verify_distribution(
    data_root: Path,
    snapshot_id: str,
    format_name: str,
    *,
    product_root: Path | None = None,
    output_root: Path | None = None,
) -> dict[str, Any]:
    source = _verified_source_product(data_root, snapshot_id, product_root=product_root)
    format_name = format_name.lower()
    if format_name not in {"csv", "parquet"}:
        raise ProductError(f"Unsupported distribution format: {format_name}")
    final_dir = distribution_path(data_root, source["dataset_id"], snapshot_id, format_name, output_root)
    manifest_path = final_dir / "distribution.json"
    manifest_hash = _verify_checksum(manifest_path)
    manifest = load_json(manifest_path)
    data_path = final_dir / str(manifest.get("data_file", ""))
    data_hash = _verify_checksum(data_path)
    actual_hash, actual_size = sha256_file(data_path)
    if data_hash != actual_hash or actual_hash != manifest.get("data_sha256"):
        raise ProductError("Distribution data hash does not match manifest")
    if actual_size != manifest.get("data_size_bytes"):
        raise ProductError("Distribution data size does not match manifest")
    if manifest.get("source_snapshot_id") != snapshot_id:
        raise ProductError("Distribution belongs to a different snapshot")
    if manifest.get("source_product_manifest_sha256") != source["product_manifest_sha256"]:
        raise ProductError("Distribution source product manifest changed")
    if manifest.get("source_product_database_sha256") != source["product_database_sha256"]:
        raise ProductError("Distribution source product database changed")
    if manifest.get("record_count") != source["record_count"]:
        raise ProductError("Distribution record count does not match source product")

    if format_name == "csv":
        with data_path.open("r", encoding="utf-8", newline="") as source_file:
            reader = csv.reader(source_file)
            try:
                header = next(reader)
            except StopIteration as exc:
                raise ProductError("CSV distribution is empty") from exc
            if header != manifest.get("columns"):
                raise ProductError("CSV distribution header does not match manifest")
            row_count = sum(1 for _ in reader)
        if row_count != manifest["record_count"]:
            raise ProductError("CSV distribution row count does not match manifest")
    else:
        pa, pq, version = _import_pyarrow()
        del pa
        if manifest.get("engine") != {"name": "pyarrow", "version": version}:
            raise ProductError("Parquet engine identity does not match distribution manifest")
        metadata = pq.read_metadata(data_path)
        if metadata.num_rows != manifest["record_count"]:
            raise ProductError("Parquet distribution row count does not match manifest")
        parquet_columns = list(metadata.schema.names)
        if parquet_columns != manifest.get("columns"):
            raise ProductError("Parquet schema columns do not match manifest")

    return {
        "status": "VERIFIED",
        "format": format_name,
        "snapshot_id": snapshot_id,
        "distribution_dir": str(final_dir),
        "data_path": str(data_path),
        "distribution": {**manifest, "manifest_sha256": manifest_hash},
    }
