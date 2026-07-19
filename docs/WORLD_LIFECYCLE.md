# World Lifecycle

## Product mental model

Users should think:

> I want to continue our world.

They should not need to think about which machine owns the save, which mod folder is active, which person is the permanent host, or where the canonical files live.

The product should make this true:

> The group owns the World. Nobody permanently owns the host.

## Canonical World

A canonical World has one current environment revision and one current state revision.

Only one canonical host may advance the canonical World at a time.

This avoids conflicting save histories and impossible automatic merging.

## Import

Import converts an existing game save/world into the product model.

```text
DetectedWorld
-> adapter captures source state
-> adapter inspects environment
-> create World
-> create EnvironmentRevision E1
-> create StateRevision S1
-> persist World pointing to E1 + S1
```

Import should not mutate the original source save.

## Continue

Continue means: play the current canonical World.

Current local vertical slice:

```text
load World
-> load current environment revision
-> load current state revision
-> adapter prepares isolated workspace
-> adapter restores state
-> adapter launches host
-> adapter observes session end
-> adapter captures resulting state
-> create next StateRevision
-> move World's canonical state head forward
```

Normal clean play therefore looks like:

```text
E1 + S1
-> play
-> E1 + S2
```

## Join

Future shared behavior:

If another member already owns the canonical host role, Continue should become Join rather than starting a conflicting host.

The adapter receives a generic `HostConnection` and performs game-specific connection behavior.

## Host acquisition

When no canonical session is active, the first member who starts canonical play acquires the host role.

The host role is temporary and belongs to the current session, not permanently to one person.

## Host handoff

Host handoff is a controlled restart.

```text
1. New player requests host.
2. Current host accepts.
3. Current game is allowed/asked to save.
4. Current host session closes.
5. Adapter waits until the relevant session truly ended.
6. Latest state is captured.
7. New canonical state revision is committed.
8. New host restores latest canonical state.
9. Required environment is prepared.
10. New host launches.
11. Other players join the new host.
```

No live process migration is required.

## Session states

Current generic states:

- `Available`
- `Preparing`
- `Hosting`
- `HandoffRequested`
- `Committing`
- `Synchronizing`
- `RecoveryPending`

These states describe universal product behavior. A game adapter must not invent a separate canonical lifecycle.

## Session end

Session-end detection is adapter-owned.

A simple game may map one process id directly to one session. Another launcher may spawn or hand off to another process. A dedicated-server game may need to observe a different process entirely.

For that reason, the Core calls `WaitForSessionEndAsync` rather than directly waiting on a PID.

## Temporary connectivity loss

A temporary network or Discord disconnect must not automatically end the canonical session or release host ownership.

The game/session lifecycle is authoritative for canonical state advancement.

## Crash recovery

A crash must not blindly overwrite the last known-good canonical state.

Desired behavior:

```text
last known-good canonical revision remains intact
+
latest recoverable local state is preserved
+
World enters RecoveryPending when confidence is insufficient for automatic commit
```

Recovery logic is not yet implemented in the first vertical slice.

## Sandbox Copy

A Sandbox is a disposable clone of the current canonical state.

Properties:

- starts from current environment and state
- may be used destructively
- never writes back to canonical history
- can be deleted freely

Use cases:

- testing a mod
- testing a build
- trying destructive actions

## Fresh Test World

A Fresh Test World uses the same environment but creates a brand-new game world/save.

```text
same environment
+
new empty state
```

This is useful when testing mods or configurations without loading a large mature save.

## Fork

A Fork creates a permanent independent branch.

It starts from an existing World revision but receives its own future canonical history.

```text
Original: E7 + S143 -> S144 -> S145
                     \
Fork:                 -> S143-F1 -> S143-F2
```

A Fork is not automatically merged back.

## Restore

Restore makes an older state revision become the current canonical head through an explicit controlled operation.

History should remain available rather than deleting later revisions silently.

## Revision policy

Normal target policy:

- optional pre-session safety snapshot
- use game's own autosave behavior during play
- one canonical commit at clean session end
- optional non-canonical recovery checkpoints for very long sessions

The product should not create a new canonical revision every few minutes unless a particular adapter or recovery design requires it.
