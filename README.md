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
    |     |-- Palworld
    |     |-- 7 Days to Die
    |     `-- Project Zomboid
    |-- IWorldStorage
    |     |-- local filesystem
    |     `-- shared durable object storage
    `-- IWorldSessionCoordinator
          |-- local reservation
          `-- distributed backend reservation
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

### Generic lifecycle and backend

The repository contains adapter-driven import, environment preparation and verification, restore, local/host launch boundaries, session observation, capture, immutable state storage, current-head advancement, workspace recovery, authenticated shared-World metadata, direct object-storage transfer, and distributed one-writer reservation foundations.

The canonical state transaction boundary proves:

- content hashing while storing;
- immutable revision storage;
- serialized per-World commits;
- atomic current-head advancement;
- stale-head rejection;
- unchanged-candidate detection;
- preservation of the previous head on failure.

The shared backend additionally has PostgreSQL persistence, authenticated sessions, World access control, resumable direct object-storage transfer, reservation generations, heartbeat/uncertainty/reclaim behavior, and durable idempotency for ambiguous mutation retries.

### Factorio

Factorio has validated installation/save discovery, safe import, isolated preparation, local play, hosted launch paths, Steam process handoff observation, environment/mod handling, state capture, commit, and replay on a real Windows Steam installation.

The remaining production acceptance boundary includes the real deployed two-device handoff and the exact Windows managed host-stop behavior where required.

### Palworld

Palworld has validated client and dedicated-server discovery, local and dedicated World discovery, migration of an unchanged World directory into the dedicated-server layout, exact dedicated-server build verification, REST-managed server lifecycle, safe capture, canonical restore, and a read-only `WorldOption.sav` path.

`WorldOption.sav` is never rewritten or re-encoded by Steward. Current PlM/Oodle use is decode-only; temporary management settings are materialized outside the canonical World-owned file.

Player identity conversion between local co-op and dedicated-server identities remains a Palworld-specific edge case rather than a generic Core problem.

### 7 Days to Die

7 Days to Die currently has Windows Steam client/dedicated-server discovery, native World discovery, exact dedicated-server build verification, declared dedicated-server mod inventory, portable World-state capture/restore, random-generated terrain preservation, and fail-closed workspace handling.

Runtime launch/readiness/save-stop equivalence is **not yet accepted**, so the adapter does not advertise automatic local play or hosting.

### Project Zomboid

Project Zomboid currently has Windows Steam client/dedicated-server discovery, authoritative multiplayer-World discovery that rejects remote client caches, exact dedicated-server build verification, a narrow portable server-instance package, and exact Steam Workshop content-manifest verification for Workshop-backed mods.

Runtime launch/readiness/save-stop equivalence is **not yet accepted**, so the adapter does not advertise automatic local play or hosting.

### Desktop

The WPF desktop uses one capability-driven adapter pipeline for Factorio, Palworld, 7 Days to Die, and Project Zomboid. Discovery/import are generic; Start/Host/Stop actions remain disabled automatically when an adapter has not proven the corresponding capability.

The Desktop also consumes persisted recovery responsibility, shared-World access state, remote authentication, direct transfer, and one-writer coordination instead of maintaining a separate UI-only truth model.

## Next decisive milestone

The remaining decisive product proof is live acceptance at the real deployment/game boundary:

```text
PC A commits state N+1
-> PC B retrieves and continues N+1
-> PC B commits N+2
-> PC A retrieves N+2
```

While one device owns the writable session reservation, another device must not start a competing Steward session.

The shared storage and distributed reservation boundaries already exist. The remaining proof is that the packaged Windows client, deployed backend, Steam identity boundary, and selected real game adapters preserve those invariants end to end.

Join remains a separate read-only multiplayer path: it must consume a proven ready host connection and must never acquire a second writable World reservation.

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
  SharedWorlds.Backend/
  SharedWorlds.Backend.Api/
  SharedWorlds.Backend.PostgreSql/
  SharedWorlds.Backend.ObjectStorage.S3/
  SharedWorlds.Desktop/
  SharedWorlds.Cli/
  SharedWorlds.GameAdapters/
    Factorio/
    Palworld/
    SevenDaysToDie/
    ProjectZomboid/

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
