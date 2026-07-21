# Master Roadmap

## Product target

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward coordinates three implementation workstreams:

1. [UI and UX Roadmap](UI_ROADMAP.md)
2. [Backend Roadmap](BACKEND_ROADMAP.md)
3. [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md)

They deliver one product model. No workstream may invent a competing definition of World, session, sharing, hosting, recovery, or authority.

## Current mode: implementation unlocked

Status: **P0 COMPLETE — master planning lock lifted by explicit product-owner approval. E1 may begin.**

The planning contracts are frozen as the first-release implementation baseline. Changes remain possible, but any deliberate product-boundary or contract change must be documented before implementation changes follow it.

Implementation is now allowed only when it maps to:

- one workstream;
- one numbered milestone;
- one acceptance criterion.

The first implementation phase is **E1: Contract and conformance foundation**.

## Product boundaries

### UI/UX owns

- Games Library;
- per-game World workspace;
- import;
- Start World / Host World / Join;
- Share World / Manage access;
- lifecycle/progress presentation;
- tray/background visibility;
- Connection required / Waiting to sync / Action required / Recovery needed presentation.

UI never invents backend authority or game-specific lifecycle behavior.

### Backend owns

- verified Steam identity for shared operations;
- flat World membership + one Access Manager;
- immutable package publication/transfer authorization;
- canonical state/environment heads;
- one-writer reservation/generation;
- uncertainty/reclaim;
- expected-head commit;
- remote recovery/retention/security/operations boundaries.

Backend never interprets game saves or runs permanent game servers.

### Runtime/adapters own

Runtime owns generic session orchestration, device-wide writable concurrency, local cache/materialization/recovery lifecycle, and tray/background responsibility.

Adapters own game-specific discovery, environment facts, restore/capture shape, launch/host/join details, session/readiness evidence, graceful stop, safe capture, and game-specific limitations.

## Workstream dependency model

```text
UI states/actions
    <-> generic runtime lifecycle
    <-> backend authority/transaction state
    <-> adapter capabilities/evidence
```

Rules:

- UI requests only actions supported by runtime/backend/adapter contracts.
- Runtime exposes states the UI can communicate consistently.
- Backend uncertainty/recovery semantics match runtime connectivity/crash behavior.
- Adapter capabilities determine Start/Host/Join/Stop and Save availability without game-name branches in Core/UI.
- No workstream may add a concept rejected by `NON_NEGOTIABLE_RULES.md` or `PRODUCT_BOUNDARY.md`.

# Planning phase P0

## P0.1: Roadmap structure

Status: **complete**.

## P0.2: UI-0 contract

Status: **complete and approved**.

Canonical sources:

- `UI_ROADMAP.md`;
- `UI0_SIGNOFF_CHECKLIST.md`.

Approved scope includes:

- Games -> Worlds navigation;
- explicit Start World / Host World / Join;
- local-first import;
- Host independent of persistent sharing;
- shared verification before new writable play;
- capability-driven Join including guided manual fallback;
- evidence-driven recovery;
- persistent tray whenever Steward process runs;
- fixed terminology;
- flat Share World / Manage access surface.

## P0.3: BE-0 contract

Status: **complete and approved**.

Canonical sources:

- `BACKEND_ROADMAP.md`;
- `BE0_SIGNOFF_CHECKLIST.md`.

BE-D001 through BE-D015 define the first-release backend contract. Named production provider selection is explicitly deferred without blocking BE-1.

## P0.4: AR-0 contract

Status: **complete and approved**.

Canonical sources:

- `ADAPTER_RUNTIME_ROADMAP.md`;
- `AR0_SIGNOFF_CHECKLIST.md`.

## P0.5: Cross-workstream reconciliation

Status: **complete and approved**.

Canonical source:

- `CROSS_WORKSTREAM_CONTRACT.md`.

Important reconciled rules:

- `Only on this PC` may Host when temporary-host capability exists;
- persistent sharing is not a prerequisite for temporary hosting;
- final UI terms are Running, Hosting, Host is starting, Someone is playing, Saving World, Action required, etc.;
- Waiting to sync preserves an unresolved candidate;
- Join never creates a second writer;
- access administration never grants gameplay/reservation priority.

## P0.6: First-release acceptance plan

Status: **complete as a specification; execution occurs during implementation/release validation**.

Canonical source:

- `CROSS_WORKSTREAM_CONTRACT.md`.

## P0.7: Explicit planning sign-off

Status: **complete — product owner explicitly approved continuing into implementation.**

The master planning lock is lifted.

# Drift-control rules after unlock

## Every code change maps to the roadmap

Every production change must map to:

- one workstream;
- one numbered milestone;
- one acceptance criterion.

A change without that mapping does not begin.

## Finish before expanding

Do not jump to later features to avoid a difficult current requirement.

## Parking lot new ideas

Ideas that do not unblock the active milestone are recorded/deferred. They do not enter implementation merely because they sound useful.

## Boundary changes update documentation first

A deliberate product-boundary change updates, in order where applicable:

1. `NON_NEGOTIABLE_RULES.md`;
2. `PRODUCT_BOUNDARY.md`;
3. `DECISIONS.md`;
4. affected roadmap/cross-workstream contracts;
5. implementation.

## Evidence over assumption

Claims about Factorio, Palworld, Steam, package behavior, process ownership, safe capture, or recovery remain conditional until documentation/controlled tests prove them.

# Validated implementation foundation

Existing implementation already provides useful foundations:

```text
discover
-> import
-> inspect environment
-> prepare workspace
-> restore state
-> launch local or hosted session
-> adapter observes session
-> capture updated state
-> store immutable revision
-> advance canonical head last
-> preserve recovery evidence on failure
```

Implemented/tested foundations include immutable environment/state revisions, expected-head protection, unchanged-state detection, canonical head advancement after durable storage, workspace recovery records, explicit cleanup ownership, typed failure boundaries, persisted schema envelopes/migrations, Factorio lifecycle evidence, Palworld lifecycle evidence, and the unified WPF adapter registry/game-first UI proof.

# Execution plan

## E1: Contract and conformance foundation — ACTIVE

First slices may proceed independently but must stay inside the frozen contracts:

- **BE-1** provider-free deterministic backend simulation;
- **AR-1** generic runtime/conformance extraction and tests;
- **UI-1** shell/navigation work using the frozen state/action contract.

Current implementation priority begins with **BE-1**, because it proves the shared authority invariants without introducing Steam, HTTP, cloud SDKs, credentials, or provider assumptions.

No provider-specific or game-specific shortcut may bypass the contracts.

## E2: Shared state foundation

- verified Steam identity;
- World metadata/access;
- immutable state/environment metadata;
- authorized upload/download;
- current-head commit;
- client verification/cache integration.

## E3: Distributed one-writer coordination

- acquire;
- heartbeat;
- Uncertain;
- reconnect/reclaim;
- generation invalidation;
- stale/late writer rejection;
- UI active-elsewhere/recovery states.

## E4: Background runtime integration

- desktop/tray lifetime;
- shared reservation/transfer integration;
- safe start/end;
- candidate preservation;
- startup recovery scan;
- safe close/update behavior.

## E5: Development two-device proof

```text
PC A commits N+1
-> PC B restores/continues N+1
-> PC B commits N+2
-> PC A restores N+2
```

Also prove competing writer rejection and interruption/recovery behavior.

No broad UI polishing or new adapter work takes priority over this proof.

## E6: Commercial UI completion

- Games Library/workspace;
- import;
- Start/Host/Join;
- Share/Manage access;
- lifecycle progress;
- tray/background;
- recovery/action-required flows;
- accessibility/commercial polish.

## E7: Second-adapter handoff proof

Repeat the two-device flow with the other initial adapter. Keep game-specific issues inside its adapter.

## E8: Release hardening

- security review;
- backup/restore proof;
- long-session/large-World tests;
- installer/update behavior;
- bounded retries/cache/retention;
- diagnostics/support workflow;
- EU residency/deployment verification;
- complete acceptance plan execution.

## E9: Evidence-driven performance optimization

Only after correctness:

- deduplication;
- peer-assisted transfer;
- CDN/immutable replication;
- background prefetch;
- compression tuning;
- retention compaction/delta/chunk reuse where measurements justify it.

# Initial commercial release boundary

Required:

- Windows desktop/tray product;
- Steam identity/platform integration;
- Factorio and Palworld as reliable initial adapters;
- import existing Worlds;
- local Start and temporary Host;
- background session observation;
- safe capture/commit;
- durable shared latest state;
- distributed one-writer protection;
- explicit sharing/access;
- cross-device continuation;
- interrupted-handoff recovery;
- clear environment/adapter/identity limitations;
- concise game-first UI.

Not required:

- generic save merging;
- Fork/branch workflows;
- parties/chat/public discovery/community feeds;
- gameplay role hierarchy;
- permanent hosted game-server fleet;
- live host migration;
- every mod ecosystem;
- broad game catalog;
- active-active global authority;
- premature delta/P2P optimization.

# Immediate next step

Implement **BE-1 provider-free deterministic backend simulation** according to `BE1_LOCAL_SIMULATION.md`, then validate its deterministic acceptance matrix before adding Steam/HTTP/cloud integration.