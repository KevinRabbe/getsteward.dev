# Game Adapter Guide

## Purpose

A game adapter isolates all knowledge that is specific to one game.

The Core should be able to drive the same lifecycle for Factorio, Minecraft, 7 Days to Die, Project Zomboid, or a future game without learning each game's save layout, launcher, mod ecosystem, or networking flags.

## Rule

> Core knows what must happen. Adapter knows how this game makes it happen.

Adapters may internally use any relevant ecosystem or tool. That is intentionally invisible to the Core.

Examples:

- Factorio adapter may inspect Steam libraries and Factorio user-data directories.
- A Minecraft adapter may use Prism Launcher, CurseForge, Modrinth, or manual instances.
- A 7 Days to Die adapter may manage isolated mod folders or multiple installations.

## Current contract

`IGameAdapter` exposes these responsibilities.

### Identity and capabilities

- `Id`
- `DisplayName`
- `Capabilities`

Capabilities describe what an adapter can currently automate, such as mods, automatic host launch, automatic client join, exact game versions, exact mod versions, or environment isolation.

Capabilities are descriptive. The Core should use them to decide which workflows are available without branching on a game name.

### Discover installations

`DiscoverInstallationsAsync`

Find supported installations of this game.

The adapter may inspect:

- Steam libraries
- launcher metadata
- registry entries
- conventional paths
- portable installations
- user-selected paths

The Core does not know how discovery works.

### Discover existing worlds

`DiscoverWorldsAsync`

Find existing saves/worlds that can be imported.

The adapter decides:

- which files/directories count as worlds
- which autosaves or temporary states should be hidden
- how names are presented

### Inspect environment

`InspectEnvironmentAsync`

Produce an `EnvironmentManifest` describing the relevant playable environment.

The adapter decides which details matter.

Examples:

- game version
- enabled mods
- exact mod versions
- mod source
- config values

The Core stores the manifest but does not interpret game-specific semantics.

### Capture an existing detected world

`CaptureDetectedWorldAsync`

Convert a discovered save/world into an adapter-owned state package suitable for importing as the first canonical `StateRevision`.

This operation belongs to the adapter because a game's world may be:

- one ZIP file
- one database
- a directory tree
- multiple related files
- a launcher-managed instance

The Core must never assume one universal format.

### Prepare environment

`PrepareEnvironmentAsync`

Create or select an isolated playable workspace matching the requested environment.

Possible strategies include:

- separate mod directories
- profiles
- symlinks or junctions
- transactional file swapping
- separate installations as a last resort
- launcher-managed instances

The Core does not care which strategy is used.

### Restore state

`RestoreStateAsync`

Restore a canonical state package into the prepared workspace.

### Capture state

`CaptureStateAsync`

Capture the workspace after play into a new state package.

This is used to create the next canonical `StateRevision`.

### Launch host

`LaunchHostAsync`

Start the game in the adapter-specific host mode.

### Launch client

`LaunchClientAsync`

Start or connect the game as a client.

### Wait for session end

`WaitForSessionEndAsync`

Wait until the relevant game session has actually ended.

This belongs to the adapter rather than Core because launchers may spawn another process, games may have dedicated-server processes, and different games have different lifecycle semantics.

## Adapter isolation rule

Game-specific helpers should live beside the adapter, not in Core.

Current Factorio helpers:

- installation discovery
- environment inspection
- save/world operations

Future adapters should follow the same principle without being forced into the same internal file structure.

## What an adapter must not do

An adapter should not redefine universal product semantics.

It should not decide:

- who owns the canonical host role
- whether a new canonical revision is accepted
- World membership semantics
- Fork versus Sandbox product semantics
- durable global history policy

Those belong to Core.

## Adding a new game

A new adapter should be implemented in this order:

1. Installation discovery.
2. Existing-world discovery.
3. Import/capture of one existing world.
4. Environment inspection.
5. Isolated preparation.
6. State restore.
7. Host launch.
8. Session-end observation.
9. State capture.
10. Client join.
11. Exact environment reproduction where supported.

The first goal is one complete vertical slice, not a partially implemented list of many games.
