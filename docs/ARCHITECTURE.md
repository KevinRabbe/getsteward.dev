# Architecture

## Core product idea

The product is the **World**.

A World is not merely a save file and it is not tied to one game distribution platform. It represents the reproducible playable state shared by a group:

- game identity
- game version
- save/world state
- mods and mod versions where available
- configuration
- launch requirements
- members and permissions
- current canonical revision
- current host/session state

The central architectural rule is:

> Core knows **what** must happen. Adapters know **how a game makes it happen**.

The Core must not contain game-specific branches such as `if (game == Factorio)`.

## Dependency direction

```text
Desktop / CLI
    |
    v
World Core
    |-- World lifecycle
    |-- Environment revisions
    |-- State revisions
    |-- Session coordination contracts
    |-- Storage contracts
    |
    +--> IGameAdapter
    |       |
    |       +--> Factorio-specific behavior
    |       +--> 7 Days to Die-specific behavior
    |       +--> Project Zomboid-specific behavior
    |       +--> future game-specific behavior
    |
    +--> IWorldStorage
    |       +--> Local filesystem backend
    |       +--> future Steam-backed backend
    |       +--> future other backends
    |
    +--> IWorldSessionCoordinator
            +--> future Steam lobby implementation
            +--> future other coordination implementations
```

The Core depends only on abstractions. Concrete platform and game behavior depends inward on Core contracts.

## Platform neutrality

Steam may be the primary platform used by the application for identity, distribution, storage, coordination, or networking, but Steam is **not** a requirement for supported games.

A game adapter may internally use:

- Steam
- Steam Workshop
- CurseForge
- Modrinth
- Prism Launcher
- another launcher
- a game-specific API
- filesystem conventions
- registry entries
- manual user-selected paths

The Core does not need to know which source is used.

This permits a future Minecraft adapter, for example, to use CurseForge or Modrinth internally while participating in the exact same World lifecycle as Factorio.

## Major boundaries

### World Core

Owns universal behavior:

- creating/importing Worlds
- environment and state revision relationships
- canonical World head
- Continue workflow
- future Fork, Sandbox, Restore, and host handoff workflows
- calling adapters through `IGameAdapter`
- calling durable storage through `IWorldStorage`
- calling live coordination through `IWorldSessionCoordinator`

It must not know save locations, executable paths, mod layouts, launcher behavior, or game-specific command-line arguments.

### Game adapters

Own all game-specific knowledge.

An adapter is responsible for:

- locating installations
- locating existing saves/worlds
- inspecting the relevant environment
- preparing the required environment
- importing/capturing game state
- restoring game state
- launching a host
- launching a client
- detecting when the relevant game session has actually ended

Adapters may be internally complicated. That complexity must remain isolated from the Core.

### Storage

`IWorldStorage` is the durable persistence boundary.

The current implementation is local filesystem storage. A future Steam-backed implementation can be added without changing World Core semantics.

Storage is responsible for durable World metadata and revision packages. Storage is not the same thing as live session coordination.

### Session coordination

`IWorldSessionCoordinator` represents transient shared-session state such as:

- who currently owns the canonical host role
- whether a World is available or hosting
- host handoff requests

A Steam lobby is one possible implementation. The Core does not depend directly on Steam lobbies.

## Revisions

Environment and state are versioned separately.

```text
World = Environment E7 + State S143
```

Normal play can advance only state:

```text
E7 + S143 -> E7 + S144
```

Changing mods, game version, or relevant configuration can create a new environment revision:

```text
E7 -> E8
```

This separation avoids duplicating an entire environment revision for every save update.

## Canonical host model

Only one canonical host may advance the canonical World at a time.

If nobody is playing, the World is available. The first member who starts canonical play becomes the host. Other members join that host instead of creating conflicting canonical branches.

Independent experimentation belongs in a Sandbox or Fork, not in a second canonical host session.

## Host handoff

Host handoff is intentionally a controlled restart, not live process migration:

```text
request handoff
-> current host saves
-> current session closes
-> latest state is captured and committed
-> new host restores latest state
-> new host launches
-> other members join new host
```

This is much simpler and more reliable than trying to migrate a live game process.

## Environment fingerprinting

`EnvironmentManifest` is authoritative.

`EnvironmentFingerprint` is only a temporary/cached comparison aid computed from a canonicalized manifest. It must not be treated as proof that every file in a game installation is intact.

The system should not routinely hash entire game installations. Deep file hashing is reserved for explicit verification, repair, corruption investigation, or first-import integrity checks where justified.

## Architecture philosophy

The project follows these principles:

1. Build a narrow implementation of the final architecture, not a broad disposable prototype.
2. Keep the Core boring and stable.
3. Push game-specific complexity into adapters.
4. Delegate work to legitimate existing infrastructure whenever possible.
5. Avoid owning infrastructure when a replaceable external service can provide it.
6. Never let one strange game contaminate the universal World model.
7. Prefer explicit contracts over hidden assumptions.
