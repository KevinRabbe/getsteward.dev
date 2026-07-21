# Architecture

## Product kernel

The product is the **World**.

> **One shared World. Different Steam players. Different times. No always-on game server.**

A World is the latest valid playable state plus the environment information required to run it. Players may continue it on different devices and at different times. Steward coordinates the handoff; the game and Steam continue to handle gameplay and multiplayer behavior.

The central rule is:

> Core knows **what** must happen. Adapters know **how a game makes it happen**.

Core must never contain game-specific branches such as `if (game == Factorio)` or `if (game == Palworld)`.

## Dependency direction

```text
Desktop / background runtime / development tools
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
    |       +--> Factorio-specific behavior
    |       +--> Palworld-specific behavior
    |       +--> future game-specific behavior
    |
    +--> IWorldStorage
    |       +--> local filesystem backend
    |       +--> future shared durable backend
    |
    +--> IWorldSessionCoordinator
            +--> local coordinator
            +--> future Steam-backed coordination
```

Concrete platform and game integrations depend inward on Core contracts. Core does not depend on a concrete game, storage provider, launcher, or Steam SDK.

## Steam as the product platform

Steam is the primary product platform for identity, distribution, game discovery, launching, friends, invitations, native multiplayer joining, Workshop content, and dedicated-server tooling where those facilities are useful.

That does not make game-specific Steam behavior a Core concern. An adapter may internally use Steam, Steam Workshop, CurseForge, Modrinth, Prism, a custom launcher, filesystem conventions, registry entries, or a game-specific API. The Core only sees the adapter contract.

The product boundary is:

> Steam handles games and players. Steward handles continuity of the shared World.

## World Core

Core owns the universal transaction:

```text
load latest valid state
-> reserve one writable session
-> prepare environment through adapter
-> restore state through adapter
-> launch local play or temporary hosting
-> wait for adapter-observed session completion
-> capture updated state
-> durably store and verify it
-> advance the current state last
-> release the World for the next player
```

Core also owns:

- the one-active-writer invariant;
- immutable environment and state revisions;
- the mutable current-World head;
- conservative commit ordering;
- recovery state and typed failure boundaries;
- calls to storage and session coordination abstractions.

Core does not own save paths, executable paths, process names, mod layouts, shutdown commands, file-stabilization rules, or game-specific connection behavior.

## Game adapters

An adapter owns all game-specific knowledge required to complete the handoff:

- installation and World discovery;
- environment inspection and preparation;
- state import, restore, capture, and validation;
- local launch and temporary host launch;
- relevant game, launcher, client, child-process, or server observation;
- safe shutdown behavior;
- determination of when capture is safe;
- optional native join behavior.

Adapters may be internally complicated. That complexity must remain isolated from Core.

## Background runtime

Steward is background-first, not launcher-only.

After the user starts or hosts a World, Steward remains responsible for:

- observing the adapter-defined session;
- keeping the World unavailable to competing writable Steward sessions;
- waiting for a safe capture point;
- capturing, storing, verifying, and committing the result;
- preserving recovery evidence when completion is uncertain.

The visible application may minimize or become quiet, but the lifecycle remains active until the handoff succeeds or enters a recoverable failure state.

## Durable storage

`IWorldStorage` is the durable persistence boundary.

It stores:

- World metadata;
- immutable environment revisions;
- immutable state revision metadata;
- opaque state payloads.

Storage does not determine whether a session is active. Durable state and transient coordination are separate concerns.

The current implementation is local filesystem storage. The next product layer requires a shared durable implementation that allows another trusted Steam user or device to retrieve the latest state.

## Session coordination

`IWorldSessionCoordinator` protects the one-active-writer rule.

It answers only the operational questions Steward needs:

- Is the World available?
- Has one writable session been reserved?
- Which session/device currently holds that reservation?
- May the current state advance?
- Has the reservation been released or moved into recovery?

It is not a social, ownership, party, or governance model.

## Revisions

Environment and state are versioned independently:

```text
World = Environment E7 + State S143
```

Normal play usually advances only state:

```text
E7 + S143 -> E7 + S144
```

A relevant game, mod, or configuration change may create a new environment revision:

```text
E7 -> E8
```

Revisions are immutable after publication. The mutable value is the World pointer to the current valid revision.

Revision history exists for integrity, recovery, diagnostics, compatibility, and safe commit ordering. Steward is not a Git-style branching or generic save-merging product.

## Host switching

A host switch happens between sessions:

```text
current host finishes
-> adapter observes safe session end
-> updated state is captured and committed
-> another device restores the latest state
-> another temporary host starts
```

There is no live game-process migration.

## Architecture philosophy

1. Build the smallest system that creates the complete product effect.
2. Keep Core stable, conservative, and game-agnostic.
3. Push game-specific complexity into adapters.
4. Reuse Steam, games, dedicated servers, launchers, and mod ecosystems instead of rebuilding them.
5. Generalize only after multiple real adapters prove a recurring pattern.
6. Preserve the last valid World state on uncertainty.
7. Complete the full handoff; launching alone is not success.
8. Remove obsolete product concepts rather than allowing parallel architectures to accumulate.