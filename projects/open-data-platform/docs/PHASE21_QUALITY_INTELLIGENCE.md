# Phase 21 — Quality and historical intelligence

## Objective

Turn each verified product state into a compact, immutable quality profile and make the accumulated profiles queryable as a historical timeline.

This phase reports facts and deltas. It deliberately does not invent arbitrary alert thresholds before enough real history exists to justify them.

## Automatic accumulation

Both current pipelines now build quality intelligence automatically after their product/release boundary:

```text
Level 1:
raw -> normalized -> SQLite -> release -> changes -> quality profile

RR Level 2:
raw -> normalized -> SQLite -> release -> quality profile
```

The quality stage is timed inside the existing production-run metrics.

## Immutable quality profile

Each snapshot gets:

```text
data/analytics/<source_dataset_id>/<snapshot_id>/
  quality.json
  quality.json.sha256
```

The profile binds to the exact verified product and normalized-artifact hashes.

Repeated builds verify the existing profile and return `NO_CHANGE`; published quality history is never silently rewritten.

## Level 1 measures

The legal-entity profile includes:

- total source rows;
- unique LEIs;
- duplicate LEI rows and duplicate rate;
- entity-status counts;
- registration-status counts;
- missing optional-field counts from normalization;
- legal-jurisdiction counts;
- entity-category counts;
- legal-form-code counts;
- exact product and normalized-artifact hashes.

Jurisdiction/category aggregation runs inside SQLite. Legal-form codes are read as a bounded stream from the stored canonical JSON column rather than requiring SQLite JSON extensions.

## Level 2 relationship measures

RR profiles include:

- total relationship rows;
- relationship-type counts;
- relationship-status counts;
- registration-status counts;
- start-node type counts;
- end-node type counts;
- exact product and normalized-artifact hashes.

These metrics come from the already-verified RR normalization/product boundary; they do not reinterpret the relationship graph.

## Historical change intelligence

For Level 1 snapshots, if an incoming Phase 18 change artifact exists, the quality profile records the latest verified predecessor comparison:

```text
from snapshot/version
change event count
unchanged LEI count
counts by semantic change type
exact change-file/artifact hashes
```

This joins product quality and historical change intelligence without copying or mutating either source artifact.

## Previous-version deltas

When an older quality profile exists, the new profile records direct deltas such as:

```text
record_count_delta
unique_lei_count_delta
duplicate_lei_count_delta
duplicate_rate_delta
```

The previous snapshot/version is explicit.

No fixed percentage is currently labeled “bad”. The history is preserved first so future thresholds can be based on actual observed behavior.

## Timeline

Generate a read-only timeline from all verified quality profiles:

```powershell
python .\quality_intelligence.py timeline ds_gleif_lei_level1_concat
python .\quality_intelligence.py timeline ds_gleif_rr_level2_concat
```

The timeline contains one compact row per immutable snapshot with:

- source version;
- snapshot ID;
- record count;
- unique/duplicate counts where applicable;
- duplicate rate;
- incoming change-event count/type counts where applicable;
- exact quality-profile hash.

The timeline itself is assembled on demand. There is no mutable “current analytics database” that can overwrite historical evidence.

## Manual inspection

```powershell
python .\quality_intelligence.py build <snapshot_id>
python .\quality_intelligence.py verify <snapshot_id>
```

## Trust boundary

A quality profile never replaces source/product truth.

```text
Raw / normalized / product / changes
        = authoritative inputs

Quality profile
        = verified descriptive projection
```

If an underlying product, normalized artifact or historical change artifact fails verification, quality generation fails instead of publishing derived statistics from untrusted bytes.
