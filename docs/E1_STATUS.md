# E1 Current Status

This file is the authoritative current milestone-status summary for E1 while implementation is moving quickly. Detailed checkpoint documents remain evidence, but their earlier status headline must not be read as newer than this file.

## BE-1 — Provider-free backend contract simulation

Status: **contract-matrix implementation complete; build/test confirmation pending**.

Implemented:

- deterministic shared World authority;
- one-writer reservation/generation;
- heartbeat -> Uncertain -> reconnect/reclaim;
- immutable candidate publication and integrity checks;
- resumable transfer simulation;
- idempotent acquire/finalize/commit/reclaim/last-safe recovery;
- authoritative operation-result lookup;
- failure injection before commit and after durable success/before response;
- BE-D009 candidate retention policy;
- BE-D010 canonical/pinned retention policy;
- deterministic PC A -> PC B -> PC A handoff tests.

Static-audit hardening completed after the first BE-1 matrix pass:

- candidate retention activity clocks are monotonic so stale retries cannot shorten cleanup grace;
- initial candidate retention tracking is create-once/idempotent and conflicting overwrite attempts are rejected;
- canonical retention rejects reuse of one immutable state revision with contradictory environment metadata;
- multipart transfers reject bytes immediately when a new unique part would exceed the declared package size;
- duplicate part replay does not consume byte budget twice;
- transfer-scoped operations hide existing-vs-missing transfer IDs from other World members;
- transfer identifiers are caller-scoped internally, preventing `StartTransfer` probing and cross-member transfer-ID collisions.

BE-1 is not green until the repository tests actually run successfully under warnings-as-errors/nullability rules.

## AR-1 — Generic runtime/conformance

Status: **implementation requirements complete; build/test confirmation pending**.

Implemented:

- Start/Host share one generic lifecycle;
- temporary Host does not require persistent sharing;
- device-wide in-memory writable lifecycle gate;
- durable recovery-start guard before distributed/per-World coordinator acquisition;
- `Active`, `RecoveryPending`, and `CleanupPending` recovery evidence blocks every new writable Start/Host after restart;
- unreadable recovery metadata fails closed;
- generic Join capability result including guided manual fallback;
- optional structured adapter session-evidence contract;
- generic lifecycle phase observer;
- deterministic happy/failure/cleanup/recovery-start transition tests;
- terminal `Completed` semantics occur only after successful lifecycle finalization and reservation release;
- reservation-release failure remains unresolved responsibility rather than false completion;
- runtime responsibility tracker;
- tray/close-to-tray/guarded-Quit integration;
- recovery-first unified desktop startup;
- unified Host action no longer depends on Shared;
- desktop no longer fakes persistent sharing by flipping a local enum.

Static-audit hardening:

- reservation acquisition is guarded while in flight;
- `IWorldSessionCoordinator.AcquireHostAsync` now requires ambiguous remote outcomes to be resolved internally before throwing;
- a proven failed acquisition resolves `AcquiringReservation -> Completed` and never calls release for authority that was not acquired;
- lifecycle observers are explicitly non-authoritative projections;
- desktop presentation/tray failures cannot escape into capture, commit, reservation release, or recovery behavior.

AR-1 is not green until the repository compiles and the tests execute successfully under warnings-as-errors/nullability rules. No known AR-1 product-contract correctness gate remains open before that evidence.

## UI-1 — Shell/navigation replacement

Status: **implementation requirements complete; Windows build/test confirmation pending**.

Implemented:

- persistent tray/background lifetime and close-to-tray behavior;
- guarded Quit from runtime responsibility;
- recovery loaded before unified game/World startup;
- approved Start World / Host World / Share World terminology and Host-vs-Share semantics;
- truthful `Only on this PC` / `Shared` presentation with no fake local Shared transition;
- Steward product shell/branding and collapsed technical revision details;
- responsive wide master-detail and narrow focused World view with `Back to Worlds`;
- old Factorio-only desktop execution path and legacy XAML action handlers removed;
- one authoritative game-first Import workspace; duplicate compact Import pipeline/anchors removed;
- runtime responsibility surfaced on selected World and used to guard writable actions;
- Games-level runtime attention indicator for Preparing, Running, Saving World, Recovery needed, and Action required;
- direct WPF template construction retained instead of unnecessary helper abstractions.

Static-audit hardening:

- unified Games initialization now returns/awaits `Task` instead of racing later UI layers through `async void`;
- startup ordering is deterministic: recovery -> settings -> initial Worlds -> game navigation/import -> responsibility -> responsive layout;
- WinForms tray projection remains isolated from authoritative lifecycle execution.

UI-1 is not green until the Windows desktop project compiles and repository tests execute successfully under warnings-as-errors/nullability rules. Real Share World / Manage access remains intentionally deferred to BE-2/UI-4 because `Shared` must mean real backend authority exists.

## E1 aggregate status

**All three E1 implementation slices are complete against their frozen contracts and have received an additional static safety audit. E1 remains execution-evidence blocked, not implementation blocked.**

The available GitHub connector does not expose push-run listing or workflow dispatch for this branch, combined commit status currently exposes no checks, the local execution environment does not contain the .NET SDK, and no trustworthy current build/test result has therefore been obtained yet.

Historical note only: an older PR run on this branch succeeded before the E1 batch, proving the workflow itself was functional at that time. It is not evidence for the current E1 head.

## E1 rule

Do not start BE-2, AR-2/AR-3 game-specific hardening, or later release work merely to avoid the unresolved build/test gate. The next allowed step is to obtain real restore/format/build/test evidence for the current branch and fix any failures before E2 begins.
