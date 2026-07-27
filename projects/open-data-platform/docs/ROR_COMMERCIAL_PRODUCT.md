# ROR commercial product candidate

## Objective

Create a second independent commercial product candidate from the ROR database rather than bundling ROR into the GLEIF product.

The candidate is:

```text
Global Research Organization History
product_id = prod_global_research_organization_history
```

Its commercial value comes from verified packaging, reproducible filtering, accumulated historical changes, quality history, convenient distributions, and product integrity—not from exclusive ownership of public ROR facts.

## Legal/product status

The generated package deliberately records:

```text
commercial_status = PRODUCT_CANDIDATE
customer_terms_status = LEGAL_REVIEW_REQUIRED
sale_terms_approved = false
```

No final customer licence is invented automatically.

ROR IDs and metadata used by the product are treated as CC0 1.0 source data.

The package explicitly records that it:

- claims no exclusive rights over source ROR IDs/metadata;
- contains no ROR logo or trademark licence;
- is independent and not provided, supported, authorized, endorsed, or affiliated with ROR or its governing organizations.

## Location-field boundary

The existing ROR product policy remains unchanged:

```text
Raw ROR archive
  locations preserved

Normalized/product/history/commercial layers
  locations excluded
```

ROR documents that location data is derived from GeoNames and carries a separate attribution/licensing boundary.

Because the commercial candidate intentionally excludes `locations`, it does not need to guess or collapse that separate obligation into the CC0 product.

Every bundle contains:

```text
FIELD_POLICY.json
```

which records:

```text
excluded_source_fields = ["locations"]
raw_archive_preserves_source_locations = true
normalized_product_contains_locations = false
commercial_bundle_contains_locations = false
```

Verification fails if the product, history, source notice, or field policy loses this exclusion.

## History is required

This product is intentionally named **History**, so a current-state-only ROR package is not sufficient.

Build requires:

```text
at least two verified ROR source states
+
a complete verified adjacent change chain between packaged states
```

For the initial intended product state:

```text
v2.9 → v2.10
```

must exist as a verified ROR historical change artifact.

The package never infers missing history from version numbers or release notes.

If an adjacent change artifact is missing or fails verification, commercial packaging fails.

## Package contents

Conceptually:

```text
product.zip
├── product.json
├── README.txt
├── SOURCE_LICENSES.json
├── FIELD_POLICY.json
├── LEGAL_STATUS.json
├── NON_AFFILIATION.txt
│
├── data/
│   ├── organizations.sqlite
│   ├── organizations.csv
│   └── organizations.parquet      optional
│
├── schema/
│   └── organizations.json
│
├── quality/
│   ├── organizations-quality.json
│   └── timeline.json
│
└── history/
    └── <old-version>__<new-version>.jsonl ...
```

SQLite remains the canonical queryable product state. CSV and optional Parquet are distributions of that verified state.

## History coverage

`product.json` records:

```text
first packaged source version/date
latest packaged source version/date
adjacent change pair count
complete_across_packaged_states = true
```

Only quality/history states at or before the selected target snapshot are packaged.

Future ROR releases do not alter an older commercial bundle.

## Provenance

The commercial manifest binds the package to:

- selected ROR snapshot/version;
- exact ROR product-manifest hash;
- exact SQLite hash;
- exact record count;
- exact excluded-field declaration;
- exact historical change hashes/counts;
- exact quality profile/timeline bytes;
- exact schema bytes;
- exact distribution bytes;
- package file hashes and sizes.

The ZIP contains the same `product.json` bytes as the outer manifest, and verification proves their identity.

## Build

After the required ROR historical states/change chain exist:

```powershell
python .\ror_commercial_product.py build <ror_snapshot_id>
```

Optional Parquet:

```powershell
python -m pip install ".[parquet]"
python .\ror_commercial_product.py build <ror_snapshot_id> --parquet
```

## Verification

```powershell
python .\ror_commercial_product.py verify <ror_snapshot_id>
```

Verification re-verifies:

- canonical ROR SQLite product;
- immutable quality profile;
- CSV distribution;
- optional Parquet distribution;
- every packaged ROR historical change pair;
- outer manifest and ZIP hashes;
- every declared payload file hash/size;
- CC0 source-rights notice;
- location-field exclusion;
- explicit unapproved sale-terms state;
- non-affiliation notice.

Existing commercial bundle directories are never overwritten. A repeated build verifies the existing candidate and returns `NO_CHANGE`.

## Rights boundary

ROR's current terms make ROR IDs/metadata freely available under CC0 1.0.

That does not mean a vendor should pretend public facts have become exclusive property after processing.

The product therefore separates:

```text
source data rights
    CC0

our independently produced value
    archive history
    filtering discipline
    semantic change computation
    quality intelligence
    schemas
    packaging
    integrity/provenance machinery
    delivery convenience
```

Any contractual protection for the latter belongs in separately reviewed customer terms and must not revoke the customer's underlying CC0 rights in source facts.

## Non-goals

This candidate does not add:

- ROR `locations` redistribution;
- customer-specific data transformations;
- usage metering or telemetry;
- hosted SLA/API dependency;
- revenue share;
- ROR logo use;
- GLEIF ↔ ROR entity resolution;
- a claim of exclusive ownership over ROR source data.
