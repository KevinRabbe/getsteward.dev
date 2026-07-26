# Game Adapter Guide

## Purpose

A game adapter isolates everything specific to one game while letting Core run the same complete World handoff lifecycle.

> Core knows what must happen. The adapter knows how this game makes it happen.

Current first-party adapters are Factorio, Palworld, 7 Days to Die, Project Zomboid, Terraria, Stardew Valley, Necesse, Core Keeper, The Planet Crafter, Satisfactory, ASTRONEER, Enshrouded, Conan Exiles Enhanced, Raft, ICARUS, Smalland, Abiotic Factor, and V Rising. Their proven capability sets intentionally differ. Future adapters must preserve the same boundary without forcing their edge cases into Core.

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

When authoritative launcher metadata already owns the installation directory name, prefer that identity over another hardcoded path assumption. Conan Exiles Enhanced demonstrates this after a product migration: Steward reads Steam's bounded appmanifest `installdir` and derives the install root from it instead of needing to know whether a legacy or renamed folder string is current. V Rising uses the same bounded Steam metadata approach for its install-directory identity.

## World discovery

The adapter identifies existing saves or server Worlds that can be imported.

It decides:

- which files or directories form one World;
- which nearby files belong to player identity, account/config persistence, host infrastructure, auxiliary state, or recovery rather than World state;
- which objects belong to the same account/profile namespace but still represent a different persistence identity;
- which storefront/account namespace belongs to the discovered installation source;
- which autosaves, backups, auxiliary assets, or temporary states should be hidden;
- whether native selector/index/generation metadata is required to identify the current authoritative state;
- whether native journal/transaction sidecars mean a nominal state artifact is not currently safe to capture alone;
- how duplicate native sources are collapsed;
- which display name is shown;
- which source should be preferred when the same World appears in multiple locations.

Discovery is read-only. It must not upload, publish, host, or mutate a discovered World.

Physical proximity is not ownership. A game may store World state, character state, maps, configuration, host settings, and recovery data in the same profile or session tree. The adapter must classify those objects by the game's persistence semantics rather than directory adjacency.

Core Keeper is a concrete example: character saves and player exploration maps remain player-owned even though the game stores them beside World files. Raft keeps current World state and player-owned inventory/persona state under the same `User_<SteamID64>` profile. ICARUS keeps Prospect Worlds under `Prospects` while `Characters.json`, `Profile.json`, and `MetaInventory.json` remain player/account progression. Smalland separates `Worlds`, `Players`, and root-level map annotations inside one game-owned save root.

The converse can also be true: a record that describes a player can still be World-owned when the game nests that record inside the World persistence namespace. Abiotic Factor stores multiplayer player records below `Worlds/<WorldName>/PlayerData`, alongside World settings such as `SandboxSettings.ini`. Those objects belong to the World revision, while profile-level persistence above `Worlds` remains outside it. The label "player data" alone does not decide ownership; the game's native persistence boundary does.

Persistence itself is not enough to establish World ownership. ASTRONEER stores `*.savegame` World state beside `*.savecfg` account/custom-game configuration; Steward imports the former and deliberately leaves the latter outside World revisions.

A native recovery file or generation is not another current World. The Planet Crafter hides `Backup.json`; Raft excludes backup/history `.rgd` members; ICARUS excludes `.json.backup_*`; Enshrouded follows bounded native indexes to select the active members of rolling recovery rings.

V Rising demonstrates another generation-based form. A canonical v4 GUID session may contain several `AutoSave_<N>.save` or `.save.gz` generations. Steward selects the single highest numeric native generation as current state instead of copying every recovery generation or guessing by modification time.

A native session directory may also mix World state with host infrastructure. In V Rising, `ServerGameSettings.json` is portable World gameplay configuration and travels with the current autosave, while `ServerHostSettings.json` is host/server infrastructure and remains outside the portable World revision. `SessionId.json` and `StartDate.json` travel because they are part of the proven local session identity/state bundle.

A native database filename is not sufficient evidence that the file is an idle standalone state artifact. Conan Exiles Enhanced exposes one current SQLite database per supported slot, but Steward hides a slot while `-wal`, `-shm`, or `-journal` sidecars exist. That removes the need to reason about an in-flight transaction or invent an online snapshot protocol.

## Environment inspection

The adapter produces an `EnvironmentManifest` containing the game-specific requirements needed to reproduce the World.

Examples include:

- game version;
- enabled mods;
- exact mod versions;
- relevant server or gameplay configuration;
- launcher or runtime requirements.

Core stores the manifest but does not interpret the game's semantics.

An adapter must not claim an exact environment by silently omitting a game-specific input it knows may matter. A narrower adapter may refuse unsupported environments instead.

Current examples include:

- Stardew Valley: detected SMAPI/non-empty Mods;
- Necesse: linked/non-empty local mods;
- Core Keeper: manual install Mods, Steam Workshop content, and per-profile Mods;
- The Planet Crafter: BepInEx bootstrap markers;
- Satisfactory: linked/non-empty `FactoryGame/Mods` and Steam Workshop content;
- ASTRONEER: linked/non-empty `Saved/Mods` and `Saved/Paks`;
- Enshrouded: known EML/Shroudtopia loader markers and root `mods`;
- Conan Exiles Enhanced: linked/non-empty `ConanSandbox/Mods/modlist.txt` activation state;
- Raft: game-root `mods` and roaming `RaftModLoader` surfaces;
- ICARUS: active `Icarus/Content/Paks/mods`;
- Smalland: linked/extra non-stock gameplay Paks;
- Abiotic Factor: the current UE4SS proxy/directory surface;
- V Rising: BepInEx/doorstop bootstrap markers in the game root.

The adapter does not need to enumerate every individual mod merely to know that it cannot truthfully reproduce the environment. A proven loader/bootstrap, activation file, mod-root boundary, or conservative stock-artifact boundary can be enough to refuse the narrower vanilla-only capability.

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

Player-owned persistence is not automatically part of a World package. Raft captures the current `<World>.rgd` and leaves `Player/RGD_Users.rgd` outside the package. ICARUS captures the current Prospect `.json` while leaving profile-level character, profile, and meta-inventory files outside. Smalland captures the direct `Worlds/<World>.wld` while leaving `Players/*.plr` and map-annotation `.sav` state outside.

Conversely, player-labelled persistence is part of the World package when the game itself owns it inside the World namespace. Abiotic Factor captures the full `Worlds/<WorldName>/` directory, including nested `PlayerData` and `SandboxSettings.ini`, while excluding profile-level state above `Worlds`. The native persistence boundary decides revision ownership.

When the game already stores the current World in a portable archive, do not automatically unpack and rebuild it. Necesse demonstrates the simpler rule: preserve the game-native World ZIP as opaque bytes unless Steward has a concrete need to interpret its contents.

When a World is spread across several files, capture only the smallest complete World-owned bundle proven necessary. Core Keeper demonstrates this with exactly three slot-matched files. Enshrouded packages its two bounded selector indexes plus only the two native bodies those indexes select.

When the game already stores the complete current World in one native file, do not invent an internal schema merely to move it. The Planet Crafter, Satisfactory, ASTRONEER, Conan Exiles Enhanced, Raft, ICARUS, and Smalland all demonstrate opaque byte-preserving variants of this rule.

A native directory World may need a portable container without requiring a save parser. Abiotic Factor packages the proven World directory into a bounded file-only ZIP while keeping native save bodies opaque. The archive itself becomes the trust boundary, so paths, entry count, uncompressed size, collisions, links, and extraction publication must be validated explicitly.

A one-file native database is only a one-file portable state while the game's own transactional sidecars are absent. Conan Exiles Enhanced checks for SQLite `-wal`, `-shm`, and `-journal` before capture and again after the copy. If the slot becomes active during import, Steward discards the temporary package and refuses the capture.

When current authoritative state is selected from a native generation sequence, use the game's bounded native selector rule and move only the selected current state. V Rising selects the highest numeric autosave generation, then packages exactly that autosave plus `ServerGameSettings.json`, `SessionId.json`, and `StartDate.json`. Older autosaves are recovery history and `ServerHostSettings.json` is host infrastructure, so neither enters the World package.

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

If the native package uses generation identity, restore must preserve the chosen current-generation filename and required companion metadata consistently rather than flattening recovery history into a different native shape.

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

For generation-based native state, capture should re-resolve the current native generation at the capture boundary where needed rather than assuming the generation observed at initial discovery is still current. V Rising's state path uses a capture race check so the portable package corresponds to one coherent selected generation.

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

Not every adapter needs a game-specific parser. If opaque byte preservation is enough to implement the proven capability, adding a parser creates another trust and maintenance boundary without product value. When interpretation is genuinely required only to locate current authoritative bytes, keep that interpretation as small and bounded as the native selector format allows rather than extending it into the opaque save body.

A directory-shaped World does not change that rule. A bounded archive layer can make the native directory portable while the underlying game files remain opaque; portability and save-format interpretation are separate concerns.

A generation-shaped World does not change it either. V Rising needs only the native generation number encoded in autosave filenames to choose current state; Steward does not need to interpret the autosave payload itself.

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
5. classify World-owned state separately from player-owned, account/config, host-infrastructure, auxiliary, and recovery state, even when they share one profile/session tree;
6. identify the smallest complete native World representation;
7. identify native transaction/journal companions or generation rules that determine whether/currently which artifact is safe to capture;
8. identify any bounded native selector metadata required to locate current authoritative members;
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
23. qualify the adapter head independently before integration;
24. if independent parallel slices exist, preserve both evidence branches and mechanically combine them;
25. rerun the full five-workflow matrix on the exact combined SHA.

The first target is one truthful vertical slice, not many partially claimed workflows. An adapter that safely supports discovery/import/environment/state handling may be visible in Steward while launch/hosting remains unavailable; missing runtime evidence is represented by absent capability flags, not invented generic behavior.

Terraria, Stardew Valley, Necesse, Core Keeper, The Planet Crafter, Satisfactory, ASTRONEER, Enshrouded, Conan Exiles Enhanced, Raft, ICARUS, Smalland, Abiotic Factor, and V Rising are current concrete examples of that narrower entry point. Terraria preserves one opaque vanilla `.wld`; Stardew Valley preserves exactly its two current vanilla save files; Necesse preserves the native compressed World ZIP; Core Keeper preserves three slot-matched World-owned files; The Planet Crafter preserves one opaque native `.json`; Satisfactory one Steam-profile `.sav`; ASTRONEER one `.savegame`; Enshrouded two bounded indexes plus the two selected current bodies; Conan one idle SQLite slot database; Raft one same-name current `.rgd`; ICARUS one current Prospect `.json`; Smalland one direct `.wld`; Abiotic Factor one full World-owned directory inside a bounded Steward ZIP; V Rising one highest-generation autosave plus World settings and session identity/start metadata. All fourteen verify the exact Steam build and advertise only `ExactGameVersion`; launch/hosting capabilities remain absent.

Adding a game normally does **not** require changes to Core, backend, Infrastructure, or ordinary Desktop action logic. If implementation appears to require such a change, first prove that the need is genuinely universal rather than an adapter-specific edge case.

## Acceptance tests

Every exposed capability must have evidence at its actual trust/ownership boundary.

For a discovery/import/state-only slice, prove at minimum:

```text
discover intended installation without mutation
-> stay inside the source platform/account identity namespace
-> use authoritative launcher/store metadata for install identity where it already exists
-> discover intended World without mutation
-> distinguish World persistence from player/account/config/host-infrastructure/auxiliary/recovery persistence even inside one profile or session tree
-> identify the smallest complete World-owned state representation
-> if the native World is a directory, bound archive paths, entry count, uncompressed size, and linked members
-> reject native transaction/journal activity when the chosen representation is only safe while idle
-> resolve bounded native selectors or generation identity where required to identify current authoritative bytes
-> preserve native bytes directly where interpretation is unnecessary
-> import copied/controlled World while preserving source
-> inspect exact supported environment
-> capture portable state
-> restore it into an owned workspace
-> validate restored state/package invariants
```

A generation-based adapter should additionally prove that older recovery generations are excluded, the selected current generation is stable across capture, and infrastructure-only settings do not enter the portable World package.

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
