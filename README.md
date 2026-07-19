# SharedWorlds

> Steam is the platform. Games are adapters. Worlds are the product.

This repository contains the durable product foundation for a shared-World system for games. The repository and product name are temporary and can change without changing the architecture.

The player-facing goal is simple:

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
CLI / future desktop UI
   |
World Core
   |-- IGameAdapter ------------> Factorio / 7DTD / Project Zomboid adapters
   |-- IWorldStorage -----------> local / Steam / future storage
   |-- IWorldSessionCoordinator -> Steam lobby / future coordination
   `-- IWorkspaceRecoveryStore -> durable prepared-workspace recovery metadata
```

Concrete integrations depend inward on Core contracts. Core never depends on a concrete game, storage backend, launcher, or platform SDK.

## Product-grade repository foundation

The repository now enforces a consistent engineering baseline:

- .NET 10 SDK policy through `global.json`
- nullable reference types
- warnings as errors
- deterministic builds
- build-time code-style enforcement
- centralized NuGet package versions
- independent game-adapter assemblies
- versioned persisted-document envelopes with explicit migration paths
- durable prepared-workspace recovery tracking
- automated unit/integration-boundary tests
- Linux and Windows CI build/test jobs
- formatting verification in CI
- contribution and security policies

The current CLI is deliberately a development harness. A future desktop application will be a separate composition/UI project rather than absorbing Core behavior.

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — system boundaries, dependency direction, platform neutrality, host model, and architecture philosophy.
- [Engineering Standards](docs/ENGINEERING.md) — build, dependency, testing, filesystem-safety, compatibility, and definition-of-done rules.
- [Domain Model](docs/DOMAIN_MODEL.md) — World, environment revisions, state revisions, manifests, packages, sessions, and identities.
- [Game Adapter Guide](docs/ADAPTER_GUIDE.md) — adapter responsibilities, contract rules, and how to add a new game without contaminating Core.
- [World Lifecycle](docs/WORLD_LIFECYCLE.md) — Import, Continue, Join, host handoff, Sandbox, Fresh Test World, Fork, Restore, and recovery semantics.
- [Storage](docs/STORAGE.md) — local persistence layout, atomic writes, immutable revisions, and the future remote-storage boundary.
- [Persistence Compatibility](docs/PERSISTENCE_COMPATIBILITY.md) — document envelopes, schema versions, legacy schema-0 migration, and controlled compatibility failures.
- [Workspace Recovery](docs/WORKSPACE_RECOVERY.md) — Active, RecoveryPending, and CleanupPending workspace lifecycle semantics.
- [Factorio Adapter](docs/FACTORIO.md) — current first vertical slice, discovery, state capture, environment inspection, launch behavior, limitations, and test checklist.
- [Design Decisions](docs/DECISIONS.md) — durable architectural decisions and constraints that future work should preserve.
- [Roadmap](docs/ROADMAP.md) — phased development plan from local Factorio validation to shared host coordination and recovery hardening.
- [Contributing](CONTRIBUTING.md) — development and validation workflow.
- [Security](SECURITY.md) — vulnerability reporting and security-sensitive areas.

## Initial adapters

1. **Factorio** — first active vertical slice.
2. **7 Days to Die** — planned to stress environment isolation and external mod setups.
3. **Project Zomboid** — planned to stress Workshop-heavy environments.

Each adapter is an independently compiled project. The product intentionally focuses on one complete adapter lifecycle before expanding breadth.

## Current Factorio vertical slice

Implemented in code on the current development line:

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
-> persist Active workspace recovery record
-> launch Factorio host
-> wait for adapter-observed session end
-> capture resulting save
-> create StateRevision S2
-> advance canonical World head
-> discard prepared workspace
-> remove recovery record
```

If a session starts but canonical commit does not complete, the prepared workspace is preserved and marked `RecoveryPending`. A hard application/OS crash can leave an `Active` record, which is treated as a conservative interrupted-session recovery candidate on the next startup.

The Factorio runtime path still requires manual end-to-end validation on a real Windows machine with Factorio installed. Automated tests cover environment fingerprint stability, persistence compatibility, local storage integrity, workspace recovery semantics, and Factorio save discovery without touching real user saves.

## Development CLI

Current commands:

```text
discover
import-factorio <save-name>
continue-factorio <world-id>
recovery
```

Run them through the CLI project, for example:

```bash
dotnet run --project src/SharedWorlds.Cli -- discover
```

`recovery` lists durable prepared-workspace recovery records. An `Active` record discovered after process restart is a possible interrupted-session candidate; `RecoveryPending` means gameplay started but canonical commit did not complete.

These commands are development interfaces, not the final product UX.

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

## Persisted data compatibility

JSON metadata is stored inside a versioned outer envelope containing a stable `documentType`, `schemaVersion`, and `payload`.

The pre-envelope foundation format is explicitly treated as schema version 0 and has a registered migration path into the current version. Unsupported future schema versions fail with `PersistedDataCompatibilityException`; they are never silently overwritten or interpreted as defaults.

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
  SharedWorlds.Core/
  SharedWorlds.Infrastructure/
  SharedWorlds.Cli/
  SharedWorlds.GameAdapters/
    Factorio/
      SharedWorlds.GameAdapters.Factorio.csproj
    SevenDaysToDie/
      SharedWorlds.GameAdapters.SevenDaysToDie.csproj
    ProjectZomboid/
      SharedWorlds.GameAdapters.ProjectZomboid.csproj

tests/
  SharedWorlds.Core.Tests/
  SharedWorlds.Infrastructure.Tests/
  SharedWorlds.GameAdapters.Factorio.Tests/

docs/
  ARCHITECTURE.md
  ENGINEERING.md
  DOMAIN_MODEL.md
  ADAPTER_GUIDE.md
  WORLD_LIFECYCLE.md
  STORAGE.md
  PERSISTENCE_COMPATIBILITY.md
  WORKSPACE_RECOVERY.md
  FACTORIO.md
  DECISIONS.md
  ROADMAP.md
```

## Local validation

```bash
dotnet restore SharedWorlds.sln
dotnet format SharedWorlds.sln --verify-no-changes --no-restore
dotnet build SharedWorlds.sln --configuration Release --no-restore
dotnet test SharedWorlds.sln --configuration Release --no-build
```

CI executes equivalent checks and builds/tests on both Linux and Windows.

## Immediate product milestone

Validate the Factorio vertical slice on the target Windows machine:

1. compile the solution
2. run `discover`
3. verify the correct Factorio installation and saves are found
4. import a disposable test save
5. verify the original source save remains untouched
6. run Continue
7. make and save an in-game change
8. exit cleanly
9. confirm a new canonical state revision was committed
10. confirm the prepared workspace was cleaned and no recovery record remains
11. Continue again and verify the new state loads

Reality test first. Then automated environment synchronization and networking.
