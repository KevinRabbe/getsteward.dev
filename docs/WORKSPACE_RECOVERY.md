# Prepared Workspace Recovery

## Why prepared workspaces exist

Canonical World state is restored into an adapter-owned prepared workspace before a game session starts. The game operates on that isolated working state rather than directly mutating the immutable canonical revision package.

That workspace can contain the newest recoverable local state after a crash or failed canonical commit. It therefore cannot be treated as ordinary temporary-directory garbage.

## Durable recovery registry

Before a canonical host session is launched, Core writes a durable `WorkspaceRecoveryRecord` through `IWorkspaceRecoveryStore`.

A record contains:

- workspace id
- World id
- base canonical state revision
- adapter id
- working-directory location
- user who started the session
- creation/update timestamps
- recovery status
- optional failure reason

The current local implementation stores these records under the product data root in `recovery/` using the same versioned persisted-document envelope as other JSON metadata.

## Status model

### `Active`

The workspace has been prepared and registered for a session that has not completed its lifecycle yet.

If the application starts and finds an `Active` record left by a previous process, it must be treated conservatively as a possible interrupted session. A hard process kill or OS crash cannot execute `finally` blocks, so the durable record is the evidence that the workspace may contain recoverable state.

### `RecoveryPending`

The game session started, but a new canonical revision was not successfully committed.

Examples:

- state capture failed
- revision storage failed
- canonical-head update failed
- session observation failed after launch

The adapter receives `PreparedWorldDisposition.PreserveForRecovery`. The workspace must remain intact until an explicit recovery/discard workflow resolves it.

### `CleanupPending`

The workspace no longer needs to become canonical, but cleanup could not be completed safely.

Examples:

- a successful commit was completed but adapter workspace deletion failed
- a pre-launch failure occurred and the unused workspace could not be discarded

This is cleanup debt, not an alternative canonical history.

## Successful session flow

```text
prepare canonical state
-> persist Active recovery record
-> launch game
-> session ends
-> capture state
-> store immutable StateRevision
-> update canonical World head
-> adapter finalizes workspace as Discard
-> remove recovery record
```

The canonical head advances before cleanup. A cleanup failure must not roll back a successfully committed World revision.

## Failure before launch

If preparation completed but launch never started:

```text
prepared workspace
-> failure before session start
-> adapter finalizes as Discard
-> remove recovery record
```

If discard fails, the record becomes `CleanupPending`.

No gameplay occurred, so the workspace is not considered a new recoverable gameplay state.

## Failure after launch

If the session started but canonical commit did not complete:

```text
Active
-> failure
-> RecoveryPending
-> adapter finalizes as PreserveForRecovery
```

The previous canonical revision remains the canonical head. The local workspace is preserved separately as a recovery candidate.

## Hard crash semantics

A hard crash may occur without any cleanup code running.

Because the `Active` record is persisted before launch, startup can detect that the previous lifecycle did not complete normally. `Active` records found after process restart should be surfaced by the future recovery UI alongside explicit `RecoveryPending` records.

The development CLI exposes the current registry with:

```text
recovery
```

## Adapter responsibility

`IGameAdapter.FinalizePreparedWorldAsync` receives one of two dispositions:

- `Discard`: remove adapter-owned prepared resources where safe.
- `PreserveForRecovery`: retain potentially recoverable state.

Adapters must never recursively delete arbitrary user-provided paths. They must verify ownership of a workspace before destructive cleanup.

The current Factorio adapter only recursively deletes directories matching the workspace shape it creates under the SharedWorlds Factorio work root.

If `PrepareEnvironmentAsync` fails before returning a `PreparedWorld`, the adapter is responsible for cleaning any partial resources it created during that failed preparation because Core never received a handle with which to finalize them.

## Recovery is not automatic canonical overwrite

A preserved workspace never automatically replaces canonical World state.

Recovery must be an explicit future operation that can:

1. inspect the candidate
2. validate it with the owning adapter
3. compare it with the current canonical revision
4. let the user choose recovery, fork, export, or discard where ambiguity exists
5. create a new immutable revision before changing the canonical head

This keeps recovery evidence separate from canonical history until the system has enough confidence to commit it safely.
