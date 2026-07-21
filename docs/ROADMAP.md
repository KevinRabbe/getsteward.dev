# Master Roadmap

## Product target

> **One shared World. Different Steam players. Different times. No always-on game server.**

The master roadmap coordinates three implementation workstreams:

1. [UI and UX Roadmap](UI_ROADMAP.md)
2. [Backend Roadmap](BACKEND_ROADMAP.md)
3. [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md)

The workstreams may contain different technical milestones, but they deliver one product. None may create its own product model.

## Current mode: planning lock

Status: **ACTIVE — no production code until planning is complete.**

The repository has enough validated implementation to plan against real behavior. The next risk is not lack of code; it is coding before the UI, backend, runtime, recovery, and cross-device contracts agree.

Until this lock is explicitly lifted, do not add or modify:

- production UI behavior or layout;
- backend/service code or infrastructure;
- storage or coordination schemas;
- adapter contracts or lifecycle code;
- Factorio or Palworld implementation behavior;
- background/tray runtime code;
- new game adapters;
- speculative prototypes intended to become product code.

Allowed during the planning lock:

- documentation and diagrams;
- decision research and cost analysis;
- threat modeling;
- read-only repository inspection;
- real-game fact verification using copied/disposable data only when necessary to settle a planning decision;
- removal of contradictory documentation;
- acceptance-test specification without implementation.

The lock is lifted only by an explicit product decision after all planning exit criteria are satisfied.

## Why three workstreams

### UI and UX

Defines what the user sees and does:

- Games Library;
- game-specific World workspace;
- import;
- Start World / Host World / Join;
- lifecycle progress;
- background/tray behavior;
- blocked and recovery states.

It must not invent backend or adapter behavior to make a screen convenient.

### Backend

Defines persistent cross-device truth:

- verified Steam identity;
- minimal World access;
- immutable state storage and transfer;
- current-head commit;
- distributed one-writer reservation;
- uncertainty/reclaim behavior;
- security, operations, and cost boundaries.

It must not understand game saves or run game servers.

### Adapter and Background Runtime

Defines the complete local session transaction:

- resolve current World;
- reserve it;
- prepare/restore through the adapter;
- launch and observe the real session;
- stop safely;
- capture and validate;
- upload/store/verify/commit;
- remain alive in the background;
- preserve recovery evidence.

It must not invent social, ownership, or merge semantics.

## Workstream dependency model

The roadmaps are separate for clarity, not independence.

```text
UI requirements
    <-> generic runtime states/actions
    <-> backend reservation/commit states
    <-> adapter capabilities and limitations
```

Rules:

- UI may request only actions supported by the runtime/backend contracts.
- Runtime may expose only states the UI can communicate clearly.
- Backend uncertainty and recovery semantics must match runtime crash/connectivity behavior.
- Adapter capability declarations determine whether Start, Host, Join, Stop and Save, Repair, or manual fallback is available.
- No workstream may add a concept rejected by the product boundary.

## Planning phase P0

### P0.1: Roadmap structure

Status: **complete**.

Deliverables:

- master roadmap;
- UI and UX roadmap;
- backend roadmap;
- adapter/background runtime roadmap;
- planning lock and drift-control rules.

### P0.2: UI contract decisions

Resolve every first-release decision listed in `UI_ROADMAP.md`, including:

- navigation and screen structure;
- Start versus Host action model;
- state/action matrix;
- shared/offline behavior presentation;
- tray/background behavior;
- recovery actions and terminology;
- accessibility and commercial polish boundary.

Output: approved UI-0 contract.

### P0.3: Backend contract decisions

Resolve every first-release decision listed in `BACKEND_ROADMAP.md`, including:

- hosted service versus Steam-only/hybrid design;
- Steam authentication;
- minimal access/invitation/revocation policy;
- logical data model;
- package transfer and limits;
- current-head compare-and-swap;
- reservation heartbeat, uncertainty, reclaim, and generation invalidation;
- offline behavior;
- security, privacy, operations, and cost assumptions.

Output: approved BE-0 contract.

### P0.4: Adapter/runtime contract decisions

Resolve every first-release decision listed in `ADAPTER_RUNTIME_ROADMAP.md`, including:

- desktop/tray process model;
- one-active-session-per-device limit;
- lifecycle state machine;
- adapter capability model;
- session evidence and safe capture contracts;
- close/shutdown/update behavior;
- connectivity-loss and recovery behavior;
- Factorio and Palworld capability/limitation matrices;
- release acceptance tests.

Output: approved AR-0 contract.

### P0.5: Cross-workstream contract review

Create one agreed contract matrix containing:

- each user-visible state;
- its backend meaning;
- its runtime meaning;
- allowed user actions;
- adapter capability requirements;
- failure/recovery transition;
- authoritative source of truth.

No state may have contradictory meanings across documents.

### P0.6: First-release acceptance plan

Specify the evidence required before release:

- PC A -> PC B -> PC A handoff;
- competing writer rejection;
- interrupted upload resume;
- backend outage during an active session;
- stale reservation reclaim;
- late old-session commit rejection;
- application crash/restart recovery;
- Factorio local/host/capture/replay;
- Palworld dedicated host/capture/restore;
- safe failure when environment or player identity limitations block continuation;
- UI flow from import to Ready to Running to Saving to Ready;
- security and backup/restore checks.

### P0.7: Explicit planning sign-off

The planning lock is lifted only when:

- UI-0, BE-0, and AR-0 are complete;
- the cross-workstream matrix is complete;
- the first-release acceptance plan is complete;
- unresolved questions are either answered or explicitly deferred without affecting the first implementation slice;
- the release boundary remains within the Non-Negotiable Rules and Product Boundary;
- the user explicitly approves moving from planning to implementation.

## Drift-control rules

### One active planning question

Resolve one decision group at a time. Do not jump between UI styling, infrastructure providers, adapter edge cases, and pricing without closing the current question or recording why it is blocked.

### Roadmap-linked implementation only

After the lock is lifted, every product code change must map to:

- one workstream;
- one numbered milestone;
- one acceptance criterion.

A change without that mapping does not begin.

### Parking lot for new ideas

A new idea that does not unblock the active milestone goes into a parking lot for later review. It does not enter implementation merely because it sounds useful.

### Finish before expanding

Do not begin a later milestone to avoid a difficult unfinished requirement in the current milestone.

### Boundary change requires documentation first

A deliberate product-boundary change must update, in order:

1. Non-Negotiable Rules when required;
2. Product Boundary;
3. Design Decisions;
4. affected roadmap contracts;
5. implementation.

### Evidence over assumption

A claim about Factorio, Palworld, Steam, storage limits, process behavior, or failure recovery remains conditional until documentation or a controlled test proves it.

## Validated foundation

### Generic lifecycle

The existing implementation already provides valuable foundations:

```text
discover
-> import
-> inspect environment
-> prepare workspace
-> restore state
-> launch local or hosted session
-> observe session end through adapter
-> capture updated state
-> store immutable revision
-> advance current World head last
-> preserve recovery state on failure
```

### State safety

Implemented/tested foundations include:

- immutable environment and state revisions;
- one active writable session boundary;
- expected-head protection;
- unchanged-state detection;
- canonical head advancement after durable storage;
- workspace recovery records;
- explicit cleanup ownership;
- typed failure boundaries;
- persisted schema envelopes and migrations.

### Factorio

Real-machine evidence includes:

- Steam/non-default-library discovery;
- save import with source preservation;
- isolated environment/write-data handling;
- local and hosted launch paths;
- Steam process handoff observation;
- state capture, commit, and replay.

### Palworld

Real-machine evidence includes:

- client and dedicated-server discovery;
- local/dedicated World discovery;
- unchanged World migration into the server layout;
- dedicated server launch;
- portable capture excluding backup noise;
- staging/rollback restore;
- restored-byte verification;
- launch from restored canonical state;
- canonical state commit.

### Desktop

The WPF desktop proves:

- a unified Factorio/Palworld adapter registry;
- game-first browsing and import concepts;
- artwork resolution;
- direct lifecycle actions.

Its composition remains transitional and should not be expanded until UI-0 is approved.

## Execution plan after planning unlock

### E1: Contract and conformance foundation

Parallel work may begin only after P0 sign-off:

- backend local deterministic simulation from BE-1;
- runtime lifecycle/conformance tests from AR-1;
- UI shell work from UI-1 using the frozen state/action contract.

No provider-specific or game-specific shortcut may bypass the contracts.

### E2: Shared state foundation

- verified Steam identity;
- World metadata/access;
- immutable state upload/download;
- current-head commit;
- client cache/verification integration.

### E3: Distributed one-writer coordination

- acquire/heartbeat/uncertain/reclaim/complete;
- session generation invalidation;
- stale/late writer rejection;
- UI active-elsewhere and recovery states.

### E4: Background runtime integration

- desktop/tray lifetime;
- shared reservation and transfer integration;
- safe session start/end;
- candidate preservation;
- startup recovery scan;
- safe application close/update behavior.

### E5: Development two-device proof

Use one real game and controlled test accounts/devices:

```text
PC A commits N+1
-> PC B restores and commits N+2
-> PC A restores N+2
```

Also prove competing writer rejection and recovery from interruption.

No broad UI polishing or new adapter work takes priority over this proof.

### E6: Commercial UI completion

- Games Library and game workspace;
- import;
- Start/Host/Join;
- lifecycle progress;
- tray/background state;
- recovery and blocked flows;
- commercial polish and accessibility.

### E7: Second-adapter handoff proof

Repeat the two-device flow with the other initial adapter. Resolve only game-specific issues inside that adapter.

### E8: Release hardening

- security review;
- backup/restore proof;
- long-session and large-World tests;
- installer/update behavior;
- bounded retries/cache/retention;
- diagnostics and support workflow;
- release acceptance plan executed completely.

### E9: Performance optimization

Only after correctness:

- deduplication;
- direct peer-to-peer acceleration;
- background prefetch;
- compression tuning;
- retention compaction.

## Initial commercial release boundary

Required:

- Windows desktop/tray product;
- Steam identity/platform integration;
- Factorio and Palworld as reliable initial adapters;
- import of existing Worlds;
- local start and temporary hosting;
- background session observation;
- safe automatic capture and commit;
- shared durable latest state;
- distributed one-writer protection;
- cross-device continuation;
- interrupted-handoff recovery;
- clear environment/adapter limitations;
- concise game-first UI.

Not required:

- generic save merging;
- Fork/branch workflows;
- parties, chat, public discovery, likes, or community feeds;
- complex ownership or gameplay roles;
- permanent hosted game-server fleets;
- live host migration;
- every mod ecosystem;
- a large game catalog.

## Cross-workstream decisions that must be resolved first

The first planning sequence should close these questions in order:

1. Exact first-release user actions: Start World, Host World, Join, Stop and Save.
2. Desktop lifetime: single tray process and one active managed session per device.
3. Shared backend shape: hosted service, Steam-only, or hybrid.
4. Minimal shared access/invitation/revocation model.
5. Reservation uncertainty, reclaim, and late-session rejection.
6. Shared World offline behavior.
7. Recovery candidate actions and UI wording.
8. Factorio final hosted-session model.
9. Palworld graceful stop/readiness and player identity limitation treatment.
10. Package size, retention, cost, security, and provider constraints.

## Immediate next step

Do not implement.

Begin P0.2 by resolving the UI action model and user-visible state/action matrix, because those decisions define what the backend and runtime must expose without deciding their internal implementation.