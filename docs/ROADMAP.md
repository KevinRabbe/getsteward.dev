# Master Roadmap

Status: **CURRENT — DETERMINISTIC FIRST-RELEASE PRODUCT/UI RECONCILIATION IS COMPLETE; V3-E REAL WINDOWS ACCEPTANCE IS ACTIVE; V3-F REMAINS THE FINAL EXTERNAL RELEASE GATE.**

## Product target

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward coordinates three implementation domains:

1. [UI and UX Roadmap](UI_ROADMAP.md)
2. [Backend Roadmap](BACKEND_ROADMAP.md)
3. [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md)

They implement one product model. No workstream may invent a competing definition of World, session, sharing, hosting, recovery, or authority.

## Current execution state

The generic product platform is implemented. Deterministic V3 Steam release-candidate preparation is complete through qualified PR #137.

The current non-documentation executable/UI baseline is PR #151:

> `01cb15927a9e056fb9a6784d037eabde66e7d819`

PR #153 then closes the remaining deterministic first-release product-contract/documentation reconciliation without reopening Core/backend/adapter authority. Before this final status cleanup, its latest documentation head `35e5f6464eeea113a8ab9ecf66b64f4055553803` had already passed all five top-level workflow groups.

The first real Windows V3-E run also produced useful evidence earlier: legacy device-settings migration failed because Windows would not atomically replace the settings file while Steward still held its source read handle open. #138 closes that read boundary before the migration write and regression-tests the fix.

That leaves one development mode:

```text
platform architecture complete
+ deterministic V3 release shape complete
+ deterministic first-release product/UI contract complete
+ real V3-E evidence has started

therefore

stop inventing generic subsystems
-> continue real V3-E observations
-> fix only concrete release defects that evidence exposes
-> open V3-F with the real Steam/provider resources
```

The final production Steam/AppID/publisher/depot/two-installation/game proof remains external and unclaimed.

## Current priority order

### 1. Deterministic documentation/product reconciliation — COMPLETE

The post-#138 reconciliation stack is closed:

```text
#139-#145 documentation-state reconciliation
-> #147 Games Library hierarchy
-> #148 small World Lobby
-> #149 existing-search audit + missing World sort + global Settings
-> #150 responsibility-backed game attention
-> #151 localization-ready fixed product vocabulary
-> #153 publish-first Share/access contract simplification
```

The approved first-release hierarchy is implemented:

```text
Games Library
-> select game
-> game workspace
-> Worlds for that game
-> selected World details/actions
```

Search was already present; only the missing sort was added. Global Settings reuses the existing device-settings owner. The lobby is a small read-only operational glance. Share publishes first and Manage access owns membership changes afterward. No pre-publication invitation staging, Steward friends graph, destructive shared-World delete workflow, or second UI-only state machine was added.

`DOCUMENTATION_AUDIT.md` is now the current classification of active vs historical documentation, not a temporary implementation queue.

There is no known deterministic first-release product discrepancy left to implement merely to remain busy.

### 2. V3-E — real Windows release acceptance — ACTIVE

Continue real-machine observations that CI cannot prove:

- normal release-package/Steam-installed startup path;
- keyboard/focus traversal;
- Narrator/UI Automation names and live regions;
- mixed-DPI monitor transitions;
- tray/background/Quit guards;
- restart/suspend/logoff interactions where relevant;
- actual startup/package/capture/restore/transfer timings;
- long real managed game sessions.

Workflow:

```text
observe real failure
-> identify the smallest owning boundary
-> fix only that boundary
-> add deterministic regression where possible
-> requalify exact integrated head
-> resume empirical run
```

#138 is the first completed example of this process.

Do not convert “might fail on Windows” into speculative product code.

### 3. V3-F — real Steam/provider/game release gate — EXTERNAL

Open this gate only when the product owner decides the release candidate is otherwise good enough to spend the external setup cost.

Required real inputs/evidence include:

- real Steward Steam AppID;
- real Windows depot identity;
- authorized Steamworks builder access;
- real publisher Web API credential kept server-side;
- one real `GetAuthTicketForWebApi` identity;
- real public HTTPS Backend.Api;
- real PostgreSQL;
- real private S3-compatible object storage;
- exact proxy trust matching the actual topology;
- at least two independent Windows/Steam installations/accounts;
- representative real Worlds for every advertised action/game claim.

One-pass acceptance:

```text
exact qualified depot content
-> SteamPipe upload
-> Steam installs candidate on PC A/B
-> actual runtime AppID matches packaged expected AppID
-> genuine Steam Web API tickets
-> deployed backend verifies identity
-> shared World/access works across installations
-> advertised Host/Join/game lifecycle succeeds
-> canonical capture/upload/commit/handoff succeeds
-> Windows V3-E observations are recorded in the same expensive setup
-> second qualified Steam build proves Steam-owned update behavior
```

No Friends Build fallback, static production token, fake publisher key, `steam_appid.txt`, sideload-only proof, or manual World copying may make this gate green.

Canonical runbook: `STEAM_RELEASE_GATE.md`.

### 4. Performance — ONLY FROM MEASUREMENTS

Do not optimize transfer/storage/runtime architecture merely because an optimization is imaginable.

After correctness and real measurements, candidates may include:

- immutable package deduplication;
- background prefetch;
- CDN/replication;
- compression tuning;
- chunk/delta reuse;
- peer-assisted transfer.

A measured bottleneck must justify the mechanism.

## Product boundaries

### UI/UX owns

- Games Library;
- per-game World workspace;
- import and native creation where supported;
- Start World / Host World / Join / Stop and Save presentation;
- Share World / Manage access / invitations;
- lifecycle/progress presentation;
- tray/background visibility;
- Connection required / Waiting to sync / Action required / Recovery needed / Interrupted session presentation.

UI never invents backend authority or game-specific lifecycle behavior.

### Backend owns

- verified external identity/session issuance for configured auth modes;
- production Steam-ticket verification when Steam credentials are configured;
- flat World membership + one Access Manager;
- immutable package publication/transfer authorization;
- canonical state/environment heads;
- one-writer reservation/generation;
- uncertainty/reclaim;
- expected-head commit;
- short-lived host-presence evidence bound to current authority;
- remote recovery/retention/security/operations boundaries.

Backend never interprets game saves or runs permanent game servers.

### Runtime owns

- generic Start/Host lifecycle orchestration;
- one active managed writable lifecycle per device;
- local/remote storage and reservation composition;
- verified package materialization/cache;
- durable recovery responsibility;
- read-only Join orchestration;
- tray/background responsibility.

### Adapters own

- installation and World discovery;
- environment facts/preparation;
- native World creation where proven;
- restore/capture package shape;
- local/host/client launch details;
- session/readiness evidence;
- safe host stop when advertised;
- safe capture timing;
- game-specific identity/environment limitations.

## Capability and action contract

Catalog registration does not grant gameplay behavior.

```text
Start World
    -> AutomaticLocalLaunch

Host World
    -> AutomaticHostLaunch

Join
    -> AutomaticClientJoin
       + current GetJoinCapabilityAsync result

Stop and Save
    -> AutomaticHostStop

Create World
    -> NativeWorldCreation
```

First-release Join is automatic-or-unavailable. The earlier guided-manual Join fallback was removed because Core had no truthful lifecycle for owning preparation, user-controlled manual play, completion, and cleanup. Do not resurrect it until a real adapter proves that complete lifecycle is needed.

## Current adapter depth relevant to release

### Factorio

Current advertised capability includes local Start, managed Host, automatic Join, native World creation, exact game version, and mod support.

The active interface Host path is:

```text
private dedicated Factorio server
-> UDP 34197 game endpoint
-> ephemeral loopback RCON
-> authenticated readiness
-> normal graphical host client
-> client ends
-> /server-save
-> observed save refresh
-> managed server process ends
-> capture/commit
```

`AutomaticHostStop` is not advertised. Real Internet reachability, real Join, final safe completion/capture, and cross-device handoff remain release evidence.

### Palworld

Current advertised capability includes automatic Host, automatic Host Stop, and exact game version.

The canonical `WorldOption.sav` remains read-only. Disposable runtime settings + localhost REST own management. Automatic client Join is not advertised.

Remaining release evidence includes real network/native Join behavior where required and complete handoff.

### 7 Days to Die

Discovery/environment/state is implemented. Exact opaque `SandboxCode` remains a World-specific reproduction input.

Automatic Host/Stop/Join stays frozen until the current real V3 server lifecycle proves:

- readiness;
- minimum loopback `shutdown` framing;
- long-lived process ownership/clean exit;
- final authoritative save completion;
- capture/relaunch of the same updated World.

### Project Zomboid

Discovery/environment/state is implemented, including dedicated-server and Workshop identity boundaries.

Managed runtime stays frozen until a real isolated dedicated-server lifecycle proves launch ownership, safe shutdown, capture, and relaunch without treating the player's live profile as Steward-owned session state.

### Remaining catalog adapters

The other fifteen first-party adapters intentionally provide narrower discovery/environment/state slices. They remain useful product support without invented Start/Host/Join/Stop behavior.

## Backend/platform foundation — IMPLEMENTED

The repository already contains:

- authenticated shared World metadata/access;
- PostgreSQL persistence;
- private S3-compatible immutable object transfer;
- resumable multipart upload/direct download;
- exact package size/hash verification;
- distributed reservation generations;
- heartbeat -> Uncertain -> deliberate reclaim;
- expected-head canonical commit;
- durable idempotency for ambiguous authority mutations;
- verified package cache/materialization;
- short-lived Host presence;
- deterministic local/remote recovery;
- bounded retention/cleanup;
- redacted diagnostics;
- real PostgreSQL backup/restore CI proof;
- synthetic large-transfer/endurance CI proof.

Do not describe those as future architecture.

Real provider deployment/operations remain empirical because no provider account/resource creation is available in the current tool environment. `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` defines the first disposable Instance + Caddy + PostgreSQL + S3 shape without broadening Steward's proxy trust model.

## Historical implementation milestones

The old E-stage sequence is preserved here only as provenance. It is not the active task queue.

| Historical stage | Proven outcome |
|---|---|
| P0 | UI/backend/runtime contracts and cross-workstream state/action model approved. |
| E1 | Provider-free authority simulation, generic runtime conformance, first commercial shell. |
| E2 | Real authenticated metadata/access + immutable transfer foundation. |
| E3 | PostgreSQL-backed one-writer authority and canonical commit. |
| E4 | Desktop remote composition, exact environment, recovery, access, Host presence, read-only Join. |
| E5 / BE-5 | Deterministic PC A -> PC B -> PC A handoff through production contracts/TestServer + real PostgreSQL. |
| E6 | Commercial UI/code accessibility composition; real Windows accessibility/DPI evidence remained deferred. |
| E8 | Deterministic hardening: integrity/bounds/provider backup/transfer/endurance/diagnostics/transport security. |
| V2 | Private Friends Build product/deployment preparation. |
| V3-A..D | Production release config, exact depot bytes, matched public backend identity, action-specific release claims. |
| #137 | Final deterministic V3-E/V3-F gate aligned with Steam-owned install/update. |
| #138 | First real V3-E Windows defect/fix. |
| #139-#145 | Active-documentation reconciliation to executable truth. |
| #147-#153 | Deterministic first-release UI/product reconciliation closed without new generic authority. |

Historical status files remain evidence. Their old “next phase” statements do not override this roadmap.

## Drift-control rules

### Evidence beats prose

Documentation must describe executable truth. When status prose conflicts with production code, inspect the actual contract used by callers before changing implementation.

Explicit interface implementations are part of that rule: a convenient public helper method is not automatically the product path if Core/Desktop call through a different interface implementation.

### Empirical uncertainty is accumulated, not serialized

```text
reach empirical uncertainty
-> record exact deferred test
-> freeze dependent capability/claim
-> continue independent deterministic work
-> batch expensive real tests later
```

A deferred uncertainty blocks only the claim that depends on it.

### Eliminate ownership before solving it

```text
need to solve X
-> discover Steam / Windows / the game already owns X
-> reuse the existing primitive
-> delete Steward-owned substitute work
-> stop caring about X
```

Examples already applied:

- Steam owns Steward installation/update; no self-updater.
- Palworld owns WorldOption serialization; no Steward WorldOption writer.
- 7DTD owns SandboxCode semantics; Steward stores/reuses it opaquely instead of decoding it.
- a missing required dedicated-server installation is useful negative evidence; Steward does not need a generic host-eligibility theory for that case.

### Do not preserve abandoned abstractions because work was invested

Delete obsolete probes, codecs, compatibility paths, or generic abstractions when the product no longer needs the problem they existed to solve.

### Capability claims require evidence

No game gets a release claim because it is registered in the catalog.

No empirical gate becomes “passed” because deterministic CI resembles it.

No unknown networking problem gets a traversal subsystem before the real failure is measured.

## Canonical current documents

For active execution use:

- `DOCUMENTATION_AUDIT.md` — current active-vs-historical document classification and reconciliation closure;
- `V3_STEAM_RELEASE_CANDIDATE.md` — current release-stage contract;
- `PLATFORM_IMPLEMENTATION_STATUS.md` — stable platform/current executable state;
- `UI_ROADMAP.md` — implemented deterministic UI hierarchy/state/action contract;
- `STEAM_RELEASE_GATE.md` — final real V3-E/V3-F acceptance batch;
- `DEFERRED_EMPIRICAL_TESTS.md` — exact unproven real-system questions.

Lower-level subsystem/adapter documents add detail but may not silently expand the product boundary.
