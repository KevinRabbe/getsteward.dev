# World Lifecycle

## Product mental model

Users should think:

> **Open the World, play, and leave the newest valid state ready for whoever continues next.**

They should not manage save folders, server installations, revision packages, host ownership, branches, or merge conflicts.

## Canonical World rule

A Steward-managed World has:

- one current valid environment revision;
- one current valid state revision;
- at most one active writable Steward session.

A local session and a temporarily hosted session are both writers. They use the same exclusive session-reservation boundary.

The rule prevents Steward from creating competing save histories. It does not attempt to control manual copies outside Steward.

## Lifecycle overview

```text
Ready
-> Preparing
-> Running
-> Capturing
-> Storing
-> Ready
```

Failure after gameplay may lead to:

```text
Recovery needed
```

The previous valid state remains current until the new state has been completely captured, stored, verified, and committed.

## Discovery

Adapters discover installations and existing saves or server Worlds.

Discovery is read-only. It must not:

- mutate the source;
- upload it;
- publish it;
- host it;
- mark it shared.

## Import

Import turns one detected save or server World into a Steward-managed World.

```text
DetectedWorld
-> inspect environment
-> adapter captures source state
-> create initial EnvironmentRevision
-> create initial StateRevision
-> durably store both
-> save World metadata last
```

Import must leave the original source untouched.

A newly imported World starts as `LocalOnly` unless the user explicitly enables shared handoff.

## Start World locally

Local play means advancing the current World without exposing a multiplayer host through Steward.

```text
load World
-> acquire exclusive session reservation
-> load current environment and state
-> adapter prepares workspace
-> adapter restores state
-> adapter launches local session
-> adapter observes real session end
-> adapter captures updated state
-> store and verify immutable StateRevision
-> advance World head last
-> finalize workspace
-> release reservation
```

The adapter owns the difference between local play and hosted play.

## Host World temporarily

Hosted play advances the same World while the current device temporarily runs the game's multiplayer host or dedicated server.

```text
load shared World
-> acquire exclusive session reservation
-> prepare and restore current state
-> adapter launches temporary host
-> Steam/game handles players joining
-> adapter observes the host session
-> host stops safely
-> adapter captures updated state
-> store and verify immutable StateRevision
-> advance World head last
-> release reservation
```

The host contributes resources for that session. Hosting does not transfer ownership because Steward does not require a permanent World-owner/host hierarchy.

For a dedicated server, closing one player's client does not necessarily end the World session. The adapter must observe the server lifecycle that actually owns the writable state.

## Join active session

When another device is already hosting the World, Steward must not start a competing writable session.

Where supported, the user may join through:

- Steam/game multiplayer invitation or native joining;
- the game's own server browser or connection system;
- adapter-provided connection information.

Joining does not create or advance a separate Steward World state. The active host remains the only writer.

## Session observation

Session observation is adapter-owned.

The adapter determines:

- which process or processes represent the session;
- whether a launcher handed off to another process;
- whether a dedicated server remains active after clients close;
- how the session stops safely;
- when the save is no longer being written;
- whether required state files are complete and valid.

Core calls the adapter contract and does not guess from a process name or fixed delay.

## Capture and commit

A session is not complete when the game launches or even when the process exits. Steward's job is complete only after the resulting state is safely handed off.

```text
capture candidate
-> package opaque game state
-> validate required contents
-> durably store package and metadata
-> verify stored result
-> atomically advance current World state
```

Commit rule:

> The current World state advances last.

A failed capture, upload, verification, or metadata write leaves the previous valid revision authoritative.

## Switching host

Switching host happens between sessions:

```text
Kevin hosts
-> session ends
-> updated state commits
-> World becomes Ready
-> Alex later presses Host
-> Alex restores the latest state
-> Alex temporarily hosts
```

There is no live process or memory migration.

No request/accept governance workflow is required for the product kernel. The shared coordinator only needs to ensure the previous session is complete before another begins.

## Switching game

Switching game means selecting another World that belongs to another game:

```text
finish Palworld World
-> commit Palworld state
-> select Factorio World
-> Factorio adapter runs the same lifecycle
```

Steward never converts one game's World into another game's World.

## Shared handoff across devices

The essential commercial flow is:

```text
PC A starts latest state N
-> plays
-> commits state N+1

PC B later starts
-> receives state N+1
-> plays
-> commits state N+2
```

A shared storage backend carries durable state. A shared session coordinator prevents both PCs from starting writable sessions from state N at the same time.

## Recovery

After launch, a prepared workspace may contain newer recoverable gameplay state than the last committed revision.

On uncertain failure:

```text
last valid revision remains current
+
workspace/recovery evidence is preserved
+
World enters Recovery needed when appropriate
```

Recovery may retry capture/store or deliberately continue from the last known-good state. Steward must never silently promote an incomplete candidate.

## Sharing boundary

`LocalOnly` and `Shared` are operational states:

- `LocalOnly`: no shared cross-device handoff through Steward;
- `Shared`: eligible for shared storage, session coordination, and temporary hosting.

`Shared` does not mean public discovery, social ownership, or DRM. Groups organize themselves through Steam, the game, Discord, or their own communication.

## Explicitly unsupported lifecycle concepts

The active product lifecycle does not include:

- Git-style branches;
- Fork workflows;
- generic save merging;
- merge conflict resolution;
- automatic reconciliation of divergent saves;
- live host migration;
- permanent game-server execution;
- ownership-transfer workflows;
- party or governance workflows.

When users possess different external copies, they may choose which save to import or continue. Steward does not merge them.

## Completion contract

A lifecycle implementation is complete only when all of these are true:

1. The correct latest state was selected.
2. Exactly one writable session was reserved.
3. The correct environment was prepared.
4. The adapter restored and launched the World.
5. The adapter observed the actual session lifecycle.
6. Capture occurred only at a safe point.
7. The new state was durably stored and verified.
8. The World head advanced only after success.
9. Failure preserved the previous valid state and recoverable evidence.
10. The World became safely available to the next player.
