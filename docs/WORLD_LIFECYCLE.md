# World Lifecycle

## Product mental model

Users should think:

> I want to continue this world.

They should not need to think about which machine owns the save, which mod folder is active, which person is the permanent host, or where the canonical files live.

For shared Worlds, the product should make this true:

> The group owns the World. Nobody permanently owns the host.

For private Worlds, the product should make this true:

> Nothing is shared unless I explicitly choose to share it.

## Canonical World

A canonical World has one current environment revision and one current state revision.

Only one canonical session may advance the canonical World at a time. A local single-player Continue and a shared hosted session are both canonical writers, so they use the same exclusive session coordination boundary internally.

This avoids conflicting save histories and impossible automatic merging.

## Privacy and sharing

Sharing is opt-in.

A World has one of two current sharing modes:

```text
LocalOnly
Shared
```

Rules:

- discovery never shares anything
- import creates a `LocalOnly` World
- local Continue is allowed for `LocalOnly`
- Host and Join are blocked for `LocalOnly`
- changing to `Shared` requires an explicit user action
- changing back to `LocalOnly` disables future Host / Join workflows

The UI should therefore present Share / Host / Join only where the World is explicitly shared. A hidden or missing UI button is not the security boundary; Core also rejects hosted play for a `LocalOnly` World.

## Import

Import converts an existing game save/world into the product model.

```text
DetectedWorld
-> adapter inspects environment
-> validate adapter/environment identity
-> adapter captures source state
-> create EnvironmentRevision E1
-> create StateRevision S1
-> durably store E1 and S1
-> persist LocalOnly World pointing to E1 + S1 last
-> clean adapter-declared temporary capture package
```

Import must not mutate the original source save.

Import also must not publish, upload, host, or otherwise share the source save merely because it was discovered or imported.

The World metadata is written last so a partially failed import cannot create a canonical World that points at incomplete revision data.

## Continue local

Local Continue means: play the current canonical World without exposing it as a multiplayer host.

```text
acquire exclusive canonical-session lease
-> load World
-> validate World adapter identity
-> load current environment revision
-> validate environment adapter identity
-> load current state revision metadata
-> validate state adapter identity
-> adapter prepares isolated workspace
-> adapter restores state
-> adapter launches local/single-player session
-> adapter observes session end
-> adapter captures resulting state
-> durably store next immutable StateRevision
-> move World's canonical state head forward last
-> clean adapter-declared temporary capture package
-> release canonical-session lease
```

For Factorio, local Continue uses the game's single-player load path rather than the multiplayer host path.

A local-only World can use this flow.

## Host

Host means: advance a shared canonical World while exposing the game session for multiplayer according to adapter behavior.

Before the session coordinator is acquired, Core verifies that the World is explicitly `Shared`.

```text
verify World is Shared
-> acquire exclusive canonical-session lease
-> prepare canonical state
-> adapter launches host
-> adapter observes session end
-> capture and commit next immutable StateRevision
-> advance canonical head last
-> release canonical-session lease
```

Attempting to Host a `LocalOnly` World fails with `WorldSharingRequiredException`.

## Join

Future shared behavior:

If another member already owns the canonical hosted session, the UI should offer Join rather than starting a conflicting host.

The adapter receives a generic `HostConnection` and performs game-specific connection behavior.

Join is only meaningful for a World whose sharing mode is `Shared`.

## Sharing transitions

Current development commands expose the intended domain transition:

```text
share-world <world-id>
unshare-world <world-id>
```

These commands change the World's sharing eligibility. They do not imply that every future remote backend action is already implemented.

A future desktop UI should make this a deliberate control, for example:

```text
Private / Local-only
[ Enable sharing ]

Shared
[ Host ] [ Join active host ] [ Manage members ] [ Disable sharing ]
```

Disabling sharing must not delete the World or its revision history.

## Host acquisition

When no canonical session is active, the player starting canonical play acquires the exclusive writer role through `IWorldSessionCoordinator`.

The current interface still uses host-oriented naming, but the lease is also used for local canonical play so two local processes cannot advance the same World concurrently.

A future distributed coordinator may model local and hosted session modes explicitly while preserving the same one-writer invariant.

## Host handoff

Host handoff is a controlled restart.

```text
1. New player requests host.
2. Session coordinator records HandoffRequested and the requested host.
3. Current host accepts.
4. Current game is allowed/asked to save.
5. Current host session closes.
6. Adapter waits until the relevant session truly ended.
7. Latest state is captured.
8. New canonical state revision is committed.
9. New host restores latest canonical state.
10. Required environment is prepared.
11. New host launches.
12. Other players join the new host.
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

The current coordinator naming/state model is still host-centric and will likely gain an explicit local-playing state when the distributed coordinator is implemented. This is a naming/model refinement, not a change to the one-canonical-writer rule.

## Session end

Session-end detection is adapter-owned.

A simple game may map one process id directly to one session. Another launcher may spawn or hand off to another process. A dedicated-server game may need to observe a different process entirely.

For that reason, the Core calls `WaitForSessionEndAsync` rather than directly waiting on a PID.

## Temporary connectivity loss

A temporary network or Discord disconnect must not automatically end the canonical session or release host ownership.

The game/session lifecycle is authoritative for canonical state advancement.

A future distributed coordinator should use lease expiry/recovery semantics rather than treating one transient network failure as proof that the game session ended.

## Crash recovery

A crash must not blindly overwrite the last known-good canonical state.

Current behavior:

```text
last known-good canonical revision remains intact
+
prepared workspace is registered before game launch
+
post-launch commit failure preserves workspace
+
RecoveryPending is recorded when possible
```

An `Active` workspace record left after a hard application or OS crash is treated conservatively as a possible interrupted-session recovery candidate.

## Captured package ownership

Adapters may create temporary portable packages when capturing game state.

Core deletes a captured package only when the adapter explicitly sets `DeletePackageAfterStore = true`. Core must never infer from a path that the file is disposable.

This prevents cleanup logic from accidentally deleting user-owned saves, caches, or launcher-managed files.

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

`IWorldStorage` exposes state revision metadata separately from opening payload bytes so future history and restore workflows can inspect lineage without interpreting game-specific payloads.

## Revision policy

Normal target policy:

- optional pre-session safety snapshot
- use game's own autosave behavior during play
- one canonical commit at clean session end
- optional non-canonical recovery checkpoints for very long sessions

The product should not create a new canonical revision every few minutes unless a particular adapter or recovery design requires it.
