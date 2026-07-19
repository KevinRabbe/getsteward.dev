# Engineering Standards

This document defines the default engineering rules for the product. These rules are intentionally stricter than a disposable prototype because the architecture is expected to survive additional games, storage backends, platforms, and user interfaces.

## Dependency direction

The allowed direction is:

```text
SharedWorlds.Cli / future desktop UI   (composition root)
    |-- references SharedWorlds.Core
    |-- references concrete infrastructure implementations
    `-- references selected game adapter assemblies

individual game adapter -> SharedWorlds.Core
infrastructure          -> SharedWorlds.Core
Core                    -X-> concrete adapter
Core                    -X-> concrete storage backend
Core                    -X-> Steam / CurseForge / Modrinth / launcher SDK
```

`SharedWorlds.Core` owns product semantics and ports. Concrete integrations depend inward on those contracts. The composition root is the only layer expected to know which concrete implementations are assembled for a runnable product.

## Project boundaries

- `SharedWorlds.Core`: domain models, World lifecycle, revision semantics, storage/session ports, adapter contract.
- `SharedWorlds.Infrastructure`: replaceable technical implementations such as local storage.
- `SharedWorlds.GameAdapters/<Game>`: one independently compiled adapter project per game.
- `SharedWorlds.Cli`: temporary development composition root and manual test harness.
- `tests/*`: automated tests aligned to production project boundaries.

A future desktop client should be a new project. It must not absorb Core responsibilities.

## Build policy

The repository uses:

- .NET 10 target framework.
- `global.json` to define the accepted SDK line.
- warnings as errors.
- nullable reference types.
- deterministic builds.
- code-style enforcement during build.
- centralized NuGet package versions through `Directory.Packages.props`.

Do not disable a warning globally to make one local problem disappear. Fix the code or suppress the warning at the narrowest justified scope with an explanation.

## Continuous integration

CI is required to verify the repository independently of a developer workstation.

Current gates:

- formatting verification on Linux
- Release build on Linux
- Release build on Windows
- automated tests on Linux
- automated tests on Windows

A change is not considered integration-ready merely because one developer machine builds it.

## Dependency policy

External packages are added only when they remove meaningful implementation or maintenance risk.

Before adding a package, ask:

1. Is the capability already in the .NET runtime or SDK?
2. Is the dependency maintained and appropriately licensed?
3. Does it need to exist in Core, or can it remain inside a concrete integration project?
4. Can its version be managed centrally?

Game-specific dependencies belong in that game's adapter project whenever possible.

## Testing policy

Every fixed bug should gain a regression test when practical.

Priority order:

1. Core invariants and revision semantics.
2. Storage durability and compatibility.
3. Adapter parsing/discovery logic that can run without launching the game.
4. Integration tests against real game installations where automation is safe.
5. Manual end-to-end acceptance tests for launch/session behavior.

Tests must not depend on a developer's real saves or modify a real game installation.

## Filesystem safety

The product handles user data, so filesystem operations are treated as trust boundaries.

Rules:

- Never modify an imported source save during import.
- Prepare work in isolated directories where the adapter supports it.
- Prefer write-to-temporary-file plus atomic replace/move for metadata.
- Do not advance the canonical World head until the new state payload is durably stored.
- Keep previous immutable state revisions available for recovery.
- Validate paths received from external metadata before destructive operations.
- Never recursively delete an unverified user-provided path.

## Cancellation and async behavior

Long-running I/O and process waits must accept and propagate `CancellationToken` where the surrounding contract supports it.

Avoid sync-over-async. Do not block worker threads while waiting for a game process, network operation, or large file copy.

## Failure semantics

Failures should be explicit and recoverable.

A failed session commit must not silently advance the canonical World head. A crash or incomplete capture should preserve the last known-good revision and surface recovery state to the caller.

Do not convert an unknown state into success merely to keep the UI moving.

## Versioned data

Persisted manifests and future network messages must be treated as versioned contracts.

- `EnvironmentManifest.SchemaVersion` is part of the compatibility boundary.
- Additive fields should be preferred over destructive schema changes.
- Incompatible persisted data should fail with a controlled compatibility error rather than undefined behavior.
- Migrations belong in infrastructure/application migration code, not scattered through UI logic.

## Logging and observability

The current foundation does not yet have a logging abstraction. When added, structured logging should be wired at the composition root and passed through standard abstractions.

Never log secrets, authentication tokens, private join tokens, or arbitrary save contents.

## Adapter rules

An adapter owns game-specific knowledge including:

- installation discovery
- save/world discovery
- environment inspection
- import capture
- environment preparation
- state restore/capture
- host/client launch
- real session-end detection

Core must never add a branch such as `if (game == "factorio")`.

## Definition of done

A product change is not complete until, as applicable:

- the relevant project boundary is respected
- automated tests cover the invariant or failure fixed
- `dotnet build` succeeds with warnings as errors
- `dotnet test` succeeds
- formatting verification succeeds
- documentation is updated when behavior or architecture changes
- no real user save is required or modified by automated tests
