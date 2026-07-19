# SharedWorlds

> Steam is the platform. Games are adapters. Worlds are the product.

This repository contains the first durable implementation of a shared-World system for games. The repository/product name is temporary and can change later without changing the architecture.

The product goal is simple from the player's perspective:

> Select the World. The system prepares the right environment and state. Then play.

The user should not need to manage who permanently owns the host, which exact mod folder is active, where the canonical save lives, or whether a game came from Steam, CurseForge, Modrinth, Prism, or somewhere else.

## Core architecture rule

The Core must never contain game-specific branches such as `if (game == Factorio)`.

- **Core** knows *what* must happen: World lifecycle, revisions, canonical state, storage boundaries, and session semantics.
- **Game adapters** know *how* a specific game makes it happen.
- **Adapters may use any ecosystem internally**: Steam, Steam Workshop, CurseForge, Modrinth, Prism, custom launchers, filesystem layouts, registry discovery, or game-specific APIs.
- **Storage and live coordination are generic boundaries**. Steam-backed implementations may be added later without making Steam a Core dependency.

The intended dependency direction is:

```text
App / UI
   |
World Core
   |-- IGameAdapter --------> game-specific behavior
   |-- IWorldStorage -------> local / Steam / future storage
   `-- IWorldSessionCoordinator -> Steam lobby / future coordination
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — system boundaries, dependency direction, platform neutrality, host model, and architecture philosophy.
- [Domain Model](docs/DOMAIN_MODEL.md) — World, environment revisions, state revisions, manifests, packages, sessions, and identities.
- [Game Adapter Guide](docs/ADAPTER_GUIDE.md) — adapter responsibilities, contract rules, and how to add a new game without contaminating Core.
- [World Lifecycle](docs/WORLD_LIFECYCLE.md) — Import, Continue, Join, host handoff, Sandbox, Fresh Test World, Fork, Restore, and recovery semantics.
- [Storage](docs/STORAGE.md) — local persistence layout, atomic writes, immutable revisions, and the future remote-storage boundary.
- [Factorio Adapter](docs/FACTORIO.md) — current first vertical slice, discovery, state capture, environment inspection, launch behavior, limitations, and test checklist.
- [Design Decisions](docs/DECISIONS.md) — durable architectural decisions and constraints that future work should preserve.
- [Roadmap](docs/ROADMAP.md) — phased development plan from local Factorio validation to shared host coordination and recovery hardening.

## Initial adapters

1. **Factorio** — first active vertical slice.
2. **7 Days to Die** — planned to stress environment isolation and external mod setups.
3. **Project Zomboid** — planned to stress Workshop-heavy environments.

The project intentionally focuses on one complete adapter lifecycle before expanding breadth.

## Current Factorio vertical slice

Implemented in code on the current development branch:

```text
discover installation
-> discover existing save
-> import save as World
-> create EnvironmentRevision E1
-> create StateRevision S1
-> persist World
-> Continue
-> prepare isolated working copy
-> restore canonical state
-> launch Factorio host
-> wait for adapter-observed session end
-> capture resulting save
-> create StateRevision S2
-> advance canonical World head
```

This code still requires compile and runtime validation on a real machine with the .NET SDK and Factorio installed.

## Development CLI

Current commands:

```text
discover
import-factorio <save-name>
continue-factorio <world-id>
```

These are development interfaces, not the final product UX.

## Revision model

Environment and state are versioned independently:

```text
World = Environment E7 + State S143
```

Normal play may advance only state:

```text
E7 + S143 -> E7 + S144
```

Changing the relevant game/mod environment creates a new environment revision:

```text
E7 -> E8
```

## Canonical host rule

Only one canonical host may advance a shared World at a time.

The group owns the World. No player permanently owns the host role.

Future host handoff is a controlled restart:

```text
save
-> close old host
-> capture and commit latest state
-> restore on new host
-> launch new host
-> others join
```

The project does not attempt live process migration.

## Environment fingerprint rule

`EnvironmentManifest` is authoritative.

`EnvironmentFingerprint.Compute()` hashes only a canonicalized representation of the adapter-produced manifest. The fingerprint is a disposable comparison/cache aid and must not be treated as proof that every game file is intact.

The normal workflow must not repeatedly hash entire game installations.

## Repository structure

```text
src/
  SharedWorlds.App/
  SharedWorlds.Core/
  SharedWorlds.GameAdapters/
    Factorio/
    SevenDaysToDie/
    ProjectZomboid/
  SharedWorlds.Infrastructure/

docs/
  ARCHITECTURE.md
  DOMAIN_MODEL.md
  ADAPTER_GUIDE.md
  WORLD_LIFECYCLE.md
  STORAGE.md
  FACTORIO.md
  DECISIONS.md
  ROADMAP.md
```

## Immediate next step

Build and run the Factorio vertical slice on the target Windows machine:

1. compile the solution
2. run `discover`
3. verify the correct Factorio installation and saves are found
4. import a disposable test save
5. verify the original source save remains untouched
6. run Continue
7. make and save an in-game change
8. exit cleanly
9. confirm a new canonical state revision was committed
10. Continue again and verify the new state loads

Reality test first. Then add automated environment synchronization and networking.
