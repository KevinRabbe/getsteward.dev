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

BE-1 is not green until the repository tests actually run successfully under warnings-as-errors/nullability rules.

## AR-1 — Generic runtime/conformance

Status: **in progress; one durable-recovery start guard plus build/test confirmation remain**.

Implemented:

- Start/Host share one generic lifecycle;
- temporary Host does not require persistent sharing;
- device-wide in-memory writable lifecycle gate;
- generic Join capability result including guided manual fallback;
- optional structured adapter session-evidence contract;
- generic lifecycle phase observer;
- deterministic happy/failure/cleanup transition tests;
- terminal `Completed` semantics moved behind successful lifecycle finalization and reservation release;
- runtime responsibility tracker;
- tray/close-to-tray/guarded-Quit integration;
- recovery-first unified desktop startup;
- unified Host action no longer depends on Shared;
- desktop no longer fakes persistent sharing by flipping a local enum.

Open correctness gate:

- durable `Active` / `RecoveryPending` / unresolved recovery evidence must hard-block every new Start/Host at the generic lifecycle boundary after restart, before coordinator acquisition. See `AR1_RECOVERY_START_GUARD.md`.

AR-1 is not green until that guard is implemented and tests/build execute successfully.

## UI-1 — Shell/navigation replacement

Status: **in progress**.

Implemented so far:

- persistent tray/background lifetime;
- close window -> hide, not quit;
- guarded Quit from runtime responsibility;
- recovery loaded before unified game/World startup;
- Host-vs-Share semantics corrected in unified and legacy action paths;
- fake local `Shared` transition removed from the active unified Share action.

Still open:

- remove/quarantine obsolete legacy Factorio-only desktop composition;
- finish intended global shell/navigation and responsive workspace cleanup;
- surface recovery/attention state directly in unified World presentation;
- real Share World / Manage access implementation waits for BE-2/UI-4 shared authority.

## E1 rule

Do not start BE-2, AR-2/AR-3 game-specific hardening, or later release work merely to avoid the remaining E1 correctness/build gates.
