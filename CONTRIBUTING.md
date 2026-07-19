# Contributing

The repository is being built as a durable product rather than a disposable prototype. Contributions should preserve the architecture boundaries documented in `docs/ARCHITECTURE.md` and `docs/ENGINEERING.md`.

## Local prerequisites

- .NET SDK compatible with `global.json`
- Git
- A game installation only when manually validating that game's adapter

## Before changing code

Identify the correct boundary:

- Product rules and World lifecycle belong in `SharedWorlds.Core`.
- Generic technical implementations belong in `SharedWorlds.Infrastructure`.
- Game-specific behavior belongs in that game's adapter project.
- UI/CLI composition belongs outside Core.

Do not put game-specific branches into Core.

## Validation

Run:

```bash
dotnet restore SharedWorlds.sln
dotnet format SharedWorlds.sln --verify-no-changes --no-restore
dotnet build SharedWorlds.sln --configuration Release --no-restore
dotnet test SharedWorlds.sln --configuration Release --no-build
```

CI performs equivalent checks.

## Adding a game adapter

1. Create an independent adapter project under `src/SharedWorlds.GameAdapters/<Game>/`.
2. Reference `SharedWorlds.Core` only as required by the adapter contract.
3. Keep launcher, platform, mod-manager, and filesystem knowledge inside the adapter.
4. Add adapter-focused tests that use temporary files and never a developer's real saves.
5. Document supported capabilities and known limitations.
6. Add the adapter to the composition root only after discovery is safe.

See `docs/ADAPTER_GUIDE.md`.

## Pull requests

Keep changes focused. Describe:

- what changed
- why the change belongs at that architectural boundary
- failure modes considered
- tests added or run
- documentation changed

A change that modifies persisted data, revision semantics, or adapter contracts should explicitly describe compatibility impact.

## User data safety

Never use a contributor's real save files in automated tests. Import and restore operations must work on copies or isolated workspaces. Destructive filesystem operations require validated product-owned paths.
