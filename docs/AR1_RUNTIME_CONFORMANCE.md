# AR-1 Runtime Conformance

AR-1 hardens the generic runtime contract after P0 without implementing Factorio- or Palworld-specific readiness/shutdown behavior.

## Current status

Status: **in progress — generic lifecycle/concurrency/state contracts implemented; desktop responsibility gating and build confirmation remain**.

Implemented in the current AR-1 slice:

- `Start World` and `Host World` reuse the same generic canonical-writer lifecycle;
- persistent Steward sharing is no longer a prerequisite for temporary Host;
- regression coverage proves an `Only on this PC` World can Host and remains local-only;
- one active writable managed lifecycle per desktop runtime through `ManagedWritableSessionGate`;
- the device gate sits above per-World coordination so a second World is rejected before a coordinator acquisition;
- generic Join capability result supports automatic, guided manual, unsupported, environment-blocked, and identity-blocked outcomes without game-name branches;
- legacy `AutomaticClientJoin` adapters remain source-compatible through the default Join capability mapping;
- optional `IGameSessionEvidenceProvider` contract represents structured session facts, graceful hosted stop, and safe-capture evidence without treating a PID as the universal session definition;
- generic `WorldLifecyclePhase` events expose resolving, reservation, environment/state preparation, recovery registration, start, running, safe-capture wait, capture, store, commit, finalization, completion, recovery, and cleanup-pending phases;
- deterministic happy-path phase ordering is covered;
- pre-launch launch failure discards controlled prepared work and does not emit `RecoveryNeeded`;
- post-launch/session-observation or capture failure preserves the workspace and recovery record as `RecoveryPending` and emits `RecoveryNeeded`;
- workspace cleanup failure after a successful canonical commit ends at `CleanupPending` and no longer also emits the contradictory `Completed` phase;
- device-wide lifecycle lease releases on every normal/exception path through structured disposal.

## Important compatibility boundary

The new structured session-evidence interface is **not** treated as proof that current Factorio or Palworld adapters already satisfy readiness/stop/capture requirements.

Until AR-2/AR-3 provide controlled evidence and implement the provider contract:

- existing adapters keep their current lifecycle behavior;
- Core does not invent host-readiness or safe-capture facts;
- Factorio/Palworld-specific process, readiness, graceful-stop, and identity limitations stay inside those adapters;
- UI/Core do not branch on game name.

The current generic lifecycle still uses the established `WaitForSessionEndAsync` behavior for adapters that have not yet opted into the structured evidence provider. AR-2/AR-3 must prove the game-specific evidence before the production lifecycle treats that evidence as authoritative.

## Remaining AR-1 work

- connect runtime writable responsibility/phases to desktop tray, Quit, and self-update gating;
- preserve startup recovery scan as a prerequisite before affected Worlds can be shown Ready;
- define the production integration point for structured launch/readiness evidence once an adapter implements it, without making missing evidence equivalent to proof that no gameplay started;
- define the production integration point for safe hosted Stop and Save once an adapter proves graceful-stop/safe-capture capability;
- build/test confirmation under repository warnings-as-errors/nullability rules.

## Milestone boundary

AR-1 does not:

- add game-name branches to Core/UI;
- claim Factorio readiness/graceful stop is proven;
- claim Palworld readiness/graceful stop/identity portability is proven;
- implement the remote backend;
- implement a Windows Service;
- add multiple simultaneous writable sessions per device;
- add branches/merge/save-conflict behavior.

AR-1 remains complete only when the generic conformance tests are green and the runtime exposes the approved lifecycle/evidence contracts without requiring provider- or game-specific guesses.
