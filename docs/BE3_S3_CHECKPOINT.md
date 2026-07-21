# BE-3 Immutable Transfer Status

Status: **COMPLETE AND GREEN**.

BE-3 now provides the complete provider-neutral immutable package path required before distributed World authority is implemented.

The remaining real-provider credential/deployment proof is a deployment validation gate, not an architectural blocker for BE-4.

## Provider-neutral transfer authority

Implemented:

- private immutable object-storage contract;
- direct client/object-store multipart transfer authorization;
- 20 GiB first-release package ceiling;
- approximately 64 MiB default multipart target;
- 24-hour transfer lifetime and short-lived transfer authorizations;
- expected byte size and SHA-256 declared before transfer;
- active-member authorization before new upload;
- StateRevision -> required EnvironmentRevision validation;
- owner-private transfer IDs;
- resumable progress;
- exact object verification before immutable revision publication;
- `RevocationPending` writers may finish an already-authorized transfer;
- native/reproducible environments may remain references without a Steward-hosted package;
- object storage cannot advance the canonical World head.

## Durable PostgreSQL transfer persistence

PostgreSQL persists the resume-critical transfer record:

- World / Revision / package kind;
- owner;
- deterministic object key;
- opaque durable provider upload handle;
- expected size / SHA-256;
- required environment reference;
- part size/count;
- created/expiry timestamps;
- transfer state/finalization time.

State mutation uses owner + expected-state compare-and-set semantics.

Cleanup queries are bounded, state/cutoff filtered, oldest-first, and cleanup deletion requires the exact owner/state tuple.

## Generic S3-compatible production adapter

Project:

`SharedWorlds.Backend.ObjectStorage.S3`

Configuration remains provider-neutral:

- endpoint;
- authentication region;
- bucket;
- access key;
- secret key;
- path-style option.

Supported behavior:

- initiate multipart;
- paginated part listing/resume;
- presigned part PUT;
- multipart completion;
- idempotent completion recovery;
- idempotent abort;
- private presigned GET;
- delete;
- streamed full-object SHA-256 verification with bounded memory.

The opaque multipart handle is restart-safe and contains the object key, native multipart upload ID, expected size, and expected SHA-256. No process-local upload-ID map is required.

The S3 protocol compatibility gate exposed and fixed a real issue: presigned URLs initially defaulted to HTTPS independently of a custom HTTP endpoint. Presigned PUT/GET now explicitly follow the configured endpoint scheme.

## S3-compatible protocol proof — COMPLETE AND GREEN

Disposable MinIO is used only as a CI S3 protocol harness. It is not a production dependency or provider choice.

The live protocol proof passes:

```text
begin multipart
-> authorize direct part PUTs
-> upload bytes directly to object storage
-> observe parts
-> destroy first backend adapter/client
-> create fresh adapter/client
-> resume using persisted opaque handle
-> complete
-> independently verify full size + SHA-256
-> replay completion idempotently
-> authorize private GET
-> download identical bytes
-> delete
```

Abort idempotency is also proven.

## Versioned HTTP/JSON control plane — COMPLETE AND GREEN

Production host:

`SharedWorlds.Backend.Api`

Implemented `/api/v1` surface includes:

- Steam ticket -> Steward session;
- session refresh/revoke;
- accessible World list/read/create;
- current revision metadata;
- immutable package-transfer create/progress;
- scoped part authorization;
- transfer finalization;
- scoped revision download authorization.

Boundary rules:

- Bearer Steward access credentials protect shared routes;
- stable machine-readable domain codes drive client behavior;
- infrastructure/protocol failures use Problem Details-style responses;
- retryability and correlation IDs are machine-readable;
- provider upload handles/private object keys are not exposed in normal transfer DTOs;
- storage URLs appear only as short-lived scoped authorizations;
- BE-3 finalization publishes immutable revision metadata but **does not advance the canonical World head**.

The TestServer suite proves routing, Bearer authentication, stable outcomes, transfer lifecycle, no storage-internal leakage, and that canonical head remains unchanged after BE-3 publication.

## Conservative cleanup and retention reconciliation — COMPLETE AND GREEN

`SharedPackageTransferCleanupService` owns transfer-session cleanup only.

Rules:

- expired incomplete multipart may be aborted;
- dead incomplete transfer rows may be guardedly deleted;
- exact completed candidate becomes/persists as `Abandoned` instead of being silently deleted;
- verified exact candidates older than the ordinary seven-day grace become **cleanup-eligible only**;
- the cleanup worker does not physically delete a verified candidate because transfer state alone cannot prove that no authoritative revision/recovery reference exists;
- integrity failures and publication conflicts remain evidence;
- matching published revision metadata repairs stale transfer state to `Finalized`;
- expired `Abandoned` transfers are reconciled every pass, so the seven-day window never delays publication/conflict repair;
- canonical revision retention remains BE-D010 authority, separate from transfer cleanup.

API background worker defaults:

- 15-minute interval;
- seven-day verified-candidate grace;
- batch size 100;
- bounded configuration;
- first cleanup occurs after the interval, not during API startup;
- failures retry next pass;
- multiple API instances can run the worker because destructive boundaries are compare-and-set/idempotent rather than coordinated through Redis.

## Desktop download verification/cache/materialization — COMPLETE AND GREEN

The existing lifecycle already materializes an `IWorldStorage.OpenRevisionAsync` stream into a temporary `.package` before adapter restore. BE-3 therefore adds the missing verified remote layer beneath that existing boundary instead of creating a second materialization architecture.

Implemented files:

- `SharedWorlds.Infrastructure/Remote/AuthorizedPackageDownload.cs`;
- `VerifiedPackageCache.cs`;
- `StewardPackageDownloadClient.cs`;
- `StewardVerifiedPackageSource.cs`.

Path:

```text
Steward download authorization
-> direct object-store GET
-> resumable local .partial
-> exact size + SHA-256 verification
-> atomic content-addressed cache publication
-> verified read-only stream
-> existing lifecycle package materialization
```

Cache rules:

- cache identity is SHA-256, enabling safe local content reuse without server-side BE-7 dedup logic;
- presigned URLs and Steward credentials are never persisted;
- incomplete downloads remain `.partial` and resume with `Range` under a fresh authorization;
- `206` Content-Range is validated;
- a provider that ignores Range and returns `200` causes a safe restart from byte zero rather than append;
- wrong size/hash never becomes a final cache entry;
- existing final entries are reverified before reuse;
- final cache publication happens only after complete verification;
- an atomic publish collision is followed by re-verification of the winning final file, so a SHA-shaped filename is never trusted as proof;
- a corrupt competing final is rejected, then removed/redownloaded on the next attempt;
- best-effort free-space preflight is bounded;
- in-process keyed gates prevent duplicate same-hash downloads and are removed when idle.

Credential separation is explicitly tested with separate HttpClients:

- Steward Bearer token appears only on the Steward API request;
- it never reaches the object-store GET;
- storage receives only its scoped required headers.

The client preserves machine-readable backend status/code/retryability for higher-level policy.

## Final BE-3 evidence

CI run `29875411791` on commit `58a73702dbe96219e7b781b5c869cae35972e01e` passed all five gates:

- Quality;
- Ubuntu Release build + full tests;
- Windows Release build + full tests;
- live PostgreSQL integration;
- live S3-compatible direct-transfer integration.

This final run includes the desktop verified-cache tests and corrupt competing-publication regression.

## Provider deployment status

The generic first-release transfer architecture is complete without a production provider dependency.

Current research has identified Hetzner Object Storage as a plausible EU deployment target because its S3-compatible surface fits the frozen contract. Actual provider credentials, production bucket configuration, EU residency confirmation, measured cost/egress behavior, and deployment rollback remain later deployment/commercial-hardening gates.

A provider may not redefine World authority, revision semantics, transfer limits, recovery, or cache behavior.

## BE-3 completion boundary

BE-3 is complete because the system now has a real path for authenticated immutable package publication, resumable direct transfer, durable transfer recovery, independent integrity verification, safe cleanup eligibility, and verified client caching/materialization.

BE-3 intentionally does **not** decide which writer or revision is authoritative.

The next backend milestone is **BE-4: Distributed reservation and commit**:

- one writable generation per World;
- acquire/heartbeat/Uncertain/reconnect;
- deliberate reclaim;
- generation invalidation;
- expected-head atomic commit;
- atomic state/environment head advancement where required;
- idempotent mutation behavior;
- competing-writer race rejection;
- late invalidated writer rejection.
