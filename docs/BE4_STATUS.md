# BE-4 Status — Distributed Reservation and Canonical Commit

Status: **COMPLETE AND GREEN**

BE-4 replaces the provider-free BE-1 authority simulation with durable PostgreSQL-backed one-writer authority and a versioned HTTPS/JSON control surface. Object storage remains an immutable byte store and never decides which revision is canonical.

## Proven authority model

For each shared World, Steward now persists exactly one reservation record with a monotonically increasing generation.

```text
Available
-> Active generation
-> Uncertain after missed heartbeat threshold
-> same valid generation reconnects
   OR deliberate reclaim invalidates that generation
-> Available
```

A successful commit is the only operation that advances the canonical World head:

```text
verified immutable candidate exists
AND current head == expected starting head
AND reservation session/generation/installation is still valid
-> atomically advance state/environment head
-> resolve reservation
```

Every failed commit leaves the previous canonical head authoritative.

## Implemented and validated

- active-membership + exact-head reservation acquisition;
- parallel acquisition allows exactly one writer;
- server-observed heartbeat;
- `Active -> Uncertain` transition without manufacturing availability;
- reconnect by the same still-valid generation;
- deliberate reclaim only after the configured uncertainty grace period;
- monotonically increasing generation on the next acquisition;
- invalidated generations cannot heartbeat or commit;
- atomic state + environment head advancement;
- candidate environment compatibility validation;
- unchanged-state resolution without unnecessary head advancement;
- `RevocationPending` writer may finish its already-acquired responsibility safely;
- unresolved writable responsibility remains queryable for access/lifecycle safety.

## Durable mutation idempotency

BE-D013 is now implemented for the authority mutations whose ambiguous network outcome could otherwise duplicate a transition:

- acquire;
- reclaim;
- commit.

The HTTP contract requires one valid `Idempotency-Key` header for those mutations.

The PostgreSQL authority layer stores:

- caller scope;
- operation scope;
- idempotency key;
- World ID;
- logical-request SHA-256;
- exact completed domain result;
- bounded expiry metadata.

The authority mutation and its idempotency result are committed in the **same PostgreSQL transaction**. Therefore a client can safely retry after losing the response:

```text
same key + same logical request
-> original logical result is replayed

same key + different logical request
-> IdempotencyKeyConflict
```

Same-key concurrency is serialized with a transaction-scoped PostgreSQL advisory lock. The lock is coordination only; the durable database row is the retry truth after the transaction completes.

Heartbeat intentionally remains outside this one-shot idempotency-key contract because it is a repeated liveness observation for an already-valid generation.

## Production composition

The API production graph is:

```text
PostgreSqlSharedWorldAuthorityStore
        |
        v
PostgreSqlIdempotentSharedWorldAuthorityStore
        |   commit idempotency
        v
PostgreSqlIdempotentReservationAuthorityStore
        |   acquire/reclaim idempotency
        v
ISharedWorldAuthorityStore
        |
        v
SharedWorldAuthorityService
        |
        v
/api/v1/.../reservation/*
```

The base PostgreSQL authority store remains the responsibility inspector used by membership/access flows.

## Acceptance evidence

PostgreSQL integration coverage proves:

- parallel one-writer acquisition;
- membership/head acquisition preconditions;
- uncertainty and same-generation reconnect;
- reclaim grace + generation invalidation;
- safe completion while revocation is pending;
- atomic state/environment commit;
- invalid-candidate rejection;
- unchanged-state resolution;
- late invalidated-writer rejection;
- acquire replay/conflict/concurrency idempotency;
- reclaim replay/conflict idempotency;
- commit replay/conflict/concurrency idempotency after the reservation has already been resolved.

HTTP integration coverage proves stable machine-readable idempotency behavior, including missing/invalid keys, exact replay, and conflicting key reuse.

### CI proof

GitHub Actions CI run `29906786662` on the BE-4 authority/idempotency tree passed:

- Quality / formatter verification ✅
- Ubuntu Release build + tests ✅
- Windows Release build + tests ✅
- live PostgreSQL integration ✅
- S3-compatible direct-transfer integration ✅

The S3 gate is intentionally still part of the matrix: BE-4 authority changes must not regress BE-3 immutable-transfer behavior.

## What BE-4 does not add

BE-4 does not add:

- save merging or branches;
- permanent game-server hosting;
- Access Manager reservation priority;
- object-store canonical authority;
- active-active multi-region consensus;
- automatic timeout release from `Uncertain`.

## Next milestone

The next backend acceptance target is **BE-5 — two-device proof**:

```text
PC A commits N+1
-> PC B downloads and verifies N+1
-> PC B acquires authority and commits N+2
-> PC A downloads and verifies N+2
```

That proof must also exercise competing-writer rejection, interrupted transfer/resume, outage/uncertainty behavior, deliberate reclaim, late-generation rejection, and last-safe recovery through the real composed boundaries.
