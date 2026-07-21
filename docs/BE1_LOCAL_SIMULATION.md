# BE-1 Local Contract Simulation

This document defines the first backend implementation slice after the master
planning lock is lifted. It is a provider-free deterministic simulation of the
remote backend contract, not a production API or database.

## Current status

Status: **implementation complete against the BE-1 contract matrix; build/test confirmation pending**.

Implemented in the current BE-1 slice:

- deterministic shared World authority;
- membership-gated metadata/revision access;
- one active writable reservation;
- expected-head acquisition and commit;
- generation-based heartbeat/reconnect/reclaim;
- `Active -> Uncertain` anchored to the heartbeat deadline;
- late invalid-generation commit rejection;
- immutable candidate publication with size/SHA-256 verification;
- resumable multipart transfer state;
- retry-safe duplicate transfer parts;
- provider-independent 20 GiB package safety ceiling;
- idempotent acquire/finalize/commit/reclaim/last-safe recovery replay with request-fingerprint conflict detection;
- authoritative operation-result lookup after ambiguous response loss;
- deterministic failure injection before commit and after durable commit/before response;
- explicit `Continue from last safe state` that validates candidate evidence before releasing authority and preserves the abandoned candidate;
- BE-D009 candidate cleanup-eligibility policy;
- BE-D010 current + previous-two canonical retention with pinned recovery dependencies and reference-driven environment retention;
- deterministic PC A -> PC B -> PC A handoff coverage.

The deterministic test matrix below now has an implementation/test case for every listed scenario. BE-1 is not marked fully green until the repository build/test suite actually executes successfully under warnings-as-errors/nullability rules.

No Steam integration, HTTP hosting, cloud SDK, provider credential, production database, or object-storage integration belongs in this milestone.

## Purpose

BE-1 proves the safety rules before Steam, HTTP, cloud storage, or deployment
details are introduced:

- one current valid state head per World;
- at most one active writable reservation;
- immutable candidate publication;
- expected-head compare-and-swap;
- generation invalidation after recovery/reclaim;
- idempotent retry behavior;
- preservation of candidates and the previous valid head after failure.

## Boundary

The simulation exposes application-level contracts only. It must not depend on:

- ASP.NET Core or a specific HTTP framework;
- Steam SDKs or Web API credentials;
- PostgreSQL, S3, or another provider;
- Factorio, Palworld, or game-specific save parsing;
- process state, file timestamps, or local UI behavior.

The implementation may use an in-memory repository and a deterministic clock.
All generated ids, timestamps, and failure injection points must be controllable
by the tests.

## Simulated records

The minimum records are:

- `WorldRecord`: World id, adapter id, current environment/state heads, access
  identities, and lifecycle metadata;
- `RevisionRecord`: immutable revision id, World id, kind, previous head,
  content hash, byte size, publication state, and opaque package bytes;
- `ReservationRecord`: World id, session id, generation, holder identity,
  device id, starting head, mode, state, heartbeat deadline, and recovery grace;
- `TransferRecord`: transfer id, target revision, expected size/hash, uploaded
  parts, expiry, and finalization state;
- `IdempotencyRecord`: caller, operation, key, request fingerprint, and durable
  result.

The simulation may keep records in dictionaries, but state-changing operations
must behave as if the relevant World row and reservation row were updated in
one transaction.

## Contract operations

Implement these operations in this order:

1. Create/list/get World metadata and enforce access checks.
2. Start, resume, finalize, and abandon an immutable transfer.
3. Publish a verified candidate without advancing the current head.
4. Acquire, heartbeat, inspect, and release a reservation.
5. Commit a candidate with expected-head and generation checks.
6. Mark uncertainty, recover the original session, and reclaim after grace.
7. Repeat every important retryable mutation with the same idempotency key and expose authoritative completion lookup where ambiguity can remain after transport failure.

## Deterministic test matrix

The first test suite must include:

| Scenario | Required invariant |
|---|---|
| Two clients acquire the same World concurrently | Exactly one succeeds |
| Stale expected head commits a valid candidate | Candidate is not current; old head remains authoritative |
| Active reservation blocks another acquire | Second client receives `AlreadyReserved` |
| Heartbeat from wrong generation | Reservation is unchanged; caller receives `ReservationMismatch` |
| Missed heartbeat | Reservation becomes `Uncertain`, never `Available` |
| Delayed first observation of missed heartbeat | Uncertainty/reclaim timing is measured from the heartbeat deadline, not the observation request |
| Reconnect during grace | Original generation may resume; competing acquire remains blocked |
| Reclaim after grace | Old generation is invalidated before new acquire succeeds |
| Late old-generation commit | Rejected; current head remains authoritative |
| Interrupted multipart transfer | Recorded parts resume without restarting from byte zero |
| Duplicate transfer part | Identical retry is accepted; different bytes for the same part conflict |
| Duplicate transfer finalization | Same logical result is returned; no mutable duplicate is created |
| Same idempotency key with different request | Rejected as key reuse conflict |
| Truncated or altered package | Publication rejected by size/hash verification |
| Package above first-release hard ceiling | Authorization/start is rejected before package bytes are accepted |
| Commit failure before transaction | Previous canonical head remains authoritative and no durable operation result exists |
| Commit response loss after durable success | Retry and authoritative status lookup return the durable prior result |
| Failed candidate commit | Candidate remains recoverable; current head is unchanged |
| Invalid last-safe request | Valid reservation is not released before candidate/authority preconditions are proven |
| Continue from last safe state | Candidate is explicitly abandoned only after authority is resolved and remains preserved |
| Unresolved local candidate retention | Time alone never makes uncommitted gameplay cleanup-eligible |
| Abandoned/remote/partial candidate retention | Each candidate follows its BE-D009 grace/pin rule |
| Unauthorized World/revision/transfer access | No private existence or metadata is disclosed |
| Canonical retention advances | Current + previous two remain, except older pinned recovery dependencies |

## Two-client acceptance scenario

Use fixed identities `steam-a` and `steam-b`, devices `pc-a` and `pc-b`, and
initial state `S0`:

```text
create World at S0
-> steam-a/pc-a acquires at S0
-> publish and commit S1
-> release reservation
-> steam-b/pc-b downloads and verifies S1
-> steam-b/pc-b acquires at S1
-> steam-a/pc-a is rejected while B is active
-> publish and commit S2
-> release reservation
-> steam-a/pc-a downloads and verifies S2
```

The test fails if a stale client can commit, if two writers are active, if a
candidate becomes current before verification, or if the previous valid head is
lost after any injected failure.

## Completion evidence

BE-1 is complete when the test suite demonstrates the matrix above with a
deterministic clock and repeatable failure injection, the build/test suite is
green under the repository's warnings-as-errors policy, and the contract can be
implemented without referring to a provider, game adapter, or UI decision.
