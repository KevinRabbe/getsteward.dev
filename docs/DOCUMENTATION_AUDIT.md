# Documentation State Audit

Status: **RECONCILED — ACTIVE DOCUMENTATION MATCHES THE QUALIFIED PRODUCT/PLATFORM STATE; ONE KNOWN UI IMPLEMENTATION DRIFT REMAINS.**

Executable/product baseline used for the audit:

> `e63c6f7d20d103cd2ea3d9a922b73de3c3ba1f5f` — PR #138, first evidence-driven V3-E Windows defect/fix

Documentation-only reconciliation stack:

```text
#139 documentation-state audit / Factorio interface correction
-> #140 root README
-> #141 V3 + platform status
-> #142 master roadmap
-> #143 architecture + storage
-> #144 backend contracts
-> #145 runtime/adapters/native creation/persistence
-> final authority/index cleanup
```

Documentation-only commits do not redefine executable product ancestry.

## Result

The audit found that Steward's **core product architecture was not fundamentally stale**. Most drift came from old milestone/status prose continuing to sound current after the implementation advanced.

The reconciliation therefore followed this rule:

```text
current contract still correct
-> keep it

current document contains stale implementation/status detail
-> reconcile it to executable truth

old milestone proof still useful
-> keep as HISTORICAL CHECKPOINT

old active-sounding statement conflicts with current code
-> explicitly mark it non-authoritative
```

The one remaining intentional docs↔implementation mismatch is the Games Library information architecture. In that case the **documented UI contract is current and the WPF implementation is the drift**.

## Audit method

When prose and production code disagree, inspect the actual contract callers use before changing either side.

This mattered during the audit for Factorio.

A first pass saw the simpler public `FactorioAdapter.LaunchHostAsync` method plus an old E4 status paragraph and incorrectly concluded the dedicated-server/RCON path was inactive.

The deeper code check found:

```text
FactorioAdapter : IGameAdapter
explicit IGameAdapter.LaunchHostAsync
-> LaunchAuthoritativeHostAsync
-> dedicated Factorio server
-> authenticated loopback RCON readiness
-> normal graphical host client
```

Core/Desktop consume adapters as `IGameAdapter`, so that explicit interface implementation is the product path.

The audit was corrected rather than forcing current code toward the stale E4 checkpoint description.

## Current executable/product facts

- deterministic V3 release preparation is complete through qualified #137;
- real V3-E Windows acceptance has started;
- #138 fixed the first real V3-E defect: Windows v1 -> v2 device-settings migration held the source read handle open during atomic replacement;
- Desktop contains 19 first-party adapters;
- `GameAdapterCapabilities` owns Start/Host/Join/Stop/Create action claims;
- shared Backend.Api/PostgreSQL/S3/remote Desktop authority is implemented;
- production Steam config is package-owned; engineering `STEWARD_*` configuration remains an acceptance/development source;
- Steam owns Steward installation/update;
- Factorio active Host path is dedicated server + RCON + graphical host client on managed UDP 34197; no `AutomaticHostStop` flag;
- Palworld advertises Host + Host Stop + exact game version, not automatic Join;
- Palworld manual direct-connect guidance is presentation-only, not a guided-manual Steward client lifecycle;
- 7DTD/PZ managed runtime remains frozen behind recorded empirical gates;
- Factorio is the current `NativeWorldCreation` adapter;
- local World/recovery persistence uses integrity-protected schemas; state revision v4 binds payload SHA inside protected metadata; device settings have a separate v1 -> v2 migration contract.

## Active documentation classification

### Product/architecture authority

| Document | Status | Current note |
|---|---|---|
| `NON_NEGOTIABLE_RULES.md` | **CURRENT** | Product/safety boundaries. |
| `PRODUCT_BOUNDARY.md` | **CURRENT** | Product definition/non-goals. |
| `DECISIONS.md` | **CURRENT** | Active durable decisions only; completed sequencing/planning lock removed. |
| `ARCHITECTURE.md` | **CURRENT** | Local + remote storage/coordination/backend authority now described as implemented. |
| `DOMAIN_MODEL.md` | **CURRENT** | Active World/revision/session/recovery model. |
| `WORLD_LIFECYCLE.md` | **CURRENT** | Generic handoff ordering remains aligned. |
| `CROSS_WORKSTREAM_CONTRACT.md` | **CURRENT** | UI/backend/runtime state/action authority mapping. |

### Current execution/release

| Document | Status | Current note |
|---|---|---|
| `ROADMAP.md` | **CURRENT** | Current sequence: docs -> Games Library -> V3-E -> V3-F -> measurements. Historical E stages retained only as provenance. |
| `V3_STEAM_RELEASE_CANDIDATE.md` | **CURRENT** | Correct final #137 SHA + #138 first real V3-E evidence. |
| `PLATFORM_IMPLEMENTATION_STATUS.md` | **CURRENT** | Separates generic platform-completion checkpoint from current #138 product line; 19-adapter matrix current. |
| `STEAM_RELEASE_GATE.md` | **CURRENT EXTERNAL RUNBOOK** | Real V3-E/V3-F provider/Steam/Windows/game batch. |
| `DEFERRED_EMPIRICAL_TESTS.md` | **CURRENT EMPIRICAL REGISTRY** | Exact unproven real-system questions; individual defects may be closed in later evidence PRs without invalidating remaining questions. |
| `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` | **CURRENT EXTERNAL RUNBOOK** | Qualified Instance + Caddy + exact-one-proxy first disposable topology. |

### UI

| Document | Status | Current note |
|---|---|---|
| `UI_ROADMAP.md` | **CURRENT CONTRACT — IMPLEMENTATION DRIFT REMAINS** | Approved Games Library -> game workspace -> per-game Worlds hierarchy. |

The current WPF shell still uses a fixed World-list sidebar + compact game selector + permanent detail pane.

**Decision:** UI contract wins. This is the next product implementation reconciliation.

### Backend

| Document | Status | Current note |
|---|---|---|
| `BACKEND_ROADMAP.md` | **CURRENT** | BE-1..5 treated as implemented history; current authority/external gates described. |
| `BE_API_CONTRACT.md` | **CURRENT** | Implemented `/api/v1` route families/results/idempotency/direct transfer. |
| `BE_SCHEMA_AND_LIFECYCLE.md` | **CURRENT** | Implemented PostgreSQL logical stores/transactions/retention. |
| `BE_PROVIDER_EVALUATION.md` | **CURRENT** | Criteria + qualified first Instance/Caddy topology; final vendor still open. |
| `BE_OPERATIONS_RUNBOOK.md` | **CURRENT** | Current deterministic vs real operational/restore boundary. |
| `BE_SECURITY_THREAT_MODEL.md` | **CURRENT** | Production Steam + private Friends proof + authority/transfer/proxy threat model. |

### Runtime/adapters

| Document | Status | Current note |
|---|---|---|
| `ADAPTER_RUNTIME_ROADMAP.md` | **CURRENT** | Implemented runtime, automatic Join vs presentation-only direct connect, current Factorio/Palworld boundaries. |
| `ADAPTER_GUIDE.md` | **CURRENT** | Stable adapter construction rules; current detailed per-game matrix delegated to Platform Status. |
| `NATIVE_WORLD_CREATION.md` | **CURRENT** | Factorio current implementation; other games remain unadvertised evidence-gated candidates. |
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

The following documents intentionally remain historical evidence. Their old “current/next” language does **not** override active contracts:

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

These files are not deleted because they contain useful proof, failure history, and implementation provenance.

## Known stale historical statements

Historical does not mean every sentence remains technically current.

### `E4_DESKTOP_STATUS.md`

Two sections are explicitly superseded:

1. its production Desktop configuration centered on three `STEWARD_*` environment values predates V3 package-owned `steward-steam-release.json`;
2. its Factorio “current truth” paragraph says direct `factorio --host` is active and the dedicated-server/RCON code is inactive. Current explicit `IGameAdapter.LaunchHostAsync` proves the opposite.

Use current V3/Factorio/runtime documentation and code, not those historical paragraphs.

### Other old checkpoint next-step statements

Old statements such as:

- “BE-3 is active”;
- “next phase is E2/E4/E8”;
- “four production adapters”;
- “guided manual Join”; 
- “implement installer/updater”;

are historical sequencing/evidence, not current work.

The active documents listed above have been reconciled so these statements no longer need to be interpreted by guesswork.

## Reconciled differences

The audit originally found these docs↔code differences:

| Area | Result after reconciliation |
|---|---|
| root README four-adapter/E4-era state | **reconciled** |
| V3/platform status stopped before #138 | **reconciled** |
| roadmap old E-stage active queue | **reconciled** |
| guided-manual Join in active planning | **reconciled/removed** |
| architecture/storage called shared backend future | **reconciled** |
| backend said BE-3 active | **reconciled** |
| API docs used proposed `/v1` routes | **reconciled to implemented `/api/v1`** |
| provider evaluation still chose Serverless first | **reconciled to qualified Instance + Caddy candidate** |
| adapter guide said 18 adapters | **reconciled to 19; matrix de-duplicated** |
| Factorio host path confusion | **reconciled from explicit interface implementation** |
| native creation framed as V2 future breadth | **reconciled; Factorio current, others evidence-gated** |
| persistence doc predated integrity/device-settings migration | **reconciled** |
| backend operations/security still planning/BE-2 wording | **reconciled** |
| active decisions still contained completed sequencing/planning lock | **reconciled** |
| Games Library docs vs current WPF layout | **INTENTIONALLY OPEN — implementation drift is next** |

## Stable rules that were not changed by cleanup

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

Documentation reconciliation is complete enough to stop being the blocker.

The next deterministic product discrepancy is now unambiguous:

> **Bring the WPF Desktop navigation back to the approved Games Library -> game workspace -> Worlds -> selected World hierarchy without changing backend/runtime authority or inventing a UI-only truth model.**

After that, continue evidence-driven V3-E real Windows acceptance and eventually the real V3-F release gate.