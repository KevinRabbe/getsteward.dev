# E8 Release Hardening Status

Status: **ACTIVE — DETERMINISTIC E8 HARDENING IS CI-PROVEN THROUGH PR #24; RELEASE/PROVIDER/REAL-MACHINE EMPIRICAL ACCEPTANCE REMAINS OPEN.**

This checkpoint records executable truth after the isolated E8 hardening stack through PR #24. It does **not** promote E8 to release-complete and it does not replace any deferred real-machine, real-game, real-Steam, or real-provider evidence.

## Checkpoint discipline

The last repository checkpoint known to have all five workflows green together remains:

> `62517139`

The current E8 stack is intentionally based on an older remote branch that still contains two Factorio Windows tests already fixed in the newer unpushed workspace. Therefore the E8 slices are qualified by their own CI evidence, but this document does not relabel the stacked remote head as a new global all-workflows-green checkpoint.

On the PR #24 head, Windows proves Core, Infrastructure, Backend, Backend API, Palworld, 7 Days to Die, Project Zomboid, Architecture, Desktop acceptance packaging, and all new E8 tests. The only inherited Windows failures are:

- `FactorioModCatalogLinkedPathTests.DiscoverRejectsLinkedStartupSettings`;
- `FactorioModInputSafetyTests.ReproductionRejectsLinkedStartupSettingsBeforeVersionWork`.

Do not reimplement those fixes in this E8 stack. Reconcile/cherry-pick the E8 commits onto the newer Factorio tree later, then obtain a new true all-workflows-green checkpoint there.

## E8 deterministic hardening completed in this stack

### Persisted metadata integrity — PR #6

New persisted metadata writes carry an integrity proof. Current-format documents fail closed if that proof is missing or invalid while older supported schema versions remain explicitly readable through migration compatibility.

### Storage-key identity binding — PR #7

A valid, correctly checksummed document is still rejected when stored under the wrong World/revision/workspace identity. Path identity is part of the read contract rather than merely a locator.

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

Application/value-object boundaries bound caller-controlled metadata before PostgreSQL persistence:

- World adapter ID: 256 characters;
- World display name: 512 characters;
- external identity provider: 128 characters;
- external identity ID: 512 characters;
- verified display name: 512 characters;
- serialized environment manifest: 4 MiB.

Idempotency keys were audited and already had their own explicit 128-visible-ASCII bound.

## Provider and endurance proofs

### PostgreSQL native backup/restore — PR #13

The PostgreSQL integration suite proves the actual provider boundary:

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

The provider integration suite includes one synthetic 256 MiB immutable package:

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

The PostgreSQL authority integration suite executes 2,880 real heartbeat updates at 30-second logical intervals, representing 24 hours of writable responsibility without wall-clock sleeping.

After the full logical day it proves:

- the same reservation session ID;
- the same generation;
- `Active` state;
- exact final heartbeat timestamp;
- no `BecameUncertainAt` value;
- exactly one reservation row for the World.

The endurance proof adds approximately one second to the PostgreSQL suite and is therefore suitable as a permanent gate.

This proves backend authority endurance, not a 24-hour real game-process run.

## Diagnostics/support boundary — PR #16

Local support diagnostics use one shared Infrastructure boundary for CLI and Desktop.

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

## Bounded package transfer/cache behavior

### Download write ceiling — PR #18

`VerifiedPackageCache` now enforces the authorized immutable package byte size while the response is streaming.

For both fresh and resumed downloads:

```text
authorized remaining bytes
-> write at most that many bytes to .partial
-> read at most one additional byte to detect an oversized body
-> reject immediately
-> remove the invalid partial
```

A malformed multi-gigabyte response can therefore no longer become a multi-gigabyte disk write before Steward notices the mismatch.

### Disposable verified-cache retention — PR #19

The content-addressed download cache now has a configurable total byte ceiling.

First-release default:

> 40 GiB = one maximum 20 GiB State package + one maximum 20 GiB hosted Environment package.

Properties:

- cache mutation is serialized inside one cache instance;
- only `packages/sha256/**/*.package` and `.partial` are eviction candidates;
- oldest disposable entries are evicted first;
- successful cache hits refresh recency;
- deletion failures continue to count as occupied bytes and can make the operation fail closed;
- `OpenVerifiedReadAsync` keeps the cache mutation lease until the verified file stream is actually open;
- recovery candidates/workspaces are outside the cache scan by construction and are never evicted for cache pressure.

The 40 GiB value is a configurable safety capacity, not a measured target for real game Worlds.

### Download inactivity — PR #20

Large downloads retain no fixed total-duration timeout. Instead Steward applies a configurable no-progress boundary, first-release default five minutes:

- time to initial response headers is bounded;
- time between successful body reads is bounded;
- every successful body read resets the inactivity window;
- a slow-but-progressing 20 GiB transfer may therefore take arbitrarily long;
- bytes already received before a stall remain as the size-bounded resumable `.partial`;
- caller cancellation remains caller cancellation rather than being relabeled as a transfer timeout.

### Multipart upload part duration — PR #21

Multipart upload has no automatic retry loop and no fixed whole-package deadline.

Each authorized finite PUT part has a configurable completion deadline, first-release default 30 minutes. A timed-out part is not finalized; re-invoking the existing upload operation resumes from backend-observed completed parts.

This uses the existing finite multipart protocol as the bound instead of adding a second retry/watchdog subsystem.

## Transport/security hardening

### Steward API credential transport — PR #22

Remote Steward API endpoints now obey one explicit rule:

```text
HTTPS remote endpoint     -> allowed
HTTP loopback endpoint    -> allowed for local development
HTTP non-loopback endpoint-> rejected
```

The policy is applied:

- when Desktop reads `STEWARD_API_BASE_URL`, before Steam authentication starts;
- again when the authenticated remote runtime is composed.

Automatic redirects are disabled on both:

- the initial Steam-ticket authentication client;
- the authenticated Steward API client.

Therefore Steam tickets and Steward bearer credentials terminate at the configured API origin instead of being silently replayed to a redirect target.

### Direct object-storage transport — PR #23

The same secure-remote rule applies to private World package transfers and backend S3 configuration.

Client:

- immutable package download authorization rejects plaintext non-loopback URLs before network I/O;
- one direct-transfer handler rejects plaintext remote GET/PUT requests;
- the production direct-transfer handler does not follow redirects.

Backend:

- S3-compatible service URL must use HTTPS remotely;
- loopback HTTP remains available for disposable local MinIO/CI.

This matches the current production-provider candidate, whose S3 endpoint is HTTPS, without creating an insecure-production override.

### Steam identity provider boundary — PR #24

Steam Web API verification remains semantically unchanged, but its external transport/resource boundary is now explicit:

- named Steam identity HTTP client has a 30-second timeout;
- automatic redirects are disabled;
- response body is capped at 1 MiB before JSON parsing;
- declared oversize is rejected through `Content-Length`;
- unknown-length/chunked oversize is rejected while streaming after at most one detection byte beyond the ceiling;
- response stream acquisition/body reads/JSON parsing have their own configurable read deadline, default 30 seconds;
- caller cancellation is not relabeled as a provider timeout;
- publisher credentials remain absent from provider exceptions.

Session-token design was audited and remains unchanged: access/refresh credentials use 256-bit random opaque secrets, the server stores only SHA-256 hashes, access credentials expire after 15 minutes by default, refresh credentials after 30 days, and refresh rotates credentials.

## Installer/update ownership is eliminated, not implemented

`PRODUCT_BOUNDARY.md` assigns Steward distribution and updates to Steam. `DEFERRED_EMPIRICAL_TESTS.md` likewise requires release/depot/update validation through Steam and explicitly rejects a second self-updater.

Therefore E8 does **not** add:

- a Steward self-updater;
- a parallel signing/update trust service;
- a second distribution channel merely to satisfy the wording "installer/update behavior".

The Windows acceptance package remains a CI/release-acceptance artifact. Its manifest verifies every declared file by size/SHA-256, rejects missing/changed/duplicate/escaping entries, and rejects undeclared files before launching the selected Desktop executable. Production distribution/update authenticity remains Steam's responsibility.

## Deterministic E8 hardening checkpoint

The deterministic E8 categories currently have concrete treatment:

- security review: persisted integrity, input/output bounds, credential/direct-transfer transport, identity-provider bounds;
- backup/restore: PostgreSQL-native dump/restore proof;
- long-session/large-transfer stress: accelerated authority endurance + resumable 256 MiB provider transfer;
- installer/update behavior: ownership eliminated in favor of Steam rather than duplicated;
- bounded retries/cache/retention: no automatic unbounded transfer retry loop, bounded transfer stalls/parts, bounded cache writes and retention;
- diagnostics/support workflow: bounded redacted local incidents with fatal unknown-state behavior preserved.

This is a **deterministic hardening checkpoint**, not E8 completion. Do not keep inventing generic security/retry/cache frameworks merely because more abstraction is possible. Reopen deterministic hardening only when CI, code review, or a product contract identifies a concrete unowned/unbounded defect.

## Deferred empirical E8 boundaries

CI does not replace the following evidence:

- real game-process long-session/endurance behavior;
- measured representative large-World capture/package/restore/transfer behavior;
- actual EU production deployment/residency evidence;
- E4-B real Steam production identity + two-installation host/Join/save/commit handoff;
- real Windows assistive-technology/mixed-DPI UI acceptance;
- real Steam release/depot/update acceptance.

These remain recorded in `DEFERRED_EMPIRICAL_TESTS.md` and must not be promoted from synthetic/deterministic evidence.

## Remaining E8 direction

Highest-value remaining work, in order:

1. reconcile/cherry-pick the E8 stack onto the newer Factorio tree and obtain a new true all-workflows-green checkpoint;
2. execute E4-A against a real disposable EU deployment using real PostgreSQL, S3-compatible storage, HTTPS, restart/readiness, backup/restore, and transfer probes — no Steam publisher credentials are required for this infrastructure portion;
3. batch the recorded real-game, real-Windows, and real-Steam/two-installation empirical tests when the required machines/credentials are available;
4. record measured real package sizes, capture/restore/transfer durations, long-session behavior, provider residency configuration, and release/depot evidence;
5. only then decide whether E8/release acceptance can be called complete.

Do not start E9 performance optimization while these release-correctness/evidence boundaries remain open.
