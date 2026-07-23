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

Noch **nicht** enthalten: XML-Parsing, Normalisierung, Product Build oder Release Packaging. Das kommt erst, wenn der Raw-Ingest sauber funktioniert.

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

## Tests

Im Projektordner:

```powershell
$env:PYTHONPATH = ".\src"
python -m unittest discover -s tests -v
```

Die Tests brauchen kein Internet.

## Nächster Schritt

Erst nachdem ein echter GLEIF-Snapshot archiviert und `verify` erfolgreich ist:

```text
ZIP
→ streaming XML parser
→ selected source-native fields
→ normalized artifact
→ validation/quality report
```
