# Documentation State Audit

Audited against exact qualified product head:

> `e63c6f7d20d103cd2ea3d9a922b73de3c3ba1f5f` — PR #138, first real V3-E Windows defect fix

Status: **CURRENT DOCUMENTATION OVERLAY**

This file does not replace the product contracts. It answers a narrower question:

> **Which repository documents still describe current executable/product truth, which are useful but stale, and where has code drifted away from an approved document?**

Until individual stale documents are reconciled, use this audit as the status overlay. Do not delete historical evidence merely because its old milestone language is no longer current.

## Status meanings

- **CURRENT** — safe to use as current product/engineering guidance.
- **CURRENT CONTRACT — IMPLEMENTATION DRIFT** — the document remains the intended product contract; executable/UI state has drifted and should be brought back to the contract rather than rewriting the contract to excuse the drift.
- **PARTIALLY OUTDATED** — core rules remain useful, but current-state, milestone, topology, adapter-count, or implementation statements are stale. Do not use stale sections as executable truth.
- **OUTDATED** — contains active technical claims that contradict the current product path. Do not implement from it until reconciled.
- **HISTORICAL CHECKPOINT** — preserved evidence for a completed earlier milestone. It is not the current plan or current repository status.

## Current executable/product baseline

The audit uses these current facts:

1. Deterministic V3 release-candidate preparation is complete through #137.
2. Real V3-E Windows acceptance has started. The first observed defect was the v1 -> v2 device-settings migration failure on Windows; #138 fixes and regression-tests it.
3. Desktop composition contains **19 first-party adapters**.
4. `GameAdapterCapabilities` remains the executable source of truth for Start/Host/Join/Stop/Create claims; catalog registration alone grants none of those actions.
5. Steam owns Steward distribution/update and actual runtime AppID discovery. Normal commercial release configuration is package-owned; `STEWARD_*` environment configuration is engineering/isolated-acceptance configuration.
6. The generic platform/backend/storage/authority/recovery architecture is already implemented. Shared PostgreSQL/S3-backed authority is not a future architecture layer.
7. The first-release UI product hierarchy remains **Games -> Worlds -> selected World details**.
8. Current Desktop XAML does **not** fully implement that hierarchy: it still uses a fixed 330 px World-navigation sidebar with the World list directly beside the selected-World detail surface.

## Highest-value discrepancies

### 1. UI specification is current; executable navigation has drifted

`UI_ROADMAP.md` explicitly requires:

```text
Games Library
-> select game
-> game workspace
-> Worlds for that game
-> selected World details
```

The approved game workspace includes a game banner/header, search/sort, Import World, per-game Worlds, and responsive World details.

Current `MainWindow.xaml` instead composes:

```text
fixed 330 px left navigation
-> small game selector
-> WorldList directly in sidebar

right side
-> selected World details
```

With 19 adapters this is no longer merely visual polish. It is a navigation/information-architecture mismatch.

**Decision:** `UI_ROADMAP.md` remains authoritative. Do not rewrite it to match the current sidebar. Later UI work should bring executable navigation back to the approved Games Library -> Game -> Worlds hierarchy.

### 2. Factorio documentation describes the wrong active host architecture

`FACTORIO.md` describes dedicated/headless server + RCON + separate host client as the active hosted product lifecycle.

Current product/status evidence distinguishes richer dedicated-server/RCON primitives from the active adapter host path. The richer primitives may remain in the repository, but code existence is not product capability.

**Decision:** `FACTORIO.md` is **OUTDATED** for hosted-runtime guidance and must not be used to expand/rebuild that path. Reconcile it from the actual `LaunchHostAsync` behavior and current empirical gate before further Factorio lifecycle work.

### 3. Several architecture/storage documents still call implemented shared infrastructure "future"

`ARCHITECTURE.md` and `STORAGE.md` contain correct invariants but still describe shared durable backend/storage/coordination as a next/future layer.

Current repository state already contains Backend.Api, PostgreSQL persistence, S3-compatible immutable object storage, authenticated shared metadata/access, distributed reservation generations, direct transfer, remote Desktop composition, and deterministic two-device handoff proof.

**Decision:** retain their invariant sections; do not use their old implementation-status paragraphs as current sequencing.

### 4. Old planning/status files are being mistaken for current roadmap state

BE/E1/E4/E6/E8 status documents preserve important evidence, but many have statements such as "next active phase", old adapter counts, old branch limitations, or pre-V3 deployment configuration.

**Decision:** preserve them as historical checkpoints. Current execution order comes from V3 + this audit + current empirical evidence, not an old checkpoint's final paragraph.

### 5. Package/deployment truth superseded environment-variable production setup

`E4_DESKTOP_STATUS.md` describes the older commercial remote setup requiring three `STEWARD_*` client environment values.

V3-A replaced that production assumption with adjacent immutable `steward-steam-release.json`; environment variables remain engineering configuration only.

**Decision:** E4 status is historical composition evidence, not the commercial release configuration authority.

### 6. First disposable Scaleway topology changed

`BE_PROVIDER_EVALUATION.md` still names Scaleway Serverless Containers as the first candidate topology.

Qualified #131 deliberately replaced that first Host/Join candidate with one small Scaleway Instance + Caddy + Backend.Api on Linux host networking, using one exact trusted loopback proxy peer. The reason was to reuse Steward's narrow one-proxy trust contract rather than broaden forwarded-header trust for an ingress whose exact peer address was not documented.

**Decision:** provider-evaluation criteria remain useful; its named first-candidate topology is stale. `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` owns the current disposable topology.

## Document classification

### Product/architecture authority

| Document | Status | Audit note |
|---|---|---|
| `NON_NEGOTIABLE_RULES.md` | **CURRENT** | Product safety/boundary rules remain aligned. |
| `PRODUCT_BOUNDARY.md` | **CURRENT** | Product definition and ownership boundaries remain aligned. |
| `DECISIONS.md` | **PARTIALLY OUTDATED** | Durable decisions mostly hold; old validation-set/next-proof/planning-lock statements are stale. |
| `ARCHITECTURE.md` | **PARTIALLY OUTDATED** | Dependency model is valid; shared backend/storage described as future is obsolete. |
| `DOMAIN_MODEL.md` | **CURRENT** | Current World/revision/session/recovery concepts remain aligned; no permanent Server object. |
| `WORLD_LIFECYCLE.md` | **CURRENT** | Generic handoff lifecycle still matches the implementation model. |
| `NATIVE_WORLD_CREATION.md` | **PARTIALLY OUTDATED** | Core native-creation rule is current; top-level V2 status/game qualification sections are stale. |

### Current release/product state

| Document | Status | Audit note |
|---|---|---|
| `V3_STEAM_RELEASE_CANDIDATE.md` | **PARTIALLY OUTDATED** | V3 contract is current, but final #137 SHA/status text predates the final closure head and #138's first real V3-E defect/fix. |
| `PLATFORM_IMPLEMENTATION_STATUS.md` | **PARTIALLY OUTDATED** | Stable platform and 19-adapter model are current; "latest product line = #135" is stale and V3-E has now begun. |
| `STEAM_RELEASE_GATE.md` | **CURRENT** | Correct V3-F external gate and Steam-owned install/update boundary. #138 is exactly the narrow V3-E defect workflow it anticipates. |
| `DEFERRED_EMPIRICAL_TESTS.md` | **CURRENT, SMALL STATUS DRIFT** | Empirical registry is still authoritative; Desktop Windows section should record that V3-E has begun and #138 closed one observed migration defect while keyboard/Narrator/DPI tests remain open. |
| `V2_FRIENDS_BUILD.md` | **HISTORICAL / COMPLETED STAGE** | Correctly self-identifies deterministic V2 as complete and points active work to V3. |
| `V2_REAL_ACCEPTANCE_BATCH.md` | **CURRENT EMPIRICAL RUNBOOK** | Still usable for the private real-machine/provider evidence that was never claimed complete. |
| `V2_FRIENDS_DEPLOYMENT.md` | **CURRENT PRIVATE-TEST RUNBOOK** | Friends Build deployment/distribution remains a test surface, not commercial release authority. |
| `V2_7DTD_SANDBOX_AUTHORITY.md` | **CURRENT GAME-SPECIFIC DECISION** | Current 7DTD deterministic SandboxCode ownership rule. |

### UI/product experience

| Document | Status | Audit note |
|---|---|---|
| `UI_ROADMAP.md` | **CURRENT CONTRACT — IMPLEMENTATION DRIFT** | Games Library -> game workspace -> Worlds remains the approved first-release hierarchy. Current XAML is the side that drifted. "installer/update behavior" in old UI-6 wording is superseded by Steam-owned updates. |
| `UI0_SIGNOFF_CHECKLIST.md` | **HISTORICAL APPROVED CONTRACT CHECKPOINT** | Terminology/state/action decisions remain useful; milestone status is historical. |
| `CROSS_WORKSTREAM_CONTRACT.md` | **CURRENT** | State/action/authority/recovery mapping remains aligned with current product behavior. |
| `E6_STATUS.md` | **HISTORICAL CHECKPOINT** | Useful evidence for the earlier commercial UI stack; its four-adapter/current-completion framing must not override the current UI roadmap or real V3-E observations. |

### Backend

| Document | Status | Audit note |
|---|---|---|
| `BACKEND_ROADMAP.md` | **PARTIALLY OUTDATED** | BE-D safety decisions remain useful; implementation status saying BE-3 is active is obsolete because BE-2 through BE-5 are complete. |
| `BE_API_CONTRACT.md` | **PARTIALLY OUTDATED** | Conceptual HTTPS/idempotency/authority/transfer rules remain useful; document still calls itself proposed/planning and examples are not the current route/schema authority. Executable API + tests win on exact wire shape. |
| `BE_SCHEMA_AND_LIFECYCLE.md` | **PARTIALLY OUTDATED** | Invariants remain useful; planning-lock/completion wording and some logical enum/record examples predate actual PostgreSQL implementation. |
| `BE_OPERATIONS_RUNBOOK.md` | **CURRENT CONTRACT, HISTORICAL PLANNING WORDING** | Operating/fail-closed/backup rules remain applicable; it is no longer merely pre-implementation planning. |
| `BE_SECURITY_THREAT_MODEL.md` | **CURRENT CONTRACT, STALE GATE WORDING** | Threat/control model still applies; "before BE-2" acceptance-gate wording is historical. |
| `BE_PROVIDER_EVALUATION.md` | **PARTIALLY OUTDATED** | Evaluation criteria are valid; first disposable Serverless topology is superseded by qualified #131 Instance + Caddy topology. |
| `BE0_SIGNOFF_CHECKLIST.md` | **HISTORICAL CHECKPOINT** | Planning sign-off evidence. |
| `BE1_LOCAL_SIMULATION.md` | **HISTORICAL CHECKPOINT** | Provider-free contract proof, superseded as current implementation by provider-backed layers. |
| `BE2_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid BE-2 evidence, not current project status. |
| `BE3_S3_CHECKPOINT.md` | **HISTORICAL CHECKPOINT** | Valid immutable-transfer evidence, not current project status. |
| `BE4_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid distributed-authority evidence, not current project status. |
| `BE5_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid deterministic two-device proof, not the remaining real Steam/game release proof. |

### Runtime/adapters

| Document | Status | Audit note |
|---|---|---|
| `ADAPTER_RUNTIME_ROADMAP.md` | **PARTIALLY OUTDATED** | Generic runtime/capture/background contracts are valid; Factorio/Palworld capability matrices and release-supported terminology lag current V3 capability/evidence model. |
| `AR0_SIGNOFF_CHECKLIST.md` | **HISTORICAL CHECKPOINT** | Approved initial runtime planning evidence. |
| `ADAPTER_GUIDE.md` | **PARTIALLY OUTDATED** | Adapter rules remain strong, but first-party list omits Space Engineers and therefore says 18 where executable catalog has 19. |
| `FACTORIO.md` | **OUTDATED** | Hosted-runtime section conflicts with current product path; do not use as implementation authority until reconciled. |
| `PALWORLD.md` | **CURRENT** | Read-only WorldOption, disposable management inputs, process-tree/REST save-stop, and identity limitation remain aligned. |

### Persistence/recovery/engineering

| Document | Status | Audit note |
|---|---|---|
| `STORAGE.md` | **PARTIALLY OUTDATED** | Atomicity/immutability rules are current; shared durable storage described as a next/future milestone is already implemented. |
| `PERSISTENCE_COMPATIBILITY.md` | **PARTIALLY OUTDATED** | Compatibility principles are current; listed document types/migration description predate later integrity hardening and device-settings v1->v2 migration exercised by #138. |
| `WORKSPACE_RECOVERY.md` | **CURRENT** | Active/RecoveryPending/CleanupPending/Interrupted-session semantics match current product behavior. |
| `ENGINEERING.md` | **CURRENT** | Engineering, testing, bounds, state safety, and documentation-drift rules remain aligned. |
| `ERROR_HANDLING.md` | **CURRENT** | Conservative failure semantics remain aligned. |

### Historical execution checkpoints

| Document | Status | Audit note |
|---|---|---|
| `E1_STATUS.md` | **HISTORICAL CHECKPOINT** | Valid E1 evidence; "next active phase E2" and old guided-manual Join are historical. |
| `E4_DESKTOP_STATUS.md` | **HISTORICAL CHECKPOINT** | Valuable remote-composition evidence; production `STEWARD_*` configuration wording predates V3-A package configuration. |
| `E8_STATUS.md` | **HISTORICAL CHECKPOINT** | Valuable deterministic hardening evidence; old branch/checkpoint language is not current status. |
| `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` | **CURRENT EXTERNAL RUNBOOK** | Qualified #131 disposable Instance + Caddy topology and exact one-proxy trust shape. |

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

Do not use the root README's current-status sections as the release roadmap until reconciled.

## Docs -> executable differences that require action

These are actual mismatches, not merely historical wording:

| Area | Document says | Current executable/evidence says | Correct owner of truth |
|---|---|---|---|
| Main navigation | Games Library -> game workspace -> per-game Worlds | fixed 330 px World sidebar + compact game selector + permanent World detail pane | **UI contract wins; code must later be reconciled** |
| Adapter count | several older docs show 4 or 18 | `DesktopGameAdapterCatalog` contains 19 | **code/current platform status wins** |
| Factorio hosted architecture | active dedicated server + RCON + separate host client | current product status distinguishes those primitives from active host behavior | **current executable/status evidence wins** |
| Shared storage | future/next layer | PostgreSQL + S3-compatible remote composition is implemented | **code/current backend status wins** |
| Backend milestone | BE-3 active | BE-2 through BE-5 complete | **current evidence wins** |
| Production Desktop config | three `STEWARD_*` values | V3 production package uses `steward-steam-release.json`; env vars are engineering-only | **V3/current code wins** |
| First Scaleway ingress | Serverless Containers | Instance + Caddy + localhost Backend.Api exact-proxy topology | **qualified #131 deployment doc wins** |
| V3 current head | older V3 docs stop at #135/#137 intermediate SHA | #137 final closure `8765394c...`; #138 qualified `e63c6f7d...` | **qualified current head wins** |
| Windows acceptance | entirely deferred | first V3-E real defect observed and fixed in #138; other UI/OS observations remain open | **real evidence wins** |
| Native creation stage label | V2 active | deterministic V2 finished; only capability-proven creation should be exposed | **V3/current capability model wins** |

## What is *not* a mismatch

Do not "fix" these stable rules while reconciling docs:

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

Do not mass-rewrite history. Reconcile only documents that can currently misdirect implementation:

1. `FACTORIO.md` — wrong active hosted-runtime description.
2. root `README.md` — high-traffic current-state drift.
3. `V3_STEAM_RELEASE_CANDIDATE.md` and `PLATFORM_IMPLEMENTATION_STATUS.md` — current-head/V3-E drift.
4. `ROADMAP.md` — replace old E-stage "current" sequencing with current V3/empirical state while preserving history.
5. `ARCHITECTURE.md` and `STORAGE.md` — remove "future shared backend" statements.
6. `BACKEND_ROADMAP.md`, `BE_API_CONTRACT.md`, `BE_SCHEMA_AND_LIFECYCLE.md`, `BE_PROVIDER_EVALUATION.md` — preserve contracts, correct status/topology wording.
7. `ADAPTER_RUNTIME_ROADMAP.md`, `ADAPTER_GUIDE.md`, `NATIVE_WORLD_CREATION.md`, `PERSISTENCE_COMPATIBILITY.md` — reconcile current capabilities/counts/migrations.
8. Historical status files stay historical unless a missing banner causes repeated confusion.

The Games Library implementation should be addressed **after** this documentation reconciliation, on a separate UI branch. The audit result is that its intended hierarchy is already documented; the current UI is the drift, not the specification.
