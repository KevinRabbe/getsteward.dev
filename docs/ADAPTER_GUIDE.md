# Game Adapter Guide

## Purpose

A game adapter isolates everything specific to one game while letting Core run the same complete World handoff lifecycle.

> Core knows what must happen. The adapter knows how this game makes it happen.

Current validation adapters are Factorio and Palworld. Future adapters must preserve the same boundary without forcing their edge cases into Core.

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

## Import capture

The adapter converts a detected World into a portable `StatePackage` suitable for initial durable storage.

The package may represent:

- one ZIP;
- a directory archive;
- a database;
- several related files;
- launcher-managed state.

Import must leave the source untouched.

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

Implement in this order:

1. installation discovery;
2. existing-World discovery;
3. safe import capture;
4. environment inspection;
5. environment preparation;
6. state restore;
7. local launch;
8. real session observation;
9. safe state capture and validation;
10. temporary host launch;
11. graceful hosted-session shutdown;
12. optional native join;
13. exact environment reproduction where justified.

The first target is one complete vertical handoff, not many partially supported workflows.

## Acceptance test

A new adapter is product-relevant only when it can prove:

```text
import known World
-> restore it
-> launch it
-> make a visible gameplay change
-> observe safe session end
-> capture and commit the change
-> restore the committed result in the next session
```

Hosted support additionally proves that the server state, not merely a client process, controls the capture boundary.