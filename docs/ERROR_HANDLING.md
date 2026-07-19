# Error Handling and Failure Boundaries

SharedWorlds handles user-owned game state. Error handling therefore favors preserving known-good state over trying to continue after an unknown failure.

## Core rule

Do not catch an exception merely to keep the application moving.

A catch block must do at least one meaningful thing:

- translate a lower-level failure into a stable typed product failure
- preserve or update recovery state
- release a lease or other owned resource
- add diagnostics at an application boundary
- deliberately suppress only a secondary cleanup failure when a primary failure is already being propagated

Unknown failures stop the current operation.

## Failure layers

### Core and domain

Core exposes typed product failures for conditions callers can reason about:

- `WorldNotFoundException`
- `RevisionNotFoundException`
- `WorldIntegrityException`
- `AdapterMismatchException`
- `PersistedDataCompatibilityException`
- `WorldSessionConflictException`

Core does not convert all exceptions into success or generic result values. A storage or adapter failure may propagate when the caller must stop rather than guess.

### Lifecycle cleanup

Lifecycle cleanup is conservative:

- the canonical World head advances only after durable revision publication
- host ownership is released in `finally`
- a primary lifecycle exception is not replaced by a secondary host-release failure
- adapter-owned temporary capture packages are deleted only when the adapter explicitly grants cleanup authority
- a post-launch failure preserves the prepared workspace as a recovery candidate
- failed workspace cleanup becomes `CleanupPending` rather than being silently forgotten

### Application boundary

The CLI is currently the application composition root and owns the top-level exception boundary.

Expected typed product failures:

- receive a concise user-facing message
- receive a recovery-oriented next step
- do not print a raw stack trace
- return a non-zero product-failure exit code

Operational filesystem/data failures:

- stop the operation
- receive a concise user-facing explanation
- write a local diagnostic entry when possible
- return a distinct non-zero exit code

Unexpected failures:

- stop the operation immediately
- receive a generated incident ID
- write the full exception locally when possible
- never continue in an unknown state

## Cancellation

The CLI converts Ctrl+C into cancellation rather than immediate process termination.

Cancellation tokens propagate through discovery, import, preparation, session observation, capture, storage, and recovery operations where supported.

If cancellation occurs after gameplay has started, normal lifecycle failure handling can preserve the prepared workspace for recovery instead of pretending the session completed cleanly.

`OperationCanceledException` is treated as cancellation at the application boundary rather than an unexpected defect.

## Exit codes

The development CLI uses stable categories:

```text
0   success
2   command/usage error
10  controlled product/domain failure
20  filesystem, storage, or persisted-data operational failure
70  unexpected application failure
130 user cancellation
```

A future desktop UI will map the same failure categories into UI states and recovery actions rather than process exit codes.

## Diagnostics

Operational and unexpected exceptions are written under the local SharedWorlds log directory when possible.

A diagnostic entry contains:

- UTC timestamp
- generated incident ID
- process ID
- OS/runtime information
- full exception details

Diagnostics are local. They are not automatically uploaded.

Future telemetry or crash-upload functionality must be opt-in and must never include authentication tokens, private join tokens, arbitrary save contents, or other secrets.

## Do not do this

Avoid patterns such as:

```text
try
  risky operation
catch
  ignore
continue as if successful
```

Also avoid broad retries for destructive or state-changing operations. A retry must be explicitly safe and idempotent.

## Design intent

The desired behavior is:

```text
known failure
-> controlled stop
-> useful message
-> preserved canonical state
-> recovery path when possible

unknown failure
-> controlled stop
-> incident ID + local diagnostics
-> no guessed continuation
```

The product should fail visibly and recoverably, not silently and optimistically.
