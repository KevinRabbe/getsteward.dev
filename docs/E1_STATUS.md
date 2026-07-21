# E1 Status

E1 is **COMPLETE and GREEN**.

Canonical validation head: `fc22978ed46e5158e3cabdd06cd991f0d6c47c93`

GitHub Actions CI run `600` completed successfully on that exact implementation head:

- Quality: restore + `dotnet format SharedWorlds.sln --verify-no-changes --no-restore` — **passed**;
- Ubuntu Release build — **passed**;
- Ubuntu full test run — **passed**;
- Windows Release build — **passed**;
- Windows full test run — **passed**.

The temporary formatter-diagnostic workflow was removed before the final validation run; the normal CI policy is restored.

## BE-1 — Provider-free backend contract simulation

Status: **complete and validated**.

Implemented and tested:

- deterministic shared World authority;
- one-writer reservation/generation;
- heartbeat -> Uncertain -> reconnect/reclaim;
- immutable candidate publication and independent size/hash verification;
- resumable multipart transfer simulation;
- idempotent acquire/finalize/commit/reclaim/last-safe recovery;
- authoritative operation-result lookup;
- failure injection before commit and after durable success/before response;
- BE-D009 candidate-retention policy;
- BE-D010 canonical/pinned retention policy;
- deterministic PC A -> PC B -> PC A handoff;
- monotonic candidate-retention activity clocks;
- create-once/idempotent candidate tracking with conflicting reuse rejection;
- immutable state/environment retention mapping validation;
- immediate rejection of multipart bytes exceeding the declared package size;
- duplicate multipart replay without double-consuming byte budget;
- transfer existence privacy through caller-scoped transfer identifiers;
- World-scoped revision identifiers preventing cross-World revision collision/probing.

## AR-1 — Generic runtime/conformance

Status: **complete and validated**.

Implemented and tested:

- Start/Host share one generic lifecycle;
- temporary Host does not require persistent sharing;
- device-wide writable lifecycle gate;
- durable recovery-start guard before distributed/per-World coordinator acquisition;
- `Active`, `RecoveryPending`, and `CleanupPending` recovery evidence blocks new writable Start/Host after restart;
- unreadable recovery metadata fails closed;
- generic Join capability result including guided manual fallback;
- optional structured adapter session-evidence contract;
- generic lifecycle phase observer;
- deterministic happy/failure/cleanup/recovery transition tests;
- terminal `Completed` only after successful lifecycle finalization and reservation release;
- reservation-release failure remains unresolved responsibility;
- reservation acquisition is guarded while in flight;
- `AcquireHostAsync` contract requires ambiguous remote outcomes to be resolved before throwing;
- proven failed acquisition resolves `AcquiringReservation -> Completed` without releasing authority that was never acquired;
- lifecycle observers are non-authoritative projections;
- presentation/tray failures cannot alter capture, commit, reservation release, or recovery behavior;
- runtime responsibility tracker and recovery-first desktop startup.

## UI-1 — Shell/navigation replacement

Status: **complete and validated**.

Implemented and validated by the Windows build/test job:

- persistent tray/background lifetime and close-to-tray behavior;
- guarded Quit from runtime responsibility;
- approved Start World / Host World / Share World terminology;
- Host-vs-Share independence;
- truthful `Only on this PC` / `Shared` semantics with no fake Shared transition;
- responsive wide master-detail and narrow focused World view with `Back to Worlds`;
- collapsed technical revision details;
- obsolete Factorio-only desktop execution path and legacy XAML action handlers removed;
- one authoritative game-first Import workspace;
- runtime responsibility shown on selected World and used to guard writable actions;
- Games-level attention for Preparing, Running, Saving World, Recovery needed, and Action required;
- deterministic awaited unified startup rather than `async void` startup races;
- WinForms tray namespaces isolated from WPF;
- environment Verify/Repair routed through the selected adapter rather than a Factorio-only desktop helper.

Real Share World / Manage access remains intentionally deferred to BE-2/UI-4 because `Shared` must only mean real backend authority exists.

## E1 exit

All E1 implementation and execution-evidence gates are satisfied.

The next active phase is **E2: Shared state foundation**. E2 must replace simulation-only shared persistence boundaries incrementally without weakening the BE-1 invariants that are now executable and green.
