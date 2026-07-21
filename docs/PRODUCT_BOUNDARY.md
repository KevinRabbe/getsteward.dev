# Product Boundary

## Commercial product definition

Steward is a commercial product moving beyond prototype validation into a durable, user-facing system.

Its core promise is:

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward makes a game World portable between trusted players and devices. A player starts or hosts the latest valid World state, plays, and leaves the updated state ready for whoever continues next.

Steam is the primary platform surface. Games are adapters. Worlds are the product.

## What Steward must do

Steward owns the complete World handoff lifecycle:

```text
get latest valid World state
-> reserve one writable session
-> prepare the correct game environment
-> restore the World on the selected device
-> launch the local game or temporary host
-> monitor the game or server process
-> determine when the session has ended safely
-> capture the updated World state
-> store and verify the new state
-> advance the shared World only after successful storage
-> make the World available to the next player
```

These are not optional convenience features. They are the product's essential operating responsibilities.

## Background-first behavior

Steward should be used briefly and then stay mostly in the background.

The normal user interaction is:

```text
select World
-> Start World or Host World
-> play
```

While the game or server is running, Steward continues to:

- observe the adapter-defined session process or processes;
- keep the World unavailable to competing writable Steward sessions;
- wait for the adapter-defined safe capture point;
- capture, store, verify, and commit the updated state;
- preserve the last valid state when anything fails.

A background-first product is not a passive launcher. Steward remains responsible for the handoff from latest shared state to playable session and back to the next valid shared state.

## Switching host and switching game

Both operations reuse the same lifecycle.

### Switching host

```text
same World
+ same game adapter
+ different device
```

The previous session finishes and commits its state. Another player later starts or hosts that same World on another device.

There is no live process migration.

### Switching game

```text
different World
+ that World's adapter
+ selected device
```

The user finishes one World, selects another World belonging to another game, and Steward runs the same generic lifecycle through the other adapter.

Steward does not convert save data between games.

## Essential state rule

A Steward-managed World has one current valid state and at most one active writable session.

```text
World available
-> one player starts
-> that device becomes the temporary writer
-> others may join through the game or Steam
-> session ends
-> updated state commits
-> World becomes available again
```

This rule prevents Steward itself from creating competing save histories. It does not attempt to control manual copies outside Steward.

## Steam's role

Steward should reuse Steam wherever Steam already solves the problem well:

- identity;
- game ownership and installation;
- launching;
- friends and invitations;
- native multiplayer joining;
- Workshop and dedicated-server tooling;
- Steward distribution and updates.

Steward must not rebuild Steam into a second platform.

## Adapter boundary

The Core owns the universal transaction. Each adapter owns game-specific facts.

An adapter is responsible for:

- finding installations and Worlds;
- describing the required environment;
- preparing that environment;
- restoring the World state;
- launching local play or hosting;
- identifying the relevant game or server process lifecycle;
- determining when capture is safe;
- capturing and validating the updated World state.

The Core must never guess game-specific process names, save locations, shutdown behavior, or file-completion rules.

## Commercial product scope

The project is no longer treated as a disposable prototype. The current Factorio and Palworld work are product foundation and validation assets.

Commercial-quality work means:

- durable state handling;
- conservative failure behavior;
- recoverable interrupted sessions;
- testable contracts;
- stable adapter boundaries;
- clear user-facing states;
- maintainable code that can support additional games without rewriting the product.

The product should still grow narrowly. Commercial quality does not justify unnecessary scope.

## Explicit non-goals

Steward is not:

- a GitHub-style branching and merging system for save files;
- a universal save merger;
- a social network;
- a Discord replacement;
- a public server browser;
- a complex ownership, role, or governance platform;
- a permanent game-server provider;
- a system that guarantees external copies can be deleted;
- a system that understands gameplay semantics inside arbitrary saves.

Groups can organize themselves. Steward's responsibility is to keep the selected shared World state safe, current, portable, and playable.

## Product test

Every proposed feature must pass both questions:

> **Does this directly help a group continue the same World state safely across different Steam players, devices, hosts, or times?**

> **Or does it help another game's adapter complete that same lifecycle without expanding Core unnecessarily?**

If neither answer is yes, the feature should be removed, deferred, or left to Steam, the game, or another existing platform.