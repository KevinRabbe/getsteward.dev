# Prepared Workspace Recovery

## Why prepared workspaces exist

Canonical World state is restored into an adapter-owned prepared workspace before a game session starts. The game operates on that working state rather than directly mutating an immutable stored revision.

After gameplay begins, the workspace may contain the newest recoverable state after a crash, failed capture, failed upload, or failed commit. It is therefore not ordinary temporary-directory garbage.

## Durable recovery registry

Before a writable local or hosted session launches, Core writes a durable `WorkspaceRecoveryRecord` through `IWorkspaceRecoveryStore`.

A record contains the operational information required to recover safely:

- workspace id;
- World id;
- starting state revision;
- adapter id;
- workspace location or adapter-owned locator;
- session/device identity where available;
- creation and update timestamps;
- recovery status;
- optional failure reason.

The registry is persisted independently of the running application so a hard process or OS crash can be detected later.

## Status model

### `Active`

The workspace was prepared and registered for a session whose lifecycle has not completed.

An `Active` record found after application restart is treated conservatively as a possible interrupted session. It is evidence, not proof, that newer recoverable state exists.

### `RecoveryPending`

Gameplay started, but the updated state was not successfully committed.

Examples:

- session observation failed after launch;
- safe capture failed;
- package validation failed;
- durable storage or upload failed;
- stored-result verification failed;
- current-head advancement failed.

The adapter receives `PreparedWorldDisposition.PreserveForRecovery`. The workspace remains intact until an explicit recovery decision resolves it.

### `CleanupPending`

The workspace is no longer needed for state recovery, but controlled cleanup could not complete.

Examples:

- commit succeeded but workspace deletion failed;
- preparation failed before launch and unused workspace cleanup failed.

This is cleanup debt, not a second World history.

## Successful session flow

```text
prepare latest World state
-> persist Active recovery record
-> launch game or temporary host
-> adapter observes safe session end
-> capture and validate state
-> durably store and verify new revision
-> advance current World head
-> adapter discards controlled workspace
-> remove recovery record
```

The current head advances before cleanup. Cleanup failure must not roll back a successfully committed state.

## Failure before launch

When preparation completed but gameplay never started:

```text
prepared workspace
-> pre-launch failure
-> discard controlled temporary resources
-> remove recovery record
```

When safe discard fails, mark `CleanupPending`.

Because no gameplay occurred, the workspace is not treated as a newer gameplay candidate.

## Failure after launch

When gameplay started but the handoff did not complete:

```text
Active
-> failure
-> RecoveryPending
-> preserve workspace
```

The previous valid World state remains authoritative. The workspace stays separate as a recovery candidate.

## Hard crash semantics

A hard crash may bypass every `finally` block.

Persisting `Active` before launch allows the next Steward startup to detect that the previous lifecycle may have been interrupted. The desktop should surface the affected World as `Recovery needed` instead of silently releasing it as fully safe.

## Adapter responsibility

`IGameAdapter.FinalizePreparedWorldAsync` receives a controlled disposition:

- `Discard`: remove adapter-owned prepared resources where safe;
- `PreserveForRecovery`: retain potentially recoverable state.

Adapters must:

- validate workspace ownership before recursive deletion;
- never delete arbitrary user-provided paths;
- clean partial resources they created when preparation fails before returning a `PreparedWorld`;
- preserve post-launch state when completion is uncertain.

## Recovery decision

A preserved workspace never automatically replaces the current World state.

Recovery may perform only explicit safe actions such as:

1. inspect the candidate through the owning adapter;
2. validate that required World contents are complete;
3. compare the candidate's starting revision with the current World head;
4. retry capture and durable commit when the expected head still matches;
5. deliberately choose the validated candidate as the continuing complete state when policy allows;
6. discard the candidate after explicit confirmation when it is no longer needed.

Steward does not merge the recovery candidate with another independently advanced save. When the current World has already advanced elsewhere, the candidate remains separate evidence until the user chooses one complete state or discards it.

## Recovery invariants

- The last committed state remains authoritative until a replacement commit succeeds.
- Recovery evidence is never deleted merely to release a stuck session.
- A stale candidate must not overwrite a newer current state silently.
- Recovery does not create Fork, branch, or merge workflows.
- Cleanup and state authority remain separate concerns.