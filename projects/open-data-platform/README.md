# Open Data Platform v0.12 — Phases 1-15

Der erste echte Trust-Boundary-Durchlauf für **GLEIF Level 1 LEI-CDF**.

## Was diese Phase bereits macht

```text
Source Registry
→ Admission Rule
→ GLEIF latest metadata
→ HTTPS download to staging
→ immutable SHA-256 content archive
→ read-back verification
→ immutable snapshot manifest
→ event log
→ optional verified second copy
```

Phase 1 archived and verified the raw payload. Phases 2-10 extend that boundary through normalization, a queryable product, deterministic releases, read-only serving, retention planning, and deployment readiness.

## Warum GLEIF

GLEIF veröffentlicht seine Concatenated Files täglich und dokumentiert einen Download-API-Endpunkt für automatisierte Verarbeitung. Die über den GLEIF Access Service bereitgestellten LEI/LE-RD-Daten stehen laut GLEIF unter CC0 1.0.

Referenzen:

- https://leidata.gleif.org/api/v1/concatenated-files/lei2/latest
- https://www.gleif.org/en/meta/lei-data-terms-of-use
- https://www.gleif.org/en/lei-data/gleif-concatenated-file/download-the-concatenated-file

## Windows 11 — Setup

PowerShell im Projektordner:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\setup_windows.ps1
```

Es werden **keine Python-Pakete installiert**. Das Projekt verwendet in Phase 1 nur die Python-Standardbibliothek.

Danach der erste echte Ingest:

```powershell
.\scripts\ingest_gleif.ps1
```

Alternativ direkt:

```powershell
python .\odp.py ingest-gleif
```

Mit zweiter verifizierter Kopie auf einem anderen Laufwerk/Ordner:

```powershell
python .\odp.py ingest-gleif --replica-root "E:\open-data-replica"
```

## Ergebnis

Nach erfolgreichem Lauf ungefähr:

```text
data/
├── staging/
├── archive/
│   ├── content/
│   │   └── sha256/
│   │       └── ab/
│   │           └── abcd....zip
│   └── snapshots/
│       └── snp_<uuid>/
│           ├── manifest.json
│           └── manifest.json.sha256
└── events/
    └── events.jsonl
```

Der Source-Payload wird über seinen SHA-256 adressiert. Zwei Snapshots mit exakt denselben Bytes können dieselben Content-Bytes referenzieren, ohne die Snapshot-Historie zu verlieren.

## Snapshot prüfen

Die `snapshot_id` steht in der Ausgabe des Ingests:

```powershell
python .\odp.py verify snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

Das prüft:

1. `manifest.json` gegen `manifest.json.sha256`
2. archivierte Source-Bytes gegen den im Manifest gespeicherten SHA-256
3. archivierte Byte-Länge gegen das Manifest

## Wichtige Sicherheitsgrenzen

- nur HTTPS
- nur explizit allowgelistete Download-Hosts
- Admission muss `ALLOW` sein
- temporärer Download wird erst nach Hash + Archivierung zum permanenten Raw-Objekt
- Raw wird nie in place verändert
- bereits archivierte Bytes werden read-back-verifiziert
- ein bereits vorhandenes Content-Objekt wird vor Deduplication erneut verifiziert
- neue Source-Version überschreibt keinen alten Snapshot

## Phase 2 - streaming normalization

After a raw snapshot has been archived and verified, normalize it without extracting the ZIP to disk:

```powershell
python .\odp.py parse-gleif snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

The parser reads the single CDF XML member with a streaming `iterparse` loop and writes one normalized LEI record per line. It validates the CDF header record count and required fields before atomically publishing:

```text
data/normalized/<source_dataset_id>/<snapshot_id>/
  records.jsonl
  records.jsonl.sha256
  quality.json
  quality.json.sha256
  artifact.json
  artifact.json.sha256
```

The raw archive and raw snapshot manifest are never modified. A repeated parse of the same snapshot returns `NO_CHANGE` after verifying the normalized artifact checksums.

## Phase 3 - verified SQLite product

Verify the normalized artifact independently:

```powershell
python .\odp.py verify-normalized snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

Then build the queryable SQLite product:

```powershell
python .\odp.py build-gleif snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
python .\odp.py verify-product snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

The product is built only after every JSONL record, sidecar checksum, and declared record count passes verification. It contains a `lei` table with stable lookup indexes and a `metadata` table linking the database to the immutable raw snapshot and normalized artifact. Duplicate LEIs are preserved as separate source rows; `lookup-lei` returns `AMBIGUOUS` with all variants instead of silently selecting one.

```text
data/products/<source_dataset_id>/<snapshot_id>/
  lei.sqlite
  lei.sqlite.sha256
  product.json
  product.json.sha256
```

The SQLite build is zero-install, streams the normalized JSONL, rejects duplicate LEIs, runs `PRAGMA integrity_check`, and publishes atomically.

## Phase 4 - release packaging

Package a verified product together with its source manifest and normalized quality report:

```powershell
python .\odp.py build-release snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
python .\odp.py verify-release snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

The resulting deterministic ZIP contains the SQLite database, product manifest, raw snapshot manifest, quality report, and checksum sidecars. It does not duplicate the large raw ZIP payload.

## Phase 5 - consumer query access

Read-only queries verify the selected product before opening SQLite:

```powershell
python .\odp.py lookup-lei 5493001KJTIIGC8Y1R12
python .\odp.py search-name "Example" --limit 20
```

Queries return provenance for the exact snapshot and product used. Omitting `--snapshot-id` selects the newest verified product by source version.

## Phase 6 - end-to-end operation

Run the complete pipeline with one command:

```powershell
python .\odp.py run-gleif
python .\odp.py events
```

This executes ingest, normalization, SQLite build, and release packaging while appending stage events to the existing event log. A failure at any boundary stops the pipeline and records `PIPELINE_FAILED`.

## Phase 7 - operations

Copy a verified release to a second physical root and verify the copied bytes:

```powershell
python .\odp.py replicate-release snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx --replica-root "E:\open-data-releases"
python .\odp.py status
```

Replication never overwrites an existing destination. The status report verifies each snapshot through raw, normalized, product, and release boundaries and includes a recent event summary.

## Phase 8 - downstream HTTP service

Serve one verified product through a small read-only standard-library HTTP API:

```powershell
python .\odp.py serve --host 127.0.0.1 --port 8080
```

Endpoints are `GET /healthz`, `GET /v1/lei/<lei>`, `GET /v1/search?name=<text>&limit=<n>`, and `GET /v1/status`. The product is fully verified once at startup and every request uses a separate SQLite read-only connection.

## Phase 9 - retention planning

Generate a report without deleting or overwriting anything:

```powershell
python .\odp.py retention-plan --keep-latest 7
```

Only snapshots outside the keep window whose raw, normalized, product, and release boundaries all verify are marked `eligible_for_archive_review`. The command is explicitly report-only.

## Phase 10 - scheduled execution and deployment readiness

For Windows Task Scheduler or another external scheduler, use the checked-in wrapper. It runs the complete pipeline, writes a timestamped log, forwards the pipeline exit code, and never installs packages:

```powershell
.\scripts\run_gleif_pipeline.ps1
.\scripts\run_gleif_pipeline.ps1 -PythonExe "C:\\Python311\\python.exe" -ReplicaRoot "E:\\open-data-releases"
```

The wrapper is intentionally a process boundary rather than an automatic Task Scheduler registration. Scheduling, credentials, host binding, backups, and restart policy remain deployment-owner decisions.

Check whether the newest release and query product are verified before exposing the service:

```powershell
python .\odp.py deployment-check
python .\odp.py deployment-check --replica-root "E:\\open-data-releases"
```

`deployment-check` is report-only. It verifies the selected release and SQLite product, optionally compares a second physical release copy, and prints suggested service and scheduled-run commands. It does not start, copy, delete, or overwrite anything.

## Phase 11 - full-file conflict handling and large releases

The live global CDF can contain a small number of repeated LEIs across source records. The product preserves every normalized row, reports `record_count`, `unique_lei_count`, and `duplicate_lei_count`, and returns `AMBIGUOUS` with all variants for a conflicting `lookup-lei`. No record is silently discarded or heuristically selected.

Release packaging uses ZIP64 when needed, so the same deterministic packaging path works for multi-gigabyte SQLite products as well as the small offline fixtures.

## Phase 12 - scheduled-run locking and recovery visibility

The end-to-end pipeline now takes an exclusive lock under `data/events/pipeline.lock`, preventing overlapping scheduled runs from racing on the same archive. The lock records run ID, PID, host, and start time and is released only by its owning process.

```powershell
python .\odp.py pipeline-lock
python .\odp.py recovery-plan
```

Both commands are report-only. `pipeline-lock` distinguishes `CLEAR`, `ACTIVE`, `STALE`, and `CORRUPT`. `recovery-plan` lists interrupted `.tmp-*` artifacts and partial staging downloads without deleting them. A stale lock or temporary artifact requires explicit operator review before retrying.

## Phase 13 - scheduler/deployment integration

Create a Task Scheduler plan without changing the machine:

```powershell
python .\odp.py schedule-plan --frequency daily --start-time 02:00
python .\odp.py schedule-plan --replica-root "E:\\open-data-releases"
```

When the host owner is ready to register the task, use the explicit PowerShell registration script. It supports `-WhatIf`, runs under the interactive user with limited privileges, and relies on the pipeline lock:

```powershell
.\scripts\register_task_scheduler.ps1 -WhatIf
.\scripts\register_task_scheduler.ps1 -TaskName "OpenDataPlatform-GLEIF" -Frequency Daily -StartTime "02:00"
```

The repository does not register a task implicitly; task identity, schedule, credentials, and host policy remain visible operator choices.

## Phase 14 - HTTP service security and network boundaries

The service remains loopback-only by default. Remote binding requires both explicit `--allow-remote` and a bearer token file:

```powershell
python .\odp.py serve --host 0.0.0.0 --allow-remote --auth-token-file "C:\\secrets\\odp.token"
```

With a token configured, every endpoint requires `Authorization: Bearer <token>`. Token contents are read once at startup and never returned or logged. Responses include no-store, MIME-sniffing, and clickjacking headers; name queries are bounded to 500 characters.

## Phase 15 - backup, restore, and query failover

Verify a release stored on a second physical root:

```powershell
python .\odp.py verify-replica snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx --replica-root "E:\\open-data-releases"
```

Restore the verified release into a separate failover data root without overwriting existing artifacts:

```powershell
python .\odp.py restore-release snp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx --replica-root "E:\\open-data-releases" --restore-root "F:\\odp-failover"
python .\odp.py lookup-lei 5493001KJTIIGC8Y1R12 --data-root "F:\\odp-failover"
```

The restore contains the source manifest, SQLite product, and release bundle. It is query-failover-ready, while raw and normalized source payloads remain owned by the primary archive. Existing target artifacts are never overwritten.

## Tests

Im Projektordner:

```powershell
$env:PYTHONPATH = ".\src"
python -m unittest discover -s tests -v
```

Die Tests brauchen kein Internet.

## Next step

Remaining work is environment-specific deployment policy: choose a scheduler, bind the HTTP service behind the intended network boundary, define backup/restore ownership, and add monitoring/alert delivery. The repository provides verification and report-only primitives for those decisions without making them implicitly.
