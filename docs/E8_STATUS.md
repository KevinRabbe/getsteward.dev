# E8 Release Hardening Status

Status: **ACTIVE — DETERMINISTIC STORAGE/CONTROL INTEGRITY, PROVIDER-NATIVE DATABASE RESTORE, LARGE-TRANSFER STRESS, LONG-AUTHORITY STRESS, AND BOUNDED LOCAL SUPPORT DIAGNOSTICS ARE CI-PROVEN; RELEASE EMPIRICAL ACCEPTANCE AND ONE LOCAL DOWNLOAD-CACHE BOUNDARY REMAIN OPEN.**

This checkpoint records executable truth after the isolated E8 hardening stack through PR #16. It does **not** promote E8 to release-complete and it does not replace any deferred real-machine/game/provider evidence.

## Checkpoint discipline

The last repository checkpoint known to have all five workflows green together remains:

> `62517139`

The current E8 stack is intentionally based on an older remote branch that still contains two Factorio Windows tests already fixed in the newer unpushed workspace. Therefore the E8 slices are qualified by their own CI evidence, but this document does not relabel the stacked remote head as a new global all-workflows-green checkpoint.

On the current E8 head, Windows proves Core, Infrastructure, Backend, Backend API, Palworld, 7 Days to Die, Project Zomboid, Architecture, Desktop acceptance packaging, and the new E8 tests. The only inherited Windows failures are:

- `FactorioModCatalogLinkedPathTests.DiscoverRejectsLinkedStartupSettings`;
- `FactorioModInputSafetyTests.ReproductionRejectsLinkedStartupSettingsBeforeVersionWork`.

Do not reimplement those fixes in this E8 stack. Reconcile/cherry-pick the E8 commits onto the newer Factorio tree later.

## E8 deterministic hardening completed in this stack

### Persisted metadata integrity — PR #6

New persisted metadata writes carry an integrity proof. Current-format documents fail closed if that proof is missing or invalid while older supported schema versions remain explicitly readable through migration compatibility.

### Storage-key identity binding — PR #7

A valid, correctly checksummed document is still rejected when stored under the wrong World/revision/workspace identity. Path identity is now part of the read contract rather than merely a locator.

### Recovery candidate package verification — PR #8

A reused candidate cannot become canonical merely because candidate metadata exists. Its package must still open and pass the immutable integrity path before recovery promotes or clears responsibility.

### State revision to payload binding — PR #9

New state revisions bind their payload SHA-256 inside the integrity-protected `revision.json`.

```text
new revision
    revision.json  -> revision identity + protected payload digest
    payload.bin
```

The former `payload.sha256` sidecar remains legacy compatibility only. This removes one persisted artifact while closing valid-payload transplant between revision directories.

### Bounded local persistence reads — PR #10

- persisted JSON is capped at a generous 16 MiB safety ceiling before parsing;
- legacy checksum sidecars are capped at 4 KiB before text decoding;
- malformed local metadata can no longer force unbounded parsing/allocation before rejection.

### Bounded remote control responses — PR #11

Session/authentication, host-presence, package-upload control, and package-download authorization JSON all use the same 4 MiB bounded reader, including chunked responses without `Content-Length`.

Large package bytes remain on the separate direct object-storage transfer path.

### Bounded persistent control inputs — PR #12

Application/value-object boundaries now bound caller-controlled metadata before PostgreSQL persistence:

- World adapter ID: 256 characters;
- World display name: 512 characters;
- external identity provider: 128 characters;
- external identity ID: 512 characters;
- verified display name: 512 characters;
- serialized environment manifest: 4 MiB.

Idempotency keys were audited and already had their own explicit 128-visible-ASCII bound.

## E8 provider and endurance proofs

### PostgreSQL native backup/restore — PR #13

The PostgreSQL integration suite now proves the actual provider boundary:

```text
seed real Steward records through production services/stores
-> PostgreSQL 17 pg_dump
-> create fresh database
-> PostgreSQL 17 pg_restore
-> DO NOT run Steward schema initialization on restored database
-> read World/member/environment/state through production stores
-> verify exact restored graph
```

This is not an in-memory or serializer round trip. It proves PostgreSQL-native schema + relational data restoration.

### Large resumable S3-compatible transfer — PR #14

The provider integration suite now includes one synthetic 256 MiB immutable package:

```text
4 x 64 MiB parts
-> upload parts 1-2
-> dispose first store/client boundary
-> reopen provider boundary
-> observe exactly two completed parts
-> upload parts 3-4
-> finalize
-> direct streamed GET
-> exact 256 MiB byte count + SHA-256
```

The package is generated as a deterministic repeating stream rather than a 256 MiB in-memory array. The proof completed in roughly two seconds in CI, so it is cheap enough to remain a permanent gate.

This is synthetic provider stress evidence. It is **not** a claim about measured Factorio, Palworld, 7DTD, or Project Zomboid package sizes.

### Accelerated 24-hour writable authority — PR #15

The PostgreSQL authority integration suite now executes 2,880 real heartbeat updates at 30-second logical intervals, representing 24 hours of writable responsibility without wall-clock sleeping.

After the full logical day it proves:

- the same reservation session ID;
- the same generation;
- `Active` state;
- exact final heartbeat timestamp;
- no `BecameUncertainAt` value;
- exactly one reservation row for the World.

The endurance proof added approximately one second to the PostgreSQL suite and is therefore suitable as a permanent gate.

This proves backend authority endurance, not a 24-hour real game-process run.

## E8 diagnostics/support boundary — PR #16

Local support diagnostics now use one shared Infrastructure boundary for CLI and Desktop.

Properties:

- bearer credentials, token/secret assignments, and URL query strings are redacted before persistence;
- diagnostic processing is bounded before formatting full exception trees;
- maximum detail is 64 KiB;
- maximum inner-exception depth is 8;
- at most 64 new-format incident files are retained;
- old `errors-*.log` files from the previous unredacted CLI format are removed on the first safe diagnostic write;
- each persisted failure receives a short incident ID;
- Desktop caught failures show the incident ID and local diagnostic path;
- dispatcher/AppDomain crash paths use the same writer;
- an unknown dispatcher exception is logged but remains fatal — Steward does not continue in an unknown state.

Recovery workspace exports remain a separate user action containing gameplay recovery material. They are **not** support diagnostic bundles and are never subject to diagnostic-log retention.

## Installer/update ownership is eliminated, not implemented

`PRODUCT_BOUNDARY.md` already assigns Steward distribution and updates to Steam. `DEFERRED_EMPIRICAL_TESTS.md` likewise requires release/depot/update validation through Steam and explicitly rejects a second self-updater.

Therefore E8 does **not** add:

- a Steward self-updater;
- a parallel signing/update trust service;
- a second distribution channel merely to satisfy the wording "installer/update behavior".

The Windows acceptance package remains a CI/release-acceptance artifact. Its manifest verifies every declared file by size/SHA-256, rejects missing/changed/duplicate/escaping entries, and rejects undeclared files before launching the selected Desktop executable. Production distribution/update authenticity remains Steam's responsibility.

## Open deterministic E8 issue: local verified package cache

`VerifiedPackageCache` still has one concrete hardening gap and one related retention gap.

### Streaming oversize gap

The current download path streams the HTTP response to the `.partial` file and only checks `downloadedBytes > ExpectedByteSize` after `CopyToAsync` returns.

Therefore a malformed/misbehaving object-storage response can write beyond the authorized immutable package size before Steward rejects and deletes it. The read is not unbounded in product intent, but the enforcement occurs too late to protect disk consumption.

Required fix:

```text
expected immutable byte size
-> enforce remaining-byte ceiling while streaming
-> stop/reject as soon as response exceeds authorization
-> never write bytes beyond the authorized package size
```

### Cache retention gap

Verified content-addressed package files currently have no total cache-size/eviction policy. Unique downloaded revisions can therefore accumulate indefinitely.

Any fix must preserve the existing data-class distinction:

- package cache: disposable/re-downloadable;
- diagnostics: disposable/bounded support data;
- unresolved recovery candidate/workspace: durable gameplay evidence and **never** silently evicted for cache pressure.

This issue remains open in this checkpoint. Do not describe local download cache as fully bounded yet.

## Deferred empirical E8 boundaries

CI does not replace the following evidence:

- real game-process long-session/endurance behavior;
- measured representative large-World capture/package/restore/transfer behavior;
- actual EU production deployment/residency evidence;
- E4-B real Steam production identity + two-installation host/Join/save/commit handoff;
- real Windows assistive-technology/mixed-DPI UI acceptance.

These remain in `DEFERRED_EMPIRICAL_TESTS.md` and must not be promoted from synthetic/deterministic evidence.

## Remaining E8 direction

Highest-value remaining work, in order:

1. fix the local verified-package streaming byte ceiling and define bounded disposable-cache retention;
2. continue deterministic security/operations review only where it finds concrete unowned/unbounded boundaries;
3. execute E4-A/live EU infrastructure acceptance when a real disposable deployment is available;
4. batch the recorded real-game/Windows/Steam empirical tests when the required machines/credentials are available;
5. only then decide whether E8 release acceptance can be called complete.

Do not start E9 performance optimization while these correctness/release boundaries remain open.
