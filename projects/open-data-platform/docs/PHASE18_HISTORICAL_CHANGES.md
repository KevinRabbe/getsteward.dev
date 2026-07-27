# Phase 18 — Historical change engine

## Objective

Turn preserved daily GLEIF product states into a second immutable asset: deterministic historical change sets.

The source snapshots and per-snapshot products remain authoritative. Change artifacts are derived interpretations that can always be rebuilt from those preserved states.

## Automatic pipeline behavior

After a release is built, `run-gleif` finds the latest older verified product for the same source dataset and builds a historical change artifact.

For the first available product it returns:

```text
NO_PREVIOUS_SNAPSHOT
```

For later source versions it creates:

```text
data/changes/<source_dataset_id>/<old_snapshot>__<new_snapshot>/
  changes.jsonl
  changes.jsonl.sha256
  artifact.json
  artifact.json.sha256
```

The change stage is measured by the Phase 17 run metrics as `stage_seconds.changes`.

## Streaming comparison

The engine performs an ordered merge over the two verified SQLite products:

```text
old product rows ORDER BY lei,row_id
new product rows ORDER BY lei,row_id
→ one bounded LEI group from each side
→ deterministic change event
```

It does not load millions of records into memory.

## Semantic change types

Unique LEIs can produce:

```text
NEW
REMOVED
LEGAL_NAME_CHANGED
LEGAL_NAME_LANGUAGE_CHANGED
STATUS_CHANGED
JURISDICTION_CHANGED
ENTITY_CATEGORY_CHANGED
ENTITY_SUBCATEGORY_CHANGED
ENTITY_CREATION_DATE_CHANGED
LEGAL_FORM_CHANGED
REGISTRATION_AUTHORITY_CHANGED
LEGAL_ADDRESS_CHANGED
HEADQUARTERS_ADDRESS_CHANGED
REGISTRATION_CHANGED
```

One LEI may carry several change types in one event.

Each change event retains the complete before/after normalized product row so the derived classification remains auditable.

## Duplicate / ambiguous LEIs

The existing product deliberately preserves duplicate LEI source rows. Phase 18 does not undo that safety property.

If either side contains multiple variants for one LEI, the engine does **not** guess which old variant corresponds to which new variant.

Instead it compares the canonical variant multisets and emits an explicit ambiguity event only when that set changed:

```text
AMBIGUOUS_VARIANTS_CHANGED
NEW_AMBIGUOUS_VARIANTS
REMOVED_AMBIGUOUS_VARIANTS
```

Exact unchanged duplicate variant sets produce no change event.

## Artifact manifest

`artifact.json` binds the change file to:

- old and new snapshot IDs;
- old and new source versions;
- exact verified old/new product hashes;
- change-file SHA-256 and byte size;
- total change-event count;
- unchanged LEI count;
- counts by semantic change type;
- change-engine version.

Both the manifest and JSONL have adjacent SHA-256 sidecars.

## Verification

Run a specific comparison manually:

```powershell
python .\historical_changes.py <old_snapshot_id> <new_snapshot_id>
```

Verify an existing artifact:

```powershell
python .\historical_changes.py <old_snapshot_id> <new_snapshot_id> --verify-only
```

Verification re-verifies both product inputs and checks:

- artifact and change-file checksums;
- exact input product hashes;
- snapshot identities;
- strictly increasing LEI event order;
- JSON structure;
- event count;
- change-type counts.

## Chronology rule

The engine accepts only:

```text
older source_version < newer source_version
```

Reverse or equal source versions are rejected.

## Immutability rule

If an identical old/new snapshot pair already has a change artifact, the engine verifies it and returns `NO_CHANGE`. It never silently rebuilds over an existing artifact.

## Product value

This creates information that was not available from a single current GLEIF download:

```text
what changed
when it changed
previous state
new state
semantic change class
exact provenance
```

As the archive ages, this historical layer accumulates automatically with every new product state.
