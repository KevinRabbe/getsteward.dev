# Platform Implementation Status

Status: **PLATFORM CODE/CI COMPLETE — NORMAL PRODUCT EXPANSION IS GAME-ADAPTER WORK. RELEASE EMPIRICAL ACCEPTANCE REMAINS OPEN.**

This checkpoint records the first point at which Steward's generic product shell, lifecycle/runtime contracts, backend/storage/authority stack, commercial Desktop surface, and deterministic hardening are composed on one exact all-workflows-green line.

It does **not** claim that real-machine, real-provider, real-Steam, or final release acceptance has happened. Those evidence gates remain separate and must not be replaced with CI claims.

## Canonical platform-completion checkpoint

The platform-completion code slice is PR #72:

> `9db4765948e3b69b4d07bc1442d7a1be5c2e3fc7`

On that exact head all five repository workflows are green:

- General CI;
- Palworld read-only CI;
- 7 Days to Die adapter CI;
- Project Zomboid adapter CI;
- Windows acceptance package.

The immediately preceding hardening/integration checkpoint is:

> `5d48b3d83956c12664be713341c4e538f56cd9c9`

It also passed all five workflows. PR #72 changes only Desktop adapter composition: it creates one first-party adapter catalog and makes all four then-existing adapters available to the generic Games/Import shell without changing any adapter capability.

## Latest qualified product line

Normal adapter expansion has continued without reopening the platform. The latest qualified product line is ASTRONEER PR #89:

> `cadb327810c6ac1b2db352dcb4cac19e08fc42ad`

On that exact head all five repository workflows are green again, including Quality, Ubuntu/Windows build-and-test, backend container, PostgreSQL, S3-compatible integration, and Windows acceptance.

The aggregate adapter counts on that line are:

- Factorio: 41/41;
- Palworld: 75/75;
- 7 Days to Die: 47/47;
- Project Zomboid: 94/94;
- Terraria: 8/8;
- Stardew Valley: 11/11;
- Necesse: 9/9;
- Core Keeper: 14/14;
- The Planet Crafter: 12/12;
- Satisfactory: 12/12;
- ASTRONEER: 12/12 on both Ubuntu and Windows.

PR #89 extends the same post-platform pattern already proven by Terraria, Stardew Valley, Necesse, Core Keeper, The Planet Crafter, and Satisfactory: one independent adapter project, game-owned discovery/environment/state behavior, truthful capability exposure, adapter-specific tests, solution/Desktop registration, and exact combined qualification. It does not modify Core, backend, Infrastructure, or generic Desktop lifecycle contracts.

ASTRONEER adds another identity distinction: the same game-managed directory can contain different persistence classes. Current `*.savegame` files are World state, while adjacent `*.savecfg` files represent account/custom-game configuration. Steward captures only the World persistence class instead of treating all nearby persistent files as one revision.

## What "platform complete" means

For current first-release development, the generic Steward platform is considered complete when all of the following remain true:

1. Core owns only universal World/session/revision/recovery lifecycle behavior.
2. Backend owns authenticated shared authority, membership, immutable revision publication, one-writer reservation, idempotency, and durable persistence.
3. Infrastructure owns local/remote persistence, verified transfer/cache/materialization, diagnostics, and recovery support without game semantics.
4. Desktop owns one commercial capability-driven Games/Worlds/Import/Share/Join/recovery/tray surface rather than one UI implementation per game.
5. Game-specific discovery, package shape, environment rules, launch, readiness, session ownership, safe stop, capture timing, and identity limitations remain behind `IGameAdapter`.
6. Unsupported adapter behavior remains unavailable through capability flags instead of being simulated in Core or UI.
7. Deterministic trust boundaries remain bounded and fail closed where Steward owns the bytes/state.
8. The exact integrated head passes the full five-workflow matrix.

The platform is **not** reopened merely because another abstraction, security wrapper, UI polish pass, or generic feature could be imagined.

Reopen generic platform implementation only when at least one of these is true:

- a concrete generic correctness/security/recovery defect is demonstrated;
- a real adapter proves a genuinely universal contract is missing;
- a first-release product requirement cannot be implemented through the existing generic contracts;
- deterministic CI exposes a generic defect;
- deferred real-system acceptance exposes a specific platform failure.

Otherwise keep the platform stable.

## Current first-party adapter composition

Desktop has one explicit first-party composition point, `DesktopGameAdapterCatalog`.

Current adapters:

| Adapter | Current product role |
|---|---|
| Factorio | mature local/host/shared lifecycle path; remaining work is adapter/release evidence where still recorded |
| Palworld | dedicated-server/World lifecycle path with explicit game-specific limitations; remaining work is adapter/release evidence where still recorded |
| 7 Days to Die | discovery/import/environment/state support; launch/hosting remains unavailable until its adapter proves truthful runtime semantics |
| Project Zomboid | discovery/import/environment/state support; launch/hosting remains unavailable until its adapter proves truthful runtime semantics |
| Terraria | local vanilla `.wld` discovery/import, exact Steam build identity, opaque capture/restore, and owned workspace handling; Steam Cloud, tModLoader, launch, Host, Stop, and Join remain unsupported |
| Stardew Valley | host-owned local vanilla save discovery/import, exact Steam build identity, exact two-file current-state capture/restore, and owned workspace handling; SMAPI/modded environments, launch, Host, Stop, and Join remain unsupported |
| Necesse | local vanilla compressed-World ZIP discovery/import, exact Steam build identity, raw byte-preserving capture/restore, and owned workspace handling; uncompressed `-zipsaves 0` Worlds, mods, launch, Host, Stop, and Join remain unsupported |
| Core Keeper | local vanilla slot-based World discovery/import, exact Steam build identity, exact three-file World-owned capture/restore, and owned workspace handling; character/map state, mods, launch, Host, Stop, and Join remain unsupported |
| The Planet Crafter | local vanilla non-empty `.json` World discovery/import, exact Steam build identity, raw byte-preserving capture/restore, and owned workspace handling; `Backup.json`, BepInEx environments, launch, Host, Stop, and Join remain unsupported |
| Satisfactory | Steam-profile-only vanilla non-empty `.sav` World discovery/import, exact Steam build identity, raw byte-preserving capture/restore, and owned workspace handling; non-Steam profiles, backup/blueprint trees, modded environments, launch, Host, Stop, and Join remain unsupported |
| ASTRONEER | local vanilla non-empty `.savegame` World discovery/import, exact Steam build identity, raw byte-preserving capture/restore, and owned workspace handling; adjacent `.savecfg` account/custom-game state, modded environments, launch, Host, Stop, and Join remain unsupported |

Registration does not grant capabilities. The Desktop reads each adapter's `GameAdapterCapabilities`; adding an adapter to the catalog cannot silently make Start, Host, Join, or Stop available.

Terraria, Stardew Valley, Necesse, Core Keeper, The Planet Crafter, Satisfactory, and ASTRONEER currently advertise only `ExactGameVersion`. Their state-only entries are deliberate evidence that adapters can join the product before launch/hosting semantics are proven.

Stardew Valley refuses a detected SMAPI or non-empty Mods environment rather than recording an incomplete vanilla `EnvironmentManifest` for a modded installation. Necesse applies the same principle to its local mods directory. Core Keeper applies it across manual install Mods, Steam Workshop content, and per-profile Mods. The Planet Crafter refuses known BepInEx bootstrap markers rather than enumerating or pretending to reproduce individual mods. Satisfactory refuses linked/non-empty `FactoryGame/Mods` and Steam Workshop content; this also catches an installed SML environment without requiring Steward to understand individual mods. ASTRONEER refuses linked/non-empty `Saved/Mods` or `Saved/Paks` rather than claiming a vanilla environment while mod-integration content is present.

Necesse demonstrates a simple state rule: when the game's native current World artifact is already a portable ZIP, Steward preserves those bytes directly instead of unpacking and rebuilding a second archive format.

Core Keeper demonstrates the complementary ownership rule: storage adjacency is not identity. Character saves and player exploration maps are excluded even though they live in the same game-managed profile tree as the World files.

The Planet Crafter demonstrates the same restraint for an opaque native file: Steward does not need to understand the internal save grammar to preserve, transfer, restore, and verify the exact bytes it owns.

Satisfactory demonstrates that the source platform is also part of discovery scope. When Steam and non-Steam account namespaces coexist below one game save root, the Steam adapter remains inside canonical Steam profile identities instead of importing every directory that happens to contain a `.sav` file.

ASTRONEER demonstrates that persistence type matters even inside one directory. A file being persistent and adjacent to a World does not make it World-owned state; account/custom-game `.savecfg` remains outside the `.savegame` World revision.

## Normal path for adding a game

From this checkpoint, adding another supported game should normally be an adapter project, not a platform project.

A new game should:

1. implement `IGameAdapter` using game-owned discovery and state/environment rules;
2. prove safe import while preserving the native source;
3. implement portable capture/restore and exact environment semantics appropriate to that game;
4. expose only capabilities backed by deterministic or empirical evidence;
5. add adapter-specific tests and CI coverage;
6. add the Desktop project reference and one entry to `DesktopGameAdapterCatalog`;
7. integrate only after the adapter head is independently qualified;
8. rerun the full five-workflow matrix on the exact combined SHA.

A new game must **not** add game-name branches to Core, backend, Infrastructure, or normal Desktop behavior. If a game is unusual, keep the unusual behavior inside its adapter unless evidence proves a universal contract is missing.

## What remains outside platform implementation

The following are still required before Steward can be called fully release-accepted, but they do not justify continuing generic platform construction now:

### E4-A — real deployment acceptance

- deploy the actual Backend.Api container to a disposable EU environment;
- real PostgreSQL;
- real S3-compatible object storage;
- HTTPS, readiness, restart, logging, transfer and backup/restore probes;
- verify provider/residency configuration.

No private Steam publisher credential is required for this phase.

### E4-B — Steam production acceptance

Only near release-candidate quality:

- real Steward Steam AppID;
- publisher credential;
- real Web API ticket verification;
- two Steam accounts/installations;
- real PC A -> PC B -> PC A World handoff;
- competing-writer rejection and Join/host behavior at the real Steam/game boundary.

There is no production authentication bypass and no reason to make these credentials a current coding blocker.

### Other empirical release evidence

- representative real-World capture/package/restore/transfer measurements;
- real game-process long-session/endurance behavior;
- real Windows keyboard/focus, assistive-technology, and mixed-DPI acceptance;
- real Steam depot/release/update acceptance.

These remain recorded in `DEFERRED_EMPIRICAL_TESTS.md` and related acceptance documents.

## Relationship to older status documents

Older E6/E8 status files preserve useful historical evidence, but checkpoint statements such as `62517139` being the last globally green head or "reconcile the E6/E8 stack onto the newer Factorio tree" are superseded by this document.

The current line contains the E6 commercial UI stack, the E8 deterministic hardening stack, the later adapter hardening work, the PR #72 Desktop composition correction, and post-platform adapter expansion through qualified Terraria #77, Stardew Valley #79, Necesse #81, Core Keeper #83, The Planet Crafter #85, Satisfactory #87, and ASTRONEER #89 on one all-workflows-green ancestry.

Use this file for the current implementation mode. Use E6/E8 documents for the detailed evidence and deferred acceptance categories they describe.

## Development rule from here

> **Platform stable. Games are adapters. Add evidence where a game needs it; do not grow Core/UI to accommodate speculation.**

When work reaches an empirical uncertainty that cannot be settled in CI, record the exact deferred test, freeze that assumption, and continue on an independent deterministic adapter path.

When a problem can be eliminated rather than solved, eliminate it.
