# Contributing

Steward is a commercial product moving beyond prototype validation. Contributions must preserve its product identity, user-state safety, and architecture boundaries.

## Required reading

Before changing product behavior or architecture, read in this order:

1. `docs/NON_NEGOTIABLE_RULES.md`
2. `docs/PRODUCT_BOUNDARY.md`
3. `docs/DECISIONS.md`
4. `docs/ARCHITECTURE.md`
5. the relevant subsystem or adapter document

A lower-level implementation must not silently contradict those documents.

## Product test

Before adding a feature, abstraction, service, or workflow, ask:

> Does this directly help Steward move the latest valid World into a playable session and return the updated valid state for the next player?

When the answer is no, the default is remove, defer, or delegate to Steam, the game, or another existing platform.

Do not reintroduce obsolete directions such as generic save merging, Git-style branches/Forks, social-platform features, complex ownership/governance, or permanent game-server fleets without an explicit replacement of the product boundary.

## Local prerequisites

- .NET SDK compatible with `global.json`;
- Git;
- a game installation only when manually validating that adapter.

## Choose the correct boundary

- Universal World lifecycle and state rules belong in `SharedWorlds.Core`.
- Replaceable storage, coordination, persistence, and recovery implementations belong in `SharedWorlds.Infrastructure`.
- Game-specific discovery, environment, launch, process/server observation, capture, and validation belong in that game's adapter.
- Commercial UI and background runtime composition belong in `SharedWorlds.Desktop`.
- `SharedWorlds.Cli` and `tools/*` are development and validation surfaces.

Never add game-name branches to Core.

## State-safety requirements

Every relevant change must preserve:

- one current valid World state;
- at most one active writable Steward session;
- immutable published revisions;
- current-head advancement after complete durable storage and verification;
- stale-head rejection;
- previous valid state on failure;
- post-launch workspace recovery evidence;
- explicit adapter cleanup ownership;
- adapter-owned session-end and safe-capture observation.

Launching the game is not a complete lifecycle.

## Validation

Run:

```bash
dotnet restore SharedWorlds.sln
dotnet format SharedWorlds.sln --verify-no-changes --no-restore
dotnet build SharedWorlds.sln --configuration Release --no-restore
dotnet test SharedWorlds.sln --configuration Release --no-build
```

CI performs equivalent checks where supported.

Manual real-game validation is required when process, server, save, launcher, or environment behavior cannot be proven safely through automated tests.

## Adding a game adapter

1. Create an independent adapter project under `src/SharedWorlds.GameAdapters/<Game>/`.
2. Reference Core contracts without adding game-specific fields to Core.
3. Keep launcher, platform, mod-manager, filesystem, server, and save knowledge inside the adapter.
4. Add tests using temporary controlled data, never a contributor's live saves.
5. Prove import, restore, launch, real session observation, safe capture, commit, and replay.
6. Document supported capabilities, validated paths, and known limitations.
7. Register the adapter in the composition root only after discovery and import are safe.
8. Generalize behavior only after multiple real adapters prove the same pattern.

See `docs/ADAPTER_GUIDE.md`.

## Pull requests

Keep changes focused and describe:

- what changed;
- why it belongs at that boundary;
- product rule or decision affected;
- failure modes considered;
- tests and real-game validation performed;
- persistence or compatibility impact;
- documentation updated;
- scope deliberately not added.

A change to product boundaries, state semantics, persisted data, storage contracts, session coordination, or adapter contracts requires documentation in the same change.

## User-data safety

- Never require or mutate a contributor's real save in automated tests.
- Import from copies or read-only sources.
- Restore into adapter-owned controlled workspaces.
- Validate paths before destructive operations.
- Never recursively delete an unverified path.
- Preserve recovery candidates after uncertain post-launch failure.
- Do not log authentication secrets, private join data, or arbitrary save contents.