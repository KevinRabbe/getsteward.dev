# Backend Schema and Data Lifecycle

Status: **CURRENT LOGICAL PERSISTENCE CONTRACT — IMPLEMENTED WITH POSTGRESQL-COMPATIBLE STORES**

This document defines the logical relational authority behind Backend.Api. Exact SQL/table/index details remain implementation/migration concerns; the logical identities, invariants, and transaction boundaries below are product contracts.

## Current PostgreSQL composition

Backend.Api currently composes PostgreSQL-backed stores for:

- normal Steward authentication sessions;
- shared World/current-head/revision metadata;
- World membership/invitations/Access Manager;
- writable reservation generations;
- durable idempotent authority mutation results;
- writable-responsibility inspection;
- narrow pre-launch reservation abandonment;
- short-lived Host presence;
- resumable immutable package-transfer metadata;
- shared revision retention/cleanup metadata.

Private S3-compatible storage contains opaque immutable package bytes. It does not replace these relational records and never decides which revision is canonical.

The design remains provider-neutral above the PostgreSQL/S3 infrastructure boundary.

## Logical records

### External identity

Logical identity contains:

- internal/external identity reference;
- provider (`steam` for production release identity; `friends-build` only for the explicitly enabled private test path);
- stable provider external ID;
- optional bounded presentation metadata where required by the service;
- session/access relationships.

Authentication proof bytes and infrastructure/provider secrets are not identity-record attributes.

Steam passwords/emails/payment data are never needed.

### Steward authentication session

Normal Steward sessions bind:

- verified external identity;
- Steward installation ID;
- access credential hash + expiry;
- refresh credential hash + expiry;
- revocation/rotation state required by the session service.

Plaintext access/refresh credentials are returned only to the authenticated client and are not persisted as plaintext by the server.

Credential/session expiry is not writable-World authority. An expired login session does not silently release a reservation generation.

### Shared World

A shared World record contains the minimum durable coordination metadata:

- opaque World ID;
- adapter ID;
- display name;
- current state revision ID;
- current environment revision ID where applicable;
- current Access Manager identity;
- created/updated metadata.

There is exactly one current compatible state/environment head per World.

The record does not contain gameplay ownership, branch history, merge state, permanent host identity, or game-process information.

### World membership/access

Logical access state records:

- World ID;
- external identity;
- membership/revocation-pending state;
- invitation identity and lifecycle metadata where applicable;
- Access Manager relationship;
- actors/timestamps required by the access service.

Membership is flat.

Pending invitations grant no package/reservation/commit access.

Revoking a member with unresolved writable responsibility does not erase that responsibility; removal waits safely in the pending state defined by the access service.

### Environment revision

An environment revision is immutable after publication and includes:

- revision ID + World relationship;
- adapter identity;
- structured/native artifact reference required by the World;
- optional hosted package byte size/SHA-256 where an environment package exists;
- publication timestamp/metadata.

An environment may be reproducible from game/platform-native inputs and therefore need no Steward-hosted package.

Backend stores the revision relationship but does not interpret game-specific environment semantics.

### State revision

A state revision is immutable after publication and includes:

- revision ID + World relationship;
- adapter identity;
- byte size;
- SHA-256;
- required environment revision relationship where applicable;
- publication timestamp/metadata;
- lineage/authority facts required to validate canonical commit.

A state revision may exist as a verified non-canonical candidate. Publication is not canonical advancement.

### Writable reservation

The reservation authority records enough durable state to prove one writer:

- World ID;
- session ID;
- monotonically increasing/non-reused generation semantics;
- holder external identity;
- holder installation ID;
- starting state head;
- starting environment head where applicable;
- Active/Uncertain/available-terminal facts;
- acquired/heartbeat/uncertainty/reclaim/completion metadata.

Only one non-terminal writable reservation authority may exist for one World.

A generation that has been invalidated/reclaimed can never become valid again.

### Idempotency record

Authority mutations whose duplicate execution would be unsafe persist:

- caller/operation scope;
- idempotency key;
- logical request fingerprint;
- durable logical result;
- bounded retention metadata.

Current authority use includes acquire, reclaim, and commit.

The same key + same logical request replays the original result. The same key + different logical request is rejected.

### Package transfer

A resumable package-transfer record contains the minimum state required to safely continue/finalize a direct object-store transfer:

- transfer ID;
- caller/resource scope;
- World/revision target;
- package kind;
- expected byte size/SHA-256;
- required environment relation where relevant;
- provider upload/object reference;
- part sizing/count/progress information;
- expiry/state/finalization metadata.

Transfer records cannot make a revision canonical.

### Host presence

Host presence is short-lived non-authoritative evidence bound to:

- World;
- exact reservation session ID;
- exact reservation generation;
- host installation/caller authority;
- Starting/Ready state;
- game-owned address/port/join material where applicable;
- expiry/refresh metadata.

A stale/uncertain/superseded reservation cannot publish newer-looking Host evidence.

Presence is deleted/expired independently from canonical World history.

### Retention/cleanup metadata

The backend needs enough relational metadata to distinguish:

- current canonical state/environment revisions;
- previous canonical revisions retained by policy;
- candidates pinned by active/recovery dependencies;
- verified but uncommitted candidates inside grace/hold;
- active/abandoned transfers;
- cleanup-eligible objects.

Object deletion is downstream of authority classification. Age alone does not prove safe deletion.

## Database invariants

The relational layer must enforce or transactionally guarantee:

1. every current World head references immutable revision metadata for that same World/adapter;
2. compatible state/environment relationships are validated before current-head advancement;
3. a state head never advances unless the expected prior head is still current;
4. at most one non-terminal writable reservation generation exists per World;
5. reservation generation identity is never silently reused/revived;
6. only authorized active members may read/use protected World resources;
7. only matching reservation authority may heartbeat/commit/publish reservation-bound Host presence;
8. an unverified/invalid package can never become a valid published State/Environment revision;
9. object-store existence alone cannot change the canonical head;
10. idempotency-key reuse with different logical input is rejected;
11. revocation cannot silently delete canonical state or unresolved recovery/writer evidence;
12. cleanup cannot remove current/pinned/in-use data;
13. Host presence cannot grant writable authority;
14. authentication-session expiry cannot manufacture reservation availability.

## Transaction boundaries

### Authenticate/session issue

```text
verify external proof
-> derive backend-verified external identity
-> create/rotate normal Steward session credentials
-> persist only server-side session authority/hash state
```

Authentication is separate from World membership and reservation authority.

### Create shared World

```text
authorized verified caller
-> create World with exact supplied Steward identity/head references
-> establish creator membership
-> establish one Access Manager
```

Initial sharing/publication logic verifies the referenced immutable environment/state separately. Failed publication must not silently restore local writable authority once remote creation may have become ambiguous.

### Acquire reservation

In one authority decision:

```text
check caller active membership
-> lock/read current compatible World head
-> compare expected state/environment head
-> verify no conflicting Active/Uncertain reservation
-> create/adopt caller's valid Active generation
-> store idempotent logical result
```

A race produces one writer result, never two.

### Heartbeat

```text
check caller + exact session/generation/installation
-> update server-observed heartbeat
```

A stale generation cannot refresh itself.

### Active -> Uncertain

Server-observed missed-heartbeat time may classify an Active reservation as Uncertain.

That transition blocks competing writers. It does not make the World available.

### Reconnect

The same still-valid generation may reconnect and return to Active while it has not been invalidated/reclaimed.

### Reclaim

In one authority transaction:

```text
verify current reservation is reclaimable Uncertain generation
-> verify caller membership + grace/head conditions
-> atomically invalidate old generation
-> persist durable idempotent result
-> leave canonical head at the last committed valid revision
```

A new writer acquires separately after the old authority has been invalidated.

Reclaim does not promote or delete an unresolved candidate.

### Pre-launch abandon

The narrow abandon transaction validates the exact reservation identity/installation when Core proves gameplay never began.

It may release that unused authority without waiting for uncertainty/reclaim.

It is not valid for post-launch uncertainty where gameplay changes may exist.

### Publish candidate

```text
authorize transfer/resource
-> direct object upload
-> verify expected size/hash/provider completion
-> publish immutable revision metadata
```

Do not update current World head in this transaction.

### Commit candidate

In one canonical authority transaction:

```text
lock/check current World head
-> verify immutable candidate + World/adapter/environment relationships
-> verify exact reservation session/generation/installation is still commit-eligible
-> require current head == expected starting head
-> atomically advance compatible state/environment head
-> persist commit/idempotency result
```

Any failure leaves the previous canonical head authoritative.

### Host-presence publish

```text
verify caller active membership/installation
-> verify exact Active reservation session/generation
-> publish/refresh bounded Starting/Ready endpoint evidence
```

No head/reservation rights are created by this operation.

### Access Manager transfer/revocation

Administrative access changes are transactional with the access invariants:

- exactly one Access Manager remains where required;
- pending invitation has no normal member authority;
- active writer revocation does not invalidate a healthy write merely as an administrative shortcut;
- leave/transfer cannot abandon unresolved responsibility.

## Retention and object cleanup

Current first-release canonical retention normally keeps:

> **current canonical state + previous two successfully committed canonical states**

plus any older state/environment package still pinned by active/recovery dependencies.

Verified uncommitted remote candidates receive a bounded grace/hold. Partial transfers receive a shorter inactivity/abandonment lifetime. Exact values are operational configuration, not authority semantics.

Unresolved local recovery candidates are outside backend object cleanup and are not silently deleted by backend retention.

Cleanup workers:

```text
classify relational authority/dependencies
-> identify bounded cleanup candidates
-> delete object bytes where safe
-> reconcile metadata/result idempotently
```

A cleanup failure is operational debt; it does not roll back a valid canonical commit.

## Schema/API versioning

- HTTP control routes use `/api/v1`.
- PostgreSQL schema evolution is explicit and tested.
- local persisted documents have their own versioned compatibility envelopes; do not conflate those schema versions with backend SQL schema/versioned API routes.
- additive evolution is preferred.
- incompatible changes require deliberate migration/compatibility handling.
- Core domain types do not gain PostgreSQL/S3/Steam SDK implementation types.

## Backup and restore

CI already proves PostgreSQL-native dump/restore of production-store data into a fresh database without rerunning Steward initialization to “repair” the restored state.

Real operations still require provider-level backup/restore evidence in the selected EU deployment.

Restore validation must confirm at least:

- World/current-head references;
- membership/Access Manager state;
- immutable revision metadata relationships;
- reservation safety;
- idempotent authority state where retained;
- object/package integrity/availability for retained current/recovery data.

An incomplete/uncertain restore must fail closed for new shared writable sessions rather than present Ready.

## Data minimization

The backend deliberately does not need:

- game save contents in relational tables;
- Steam passwords/emails/payment data;
- full friend graph;
- gameplay roles/progression;
- permanent game server records;
- branch/merge graphs;
- arbitrary client filesystem paths;
- client plaintext session credentials at rest on the server.

The logical schema exists only to preserve shared World continuity and authority safely.