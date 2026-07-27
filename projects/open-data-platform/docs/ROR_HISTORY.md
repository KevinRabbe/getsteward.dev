# ROR historical backfill and semantic change assets

## Objective

Turn the independent ROR database into an accumulating historical asset rather than a current-state-only product.

This slice adds two capabilities:

1. explicit ingestion of a concrete historical Zenodo ROR release;
2. deterministic semantic differences between adjacent verified ROR product states.

The implementation is intentionally ROR-specific. It does not force the GLEIF change model into a different identifier/schema domain.

## Explicit historical backfill

The normal ROR command still discovers the current release through the Zenodo concept record.

Historical import uses a concrete immutable Zenodo record ID:

```powershell
python .\ror_registry.py backfill 20818161
```

For the first backfill target, record `20818161` is ROR v2.9, published 2026-06-23.

The current v2.10 state is Zenodo record `21458494`, published 2026-07-20.

The explicit path resolves:

```text
https://zenodo.org/api/records/<record_id>
```

and then applies the same trust boundary as latest-release ingestion:

```text
exact Zenodo record identity
→ exactly one ROR data ZIP
→ publisher MD5 verification
→ immutable SHA-256 archive
→ normal ROR normalization/product/release pipeline
```

A requested record ID must equal the ID returned by Zenodo metadata. Redirecting an explicit historical request to a different record fails closed.

The source version remains the publisher version (`v2.9`, `v2.10`, ...); the Zenodo publication date remains the chronological authority.

## Backfill does not assume oldest-first ingestion

Real archives are often discovered out of order.

For example, this repository already had v2.10 before v2.9 was selected for backfill.

Therefore the change builder does not only compare a newly processed snapshot with an older predecessor. It finds the verified product states immediately adjacent to the inserted state:

```text
previous < inserted < next
```

and can build:

```text
previous → inserted
inserted → next
```

If only the newer state exists:

```text
v2.9 backfilled after v2.10
→ build v2.9 → v2.10
```

If a missing middle version is inserted later, new adjacent pairs are created without deleting earlier derived artifacts.

Historical artifacts are append-only derived evidence; superseded/non-adjacent comparisons may remain preserved.

## ROR semantic change engine

ROR products guarantee unique `ror_id`, so comparison is simpler than the duplicate-aware GLEIF Level 1 engine.

The engine performs a bounded ordered merge:

```text
old organizations.sqlite ORDER BY ror_id
new organizations.sqlite ORDER BY ror_id
→ compare one current row from each side
→ immutable changes.jsonl
```

The complete products are never loaded into memory.

## Change types

The ROR engine emits:

```text
NEW
REMOVED
DISPLAY_NAME_CHANGED
STATUS_CHANGED
ESTABLISHED_CHANGED
TYPES_CHANGED
DOMAINS_CHANGED
NAMES_CHANGED
EXTERNAL_IDS_CHANGED
LINKS_CHANGED
RELATIONSHIPS_CHANGED
ADMIN_CHANGED
```

One organization may carry several semantic change types in a single event.

For changed organizations, each event retains the complete filtered normalized state before and after the change.

## Field-rights boundary remains intact

ROR `locations` is excluded from the normalized/product layer under the existing conservative rights rule.

Historical change events are generated only from the verified filtered SQLite products, therefore:

```text
Raw source history
  locations preserved

Commercial/product history
  locations absent
```

The engine and verifier both explicitly reject a historical event if `locations` appears in a before/after payload.

Backfill therefore increases historical depth without silently broadening the legal product boundary.

## Historical artifact layout

Each comparison is stored under the common changes hierarchy:

```text
data/changes/ds_ror_organizations/<old_snapshot>__<new_snapshot>/
  changes.jsonl
  changes.jsonl.sha256
  artifact.json
  artifact.json.sha256
```

The manifest binds the change file to:

- old/new snapshot IDs;
- old/new source versions;
- old/new publication dates;
- exact old/new product-manifest hashes;
- exact old/new SQLite hashes;
- excluded field declaration;
- change file hash/size;
- total change-event count;
- unchanged ROR-ID count;
- counts by semantic change type;
- change-engine version.

## Verification

Manual build:

```powershell
python .\ror_registry.py changes <old_snapshot_id> <new_snapshot_id>
```

Verification only:

```powershell
python .\ror_registry.py changes <old_snapshot_id> <new_snapshot_id> --verify-only
```

Verification re-verifies both ROR products and checks:

- chronological publication-date order;
- exact product hashes;
- artifact/data checksums;
- snapshot identities;
- field-exclusion boundary;
- strict `ror_id` event ordering;
- event JSON structure;
- event/type counts.

An existing pair is never overwritten. A repeated build verifies it and returns `NO_CHANGE`.

## Pipeline order

Latest and explicit historical ROR runs now execute:

```text
acquire
→ parse
→ product
→ release
→ build adjacent historical changes
→ quality profile
→ run metrics
```

The `changes` stage is measured independently.

A historical release imported after a newer release can therefore create the missing adjacent change asset in the same run.

## Immutable quality profiles are not rewritten

A previously published quality profile is not modified merely because an older source version is discovered later.

Example:

```text
v2.10 quality profile already immutable
↓
backfill v2.9
↓
v2.9 → v2.10 change artifact created
↓
v2.10 quality.json remains byte-identical
```

The historical change artifact is a separate authoritative derived object and can be consumed directly by commercial history packages.

Future ROR releases processed after this engine exists can accumulate adjacent change assets before their new quality profile is frozen.

## First empirical pair

Official ROR release metadata provides a high-value acceptance pair:

```text
v2.9
Zenodo record: 20818161
Published: 2026-06-23
Organizations: 129,046

v2.10
Zenodo record: 21458494
Published: 2026-07-20
Organizations: 132,537
```

ROR's v2.10 release notes state that the release adds 3,491 new records and updates metadata for 2,920 existing records.

Those values are empirical acceptance evidence, not hard-coded change-engine rules. The engine must derive its result from the verified products.

## Scope boundary

Not included here:

- automatic crawling of every historical Zenodo version;
- deletion of older non-adjacent derived comparisons;
- rewriting old quality profiles;
- ROR location redistribution;
- GLEIF ↔ ROR entity resolution;
- a customer-facing ROR commercial bundle.

The next product step is to use the current ROR state, verified history, quality timeline, and CSV/Parquet representations to build a separate ROR commercial product candidate.
