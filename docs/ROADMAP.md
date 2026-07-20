# Roadmap

## Guiding rule

Build a narrow implementation of the final architecture.

Do not expand to many games or infrastructure providers until one complete World lifecycle is reliable.

## Phase 1: Local Factorio vertical slice

Status: **end-to-end validated on a real Windows Steam installation**.

Validated flow:

```text
discover Factorio
-> discover save
-> import as LocalOnly World
-> create E1 + S1
-> Continue
-> prepare isolated workspace
-> restore canonical state
-> launch local Factorio
-> survive Steam process handoff
-> play and save inside isolated write-data
-> wait for real session end
-> capture new immutable state revision
-> persist revision
-> move canonical World head last
-> clean workspace/recovery record
-> Continue again
-> restore newly committed canonical state
```

Real-machine validation confirmed:

- non-default Steam library discovery
- save discovery with autosave filtering
- privacy-by-default import as `LocalOnly`
- source-save preservation during import
- Steam bootstrap/process-handoff tracking
- isolated Factorio `write-data` for product-managed play
- gameplay changes captured into SharedWorlds revision history
- original source save remains unchanged during isolated play
- canonical head advances only after durable state capture
- clean-session workspace cleanup
- no recovery record remains after successful completion
- next Continue restores the newly committed visible gameplay state

Phase 1 is complete for the tested Windows Steam configuration. Known platform-specific and custom-path edge cases remain adapter hardening work rather than blockers for the validated local lifecycle.

## Phase 1b: Explicit sharing and hosted Factorio validation

Status: **validated on the same real Windows Steam installation**.

Validated flow:

```text
LocalOnly
-> explicit share-world
-> Shared
-> host-factorio
-> prepare isolated workspace
-> restore canonical state
-> launch Factorio with --host
-> survive Steam process handoff
-> replacement Factorio process retains --host
-> Factorio owns active UDP endpoints
-> save and exit cleanly
-> capture new immutable state revision
-> advance canonical World head
-> clean workspace/recovery record
-> original imported source save remains unchanged
```

The hosted validation advanced World `newme` from state revision `e8e0c36761934d0ca4ded2c0b125a378` to `669e8b7b10cc4b8da23a35b8f2ebd343`, left no prepared-workspace recovery records, and preserved the original source save SHA-256 at `9110897489D5CD73573A62DD949CBBDE5E1194972660EB5651C8D1E4DB2A879B`.

This validates the current local host lifecycle and sharing gate. It does **not** yet validate multi-user Join, remote durable synchronization, invitations, cross-machine coordination, or host handoff.

## Phase 2: Factorio environment reproduction

Target:

- resolve actual write-data path robustly
- create per-World isolated mod/config environment where appropriate
- test Factorio native `--sync-mods` behavior
- decide safe exact-version synchronization policy
- validate mod startup settings handling
- add Verify/Repair path for environment mismatches

Do not add automatic canonical mod synchronization until destructive or surprising behavior has been tested on disposable environments.

## Phase 3: Local product UX

Status: **initial CLI productization validated on the target Windows machine**.

Implemented and real-machine validated:

- `worlds` managed-World listing
- `world <selector>` details view
- World selectors by unique name, full ID, or unique ID prefix
- visible game, sharing mode, current environment revision, and current state revision
- visible members and revision metadata
- context-sensitive available actions
- `LocalOnly` vs `Shared` status surfaced explicitly

The first desktop flow should still provide:

- installed supported games
- discovered/importable saves
- Worlds library
- Continue
- World details
- sharing status
- environment status
- revision history
- Restore

Main UI should show only supported games that are actually installed. A separate Supported Games view may show supported but uninstalled titles.

## Phase 4: Sandbox, Fresh Test World, Fork, Restore

Implement the local branching/recovery product model before shared networking.

### Sandbox

Disposable copy that never writes to canonical history.

### Fresh Test World

Same environment, new game state.

### Fork

Permanent independent history derived from an existing revision.

### Restore

Explicitly move canonical head to a previous known-good state while preserving history.

## Phase 5: Second adapter — 7 Days to Die

Primary purpose: prove environment isolation for messy external mod setups.

Targets:

- installation discovery
- save/world discovery
- exact relevant environment manifest
- isolated mod/config profiles
- avoid requiring many duplicated full game folders where possible
- host/client launch
- session-end capture

This phase should reveal whether the generic environment contract is sufficient without changing Core semantics.

## Phase 6: Third adapter — Project Zomboid

Primary purpose: prove a Workshop-heavy adapter.

Targets:

- installation discovery
- save/server-state discovery
- Workshop environment representation
- configuration capture
- host/client flow
- state capture

## Phase 7: Remote durable World storage

Add a remote `IWorldStorage` implementation.

Steam-backed storage is a candidate, but the exact UGC/Workshop object model must be tested with multiple accounts before becoming canonical.

Preferred conceptual model:

- immutable revisions
- explicit canonical head selection
- avoid multiple users destructively overwriting one shared object when possible

Local storage remains useful for cache, offline access, recovery, and testing.

## Phase 8: Shared membership and live coordination

Add:

- World membership
- invitations
- live World availability
- canonical host acquisition
- Join instead of conflicting host launch
- host handoff request/accept flow

A Steam lobby may implement `IWorldSessionCoordinator`, but the Core remains platform-neutral.

## Phase 9: Host handoff

Implement the controlled restart flow:

```text
request
-> accept
-> save/close old host
-> stable capture
-> canonical commit
-> synchronize new host
-> prepare environment
-> launch new host
-> clients rejoin
```

Add explicit failure and recovery handling for each transition.

## Phase 10: Recovery hardening

Add:

- pre-session snapshots where useful
- save stabilization checks
- crash detection
- `RecoveryPending`
- local recovery candidate preservation
- controlled promotion of a recovered state
- corruption detection
- targeted Verify/Repair

Never overwrite a known-good canonical revision merely because a newer local file exists.

## Phase 11: Performance and transport optimization

Only after correctness:

- caching
- delta/deduplicated state transfer if worthwhile
- direct P2P transfer for speed
- background prefetching
- storage compaction/retention policies

Durable storage and direct transfer can coexist: durable backend for history, P2P for fast current-state transfer.

## Initial release boundary

A credible first release should prioritize depth over game count:

- 2–3 excellent adapters
- shared Worlds
- save synchronization
- supported environment/mod synchronization
- Continue / Join
- automatic clean-session commit
- host handoff
- Sandbox
- Fresh Test World
- Fork
- basic snapshot Restore

Postpone until the foundation is proven:

- large game catalog
- every mod platform
- advanced P2P networking
- dedicated-server fleet orchestration
- public adapter marketplace
- cross-platform support that materially delays a reliable Windows release

## Current immediate next step

The local Continue path, replay path, World listing/details UX, explicit sharing gate, and hosted Factorio lifecycle are now validated on the target Windows Steam installation.

Next:

1. harden Factorio environment reproduction, especially per-World mod/config isolation and exact-version verification
2. correct user-facing sharing copy so it does not imply Join is implemented before remote synchronization/live coordination exists
3. add revision-history/Restore capabilities for local World safety and debugging
4. design and validate the remote durable storage model needed to move canonical World state between machines
5. add live shared coordination and only then expose genuine multi-user Join and host handoff

The principle remains: prove each product boundary with a real game before adding another abstraction layer.
