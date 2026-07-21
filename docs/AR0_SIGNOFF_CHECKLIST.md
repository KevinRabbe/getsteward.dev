# AR-0 Sign-off Checklist

This is the adapter/runtime planning checkpoint before runtime extraction or
adapter behavior changes. It records proposed defaults; it does not lift the
master planning lock.

Status: **AR-0 approved by the product owner.** The master planning lock remains
active until the remaining master-gate work is complete.

## Proposed decisions

| Area | Proposed first-release decision | Evidence/status |
|---|---|---|
| Desktop lifetime | One user-session desktop process with tray/background lifetime; no Windows Service | Proposed; current WPF app has no tray implementation yet |
| Device concurrency | One active writable managed session per desktop/device | Proposed; current local coordinator is per-World, not device-wide |
| Lifecycle ownership | Runtime owns generic orchestration; adapter owns game evidence and safe capture | Aligned with Core boundary |
| Internal state | Preserve detailed runtime phases; map to UI states from the shared contract | Proposed |
| Recovery naming | Preserve current internal `RecoveryPending` names if migration is unnecessary; expose `RecoveryNeeded` to runtime/UI | Current Core session/recovery enums use `RecoveryPending` |
| Session evidence | Structured adapter result facts, not PID-only handles | Required gap; current `GameSessionHandle` is PID/time only |
| Pre-launch cancellation | Cancel may discard controlled temporary work before gameplay is proven | Safe cleanup boundary |
| Post-launch cancellation | Preserve workspace/recovery evidence; stop safely when supported; never abandon silently | Conservative failure rule |
| Stop and Save | Available only when adapter can prove safe hosted stop/capture; otherwise unavailable | Proposed; Core has no universal safe-stop command today |
| Close/update | Minimize to tray during active work; guard Quit; defer self-update until no active or unresolved writable lifecycle; scan recovery on startup | Existing planning default |
| Connectivity loss | Active session may continue; reservation becomes uncertain; candidate remains local; no competing writer | Matches backend contract |
| Cache/materialization | Runtime owns cache/recovery lifecycle; adapter owns workspace layout, restore, capture, and cleanup authority | Boundary proposal |
| Join capability | Generic capability result supports native/game/Steam automatic, adapter automatic, guided manual, unsupported | Proposed gap; current Core flag is automatic-only |
| Factorio host | Temporary local Factorio host using validated native multiplayer/process handoff; no permanent Steward host | Substantially evidenced; final release proof required |
| Palworld host | Temporary Palworld dedicated-server session with readiness and graceful-save proof | Substantially evidenced; readiness/stop proof required |
| Palworld identity | World portability may proceed only with explicit player-identity limitation disclosure and safe adapter handling | Limitation remains game-specific |
| Recovery actions | Retry recovery, Export recovery copy, Continue from last safe state when proven safe | Matches UI contract |

## Session evidence contract

Adapters should return structured facts sufficient to distinguish:

- launch requested;
- real local session started;
- hosted server ready;
- session still running;
- graceful stop requested;
- session ended normally or unexpectedly;
- safe capture boundary established;
- capture blocked/incomplete;
- recovery evidence preserved.

Evidence may include process ids, start times, server readiness, shutdown
responses, stable file/package checks, and adapter-specific diagnostics. A PID is
an input to evidence, never the universal definition of a session.

## Capability result contract

The runtime exposes generic capability outcomes:

```text
SupportedAutomatic
SupportedGuidedManual
Unsupported
BlockedByEnvironment
BlockedByIdentityLimitation
```

The UI exposes one generic action, such as Join, and receives the adapter-owned
instructions/data only for `SupportedGuidedManual`. Core must not branch on game
name to interpret these outcomes.

## AR-0 acceptance batch

Before implementation begins, verify:

1. The desktop can remain alive in the user session while the window is closed
   or minimized.
2. A second managed writable session is rejected on the same device.
3. A fake adapter proves every lifecycle phase and failure transition.
4. Pre-launch cancellation cleans only controlled temporary work.
5. Post-launch failure preserves recovery evidence.
6. Stop and Save is exposed only for a safe adapter capability.
7. A launcher/process handoff does not end a session prematurely.
8. Backend uncertainty keeps the World unavailable to competing writers.
9. Guided manual Join can be represented without a game-name branch.
10. Factorio local/host/capture/replay and Palworld dedicated-server
    readiness/stop/capture/restore are tested with controlled data.
11. Application restart finds unresolved recovery before showing Ready.
12. The PC A -> PC B -> PC A handoff is specified for both initial adapters.

## Post-approval evidence

The product decisions above are approved. The following evidence is still
required during implementation and release validation:

- tray/background lifetime and device-wide session enforcement;
- structured session evidence and capability-result implementation;
- safe-stop and cancellation behavior for each supported adapter;
- Factorio temporary-host and Palworld readiness/identity acceptance runs;
- the AR-0 acceptance batch;
- agreement with final UI-0 terminology and actions.

AR-1 implementation remains blocked until AR-0 and the master planning gate are
approved.
