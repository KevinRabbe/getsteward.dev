# Engineering Standards

## Purpose

Steward is a commercial product moving beyond prototype validation. Engineering decisions must protect user-owned game state, support additional adapters without rewriting Core, and keep the product boundary narrow.

Commercial quality means conservative state handling, recoverable failure, testable contracts, maintainable code, and dependable user-facing behavior. It does not mean adding speculative subsystems.

## Dependency direction

```text
SharedWorlds.Desktop / SharedWorlds.Cli / tools   (composition boundaries)
    |-- reference SharedWorlds.Core
    |-- reference selected infrastructure implementations
    `-- reference selected game adapter assemblies

individual game adapter -> SharedWorlds.Core
infrastructure          -> SharedWorlds.Core
Core                    -X-> concrete adapter
Core                    -X-> concrete storage backend
Core                    -X-> Steam / launcher / mod-platform SDK
```

`SharedWorlds.Core` owns universal product semantics and ports. Concrete integrations depend inward on those contracts.

Architecture tests must enforce dependency direction; documentation alone is insufficient.

## Project boundaries

- `SharedWorlds.Core`: World lifecycle, state/environment relationships, session/storage/recovery contracts, typed product failures.
- `SharedWorlds.Infrastructure`: replaceable storage, coordination, persistence, and recovery implementations.
- `SharedWorlds.GameAdapters/<Game>`: one independently compiled adapter per game.
- `SharedWorlds.Desktop`: commercial Windows UI, background runtime composition, and user-facing application boundary.
- `SharedWorlds.Cli`: development and diagnostic harness, not the final product model.
- `tools/*`: focused probes and validation utilities.
- `tests/*`: automated tests aligned with production boundaries.

The desktop may coordinate services but must not absorb Core or adapter responsibilities.

## Build policy

The repository uses:

- .NET 10 SDK policy through `global.json`;
- nullable reference types;
- warnings as errors;
- deterministic builds;
- build-time code-style enforcement;
- centralized NuGet package versions;
- repository-local NuGet source policy;
- explicit persisted-data schemas and migrations.

Do not disable a warning globally to hide a local defect. Fix the code or apply the narrowest justified suppression with an explanation.

## Dependency policy

Add external packages only when they remove meaningful implementation, security, or maintenance risk.

Before adding one, ask:

1. Is the capability already provided by .NET, Steam, the game, or an existing adapter dependency?
2. Is the package actively maintained and appropriately licensed for a commercial product?
3. Can it remain inside one adapter or infrastructure project instead of Core?
4. Can its version and update policy be controlled centrally?
5. Does it add runtime, security, or supply-chain surface disproportionate to its value?

Game-specific SDKs and parsers belong inside the relevant adapter assembly.

## Continuous integration

Integration-ready changes must pass independent repository validation, including as applicable:

- formatting verification;
- Release builds on supported CI platforms;
- automated tests;
- architecture-boundary tests;
- persistence compatibility tests;
- adapter tests that do not require real user saves;
- artifact/log retention sufficient to diagnose CI failure.

A developer-machine success is not sufficient evidence.

## Testing priority

1. Current-state and one-writer invariants.
2. Canonical commit ordering and stale-head rejection.
3. Storage integrity and compatibility.
4. Workspace recovery and cleanup ownership.
5. Architecture dependency boundaries.
6. Adapter discovery, parsing, preparation, and capture logic.
7. Background session/process observation.
8. Real-game acceptance tests in controlled disposable environments.
9. Two-device handoff tests.

Every fixed defect should gain a regression test where practical.

Automated tests must not require or mutate a developer's real saves or live game installation.

## Filesystem safety

User-owned game state is a trust boundary.

Rules:

- Never mutate the original source during import.
- Prefer adapter-owned isolated workspaces.
- Validate every path before destructive use.
- Never recursively delete an unverified user-provided path.
- Publish immutable revision metadata and payload only after both are complete.
- Never overwrite a published revision identity.
- Advance the current World head only after durable storage and verification.
- Preserve post-launch workspaces on uncertain failure.
- Delete captured packages only with explicit adapter cleanup authority.
- Treat backups, caches, temporary packages, canonical state, and user sources as different ownership classes.

## State transaction policy

A successful session may advance the current state exactly once.

```text
capture candidate
-> durable immutable storage
-> verification
-> expected-head check
-> atomic current-head advancement
```

A failed step leaves the previous valid state authoritative.

Identical state may return `Unchanged`. A stale writer must return `HeadChanged` or an equivalent controlled conflict instead of overwriting a newer state.

## Async, cancellation, and process behavior

Long-running I/O, network transfer, process observation, and game/server waits must propagate cancellation where contracts allow it.

Avoid sync-over-async and unbounded parallelism.

Cancellation after gameplay begins is not equivalent to safe completion. It must preserve recovery state rather than releasing the World as successfully committed.

Adapters own the real session lifecycle. Core must not assume one PID equals one session.

## Bounded resource policy

Queues, caches, histories, retries, workers, transfer concurrency, logs, and recovery retention must have explicit bounds.

Do not repeatedly copy, hash, serialize, tokenize, or log large World payloads in hot paths without demonstrated need.

Full-install hashing is reserved for explicit verification or investigation, not ordinary play.

## Failure semantics

Unknown state-handling failures stop the current operation.

Expected product failures use typed categories so the desktop can present recovery actions without parsing exception text.

A failed capture, upload, verification, or current-head update must never become user-visible success.

A cleanup failure after successful commit becomes cleanup debt, not rollback of the valid state.

## Persistence compatibility

Persisted metadata and future network messages are versioned product contracts.

- Prefer additive schema evolution.
- Use explicit outer document envelopes.
- Register safe migrations deliberately.
- Reject unknown future versions with typed compatibility errors.
- Never overwrite unknown data with defaults.
- Keep migration logic in stable persistence boundaries, not UI code.

## Logging, diagnostics, and privacy

The desktop and development tools own top-level diagnostics.

Never log:

- authentication tokens;
- private join credentials;
- arbitrary save contents;
- personal data unnecessary for diagnosis;
- complete environment paths when a safer redacted form is sufficient.

Diagnostics remain local unless a future explicit user-controlled upload workflow is added.

## Adapter rules

An adapter owns:

- installation and World discovery;
- environment inspection and preparation;
- import capture;
- state restore and capture;
- local/host/client launch behavior;
- process/server observation;
- safe shutdown and capture readiness;
- game-specific validation.

An adapter must not redefine one-writer semantics, durable commit order, recovery policy, social governance, branches, or generic merging.

## Documentation policy

Active documents must match the current product boundary.

When a direction is abandoned:

- remove it from architecture, domain, lifecycle, decisions, and roadmap documents;
- do not leave contradictory active plans for future readers to interpret;
- preserve only evidence that remains useful to current implementation or validation.

## Definition of done

A product change is complete only when applicable evidence shows:

- product and architecture boundaries remain intact;
- state-safety and recovery invariants are preserved;
- automated tests cover the changed behavior or fixed defect;
- architecture tests remain green;
- build, tests, and formatting succeed;
- real-game validation is performed when automation cannot prove the behavior;
- documentation is updated in the same change;
- no real user save was required or modified by automated tests;
- the change directly supports the World handoff product rather than expanding unrelated scope.