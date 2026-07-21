# AR-1 Runtime Conformance

AR-1 hardens the generic runtime contract after P0 without implementing Factorio- or Palworld-specific readiness/shutdown behavior.

## Current status

Status: **implementation complete against the generic AR-1 contract; build/test confirmation pending**.

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
- device-wide lifecycle lease releases on every normal/exception path through structured disposal;
- `WorldLifecycleResponsibilityTracker` converts lifecycle phases and durable startup-recovery records into one conservative Quit/update responsibility signal;
- an `Active` recovery record found after restart becomes `RecoveryNeeded` rather than being treated as Ready;
- the WPF desktop lifecycle feeds that same responsibility tracker;
- the tray icon is present whenever Steward runs, closing the window hides it, and explicit Quit is refused while active/unresolved World responsibility remains;
- tray status maps runtime responsibility to the approved user-facing concepts such as Running, Saving World, Recovery needed, and Action required;
- unified startup loads durable recovery responsibility before initializing the game/World surface;
- unified and legacy Host action gating no longer requires persistent sharing; temporary Host depends on adapter capability plus the device hosting preference only;
- the responsibility tracker exposes `CanSelfUpdate`; the current desktop has no self-update executor yet, so there is no updater path that can bypass the gate.

## Important compatibility boundary

The new structured session-evidence interface is **not** treated as proof that current Factorio or Palworld adapters already satisfy readiness/stop/capture requirements.

Until AR-2/AR-3 provide controlled evidence and implement the provider contract:

- existing adapters keep their current lifecycle behavior;
- Core does not invent host-readiness or safe-capture facts;
- Factorio/Palworld-specific process, readiness, graceful-stop, and identity limitations stay inside those adapters;
- no new game-name branch is added to Core or to the unified action model.

The current generic lifecycle still uses the established `WaitForSessionEndAsync` behavior for adapters that have not yet opted into the structured evidence provider. AR-2/AR-3 must prove the game-specific evidence before the production lifecycle treats that evidence as authoritative.

Likewise, generic graceful-stop and safe-capture result types now exist, but `Stop and Save` must not become available for an adapter until that adapter proves the corresponding capability. This is intentionally deferred to AR-2/AR-3 rather than guessed in AR-1.

## Remaining AR-1 gate

- successful build/test confirmation under the repository warnings-as-errors/nullability rules.

## Milestone boundary

AR-1 does not:

- add game-name branches to Core or the unified action contract;
- claim Factorio readiness/graceful stop is proven;
- claim Palworld readiness/graceful stop/identity portability is proven;
- implement the remote backend;
- implement a Windows Service;
- add multiple simultaneous writable sessions per device;
- add branches/merge/save-conflict behavior.

AR-1 may be marked green only after the implementation/test suite executes successfully. Until then, the contract implementation is complete but validation remains pending.
