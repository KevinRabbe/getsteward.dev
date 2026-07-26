# Game Adapter Guide

## Purpose

A game adapter isolates everything specific to one game while letting Core run the same complete World handoff lifecycle.

> Core knows what must happen. The adapter knows how this game makes it happen.

Current first-party adapters are Factorio, Palworld, 7 Days to Die, Project Zomboid, Terraria, Stardew Valley, Necesse, Core Keeper, The Planet Crafter, Satisfactory, ASTRONEER, Enshrouded, Conan Exiles Enhanced, Raft, ICARUS, Smalland, and Abiotic Factor. Their proven capability sets intentionally differ. Future adapters must preserve the same boundary without forcing their edge cases into Core.

## Product contract

Every useful adapter contributes to this lifecycle:

```text
find World
-> inspect required environment
-> prepare device
-> restore latest state
-> launch local play or temporary host
-> observe the real session
-> determine safe capture point
-> capture and validate updated state
```

An adapter is not complete merely because it can launch the game.

An adapter may enter the product with a narrower truthful capability set while later runtime semantics remain unproven. Registration never grants capabilities: unsupported Start, Host, Join, or Stop behavior stays unavailable until the adapter has evidence for the corresponding `GameAdapterCapabilities` flag.

## Identity and capabilities

An adapter exposes:

- stable adapter id;
- display name;
- supported capabilities.

Capabilities may include:

- local launch;
- temporary host launch;
- native client join;
- exact game-version handling;
- mod/environment inspection;
- isolated environment preparation;
- automatic capture;
- graceful host shutdown.

Core uses capabilities. It does not branch on the game name.

## Installation discovery

The adapter finds supported installations using game-relevant sources such as:

- Steam libraries;
- launcher metadata;
- registry entries;
- conventional paths;
- portable installations;
- user-selected paths.

Core does not know how discovery works.

The installation source also constrains the identity namespace the adapter may claim. If Steam, Epic, launcher-specific, or other account profiles share one broader game save root, a Steam-discovered adapter should not automatically treat every neighboring profile as Steam-owned state. Satisfactory is a concrete example: its Steam adapter enters only canonical numeric Steam profile directories.

When authoritative launcher metadata already owns the installation directory name, prefer that identity over another hardcoded path assumption. Conan Exiles Enhanced demonstrates this after a product migration: Steward reads Steam's bounded appmanifest `installdir` and derives the install root from it instead of needing to know whether a legacy or renamed folder string is current.

## World discovery

The adapter identifies existing saves or server Worlds that can be imported.

It decides:

- which files or directories form one World;
- which nearby files belong to player identity, account/config persistence, or auxiliary state rather than World state;
- which objects belong to the same account/profile namespace but still represent a different persistence identity;
- which storefront/account namespace belongs to the discovered installation source;
- which autosaves, backups, auxiliary assets, or temporary states should be hidden;
- whether native selector/index metadata is required to identify the current authoritative state;
- whether native journal/transaction sidecars mean a nominal state artifact is not currently safe to capture alone;
- how duplicate native sources are collapsed;
- which display name is shown;
- which source should be preferred when the same World appears in multiple locations.

Discovery is read-only. It must not upload, publish, host, or mutate a discovered World.

Physical proximity is not ownership. A game may store World state, character state, maps, configuration, and recovery data in the same profile tree. The adapter must classify those objects by semantics rather than directory adjacency. Core Keeper is a concrete example: character saves and player exploration maps remain player-owned even though the game stores them beside World files.

A shared account/profile namespace also does not make every object part of one revision. Raft keeps current World state and player-owned inventory/persona state under the same `User_<SteamID64>` profile. ICARUS does the same kind of separation under a numeric SteamID64 profile: current Prospect Worlds live under `Prospects`, while `Characters.json`, `Profile.json`, and `MetaInventory.json` remain player/account persistence. Steward discovers the World state and deliberately leaves the personal state outside the World revision.

A game-owned save root can itself contain authoritative persistence namespaces. Smalland stores direct World files under `SaveGames/Worlds`, player-character files under `SaveGames/Players`, and map-annotation `.sav` files at the `SaveGames` root. Steward follows those native ownership boundaries rather than treating the entire save root as one World package.

The converse can also be true: a record that describes a player can still be World-owned when the game nests that record inside the World persistence namespace. Abiotic Factor stores multiplayer player records below `Worlds/<WorldName>/PlayerData`, alongside World settings such as `SandboxSettings.ini`. Those objects belong to the World revision, while profile-level persistence above `Worlds` remains outside it. The label "player data" alone does not decide ownership; the game's native persistence boundary does.

Persistence itself is not enough to establish World ownership. ASTRONEER stores `*.savegame` World state beside `*.savecfg` account/custom-game configuration; Steward imports the former and deliberately leaves the latter outside World revisions.

A native recovery file is not another current World. The Planet Crafter, for example, exposes `Backup.json` beside current save files; the adapter deliberately hides it from normal World discovery.

A native recovery ring is also not automatically one current World bundle. Enshrouded keeps rolling World-data and `_info` generations while separate bounded index files identify the active member of each ring. Discovery follows those selectors and leaves inactive generations outside the current World revision.

A directory containing several files with the same native extension does not mean they all belong to current state. Raft uses the same-name `World/<name>/<name>.rgd` as the current World artifact while other `.rgd` members in that World directory are treated as backup/history state and excluded from normal discovery. ICARUS similarly treats the plain `<Prospect>.json` as current state while `.json.backup_*` generations remain recovery history.

A native database filename is not sufficient evidence that the file is an idle standalone state artifact. Conan Exiles Enhanced exposes one current SQLite database per supported slot, but Steward hides a slot while `-wal`, `-shm`, or `-journal` sidecars exist. That removes the need to reason about an in-flight transaction or invent an online snapshot protocol.

Auxiliary user assets are not automatically World state either. Satisfactory's backup and blueprint trees are deliberately outside its current `.sav` World boundary, and adjacent non-Steam account profiles are outside the Steam adapter's identity scope.

## Environment inspection

The adapter produces an `EnvironmentManifest` containing the game-specific requirements needed to reproduce the World.

Examples include:

- game version;
- enabled mods;
- exact mod versions;
- relevant server or gameplay configuration;
- launcher or runtime requirements.

Core stores the manifest but does not interpret the game's semantics.

An adapter must not claim an exact environment by silently omitting a game-specific input it knows may matter. A narrower adapter may refuse unsupported environments instead. Stardew Valley uses this rule for detected SMAPI/non-empty Mods installations; Necesse applies it to a linked or non-empty local mods directory; Core Keeper applies it across manual install Mods, Steam Workshop content, and per-profile Mods; The Planet Crafter refuses known BepInEx bootstrap markers; Satisfactory refuses linked/non-empty `FactoryGame/Mods` and Steam Workshop content; ASTRONEER refuses linked/non-empty `Saved/Mods` and `Saved/Paks`; Enshrouded refuses known EML/Shroudtopia loader markers and a linked/non-empty root `mods` directory; Conan Exiles Enhanced refuses a linked or non-empty `ConanSandbox/Mods/modlist.txt` activation surface; Raft refuses linked/non-empty game-root `mods` and roaming `RaftModLoader` surfaces; ICARUS refuses a linked or non-empty active `Icarus/Content/Paks/mods` directory; Smalland refuses linked gameplay Paks and top-level `.pak` entries that do not match the stock `pakchunkN-WindowsNoEditor.pak` naming boundary; Abiotic Factor refuses the current UE4SS proxy/directory loader surface.

The adapter does not need to enumerate every individual mod merely to know that it cannot truthfully reproduce the environment. A proven loader/bootstrap, activation file, mod-root boundary, or conservative stock-artifact boundary can be enough to refuse the narrower vanilla-only capability. Satisfactory demonstrates this by refusing a non-empty `FactoryGame/Mods`; an installed SML environment is caught there without a separate SML abstraction. ASTRONEER similarly uses its known mod-integration roots without interpreting individual packages. Enshrouded applies the same restraint to known loader bootstrap files and its root mod surface. Conan Exiles Enhanced uses the game's own `modlist.txt` activation surface and does not need to enumerate Workshop packages. Raft applies the same rule to the game-root mod directory and the separate RaftModLoader installation surface. ICARUS applies it to the active Paks mods directory without interpreting individual `.pak` files. Smalland accepts only regular stock-style top-level Paks and fails closed on extra or linked Paks instead of parsing mod packages. Abiotic Factor applies the same restraint to UE4SS: the proven loader surface is sufficient to know the current vanilla-only adapter cannot reproduce the environment, so Steward does not need a UE4SS mod catalog.

## Import capture

The adapter converts a detected World into a portable `StatePackage` suitable for initial durable storage.

The package may represent:

- one opaque save file;
- one game-native archive;
- one Steward-owned ZIP;
- a directory archive;
- a database;
- several related files;
- launcher-managed state.

Import must leave the source untouched.

Native backup/recovery history is not automatically canonical World state. An adapter should include only the files required for the current authoritative state unless game-specific evidence says otherwise.

Player-owned persistence is not automatically part of a World package either. Raft demonstrates the distinction directly: Steward captures the current `<World>.rgd` and leaves `Player/RGD_Users.rgd` outside the package. ICARUS likewise captures the current Prospect `.json` while leaving profile-level character, profile, and meta-inventory files outside the package. Smalland captures the direct `Worlds/<World>.wld` while leaving `Players/*.plr` and root-level map-annotation `.sav` state outside the package. Moving a World therefore does not silently move the host's personal progression or auxiliary state.

Conversely, player-labelled persistence is part of the World package when the game itself owns it inside the World namespace. Abiotic Factor captures the full `Worlds/<WorldName>/` directory, including nested `PlayerData` and `SandboxSettings.ini`, while excluding profile-level state above `Worlds`. The native persistence boundary decides revision ownership.

When the game already stores the current World in a portable archive, do not automatically unpack and rebuild it. Necesse demonstrates the simpler rule: preserve the game-native World ZIP as opaque bytes unless Steward has a concrete need to interpret its contents.

When a World is spread across several files, capture only the smallest complete World-owned bundle proven necessary. Core Keeper demonstrates this rule with exactly three slot-matched files: World data, World metadata, and World-generation parameters. Character saves, player maps, and recovery copies are not included.

When the game already stores the complete current World in one native file, do not invent an internal schema merely to move it. The Planet Crafter demonstrates this rule with one opaque `.json`; Satisfactory does the same with one opaque `.sav`; ASTRONEER does the same with one opaque `.savegame`; Conan Exiles Enhanced does the same with one idle SQLite `.db`; Raft does the same with the same-name current `.rgd`; ICARUS does the same with one current Prospect `.json`; Smalland does the same with one direct `.wld`. Steward copies those native bytes exactly and avoids a parser/re-encoder trust boundary that its proven state capability does not need.

A native directory World may need a portable container without requiring a save parser. Abiotic Factor demonstrates this shape: Steward packages the proven World directory into a bounded file-only ZIP while keeping the native save bodies opaque. The archive itself becomes the trust boundary, so paths, entry count, uncompressed size, collisions, links, and extraction publication must be validated explicitly.

A one-file native database is only a one-file portable state while the game's own transactional sidecars are absent. Conan Exiles Enhanced checks for SQLite `-wal`, `-shm`, and `-journal` before capture and checks again after the copy. If the slot becomes active during import, Steward discards the temporary package and refuses the capture. Doing less here is stronger than implementing a partial live-database snapshotter.

When the current authoritative bytes live inside a bounded native rolling-recovery scheme, parsing minimal selector metadata can be the simpler path. Enshrouded reads only its small data and `_info` index files, uses each `latest` selector to identify the active native body independently, and packages exactly those four files. It does not parse the large save bodies and does not copy the inactive recovery generations.

## Environment preparation

The adapter creates or selects a playable environment matching the requested manifest.

Possible strategies include:

- isolated mod directories;
- launcher profiles;
- workspace-local configuration;
- symlinks or junctions;
- transactional file swapping;
- game-native dedicated-server installation;
- separate installations only where necessary.

The strategy remains an adapter detail.

## Restore

The adapter restores the opaque state package into its prepared workspace.

It must validate trust boundaries and must not assume that a path is safe to overwrite merely because it resembles a save location.

If selector metadata is part of the portable state, restore must validate that the package actually contains the files selected by that metadata rather than trusting filenames or archive contents independently.

If the portable state is a Steward-owned directory archive, restore must validate the archive as an input boundary before publication: reject directory/link/traversal/absolute/non-canonical or colliding paths, bound entry count and declared extraction size, extract only inside an owned staging root, and publish the completed tree transactionally.

## Launch local play

Local launch starts the World without deliberately exposing Steward's hosted multiplayer path.

Where the game has a meaningful distinction, local play and hosting remain separate adapter operations.

## Launch temporary host

Hosted launch starts the game or dedicated server so other players can join through Steam or the game.

The adapter owns:

- executable and arguments;
- server configuration;
- readiness checks;
- connection information where needed;
- graceful shutdown behavior;
- identification of the process that actually owns the writable World.

A client's exit is not automatically the end of a dedicated-server session.

## Join active host

An adapter may expose native joining through Steam, direct connection information, or game-specific mechanisms.

Joining does not create another writable Steward session. The active host remains the only writer.

## Session observation

The adapter determines when the actual World session has ended.

It must handle relevant realities such as:

- launcher/bootstrap process exit before the game exits;
- process replacement or handoff;
- separate client and server processes;
- dedicated servers outliving clients;
- graceful save/shutdown commands;
- files continuing to change briefly after process signals;
- game-specific completion markers.

Core must not replace this contract with a generic `Process.WaitForExit` assumption.

## Capture and validation

After the safe capture point, the adapter creates a new portable state package and validates the game-specific minimum required for restore.

It must report whether the returned package is disposable after durable storage.

Core then owns durable storage, verification, current-head advancement, and recovery semantics.

## Workspace finalization

The adapter cleans or preserves its prepared workspace according to the disposition requested by Core.

Rules:

- pre-launch failure may clean controlled temporary work;
- successful commit may clean controlled work;
- uncertain post-launch failure preserves recoverable state;
- recursive deletion requires explicit ownership validation;
- user-owned source saves must never be deleted.

## Adapter isolation

Game-specific helpers, SDKs, parsers, commands, and dependencies stay inside the adapter assembly.

Adding Palworld-specific dedicated-server behavior must not add Palworld fields to Core. Adding a Factorio-specific RCON or mod behavior must not become a universal requirement.

Not every adapter needs a game-specific parser. If opaque byte preservation is enough to implement the proven capability, adding a parser creates another trust and maintenance boundary without product value. When interpretation is genuinely required only to locate current authoritative bytes, keep that parser as small and bounded as the native selector format allows rather than extending it into the opaque save body.

A directory-shaped World does not change that rule. A bounded archive layer can make the native directory portable while the underlying game files remain opaque; portability and save-format interpretation are separate concerns.

## What an adapter must not decide

An adapter must not redefine:

- the one-active-writer rule;
- whether the current World head may advance;
- durable revision publication order;
- recovery policy;
- shared-storage access policy;
- generic product lifecycle states;
- generic save merging;
- branch or Fork semantics;
- ownership or social governance.

Those either belong to Core or are outside Steward's product scope.

## Adding a new game

Implement in this order, stopping capability growth whenever the next game-specific behavior is not yet proven:

1. create the independent adapter project and implement its stable id/display name;
2. installation discovery;
3. bind discovery to the storefront/account identity and authoritative installation locator proven by that installation source;
4. existing-World discovery;
5. classify World-owned state separately from player-owned, account/config, auxiliary, and recovery state, even when they share one account/profile namespace or game-owned save root;
6. identify the smallest complete native World representation;
7. identify any native transaction/journal companions that make an otherwise standalone artifact unsafe to copy alone;
8. identify any bounded native selector metadata required to locate the current authoritative members;
9. safe import capture without parsing/re-encoding beyond what is actually required;
10. environment inspection;
11. environment preparation where required by the supported slice;
12. state restore;
13. local launch only when its ownership/session semantics are proven;
14. real session observation;
15. safe state capture and validation;
16. temporary host launch only when its server/runtime semantics are proven;
17. graceful hosted-session shutdown;
18. optional automatic Join;
19. exact environment reproduction where justified;
20. expose only the `GameAdapterCapabilities` proven by the implemented slice;
21. add adapter-specific deterministic tests and a dedicated CI lane where appropriate;
22. add the Desktop project reference and one entry to `DesktopGameAdapterCatalog`;
23. qualify the adapter head independently before mechanical integration;
24. rerun the full five-workflow matrix on the exact combined SHA.

The first target is one truthful vertical slice, not many partially claimed workflows. An adapter that safely supports discovery/import/environment/state handling may be visible in Steward while launch/hosting remains unavailable; missing runtime evidence is represented by absent capability flags, not invented generic behavior.

Terraria, Stardew Valley, Necesse, Core Keeper, The Planet Crafter, Satisfactory, ASTRONEER, Enshrouded, Conan Exiles Enhanced, Raft, ICARUS, Smalland, and Abiotic Factor are current concrete examples of that narrower entry point. Terraria preserves one opaque vanilla `.wld`; Stardew Valley preserves exactly its two current vanilla save files while excluding `_old` recovery files; Necesse preserves the game's native compressed World ZIP byte-for-byte instead of adding a second archive layer; Core Keeper preserves exactly the three slot-matched World-owned files while excluding character and map state; The Planet Crafter preserves one opaque non-empty native `.json` World while excluding `Backup.json`; Satisfactory preserves one opaque Steam-profile `.sav` while excluding non-Steam profiles, backup trees, and blueprints; ASTRONEER preserves one opaque `.savegame` while excluding adjacent `.savecfg` account/custom-game configuration; Enshrouded uses two bounded selector indexes to preserve only the current World-data and `_info` members while excluding inactive native recovery generations and user configuration; Conan Exiles Enhanced preserves one opaque idle slot `.db`, derives install identity from Steam metadata, and refuses SQLite sidecar activity instead of implementing live database snapshotting; Raft preserves one same-name current `.rgd` while excluding World backup/history files and the separate player/inventory tree under the same Steam profile; ICARUS preserves one current Prospect `.json` while excluding rolling `.json.backup_*` recovery generations and profile-level character/account progression; Smalland preserves one direct `Worlds/<World>.wld` while excluding player-character and map-annotation persistence in adjacent native namespaces; Abiotic Factor preserves one full World-owned `Worlds/<WorldName>/` directory, including nested `PlayerData` and `SandboxSettings.ini`, inside a bounded Steward ZIP while excluding profile-level state. All thirteen verify the exact Steam build and advertise only `ExactGameVersion`; launch/hosting capabilities remain absent.

Adding a game normally does **not** require changes to Core, backend, Infrastructure, or ordinary Desktop action logic. If implementation appears to require such a change, first prove that the need is genuinely universal rather than an adapter-specific edge case.

## Acceptance tests

Every exposed capability must have evidence at its actual trust/ownership boundary.

For a discovery/import/state-only slice, prove at minimum:

```text
discover intended installation without mutation
-> stay inside the source platform/account identity namespace
-> use authoritative launcher/store metadata for install identity where it already exists
-> discover intended World without mutation
-> distinguish World persistence from player/account/config/auxiliary/recovery persistence even inside one profile or game-owned save root
-> identify the smallest complete World-owned state representation
-> if the native World is a directory, bound archive paths, entry count, uncompressed size, and linked members
-> reject native transaction/journal activity when the chosen representation is only safe while idle
-> resolve bounded native selectors where required to identify the authoritative current bytes
-> preserve native bytes directly where interpretation is unnecessary
-> import copied/controlled World while preserving source
-> inspect exact supported environment
-> capture portable state
-> restore it into an owned workspace
-> validate restored state/package invariants
```

A launch-capable adapter additionally proves the complete writable handoff:

```text
restore known World
-> launch it
-> prove the real session owner
-> make a visible gameplay change
-> observe safe session end
-> capture and commit the change
-> restore the committed result in the next session
```

Hosted support additionally proves that the authoritative server/session state, not merely a client process, controls readiness, stop, and the capture boundary.

Automatic Join must remain read-only with respect to Steward World authority and must consume a proven ready host connection without acquiring another writable reservation.
