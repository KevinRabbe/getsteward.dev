# AR-1 Durable Recovery Start Guard

Status: **open AR-1 correctness gate**.

The current AR-1 implementation loads durable workspace-recovery records into `WorldLifecycleResponsibilityTracker` before unified startup and uses that responsibility to guard Quit/self-update behavior.

That is necessary but not sufficient.

## Required invariant

> **A durable unresolved workspace responsibility must block every new writable Start World / Host World lifecycle on that desktop until the responsibility is safely resolved.**

The rule must be enforced at the generic runtime/lifecycle boundary, not only by button state.

Reason:

- an application restart releases all in-memory `ManagedWritableSessionGate` leases;
- the durable `IWorkspaceRecoveryStore` may still contain `Active`, `RecoveryPending`, or `CleanupPending` evidence from the previous process;
- allowing a new writable lifecycle merely because the in-memory gate is empty would violate the first-release one-active/unresolved-responsibility-per-device rule;
- UI gating alone is insufficient because callers other than the current WPF buttons may invoke the lifecycle later.

## Required AR-1 completion behavior

Before a new `ContinueLocalAsync` or `ContinueAsHostAsync` transaction acquires the distributed/per-World writer reservation:

1. acquire the process-local device gate so concurrent starts still serialize;
2. inspect durable workspace recovery records;
3. when an unresolved record exists, reject the new writable lifecycle with a deterministic generic result/error;
4. do not call `IWorldSessionCoordinator.AcquireHostAsync`;
5. preserve the existing recovery evidence;
6. surface `Recovery needed` or `Action required` according to the durable record state;
7. allow the future dedicated recovery path to bypass/resolve this guard only after its own authority checks succeed.

Required tests:

- restart + `Active` record blocks Start/Host before coordinator acquisition;
- `RecoveryPending` blocks Start/Host;
- unresolved cleanup/recovery does not disappear because a new process has an empty in-memory gate;
- resolving/removing the durable responsibility permits the next writable lifecycle;
- two simultaneous starts remain serialized by `ManagedWritableSessionGate` even when the recovery store is empty.

Until this guard is integrated and the repository build/tests execute successfully, AR-1 is **not green**.
