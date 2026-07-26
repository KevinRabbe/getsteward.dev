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

Normal adapter expansion has continued without reopening the platform. The latest qualified product line is the Abiotic Factor + V Rising integration PR #104, containing 18 first-party adapters:

> `e99065b6a3cf354e91db0ae4534da93846ba8f87`

On that exact head all five repository workflows are green again, including Quality, Ubuntu/Windows build-and-test, backend container, PostgreSQL, S3-compatible integration, Windows acceptance, and the dedicated Palworld/7 Days to Die/Project Zomboid lanes.

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
- ASTRONEER: 12/12;
- Enshrouded: 14/14;
- Conan Exiles Enhanced: 15/15;
- Raft: 13/13;
- ICARUS: 13/13;
- Smalland: 14/14;
- Abiotic Factor: 15/15;
- V Rising: 16/16 on both Ubuntu and Windows.

PR #104 composes independently qualified Abiotic Factor PR #102 and V Rising PR #103 on one fresh integration branch. The combined line preserves the same post-platform pattern already proven by earlier state-only adapters: independent game-owned discovery/environment/state behavior, truthful capability exposure, adapter-specific tests, Desktop/solution registration, and exact combined qualification. It does not modify Core, backend, Infrastructure, generic Desktop lifecycle contracts, or CI policy.

Abiotic Factor makes a useful subtree-ownership boundary explicit. A canonical Steam profile can contain account/profile persistence above `Worlds`, while one direct `Worlds/<World>` subtree is the complete World-owned revision and may itself legitimately contain nested native player/sandbox state. Steward archives that World subtree without promoting the whole profile to World state and without parsing the native save grammar.

V Rising makes a different current-state boundary explicit. A `v4` session can contain rolling `AutoSave_*` recovery generations, World gameplay rules, session identity/time metadata, and machine host configuration in one directory. Steward packages only the latest native autosave plus `ServerGameSettings.json`, `SessionId.json`, and `StartDate.json`; older autosaves and `ServerHostSettings.json` remain outside the portable current World revision. That follows the game's native transfer contract without parsing or re-encoding the autosave body.

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
| Enshrouded | local vanilla indexed-World discovery/import, exact Steam build identity, bounded selector-index parsing, exact four-file current-state capture/restore, and owned workspace handling; inactive recovery generations, `enshrouded_user.json`, Steam Cloud, dedicated-server saves, modded environments, launch, Host, Stop, and Join remain unsupported |
| Conan Exiles Enhanced | local vanilla ten-slot `game_0.db` through `game_9.db` discovery/import, manifest-derived install identity, exact Steam build identity, opaque SQLite capture/restore, SQLite-sidecar refusal, and owned workspace handling; live-database snapshotting, modded environments, dedicated-server lifecycle, launch, Host, Stop, and Join remain unsupported |
| Raft | local vanilla canonical `User_<SteamID64>` World discovery/import using the same-name current `<World>.rgd`, exact Steam build identity, opaque capture/restore, backup/player-state exclusion, and owned workspace handling; player inventory/persona migration, backup selection, RaftModLoader environments, launch, Host, Stop, and Join remain unsupported |
| ICARUS | local vanilla canonical numeric SteamID64 Prospect discovery/import using one current `<Prospect>.json`, exact Steam build identity, opaque capture/restore, rolling-backup/player-state exclusion, and owned workspace handling; character/profile/meta-inventory migration, backup selection, Paks mods, launch, Host, Stop, and Join remain unsupported |
| Smalland | local vanilla direct `Worlds/<World>.wld` discovery/import, exact Steam build identity, opaque capture/restore, player/map-state exclusion, conservative extra-Pak refusal, and owned workspace handling; player-character/map-annotation migration, mod reproduction, dedicated-server lifecycle, launch, Host, Stop, and Join remain unsupported |
| Abiotic Factor | local vanilla canonical SteamID64-profile `Worlds/<World>` directory discovery/import, exact Steam build identity, opaque World-subtree capture/restore, profile-scope separation, UE4SS refusal, and owned workspace handling; account/profile-root migration, mod reproduction, launch, Host, Stop, and Join remain unsupported |
| V Rising | local non-cloud vanilla `Saves/v4/<SessionGuid>` discovery/import, exact Steam build identity, latest-autosave selection, exact four-member current-state capture/restore, host-config/recovery exclusion, BepInEx refusal, and owned workspace handling; CloudSaves, mod reproduction, dedicated-server lifecycle, launch, Host, Stop, and Join remain unsupported |

Registration does not grant capabilities. The Desktop reads each adapter's `GameAdapterCapabilities`; adding an adapter to the catalog cannot silently make Start, Host, Join, or Stop available.

Terraria, Stardew Valley, Necesse, Core Keeper, The Planet Crafter, Satisfactory, ASTRONEER, Enshrouded, Conan Exiles Enhanced, Raft, ICARUS, Smalland, Abiotic Factor, and V Rising currently advertise only `ExactGameVersion`. Their state-only entries are deliberate evidence that adapters can join the product before launch/hosting semantics are proven.

Stardew Valley refuses a detected SMAPI or non-empty Mods environment rather than recording an incomplete vanilla `EnvironmentManifest` for a modded installation. Necesse applies the same principle to its local mods directory. Core Keeper applies it across manual install Mods, Steam Workshop content, and per-profile Mods. The Planet Crafter refuses known BepInEx bootstrap markers rather than enumerating or pretending to reproduce individual mods. Satisfactory refuses linked/non-empty `FactoryGame/Mods` and Steam Workshop content; this also catches an installed SML environment without requiring Steward to understand individual mods. ASTRONEER refuses linked/non-empty `Saved/Mods` or `Saved/Paks` rather than claiming a vanilla environment while mod-integration content is present. Enshrouded refuses known EML/Shroudtopia loader markers and a linked/non-empty root `mods` directory rather than interpreting individual mod packages. Conan Exiles Enhanced refuses a linked or non-empty `ConanSandbox/Mods/modlist.txt` activation surface rather than enumerating individual mod packages. Raft refuses linked/non-empty game-root `mods` and roaming `RaftModLoader` surfaces rather than enumerating individual mods. ICARUS refuses a linked or non-empty active `Icarus/Content/Paks/mods` directory rather than enumerating individual Paks. Smalland accepts only regular stock-style top-level `pakchunkN-WindowsNoEditor.pak` members in its gameplay Paks directory and refuses linked or extra non-stock-named Paks rather than parsing mod content. Abiotic Factor refuses the known UE4SS `dwmapi.dll` proxy or `ue4ss` loader directory instead of enumerating mod scripts. V Rising refuses known BepInEx/bootstrap markers in the game root instead of claiming reproducibility for a modded environment.

Necesse demonstrates a simple state rule: when the game's native current World artifact is already a portable ZIP, Steward preserves those bytes directly instead of unpacking and rebuilding a second archive format.

Core Keeper demonstrates the complementary ownership rule: storage adjacency is not identity. Character saves and player exploration maps are excluded even though they live in the same game-managed profile tree as the World files.

The Planet Crafter demonstrates the same restraint for an opaque native file: Steward does not need to understand the internal save grammar to preserve, transfer, restore, and verify the exact bytes it owns.

Satisfactory demonstrates that the source platform is also part of discovery scope. When Steam and non-Steam account namespaces coexist below one game save root, the Steam adapter remains inside canonical Steam profile identities instead of importing every directory that happens to contain a `.sav` file.

ASTRONEER demonstrates that persistence type matters even inside one directory. A file being persistent and adjacent to a World does not make it World-owned state; account/custom-game `.savecfg` remains outside the `.savegame` World revision.

Enshrouded demonstrates the narrow exception to opaque-only handling: when a small bounded native index is required to identify authoritative current bytes inside a rolling recovery ring, Steward should parse only that selector metadata. The large selected World bodies remain opaque, and inactive recovery generations remain outside the current revision.

Conan Exiles Enhanced demonstrates that location identity and state capture safety can often be proven without learning more game internals. Steam's own manifest gives the install-directory identity, and the presence of SQLite transient sidecars is enough to know a database is not an idle standalone artifact. Steward refuses that state instead of understanding Conan's database schema or implementing a live snapshot protocol.

Raft demonstrates that one account/profile namespace can contain multiple independent persistence identities. Its current World artifact and its player/inventory state live under the same `User_<SteamID64>` profile but do not belong to the same World revision. Steward moves the World without silently moving the host's personal state.

ICARUS reinforces that boundary with a different native layout. A canonical numeric SteamID64 profile owns current Prospect Worlds under `Prospects`, but profile-level `Characters.json`, `Profile.json`, and `MetaInventory.json` belong to player/account progression instead. Rolling `.json.backup_*` copies are recovery history. Steward moves only the current Prospect bytes.

Smalland demonstrates that the same separation can be encoded directly by a game's native directory layout without any account-profile parser. `SaveGames/Worlds` is the World namespace, `SaveGames/Players` is player persistence, and root-level `.sav` objects are auxiliary map annotations. Steward follows that ownership structure and moves only the direct `.wld` World bytes.

Abiotic Factor demonstrates that a World-owned object can itself be a directory subtree rather than one flat file. Steward enters only canonical SteamID64 profile identities and only their `Worlds` namespace, then preserves the complete selected World subtree—including legitimate nested native state—without absorbing profile-level persistence above `Worlds` or parsing the `.sav` bodies.

V Rising demonstrates that "current World" can be a projection of one native session directory rather than the whole directory. Steward selects the numerically latest `AutoSave_*` generation and packages it with gameplay rules plus session identity/time metadata, while excluding older recovery generations and machine-specific `ServerHostSettings.json`. The save body remains opaque.

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

The current line contains the E6 commercial UI stack, the E8 deterministic hardening stack, the later adapter/test hardening work, the PR #72 Desktop composition correction, and post-platform adapter expansion through qualified Terraria #77, Stardew Valley #79, Necesse #81, Core Keeper #83, The Planet Crafter #85, Satisfactory #87, ASTRONEER #89, Enshrouded #92, Conan Exiles Enhanced #94, Raft #96, ICARUS #98, Smalland #100, Abiotic Factor #102, and V Rising integration #104 on one all-workflows-green ancestry.

Use this file for the current implementation mode. Use E6/E8 documents for the detailed evidence and deferred acceptance categories they describe.

## Development rule from here

> **Platform stable. Games are adapters. Add evidence where a game needs it; do not grow Core/UI to accommodate speculation.**

When work reaches an empirical uncertainty that cannot be settled in CI, record the exact deferred test, freeze that assumption, and continue on an independent deterministic adapter path.

When a problem can be eliminated rather than solved, eliminate it.
