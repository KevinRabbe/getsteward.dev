# Product Boundary

## Product definition

SafeWorld is a Windows desktop product for continuing the same game World across trusted Steam players, PCs, hosts, and times.

Its core promise is:

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steam is the primary platform surface. Games are adapters. Worlds are the product.

## What SafeWorld owns

SafeWorld owns the complete World continuity lifecycle:

```text
get latest valid World state
-> establish one writable authority
-> prepare the correct game environment
-> restore the World on the active device
-> start local play or temporary hosting
-> observe the adapter-defined session lifecycle
-> stop/save safely
-> capture the updated World state
-> store and verify it
-> commit the new current state
-> make the World available for the next session
```

Launching the game is not enough. SafeWorld is responsible for returning a valid updated World after play.

## Peer-hosted topology

Ordinary SafeWorld use is peer-hosted.

There is no permanent SafeWorld backend or separate SafeWorld server distribution required for Host, Join, membership, handoff, or normal World continuity.

When a World is active, one peer Host carries the authoritative live state. When no Host is active, the World is inactive.

## One World, one writer

A SafeWorld-managed World has one current valid state and at most one active writable authority.

```text
World inactive / available
-> one Host or local player starts
-> that device becomes the temporary writer
-> other members may join the active game
-> session ends safely
-> updated state commits
-> World becomes available again
```

SafeWorld does not create competing save histories and does not provide generic save merging.

## Steam's role

SafeWorld should reuse Steam wherever Steam already solves the platform problem well:

- identity;
- friends;
- invitations;
- lobby discovery;
- peer networking;
- distribution and updates;
- game ownership and installation where useful.

Steam lobby ownership is temporary platform state. It is not durable World authority by itself.

## World membership

Canonical membership belongs to the World.

Steam supplies identity and invitation transport, but SafeWorld decides whether a Steam identity is currently authorized for that World.

Adding, removing, leaving, joining, and handoff must preserve the current World authority generation and fail closed when evidence is stale or ambiguous.

## Host handoff

Host switching is not live process migration.

```text
stop outgoing Host
-> final save/capture
-> commit exact final revision
-> transfer and verify that revision
-> activate the target Host
-> advance durable authority generation
```

Writable responsibility moves only after the outgoing Host has safely finished its state transition.

## Adapter boundary

Core owns the universal transaction. Each adapter owns game-specific truth.

An adapter is responsible for facts such as:

- game installation and World discovery;
- required environment description;
- environment preparation;
- state restore;
- local/host/join launch behavior;
- relevant process lifecycle;
- readiness;
- safe stop/save behavior;
- capture and validation.

Core must never guess game-specific process names, save paths, shutdown rules, or file-completion semantics.

## Background-first behavior

SafeWorld should require little attention while the user plays.

The normal interaction is:

```text
select World
-> Start World or Host World
-> play
```

SafeWorld remains active in the background because it must observe the session and complete the final save/capture/commit boundary.

Background-first never means launcher-only.

## Windows product boundary

Public beta and release use a normal installed Windows application.

The user-facing product identity is **SafeWorld** and the installed executable is `SafeWorld.Desktop.exe`.

The release path must not depend on command-script launchers or expose engineering executable names.

World data lives outside the application installation directory so uninstalling SafeWorld does not delete Worlds.

## Steam package boundary

New SafeWorld packages use only the SafeWorld Steam configuration surface:

- `safeworld-steam.json`;
- `SAFEWORLD_STEAM_APP_ID` as the environment override.

A public-beta package must not ship `steam_appid.txt` or development AppID 480.

Compatibility with older local package names may be read only at explicit migration boundaries; new SafeWorld packages must not emit them.

## Explicit non-goals

SafeWorld is not:

- a Git-style branching/merging system for saves;
- a universal save merger;
- a social network;
- a Discord replacement;
- a public game-server browser;
- a permanent game-server provider;
- a complex governance platform;
- DRM for external World copies;
- a system that interprets arbitrary gameplay semantics inside save files.

## Scope test

Every proposed feature must answer at least one of these questions with yes:

> **Does this directly help a group continue the same World safely across different Steam players, devices, hosts, or times?**

> **Does this help a game adapter complete that lifecycle without expanding Core unnecessarily?**

If neither answer is yes, the feature should be removed, deferred, or delegated to Steam, the game, or another existing platform.
