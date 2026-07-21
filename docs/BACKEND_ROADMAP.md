# Backend Roadmap

## Purpose

This roadmap defines the smallest persistent backend required to make one shared World available across different Steam users, devices, and times without running the game server permanently.

The backend exists only to support:

- durable latest World state;
- immutable revision storage;
- minimal shared access;
- one writable session reservation;
- safe current-head advancement;
- recovery after interrupted handoff.

It must not become a second Steam, a social platform, a permanent game-hosting fleet, or a generic save-merging service.

## Planning status

Status: **planning locked — BE-0 in progress**.

No backend implementation, database schema, API endpoint, service scaffold, provider integration, or infrastructure deployment begins until the backend planning gate and the master planning gate are complete.

Allowed during the lock:

- requirements and contract documentation;
- provider and cost research;
- threat modeling;
- data-flow and failure analysis;
- read-only inspection of current storage and coordination implementations;
- disposable calculations that do not create product code or infrastructure.

## Approved BE-0 decisions

### BE-D001: Deliberately hybrid backend

Status: **approved**.

Steward uses Steam for identity and game-platform functionality, while a small Steward backend owns only the persistent cross-user authority that Steam does not provide.

First-release conceptual deployment:

```text
Steward desktop clients
        |
        | Steam authentication ticket
        v
small Steward API / coordination service
        |-- one transactional relational database
        `-- immutable object/blob storage
```

The relational database is authoritative for:

- World metadata;
- flat shared access records;
- current state/environment head pointers;
- active session reservation;
- session generation and starting revision;
- compare-and-swap commit state;
- durable recovery/coordination metadata.

Object storage contains opaque immutable World/environment packages. Object storage never decides which revision is current.

Rules:

- Steam remains the identity and game/platform layer rather than Steward inventing another account, friend, party, Workshop, launch, or game-server ecosystem.
- The Steward backend does not run Factorio, Palworld, or another game server.
- Package bytes normally transfer directly between desktop and object storage through short-lived authorized upload/download mechanisms rather than being proxied through the API.
- The transactional database owns one-writer reservation and current-head advancement; no separate reservation service is required for the first release.
- Redis, Kafka, message brokers, distributed caches, a microservice fleet, Kubernetes, and permanent game-server compute are not first-release requirements.
- Database and object-storage providers remain implementation/provider decisions; the product model stays provider-independent.

Core principle:

> **Use Steam for what Steam already owns. Steward owns only the missing shared-World transaction.**

### BE-D002: Steam authentication bootstraps a Steward session

Status: **approved**.

The Windows desktop does not create or require a separate Steward username/password account.

Authentication flow:

```text
Steward starts
-> obtain Steam Web API authentication ticket scoped to the Steward backend
-> send ticket to Steward backend
-> backend verifies ticket directly with Steam
-> verified SteamID64 becomes the external authenticated identity
-> backend creates a Steward session
-> desktop uses Steward session credentials for normal API calls
```

Rules:

- A client-supplied SteamID is never trusted as authentication by itself.
- Steam authentication tickets prove identity and bootstrap/re-authenticate the Steward session; they are not sent for every normal API operation.
- Steam publisher/Web API secrets remain server-side and never ship in the desktop client.
- Initial session planning uses a short-lived access credential of about **15 minutes** and a renewable installation-bound refresh session of about **30 days**.
- Those lifetimes are operational defaults and may later be tuned without changing the authentication model.
- The refresh credential is stored using Windows-protected credential storage rather than plaintext configuration.
- A random Steward installation/device id distinguishes installations for session revocation, reservation diagnostics, and recovery. It is not authentication and must not become invasive hardware fingerprinting.
- A Steam account identity change requires reauthentication. A session authenticated as Steam user A never silently continues as Steam user B.
- Existing active/recovery evidence is preserved across identity changes and is not reassigned to the newly signed-in Steam account.
- Authentication failure prevents new writable shared-World sessions but does not unnecessarily disable unrelated safe local-only behavior.
- Steward stores SteamID64 and may cache presentation metadata such as persona name/avatar, but stores no Steam password, email, payment data, or equivalent Steam credentials.
- **Sign out of Steward** revokes the current installation refresh session. Per-device/session revocation remains possible without creating a larger account-management product.
- Browser/OpenID authentication may later serve a web/account surface; the first-release Windows desktop uses native Steam-ticket authentication.

Core principle:

> **Steam proves who you are. Steward decides what that verified identity may do.**

### BE-D003: Flat members plus one Access Manager

Status: **approved**.

A first-release shared World has a flat set of authorized Steam members. Every member has the same World usage rights. Exactly one member additionally holds the administrative responsibility of **Access Manager**.

A normal member may:

- see and download the shared World;
- Start World;
- Host World;
- Join;
- acquire the one writable reservation when available;
- complete and commit a valid session;
- leave the shared World when no unresolved responsibility is being abandoned.

The Access Manager may additionally:

- invite/add another Steam identity through the approved invitation flow;
- revoke another member's future access;
- transfer Access Manager responsibility to another existing member;
- stop sharing/delete the shared Steward World when the later deletion policy permits it.

Access Manager status gives **no**:

- gameplay authority;
- priority reservation;
- special hosting right;
- right to terminate another healthy session merely because they manage access;
- right to overwrite or choose the canonical revision;
- merge, branch, Fork, or conflict-resolution privilege;
- control over another player's local files.

#### Sharing and invitation

Initial sharing follows the local-first UI rule:

```text
Only on this PC
-> Share World
-> choose Steam identities
-> upload and verify current World
-> sharer becomes Access Manager
-> invitations are created
-> invited user accepts
-> accepted identity becomes an active member
```

Pending invitations do not grant package download, reservation, or commit access before acceptance.

The first release does not need a role editor or permission matrix.

#### Revocation

Ordinary revocation removes authorization for future operations, but must not destroy an already-authorized active writable transaction.

```text
member holds active writable session
-> Access Manager revokes member
-> revocation becomes pending
-> current session may finish/recover safely
-> session resolves
-> revocation becomes effective
```

Exceptional emergency/security revocation may be designed separately only if a concrete threat requires it.

#### Leaving and transfer

A member cannot leave while they own an unresolved active World responsibility.

The Access Manager cannot leave while still being the only Access Manager. They must first either:

- atomically transfer Access Manager responsibility to another existing member; or
- stop sharing/delete the shared World according to the later deletion policy.

Transfer is an atomic administrative change so there is never an intentional period with two Access Managers or none.

Data-model principle:

```text
AuthorizedMember
+
AccessManagerIdentityId
```

not:

```text
Owner / Admin / Moderator / Host / Member role hierarchy
```

Core principle:

> **Membership controls who may use the World. Access Manager controls only who is a member.**

## Backend product boundary

The backend must answer only:

1. Which verified Steam identity is making the request?
2. Which shared Worlds may that identity access?
3. What is the latest valid environment/state head for a World?
4. Where is the immutable package?
5. Is a writable session reserved?
6. May this caller acquire or complete that reservation?
7. Did a candidate state store and verify successfully?
8. May the current head advance from the caller's expected starting revision?

The backend does not need to understand:

- game save semantics;
- process names;
- game shutdown behavior;
- gameplay roles;
- Discord parties;
- public discovery;
- permanent host ownership;
- branches, Forks, or merges.

## Approved backend architecture

```text
Steward desktop clients
        |
        v
small Steward API / coordination service
        |-- transactional relational database
        `-- immutable object/blob storage
```

The API authenticates and authorizes requests, issues transfer authorization, and performs coordination transactions. The database stores small authoritative records. Object storage stores large immutable packages.

Provider choice remains open until provider/cost planning. No provider may redefine the Core product model.

## Minimal logical data model

The exact schema remains an implementation decision. The required logical records are:

### ExternalIdentity

- provider, primarily Steam;
- stable external id / SteamID64;
- optional cached display metadata;
- authentication/session metadata outside Core domain objects.

### SharedWorldRecord

- World id;
- adapter/game id;
- display name;
- current environment revision id;
- current state revision id;
- AccessManagerIdentityId;
- creation/update metadata;
- durable lifecycle/recovery status only where coordination requires it.

### WorldMember / AccessRecord

- World id;
- authorized Steam identity;
- membership state: pending/active/revocation-pending where required;
- invitation/acceptance metadata where required;
- created/revoked timestamps.

Membership is flat. The access record does not encode gameplay roles.

### EnvironmentRevisionRecord

- immutable revision id;
- World id;
- adapter id;
- package/manifest reference as required;
- content/integrity metadata;
- creation metadata.

### StateRevisionRecord

- immutable revision id;
- World id;
- adapter id;
- expected previous/current head used for commit;
- package object reference;
- content hash;
- byte size;
- creation metadata;
- publication status.

A previous revision reference supports expected-head validation and diagnostics. It is not a branch graph.

### SessionReservationRecord

- World id;
- unique session id/generation;
- starting state revision;
- holder identity;
- device/installation id;
- local or hosted mode where useful;
- acquired/heartbeat timestamps;
- reservation state;
- recovery/expiry metadata.

### TransferRecord

Only when resumable upload/download requires durable tracking:

- transfer id;
- target immutable revision/package;
- expected byte size and hash;
- completed chunks/provider upload id;
- expiration;
- finalization state.

## Required backend contracts

### Authentication

The approved first-release authentication contract is BE-D002.

Required behavior:

- obtain Steam Web API authentication ticket in the Windows desktop;
- verify it server-side with Steam;
- derive authenticated SteamID64 only from successful verification;
- bootstrap short-lived Steward access credentials and a renewable installation-bound refresh session;
- protect refresh credentials using Windows credential protection;
- reauthenticate on Steam identity change;
- support sign-out and installation/session revocation;
- retain only minimal identity data.

### World access

The approved first-release access contract is BE-D003.

Required operations:

- list Worlds where the caller is an active member;
- retrieve one accessible World;
- create/register a shared World;
- create an invitation as Access Manager;
- accept/reject an invitation as the invited Steam identity;
- revoke a member's future access as Access Manager;
- atomically transfer Access Manager responsibility;
- leave a World when doing so abandons no unresolved responsibility;
- prevent unauthorized package download, reservation, transfer, and commit.

Ordinary member revocation is deferred until an already-authorized active writable transaction resolves safely.

### Immutable revision upload

```text
request candidate upload
-> receive resumable upload target/session
-> upload immutable bytes
-> verify size and content hash
-> publish immutable revision metadata
-> candidate becomes eligible for head commit
```

Partial or unverifiable uploads never become current.

### Revision download

Required properties:

- authorization before object access;
- resumable/range-capable download where state sizes justify it;
- content hash and size returned independently;
- client verification before restore;
- no mutable shared object that several users overwrite in place.

### Current-head compare-and-swap

```text
commit candidate revision
where current head == expected starting revision
and reservation == caller's active session generation
```

Results must distinguish at least:

- Committed;
- Unchanged;
- HeadChanged/stale caller;
- ReservationMismatch;
- InvalidCandidate;
- Unauthorized.

The previous valid head remains authoritative on every failure.

### Session reservation

Acquire requires:

- active authorized membership;
- expected current state revision;
- no safely active writer;
- unique session generation/token;
- durable starting state.

While active:

- holder heartbeats at a planned bounded interval;
- only that session generation may commit from its starting revision;
- other clients see active/uncertain state and cannot acquire a competing writer.

Complete requires:

- candidate stored and verified;
- expected-head commit result known;
- reservation transitioned safely;
- World released only after successful commit or deliberate last-safe recovery action.

### Reservation uncertainty and crash recovery

A missed heartbeat must not immediately prove the game/server session ended.

```text
Available
-> Active
-> Uncertain
-> RecoveryNeeded or deliberately Reclaimed
-> Available
```

Safety rules:

- expiry moves a session to Uncertain, not directly Available;
- no second writer starts while uncertainty is unresolved;
- the original device may reconnect and resume/finish while its session generation remains valid;
- after an explicit grace/recovery decision, an authorized member may start from the last committed state;
- reclaim invalidates the old session generation;
- a late old device cannot commit after generation invalidation or head change.

Heartbeat interval, uncertainty grace period, and reclaim authority remain BE-0 decisions.

## Offline behavior

Current safe first-release default:

- local-only Worlds may continue without backend access using local coordination;
- shared Worlds may be browsed from cache while offline;
- a new writable shared session does not start unless backend identity, current head, and reservation can be verified;
- an active session that loses connectivity may continue running locally;
- captured updated state is preserved locally until upload/commit succeeds or recovery resolves;
- unresolved shared state never becomes falsely Ready.

Exact active-session outage/reconnect behavior remains to be completed in BE-0 and aligned with UI-D004.

## Storage and transfer requirements

The backend treats World packages as opaque bytes.

BE-0 must still decide:

- maximum first-release package size;
- multipart/chunk size;
- compression responsibility;
- upload/download resume strategy;
- local cache layout and bounds;
- deduplication now versus later;
- previous-revision retention;
- orphan cleanup;
- provider egress/storage cost limits;
- integrity hash algorithm and metadata location;
- object encryption at rest and TLS in transit;
- malware/archive boundary without interpreting game-save contents.

Correctness precedes delta transfer or peer-to-peer acceleration.

## Security requirements

The first commercial backend requires:

- verified Steam authentication;
- authorization on every World, revision, transfer, reservation, invitation, and access-management operation;
- no use of unguessable IDs as authorization by themselves;
- least-privilege object-storage access;
- short-lived signed transfer authorization or equivalent;
- rate and payload bounds;
- replay protection for reservation/commit tokens;
- secret management;
- audit events for access, invitation, revocation, reservation, commit, and administrative transfer;
- private-by-default sharing;
- data deletion/export obligations;
- backup/disaster-recovery policy;
- incident response and key rotation.

## Reliability and operations requirements

The first commercial backend needs:

- health/dependency checks;
- structured logs without save contents or secrets;
- metrics for transfer failure, reservation uncertainty, commit conflict, and storage integrity;
- correlation ids across client handoff phases;
- database backup/restore testing;
- documented object durability assumptions;
- idempotency for safe retryable operations;
- bounded retries and timeouts;
- deployment rollback;
- schema migration policy;
- development/staging/production separation;
- cost monitoring and hard limits where possible.

It does not need corporation-scale microservices.

## Backend roadmap milestones

### BE-0: Planning contract

Deliverables:

- chosen backend architecture;
- authentication flow;
- minimal access/invitation policy;
- logical data model;
- API style/transport;
- upload/download protocol;
- expected-head commit contract;
- reservation state machine;
- offline/crash behavior;
- security/threat model;
- provider/cost assumptions;
- first-release package/retention limits;
- API error/result contract;
- cross-workstream contract with UI/runtime.

No backend code starts before BE-0 and the master planning gate are complete.

### BE-1: Local contract simulation

After planning unlock:

- implement remote contracts against an in-memory/local deterministic test backend;
- simulate two independent clients/processes;
- prove expected-head and reservation invariants;
- test idempotent retries and stale session generations;
- avoid provider-specific behavior in Core.

### BE-2: Authentication and World metadata service

- verified Steam identity;
- accessible-World listing;
- flat membership + Access Manager policy;
- invitation acceptance/rejection;
- access revocation/transfer;
- environment/state metadata retrieval;
- authorization tests.

### BE-3: Immutable object transfer

- resumable upload;
- immutable publication;
- size/hash verification;
- authorized resumable download;
- local cache integration contract;
- orphan candidate handling.

### BE-4: Distributed reservation and commit

- acquire/heartbeat/uncertain/reclaim/complete;
- expected-head compare-and-swap;
- session generation invalidation;
- no competing writer under race tests;
- safe late-client rejection.

### BE-5: Two-device handoff backend proof

- PC A uploads/commits N+1;
- PC B downloads/verifies N+1;
- PC B acquires and commits N+2;
- PC A receives N+2;
- competing writer rejected;
- interrupted transfer resumed;
- uncertain session recovered from last committed state.

### BE-6: Commercial hardening

- security review;
- backup/restore test;
- rate and size limits;
- observability;
- deployment/rollback;
- cost limits;
- privacy/delete/export flows;
- load tests based on measured Factorio and Palworld package sizes.

### BE-7: Performance optimization

Only after correctness:

- deduplication;
- direct peer-to-peer acceleration;
- background prefetch;
- transfer-compression tuning;
- retention compaction.

## Decisions still required before BE-0 completes

- API style and transport.
- Metadata database and object-storage provider.
- Reservation heartbeat interval, uncertainty grace period, and reclaim authority.
- First-release package size and retention limits.
- Whether environment packages share the same transfer model as state packages.
- Exact offline behavior during an already active shared session.
- Candidate retention after failed or stale commit.
- Required encryption, regional storage, privacy, and deletion guarantees.
- Initial commercial pricing/cost assumptions that constrain storage and transfer.
- Whether one backend deployment is acceptable for the first release or regional separation is required.

## Backend planning completion gate

Backend planning is complete only when:

- every remaining decision is resolved or explicitly deferred without blocking the first implementation slice;
- API and state-machine contracts are precise enough to test without provider guessing;
- UI-visible states/actions are supported;
- runtime recovery semantics match reservation and commit behavior;
- threat model and provider/cost constraints are documented;
- the first two-device backend acceptance test is specified;
- no backend feature depends on branches, merging, social role hierarchies, or permanent game-server execution;
- the master roadmap lifts the planning lock.
