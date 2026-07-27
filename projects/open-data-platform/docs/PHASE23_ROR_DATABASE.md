# Phase 23 — Second independent database: ROR organizations

## Objective

Prove that the Open Data Platform is no longer a GLEIF-specific application by onboarding a genuinely independent publisher, transport, schema, legal admission and product lifecycle.

The second database is:

```text
Research Organization Registry (ROR)
source_dataset_id = ds_ror_organizations
```

It remains fully separate from the GLEIF databases.

```text
GLEIF Level 1       GLEIF RR        ROR Organizations
      │                 │                  │
      └──── optional ────┘                  │
             joins                         │
                                           │
                               independent database
```

No automatic cross-database entity mapping is introduced.

## Source discovery

ROR publishes periodic full registry dumps through the Zenodo concept record:

```text
https://zenodo.org/api/records/6347574
```

Phase 23 resolves the current concept-record metadata, selects exactly one `*-ror-data.zip` file, records the concrete Zenodo record/version/DOIs/publication date, and downloads the publisher-provided ZIP.

Zenodo's source-supplied MD5 is verified before the file crosses the normal immutable SHA-256 archive boundary.

The platform's SHA-256 remains the preservation/content identity; MD5 is used only to verify the publisher-supplied acquisition checksum.

## Legal / field boundary

ROR states that ROR IDs and metadata are available under CC0 1.0.

However, the ROR data structure also contains `locations`, which is sourced from GeoNames and carries attribution/licensing considerations that are distinct from the clean CC0 registry boundary.

The first ROR product therefore applies an explicit conservative field rule:

```text
RAW ZIP
  └── preserved exactly, including locations

NORMALIZED / PRODUCT / RELEASE / QUERY
  └── locations excluded
```

The admission record permanently documents:

```text
product_field_exclusions = ["locations"]
```

This is deliberate field-level rights filtering, not accidental data loss.

A later location-enabled product would require its own versioned attribution/licence manifest and legal rule. It must not silently broaden this product.

## Schema trust boundary

The current parser targets **ROR Schema 2.1**.

Expected top-level fields are explicitly enumerated:

```text
id
admin
domains
established
external_ids
links
locations
names
relationships
status
types
```

`locations` is validated at the source boundary and then excluded.

Any unknown future top-level field fails normalization instead of being silently dropped. A schema change therefore becomes an explicit engineering/legal review event.

## Streaming JSON normalization

The ROR ZIP contains the format-of-record JSON plus CSV.

Phase 23 reads the JSON member directly from the verified ZIP and uses a bounded standard-library streaming parser over the top-level JSON array. The complete registry is not loaded into memory.

Each normalized record contains:

```text
ror_id
display_name
status
established
types[]
domains[]
names[]
external_ids[]
links[]
relationships[]
admin{}
```

It contains no `locations` field.

The parser requires exactly one name carrying the source-defined `ror_display` type and validates source relationship types.

Normalized artifacts remain in the common dataset-separated hierarchy:

```text
data/normalized/ds_ror_organizations/<snapshot_id>/
  records.jsonl
  records.jsonl.sha256
  quality.json
  quality.json.sha256
  artifact.json
  artifact.json.sha256
```

Quality evidence includes organization statuses/types, relationship types, and the number of source records from which location data was intentionally excluded.

## SQLite product

The normalized registry becomes:

```text
data/products/ds_ror_organizations/<snapshot_id>/
  organizations.sqlite
  organizations.sqlite.sha256
  product.json
  product.json.sha256
```

`ror_id` is a required unique stable identifier.

The product contains one row per ROR organization, with indexes for display name and status. Structured source arrays/objects are kept as canonical JSON columns rather than flattened into a guessed universal organization schema.

The SQLite product metadata and manifest both preserve:

```text
excluded_source_fields = ["locations"]
```

Duplicate ROR IDs fail the build instead of being arbitrarily resolved.

## Read-only queries

```powershell
python .\ror_registry.py lookup <snapshot_id> 03yrm5c26
python .\ror_registry.py lookup <snapshot_id> https://ror.org/03yrm5c26
python .\ror_registry.py search <snapshot_id> "University" --limit 20
```

Every query re-verifies the selected ROR product first and returns exact snapshot/product provenance plus the excluded-field declaration.

Locations can therefore never appear merely because a caller used a different query path.

## Deterministic release

Each verified ROR product receives its own immutable release:

```text
release.zip
├── product/organizations.sqlite
├── product/product.json
├── source/manifest.json
├── normalized/quality.json
└── release.json + checksum sidecars
```

The release manifest also locks:

```text
release_type = ror_organizations_sqlite_bundle
excluded_source_fields = ["locations"]
```

The generic release verifier is reused.

## Full independent pipeline

```powershell
python .\ror_registry.py run
```

executes:

```text
Zenodo discovery
→ ROR-specific admission
→ source checksum verification
→ immutable shared raw archive
→ ROR Schema 2.1 streaming normalization
→ verified organizations.sqlite
→ deterministic ROR release
→ common production-run metrics
```

The same global pipeline lock protects the runtime root, so ROR and GLEIF scheduled writes cannot race.

## What this proves

Before Phase 23, the platform had multiple products but one publisher family.

After Phase 23, shared infrastructure has proven it can support:

```text
GLEIF
- custom GLEIF metadata/download API
- XML source formats
- legal entities + relationships

ROR
- Zenodo concept-record API
- source checksum from publisher repository
- ZIP + large JSON array
- different stable identifier
- field-level legal exclusion
- organization product
```

while preserving the same archive/provenance/fixity/product/release/runtime principles.

## Empirical boundary

Deterministic tests qualify the Zenodo metadata contract and a representative Schema 2.1 source fixture.

The repository must not claim that the current full live ROR dump has been downloaded and processed until that network-boundary run actually occurs.

## Non-goals

Not added in Phase 23:

- ROR `locations` redistribution;
- GLEIF ↔ ROR entity resolution;
- probabilistic organization matching;
- commercial packaging of ROR together with GLEIF;
- automatic historical ROR change engine;
- customer-specific schemas;
- graph database.

Those are later product choices, not prerequisites for proving a second independent database.
