# Master Roadmap

## Product target

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward coordinates three implementation workstreams:

1. [UI and UX Roadmap](UI_ROADMAP.md)
2. [Backend Roadmap](BACKEND_ROADMAP.md)
3. [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md)

They deliver one product model. No workstream may invent a competing definition of World, session, sharing, hosting, recovery, or authority.

## Current mode: implementation unlocked

Status: **P0 COMPLETE. E1 COMPLETE AND GREEN. E2 SHARED STATE FOUNDATION ACTIVE — BE-2 COMPLETE, BE-3 ACTIVE.**

The planning contracts remain the first-release implementation baseline. Changes remain possible, but any deliberate product-boundary or contract change must be documented before implementation changes follow it.

Implementation is allowed only when it maps to:

- one workstream;
- one numbered milestone;
- one acceptance criterion.

The active implementation phase is **E2: Shared state foundation**.

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

BE-D001 through BE-D015 define the first-release backend contract. Named production provider selection is explicitly deferred until the immutable-transfer milestone has enough measured evidence to choose one without redefining the product model.

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

Existing implementation now provides:

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

E1 additionally proved the shared authority contract in a provider-free deterministic simulation: one-writer generations, uncertainty/reclaim, resumable verified candidate transfer, idempotent mutation replay/result lookup, expected-head commit, retention, recovery preservation, and PC A -> PC B -> PC A handoff behavior.

The E1 implementation was validated by GitHub Actions CI run `600` on head `fc22978ed46e5158e3cabdd06cd991f0d6c47c93`: formatter verification, Ubuntu Release build/tests, and Windows Release build/tests all passed.

BE-2 replaced the simulated identity/access/metadata side with real backend contracts and durable PostgreSQL persistence: server-verified Steam identity, Steward access/refresh sessions, shared World metadata, flat membership + Access Manager administration, invitations/revocation/leave flows, and immutable state/environment metadata.

BE-2 persistence was validated by GitHub Actions run `29866444653`: formatter verification, Ubuntu Release build/tests, Windows Release build/tests, and live PostgreSQL integration tests all passed. Canonical detail is recorded in `BE2_STATUS.md`.

# Execution plan

## E1: Contract and conformance foundation — COMPLETE AND GREEN

Completed slices:

- **BE-1** provider-free deterministic backend simulation;
- **AR-1** generic runtime/conformance extraction and tests;
- **UI-1** shell/navigation replacement using the frozen state/action contract.

Canonical evidence:

- `E1_STATUS.md`;
- CI run `600` on `fc22978ed46e5158e3cabdd06cd991f0d6c47c93`.

No provider-specific or game-specific shortcut may bypass the E1 contracts as later phases replace simulated boundaries with real services.

## E2: Shared state foundation — ACTIVE

E2 is replacing BE-1's simulated shared-state boundaries incrementally while preserving the same authority and failure semantics.

### BE-2 — Authentication and World metadata service: COMPLETE AND GREEN

Implemented and validated:

- server-verified Steam identity boundary;
- Steward access/refresh authentication sessions;
- persistent shared World metadata;
- flat membership + one Access Manager;
- World-access invitations, acceptance/decline, revocation, pending revocation, leave, and manager transfer;
- immutable state/environment metadata;
- provider-neutral PostgreSQL-compatible persistence;
- live PostgreSQL integration coverage.

Canonical evidence:

- `BE2_STATUS.md`;
- CI run `29866444653`.

### BE-3 — Immutable object transfer: ACTIVE

Implement:

- HTTPS/JSON transfer authorization control plane;
- private immutable object-storage boundary;
- bounded/resumable upload;
- exact size/hash verification before publication;
- immutable state/environment package publication;
- resumable authorized download;
- direct desktop/object-storage byte transfer;
- orphan/partial cleanup;
- BE-D009/BE-D010 retention integration;
- client verification/cache/materialization contract.

Provider selection may occur during BE-3 only after the provider-neutral contract is fixed and current EU storage/transfer economics have been measured. No provider may redefine World authority or package semantics.

Canonical-head compare-and-swap commit and distributed reservation/generation authority remain **BE-4 / E3** concerns. Object storage never decides what revision is current.

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

Begin **E2 / BE-3 immutable object transfer** by defining the provider-neutral private object-storage and transfer-authorization contracts first. Preserve the 20 GiB hard ceiling, resumable transfer semantics, exact byte/hash verification, opaque package boundary, and direct client/object-store architecture before selecting a production storage provider.
