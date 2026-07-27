# Phase 17 — Real GLEIF production acceptance

## Objective

Convert the Phase 1–16 production-readiness implementation into measured operational evidence against real daily GLEIF Level 1 source versions.

Phase 17 is deliberately split into two parts:

1. deterministic acceptance machinery, which can be qualified immediately;
2. elapsed-time evidence, which can only become true after distinct real GLEIF source versions have actually been processed.

The project must not count repeated executions of one source version as additional production days.

## Run evidence

Every `run-gleif` execution now writes one immutable-by-convention JSON run record under:

```text
data/metrics/runs/<run_id>.json
```

The metrics record includes:

- run ID and status;
- start/finish timestamps;
- source version and snapshot ID;
- ingest, parse, product-build and release-build durations;
- total pipeline duration;
- peak process resident/working-set memory when the host exposes it;
- download/raw/normalized/product/release byte sizes;
- record, unique-LEI and duplicate-LEI counts;
- physical runtime-data bytes before and after the run;
- physical growth attributed to the run;
- failure text for failed runs.

Metrics are excluded from the physical-data footprint calculation so the measurement does not recursively measure its own history.

A failed run still attempts to persist failure metrics. Failure to write metrics does not replace the original pipeline exception; it is recorded separately as `PIPELINE_METRICS_FAILED`.

## Acceptance report

Run:

```powershell
python .\production_acceptance.py
```

or choose a different evidence window:

```powershell
python .\production_acceptance.py --required-versions 7
```

The report returns:

```text
IN_PROGRESS
PASS
ALERT
```

`PASS` requires the configured number of **distinct successful GLEIF source versions**.

A same-day rerun is operational evidence but does not increase `observed_distinct_source_versions`.

`ALERT` is reserved for corrupt/unreadable metrics evidence rather than silently ignoring it.

The report aggregates:

- observed and remaining source versions;
- calendar span;
- completed and failed run counts;
- average total and per-stage durations;
- maximum observed peak RSS;
- latest physical storage footprint;
- growth across the acceptance window;
- average growth per new source version;
- average raw snapshot and release bundle size.

## Seven-version gate

The default Phase 17 gate is:

```text
7 distinct real source versions
+ successful end-to-end releases
+ persisted run metrics
= PASS
```

Until seven real source versions exist, the honest status is `IN_PROGRESS`.

## Failure drills

The deterministic suite already locks the mechanisms used for the operational drills:

- interrupted `.tmp-*` and partial staging visibility through `recovery-plan`;
- stale/active/corrupt pipeline-lock classification;
- release and replica checksum verification;
- non-overwriting restore into a failover root;
- read-only query from the restored product;
- corrupted artifacts fail verification instead of being silently accepted.

For the real-machine acceptance window, exercise those mechanisms only against disposable copies / failover roots. Do not corrupt or delete canonical source history merely to prove the detector.

## Exit criteria

Phase 17 is complete only when the production-acceptance report says `PASS` on real accumulated data and the operator has recorded the recovery/corruption/failover drills against disposable copies.

The deterministic code/CI qualification alone is not sufficient to claim the elapsed-time gate has happened.
