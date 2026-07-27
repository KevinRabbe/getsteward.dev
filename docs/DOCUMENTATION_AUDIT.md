# Documentation State Audit

Status: **RECONCILED — ACTIVE DOCUMENTATION MATCHES THE CURRENT QUALIFIED DETERMINISTIC PRODUCT; NO KNOWN DETERMINISTIC UI/PRODUCT DRIFT REMAINS.**

## Current baselines

The useful baselines are intentionally separated:

- generic platform-completion checkpoint: PR #72, `9db4765948e3b69b4d07bc1442d7a1be5c2e3fc7`;
- deterministic V3 Steam release-shape closure: PR #137, `8765394c63d5d6257479269b11ab5c1f86bd7865`;
- first evidence-driven V3-E Windows defect/fix: PR #138, `e63c6f7d20d103cd2ea3d9a922b73de3c3ba1f5f`;
- current non-documentation executable/UI product head: PR #151, `01cb15927a9e056fb9a6784d037eabde66e7d819`;
- current deterministic product-contract head: PR #153, `2556a3ef1ba08f72b83c8c14b55ecef9e4b7ce27`.

The later UI/product work did not reopen Core/backend/adapter authority. It reconciled the commercial Desktop with the already-approved product contract and then removed stale product requirements that added no authority.

## Reconciliation stack after #138

```text
#139-#145 documentation-state reconciliation
-> #147 Games Library hierarchy
-> #148 small World Lobby
-> #149 existing-search audit + missing World sort + global Settings
-> #150 responsibility-backed game attention
-> #151 localization-ready fixed product vocabulary
-> #153 publish-first Share/access contract simplification
```

Important negative findings:

- search was already implemented and wired; duplicate search work was deleted;
- a richer/decorative game-banner subsystem was not required;
- pre-publication invitation staging added no authority and was removed from first release;
- destructive shared-World deletion has no backend terminal contract and was removed from first release;
- production Steam-friendly identity/invite selection belongs to the real V3-F Steam boundary, not a Steward friends graph.

## Audit rule

When prose and production code disagree, inspect the actual authority/caller contract before changing either side.

```text
current contract still correct
-> keep it

current document contains stale implementation/status detail
-> reconcile it to executable truth

old milestone proof still useful
-> keep as HISTORICAL CHECKPOINT

old active-sounding statement conflicts with current code
-> explicitly mark it non-authoritative

supposed missing feature already exists or adds no authority
-> delete the duplicate requirement
```

This rule previously prevented a false Factorio regression: the public helper looked simpler, but the explicit `IGameAdapter.LaunchHostAsync` path already used the dedicated server + RCON implementation consumed by Core/Desktop.

## Current executable/product facts

- generic product platform is implemented;
- deterministic V3 release preparation is complete;
- V3-E real Windows acceptance has started;
- #138 fixed the first real V3-E Windows defect in device-settings v1 -> v2 migration;
- Desktop contains 19 first-party adapters;
- `GameAdapterCapabilities` owns Start/Host/Join/Stop/Create claims;
- shared Backend.Api/PostgreSQL/S3/remote Desktop authority is implemented;
- production Steam config is package-owned; engineering `STEWARD_*` configuration remains an acceptance/development source;
- Steam owns Steward installation/update;
- Factorio active Host path is dedicated server + RCON + graphical host client on managed UDP 34197; no `AutomaticHostStop` flag;
- Palworld advertises Host + Host Stop + exact game version, not automatic Join;
- 7DTD/PZ managed runtime remains frozen behind recorded empirical gates;
- Factorio is the current `NativeWorldCreation` adapter;
- Games Library -> game workspace -> Worlds -> selected World details is implemented;
- the World Lobby is membership + observed current players + authoritative current Host, not a social network;
- global Settings reuses the existing device-settings owner;
- fixed first-release product vocabulary is resource-owned/localization-ready;
- Share publishes one creator-owned shared World first; Manage access owns invitations afterward;
- destructive shared deletion is deliberately absent from first release;
- local World/recovery persistence uses integrity-protected schemas and device settings retain the v1 -> v2 migration contract.

## Active documentation classification

### Product/architecture authority

| Document | Status | Current note |
|---|---|---|
| `NON_NEGOTIABLE_RULES.md` | **CURRENT** | Product/safety boundaries. |
| `PRODUCT_BOUNDARY.md` | **CURRENT** | Product definition/non-goals. |
| `DECISIONS.md` | **CURRENT** | Active durable decisions. |
| `ARCHITECTURE.md` | **CURRENT** | Local + remote storage/coordination/backend authority implemented. |
| `DOMAIN_MODEL.md` | **CURRENT** | Active World/revision/session/recovery model. |
| `WORLD_LIFECYCLE.md` | **CURRENT** | Generic handoff ordering. |
| `CROSS_WORKSTREAM_CONTRACT.md` | **CURRENT** | Publish-first Share/access split and current UI/backend/runtime authority mapping. |
| `PRODUCT_COMPLETENESS.md` | **CURRENT** | Deterministic first-release product completeness closed; remaining gates empirical. |

### Current execution/release

| Document | Status | Current note |
|---|---|---|
| `ROADMAP.md` | **CURRENT** | Deterministic product/UI reconciliation complete; V3-E then V3-F are next. |
| `V3_STEAM_RELEASE_CANDIDATE.md` | **CURRENT** | Deterministic Steam release shape + #138 first real V3-E evidence; no invented V3-G subsystem. |
| `PLATFORM_IMPLEMENTATION_STATUS.md` | **CURRENT** | Generic platform frozen; current executable/product line includes #147-#151 UI reconciliation. |
| `STEAM_RELEASE_GATE.md` | **CURRENT EXTERNAL RUNBOOK** | Real V3-E/V3-F provider/Steam/Windows/game batch. |
| `DEFERRED_EMPIRICAL_TESTS.md` | **CURRENT EMPIRICAL REGISTRY** | Exact unproven real-system questions. |
| `V2_REAL_ACCEPTANCE_BATCH.md` | **CURRENT EXTERNAL RUNBOOK** | One expensive real-machine/Friends Build/network batch also piggybacks Windows UI evidence. |
| `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` | **CURRENT EXTERNAL RUNBOOK** | Qualified Instance + Caddy + exact-one-proxy first disposable topology. |

### UI

| Document | Status | Current note |
|---|---|---|
| `UI_ROADMAP.md` | **CURRENT / IMPLEMENTED DETERMINISTIC CONTRACT** | Game-first hierarchy, search/sort, Settings, attention, lobby, recovery, publish-first Share and non-destructive first release are reconciled. |
| `V2_WORLD_LOBBY.md` | **CURRENT** | Small operational lobby boundary; no Steward social network. |

There is no known current docs↔WPF hierarchy mismatch after #147-#151.

### Backend

| Document | Status | Current note |
|---|---|---|
| `BACKEND_ROADMAP.md` | **CURRENT** | BE-1..5 are implemented history; current authority/external gates described. |
| `BE_API_CONTRACT.md` | **CURRENT** | Implemented `/api/v1` route families/results/idempotency/direct transfer. No destructive shared-World delete route exists. |
| `BE_SCHEMA_AND_LIFECYCLE.md` | **CURRENT** | Implemented PostgreSQL logical stores/transactions/retention. |
| `BE_PROVIDER_EVALUATION.md` | **CURRENT** | Criteria + qualified first Instance/Caddy topology; final provider evidence remains empirical. |
| `BE_OPERATIONS_RUNBOOK.md` | **CURRENT** | Current deterministic vs real operational/restore boundary. |
| `BE_SECURITY_THREAT_MODEL.md` | **CURRENT** | Production Steam + private Friends proof + authority/transfer/proxy threat model. |

### Runtime/adapters

| Document | Status | Current note |
|---|---|---|
| `ADAPTER_RUNTIME_ROADMAP.md` | **CURRENT** | Implemented runtime, automatic Join vs presentation-only direct connect, current Factorio/Palworld boundaries. |
| `ADAPTER_GUIDE.md` | **CURRENT** | Stable adapter construction rules; current matrix delegated to Platform Status. |
| `NATIVE_WORLD_CREATION.md` | **CURRENT** | Factorio current implementation; other games remain evidence-gated. |
| `FACTORIO.md` | **CURRENT** | Explicit interface Host path, UDP 34197, no AutomaticHostStop. |
| `PALWORLD.md` | **CURRENT** | Read-only WorldOption/runtime REST/process-tree lifecycle. |

### Persistence/recovery/engineering

| Document | Status | Current note |
|---|---|---|
| `STORAGE.md` | **CURRENT** | Local + shared storage/transfer/retention implemented. |
| `PERSISTENCE_COMPATIBILITY.md` | **CURRENT** | Integrity schemas, state payload binding, storage identity, device settings v1->v2. |
| `WORKSPACE_RECOVERY.md` | **CURRENT** | Durable recovery/interrupted-session contract. |
| `ENGINEERING.md` | **CURRENT** | Engineering/bounds/documentation rules. |
| `ERROR_HANDLING.md` | **CURRENT** | Conservative failure/retry/cancellation semantics. |

## Historical checkpoint classification

The following documents remain historical evidence. Their old “current/next” language does not override active contracts.

### Planning/sign-off history

- `UI0_SIGNOFF_CHECKLIST.md`
- `BE0_SIGNOFF_CHECKLIST.md`
- `AR0_SIGNOFF_CHECKLIST.md`
- `BE1_LOCAL_SIMULATION.md`

### Implementation checkpoint history

- `E1_STATUS.md`
- `BE2_STATUS.md`
- `BE3_S3_CHECKPOINT.md`
- `BE4_STATUS.md`
- `BE5_STATUS.md`
- `E4_DESKTOP_STATUS.md`
- `E6_STATUS.md`
- `E8_STATUS.md`
- `V2_FRIENDS_BUILD.md` as the completed deterministic V2 stage

These files are intentionally retained for proof, failure history, and provenance.

## Known stale historical statements

Historical does not mean every sentence remains technically current.

Examples that must not override current code/contracts include:
- E4-era environment-variable-centered production Desktop configuration before package-owned V3 Steam release configuration;
- old Factorio prose saying direct `factorio --host` is the active interface path;
- “BE-3 is active”;
- “next phase is E2/E4/E8”;
- “four production adapters”;
- “guided manual Join”;
- “implement installer/updater”;
- old pre-#147 fixed-sidebar UI descriptions.

Use current active docs/code instead.

## Stable rules not changed by reconciliation

- one current valid World state;
- at most one writable Steward authority;
- immutable published revisions;
- canonical head advances last;
- no generic save merge/branch model;
- no permanent Steward game-server fleet;
- no second social/account platform;
- Steam owns Steward distribution/update;
- adapters own game-specific evidence;
- catalog registration does not grant actions;
- unresolved responsibility blocks conflicting writable work;
- failure preserves previous valid state and recovery evidence;
- provider/object storage does not become canonical authority;
- empirical uncertainty freezes only the dependent claim;
- remove/delegate problems before adding another Steward subsystem.

## Next implementation boundary

There is no known deterministic first-release product discrepancy to implement merely to remain busy.

The next work is empirical:

```text
exact qualified Windows build
-> continue V3-E real-machine acceptance
-> record first concrete failure or successful evidence
-> fix only a demonstrated owning boundary
-> then run V3-F real Steam/provider/game gate
```

Do not create a new generic feature, status model, social layer, deletion workflow, networking subsystem, or release tier until real evidence demonstrates that it is required.
