# Backend Roadmap

## Purpose

This roadmap defines the smallest persistent backend required to make one shared World available across different Steam users, devices, and times without running the game server permanently.

The backend exists only to support:
- verified Steam identity for shared operations;
- minimal World membership/access administration;
- durable latest World state;
- immutable state/environment package storage;
- one writable session reservation;
- safe current-head advancement;
- interrupted-handoff recovery;
- commercial security/operations boundaries.

It must not become a second Steam, social platform, permanent game-hosting fleet, or generic save-merging/version-control service.

## Implementation status

Status: **BE-0 approved; implementation unlocked. BE-1 COMPLETE AND GREEN. BE-2 COMPLETE AND GREEN. BE-3 ACTIVE.**

BE-D001 through BE-D015 remain the canonical first-release backend decisions.

Current implementation evidence:
- BE-1 deterministic contract foundation: `E1_STATUS.md`;
- BE-2 authenticated World/access/revision metadata + PostgreSQL persistence: `BE2_STATUS.md`;
- active backend milestone: **BE-3 — Immutable object transfer**.

# Approved BE-0 decisions

## BE-D001: Deliberately hybrid backend

Status: **approved**.

Steward uses Steam for identity/platform functionality and one small Steward backend for persistent cross-user authority Steam does not provide.

```text
Steward desktop clients
        |
        | HTTPS/JSON control plane
        v
small Steward API / coordination service
        |-- one transactional relational database
        `-- immutable object/blob storage
```

The relational database is authoritative for:
- shared World metadata;
- membership and Access Manager;
- current state/environment head pointers;
- active reservation/session generation;
- compare-and-swap commit state;
- invitation/revocation/recovery coordination metadata.

Object storage contains opaque immutable packages. It never decides which revision is current.

First release does not require Redis, Kafka, message brokers, Kubernetes, microservice fleets, or permanent game-server compute.

Core principle:

> **Use Steam for what Steam already owns. Steward owns only the missing shared-World transaction.**

## BE-D002: Steam authentication bootstraps a Steward session

Status: **approved**.

The Windows desktop does not require a separate Steward username/password.

```text
Steward desktop obtains Steam Web API authentication ticket
-> backend verifies ticket directly with Steam
-> verified SteamID64 becomes authenticated external identity
-> backend creates short-lived Steward access credential
-> renewable installation-bound refresh session supports normal API use
```

Rules:
- client-supplied SteamID alone is never authentication;
- Steam publisher/Web API secrets remain server-side;
- initial access credential target is about 15 minutes;
- renewable installation-bound refresh session target is about 30 days;
- those durations are operational defaults, not product invariants;
- refresh credential uses Windows-protected credential storage;
- random Steward installation/device ID is not authentication or invasive hardware fingerprinting;
- Steam account identity changes require reauthentication;
- active/recovery evidence is never silently reassigned between identities;
- local-only behavior remains available where shared authentication is irrelevant;
- Steward stores no Steam password, email, payment data, or unnecessary profile/social data.

Core principle:

> **Steam proves who you are. Steward decides what that verified identity may do.**

## BE-D003: Flat members plus one Access Manager

Status: **approved**.

A shared World has a flat set of accepted Steam members. Exactly one active member additionally holds **Access Manager** responsibility.

Every active member may:
- see/download the shared World;
- Start World;
- Host World;
- Join;
- acquire the one writable reservation when available;
- complete a valid handoff;
- leave when no unresolved responsibility is abandoned.

Access Manager may additionally:
- create World-access invitations;
- revoke future membership;
- atomically transfer access management;
- stop sharing/delete according to deletion policy.

Access Manager has no special:
- gameplay authority;
- reservation priority;
- hosting privilege;
- overwrite/merge privilege;
- right to terminate a healthy active writer merely for administrative reasons.

World-access invitations are distinct from Steam/game multiplayer-session invitations.

Pending World-access invitation grants no package/reservation/commit access before acceptance.

Revocation of a member who owns active writable responsibility becomes pending until the responsibility resolves safely.

Core principle:

> **Membership controls who may use the World. Access Manager controls only who is a member.**

## BE-D004: Versioned HTTPS/JSON control API with direct package transfer

Status: **approved**.

First release uses a versioned HTTPS request/response control API, such as `/api/v1/`, with JSON for metadata and coordination.

Large package bytes normally transfer directly between authorized desktop clients and object storage through short-lived scoped authorization.

Rules:
- no base64-embedded large packages in JSON;
- transactional operations may use explicit command endpoints where clearer than artificial CRUD;
- API authenticates/authorizes and issues transfer authorization;
- object storage moves opaque bytes but owns no World authority;
- important retryable mutations are idempotent;
- machine-readable result states drive client behavior;
- first release does not depend on persistent WebSockets, gRPC, custom binary protocols, or provider-specific transport semantics.

Core principle:

> **The API controls authority. Object storage moves bytes.**

## BE-D005: Heartbeat, uncertainty, and deliberate reclaim

Status: **approved**.

A writable reservation is not a simple expiring lock.

Initial operational defaults:
- heartbeat approximately every **30 seconds**;
- `Active -> Uncertain` after approximately **2 minutes** without a valid heartbeat;
- deliberate reclaim by another active member becomes available after approximately **15 minutes** in Uncertain.

These values are tunable operational constants. The safety semantics are not.

```text
Available
-> Active
-> Uncertain
```

From `Uncertain`:

```text
same still-valid generation reconnects
-> Active
```

or:

```text
deliberate reclaim/recovery
-> atomically invalidate old generation
-> resolve authority from last committed safe state
-> Available
```

There is no automatic timeout transition from Uncertain to Available.

Rules:
- backend/server time is authoritative for heartbeat observation;
- Uncertain blocks every competing writer;
- original still-valid generation may reconnect immediately while not invalidated;
- original holder may deliberately resolve/abandon its own unresolved responsibility without another-member grace delay where authority checks permit;
- after the grace window any active World member may deliberately reclaim;
- Access Manager has no special reclaim privilege;
- reclaim atomically validates current Uncertain state/generation/member/head compatibility and invalidates the old generation before another writer may acquire;
- late invalidated generation can never commit;
- late invalidated candidate is preserved as recovery material.

Core principle:

> **A timer may create uncertainty. A timer may never manufacture a second writer.**

## BE-D006: Continue from last safe state resolves authority before abandonment

Status: **approved**.

`Continue from last safe state` is an explicit recovery operation, not a cleanup shortcut.

It may become available only after Steward can safely resolve the outstanding reservation/session generation and verify the authoritative canonical head.

Rules:
- unresolved candidate is never silently merged or promoted;
- old writable generation is invalidated/released as required before another writer may start;
- the user explicitly acknowledges that newer local changes are being abandoned as canonical changes;
- the preserved candidate follows BE-D009 retention rather than being deleted as part of the authority transaction;
- current canonical head remains the last known-good committed state.

## BE-D007: Provider-neutral first implementation

Status: **approved**.

The first implementation slice is provider-free and proves backend contracts before selecting production infrastructure.

Initial provider contract:
- PostgreSQL-compatible transactional relational database semantics;
- S3-compatible or equivalent private immutable object-storage semantics;
- scoped direct upload/download authorization;
- resumable large-object transfer;
- encryption at rest and standard TLS support;
- EU deployment capability.

Named database/object-storage vendors are deliberately deferred until measured package size, transfer, restore, durability, and cost evidence exists.

BE-1 used deterministic in-memory/local simulation and no cloud SDKs/credentials. BE-2 then implemented the relational side with provider-neutral PostgreSQL semantics. BE-3 applies the same rule to immutable object transfer before a production storage provider is selected.

Commercial pricing is not required to define the provider-neutral transfer contract; hard technical bounds, retention rules, cost telemetry requirements, and provider-neutral assumptions remain sufficient until provider selection is actually needed for deployment.

## BE-D008: Active-session connectivity loss

Status: **approved**.

A shared writable session that was validly acquired may continue locally through temporary backend connectivity loss.

Before a new shared session:
- backend identity/head/reservation cannot be verified -> **Connection required**;
- no Start World, Host World, or Join.

After a valid session already started:
- gameplay/server may continue;
- heartbeat loss eventually moves remote reservation to Uncertain under BE-D005;
- outage never automatically releases the World;
- runtime preserves session ID/generation/starting revision/local recovery evidence.

If the same still-valid generation reconnects, it resumes the reservation/heartbeat.

If the session ends while backend connectivity remains unavailable:

```text
adapter proves safe capture
-> candidate captured/validated locally
-> candidate persisted durably
-> Waiting to sync
```

On reconnect Steward revalidates:
1. authentication;
2. session generation;
3. expected current head.

If generation remains valid and head unchanged:
- upload/verify candidate;
- compare-and-swap commit;
- release/finalize;
- Ready.

If generation was reclaimed/invalidated or canonical head changed:
- candidate cannot auto-commit;
- enter **Recovery needed**;
- preserve candidate.

Retries are bounded/backed off but responsibility is never abandoned merely because a retry count is exhausted.

## BE-D009: Candidate retention and cleanup

Status: **approved**.

Core rule:

> **An unresolved local candidate containing uncommitted gameplay changes is never deleted merely because time passed, connectivity stayed unavailable, or retries failed.**

Policy:

| Data | First-release retention |
|---|---|
| Unresolved local recovery/unsynchronized candidate | No automatic time-based deletion |
| Explicitly abandoned local candidate after `Continue from last safe state` | Approximately 7-day disclosed recovery grace |
| Verified remote candidate that never committed | Approximately 7 days; extend while legitimately referenced by active recovery |
| Partial/incomplete transfer | Approximately 24 hours after abandonment/inactivity |
| Successfully committed temporary local candidate | Cleanup-eligible after durable success is recorded |
| `Unchanged` redundant candidate | Cleanup-eligible after durable result is recorded |

Disk pressure does not silently delete unresolved gameplay changes. It becomes **Action required**.

Cleanup ordering is always authority/durability first, deletion later.

## BE-D010: Canonical revision retention

Status: **approved**.

For a first-release shared World, Steward normally retains:

> **current canonical state revision + previous two successfully committed canonical state revisions**

These exist for recovery/durability only. They are not exposed as branches, merge sources, or normal selectable history.

Rules:
- retention advances only after a new canonical commit succeeds durably;
- older revision referenced by an active transaction or unresolved recovery remains pinned even outside the normal three-revision window;
- environment revisions are retained by reference while any retained/pinned state requires them;
- physical deletion may occur asynchronously after data becomes cleanup-eligible.

Local-only backup/history expansion is not required by this remote retention rule.

## BE-D011: State and environment use one immutable package pipeline

Status: **approved**.

State and environment artifacts use the same first-release immutable transfer infrastructure while remaining logically distinct revision references.

Common pipeline:

```text
authorize transfer
-> upload/download opaque immutable bytes
-> verify expected size/hash
-> publish immutable package metadata
```

Rules:
- many state revisions may reference one environment revision;
- adapters/runtime decide whether an environment package is required and what it contains;
- backend never infers game-specific environment semantics;
- prefer deterministic manifests/references to re-uploading content already reliably supplied by Steam/Workshop/native systems;
- when one transaction changes both required environment and state, World-head advancement can update their references atomically so the canonical head identifies one compatible playable combination;
- unchanged environment does not upload again unnecessarily.

## BE-D012: Package size and transfer limits

Status: **approved**.

Initial provider-independent hard ceiling:

> **20 GiB per immutable State or Steward-hosted Environment package**

This is a safety ceiling, not a commercial entitlement or advertised target.

Adapters may enforce a smaller validated package limit. Effective limit is the stricter boundary.

Transfer rules:
- expected size and package identity are declared before authorization;
- authorization/finalization enforce actual size and exact content hash;
- package cannot become a valid revision until size/hash verification succeeds;
- large transfers use resumable/multipart behavior;
- approximately **64 MiB** parts are the initial tuning target where appropriate;
- small packages may use simpler single-operation transfer;
- interrupted downloads resume/range-transfer where useful and are fully verified before restore;
- local disk preflight estimates required working space before large materialization;
- compression/package format remains adapter-owned;
- backend does not unpack/recompress game saves;
- first release uses full immutable package transfer rather than delta synchronization.

## BE-D013: Deterministic API result and error contract

Status: **approved**.

Expected transactional outcomes are not arbitrary exceptions or strings.

Examples include operation-specific stable results such as:
- `Committed`;
- `Unchanged`;
- `HeadChanged`;
- `ReservationMismatch` / `SessionInvalidated`;
- `WorldBusy`;
- `WorldUncertain`;
- `InvalidCandidate`;
- `Unauthorized`.

Rules:
- UI/runtime never parse human-readable backend prose to determine behavior;
- protocol/auth/authorization/validation/infrastructure failures use conventional HTTPS semantics and structured Problem-Details-style JSON;
- responses include a correlation/request identifier for support diagnostics;
- no stack traces, SQL/provider secrets, auth tokens, or signed URLs leak in errors;
- important mutations use explicit idempotency keys;
- same idempotency key + same logical request returns the original logical result;
- reusing an idempotency key for different logical input is rejected;
- ambiguous network outcomes are resolved by idempotent retry or authoritative status lookup, never assumption;
- API authentication credential expiry does not itself invalidate/release a World session generation;
- retryability/client action is machine-readable.

## BE-D014: Security and privacy baseline

Status: **approved**.

First release uses standard strong commercial security without inventing custom cryptography.

Required baseline:
- HTTPS for all external control/transfer traffic; TLS 1.3 preferred with securely configured TLS 1.2 compatibility where required;
- database/object storage/backups encrypted at rest;
- infrastructure/master secrets remain server-side;
- desktop receives only narrowly scoped short-lived transfer credentials;
- authorization on every World/revision/transfer/reservation/invitation/access operation;
- World/environment package bytes remain opaque to the backend;
- minimum identity data: verified SteamID64, optional presentation metadata, installation/session identifiers, membership/access/transaction records;
- no Steam passwords, unnecessary friend graph, gameplay telemetry, or save-content scanning by default;
- logs/audit exclude package contents, secrets, tokens, signed transfer URLs, and unnecessary personal data;
- sharing private by default;
- encrypted backups, documented retention, and tested restore;
- data access/export/deletion workflows distinguish member departure, account deletion, and shared-World deletion;
- account/access deletion never silently corrupts unresolved World responsibility;
- rate/abuse limits never override BE-D005 one-writer safety semantics;
- security-sensitive operations emit bounded audit events.

Client-held/end-to-end package encryption is not a first-release dependency; it requires a concrete product/threat requirement before accepting its key-management complexity.

## BE-D015: One authoritative EU backend deployment

Status: **approved**.

First release uses one authoritative Steward backend deployment in the EU.

Within the documented EU residency boundary remain:
- transactional metadata;
- primary object storage;
- identity/access metadata;
- reservation/session authority;
- canonical World coordination.

Encrypted disaster-recovery backups may use another suitable EU location.

Multiple service instances/availability zones may support the deployment, but first release does not use active-active multi-region World authority or cross-region reservation consensus.

Realtime gameplay never routes through Steward.

Immutable package delivery may later use regional replicas, caches, CDN, or peer-assisted transfer without moving canonical head/reservation authority.

Core principle:

> **Centralize authority. Distribute immutable bytes later only when evidence justifies it.**

# Backend product boundary

The backend answers only:

1. Which verified Steam identity is calling?
2. Which shared Worlds may that identity access?
3. What is the current valid state/environment head?
4. Where is an authorized immutable package?
5. Is a writable session Available, Active, or Uncertain?
6. May this caller acquire/resume/recover/reclaim/complete it?
7. Did candidate transfer and integrity verification succeed?
8. May expected-head + still-valid generation atomically advance the canonical head?

It does not understand:
- game save semantics;
- process names;
- game shutdown behavior;
- gameplay roles;
- public discovery;
- permanent host ownership;
- branches/Forks/merges.

# Minimal logical data model

BE-0 defined the following logical records. BE-2 has now implemented the identity/access/revision portion using provider-neutral PostgreSQL-compatible relational semantics; later milestones add transfer and distributed-session records without changing the product model.

## ExternalIdentity
- identity provider (Steam first);
- verified external ID / SteamID64;
- optional cached presentation metadata;
- authentication/session metadata outside Core domain objects.

## SharedWorldRecord
- World ID;
- adapter/game ID;
- display name;
- current state revision ID;
- current environment revision ID where used;
- AccessManagerIdentityId;
- creation/update metadata;
- durable coordination/recovery status only where required.

## WorldMember / AccessRecord
- World ID;
- Steam identity;
- pending/active/revocation-pending state;
- invitation/acceptance/revocation metadata.

Membership is flat.

## StateRevisionRecord
- immutable revision/package ID;
- World ID;
- adapter ID;
- package object reference;
- content hash;
- byte size;
- creation/publication metadata;
- expected previous/current head information where useful for diagnostics/CAS.

## EnvironmentRevisionRecord
- immutable revision/package or manifest reference;
- World ID;
- adapter ID;
- content/integrity metadata;
- creation/publication metadata.

## SessionReservationRecord
- World ID;
- unique session ID/generation;
- starting state revision;
- holder identity;
- installation/device ID;
- local/hosted mode where useful;
- acquired timestamp;
- last valid server-observed heartbeat;
- Available/Active/Uncertain/recovery representation;
- invalidation/reclaim metadata.

## TransferRecord

Only where durable resumable transfer tracking requires it:
- transfer ID;
- immutable target package;
- expected size/hash;
- provider upload/session reference or completed parts;
- expiry;
- finalization state.

## IdempotencyRecord

Only where required for durable mutation retry semantics:
- caller/operation scope;
- idempotency key;
- logical input digest;
- completed result;
- bounded retention/expiry.

# Required transaction contracts

## Shared start

```text
authenticate
-> authorize active membership
-> verify expected current head
-> require reservation Available
-> atomically create unique Active generation
```

## Heartbeat

```text
still-valid authenticated generation
-> backend records server-observed heartbeat time
```

## Candidate publication

```text
authorize bounded upload
-> transfer immutable bytes
-> verify actual size/hash
-> publish immutable candidate metadata
```

Partial/unverified bytes are never canonical.

## Commit

```text
candidate valid
AND current head == expected starting head
AND reservation generation == caller's still-valid generation
-> atomically advance state/environment head as required
-> record commit result
-> resolve reservation
```

Every failure leaves the prior canonical head authoritative.

## Reclaim

```text
reservation Uncertain beyond required grace
AND caller active member
AND expected old generation still current
AND authority/head recovery preconditions valid
-> atomically invalidate old generation
-> resolve from last committed safe state
-> make future acquisition possible
```

## Continue from last safe state

```text
verify current canonical head
-> invalidate/resolve outstanding generation as required
-> mark unresolved candidate abandoned as canonical work
-> preserve candidate under retention policy
-> return World to safe committed availability
```

# Reliability and operations requirements

First commercial backend requires:
- health/dependency checks;
- structured logs without save contents/secrets;
- metrics for transfer failure, reservation uncertainty/reclaim, commit conflict, storage integrity, and cost;
- correlation IDs across handoff phases;
- database backup/restore testing;
- documented object durability assumptions;
- idempotent retry handling;
- bounded retries/timeouts;
- deployment rollback;
- schema migration policy;
- development/staging/production separation;
- storage/egress/cost monitoring and technical hard limits where possible;
- incident response and key rotation procedures.

It does not require corporation-scale distributed infrastructure.

# Backend milestones after planning unlock

## BE-1: Provider-free deterministic contract simulation — COMPLETE AND GREEN

Completed:
- in-memory transactional records;
- deterministic clock/failure injection;
- two independent clients/processes;
- flat World access checks;
- immutable candidate publication simulation;
- expected-head CAS;
- acquire/heartbeat/Uncertain/reconnect/reclaim;
- generation invalidation;
- idempotency;
- candidate preservation/last-safe recovery;
- failure matrix.

No Steam integration, HTTP hosting, cloud SDK, production credential, or provider deployment was introduced in BE-1.

Evidence: `E1_STATUS.md`.

## BE-2: Authentication and World metadata service — COMPLETE AND GREEN

Completed:
- verified Steam authentication;
- Steward short-lived access + installation-bound rotating refresh sessions;
- accessible Worlds;
- membership/Access Manager;
- World-access invitations;
- acceptance/decline/revocation/pending revocation/leave/transfer;
- state/environment metadata;
- authorization tests;
- provider-neutral PostgreSQL persistence;
- live PostgreSQL integration tests.

Evidence: `BE2_STATUS.md`; latest full BE-2 persistence CI run `29866444653`.

## BE-3: Immutable object transfer — ACTIVE

Implement:
- HTTPS/JSON authorization control plane;
- bounded/resumable upload;
- size/hash verification;
- immutable publication;
- resumable authorized download;
- direct desktop/object-store byte transfer;
- local cache/materialization contract;
- orphan/partial cleanup;
- retention policy enforcement.

Provider selection occurs before production BE-3 deployment, informed by measured evidence. The provider-neutral object/transfer contract is fixed first so provider capabilities cannot redefine World authority.

## BE-4: Distributed reservation and commit

- acquire;
- heartbeat;
- Active -> Uncertain;
- reconnect;
- deliberate reclaim;
- expected-head commit;
- generation invalidation;
- idempotent mutation behavior;
- race tests proving no competing writer;
- late invalidated writer rejection.

## BE-5: Two-device proof

```text
PC A commits N+1
-> PC B downloads/verifies N+1
-> PC B commits N+2
-> PC A downloads/verifies N+2
```

Also prove:
- competing writer rejection;
- interrupted transfer resume;
- outage during active session;
- Waiting to sync completion;
- deliberate reclaim;
- late old-generation rejection;
- last-safe recovery.

## BE-6: Commercial hardening

- security review;
- backup/restore proof;
- rate/size limits;
- observability;
- deployment/rollback;
- cost limits;
- privacy/delete/export flows;
- EU residency verification;
- load/long-transfer tests based on measured Factorio/Palworld packages.

## BE-7: Evidence-driven performance optimization

Only after correctness:
- deduplication;
- peer-assisted transfer;
- CDN/replicated immutable delivery;
- background prefetch;
- compression tuning;
- retention compaction/delta/chunk reuse where measurements justify it.

# BE-0 completion gate

Status: **complete and approved**.

BE-0 is complete because:
- BE-D001 through BE-D015 are approved and numbered canonically;
- backend shape/auth/access/API/package/reservation/offline/recovery contracts are deterministic enough for incremental implementation;
- package/retention/security/geography/provider assumptions are explicit;
- provider vendor choice is deliberately deferred until the milestone that requires deployment evidence;
- UI-visible states/actions match `CROSS_WORKSTREAM_CONTRACT.md`;
- runtime recovery semantics match AR-0;
- first two-device acceptance proof is specified;
- no backend feature depends on merging, branches, social role hierarchies, or permanent game-server execution.

The planning lock is lifted. Backend implementation is active under the numbered milestone sequence above; current work is **BE-3 immutable object transfer**.
