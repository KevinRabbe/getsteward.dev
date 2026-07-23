# Open Data Platform v0.1 — Phase 1

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

Phase 1 archived and verified the raw payload. Phase 2 now adds streaming XML parsing and normalized artifact generation; Product Build and Release Packaging remain out of scope.

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

The product is built only after every JSONL record, sidecar checksum, and declared record count passes verification. It contains a `lei` table with stable lookup indexes and a `metadata` table linking the database to the immutable raw snapshot and normalized artifact.

```text
data/products/<source_dataset_id>/<snapshot_id>/
  lei.sqlite
  lei.sqlite.sha256
  product.json
  product.json.sha256
```

The SQLite build is zero-install, streams the normalized JSONL, rejects duplicate LEIs, runs `PRAGMA integrity_check`, and publishes atomically.

## Tests

Im Projektordner:

```powershell
$env:PYTHONPATH = ".\src"
python -m unittest discover -s tests -v
```

Die Tests brauchen kein Internet.

## Next step

The next scope is release packaging and consumer-facing query access. The raw archive, normalized artifact, and SQLite product are already separate, verified trust boundaries.
