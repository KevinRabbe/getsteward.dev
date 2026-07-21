# Steward

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward is a commercial Windows desktop product moving beyond prototype validation.

Steam is the platform. Games are adapters. Worlds are the product.

A player starts or temporarily hosts the latest valid World state, plays, and leaves the updated state ready for whoever continues next. The expensive game or dedicated-server process runs only while somebody is playing.

## Product boundary

Steward owns the complete World handoff:

```text
latest valid World state
-> reserve one writable session
-> prepare the correct environment
-> restore the World on this device
-> launch local play or temporary hosting
-> observe the game/server session
-> capture the updated state safely
-> store and verify it
-> advance the shared World state last
-> make the World available to the next player
```

Launching alone is not success. Steward remains active in the background until capture, storage, verification, commit, or recovery is complete.

Steam and the game continue to handle players, game ownership, installations, invitations, multiplayer joining, Workshop content, and dedicated-server tooling wherever possible.

## Non-goals

Steward is not:

- a Git-style branching or merging system for saves;
- a universal save merger;
- a social network or Discord replacement;
- a complex ownership, party, role, or governance platform;
- a public server browser;
- a permanent game-server fleet;
- a live game-process migration system;
- DRM for copies already received by another device.

Groups organize themselves. Steward keeps the selected shared World state safe, current, portable, and playable.

## Core architecture

The Core contains universal lifecycle and state-safety behavior. Game-specific behavior stays behind `IGameAdapter`.

```text
Desktop / background runtime / development tools
    |
    v
World Core
    |-- IGameAdapter
    |     |-- Factorio
    |     `-- Palworld
    |-- IWorldStorage
    |     |-- local filesystem
    |     `-- future shared durable storage
    `-- IWorldSessionCoordinator
          |-- local reservation
          `-- future distributed reservation
```

The Core must never contain game-name branches. One unusual game must not expand the universal World model.

## Essential invariants

- A World has one current valid state.
- At most one Steward session may write that World at a time.
- Published revisions are immutable.
- The current World state advances only after the replacement is completely captured, stored, verified, and committed.
- A failed operation leaves the previous valid state authoritative.
- Post-launch workspaces are recovery assets until success is certain.
- Session observation and safe capture timing are adapter-owned.
- Host switching happens between sessions through stop, capture, commit, restore, and launch.
- Generic save merging and Git-style branches are not supported.

See [Non-Negotiable Rules](docs/NON_NEGOTIABLE_RULES.md) for the complete rule set.

## Current implementation status

### Generic lifecycle

The repository contains adapter-driven import, environment preparation, restore, local/host launch, session observation, capture, immutable state storage, current-head advancement, and workspace recovery foundations.

A canonical state transaction boundary additionally proves:

- content hashing while storing;
- immutable revision storage;
- serialized per-World commits;
- atomic current-head advancement;
- stale-head rejection;
- unchanged-candidate detection;
- preservation of the previous head on failure.

### Factorio

Factorio has validated installation/save discovery, safe import, isolated preparation, local play, hosted launch paths, Steam process handoff observation, environment/mod handling, state capture, commit, and replay on a real Windows Steam installation.

### Palworld

Palworld has validated client and dedicated-server discovery, local World discovery, migration of an unchanged World directory into the dedicated-server layout, server selection and launch, capture to a portable package, canonical restore through staging/rollback, restored-byte verification, and launch from restored canonical state.

Player identity conversion between local co-op and dedicated-server identities remains a Palworld-specific edge case rather than a generic Core problem.

### Desktop

The WPF desktop currently provides a unified Factorio/Palworld adapter pipeline, game-first World browsing, import, artwork resolution, and direct lifecycle actions. Parts of the UI composition remain transitional and will be simplified around the stable product workflow.

## Next decisive milestone

Prove a real two-device handoff:

```text
PC A commits state N+1
-> PC B retrieves and continues N+1
-> PC B commits N+2
-> PC A retrieves N+2
```

While one device owns the writable session reservation, another device must not start a competing Steward session.

This requires only two new shared boundaries beneath the existing lifecycle:

- durable shared World-state storage;
- distributed one-writer session coordination.

It does not require branches, merging, parties, ownership hierarchies, or permanent game servers.

## Documentation

Start with the [Documentation Index](docs/README.md).

Authoritative product documents:

- [Non-Negotiable Rules](docs/NON_NEGOTIABLE_RULES.md)
- [Product Boundary](docs/PRODUCT_BOUNDARY.md)
- [Design Decisions](docs/DECISIONS.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Domain Model](docs/DOMAIN_MODEL.md)
- [World Lifecycle](docs/WORLD_LIFECYCLE.md)
- [Roadmap](docs/ROADMAP.md)

Engineering and subsystem documentation remains under [`docs/`](docs/).

## Repository structure

```text
src/
  SharedWorlds.Core/
  SharedWorlds.Infrastructure/
  SharedWorlds.Desktop/
  SharedWorlds.Cli/
  SharedWorlds.GameAdapters/
    Factorio/
    Palworld/

tests/
tools/
docs/
```

## Validation

```bash
dotnet restore SharedWorlds.sln
dotnet format SharedWorlds.sln --verify-no-changes --no-restore
dotnet build SharedWorlds.sln --configuration Release --no-restore
dotnet test SharedWorlds.sln --configuration Release --no-build
```

The repository uses nullable reference types, warnings as errors, deterministic builds, versioned persisted documents, typed product failures, automated tests, and Windows/Linux CI where applicable.

## Working rule

Before adding a feature or abstraction, ask:

> **Does this directly help Steward move the latest valid World into a playable session and return the updated valid state for the next player?**

When the answer is no, remove it, defer it, or leave it to Steam, the game, or another existing system.