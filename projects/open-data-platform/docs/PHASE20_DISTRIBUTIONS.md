# Phase 20 — Commercial distribution formats

## Objective

Keep the verified SQLite products as the canonical local product state while adding customer delivery representations that do not redefine product or release identity.

```text
verified product / release state
        │
        ├── SQLite      canonical/queryable product
        ├── CSV         zero-dependency distribution
        └── Parquet     optional analytics distribution
```

A format is a distribution, not a new source snapshot and not a new logical release.

## Storage layout

Generated distributions live separately from products and releases:

```text
data/distributions/<source_dataset_id>/<snapshot_id>/csv/
data/distributions/<source_dataset_id>/<snapshot_id>/parquet/
```

Each format directory contains:

```text
data.<format>
data.<format>.sha256
distribution.json
distribution.json.sha256
```

The distribution manifest binds the representation to:

- source dataset ID;
- source snapshot ID/version;
- source product type;
- exact product-manifest hash;
- exact product-database hash;
- record count and ordered columns;
- format engine/version;
- distribution byte hash and size.

## CSV

CSV uses only Python's standard library.

Rows are emitted from the verified SQLite source product in `row_id` order. `row_id` is intentionally retained because duplicate source rows are legal product state and need a stable row discriminator in a flat-file representation.

SQLite JSON columns remain canonical JSON strings rather than being flattened heuristically.

```powershell
python .\build_distribution.py <snapshot_id> csv
python .\build_distribution.py <snapshot_id> csv --verify-only
```

## Parquet

Parquet is intentionally isolated behind an optional dependency:

```toml
[project.optional-dependencies]
parquet = ["pyarrow==25.0.0"]
```

The canonical archive, normalization, SQLite product, history engine and ordinary CSV path remain dependency-free.

Install only on a distribution-builder host that actually needs Parquet:

```powershell
python -m pip install ".[parquet]"
python .\build_distribution.py <snapshot_id> parquet
```

The builder requires the exact pinned PyArrow version recorded in the distribution manifest. This avoids silently generating analytics artifacts with an unqualified engine version.

Parquet is written in bounded SQLite batches with an explicit Arrow schema derived from SQLite column types; the complete multi-million-row product is not loaded into memory.

```powershell
python .\build_distribution.py <snapshot_id> parquet --batch-size 10000
python .\build_distribution.py <snapshot_id> parquet --verify-only
```

## Verification

All distributions first re-verify the canonical source product.

Verification then checks:

- distribution manifest checksum;
- data-file checksum and byte size;
- exact source product manifest/database hashes;
- snapshot and record count;
- CSV header and physical row count; or
- Parquet metadata row count and ordered schema columns.

An existing format directory is never overwritten. A repeated build verifies the artifact and returns `NO_CHANGE`.

## Supported datasets

The same distribution layer supports both current GLEIF products:

```text
ds_gleif_lei_level1_concat
ds_gleif_rr_level2_concat
```

The builder dispatches to the correct source-product verifier and source table. It does not merge Level 1 and Level 2 merely because both can be exported to the same format.

## CI boundary

The main Linux/Windows CI jobs remain zero-dependency and prove CSV plus all core behavior.

A separate Parquet matrix installs exactly PyArrow 25.0.0 and runs only distribution-format tests on Linux and Windows. Optional-format dependency risk therefore cannot silently spread into the canonical runtime.

## Non-goals

Not added in this phase:

- customer-specific schemas;
- XLSX;
- hosted warehouse delivery;
- API pagination changes;
- cloud-specific bucket layouts;
- source/product mutation during export.
