# Documentation State Audit

Audited against exact qualified executable/product head:

> `e63c6f7d20d103cd2ea3d9a922b73de3c3ba1f5f` — PR #138, first real V3-E Windows defect fix

Status: **CURRENT DOCUMENTATION OVERLAY**

This file answers one question:

> **Which repository documents still describe current executable/product truth, which are useful but stale, and where has executable behavior drifted away from an approved document?**

Until stale documents are individually reconciled, use this audit as the status overlay. Historical evidence should remain available, but old checkpoint wording must not silently become the current plan.

## Status meanings

- **CURRENT** — safe to use as current product/engineering guidance.
- **CURRENT CONTRACT — IMPLEMENTATION DRIFT** — the document remains the intended product contract; executable/UI state should be reconciled to it.
- **PARTIALLY OUTDATED** — core rules remain useful, but current-state, milestone, topology, adapter-count, or implementation statements are stale.
- **OUTDATED TECHNICAL SECTION** — a specific active-sounding technical claim conflicts with executable truth. Do not implement from that section.
- **HISTORICAL CHECKPOINT** — useful evidence from an earlier milestone, not current sequencing/status authority.

## Audit method

Do not decide executable truth from a status document when production code can answer the question directly.

In particular, explicit interface implementations matter. During this audit an initial reading incorrectly treated the public `FactorioAdapter.LaunchHostAsync` path plus an old E4 status paragraph as the active product Host lifecycle. The deeper code check found the actual interface contract:

```text
IGameAdapter.LaunchHostAsync
-> explicit implementation in FactorioAdapter.Hosting.cs
-> LaunchAuthoritativeHostAsync
-> dedicated Factorio server
-> authenticated loopback RCON readiness
-> normal graphical host client
```

Core/Desktop consume adapters as `IGameAdapter`, so the explicit implementation wins. That correction is incorporated below.

## Current executable/product baseline

1. Deterministic V3 release-candidate preparation is complete through #137.
2. Real V3-E Windows acceptance has started. The first observed defect was the v1 -> v2 device-settings migration failure on Windows; #138 fixes and regression-tests it.
3. Desktop composition contains **19 first-party adapters**.
4. `GameAdapterCapabilities` is the executable source of truth for Start/Host/Join/Stop/Create claims; catalog registration alone grants none.
5. Steam owns Steward distribution/update and actual runtime AppID. Normal commercial release configuration is package-owned; `STEWARD_*` environment configuration is engineering/isolated-acceptance configuration.
6. PostgreSQL/S3-backed shared metadata, transfer, authority, recovery, and Desktop remote composition are already implemented.
7. The intended first-release UI hierarchy remains **Games -> game workspace -> Worlds -> selected World details**.
8. Current Desktop XAML still uses a fixed 330 px World-navigation sidebar and therefore does not fully implement that approved hierarchy.
9. Factorio's active `IGameAdapter` Host path is the dedicated-server/RCON/host-client path in `FactorioAdapter.Hosting.cs`; the managed game port is UDP `34197`.
10. Factorio does not currently advertise `AutomaticHostStop`; deterministic hosted completion and a user-visible Stop capability are different claims.

## Highest-value discrepancies

### 1. UI specification is current; executable navigation has drifted

`UI_ROADMAP.md` requires:

```text
Games Library
-> select game
-> game workspace
-> Worlds for that game
-> selected World details
```

Current `MainWindow.xaml` instead composes a fixed 330 px World-navigation sidebar plus a small game selector beside a permanent selected-World detail pane.

With 19 adapters this is an information-architecture mismatch, not merely cosmetic polish.

**Decision:** `UI_ROADMAP.md` remains authoritative. Later UI work should bring executable navigation back to the approved game-first hierarchy.

### 2. E4 Desktop's Factorio "current truth" paragraph is stale

`E4_DESKTOP_STATUS.md` says the active Factorio product Host path is direct `factorio --host <save>` and that dedicated-server/RCON code is inactive.

Current executable code says the opposite at the interface boundary:

```text
FactorioAdapter : IGameAdapter
explicit IGameAdapter.LaunchHostAsync
-> LaunchAuthoritativeHostAsync
-> FactorioHostingOperations.LaunchDedicatedServerAsync
-> RCON readiness
-> host client launch
```

The managed endpoint uses game UDP `34197` plus a per-session game password. RCON uses an ephemeral loopback TCP port and separate secret.

**Decision:** `FACTORIO.md` has been reconciled to executable truth. Treat the Factorio-host paragraph in `E4_DESKTOP_STATUS.md` as an **OUTDATED TECHNICAL SECTION** inside an otherwise useful historical checkpoint.

### 3. Several architecture/storage documents still call implemented shared infrastructure "future"

`ARCHITECTURE.md` and `STORAGE.md` preserve correct invariants but still describe shared durable backend/storage/coordination as a next/future layer.

Current repository state already contains Backend.Api, PostgreSQL persistence, S3-compatible immutable object storage, authenticated shared metadata/access, distributed reservation generations, direct transfer, remote Desktop composition, and deterministic two-device handoff proof.

**Decision:** keep their invariant sections; reconcile implementation-status language.

### 4. Old planning/status files can be mistaken for current roadmap state

BE/E1/E4/E6/E8 checkpoint documents contain valid proof but also old "next phase", adapter-count, branch, or deployment statements.

**Decision:** preserve them as historical checkpoints. Current execution order comes from V3, current executable evidence, and this audit.

### 5. Production configuration superseded the old environment-variable setup

`E4_DESKTOP_STATUS.md` describes the older commercial remote setup requiring three `STEWARD_*` client environment values.

V3-A replaced that production assumption with adjacent immutable `steward-steam-release.json`. Environment variables remain an engineering/acceptance source.

**Decision:** E4 status remains historical composition evidence, not commercial release configuration authority.

### 6. First disposable Scaleway topology changed

`BE_PROVIDER_EVALUATION.md` still names Scaleway Serverless Containers as the first candidate topology.

Qualified #131 instead uses one small Scaleway Instance + Caddy + localhost Backend.Api with one exact trusted loopback proxy peer.

**Decision:** provider-evaluation criteria remain useful; `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` owns the current disposable topology.

## Document classification

### Product / architecture authority

| Document | Status | Audit note |
|---|---|---|
| `NON_NEGOTIABLE_RULES.md` | **CURRENT** | Product safety/boundary rules remain aligned. |
| `PRODUCT_BOUNDARY.md` | **CURRENT** | Product definition and ownership boundaries remain aligned. |
| `DECISIONS.md` | **PARTIALLY OUTDATED** | Durable decisions mostly hold; old validation-set/next-proof/planning-lock statements are stale. |
| `ARCHITECTURE.md` | **PARTIALLY OUTDATED** | Dependency model is valid; shared backend/storage described as future is obsolete. |
| `DOMAIN_MODEL.md` | **CURRENT** | World/revision/session/recovery concepts remain aligned; no permanent Server object. |
| `WORLD_LIFECYCLE.md` | **CURRENT** | Generic handoff lifecycle still matches the implementation model. |
| `NATIVE_WORLD_CREATION.md` | **PARTIALLY OUTDATED** | Core creation rule is current; V2 stage labels/game qualification prose is stale. |

### Current release / product state

| Document | Status | Audit note |
|---|---|---|
| `V3_STEAM_RELEASE_CANDIDATE.md` | **PARTIALLY OUTDATED** | V3 contract is current; status text predates #138's first real V3-E defect/fix. |
| `PLATFORM_IMPLEMENTATION_STATUS.md` | **PARTIALLY OUTDATED** | Stable platform/19-adapter model is current; latest-product-line/V3-E text lags #138. |
| `STEAM_RELEASE_GATE.md` | **CURRENT** | Correct V3-F external gate and Steam-owned install/update boundary. |
| `DEFERRED_EMPIRICAL_TESTS.md` | **CURRENT, SMALL STATUS DRIFT** | Empirical registry remains authoritative; Windows section should record that V3-E began and #138 closed one defect. |
| `V2_FRIENDS_BUILD.md` | **HISTORICAL CHECKPOINT** | Correctly self-identifies deterministic V2 as complete and points active work to V3. |
| `V2_REAL_ACCEPTANCE_BATCH.md` | **CURRENT EMPIRICAL RUNBOOK** | Still usable for the private real-machine/provider evidence never claimed complete. |
| `V2_FRIENDS_DEPLOYMENT.md` | **CURRENT PRIVATE-TEST RUNBOOK** | Friends deployment remains a private test surface, not commercial release authority. |
| `V2_7DTD_SANDBOX_AUTHORITY.md` | **CURRENT** | Current 7DTD SandboxCode ownership rule. |

### UI / product experience

| Document | Status | Audit note |
|---|---|---|
| `UI_ROADMAP.md` | **CURRENT CONTRACT — IMPLEMENTATION DRIFT** | Games Library -> game workspace -> Worlds remains approved. Current XAML is the drift. |
| `UI0_SIGNOFF_CHECKLIST.md` | **HISTORICAL CHECKPOINT** | Terminology/state/action decisions remain useful; milestone state is historical. |
| `CROSS_WORKSTREAM_CONTRACT.md` | **CURRENT** | State/action/authority/recovery mapping remains aligned. |
| `E6_STATUS.md` | **HISTORICAL CHECKPOINT** | Earlier commercial UI evidence; four-adapter/current-completion framing is historical. |

### Backend

| Document | Status | Audit note |
|---|---|---|
| `BACKEND_ROADMAP.md` | **PARTIALLY OUTDATED** | BE-D safety decisions remain useful; implementation status saying BE-3 active is obsolete. |
| `BE_API_CONTRACT.md` | **PARTIALLY OUTDATED** | Conceptual rules remain useful; proposed/planning wording and example wire shapes are not exact current API authority. |
| `BE_SCHEMA_AND_LIFECYCLE.md` | **PARTIALLY OUTDATED** | Invariants remain useful; planning-lock wording and logical examples predate PostgreSQL implementation. |
| `BE_OPERATIONS_RUNBOOK.md` | **CURRENT CONTRACT, HISTORICAL WORDING** | Operational/fail-closed/backup rules remain applicable. |
| `BE_SECURITY_THREAT_MODEL.md` | **CURRENT CONTRACT, STALE GATE WORDING** | Threat/control model still applies; "before BE-2" wording is historical. |
| `BE_PROVIDER_EVALUATION.md` | **PARTIALLY OUTDATED** | Criteria valid; first Serverless topology superseded by #131 Instance + Caddy. |
| `BE0_SIGNOFF_CHECKLIST.md` | **HISTORICAL CHECKPOINT** | Planning sign-off evidence. |
| `BE1_LOCAL_SIMULATION.md` | **HISTORICAL CHECKPOINT** | Provider-free proof superseded as current implementation by provider-backed layers. |
| `BE2_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid BE-2 evidence, not current project status. |
| `BE3_S3_CHECKPOINT.md` | **HISTORICAL CHECKPOINT** | Valid transfer evidence, not current project status. |
| `BE4_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid distributed-authority evidence. |
| `BE5_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid deterministic two-device proof, not the remaining real Steam/game proof. |

### Runtime / adapters

| Document | Status | Audit note |
|---|---|---|
| `ADAPTER_RUNTIME_ROADMAP.md` | **PARTIALLY OUTDATED** | Generic runtime/capture/background contracts are valid; capability matrices/release terminology lag V3. |
| `AR0_SIGNOFF_CHECKLIST.md` | **HISTORICAL CHECKPOINT** | Initial runtime planning evidence. |
| `ADAPTER_GUIDE.md` | **PARTIALLY OUTDATED** | Adapter rules remain strong; first-party list omits Space Engineers and therefore says 18 instead of 19. |
| `FACTORIO.md` | **CURRENT** | Reconciled from explicit `IGameAdapter` hosted path: dedicated server + RCON + host client, UDP 34197, no advertised AutomaticHostStop. |
| `PALWORLD.md` | **CURRENT** | Read-only WorldOption, disposable management inputs, process-tree/REST save-stop, and identity limitation remain aligned. |

### Persistence / recovery / engineering

| Document | Status | Audit note |
|---|---|---|
| `STORAGE.md` | **PARTIALLY OUTDATED** | Atomicity/immutability rules current; shared durable storage described as future is implemented. |
| `PERSISTENCE_COMPATIBILITY.md` | **PARTIALLY OUTDATED** | Principles current; listed document types/migration description predate later integrity hardening and device-settings v1->v2. |
| `WORKSPACE_RECOVERY.md` | **CURRENT** | Current recovery semantics remain aligned. |
| `ENGINEERING.md` | **CURRENT** | Engineering/testing/bounds/documentation-drift rules remain aligned. |
| `ERROR_HANDLING.md` | **CURRENT** | Conservative failure semantics remain aligned. |

### Historical execution checkpoints

| Document | Status | Audit note |
|---|---|---|
| `E1_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid E1 evidence; old next-phase/guided-manual Join language is historical. |
| `E4_DESKTOP_STATUS.md` | **HISTORICAL CHECKPOINT + OUTDATED FACTORIO SECTION** | Valuable remote-composition evidence; production `STEWARD_*` wording predates V3-A and its Factorio "direct listen-host current truth" paragraph contradicts the current explicit interface implementation. |
| `E8_STATUS.md` | **HISTORICAL CHECKPOINT** | Valuable hardening evidence; old branch/checkpoint language is not current status. |
| `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` | **CURRENT EXTERNAL RUNBOOK** | Qualified #131 Instance + Caddy topology and exact one-proxy trust shape. |

## Root README drift

Root `README.md` is **PARTIALLY OUTDATED** as a current-status page.

Still correct:

- product promise;
- World ownership model;
- state/one-writer/commit invariants;
- adapter isolation principle.

Stale:

- architecture diagram lists only the original four adapters;
- Desktop status says its adapter pipeline is Factorio/Palworld/7DTD/PZ rather than the current 19-adapter catalog;
- repository structure lists only four adapter directories;
- "next decisive milestone" predates V3 deterministic release preparation and the start of V3-E;
- it does not mention package-owned Steam release configuration, exact depot content, or the first real V3-E defect/fix.

## Docs -> executable differences that require action

| Area | Document says | Current executable/evidence says | Correct owner of truth |
|---|---|---|---|
| Main navigation | Games Library -> game workspace -> per-game Worlds | fixed 330 px World sidebar + compact game selector + permanent World detail pane | **UI contract wins; code must later be reconciled** |
| Adapter count | several older docs show 4 or 18 | `DesktopGameAdapterCatalog` contains 19 | **code/current platform status wins** |
| E4 Factorio host paragraph | direct `factorio --host` is active; RCON path inactive | explicit `IGameAdapter.LaunchHostAsync` invokes authoritative dedicated-server/RCON path | **current code wins** |
| Factorio game port | old Factorio prose said ephemeral UDP port | managed product Host uses UDP `34197`; RCON remains ephemeral loopback TCP | **current code wins** |
| Shared storage | future/next layer | PostgreSQL + S3-compatible remote composition is implemented | **current code/backend evidence wins** |
| Backend milestone | BE-3 active | BE-2 through BE-5 complete | **current evidence wins** |
| Production Desktop config | three `STEWARD_*` values | V3 package uses `steward-steam-release.json`; env vars engineering-only | **V3/current code wins** |
| First Scaleway ingress | Serverless Containers | Instance + Caddy + localhost Backend.Api exact-proxy topology | **qualified #131 runbook wins** |
| Windows acceptance | entirely deferred | V3-E began; #138 fixed first observed Windows migration defect | **real evidence wins** |
| Native creation stage label | V2 active | deterministic V2 finished; exposure remains capability-driven | **V3/current capability model wins** |

## Stable rules that must not be "fixed"

- one current valid World state;
- one writable Steward session at a time;
- immutable published revisions;
- canonical head advances last;
- no generic save merge/branch system;
- no permanent Steward game-server fleet;
- no second social/account platform;
- Steam owns release distribution/update;
- adapters own game-specific lifecycle/evidence;
- catalog registration does not grant Start/Host/Join/Stop;
- unresolved responsibility blocks competing writable work;
- failure preserves the previous valid state and recovery evidence.

## Reconciliation order

Do not mass-rewrite history. Reconcile documents that can currently misdirect implementation, in this order:

1. root `README.md` — high-traffic current-state drift.
2. `V3_STEAM_RELEASE_CANDIDATE.md` and `PLATFORM_IMPLEMENTATION_STATUS.md` — current-head/V3-E drift.
3. `ROADMAP.md` — replace old E-stage "current" sequencing with current V3/empirical state while preserving history.
4. `ARCHITECTURE.md` and `STORAGE.md` — remove "future shared backend" statements.
5. `BACKEND_ROADMAP.md`, `BE_API_CONTRACT.md`, `BE_SCHEMA_AND_LIFECYCLE.md`, `BE_PROVIDER_EVALUATION.md` — preserve contracts, correct status/topology wording.
6. `ADAPTER_RUNTIME_ROADMAP.md`, `ADAPTER_GUIDE.md`, `NATIVE_WORLD_CREATION.md`, `PERSISTENCE_COMPATIBILITY.md` — reconcile capabilities/counts/migrations.
7. Historical status files stay historical unless a stale active-sounding section repeatedly causes confusion. `E4_DESKTOP_STATUS.md` is the first known example; its Factorio paragraph must not be used as current truth.

The Games Library implementation should be addressed **after** documentation reconciliation, on a separate UI branch. Its intended hierarchy is already documented; current UI is the drift.