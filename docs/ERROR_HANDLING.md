# Error Handling and Failure Boundaries

Steward handles user-owned game state. Error handling therefore favors preserving the last valid World and recoverable evidence over continuing after an unknown failure.

## Core rule

Do not catch an exception merely to keep the application moving.

A catch block must perform at least one meaningful responsibility:

- translate a lower-level failure into a stable typed product failure;
- preserve or update recovery state;
- release a safely releasable resource or reservation;
- add diagnostics at an application boundary;
- deliberately suppress only a secondary cleanup failure while preserving the primary failure.

Unknown failures stop the current operation.

## Failure layers

### Core and domain

Core exposes typed failures for conditions callers can reason about, such as:

- missing World or revision;
- adapter mismatch;
- World integrity failure;
- persistence compatibility failure;
- session reservation conflict;
- stale expected head;
- environment reproduction failure;
- recovery required.

Core must not convert arbitrary storage, adapter, or coordination failures into success.

### Lifecycle

Lifecycle failure handling is conservative:

- the current World head advances only after durable storage and verification;
- a writable reservation is released only when the lifecycle can safely release it;
- a primary lifecycle failure is not replaced by a secondary cleanup/release failure;
- adapter-produced packages are deleted only with explicit cleanup authority;
- post-launch failures preserve the prepared workspace;
- failed workspace cleanup becomes `CleanupPending`;
- a stale writer cannot overwrite a newer current state;
- a transient network loss does not prove that the game/server session ended.

### Adapter

Adapters translate game-specific failures into stable categories where useful, but they must not hide uncertain session or save state.

Examples:

- launched process disappeared before a real session was proven;
- dedicated server failed readiness;
- graceful shutdown failed;
- save files never stabilized;
- required World files are missing;
- restored bytes do not match the package;
- environment requirements cannot be reproduced safely.

When an adapter cannot prove safe completion, it fails and preserves recovery evidence.

### Storage and coordination

Storage and session coordination have different failure semantics.

Storage failure:

```text
new state not committed
-> previous valid head remains authoritative
```

Coordination uncertainty:

```text
active reservation cannot be proven safely released
-> World remains unavailable or Recovery needed
-> do not start a competing writer
```

A remote timeout is not automatic proof that a write failed, a session ended, or another device may take over.

### Desktop application boundary

The desktop is the primary commercial application boundary.

Expected typed failures should produce:

- concise user-facing state;
- one clear recovery action where possible;
- no raw stack trace;
- local diagnostic reference when useful.

Operational failures should:

- stop the affected operation;
- preserve the last valid World;
- preserve recovery evidence;
- write local diagnostics when possible;
- avoid presenting the World as Ready until safety is established.

Unexpected failures should:

- stop the current operation;
- generate a local incident id;
- persist diagnostic details where possible;
- never continue in an unknown state.

### Development CLI and tools

The CLI and probes remain development boundaries. They may expose stable exit codes and more technical detail, but they must preserve the same state and recovery semantics as the desktop.

## Cancellation

Cancellation propagates through discovery, import, preparation, process observation, capture, storage, transfer, verification, and recovery where supported.

Before launch, cancellation may clean controlled temporary work.

After gameplay begins, cancellation is a failure path unless the adapter can still prove a clean session end and complete the handoff. Otherwise preserve the workspace and enter recovery.

Do not treat closing the UI as permission to abandon an active writable session.

## Retry policy

Retries are allowed only when the operation is explicitly safe and idempotent.

Good candidates:

- reading immutable metadata;
- resuming a chunked download;
- verifying already stored immutable bytes;
- retrying an expected-head read;
- idempotent publication where the backend guarantees it.

Dangerous candidates that require explicit design:

- launching a second game/server process;
- sending repeated shutdown commands;
- overwriting mutable save directories;
- advancing the World head;
- deleting recovery evidence;
- releasing a reservation after uncertain network state.

Retries must be bounded and observable.

## Diagnostics

A diagnostic entry may include:

- UTC timestamp;
- incident id;
- operation and lifecycle phase;
- World id in a safe internal form;
- adapter id;
- starting revision and expected head;
- process/session metadata;
- OS/runtime information;
- exception details.

Never log:

- authentication tokens;
- passwords or private join tokens;
- arbitrary save contents;
- secrets from game/server configuration;
- unnecessary personal data.

Diagnostics remain local unless the user explicitly chooses a future upload workflow.

## User-facing lifecycle states

Normal states:

```text
Ready
Preparing
Running
Saving
```

Failure states:

```text
Blocked
Recovery needed
Cleanup needed
```

The UI should state what is safe, what is uncertain, and what action is available. It should not expose internal exception names as the normal product experience.

## Do not do this

```text
try risky state-changing operation
catch everything
ignore failure
mark World Ready
```

Also avoid:

- freeing a World because one heartbeat was missed;
- deleting a workspace to clear an error badge;
- advancing the head because a newer file timestamp exists;
- using a generic delay as proof that every game finished saving;
- automatically choosing between divergent complete saves;
- retrying a non-idempotent commit without an expected-head guard.

## Desired outcome

```text
known failure
-> controlled stop
-> previous valid state preserved
-> recovery evidence preserved
-> clear action

unknown failure
-> controlled stop
-> incident id and local diagnostics
-> no guessed continuation
```

The product should fail visibly and recoverably, never silently and optimistically.