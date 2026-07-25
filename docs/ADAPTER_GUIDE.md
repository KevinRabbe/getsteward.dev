# Game Adapter Guide

## Purpose

A game adapter isolates everything specific to one game while letting Core run the same complete World handoff lifecycle.

> Core knows what must happen. The adapter knows how this game makes it happen.

Current first-party adapters are Factorio, Palworld, 7 Days to Die, Project Zomboid, Terraria, Stardew Valley, and Necesse. Their proven capability sets intentionally differ. Future adapters must preserve the same boundary without forcing their edge cases into Core.

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

## World discovery

The adapter identifies existing saves or server Worlds that can be imported.

It decides:

- which files or directories form one World;
- which autosaves, backups, or temporary states should be hidden;
- how duplicate native sources are collapsed;
- which display name is shown;
- which source should be preferred when the same World appears in multiple locations.

Discovery is read-only. It must not upload, publish, host, or mutate a discovered World.

## Environment inspection

The adapter produces an `EnvironmentManifest` containing the game-specific requirements needed to reproduce the World.

Examples include:

- game version;
- enabled mods;
- exact mod versions;
- relevant server or gameplay configuration;
- launcher or runtime requirements.

Core stores the manifest but does not interpret the game's semantics.

An adapter must not claim an exact environment by silently omitting a game-specific input it knows may matter. A narrower adapter may refuse unsupported environments instead. Stardew Valley currently uses this rule to reject detected SMAPI/non-empty Mods installations until mod reproduction exists; Necesse applies the same rule to a linked or non-empty local mods directory.

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

When the game already stores the current World in a portable archive, do not automatically unpack and rebuild it. Necesse demonstrates the simpler rule: preserve the game-native World ZIP as opaque bytes unless Steward has a concrete need to interpret its contents.

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
3. existing-World discovery;
4. safe import capture;
5. environment inspection;
6. environment preparation where required by the supported slice;
7. state restore;
8. local launch only when its ownership/session semantics are proven;
9. real session observation;
10. safe state capture and validation;
11. temporary host launch only when its server/runtime semantics are proven;
12. graceful hosted-session shutdown;
13. optional automatic Join;
14. exact environment reproduction where justified;
15. expose only the `GameAdapterCapabilities` proven by the implemented slice;
16. add adapter-specific deterministic tests and a dedicated CI lane where appropriate;
17. add the Desktop project reference and one entry to `DesktopGameAdapterCatalog`;
18. qualify the adapter head independently before mechanical integration;
19. rerun the full five-workflow matrix on the exact combined SHA.

The first target is one truthful vertical slice, not many partially claimed workflows. An adapter that safely supports discovery/import/environment/state handling may be visible in Steward while launch/hosting remains unavailable; missing runtime evidence is represented by absent capability flags, not invented generic behavior.

Terraria, Stardew Valley, and Necesse are current concrete examples of that narrower entry point. Terraria preserves one opaque vanilla `.wld`; Stardew Valley preserves exactly its two current vanilla save files while excluding `_old` recovery files; Necesse preserves the game's native compressed World ZIP byte-for-byte instead of adding a second archive layer. All three verify the exact Steam build and advertise only `ExactGameVersion`; launch/hosting capabilities remain absent.

Adding a game normally does **not** require changes to Core, backend, Infrastructure, or ordinary Desktop action logic. If implementation appears to require such a change, first prove that the need is genuinely universal rather than an adapter-specific edge case.

## Acceptance tests

Every exposed capability must have evidence at its actual trust/ownership boundary.

For a discovery/import/state-only slice, prove at minimum:

```text
discover intended installation/World without mutation
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
