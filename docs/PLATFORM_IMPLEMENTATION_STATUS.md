# Platform Implementation Status

Status: **PLATFORM CODE/CI COMPLETE — V3 STEAM RELEASE CANDIDATE IS THE ACTIVE PRODUCT GOAL; RELEASE EMPIRICAL ACCEPTANCE REMAINS OPEN.**

This checkpoint records the stable generic Steward platform boundary and the current qualified release-candidate line. It does **not** claim that real-machine, real-provider, real-Steam, or final game-release acceptance has happened. Those evidence gates remain separate and must not be replaced with CI claims.

## Canonical platform-completion checkpoint

The generic platform-completion code slice remains PR #72:

> `9db4765948e3b69b4d07bc1442d7a1be5c2e3fc7`

On that exact head all five repository workflows were green:

- General CI;
- Palworld read-only CI;
- 7 Days to Die adapter CI;
- Project Zomboid adapter CI;
- Windows acceptance package.

PR #72 created the explicit `DesktopGameAdapterCatalog` composition point and proved that adding an adapter to the commercial Games/Import shell does not grant unsupported lifecycle capabilities.

That generic platform boundary remains frozen unless concrete evidence demonstrates a missing universal contract or defect.

## Current qualified product line

The latest qualified product line is V3-C / PR #135:

> `b68a527281f1026d06d76e4cfc5cde03c2227557`

On that exact head all five top-level workflow groups are green:

- CI — Ubuntu build/test, Windows build/test, Quality, PostgreSQL integration, S3-compatible integration, backend container;
- Windows acceptance package, including exact V3 Steam depot-content staging, client/backend public Steam identity matching, and the existing V2 Friends Build path;
- Palworld read-only CI;
- 7 Days to Die adapter CI;
- Project Zomboid adapter CI.

The current V3 deterministic release shape qualified through that line includes:

- V3-A package-owned non-secret Steam release configuration;
- V3-B exact self-contained Release `win-x64` depot content with external size/SHA-256 evidence;
- V3-C one-source client/backend public Steam AppID + Web API identity with publisher credentials remaining backend-secret and Friends Build explicitly disabled for production release evidence.

The real Steam AppID/publisher/depot/install/ticket/two-machine proof remains external V3-F acceptance.

## Latest adapter-composition checkpoint

The latest adapter addition before the V2/V3 goal transition was Space Engineers PR #106:

> `90e3c700d842c0f1fabc61f3f3404043206380f2`

Space Engineers became the nineteenth first-party adapter. V2 and V3 work since then has changed product depth, packaging, deployment, and release boundaries without increasing adapter count.

The current executable `DesktopGameAdapterCatalog` therefore contains **19** first-party adapters.

## What "platform complete" means

For current release-candidate development, the generic Steward platform is considered complete while all of the following remain true:

1. Core owns only universal World/session/revision/recovery lifecycle behavior.
2. Backend owns authenticated shared authority, membership, immutable revision publication, one-writer reservation, idempotency, and durable persistence.
3. Infrastructure owns local/remote persistence, verified transfer/cache/materialization, diagnostics, and recovery support without game semantics.
4. Desktop owns one commercial capability-driven Games/Worlds/Import/Share/Join/recovery/tray surface rather than one UI implementation per game.
5. Game-specific discovery, package shape, environment rules, launch, readiness, session ownership, safe stop, capture timing, and identity limitations remain behind `IGameAdapter`.
6. Unsupported adapter behavior remains unavailable through capability flags instead of being simulated in Core or UI.
7. Deterministic trust boundaries remain bounded and fail closed where Steward owns the bytes/state.
8. The exact integrated head passes the full five-workflow matrix.

The platform is **not** reopened merely because another abstraction, security wrapper, UI polish pass, release-tier enum, or generic feature could be imagined.

Reopen generic platform implementation only when at least one of these is true:

- a concrete generic correctness/security/recovery defect is demonstrated;
- a real adapter proves a genuinely universal contract is missing;
- a release product requirement cannot be implemented through the existing generic contracts;
- deterministic CI exposes a generic defect;
- deferred real-system acceptance exposes a specific platform failure.

Otherwise keep the platform stable.

## Current first-party adapter composition

Desktop has one explicit first-party composition point: `DesktopGameAdapterCatalog`.

Catalog registration means only that Steward includes the adapter's proven discovery/environment/state behavior. It is **not** a blanket promise of Start, Host, Join, Stop, mod reproduction, or complete multiplayer support.

| Adapter | Current proven product role |
|---|---|
| Factorio | Mature import/environment/state path plus automatic local launch, Host, Join, native creation, exact game version, and mod support; final Internet Host/Join/safe-handoff release evidence remains empirical. |
| Palworld | Dedicated-server/World lifecycle with automatic Host + Host Stop + exact game version; automatic client Join is not advertised and real Internet/native Join + handoff remains empirical. |
| 7 Days to Die | Discovery/import/environment/state + mods/exact game version; automatic Host/Stop/Join remains unavailable pending the recorded V3 server lifecycle evidence. |
| Project Zomboid | Discovery/import/environment/state + mods/exact game/exact mod versions; managed runtime capability remains empirical/frozen. |
| Terraria | Local vanilla `.wld` discovery/import, exact Steam build identity, opaque capture/restore; Steam Cloud, tModLoader, Start/Host/Stop/Join unsupported. |
| Stardew Valley | Host-owned local vanilla two-file save discovery/import/capture/restore; SMAPI/mod reproduction and Start/Host/Stop/Join unsupported. |
| Necesse | Local vanilla compressed-World ZIP state/import; uncompressed Worlds, mods and Start/Host/Stop/Join unsupported. |
| Core Keeper | Local vanilla slot World bundle state/import; character/map state, mods and Start/Host/Stop/Join unsupported. |
| The Planet Crafter | Local vanilla opaque `.json` World state/import; known modded environments and Start/Host/Stop/Join unsupported. |
| Satisfactory | Steam-profile vanilla `.sav` state/import; non-Steam profiles, modded environments and Start/Host/Stop/Join unsupported. |
| ASTRONEER | Local vanilla `.savegame` state/import; adjacent account/custom-game state, mods and Start/Host/Stop/Join unsupported. |
| Enshrouded | Local vanilla indexed current-World projection with bounded selector parsing; recovery generations/mods and Start/Host/Stop/Join unsupported. |
| Conan Exiles Enhanced | Local vanilla slot SQLite World state/import with transient-sidecar refusal; live snapshot/modded/dedicated lifecycle and Start/Host/Stop/Join unsupported. |
| Raft | Local vanilla current World state/import with player/inventory/backup exclusion; mod reproduction and Start/Host/Stop/Join unsupported. |
| ICARUS | Local vanilla Prospect state/import with account/meta-inventory/backup exclusion; Paks mods and Start/Host/Stop/Join unsupported. |
| Smalland | Local vanilla `.wld` World state/import with player/map-state separation and extra-Pak refusal; Start/Host/Stop/Join unsupported. |
| Abiotic Factor | Local vanilla `Worlds/<World>` subtree state/import with profile-scope separation and UE4SS refusal; Start/Host/Stop/Join unsupported. |
| V Rising | Local non-cloud vanilla current-session projection using latest autosave + gameplay/session metadata; CloudSaves/mod reproduction/dedicated lifecycle and Start/Host/Stop/Join unsupported. |
| Space Engineers | Local vanilla SteamID64-profile World-directory state/import with native Backup exclusion and bounded transactional restore; Start/Host/Stop/Join unsupported. |

## Capability claims are action-specific

`GameAdapterCapabilities` remains the executable source of truth.

Release/user-facing claims map directly to current capability/evidence:

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

The Desktop already applies these flags when enabling actions. Registration cannot silently grant play behavior.

Do **not** add a second `ReleaseSupported`, maturity tier, marketing tier, or release-capability model. If release material needs to describe a game, describe the exact proven slice. A statement such as "full multiplayer support for all 19 games" would be false.

## Normal path for adding or deepening a game

A new game or deeper capability should:

1. implement/prove the game-owned discovery/environment/state or lifecycle behavior inside its adapter;
2. preserve native source state during import;
3. expose only capabilities backed by deterministic or empirical evidence;
4. add adapter-specific tests/CI evidence;
5. update Desktop composition only when introducing a genuinely new adapter;
6. independently qualify the adapter head;
7. integrate on the current product line;
8. rerun the full five-workflow matrix on the exact combined SHA.

A game must **not** add game-name branches to Core, backend, Infrastructure, or normal Desktop behavior. If a game is unusual, keep the unusual behavior inside its adapter unless evidence proves a universal contract is missing.

## What remains outside deterministic platform/release implementation

### V2/E4-A — real deployment/Friends evidence

Still external:

- create the disposable EU provider resources/DNS/secrets;
- deploy the actual Backend.Api container against real PostgreSQL + S3-compatible object storage;
- prove HTTPS/readiness/restart/logging/transfer/backup/restore behavior;
- execute the recorded Friends Build real-machine acceptance batch.

Qualified #131 already defines the first small deployment topology; absence of cloud-account access is an external resource boundary, not a reason to add another Steward deployment subsystem.

### V3-E — real Windows release acceptance

Still external/empirical:

- clean Steam installation/launch;
- keyboard/focus traversal;
- Narrator/UI Automation observation;
- mixed-DPI monitor transitions;
- tray/background behavior on real Windows;
- representative real package/capture/restore/transfer timings;
- long real game sessions.

### V3-F — real Steam production acceptance

Still external/empirical:

- real Steward Steam AppID;
- publisher credential injected only into Backend.Api;
- real depot identity/SteamPipe upload;
- Steam installation/update behavior;
- real Web API tickets verified by the deployed backend;
- two Steam accounts/installations;
- real shared-World handoff;
- advertised Host/Join behavior at the real game/network boundary.

There is no production authentication bypass and no reason to invent placeholder Steam infrastructure to make these external values look complete.

## Relationship to older status documents

Older E6/E8 status files preserve useful historical evidence. Their old checkpoint statements are historical where this file or the active V3 specification supersedes them.

The current ancestry includes:

- E6 commercial UI completion;
- E8 deterministic hardening;
- platform composition #72;
- post-platform state-adapter expansion through Space Engineers #106;
- V2 Friends Build product/deployment preparation through #131;
- V3 release-candidate goal #132;
- V3-A release configuration #133;
- V3-B exact Steam depot content #134;
- V3-C matched production public Steam auth configuration #135.

Use `V3_STEAM_RELEASE_CANDIDATE.md` for the active release goal, this file for the stable platform/product checkpoint, and the adapter-specific documents/PR evidence for game-level details.

## Development rule from here

> **Platform stable. Release claims are capability/evidence-driven. Remove release blockers; do not grow Core/UI to accommodate speculation.**

When work reaches an empirical uncertainty that cannot be settled in CI, record/freeze the exact test and move to another independent deterministic path.

When a problem can be eliminated rather than solved, eliminate it.
