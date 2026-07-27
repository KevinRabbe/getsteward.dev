# Phase 22 — First commercial product candidate

## Objective

Turn the verified GLEIF data assets into one immutable customer-delivery package without pretending that public CC0 facts become proprietary merely because they were processed and packaged.

The first product candidate is:

```text
Global Legal Entity History
product_id = prod_global_legal_entity_history
```

It combines the already-proven technical assets:

- current verified Level 1 entity state;
- accumulated historical Level 1 change sets;
- immutable quality intelligence;
- SQLite and CSV delivery;
- optional Parquet delivery;
- optional verified GLEIF RR-CDF relationship state.

## Product status

The repository deliberately distinguishes two states:

```text
commercial packaging complete
!=
customer sale terms legally approved
```

The generated package therefore records:

```text
commercial_status = PRODUCT_CANDIDATE
customer_terms_status = LEGAL_REVIEW_REQUIRED
sale_terms_approved = false
```

The builder will not manufacture restrictive customer terms or claim legal approval.

## Source-rights boundary

GLEIF Access Service data used by this product are treated as CC0 1.0 source data under the preserved source/admission records.

The package explicitly records:

```text
source_data_license = CC0-1.0
claims_exclusive_rights_over_source_lei_or_le_rd = false
trademark_rights_included = false
```

The commercial value is therefore the independently produced package, historical accumulation, processing, verification, quality intelligence, schemas, release discipline, convenient formats, and related protectable/software/service elements where applicable — not exclusivity over the underlying public LEI/LE-RD facts.

Any vendor-specific contractual restrictions for protectable elements must be drafted/reviewed separately before external sale.

## Non-affiliation boundary

Every product package contains `NON_AFFILIATION.txt` stating that the product is independent and is not provided, supported, authorized, endorsed, or otherwise affiliated with GLEIF or an LOU.

No GLEIF trademark/logo licence is implied or bundled.

Verification fails if the required CC0/legal-status/non-affiliation files are missing or their protected meaning changes.

## Package contents

A Level 1-only product candidate contains conceptually:

```text
product.zip
├── product.json
├── README.txt
├── SOURCE_LICENSES.json
├── LEGAL_STATUS.json
├── NON_AFFILIATION.txt
│
├── data/
│   ├── entities.sqlite
│   ├── entities.csv
│   └── entities.parquet        optional
│
├── schema/
│   └── entities.json
│
├── quality/
│   ├── entities-quality.json
│   └── timeline.json
│
└── history/
    └── <old-version>__<new-version>.jsonl ...
```

When an RR snapshot is explicitly selected, the same package can additionally contain:

```text
relationships/
├── relationships.sqlite
├── relationships.csv
└── relationships.parquet      optional

schema/relationships.json
quality/relationships-quality.json
```

Level 1 and RR remain separate product components inside the bundle; packaging them together does not merge their canonical databases.

## Temporal consistency

An optional relationship snapshot may be older than or equal to the selected entity source state.

It may **not** be newer:

```text
relationship source_version <= entity source_version
```

This prevents a commercial bundle from silently presenting a future relationship state alongside an older entity state.

## Historical cutoff

Only verified Level 1 change sets whose destination source version is at or before the selected entity source version are included.

The package never leaks later history into an older product state.

Each history item carries exact source snapshots, source versions, change counts/types, and hashes in `product.json`.

## Schema and quality

Customer-facing schema files are generated directly from the verified SQLite products using `PRAGMA table_info`.

Quality files are copied from the immutable Phase 21 profiles.

The Level 1 timeline is generated from checksum-verified historical quality profiles.

No manually maintained schema spreadsheet or quality document can drift independently from the product bytes.

## Build

Level 1 + history + quality + SQLite + CSV:

```powershell
python .\commercial_product.py build <entity_snapshot_id>
```

Include a chosen RR relationship state:

```powershell
python .\commercial_product.py build <entity_snapshot_id> `
  --relationship-snapshot-id <rr_snapshot_id>
```

Include optional Parquet representations:

```powershell
python -m pip install ".[parquet]"
python .\commercial_product.py build <entity_snapshot_id> --parquet
```

## Verification

```powershell
python .\commercial_product.py verify <entity_snapshot_id>
```

or with the relationship component:

```powershell
python .\commercial_product.py verify <entity_snapshot_id> `
  --relationship-snapshot-id <rr_snapshot_id>
```

Verification re-verifies the canonical source products and quality profiles, then checks:

- outer manifest checksum;
- ZIP checksum;
- inner/outer `product.json` identity;
- every declared payload file hash and byte size;
- entity product identity;
- optional RR product identity;
- CC0 source notice;
- explicit `sale_terms_approved = false`;
- required non-affiliation notice.

Existing commercial package directories are never overwritten. A repeated build verifies the existing candidate and returns `NO_CHANGE`.

## Legal review gate

This phase deliberately stops before drafting a final customer licence.

Before external sale, counsel should review at least:

- what protectable/package/software elements may carry vendor terms;
- wording that preserves customers' CC0 rights in underlying source facts;
- warranty/disclaimer/liability language;
- trademark/non-affiliation language;
- Internal Commercial vs OEM/Redistribution contractual distinctions.

That is a one-time legal product-contract task, not a reason to delay the technical product package.

## Non-goals

Not added here:

- payment processing;
- entitlement portal;
- usage metering;
- telemetry;
- revenue share;
- customer-specific builds;
- hosted SLA/API dependency;
- a claim of exclusive ownership over GLEIF source data.
