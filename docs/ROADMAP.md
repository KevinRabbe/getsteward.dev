# Master Roadmap

## Product target

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward coordinates three implementation workstreams:

1. [UI and UX Roadmap](UI_ROADMAP.md)
2. [Backend Roadmap](BACKEND_ROADMAP.md)
3. [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md)

They deliver one product model. No workstream may invent a competing definition of World, session, sharing, hosting, recovery, or authority.

## Current mode: implementation unlocked

Status: **P0 COMPLETE. E1 COMPLETE AND GREEN. BACKEND BE-2 THROUGH BE-5 DEVELOPMENT ACCEPTANCE COMPLETE AND GREEN. E4 WINDOWS DESKTOP PRODUCT COMPOSITION COMPLETE AT THE CODE/CI BOUNDARY; LIVE DEPLOYMENT ACCEPTANCE ACTIVE.**

The planning contracts remain the first-release implementation baseline. Changes remain possible, but any deliberate product-boundary or contract change must be documented before implementation changes follow it.

Implementation is allowed only when it maps to:

- one workstream;
- one numbered milestone;
- one acceptance criterion.

The active phase remains **E4: Background runtime integration**, but its remaining work is now acceptance rather than structural Desktop composition. The Windows Desktop already composes the authenticated remote stack, preserves the local-only path, enforces exact-environment Verify/Repair, and exposes deterministic pending/cleanup/interrupted recovery. E4 closes only after that code survives the real Steam + deployed-backend + two-installation Factorio handoff.

## Product boundaries

### UI/UX owns

- Games Library;
- per-game World workspace;
- import;
- Start World / Host World / Join;
- Share World / Manage access;
- lifecycle/progress presentation;
- tray/background visibility;
- Connection required / Waiting to sync / Action required / Recovery needed / Interrupted session presentation.

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

# Planning phase P0 — COMPLETE

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

BE-D001 through BE-D015 define the first-release backend contract.

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
- final UI terms include Running, Hosting, Host is starting, Someone is playing, Saving World, Action required, Recovery needed, and Interrupted session;
- Waiting to sync preserves an unresolved candidate;
- Join never creates a second writer;
- access administration never grants gameplay/reservation priority.

## P0.6: First-release acceptance plan

Status: **complete as a specification; execution occurs during implementation/release validation**.

Canonical source: `CROSS_WORKSTREAM_CONTRACT.md`.

## P0.7: Explicit planning sign-off

Status: **complete — product owner explicitly approved continuing into implementation.**

# Drift-control rules after unlock

## Every code change maps to the roadmap

Every production change must map to one workstream, one numbered milestone, and one acceptance criterion.

## Finish before expanding

Do not jump to later features to avoid a difficult current requirement.

## Parking lot new ideas

Ideas that do not unblock the active milestone are recorded/deferred. They do not enter implementation merely because they sound useful.

## Boundary changes update documentation first

A deliberate product-boundary change updates, where applicable:

1. `NON_NEGOTIABLE_RULES.md`;
2. `PRODUCT_BOUNDARY.md`;
3. `DECISIONS.md`;
4. affected roadmap/cross-workstream contracts;
5. implementation.

## Evidence over assumption

Claims about Factorio, Palworld, Steam, package behavior, process ownership, safe capture, or recovery remain conditional until controlled evidence proves them.

# Validated implementation foundation

The local lifecycle proves:

```text
discover
-> import
-> inspect exact environment
-> prepare isolated workspace
-> restore state
-> launch local or hosted session
-> adapter observes real session
-> capture updated state
-> store immutable revision
-> advance canonical head last
-> preserve durable recovery evidence on failure
```

E1 additionally proved the shared authority contract in a provider-free deterministic simulation: one-writer generations, uncertainty/reclaim, resumable verified candidate transfer, idempotent mutation replay/result lookup, expected-head commit, retention, recovery preservation, and PC A -> PC B -> PC A handoff behavior.

BE-2 replaced simulated identity/access/metadata with server-verified Steam identity, Steward sessions, shared World metadata, flat membership/Access Manager administration, invitations/revocation/leave flows, and durable PostgreSQL persistence.

BE-3 replaced simulated transfer with private immutable object storage, resumable multipart upload, direct authorized transfer, exact size/SHA-256 verification, publication, cleanup/retention, verified desktop cache/materialization, and a real S3-compatible protocol proof.

BE-4 replaced simulated distributed authority with PostgreSQL-backed reservation/generation coordination and canonical commit: acquire, heartbeat, Uncertain/reconnect, deliberate reclaim, generation invalidation, expected-head commit, late-writer rejection, and durable mutation idempotency.

BE-5 composes those boundaries through Core and proves authenticated remote metadata, immutable environment manifests, verified transfer, exact-generation commit, two-device continuation, adverse reclaim behavior, and deterministic lost-success recovery.

Canonical backend evidence is recorded in `BE2_STATUS.md`, `BE3_S3_CHECKPOINT.md`, `BE4_STATUS.md`, and `BE5_STATUS.md`.

# Execution plan

## E1: Contract and conformance foundation — COMPLETE AND GREEN

Completed slices:

- **BE-1** provider-free deterministic backend simulation;
- **AR-1** generic runtime/conformance extraction and tests;
- **UI-1** shell/navigation replacement using the frozen state/action contract.

Canonical evidence: `E1_STATUS.md`.

## E2: Shared state foundation — BACKEND COMPLETE AND GREEN

### BE-2 — Authentication and World metadata service

Complete and green.

### BE-3 — Immutable object transfer

Complete and green. Object storage never decides what revision is current.

## E3: Distributed one-writer coordination — BACKEND COMPLETE AND GREEN

BE-4 provides and proves:

- acquire;
- heartbeat;
- Active -> Uncertain;
- same-generation reconnect;
- deliberate reclaim;
- generation invalidation;
- expected-head canonical commit;
- durable idempotent acquire/reclaim/commit;
- parallel race protection;
- stale/late writer rejection.

## E4: Background runtime integration — CODE/CI COMPLETE, LIVE ACCEPTANCE ACTIVE

Implemented and CI-proven:

- real Steam Web API ticket acquisition path in Desktop;
- stable installation-bound identity;
- authenticated Steward session/refresh client;
- merged local/shared World catalog with authoritative routing by World ID;
- verified remote package download/cache/materialization;
- distributed coordinator using BE-4 authority;
- resumable multipart candidate upload;
- exact-generation expected-head commit;
- pre-launch reservation abandonment;
- exact immutable environment manifests;
- Verify/Repair hard gate before shared writable play;
- local-only path preserved independently of backend availability;
- durable workspace journal including base state, exact environment, and candidate identity;
- deterministic remote pending recovery;
- deterministic local pending recovery;
- exact-environment cleanup-only recovery;
- distinct crash-found `InterruptedSession` responsibility;
- explicit **Recover changes** and confirmed **Discard interrupted session** paths;
- tray/Quit/update/writable-action guards tied to durable responsibility;
- fail-closed state-head, environment-head, candidate-parent, and adapter checks.

Current code-level recovery rule:

```text
candidate already canonical
    -> no recapture/recommit
    -> cleanup + clear journal

base state + exact journaled environment still canonical
    -> acquire exact authority
    -> re-check state + environment
    -> reuse stable candidate ID
    -> store/reuse candidate
    -> commit

state or environment diverged
    -> do not overwrite or combine histories
    -> preserve evidence
```

Factorio hosted play already uses the real dedicated-server/RCON lifecycle at the adapter layer. E4 still requires the complete product path to be exercised on the real Windows Steam/deployed-backend boundary.

Canonical status: `E4_DESKTOP_STATUS.md`.

Latest code checkpoint covering interrupted-session decisions, local recovery, and exact-environment remote recovery: commit `4ec21efec9a6542938fa4f32b2a5c3ecb2c2b424`, CI run `29941402902`, full five-gate matrix green.

### E4 live acceptance sequence

```text
real Windows Steward build under Steward Steam AppID
-> real Steam Web API ticket
-> deployed Steward API verifies same AppID/identity
-> shared Factorio World appears
-> exact environment reaches Ready
-> remote Continue/Host
-> exact reservation + verified canonical download
-> dedicated Factorio server reaches authenticated RCON readiness
-> host gameplay
-> RCON server-save + observed save refresh
-> capture + multipart upload
-> expected-head commit
-> second Steward installation observes and continues new canonical revision
```

E4 is not product-complete until this real boundary is proven.

## E5: Development two-device proof / BE-5 — COMPLETE AND GREEN

```text
PC A commits N+1
-> PC B downloads, verifies, restores, and continues N+1
-> PC B commits N+2
-> PC A downloads, verifies, restores N+2
```

Also proven:

- competing writer rejection;
- interrupted multipart transfer resume;
- active-generation outage -> `Uncertain`;
- lost-success Waiting-to-sync completion;
- deliberate reclaim after grace;
- generation increase and late old-generation rejection;
- fail-closed recovery on canonical divergence.

Canonical evidence: `BE5_STATUS.md`.

## E6: Commercial UI completion

- Games Library/workspace;
- import;
- Start/Host/Join;
- Share/Manage access;
- lifecycle progress;
- tray/background;
- recovery/action-required/interrupted-session flows;
- accessibility/commercial polish.

E6 may proceed alongside E4 live acceptance only when UI work consumes real runtime/backend states instead of inventing substitute behavior.

## E7: Second-adapter handoff proof

Repeat the production-composed two-device flow with the other initial adapter. Keep game-specific issues inside its adapter.

Palworld shared play remains fail-closed until its real exact-environment verifier is implemented and proven.

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

Execute **E4 live deployment acceptance** rather than building another abstraction layer. Use the real Windows Steward build, real Steam AppID/Web API ticket, deployed backend, and two Steward installations to prove the Factorio shared-World handoff end to end. Any failure found there becomes the next concrete engineering task. Do not redesign backend authority, transfer, or adapter boundaries unless that evidence requires it.
