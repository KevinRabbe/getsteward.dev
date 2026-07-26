# Backend Roadmap

Status: **CURRENT BACKEND CONTRACT — CORE BE-1 THROUGH BE-5 IMPLEMENTATION IS COMPLETE; REAL PROVIDER/STEAM RELEASE ACCEPTANCE REMAINS EXTERNAL.**

## Purpose

Steward needs one small persistent backend for cross-user authority that Steam and the games do not provide themselves.

The backend exists to support:

- verified external identity and normal Steward sessions;
- shared World membership/access administration;
- immutable state/environment revision publication;
- private direct package transfer;
- one writable reservation generation per World;
- safe expected-head canonical advancement;
- uncertainty/reclaim and interrupted-handoff recovery;
- short-lived Host-presence evidence for Join;
- bounded retention, cleanup, security, backup, and operations.

It must not become a second Steam, social platform, permanent game-hosting fleet, public server browser, or generic save-version-control system.

## Current implementation

The provider-neutral backend architecture is already implemented:

```text
Windows Desktop
    |
    | HTTPS /api/v1 control plane
    v
Backend.Api
    |
    +-> PostgreSQL
    |     Steward sessions
    |     shared World/access metadata
    |     immutable revision metadata
    |     reservation generations
    |     expected-head canonical commit
    |     idempotency
    |     Host presence
    |     transfer/retention metadata
    |
    `-> private S3-compatible object storage
          opaque immutable package bytes

Windows Desktop <--------------------> object storage
              scoped direct transfer
```

Implemented evidence is preserved in the historical BE status documents:

- BE-1 provider-free contract simulation — `E1_STATUS.md`;
- BE-2 identity/access/metadata/PostgreSQL — `BE2_STATUS.md`;
- BE-3 immutable direct transfer — `BE3_S3_CHECKPOINT.md`;
- BE-4 reservation generation/canonical commit — `BE4_STATUS.md`;
- BE-5 deterministic PC A -> PC B -> PC A composition — `BE5_STATUS.md`;
- deterministic hardening/provider backup/large-transfer/endurance evidence — `E8_STATUS.md`.

Those milestone names are provenance, not the current task queue.

## Current external backend gates

The remaining backend work is evidence at real boundaries:

### Provider deployment

- create the disposable EU resources;
- deploy the exact qualified Backend.Api image;
- use real PostgreSQL and private S3-compatible storage;
- prove HTTPS/readiness/restart/logging/backup/restore/transfer behavior;
- measure real package/network/cost behavior.

The first disposable topology is defined in `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` and currently uses one Scaleway Instance + Caddy, managed PostgreSQL, and private S3-compatible Object Storage. It is not final vendor lock-in.

### Production Steam identity

Only V3-F introduces:

- real Steward AppID;
- real publisher Web API credential, backend-secret only;
- real Steam-installed clients;
- genuine `GetAuthTicketForWebApi` tickets;
- deployed backend verification;
- two independent Steam accounts/installations.

No development/private auth path may become a production fallback.

# Backend decisions

The original BE-D001 through BE-D015 decisions remain the durable first-release backend contract, updated below to current implementation wording.

## BE-D001 — Deliberately hybrid backend

Steward uses Steam for the platform functions Steam already owns and one small Steward backend for shared-World authority.

PostgreSQL/backend authority owns:

- shared World metadata;
- flat membership + Access Manager;
- current state/environment heads;
- reservation session/generation;
- expected-head commit;
- invitation/revocation/recovery coordination;
- idempotency and retention metadata.

Object storage owns opaque immutable bytes only.

First release does not require Redis, Kafka, Kubernetes, microservice fleets, or permanent game-server compute.

> **Use the platform for the platform. Steward owns only the missing shared-World transaction.**

## BE-D002 — Verified external identity bootstraps a Steward session

Production Steam flow:

```text
Steam-installed Desktop
-> SteamAPI initializes
-> actual AppID must match packaged expected AppID
-> GetAuthTicketForWebApi(expected identity)
-> Backend.Api verifies ticket using server-side publisher credential
-> verified SteamID64 becomes external identity
-> backend issues normal Steward access + rotating refresh credentials
```

Rules:

- client-supplied SteamID is never authentication;
- publisher/Web API credentials remain server-side;
- access credential default is approximately 15 minutes;
- refresh credential default is approximately 30 days;
- server persists only cryptographic credential hashes, not plaintext tokens;
- refresh rotates;
- sessions are installation-bound where required;
- Desktop normal Steward access/refresh credentials remain process-memory state;
- a new production Steam launch reauthenticates through Steam rather than persisting normal refresh tokens in ordinary device settings;
- identity/session expiry never silently releases World writer authority;
- local-only use remains independent where remote identity is irrelevant.

Private Friends Build authentication is a separate proof source, not a second account system:

```text
Windows-protected long-lived Friends bootstrap credential
-> POST /api/v1/auth/friends/session
-> backend verifies configured credential digest
-> verified friends-build external identity
-> same normal Steward session machinery
```

The long-lived Friends bootstrap secret is protected under the Windows user boundary. Rotating normal Steward access/refresh credentials still remain process-memory state.

> **External proof establishes who the caller is. Steward authority decides what that identity may do.**

## BE-D003 — Flat members plus one Access Manager

A shared World has flat accepted membership. Exactly one active member additionally holds Access Manager responsibility.

A normal active member may, subject to adapter/current-state capability:

- view/download the shared World;
- Start/Host/Join where the product action is available;
- acquire the one writable reservation when available;
- complete a valid handoff;
- leave when no unresolved responsibility would be abandoned.

Access Manager may additionally:

- create World-access invitations;
- revoke future membership;
- transfer access management atomically;
- stop sharing/delete only according to explicit deletion policy.

Access Manager has no gameplay priority, reservation priority, overwrite privilege, or arbitrary right to terminate a healthy writer.

Revoking an active writer becomes pending until unresolved writable responsibility resolves safely.

World-access invitations remain separate from Steam/game multiplayer invitations.

## BE-D004 — Versioned HTTPS/JSON control plane + direct package bytes

Implemented control prefix:

```text
/api/v1
```

Metadata/coordination use bounded HTTPS/JSON request/response operations.

Large package bytes normally move directly between authorized Desktop clients and private object storage using short-lived scoped authorization.

Rules:

- no large base64 packages in control JSON;
- API authenticates/authorizes before transfer authorization;
- object storage cannot publish or select a canonical revision;
- transactional outcomes are machine-readable;
- ambiguous mutations use durable idempotency where required;
- no persistent WebSocket/gRPC/custom protocol dependency is required for first release.

> **The API controls authority. Object storage moves bytes.**

## BE-D005 — Heartbeat, uncertainty, deliberate reclaim

A writable reservation is not a simple expiring lock.

Initial operational defaults remain approximately:

- heartbeat every 30 seconds;
- `Active -> Uncertain` after about 2 minutes without accepted heartbeat;
- deliberate another-member reclaim after about 15 minutes in Uncertain.

Safety semantics:

```text
Available
-> Active generation
-> Uncertain
```

From Uncertain:

```text
same still-valid generation reconnects
-> Active
```

or, after valid reclaim conditions:

```text
atomically invalidate old generation
-> resolve authority from last committed safe head
-> future acquisition may occur
```

There is no timeout-only `Uncertain -> Available` transition.

- server time is authoritative;
- Uncertain blocks competing writers;
- old invalidated generation cannot heartbeat/commit;
- Access Manager receives no special reclaim privilege;
- a late candidate is preserved as recovery evidence rather than force-promoted.

> **A timer may create uncertainty. A timer may never manufacture a second writer.**

## BE-D006 — Continue from last safe state resolves authority before abandonment

`Continue from last safe state` is an explicit recovery decision, not cleanup.

Before availability can return, Steward must resolve the outstanding reservation/generation and verify the canonical head.

The unresolved candidate:

- is never silently merged/promoted;
- remains preserved under retention policy;
- does not gain overwrite authority;
- may be explicitly abandoned as canonical work only after authority is resolved.

## BE-D007 — Provider-neutral contracts, evidence-driven provider selection

The implementation remains provider-neutral at product/Core boundaries:

- PostgreSQL-compatible transactional authority;
- private S3-compatible immutable object storage;
- scoped direct transfer;
- resumable large-object behavior;
- standard TLS/encryption support;
- EU deployment capability.

Unlike the original planning stage, these are no longer simulated-only contracts: PostgreSQL and S3-compatible implementations/integration tests exist.

Final production vendor selection is still evidence-driven. A disposable candidate does not become permanent architecture merely because it passes the first deployment.

## BE-D008 — Active-session connectivity loss

Before a new shared writable/Join decision, unverifiable backend identity/head/authority means **Connection required**.

After a valid writable session has already started, temporary backend loss does not prove gameplay stopped and does not release the World.

If gameplay ends while disconnected:

```text
adapter proves safe capture
-> candidate captured/validated locally
-> candidate persisted durably
-> Waiting to sync
```

Reconnect revalidates:

1. authentication;
2. reservation generation;
3. expected canonical state/environment heads.

Still-valid generation + unchanged head may complete upload/commit.

Invalidated generation or divergent head becomes Recovery needed with candidate preserved.

Retry exhaustion never silently abandons responsibility.

## BE-D009 — Candidate retention and cleanup

Core rule:

> **Uncommitted gameplay changes are never deleted merely because time passed, connectivity stayed unavailable, or retries failed.**

Current first-release policy remains approximately:

| Data | Retention behavior |
|---|---|
| unresolved local recovery/unsynchronized candidate | no automatic time-based deletion |
| explicitly abandoned local candidate | approximately 7-day disclosed grace |
| verified remote candidate that never committed | approximately 7 days, extended while legitimately referenced |
| abandoned/inactive partial transfer | approximately 24 hours |
| successful committed/unchanged disposable local candidate | cleanup-eligible after durable outcome |

Disk pressure becomes Action required rather than silent recovery-data deletion.

## BE-D010 — Canonical revision retention

Shared Worlds normally retain:

> **current canonical state revision + previous two successfully committed canonical state revisions**

Older state/environment dependencies remain pinned while active recovery/transactions require them.

This is recovery/durability history, not a branch/merge UI.

## BE-D011 — State and environment share one immutable transfer infrastructure

State and environment remain logically distinct revision types but can use the same immutable package transfer infrastructure.

```text
authorize
-> transfer opaque immutable bytes where a hosted package is required
-> verify expected size/hash
-> publish immutable revision metadata
```

Native/reproducible environment inputs may remain structured references/manifests rather than redundant uploaded bytes.

When one transaction changes compatible state + environment, canonical head advancement can update both references atomically.

Backend never interprets game-specific environment semantics.

## BE-D012 — Package/transfer limits

Provider-independent package safety ceiling remains:

> **20 GiB per immutable State or Steward-hosted Environment package**

It is a safety ceiling, not an advertised entitlement.

Current transfer design includes:

- expected size/hash declared before upload;
- exact verification before publication/use;
- resumable multipart upload;
- approximately 64 MiB default part target where appropriate;
- verified resumable download;
- bounded local disk/cache behavior;
- adapter-owned package/compression format;
- full immutable packages rather than generic delta sync in first release.

## BE-D013 — Deterministic API outcomes and idempotency

Expected backend state transitions use stable machine-readable results rather than prose parsing.

Authority mutations whose ambiguous outcome could duplicate a transition use durable idempotency keys. Current acquire/reclaim/commit paths bind the key to caller/operation/logical request and replay the original logical result or reject conflicting reuse.

A timeout is an unknown outcome, not permission to create a competing mutation/writer.

Error responses must not leak stack traces, provider/SQL secrets, session credentials, or transfer secrets.

## BE-D014 — Security/privacy baseline

First release uses standard platform cryptography/security rather than custom cryptography.

Required/current boundaries include:

- HTTPS for remote credential/control traffic;
- plaintext HTTP allowed only for explicit loopback development boundaries;
- private object storage;
- server-only infrastructure/publisher secrets;
- per-resource authorization;
- short-lived scoped transfer authorization;
- exact package integrity checks;
- bounded control JSON;
- bounded persisted metadata;
- local redacted diagnostics;
- minimum necessary external identity/access/transaction metadata;
- no Steam passwords, unnecessary friend graph, gameplay telemetry, or generic save-content scanning.

Client-held end-to-end package encryption remains out of scope without a concrete threat/product requirement that justifies key-management complexity.

## BE-D015 — One authoritative EU backend

First release uses one authoritative Steward backend authority in the EU.

Within that authority boundary remain:

- transactional metadata;
- primary private object storage;
- identity/access metadata;
- reservation/session generations;
- canonical World coordination.

Encrypted disaster-recovery copies may use another suitable EU location.

Multiple service instances/AZs may later support availability, but first release does not require active-active multi-region World authority or cross-region consensus.

Realtime gameplay never routes through Steward.

> **Centralize authority. Distribute immutable bytes only when measured need justifies it.**

# Current logical backend data

The implemented PostgreSQL composition contains durable stores for these logical categories:

- Steward authentication sessions;
- shared World/current-head/revision metadata;
- World membership/invitations/Access Manager;
- writable reservation generations and responsibility inspection;
- idempotent authority mutation results;
- explicit pre-launch reservation abandonment;
- short-lived Host presence;
- resumable package-transfer metadata;
- shared revision-retention/cleanup metadata.

Object storage contains package bytes, not relational authority.

The backend does not store game-process/session internals, gameplay roles, branches, or save semantics.

# Current transaction contracts

## Acquire

```text
authenticate
-> authorize active membership
-> compare expected state/environment head
-> require safe reservation availability
-> atomically create/adopt caller's valid Active generation
```

Competing races yield one safe authority result, never two writers.

## Heartbeat

```text
matching authenticated reservation generation
-> backend records server-observed heartbeat
```

A stale generation cannot extend authority.

## Candidate publication

```text
authorize bounded upload
-> direct immutable transfer
-> verify actual size/hash/object result
-> publish immutable revision metadata
```

Unverified bytes never become canonical.

## Commit

```text
verified candidate
AND current head == expected starting head
AND exact reservation session/generation/installation still valid
-> atomically advance compatible state/environment head
-> persist durable result/idempotency outcome
-> resolve authority according to commit contract
```

Every failure preserves the previous canonical head.

## Abandon before gameplay

When a writable reservation was acquired but gameplay never started and Core can prove that no gameplay candidate exists, Desktop may explicitly abandon that exact reservation identity rather than waiting for uncertainty/reclaim.

That path cannot be used as a generic post-launch recovery shortcut.

## Host presence

```text
exact active reservation generation
-> publish Starting/Ready connection evidence
-> refresh only while same authority remains valid
-> Join reads it read-only
-> clear before capture/commit completion
```

Host presence is non-authoritative and cannot create writable rights.

# Implemented API families

The current HTTP contract lives under `/api/v1` and includes:

- Steam auth/session refresh/revoke;
- optional private Friends Build session proof when explicitly enabled;
- shared World metadata/current revision;
- membership/invitations/access management;
- immutable revision metadata;
- package transfer authorization/progress/finalization/download authorization;
- writable reservation acquire/get/heartbeat/reclaim/commit/abandon;
- Host presence publish/read/clear.

See `BE_API_CONTRACT.md` for the current route map.

# Operations/hardening state

Deterministic implementation already includes or proves:

- PostgreSQL-native backup/restore in CI;
- S3-compatible resumable transfer including 256 MiB synthetic proof;
- accelerated 24-hour reservation-heartbeat proof;
- bounded metadata/control inputs and responses;
- package download write ceiling;
- bounded verified cache;
- transfer inactivity/part time boundaries;
- HTTPS/non-loopback transport rules;
- disabled automatic redirects on credential-bearing clients;
- redacted bounded local diagnostics;
- background cleanup/retention workers.

Still external:

- real production/provider backup/restore operations;
- actual EU residency/subprocessor review;
- real game package sizes/transfer timings/costs;
- real Steam production identity;
- real two-installation/game/network release acceptance.

# Performance rule

Correctness and empirical evidence come first.

Potential optimizations such as CDN, replication, peer assistance, chunk/delta reuse, compression tuning, or prefetch are not roadmap requirements. Add them only when measured behavior shows they remove a real bottleneck.

# Working rule

For backend work ask:

> **Is this missing shared-World authority that Steam/the game cannot already provide, or a concrete defect in the implemented authority/transfer/recovery boundary?**

If not, do not grow the backend.