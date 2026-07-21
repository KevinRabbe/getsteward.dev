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

Allowed work during the lock:

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

The relational database is the authority for:

- World metadata;
- flat shared access records;
- current state/environment head pointers;
- active session reservation;
- session generation and starting revision;
- compare-and-swap commit state;
- durable recovery/coordination metadata.

Object storage contains opaque immutable World/environment packages. Object storage does not decide which revision is current.

Rules:

- Steam remains the identity and game/platform layer rather than Steward inventing another account, friend, party, Workshop, launch, or game-server ecosystem.
- The Steward backend does not run Factorio, Palworld, or another game server.
- World package bytes should normally transfer directly between the desktop and object storage through short-lived authorized upload/download mechanisms rather than being proxied through the API service.
- The transactional database owns one-writer reservation and current-head advancement; no separate reservation service is required for the first release.
- The first release does not require Redis, Kafka, a message broker, distributed cache, microservice fleet, Kubernetes, or permanent game-server compute.
- Database and object-storage providers remain implementation/provider decisions; the product model must remain provider-independent.

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
- Steam authentication tickets are used to prove identity and bootstrap/re-authenticate the Steward session; they are not sent for every normal API operation.
- Steam publisher/Web API secrets remain server-side and are never embedded in the desktop client.
- The initial session design uses a short-lived access credential, planned at **15 minutes**, plus a renewable installation-bound refresh session, planned at **30 days**.
- Refresh-session lifetime values are operational defaults and may be tuned later without changing the authentication model.
- The refresh credential is stored using Windows-protected credential storage rather than plaintext application configuration.
- A random Steward installation/device id distinguishes installations for session revocation, reservation diagnostics, and recovery. It is not itself authentication and must not become invasive hardware fingerprinting.
- A Steam account identity change requires reauthentication. A session authenticated as Steam user A never silently continues as Steam user B.
- Existing active/recovery evidence is preserved across identity-change handling; it is not reassigned to the newly signed-in Steam account.
- Authentication failure prevents new writable shared-World sessions but does not unnecessarily disable unrelated safe local-only behavior.
- Steward stores the stable SteamID64 and may cache presentation metadata such as persona name/avatar, but it stores no Steam password, email, payment data, or equivalent Steam credentials.
- **Sign out of Steward** revokes the current installation refresh session. Per-device/session revocation remains possible without creating a larger account-management product.
- Browser/OpenID authentication may later serve a web/account surface, but the first-release Windows desktop uses native Steam-ticket authentication.

Core principle:

> **Steam proves who you are. Steward decides what that verified identity may do.**

## Backend product boundary

The backend must answer only these questions:

1. Which Steam identity is making the request?
2. Which shared Worlds may that identity access?
3. What is the latest valid environment/state head for a World?
4. Where is the immutable state package?
5. Is a writable session currently reserved?
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

The approved first-release logical shape is:

```text
Steward desktop clients
        |
        v
small Steward API / coordination service
        |-- transactional relational database
        `-- immutable object/blob storage
```

The API authenticates and authorizes requests, issues transfer authorization, and performs coordination transactions. The database stores the small authoritative records. Object storage stores the large immutable packages.

The provider choice remains open until the provider/cost planning decision. No provider is allowed to redefine the Core product model.

## Minimal backend data model

The exact schema remains an implementation decision, but these logical records are required.

### ExternalIdentity

- provider, primarily Steam;
- stable external id;
- optional display name/cache;
- authentication metadata outside Core domain objects.

### SharedWorldRecord

- World id;
- game adapter id;
- display name;
- current environment revision id;
- current state revision id;
- flat authorized identity set or equivalent minimal access record;
- creation/update metadata;
- lifecycle/recovery status only where durable coordination requires it.

This record does not encode gameplay ownership or permanent host ownership.

### EnvironmentRevisionRecord

- immutable revision id;
- World id;
- adapter id;
- manifest metadata/package reference as required;
- content/integrity metadata;
- creation metadata.

### StateRevisionRecord

- immutable revision id;
- World id;
- adapter id;
- expected previous/current head used for commit;
- package object key/reference;
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
- device id or installation id;
- local or hosted mode where useful;
- acquired/heartbeat timestamps;
- reservation state;
- recovery/expiry metadata.

### TransferRecord

Only when resumable upload/download requires durable tracking:

- transfer id;
- target immutable revision/package;
- expected byte size and hash;
- completed chunks or provider upload id;
- expiration;
- finalization state.

## Required backend contracts

### Authentication

The approved first-release authentication contract is BE-D002.

Required behavior:

- obtain a Steam Web API authentication ticket in the Windows desktop;
- verify that ticket server-side with Steam;
- derive the authenticated SteamID64 only from successful verification;
- bootstrap a short-lived Steward access credential and renewable installation-bound refresh session;
- protect the refresh credential with Windows credential protection;
- reauthenticate when Steam identity changes;
- support sign-out and installation/session revocation;
- retain only minimal identity data.

The backend never authenticates a request from a client-supplied SteamID alone and never exposes Steam publisher/Web API secrets to the client.

### World access

The first release needs the smallest safe shared-access model.

Required operations:

- list Worlds accessible to the caller;
- retrieve one accessible World;
- create/register a shared World;
- add access through the chosen invitation mechanism;
- revoke access where required for commercial safety;
- leave a World;
- prevent unauthorized state download or commit.

Planning must resolve who may invite or revoke without creating a complex ownership hierarchy.

### Immutable revision upload

Required flow:

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

Required commit operation:

```text
commit candidate revision
where current head == expected starting revision
and reservation == caller's active session
```

Results must distinguish:

- Committed;
- Unchanged;
- HeadChanged/stale caller;
- ReservationMismatch;
- InvalidCandidate;
- Unauthorized.

The previous valid head remains authoritative on every failure.

### Session reservation

Acquire requires:

- authorized World access;
- expected current state revision;
- no safely active writer;
- unique session generation/token;
- durable starting state.

While active:

- the holder heartbeats at a planned bounded interval;
- only that session may commit from its starting revision;
- other clients see active/uncertain state and cannot acquire a competing writer.

Complete requires:

- candidate stored and verified;
- expected-head commit result known;
- reservation transitioned safely;
- World released only after successful commit or deliberate last-safe recovery action.

### Reservation uncertainty and crash recovery

A missed heartbeat must not immediately prove the game/server session ended.

Planned state model:

```text
Available
-> Active
-> Uncertain
-> RecoveryNeeded or deliberately Reclaimed
-> Available
```

Initial safety rule:

- expiry moves a session to Uncertain, not directly Available;
- no second writer starts while uncertainty is unresolved;
- the original device may reconnect and resume/finish when its session generation remains valid;
- after an explicit grace/recovery decision, another authorized user may start from the last committed state;
- reclaim invalidates the old session generation;
- a late old device cannot commit because reservation generation and/or expected head no longer match.

The exact grace duration and reclaim authority must be decided before code.

## Offline behavior

Current safe default for first release:

- local-only Worlds may continue without backend access using local coordination;
- shared Worlds may be browsed from cache while offline;
- a new writable shared session does not start unless the backend can verify current head and acquire the reservation;
- an active session that loses connectivity may continue running locally, but the reservation becomes uncertain remotely and commit retries remain bounded;
- the desktop preserves the captured candidate locally until upload/commit succeeds or recovery is resolved.

Planning must confirm this rule before implementation.

## Storage and transfer requirements

The backend must plan for materially different World sizes and file shapes while treating packages as opaque bytes.

Required decisions:

- maximum first-release package size;
- multipart/chunk size;
- compression responsibility: adapter package versus transport;
- upload/download resume strategy;
- local cache layout and limits;
- deduplication now versus later;
- retention of previous revisions;
- orphan cleanup policy;
- provider egress and storage cost limits;
- integrity hash algorithm and metadata location;
- object encryption at rest and TLS in transit;
- malware/archive handling boundary without interpreting arbitrary save contents.

Correctness precedes delta transfer or peer-to-peer acceleration.

## Security requirements

The backend handles private World state and identity.

Planning must include:

- Steam authentication verification;
- authorization on every World, revision, transfer, and reservation operation;
- unguessable identifiers not used as authorization;
- least-privilege object-storage access;
- signed URL lifetime or equivalent transfer authorization;
- rate limits and abuse bounds;
- bounded payload size and metadata validation;
- replay protection for reservation/commit tokens;
- secret management;
- audit events for access, reservation, commit, and revocation;
- private-by-default sharing;
- data deletion/export obligations;
- backup and disaster-recovery policy;
- incident response and key rotation.

## Reliability and operations requirements

The first commercial backend needs:

- health and dependency checks;
- structured logs without save contents or secrets;
- metrics for transfer failures, reservation uncertainty, commit conflicts, and storage integrity;
- trace/correlation ids across client handoff phases;
- database backup and restore test;
- object durability assumptions documented;
- idempotency for safe retryable operations;
- bounded retries and timeouts;
- deployment rollback plan;
- schema migration policy;
- development/staging/production separation;
- cost monitoring and hard limits where possible.

It does not need corporation-scale microservices.

## Backend roadmap milestones

### BE-0: Planning contract

Deliverables:

- chosen backend versus Steam-only architecture;
- authentication flow;
- minimal access/invitation policy;
- logical data model;
- upload/download protocol;
- expected-head commit contract;
- reservation state machine;
- offline and crash behavior;
- security/threat model;
- provider/cost assumptions;
- first-release package and retention limits;
- API error/result contract;
- cross-workstream contract with UI and runtime.

No backend code starts before BE-0 and the master planning gate are complete.

### BE-1: Local contract simulation

After planning unlock:

- implement the remote contracts against an in-memory/local deterministic test backend;
- simulate two independent clients/processes;
- prove expected-head and reservation invariants;
- test idempotent retries and stale session generations;
- avoid provider-specific behavior in Core.

### BE-2: Authentication and World metadata service

- verified Steam identity;
- accessible-World listing;
- minimal sharing/invitation/access policy;
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
- deployment and rollback;
- cost limits;
- privacy/delete/export flows;
- load tests based on measured Factorio and Palworld package sizes.

### BE-7: Performance optimization

Only after correctness:

- deduplication;
- direct peer-to-peer acceleration;
- background prefetch;
- transfer compression tuning;
- retention compaction.

## Decisions still required before BE-0 completes

- API style and transport.
- Metadata database and object-storage provider.
- Flat access policy: who can invite, revoke, or transfer administrative control without creating gameplay ownership.
- Reservation heartbeat interval, uncertainty grace period, and reclaim authority.
- First-release package size and retention limits.
- Whether environment packages share the same transfer model as state packages.
- Exact offline behavior during an already active shared session.
- Candidate retention after failed or stale commit.
- Required encryption, regional storage, privacy, and deletion guarantees.
- Initial commercial pricing/cost assumptions that constrain storage and transfer.
- Whether a single backend deployment is acceptable for the first release or regional separation is required.

## Backend planning completion gate

Backend planning is complete only when:

- every decision above is resolved or explicitly deferred without blocking the first implementation slice;
- the API and state-machine contracts are precise enough to test without provider guessing;
- UI-visible states and actions are supported;
- runtime recovery semantics match reservation and commit behavior;
- threat model and provider/cost constraints are documented;
- the first two-device backend acceptance test is specified;
- no backend feature depends on branches, merging, social roles, or permanent game-server execution;
- the master roadmap lifts the planning lock.