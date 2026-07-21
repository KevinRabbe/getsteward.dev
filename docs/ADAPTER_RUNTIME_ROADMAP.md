# Adapter and Background Runtime Roadmap

## Purpose

This roadmap defines the generic runtime that carries one World through a complete session and the adapter contracts that make each game safe to support.

The runtime is the bridge between the UI and backend:

```text
UI chooses World/action
-> runtime acquires state and reservation
-> adapter prepares/restores/launches/observes/captures
-> runtime stores/verifies/commits
-> UI receives final state
```

Steward is background-first because this runtime remains active while the user is inside the game.

## Planning status

Status: **planning locked**.

No production runtime refactor, adapter contract change, new adapter behavior, tray/service implementation, process supervisor, or shared-backend integration begins until this roadmap and the master planning gate are complete.

Allowed work during the lock:

- lifecycle and capability documentation;
- process/session diagrams;
- adapter acceptance-test design;
- read-only inspection of Factorio, Palworld, Core, Infrastructure, and Desktop code;
- real-game fact verification that does not modify code or live Worlds;
- disposable manual tests against copied data only when required to settle a planning decision.

## Runtime boundary

The generic runtime owns:

- orchestration of the complete session transaction;
- one active managed session per World;
- first-release limit on active managed sessions per desktop instance/device;
- interaction with shared/local storage;
- interaction with session coordination;
- local cache/materialization;
- recovery record lifecycle;
- background application lifetime;
- mapping adapter outcomes to generic lifecycle state;
- durable candidate preservation until commit is resolved.

The adapter owns:

- game and World discovery;
- environment inspection and preparation;
- state restore and capture;
- game/server launch arguments and configuration;
- which process or server owns the writable session;
- readiness checks;
- safe stop behavior;
- safe capture timing;
- game-specific package validation;
- native join behavior where supported.

The UI owns presentation and user commands. The backend owns remote durable state and distributed reservation truth.

## First-release runtime process model

Preferred initial decision:

> Steward runs as one user-session desktop process with a tray/background lifetime, not as a Windows Service.

Reasons:

- game launch and Steam interaction occur in the signed-in user's session;
- tray/status interaction is required;
- a separate privileged service adds installation, update, IPC, security, and debugging complexity;
- hard application/OS crashes are already handled through durable recovery records.

The window may close or minimize while the process remains in the tray during active work. Explicit Quit is blocked or strongly guarded while a writable session still requires capture/commit.

A Windows Service is reconsidered only after evidence shows that the user-session process cannot meet required reliability.

## First-release concurrency model

Initial runtime limit:

- at most one active writable Steward-managed session per World globally;
- at most one active writable Steward-managed session per desktop instance/device.

This avoids simultaneous game/server supervision, conflicting tray controls, ambiguous shutdown behavior, and competing resource use during the first commercial release.

Multiple cached Worlds and downloads may exist, but only one managed writer lifecycle runs at a time on one device.

## Generic lifecycle state machine

Internal runtime states:

```text
Idle
-> ResolvingWorld
-> AcquiringReservation
-> DownloadingState
-> PreparingEnvironment
-> RestoringState
-> RegisteringRecovery
-> StartingSession
-> Running
-> Stopping
-> WaitingForSafeCapture
-> Capturing
-> Uploading
-> Verifying
-> Committing
-> Finalizing
-> Completed
```

Failure states:

```text
Blocked
RecoveryNeeded
CleanupPending
ReservationUncertain
```

Not every state must be shown separately to the user. The UI maps them into Ready, Preparing, Running, Saving, Blocked, and Recovery needed.

## Session start contract

### Local-only World

```text
load local World/current head
-> acquire local exclusive reservation
-> materialize current state
-> adapter prepares environment
-> adapter restores state
-> persist recovery record
-> adapter launches local session
-> adapter proves real session start
-> runtime enters Running
```

### Shared World

```text
refresh remote World/current head
-> acquire distributed reservation using expected head
-> download/verify current state when cache is missing or stale
-> adapter prepares environment
-> adapter restores state
-> persist recovery record locally
-> adapter launches local or hosted session
-> adapter proves real session start
-> heartbeat reservation
-> runtime enters Running
```

If launch never starts gameplay, controlled temporary work may be discarded. Once gameplay starts, the workspace becomes recovery evidence.

## Running-session contract

While Running, the runtime must:

- keep the reservation alive while connectivity permits;
- continue observing the adapter-defined session owner;
- distinguish client exit from dedicated-server end;
- preserve local recovery metadata;
- refuse another managed writable session on the same device;
- expose status to the tray/UI;
- handle UI minimize/close without abandoning the lifecycle;
- avoid treating one missed remote heartbeat as proof the local game stopped;
- avoid application update/restart that would abandon the session.

## Session stop and completion contract

### Local session

The adapter normally observes the game process/session ending. The runtime then moves to safe capture.

### Hosted session

When the user selects Stop and Save, or when the adapter/game reports a valid session end:

```text
request adapter-controlled graceful stop
-> wait for authoritative server/session end
-> wait for adapter-defined save completion
-> capture and validate candidate
-> upload/store candidate
-> verify stored bytes/metadata
-> commit with expected head and reservation generation
-> release reservation
-> finalize workspace
-> clear recovery record
```

The runtime must not kill a game/server process and immediately capture based only on a fixed delay unless the adapter explicitly validates that behavior for that game.

## Adapter session evidence contract

Planning must define a generic evidence/result shape sufficient for the runtime to distinguish:

- launch requested but no real session started;
- local game session started;
- hosted server became ready;
- session still running;
- graceful stop requested;
- session ended normally;
- session disappeared unexpectedly;
- save/capture boundary safe;
- capture blocked or incomplete.

The contract should describe facts, not force every adapter into one PID model.

## Safe capture contract

Before capture, the adapter must be able to establish the minimum game-specific conditions required for a restorable state.

Possible evidence includes:

- authoritative process exited normally;
- graceful save/shutdown command completed;
- required files exist;
- game lock/temporary markers disappeared;
- files stabilized according to a validated rule;
- server API confirmed save;
- package-specific integrity checks passed.

The runtime asks the adapter for a safe capture result. It does not implement one universal timer or file-set assumption.

## Capture and commit contract

```text
adapter captures opaque package
-> adapter validates required game-specific contents
-> runtime records package hash/size
-> local or remote store publishes immutable candidate
-> runtime verifies durable result
-> runtime commits candidate against expected head and active reservation
```

Results:

- `Committed`: new state current;
- `Unchanged`: no state change, existing current state remains;
- `HeadChanged`: candidate stale, preserve for explicit recovery/diagnostics;
- `ReservationMismatch`: do not commit;
- capture/store/verification failure: preserve candidate/workspace and enter Recovery needed.

Cleanup occurs only after authority and durability are known.

## Connectivity-loss behavior

During an active shared session:

- the local game/server may continue when network connectivity to the backend is lost;
- the remote reservation becomes uncertain after planned heartbeat rules;
- no other device receives a new writable reservation automatically;
- the runtime keeps retrying only with bounded safe behavior;
- at session end, captured state remains local until upload/commit succeeds;
- the user sees Saving or Recovery needed, not Ready;
- reclaim by another device invalidates the old session generation, so a late old host cannot commit silently.

The exact retry windows and user actions depend on the backend planning decision.

## Application close, shutdown, and update behavior

Planning default:

- closing the main window minimizes to tray while active work exists;
- explicit Quit during Running or Saving requires a clear warning and attempts a safe stop when supported;
- forced termination may leave an Active/RecoveryPending record;
- OS shutdown receives best-effort graceful handling but never promises completion;
- application self-update is deferred until no active writable session or unresolved capture exists;
- startup scans recovery records before presenting affected Worlds as Ready.

## Adapter capability model

Capabilities must describe supported product actions without game-name checks.

Minimum planned capabilities:

- installation discovery;
- World discovery/import;
- local launch;
- temporary host launch;
- native/client join;
- automatic session observation;
- graceful hosted stop;
- automatic safe capture;
- environment inspection;
- environment preparation/isolation;
- exact game-version support;
- exact mod-version support;
- environment verification;
- automatic repair where validated.

Capabilities describe proven support. An adapter must not advertise a capability merely because a command or API may exist.

## Current adapter capability baseline

### Factorio

Validated or substantially proven:

- installation and save discovery;
- safe ZIP import;
- exact game-version checks;
- isolated write-data and mod preparation;
- local launch;
- hosted launch paths;
- client connection primitive;
- Steam bootstrap/process handoff observation;
- safe state capture and replay;
- canonical commit.

Remaining planning/hardening:

- final authoritative hosted-server/client model for release;
- graceful stop contract and readiness evidence;
- exact shared-environment repair policy;
- two-device handoff;
- final adapter capability declarations.

### Palworld

Validated or substantially proven:

- client and dedicated-server discovery;
- local/dedicated World discovery;
- unchanged World migration to server layout;
- dedicated-server selection and launch;
- server process observation;
- portable capture excluding backup noise;
- canonical staging/restore/byte verification;
- canonical commit.

Remaining planning/hardening:

- validated graceful save/shutdown control used by the production adapter;
- server readiness evidence;
- automatic or manual Join capability contract;
- duplicate discovery policy;
- player identity limitation handling;
- two-device handoff;
- final adapter capability declarations.

## Adapter acceptance contract

Every supported adapter must prove the following with controlled test data:

1. Discover the intended installation and World without mutation.
2. Import a copied World and preserve the source.
3. Describe the environment required by that World.
4. Prepare a controlled workspace or safe equivalent.
5. Restore the stored state.
6. Launch and prove a real local or hosted session.
7. Observe the correct process/server lifecycle.
8. Make a visible gameplay change.
9. Reach a validated safe capture point.
10. Capture and validate a portable package.
11. Commit the package through the canonical transaction.
12. Restore and visibly confirm the change in the next session.
13. Preserve recovery evidence under injected failure.
14. Complete a shared two-device handoff before commercial release.

Hosted support additionally proves that client exit does not incorrectly end a still-running authoritative server.

## Runtime test strategy

Required automated layers:

- deterministic lifecycle state-machine tests;
- fake adapter tests for every start/end/failure transition;
- fake storage/coordinator tests;
- process handoff simulations;
- stale reservation and head-change tests;
- crash/restart recovery-record tests;
- bounded retry tests;
- desktop lifetime/tray tests where practical;
- adapter filesystem tests with temporary fixtures.

Required real-system validation:

- Factorio local and hosted sessions;
- Palworld dedicated sessions;
- graceful and forced termination;
- network interruption during Running and Saving;
- PC A -> PC B -> PC A handoff;
- application restart with Active/RecoveryPending records.

## Adapter/runtime roadmap milestones

### AR-0: Planning contract

Deliverables:

- final lifecycle state machine;
- desktop/tray process model;
- one-active-session-per-device decision;
- adapter capability definitions;
- session evidence contract;
- safe stop/capture contract;
- connectivity-loss behavior;
- application close/update behavior;
- recovery integration;
- Factorio and Palworld capability matrices;
- adapter acceptance tests;
- cross-workstream contract with backend and UI.

No runtime or adapter code starts before AR-0 and the master planning gate are complete.

### AR-1: Generic runtime extraction/hardening

After planning unlock:

- one reusable session runner;
- explicit lifecycle events/state;
- background/tray lifetime integration;
- local cache/materialization boundary;
- recovery record orchestration;
- no game-name branches;
- deterministic fake-adapter tests.

### AR-2: Factorio completion contract

- finalized local/host process ownership;
- readiness and graceful stop;
- safe capture evidence;
- environment mismatch behavior;
- adapter capability declarations;
- failure-injection acceptance runs.

### AR-3: Palworld completion contract

- finalized dedicated-server readiness;
- graceful save/shutdown;
- safe capture evidence;
- duplicate World discovery;
- Join capability/fallback;
- player identity limitation UX contract;
- failure-injection acceptance runs.

### AR-4: Shared backend integration

- distributed reservation acquisition/heartbeat;
- state download/cache/verification;
- candidate upload/commit;
- offline/uncertain behavior;
- stale session generation rejection;
- local candidate preservation.

### AR-5: Two-device handoff

- complete one game PC A -> PC B -> PC A;
- reject competing writer;
- recover interrupted upload/session;
- repeat with the second initial adapter.

### AR-6: Commercial hardening

- startup recovery scan;
- safe close/minimize/update behavior;
- bounded resources and retries;
- logs/diagnostics without secrets;
- installer/update interactions;
- long-session and large-World validation;
- explicit unsupported-capability behavior.

## Decisions still required before AR-0 completes

- Confirm single desktop/tray process versus separate background service.
- Confirm one active writable session per device for first release.
- Exact generic lifecycle state names and transition ownership.
- Session evidence/result model sufficient for local games, launchers, and dedicated servers.
- Exact safe cancellation boundary before and after launch.
- Stop and Save availability rules.
- App close, OS shutdown, and update behavior during active work.
- Connectivity-loss retry and recovery interaction with backend reservation semantics.
- Local cache/materialization ownership and cleanup limits.
- Recovery candidate actions exposed to UI.
- Final Factorio authoritative host model.
- Final Palworld graceful save/shutdown and readiness model.
- Palworld player identity limitation treatment for the first release.
- Capability naming and when a capability is considered validated.
- Which failure scenarios are mandatory release tests.

## Adapter/runtime planning completion gate

Planning is complete only when:

- every decision above is resolved or explicitly deferred without blocking the first implementation slice;
- UI can map every runtime state to a clear user state/action;
- backend reservation and commit semantics match runtime crash/connectivity behavior;
- Factorio and Palworld have explicit capability and limitation matrices;
- session start, end, safe capture, commit, and recovery contracts are testable without guessing;
- first-release concurrency and application-lifetime rules are accepted;
- no runtime responsibility depends on merging, branches, social governance, or permanent game-server infrastructure;
- the master roadmap lifts the planning lock.