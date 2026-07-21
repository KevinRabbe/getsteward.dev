# Adapter and Background Runtime Roadmap

## Purpose

This roadmap defines the generic local runtime that carries one World through a complete Steward session and the adapter contracts that make each game safe to support.

```text
UI chooses World/action
-> runtime resolves state/authority
-> adapter prepares/restores/launches/observes
-> adapter proves safe capture
-> runtime stores/verifies/commits
-> UI receives final state
```

Steward is background-first because this runtime remains responsible while the user is inside the game.

## Planning status

Status: **AR-0 approved; master planning lock remains active**.

No production runtime refactor, adapter contract change, background/tray implementation, process supervisor, or shared-backend integration begins until the master lock is explicitly lifted.

## Runtime boundary

The generic runtime owns:
- orchestration of the complete session transaction;
- one active managed writable session per World;
- first-release one-active-managed-writer-per-device limit;
- interaction with local/shared storage and session coordination;
- local cache/materialization/recovery lifecycle;
- background application responsibility;
- mapping backend/adapter outcomes into generic product state;
- durable candidate preservation until authority is resolved.

The adapter owns:
- installation/game/World discovery;
- environment inspection/preparation;
- restore/capture package shape;
- launch and host arguments/configuration;
- which process/server represents the writable session;
- readiness evidence;
- graceful/safe stop behavior;
- safe capture timing;
- game-specific package validation;
- validated Join capability: Steam/game-native automatic, adapter-controlled automatic, guided manual, or unsupported;
- game-specific identity/environment limitations.

The UI owns presentation/user commands. The backend owns shared durable authority.

## First-release process model

> **One user-session desktop process with tray/background lifetime; no Windows Service.**

Reasons:
- game launch and Steam interaction happen in the signed-in user's session;
- tray/status interaction is required;
- a privileged service adds installation/update/IPC/security/debug complexity without current evidence of need;
- durable recovery records handle hard application/OS failure conservatively.

Whenever the Steward process runs, its tray icon is visible.

Closing the main window hides it. Explicit Quit is available only when no active/unresolved World responsibility would be abandoned.

A Windows Service is reconsidered only if measured reliability proves the user-session process insufficient.

## First-release concurrency model

- at most one active writable Steward-managed session per World globally;
- at most one active writable Steward-managed session per desktop/device.

Multiple cached Worlds/downloads may exist, but one device does not supervise multiple writable World lifecycles simultaneously in the first release.

## Generic lifecycle state machine

Internal runtime phases:

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

Exceptional/internal states include:

```text
Blocked
RecoveryNeeded
CleanupPending
ReservationUncertain
WaitingToSync
```

Current Core may preserve older internal names such as `RecoveryPending` where migration is unnecessary, but user-facing mapping is fixed:

| Runtime meaning | UI term |
|---|---|
| safe/available | Ready |
| preparing/materializing/restoring/starting | Preparing |
| local writable session active | Running |
| hosted writable session active | Hosting |
| remote host owns reservation but not ready | Host is starting |
| another ready active writer | Someone is playing |
| capture/store/verify/commit/finalize incomplete | Saving World |
| local candidate preserved; remote handoff unresolved | Waiting to sync |
| capability/environment/identity problem | Action required |
| unresolved prior handoff authority/evidence | Recovery needed |

## Session start contract

### Local-only World

```text
load local current head
-> acquire local exclusive reservation
-> materialize state
-> adapter prepares environment
-> adapter restores state
-> persist recovery record
-> adapter launches local or hosted session
-> adapter proves real session start/readiness as required
-> runtime enters Running/Hosting
```

A local-only World may Host when adapter/runtime temporary-host capability exists. Persistent Steward sharing is not a prerequisite.

### Shared World

```text
authenticate/refresh World/current head
-> acquire distributed expected-head reservation
-> download/verify state/environment when cache missing/stale
-> adapter prepares environment
-> adapter restores state
-> persist local recovery record
-> adapter launches local or hosted session
-> prove session start/readiness
-> heartbeat reservation
-> Running/Hosting
```

If launch never reaches proven gameplay, controlled temporary work may be cleaned according to explicit ownership. Once gameplay begins, workspace/state becomes recovery evidence.

## Session evidence contract

Adapters expose facts sufficient to distinguish:
- launch requested but no real session started;
- local session started;
- hosted server/session became ready;
- session still running;
- graceful stop requested;
- session ended normally;
- session disappeared unexpectedly;
- safe capture boundary established;
- capture blocked/incomplete;
- recovery evidence preserved.

Evidence may include PID/process start time, launcher handoff, server readiness, shutdown response, stable file/package checks, and adapter-specific diagnostics.

A PID is evidence input, not the universal definition of a session.

## Running-session contract

While Running/Hosting, runtime must:
- keep shared reservation heartbeat alive while connectivity permits;
- observe adapter-defined authoritative session owner;
- distinguish client exit from dedicated-server end;
- preserve recovery metadata;
- reject another managed writable session on the same device;
- expose status through UI/tray;
- allow main window hide without abandoning work;
- never treat one missed heartbeat as proof local gameplay stopped;
- defer self-update/restart while writable responsibility exists.

## Stop and completion contract

### Local session

Adapter normally observes the game/session ending, then proves safe capture.

### Hosted session

**Stop and Save** is exposed only when the adapter can perform/validate a safe hosted stop.

```text
request adapter-controlled graceful stop
-> wait for authoritative hosted session end
-> prove save/capture boundary
-> capture and validate candidate
-> upload/store candidate
-> verify stored bytes/metadata
-> expected-head + generation commit
-> resolve reservation
-> finalize workspace
-> clear/resolve recovery record
```

Runtime never kills a process and captures after an arbitrary universal delay unless that exact behavior is validated by the adapter for the game.

## Safe capture contract

Before capture, adapter establishes the minimum game-specific conditions required for a restorable package.

Possible evidence:
- authoritative process/server ended normally;
- graceful save/shutdown completed;
- required files exist;
- lock/temp markers disappeared;
- files stabilized under a validated rule;
- server API confirmed save;
- package-specific integrity checks passed.

Runtime asks for a safe capture result. It does not implement one universal timer/file assumption.

## Capture and commit contract

```text
adapter captures opaque package
-> adapter validates game-specific required contents
-> runtime records hash/size
-> local/remote store publishes immutable candidate
-> runtime verifies durable result
-> runtime commits against expected head + valid reservation generation
```

Generic outcomes:
- `Committed` -> new canonical state;
- `Unchanged` -> existing canonical state remains;
- `HeadChanged` -> preserve stale candidate; Recovery needed as applicable;
- `ReservationMismatch` / invalid generation -> candidate cannot commit; preserve recovery evidence;
- capture/store/verification failure -> preserve candidate/workspace; Recovery needed or Waiting to sync according to authority/connectivity.

Cleanup occurs only after durability and authority are known.

## Connectivity-loss contract

Runtime follows BE-D005 and BE-D008.

### Before a new shared session

Backend identity/head/reservation cannot be verified:

```text
Connection required
-> no Start World
-> no Host World
-> no Join
```

### During an already valid shared session

- local game/server may continue;
- heartbeat retry is bounded/backed off;
- backend may move Active -> Uncertain after ~2 minutes without valid heartbeat;
- no competing writer becomes automatically available;
- local session generation/starting head/recovery evidence remain durable.

### Session ends while disconnected

```text
adapter proves safe capture
-> capture/validate candidate locally
-> persist candidate durably
-> Waiting to sync
```

On reconnect runtime revalidates authentication, session generation, and expected canonical head.

Still-valid generation + unchanged head:
- upload/verify/commit/finalize -> Ready.

Invalidated generation or changed head:
- no automatic overwrite;
- preserve candidate;
- Recovery needed.

Retry exhaustion never means responsibility/candidate is silently abandoned.

## Application close/shutdown/update contract

- closing main window hides to tray;
- ordinary Quit is blocked while Running, Hosting, Saving World, Waiting to sync, or unresolved recovery would be abandoned;
- controlled hosted Stop and Save is used where supported;
- forced termination may leave durable recovery evidence;
- OS shutdown gets best-effort graceful handling but no false completion promise;
- self-update waits until no active/unresolved writable lifecycle exists;
- startup scans recovery records before presenting affected Worlds as Ready.

## Adapter capability model

Capabilities describe proven product behavior, not speculative commands/APIs.

Minimum planned capability areas:
- installation discovery;
- World discovery/import;
- local launch;
- temporary host launch;
- validated Join capability;
- session observation;
- host readiness evidence;
- graceful hosted stop;
- automatic safe capture;
- environment inspection;
- environment preparation/isolation;
- exact game-version support;
- exact mod-version support where applicable;
- environment verification;
- repair where validated;
- explicit identity/environment limitation result.

Generic capability outcomes include:

```text
SupportedAutomatic
SupportedGuidedManual
Unsupported
BlockedByEnvironment
BlockedByIdentityLimitation
```

UI/Core never branch on game name to interpret these outcomes.

## Factorio capability/limitation matrix

Substantially validated:
- Steam/non-default-library discovery;
- safe save import;
- exact game-version checks;
- isolated write-data/mod preparation;
- local launch;
- temporary hosted launch paths;
- client connection primitive;
- Steam bootstrap/process handoff observation;
- safe state capture/replay;
- canonical commit.

Implementation/release evidence still required:
- finalized authoritative temporary host/client ownership;
- readiness proof;
- graceful stop/safe capture proof;
- exact shared-environment repair behavior;
- final Join capability result;
- two-device handoff.

These are implementation/acceptance evidence requirements, not unresolved AR-0 product-model questions.

## Palworld capability/limitation matrix

Substantially validated:
- client/dedicated-server discovery;
- local/dedicated World discovery;
- unchanged World migration into server layout;
- temporary dedicated-server selection/launch;
- server process observation;
- portable capture excluding backup noise;
- staging/rollback restore;
- restored-byte verification;
- canonical commit.

Implementation/release evidence still required:
- validated graceful save/shutdown control;
- server readiness proof;
- final Join automatic/guided-manual result;
- duplicate discovery behavior;
- explicit player-identity limitation handling;
- two-device handoff.

These remain adapter-specific evidence/limitations and do not expand Core.

## Adapter acceptance contract

Every release-supported adapter must prove with controlled data:

1. discover intended installation/World without mutation;
2. import a copied World and preserve source;
3. describe required environment;
4. prepare controlled workspace/safe equivalent;
5. restore stored state;
6. launch and prove a real local/hosted session;
7. observe correct session ownership/lifecycle;
8. make visible gameplay change;
9. establish validated safe capture boundary;
10. capture/validate portable package;
11. commit through canonical transaction;
12. restore and visibly confirm change next session;
13. preserve recovery evidence under injected failure;
14. represent Join capability without game-name branching;
15. complete shared two-device handoff before commercial release.

Hosted support additionally proves client exit does not incorrectly terminate/complete a still-running authoritative server.

## Runtime test strategy

Automated layers:
- deterministic lifecycle state-machine tests;
- fake adapter tests for every phase/failure transition;
- fake storage/coordinator/backend tests;
- process handoff simulations;
- stale reservation/head-change tests;
- crash/restart recovery tests;
- bounded retry tests;
- device-wide concurrency tests;
- tray/lifetime tests where practical;
- adapter filesystem tests with temporary fixtures.

Real-system validation:
- Factorio local/host sessions;
- Palworld dedicated sessions;
- graceful/forced termination;
- network interruption during Running/Hosting/Saving World;
- PC A -> PC B -> PC A handoff;
- application restart with unresolved recovery;
- Join capability/fallback validation;
- environment/identity safe blocking.

# Adapter/runtime milestones after planning unlock

## AR-1: Generic runtime extraction/hardening

- reusable session runner;
- explicit lifecycle events/state;
- background/tray integration;
- device-wide writable-session gate;
- local cache/materialization boundary;
- recovery orchestration;
- structured session evidence/capability results;
- deterministic fake-adapter tests;
- no game-name branches.

## AR-2: Factorio completion

- finalized local/host process ownership;
- readiness/graceful stop;
- safe capture evidence;
- environment mismatch/repair behavior;
- Join capability;
- failure-injection acceptance runs.

## AR-3: Palworld completion

- dedicated-server readiness;
- graceful save/shutdown;
- safe capture evidence;
- duplicate discovery;
- Join capability/fallback;
- player identity limitation UX/result;
- failure-injection acceptance runs.

## AR-4: Shared backend integration

- distributed reservation/heartbeat;
- state download/cache/verification;
- candidate upload/commit;
- Waiting to sync;
- stale generation rejection;
- local candidate preservation/recovery.

## AR-5: Two-device handoff

- one game PC A -> PC B -> PC A;
- competing writer rejection;
- interrupted upload/session recovery;
- repeat with second initial adapter.

## AR-6: Commercial hardening

- startup recovery scan;
- safe close/minimize/update;
- bounded resources/retries;
- redacted logs/diagnostics;
- installer/update interaction;
- long-session/large-World validation;
- explicit unsupported capability behavior.

# AR-0 completion gate

Status: **complete and approved**.

AR-0 is complete because:
- process/tray model is accepted;
- device concurrency is accepted;
- lifecycle/state ownership is defined;
- session evidence/capability result contracts are defined;
- safe stop/capture/cancellation semantics are defined;
- connectivity/recovery matches BE-D005/BE-D006/BE-D008/BE-D009;
- UI mapping matches final UI-D008 terminology;
- Factorio/Palworld capability and limitation matrices are explicit;
- release acceptance evidence is specified;
- no runtime behavior depends on merging, branches, social governance, or permanent Steward game-server infrastructure.

Production runtime/adapter changes remain blocked only by the master planning lock.