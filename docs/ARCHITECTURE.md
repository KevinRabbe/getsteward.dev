# Architecture

Status: **CURRENT — reconciled with the implemented local + shared Steward platform.**

## Product kernel

The product is the **World**.

> **One shared World. Different Steam players. Different times. No always-on game server.**

A World is the latest valid playable state plus the environment information required to reproduce it. Players may continue it on different devices and at different times. Steward coordinates the handoff; the game and Steam continue to own gameplay and their native multiplayer/platform behavior.

The central architecture rule is:

> **Core knows what must happen. Adapters know how a specific game makes it happen.**

Core must never contain game-name branches such as `if (game == Factorio)` or `if (game == Palworld)`.

## Dependency direction

```text
Windows Desktop / background runtime / development tools
                    |
                    v
             SharedWorlds.Core
              |      |      |
              |      |      `-> IWorldSessionCoordinator
              |      |            |-- LocalWorldSessionCoordinator
              |      |            `-- StewardWorldSessionCoordinator
              |      |                    -> Backend.Api reservation authority
              |      |
              |      `-> IWorldStorage
              |            |-- LocalWorldStorage
              |            `-- StewardWorldStorage
              |                    |-- Backend.Api metadata/authority
              |                    `-- verified direct package transfer
              |
              `-> IGameAdapter
                     `-- independently compiled game-specific adapters

Backend.Api
    |-- PostgreSQL
    |     identity/session/access
    |     World + revision metadata
    |     reservation generations
    |     current-head commit/idempotency
    |     Host-presence metadata
    |
    `-- private S3-compatible object storage
          opaque immutable package bytes
```

Concrete integrations depend inward on Core contracts. Core does not depend on a concrete game, PostgreSQL, S3, Steamworks, launcher SDK, or Desktop implementation.

`SharedWorlds.Infrastructure` and composition roots translate Core ports into local or authenticated remote behavior.

## Authority separation

Steward deliberately separates three kinds of truth:

### Durable World state

`IWorldStorage` exposes the World/environment/state persistence required by Core.

For local-only Worlds, local storage is authoritative.

For shared Worlds, backend metadata/transactions are authoritative for current heads while private object storage contains opaque immutable package bytes.

### Writable-session authority

`IWorldSessionCoordinator` owns the one-writer reservation contract used by Core.

For local Worlds this is local coordination.

For shared Worlds, `StewardWorldSessionCoordinator` maps the Core contract onto Backend.Api/PostgreSQL reservation generations, heartbeat, uncertainty, reconnect/reclaim, expected-head commit eligibility, and safe release/abandon behavior.

### Game/session evidence

`IGameAdapter` owns the facts required to know whether a game-specific operation actually started, became ready, ended, saved safely, and can be captured.

None of those three layers may infer the other merely from nearby data.

## Steam as the commercial platform

Steam is the commercial platform for Steward distribution/update and production identity bootstrap. Games may also use Steam for ownership, installations, Workshop content, friends, invitations, native joining, or dedicated-server tooling.

Steam does **not** replace Steward’s shared-World authority transaction.

Production identity flow is conceptually:

```text
Steam-installed Steward
-> SteamAPI initializes
-> actual runtime AppID is read from Steam
-> expected package AppID must match
-> GetAuthTicketForWebApi
-> Backend.Api verifies ticket with publisher-side credential
-> verified Steam identity receives a normal Steward session
```

After identity is established, Steward’s PostgreSQL-backed World membership, reservation, revision, and commit rules remain Steward authority.

The product boundary is:

> **Steam handles the platform identity/distribution primitives it already owns. Steward handles continuity and authority of the shared World.**

## World Core

Core owns the universal writable transaction:

```text
load latest valid World
-> acquire one writable authority
-> prepare exact environment through adapter
-> restore state through adapter
-> register durable recovery responsibility
-> launch local play or temporary hosting
-> observe adapter-defined session lifecycle
-> establish safe capture boundary
-> capture updated state
-> durably store and verify candidate
-> commit against expected head / valid generation
-> finalize/release only after authority is known
```

Core also owns:

- one-active-writer semantics;
- one active managed writable lifecycle per device in the first release;
- immutable environment and state revision relationships;
- conservative commit ordering;
- lifecycle/recovery state and typed failure boundaries;
- calls to storage/session/adapter abstractions;
- preservation of recovery responsibility after uncertain post-launch failure.

Core does **not** own save paths, executable names, process names, mod layouts, RCON/REST commands, safe-save heuristics, public networking mechanisms, or game-specific identity conversion.

## Read-only Join

Join intentionally owns less than Start/Host.

```text
shared World + exact environment
-> verify/prepare local environment
-> read short-lived Ready Host presence
-> adapter validates automatic Join capability
-> launch client against current Host
-> discard read-only preparation
```

Join does not:

- acquire a writable reservation;
- restore/download canonical World state merely to join;
- register writable recovery responsibility;
- capture a candidate;
- advance the canonical head.

First-release Join is **automatic or unavailable**. The earlier guided-manual fallback was removed because no truthful generic lifecycle existed for retaining preparation ownership through user-controlled manual play and proving cleanup/completion afterward.

## Game adapters

An adapter owns all game-specific knowledge required for the capability it advertises:

- installation and World discovery;
- environment inspection/preparation/verification;
- import capture and state restore/capture;
- native World creation where proven;
- local launch;
- temporary Host launch;
- automatic client Join where proven;
- relevant launcher/client/server process observation;
- Host readiness and connection material where needed;
- safe Host stop when `AutomaticHostStop` is advertised;
- determination of the safe capture boundary;
- game-specific identity/environment limitations.

Adapters may be internally complicated. That complexity must remain isolated from Core.

Catalog registration does not imply every capability. The UI/runtime consume `GameAdapterCapabilities` and current adapter evidence rather than branching on game names.

## Background runtime

Steward is background-first, not launcher-only.

After the user starts or hosts a World, Steward remains responsible for:

- observing the adapter-defined session;
- maintaining shared reservation heartbeat while possible;
- keeping competing writable Steward sessions blocked;
- preserving durable recovery metadata;
- waiting for a safe capture point;
- capturing/storing/verifying/committing the result;
- retaining recovery evidence when completion or remote authority is uncertain.

Closing the main window does not abandon that responsibility. The Desktop/tray process remains the first-release background host; there is no Windows Service requirement without evidence that one is needed.

## Durable storage

`IWorldStorage` is the Core durable persistence boundary, but **local filesystem storage is no longer the only implementation**.

Current implementations include:

- local filesystem persistence for local-only Worlds;
- authenticated remote `StewardWorldStorage` for shared Worlds.

The remote implementation composes Backend.Api metadata/current-head authority with direct verified immutable package transfer. Backend/PostgreSQL decides which revision is current; object storage never does.

See `STORAGE.md` for the detailed storage contract.

## Session coordination

`IWorldSessionCoordinator` protects the one-active-writer rule.

The shared implementation is already PostgreSQL-backed through Backend.Api. It is not “Steam-backed coordination.” Steam may authenticate the user; Steward’s backend owns the reservation generation and canonical commit authority.

Important shared properties:

```text
Available
-> Active generation
-> Uncertain after missed heartbeat threshold
-> same valid generation reconnects
   OR deliberate reclaim invalidates old generation
-> next writer later acquires a new generation
```

A timeout never manufactures `Available` while the old writer may still exist.

## Revisions

Environment and state are versioned independently:

```text
World = Environment E7 + State S143
```

Normal play usually advances only state:

```text
E7 + S143 -> E7 + S144
```

A relevant game/mod/configuration transition may create a new environment revision:

```text
E7 -> E8
```

Published revisions are immutable. The mutable authority is the current World head.

Revision history exists for integrity, recovery, retention, diagnostics, compatibility, and safe commit ordering. Steward is not a Git-style branch/merge product.

## Host presence is not authority

A short-lived Host-presence record may tell another member where a current managed Host is and whether it is Starting/Ready.

It does not decide who may write the World.

Host presence is tied to the exact active reservation session/generation and is rejected when stale, uncertain, or superseded. Join consumes it read-only.

## Host switching

A host switch happens between sessions:

```text
current writable host finishes safely
-> updated state becomes canonical
-> writable authority resolves
-> another device acquires the new current head
-> restores/prepares it
-> another temporary Host starts
```

There is no live game-process migration.

## Architecture philosophy

1. Build the smallest system that creates the complete product effect.
2. Keep Core stable, conservative, and game-agnostic.
3. Push game-specific complexity into adapters.
4. Keep durable state, writable authority, and game/session evidence separate.
5. Reuse Steam, Windows, games, dedicated servers, launchers, and native ecosystems instead of rebuilding them.
6. Generalize only after real evidence proves a recurring universal pattern.
7. Preserve the last valid World state and recovery evidence on uncertainty.
8. Complete the full handoff; launching alone is not success.
9. Remove obsolete product concepts rather than allowing parallel architectures to accumulate.
10. When a problem can be eliminated or delegated to the platform that already owns it, do that before adding another subsystem.