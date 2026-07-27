# ROR product maturity — distributions and quality chronology

## Objective

Move the independently proven ROR database onto the same mature product rails already used by GLEIF without merging the databases or inventing a second exporter/analytics framework.

This slice adds exactly two things to the ROR product:

1. shared CSV / optional Parquet distributions;
2. automatic immutable quality profiles and history ordering.

The canonical ROR SQLite product and its conservative field-level legal boundary remain unchanged.

## Distribution reuse

The common distribution layer now recognizes:

```text
ds_gleif_lei_level1_concat
ds_gleif_rr_level2_concat
ds_ror_organizations
```

ROR maps to its already verified product authority:

```text
organizations.sqlite
└── table: organization
```

The existing generic builders then produce:

```text
CSV      zero-dependency
Parquet  optional pyarrow==25.0.0
```

No ROR-specific CSV or Parquet writer exists.

Every distribution is still bound to:

- exact source snapshot/version;
- exact verified product manifest/database hashes;
- row count;
- ordered columns;
- format engine/version;
- output SHA-256 and byte size.

## Field-rights preservation

The ROR source ZIP still contains `locations` and remains preserved unchanged in Raw.

The canonical ROR SQLite product intentionally excludes `locations`.

Because distributions are derived only from that verified SQLite product, CSV and Parquet inherit the same field boundary automatically:

```text
Raw source
  locations present

Normalized ROR
  locations absent

organizations.sqlite
  locations absent

CSV / Parquet
  locations absent
```

The integration test builds a real ROR fixture CSV and verifies that neither `locations` nor `locations_json` appears in its schema.

A future location-enabled product still requires a separately versioned legal/attribution rule. Distribution support does not broaden admission.

## Automatic quality intelligence

The normal ROR pipeline now continues after release packaging:

```text
Zenodo source
→ immutable Raw
→ normalized ROR
→ organizations.sqlite
→ release
→ immutable quality profile
→ production metrics
```

The `analytics` stage is part of the measured pipeline contract.

ROR quality profiles contain:

- record count;
- organization status counts;
- organization-type counts;
- relationship-type counts;
- number of source records whose `locations` data were deliberately excluded;
- explicit `excluded_source_fields = ["locations"]`;
- exact product manifest/database hashes;
- exact normalized-artifact hash.

No arbitrary quality score or threshold is invented.

## Version identity is not chronology

GLEIF Level 1 uses ISO dates as source versions, so its version label also happens to sort chronologically.

ROR does not:

```text
v2.9
v2.10
```

Lexically, `v2.10 < v2.9`, which is wrong historically.

The shared rule is therefore now explicit:

> Source version identifies a publisher release. Source publication date orders releases.

ROR snapshot manifests already preserve the Zenodo publication date. New analytics profiles copy that value into:

```text
source_publication_date
```

Previous-version selection and timelines order by:

```text
(source_publication_date, source_version)
```

not by the version string alone.

For backwards compatibility, old immutable GLEIF analytics profiles that predate this field remain valid because their `source_version` itself is an ISO date.

Non-date version labels without an explicit publication date fail closed instead of being lexically guessed.

## Historical example locked by tests

The ROR chronology regression models:

```text
v2.9   2026-06-23   129,046 organizations
v2.10  2026-07-20   132,537 organizations
```

The quality system must identify v2.9 as the predecessor of v2.10 and record a row-count delta of 3,491 regardless of lexical version ordering.

These numbers are test evidence only; product logic does not hard-code expected ROR release sizes.

## Commands

The existing common distribution command now accepts a ROR snapshot ID directly:

```powershell
python .\build_distribution.py <ror_snapshot_id> csv
```

Optional Parquet:

```powershell
python -m pip install ".[parquet]"
python .\build_distribution.py <ror_snapshot_id> parquet
```

Quality profile inspection uses the existing shared launcher:

```powershell
python .\quality_intelligence.py build <ror_snapshot_id>
python .\quality_intelligence.py verify <ror_snapshot_id>
python .\quality_intelligence.py timeline ds_ror_organizations
```

Routine future ROR pipeline runs build the quality profile automatically.

## Scope boundary

This slice deliberately does not yet add:

- historical ROR source backfill;
- ROR semantic change sets;
- ROR commercial customer bundle;
- ROR `locations` redistribution;
- GLEIF ↔ ROR entity resolution.

The next useful product step is historical ROR backfill plus deterministic ROR changes, because that turns the second independent database into an accumulating historical asset rather than only a current-state product.
