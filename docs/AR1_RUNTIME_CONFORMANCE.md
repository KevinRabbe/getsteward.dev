# AR-1 Runtime Conformance

AR-1 hardens the generic runtime contract after P0 without implementing Factorio- or Palworld-specific readiness/shutdown behavior.

## Current status

Status: **in progress — core conformance boundaries implemented; adapter evidence integration and runtime-state/tray work remain**.

Implemented in the current AR-1 slice:

- `Start World` and `Host World` reuse the same generic canonical-writer lifecycle;
- persistent Steward sharing is no longer a prerequisite for temporary Host;
- regression coverage proves an `Only on this PC` World can Host and remains local-only;
- one active writable managed lifecycle per desktop runtime through `ManagedWritableSessionGate`;
- the device gate sits above per-World coordination so a second World is rejected before a coordinator acquisition;
- generic Join capability result supports automatic, guided manual, unsupported, environment-blocked, and identity-blocked outcomes without game-name branches;
- legacy `AutomaticClientJoin` adapters remain source-compatible through the default Join capability mapping;
- optional `IGameSessionEvidenceProvider` contract represents structured session facts, graceful hosted stop, and safe-capture evidence without treating a PID as the universal session definition.

## Important compatibility boundary

The new structured session-evidence interface is **not** treated as proof that current Factorio or Palworld adapters already satisfy readiness/stop/capture requirements.

Until AR-2/AR-3 provide controlled evidence and implement the provider contract:

- existing adapters keep their current lifecycle behavior;
- Core does not invent host-readiness or safe-capture facts;
- Factorio/Palworld-specific process, readiness, graceful-stop, and identity limitations stay inside those adapters;
- UI/Core do not branch on game name.

## Remaining AR-1 work

- expose explicit generic lifecycle phase/state events for background/UI binding;
- separate lifecycle orchestration from desktop presentation without adding a second product model;
- define safe runtime handling for structured launch evidence before an adapter opts into it;
- define integration point for safe hosted Stop and Save capability;
- connect device-wide writable responsibility to tray/quit/update gating;
- preserve startup recovery scan as a prerequisite before affected Worlds can be shown Ready;
- deterministic fake-adapter tests for pre-launch failure, post-launch failure, safe capture, cleanup-pending, and recovery-pending transitions;
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
