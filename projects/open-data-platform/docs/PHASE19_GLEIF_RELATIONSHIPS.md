# Phase 19 — GLEIF Level 2 relationship database

## Objective

Add GLEIF RR-CDF 2.1 as a second independently archived/productized dataset in the same source family.

This phase intentionally does **not** merge Level 2 rows into the Level 1 legal-entity database.

```text
Level 1 database          Level 2 relationship database
       │                             │
       └──────── common LEI ─────────┘
```

Integration remains a query/join decision rather than a storage mutation.

## Current source contract

GLEIF publishes the RR-CDF Concatenated File daily. The automated file family is `rr`:

```text
https://leidata.gleif.org/api/v1/concatenated-files/rr/{YYYYMMDD}/zip
```

The source registry uses the corresponding `rr/latest` metadata endpoint and the dated download template.

RR-CDF 2.1 supports directional relationship records including:

- `IS_DIRECTLY_CONSOLIDATED_BY`
- `IS_ULTIMATELY_CONSOLIDATED_BY`
- `IS_INTERNATIONAL_BRANCH_OF`
- `IS_FUND-MANAGED_BY`
- `IS_SUBFUND_OF`
- `IS_FEEDER_TO`

The parser therefore preserves `RelationshipType`; it does not reduce the dataset to a generic parent edge.

Reporting Exceptions are a different GLEIF dataset and remain outside this phase.

## Separate source and admission

```text
config/sources/gleif_relationships.json
config/admissions/gleif_relationships.json
```

The admission is limited to the RR-CDF Concatenated File under the same GLEIF Access Service CC0 boundary. It does not implicitly admit Reporting Exceptions or unrelated GLEIF materials.

## Normalized relationship record

Each source relationship is preserved as one normalized record containing:

```text
start_node { id, type }
end_node { id, type }
relationship_type
relationship_status
relationship_periods[]
relationship_qualifiers[]
relationship_quantifiers[]
registration {
  initial_registration_date
  last_update_date
  registration_status
  next_renewal_date?
  managing_lou
  validation_sources
  validation_documents
  validation_reference?
}
```

Node type is retained because RR-CDF permits LEI and provisional node identifiers. LEI syntax is checked only when the source declares `NodeIDType=LEI`.

## Streaming normalization

`relationship_parser.py` reads the one XML member directly from the verified raw ZIP with `ElementTree.iterparse`.

It validates the declared record count, required RR fields and LEI-form node identifiers without extracting the XML to disk.

Artifacts remain under the ordinary dataset-separated normalized hierarchy:

```text
data/normalized/ds_gleif_rr_level2_concat/<snapshot_id>/
  records.jsonl
  records.jsonl.sha256
  quality.json
  quality.json.sha256
  artifact.json
  artifact.json.sha256
```

Quality metrics include relationship type/status, registration status and node-ID-type distributions.

## SQLite relationship product

The verified JSONL becomes:

```text
data/products/ds_gleif_rr_level2_concat/<snapshot_id>/
  relationships.sqlite
  relationships.sqlite.sha256
  product.json
  product.json.sha256
```

One source relationship remains one row.

Indexes exist for:

```text
start node + relationship type
end node + relationship type
relationship type
relationship status
```

No entity merge or parent inference is performed.

## Queries

```powershell
python .\gleif_relationships.py query <snapshot_id> <LEI>
python .\gleif_relationships.py query <snapshot_id> <LEI> --direction outgoing
python .\gleif_relationships.py query <snapshot_id> <LEI> --direction incoming
python .\gleif_relationships.py query <snapshot_id> <LEI> --relationship-type IS_DIRECTLY_CONSOLIDATED_BY
```

Every query first verifies the relationship product and returns exact snapshot/product provenance.

## Full pipeline

```powershell
python .\gleif_relationships.py run
```

This executes:

```text
RR discovery/admission
→ immutable raw archive
→ RR streaming normalization
→ verified relationship SQLite
→ deterministic relationship release
```

The existing global pipeline lock is reused, so Level 1 and Level 2 scheduled writes cannot race through the same archive/runtime root.

## Release

The RR product receives its own immutable release under its own dataset ID. The bundle contains:

- `relationships.sqlite`
- product manifest
- source snapshot manifest
- normalized quality report
- release manifest/checksums

The Level 1 release format and data remain unchanged.

## Metrics correction

Phase 19 introduces a second pipeline, so production-run metrics are now explicitly dataset-scoped (`metrics_version=2`).

The Phase 17 seven-version acceptance report defaults specifically to:

```text
ds_gleif_lei_level1_concat
```

RR runs cannot accidentally advance the Level 1 production-evidence gate.

Older metrics v1 records remain backward-compatible only with the original Level 1 acceptance dataset.

## Non-goals

Not included yet:

- Reporting Exceptions
- materialized Level1+Level2 merged table
- inferred corporate ownership when no RR edge exists
- probabilistic entity resolution
- graph database
- transitive ownership calculation

Those are separate derived/product decisions.
