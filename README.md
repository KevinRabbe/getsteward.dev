# Steward

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward is a commercial Windows desktop product that moves the latest valid game World between players/devices without keeping a Steward-owned game-server fleet running permanently.

Steam is the commercial platform. Games are adapters. Worlds are the product.

## Current project state

The generic platform/backend/runtime architecture is implemented and CI-proven. Deterministic V3 Steam release-candidate preparation is complete through qualified PR #137.

The current qualified executable/product baseline is PR #138:

> `e63c6f7d20d103cd2ea3d9a922b73de3c3ba1f5f`

#138 came from the first real V3-E Windows run: an older v1 device-settings file exposed a Windows file-replacement defect during v1 -> v2 migration. The migration now closes its read handle before atomic replacement and has a Windows-relevant regression test.

The current documentation-state audit is PR #139. Use [`docs/DOCUMENTATION_AUDIT.md`](docs/DOCUMENTATION_AUDIT.md) when an older roadmap/status file disagrees with executable truth.

What remains before release acceptance is deliberately real rather than another generic subsystem:

- finish reconciling stale active documentation;
- reconcile the known Games Library navigation drift with the already-approved game-first UI contract;
- continue V3-E real Windows observation;
- open V3-F only with real provider/Steam/AppID/publisher/depot/two-installation/game evidence.

Steward does not manufacture CI substitutes for evidence that only a real Windows/Steam/game/network boundary can provide.

## Product boundary

Steward owns the complete World handoff:

```text
latest valid World state
-> reserve one writable session
-> prepare the correct environment
-> restore the World on this device
-> launch local play or temporary hosting
-> observe the game/server session
-> establish a safe capture boundary
-> capture the updated state
-> store and verify it
-> advance the shared World state last
-> make the World available to the next player
```

Launching alone is not success. Steward remains responsible in the background until capture, storage, verification, commit, cleanup, or recovery is resolved.

Steam and each game should continue owning what they already provide: game ownership/installations, Steam identity, Steam distribution/update, native multiplayer behavior, Workshop/native content tooling, and dedicated-server executables where applicable.

## Non-goals

Steward is not:

- a Git-style branching or merging system for saves;
- a universal save merger;
- a social network or Discord replacement;
- a complex ownership/party/governance platform;
- a public game-server browser;
- a permanent Steward game-server fleet;
- a live game-process migration system;
- a second Steam updater/distribution service;
- DRM for copies already received by an authorized device.

Groups organize themselves. Steward keeps the selected shared World state safe, current, portable, and playable.

## Core architecture

Universal lifecycle/state-safety behavior remains outside game-specific adapters.

```text
Windows Desktop / background runtime / development tools
                    |
                    v
             SharedWorlds.Core
              |      |      |
              |      |      `-> IWorldSessionCoordinator
              |      |            |-- local reservation
              |      |            `-- distributed backend reservation
              |      |
              |      `-> IWorldStorage
              |            |-- local filesystem
              |            `-- authenticated remote World storage
              |
              `-> IGameAdapter
                     `-- 19 first-party adapters with independent capability sets

Windows Desktop
    |
    `-> HTTPS Backend.Api
            |-- PostgreSQL: identity/access/revisions/reservations/idempotency
            `-- private S3-compatible object storage: immutable package bytes
```

Core must never gain game-name branches merely because one adapter is unusual. Backend/object storage also do not interpret game-save semantics.

Object storage moves opaque bytes; PostgreSQL/backend transactions decide shared authority and canonical heads.

## Essential invariants

- A World has one current valid state.
- At most one Steward session may write that World at a time.
- Published revisions are immutable.
- The current World state advances only after the replacement is completely captured, stored, verified, and committed.
- A failed operation leaves the previous valid state authoritative.
- Post-launch workspaces are recovery assets until success is certain.
- Session observation/readiness/safe-capture timing are adapter-owned.
- Host switching happens between sessions through safe completion, capture, commit, restore, and launch.
- Generic save merging and Git-style branches are not supported.
- Catalog registration never grants Start/Host/Join/Stop/Create behavior; capability/evidence does.
- Timers may create uncertainty but may never manufacture a second writer.

See [Non-Negotiable Rules](docs/NON_NEGOTIABLE_RULES.md) for the complete rule set.

## Current implementation

### Shared lifecycle and backend

Steward currently contains:

- generic local/shared World lifecycle orchestration;
- isolated environment preparation and verification;
- immutable state/environment revisions;
- expected-head canonical commits;
- local and distributed one-writer coordination;
- durable recovery journals and interrupted-session handling;
- authenticated shared World metadata/access;
- PostgreSQL-backed sessions/access/revisions/reservations/idempotency;
- resumable direct S3-compatible object transfer;
- verified local package cache/materialization;
- short-lived Host presence for read-only Join;
- bounded cleanup/retention and redacted diagnostics.

The canonical state transaction remains:

```text
capture candidate
-> store immutable bytes/metadata
-> verify durable result
-> validate expected head + valid reservation generation
-> advance canonical head atomically
-> release/finalize only after authority is known
```

A stale or invalidated writer cannot overwrite a newer head.

### First-party adapter catalog

`DesktopGameAdapterCatalog` currently composes **19** first-party adapters:

1. Factorio
2. Palworld
3. 7 Days to Die
4. Project Zomboid
5. Terraria
6. Stardew Valley
7. Necesse
8. Core Keeper
9. The Planet Crafter
10. Satisfactory
11. ASTRONEER
12. Enshrouded
13. Conan Exiles Enhanced
14. Raft
15. ICARUS
16. Smalland
17. Abiotic Factor
18. V Rising
19. Space Engineers

This is a catalog, not a promise that all 19 games support every action.

`GameAdapterCapabilities` is the executable action boundary:

```text
Start World -> AutomaticLocalLaunch
Host World  -> AutomaticHostLaunch
Join        -> AutomaticClientJoin + current join capability result
Stop/Save   -> AutomaticHostStop
Create      -> NativeWorldCreation
```

The detailed current adapter/capability table lives in [Platform Implementation Status](docs/PLATFORM_IMPLEMENTATION_STATUS.md).

### Factorio

Factorio currently exposes the broadest automatic action set:

- local Start;
- managed Host;
- automatic Join;
- native World creation;
- mod/environment handling;
- exact game version.

Its active `IGameAdapter` Host implementation is the authoritative dedicated-server path, not the simpler public helper path:

```text
private dedicated Factorio server
-> UDP 34197 for the managed game endpoint
-> ephemeral loopback RCON
-> authenticated RCON readiness
-> normal graphical host client joins locally
-> host-client session ends
-> /server-save
-> observed save refresh
-> server process ends
-> capture/commit
```

Factorio does **not** currently advertise `AutomaticHostStop`. Real Internet reachability, Join, final server-end/capture safety, and cross-device handoff remain release evidence rather than inferred CI claims.

See [Factorio Adapter](docs/FACTORIO.md) and [Deferred Empirical Tests](docs/DEFERRED_EMPIRICAL_TESTS.md).

### Palworld

Palworld currently advertises automatic Host + Host Stop and exact game-version support.

The managed lifecycle uses the real dedicated server and localhost REST control. `WorldOption.sav` remains canonical read-only input: Steward does not patch, rewrite, or re-encode it merely to control a hosted session.

Automatic client Join is not currently advertised. Player identity differences between local/co-op and dedicated-server representations remain a Palworld-specific limitation.

See [Palworld Adapter](docs/PALWORLD.md).

### 7 Days to Die

7 Days to Die has the proven discovery/environment/state slice, including current V3 World + generated-terrain handling and exact opaque `SandboxCode` as an explicit World-specific reproduction input.

Managed Host/Stop/Join is intentionally still unavailable until the recorded real V3 lifecycle trace establishes readiness, minimum local `shutdown` framing, clean long-lived-process exit, final-save completion, and relaunch from captured state.

### Project Zomboid

Project Zomboid has the proven discovery/environment/state slice including dedicated-server build and Workshop identity boundaries.

Managed runtime remains frozen until an isolated real dedicated-server lifecycle proves launch ownership, safe shutdown, capture, and relaunch without using the player's live Zomboid tree as authoritative state.

### Remaining 15 adapters

The later adapters deliberately entered Steward with narrower truthful discovery/environment/state capabilities. They do not gain Start/Host/Join/Stop merely because they appear in the Games Library.

That is intentional platform behavior: **one generic UI can contain an adapter before every runtime capability for that game exists.**

## Desktop

The WPF Desktop uses one capability-driven adapter catalog and one shared lifecycle/access/recovery composition. Local-only and authenticated shared Worlds use different authority implementations behind the same product concepts.

The Desktop consumes:

- persistent recovery responsibility;
- local and remote World storage;
- shared World membership/access/invitations;
- exact-environment readiness;
- distributed reservation authority;
- direct package transfer;
- Host presence and read-only Join;
- tray/background lifecycle responsibility.

One known product/UI drift is now explicit: the approved first-release information architecture is **Games Library -> game workspace -> Worlds -> selected World details**, while current XAML still uses a fixed World-list sidebar with a compact game selector. The specification remains authoritative; UI reconciliation follows documentation cleanup rather than redefining the product around the current sidebar.

## V3 Steam release shape

Normal production Steam configuration is package-owned rather than dependent on developer environment variables.

A Steam release package contains an adjacent non-secret:

```text
steward-steam-release.json
```

with the expected HTTPS Backend.Api URL, Steward AppID, and `GetAuthTicketForWebApi` identity.

The Windows release pipeline already produces:

- exact self-contained Release `win-x64` depot content;
- external byte-size/SHA-256 evidence;
- matching non-secret backend AppID/Web API identity configuration;
- `FriendsBuild__Enabled=false` for production release evidence.

The publisher API key remains backend-secret and never belongs in depot content.

SteamPipe remains responsible for actual depot upload, BuildIDs, installation, updates, branches, and rollback/build selection.

## Remaining release gate

The remaining decisive proof is real end-to-end acceptance, not another mock backend:

```text
real provider deployment
+ real Steam AppID/publisher/depot
+ Steam-installed Steward on independent Windows PCs
+ genuine Steam tickets
+ real advertised game/network lifecycle
+ one-writer/capture/commit/recovery invariants
= release acceptance
```

For Factorio, the representative handoff remains:

```text
PC A gets canonical N
-> A Hosts / B Joins
-> gameplay changes World
-> A safely completes and commits N+1
-> PC B later acquires N+1 and commits N+2
-> PC A receives the returned current N+2
```

A competing writer must remain blocked throughout active or unresolved authority.

See [Steam Release Gate](docs/STEAM_RELEASE_GATE.md).

## Documentation

Start with:

- [Documentation Index](docs/README.md)
- [Documentation State Audit](docs/DOCUMENTATION_AUDIT.md)
- [V3 Steam Release Candidate](docs/V3_STEAM_RELEASE_CANDIDATE.md)
- [Steam Release Gate](docs/STEAM_RELEASE_GATE.md)

Authoritative product/architecture contracts:

- [Non-Negotiable Rules](docs/NON_NEGOTIABLE_RULES.md)
- [Product Boundary](docs/PRODUCT_BOUNDARY.md)
- [Design Decisions](docs/DECISIONS.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Domain Model](docs/DOMAIN_MODEL.md)
- [World Lifecycle](docs/WORLD_LIFECYCLE.md)
- [Cross-Workstream Contract](docs/CROSS_WORKSTREAM_CONTRACT.md)
- [UI Roadmap](docs/UI_ROADMAP.md)

Older milestone/status documents remain useful evidence where the documentation audit marks them historical, but their old "next step" statements are not current roadmap authority.

## Repository structure

```text
src/
  SharedWorlds.Core/
  SharedWorlds.Infrastructure/
  SharedWorlds.Backend/
  SharedWorlds.Backend.Api/
  SharedWorlds.Backend.PostgreSql/
  SharedWorlds.Backend.ObjectStorage.S3/
  SharedWorlds.Desktop/
  SharedWorlds.Cli/
  SharedWorlds.GameAdapters/
    <19 independent first-party game adapter projects>

tests/
  <Core/Infrastructure/Backend/Desktop/adapter tests>

tools/
  <focused probes, acceptance, packaging and validation utilities>

docs/
  <product contracts, current runbooks and historical evidence>
```

## Validation

```bash
dotnet restore SharedWorlds.sln
dotnet format SharedWorlds.sln --verify-no-changes --no-restore
dotnet build SharedWorlds.sln --configuration Release --no-restore
dotnet test SharedWorlds.sln --configuration Release --no-build
```

The repository uses nullable reference types, warnings as errors, deterministic builds, explicit persisted schemas/migrations, typed product failures, architecture tests, provider integration tests, and Windows/Linux CI where applicable.

## Working rule

Before adding a feature or abstraction, ask:

> **Does this directly help Steward move the latest valid World into a playable session and return the updated valid state for the next player?**

Then ask the stronger question:

> **Can Steward remove the problem, let Steam/the game/Windows own it, or make the invalid state impossible instead of building another subsystem?**

When the answer is yes, do less.