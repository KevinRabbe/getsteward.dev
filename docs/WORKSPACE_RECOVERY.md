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
- exact immutable environment revision that created the workspace;
- adapter id;
- workspace location or adapter-owned locator;
- session/device identity where available;
- creation and update timestamps;
- recovery status;
- optional stable candidate state revision id;
- optional failure/decision reason.

The registry is persisted independently of the running application so a hard process or OS crash can be detected later.

## Status model

### `Active`

The workspace was prepared and registered for a session whose lifecycle has not completed.

An `Active` record found after application restart is treated conservatively as an **Interrupted session**. The record proves Steward owned a prepared workspace, but it does not prove whether gameplay actually started or whether a normal safe-capture boundary was reached.

Steward therefore does not automatically capture, commit, or delete it.

### `RecoveryPending`

A specific preserved workspace is intentionally being recovered as one stable candidate revision.

This status can arise because gameplay started and normal completion failed, or because the user explicitly chose **Recover changes** for an `Active` record found after restart.

Examples:

- session observation failed after launch;
- safe capture failed;
- package validation failed;
- durable storage or upload failed;
- stored-result verification failed;
- current-head advancement failed;
- the user chose to recover an interrupted workspace after restart.

The adapter receives `PreparedWorldDisposition.PreserveForRecovery` on normal post-launch failure. The workspace remains intact until deterministic recovery resolves it.

### `CleanupPending`

The workspace is no longer needed for state recovery, but controlled cleanup could not complete, or the user explicitly chose to discard an interrupted workspace.

Examples:

- commit succeeded but workspace deletion failed;
- preparation failed before launch and unused workspace cleanup failed;
- the user explicitly chose **Discard interrupted session**;
- canonical recovery completed but recovery-journal removal failed.

This is cleanup responsibility, not a second World history.

## Successful session flow

```text
prepare latest World state
-> persist Active recovery record with exact state + environment heads
-> launch game or temporary host
-> adapter observes safe session end
-> capture and validate state
-> journal stable candidate revision id
-> durably store and verify candidate revision
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

Persisting `Active` before launch allows the next Steward startup to detect that the previous lifecycle may have been interrupted. Startup maps that durable record to a distinct guarded **Interrupted session** responsibility instead of pretending it is either a known pending-sync candidate or ordinary temporary cleanup.

The Desktop then offers two explicit decisions for the affected World:

```text
Interrupted session
├─ Recover changes
│    -> require exact journaled environment + preserved workspace
│    -> Active -> RecoveryPending
│    -> assign/reuse one stable candidate revision id
│    -> run normal deterministic local/remote recovery
│
└─ Discard interrupted session
     -> explicit destructive confirmation
     -> Active -> CleanupPending
     -> canonical World remains unchanged
     -> adapter-owned controlled cleanup only
```

The status transition itself is persisted before the follow-up work. A second crash therefore resumes from `RecoveryPending` or `CleanupPending` rather than forgetting the user's decision.

## Deterministic pending recovery

Local-only and authenticated shared Worlds use different authority implementations, but the same safety rule:

```text
canonical state == candidate
    -> original commit already succeeded
    -> never recapture/recommit
    -> finish controlled cleanup + clear journal

canonical state == base
AND canonical environment == journaled environment
    -> acquire exact writable authority
    -> re-check state + environment heads
    -> reuse the same candidate revision id
    -> reuse already stored candidate metadata when valid
       OR capture preserved workspace under that same id
    -> commit candidate
    -> finish cleanup

canonical state != base && != candidate
    -> never overwrite
    -> preserve evidence

canonical environment != journaled environment while candidate is not canonical
    -> never combine histories
    -> preserve evidence
```

Remote recovery additionally verifies that the acquired reservation's starting **state and environment heads** both match the journal. A mismatched reservation is explicitly abandoned rather than used.

An already-published candidate is reusable only when its adapter and parent revision match the journaled recovery lineage.

Older recovery records that do not identify the exact environment fail closed whenever Steward would need that environment to reconstruct or capture an existing workspace. Steward does not substitute the World's current environment.

## Cleanup-only recovery

`CleanupPending` cannot capture, upload, publish, commit, or acquire writable authority.

```text
workspace still exists
    -> exact journaled EnvironmentRevisionId required
    -> load that immutable environment
    -> adapter FinalizePreparedWorldAsync(...Discard)
    -> remove journal only after cleanup succeeds

workspace already gone
    -> cleanup already happened or nothing remains
    -> remove stale journal only
```

A legacy cleanup record with an existing workspace but no exact environment ID remains preserved rather than guessed.

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

1. identify the exact immutable environment that created the workspace;
2. compare the candidate's starting state/environment heads with current canonical heads;
3. reuse one journaled candidate identity across retries;
4. retry capture and durable commit only when the expected heads still match;
5. recognize that a candidate already became canonical without generating another revision;
6. discard an interrupted workspace only after explicit confirmation.

Steward does not merge the recovery candidate with another independently advanced save. A changed canonical state or incompatible environment stops automatic recovery rather than creating branch/merge behavior.

## Recovery invariants

- The last committed state remains authoritative until a replacement commit succeeds.
- Recovery evidence is never deleted merely to release a stuck session.
- A stale candidate must not overwrite a newer current state silently.
- A preserved workspace is never reconstructed under a different environment silently.
- Candidate identity is stable across retries.
- Exact state **and** environment heads are re-checked at the authority boundary.
- Recovery does not create Fork, branch, or merge workflows.
- Cleanup and state authority remain separate concerns.
