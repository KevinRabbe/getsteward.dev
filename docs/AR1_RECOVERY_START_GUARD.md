# AR-1 Durable Recovery Start Guard

Status: **implemented; build/test confirmation pending**.

The runtime now enforces durable workspace responsibility before every new writable `Start World` / `Host World` lifecycle.

## Enforced invariant

> **A durable unresolved workspace responsibility blocks every new writable Start World / Host World lifecycle on that desktop until the responsibility is safely resolved.**

The rule is enforced inside `WorldLifecycleService`, not only by WPF button state.

## Implemented ordering

Before a new `ContinueLocalAsync` or `ContinueAsHostAsync` transaction acquires the distributed/per-World writer reservation:

1. the process-local `ManagedWritableSessionGate` is acquired;
2. `IWorkspaceRecoveryStore` is inspected;
3. `Active`, `RecoveryPending`, and `CleanupPending` records are treated as unresolved responsibility;
4. a blocking record prevents `IWorldSessionCoordinator.AcquireHostAsync` from being called;
5. the existing recovery record remains untouched;
6. `Active` / `RecoveryPending` surface `RecoveryNeeded` through the lifecycle observer;
7. `CleanupPending` surfaces `CleanupPending` / Action required;
8. an unreadable recovery store fails closed rather than being interpreted as an empty recovery store.

The blocking record is selected deterministically when corrupt/legacy state contains more than one unresolved record. Normal first-release behavior should still produce at most one unresolved writable responsibility per device.

## Regression coverage

`WorldLifecycleRecoveryStartGuardTests` covers:

- restart-style `Active` record blocks Start before coordinator acquisition;
- `Active` record blocks Host before coordinator acquisition;
- `RecoveryPending` blocks Start and Host;
- `CleanupPending` blocks Start and Host and maps to the attention phase;
- blocking evidence is preserved;
- removing/resolving the durable record allows the next writable lifecycle to reach coordination;
- recovery-store read failure fails closed before coordination.

Device-wide serialization itself remains covered independently by `ManagedWritableSessionGateTests` and `WorldLifecycleDeviceConcurrencyTests`.

## Remaining gate

The implementation is not declared green until the repository compiles and its tests execute successfully under the repository's warnings-as-errors/nullability configuration.
