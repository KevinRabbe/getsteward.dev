# Roadmap

## Guiding rule

Build a narrow implementation of the final architecture.

Do not expand to many games or infrastructure providers until one complete World lifecycle is reliable.

## Phase 1: Local Factorio vertical slice

Status: **implemented in code, not yet real-machine validated**.

Target flow:

```text
discover Factorio
-> discover save
-> import as World
-> create E1 + S1
-> Continue
-> prepare workspace
-> restore S1
-> launch host
-> wait for session end
-> capture S2
-> persist S2
-> move canonical World head to S2
```

Remaining work:

- compile on a machine with the .NET SDK
- fix any compiler errors
- run on Windows with Factorio installed
- verify installation discovery
- verify save discovery
- verify original source save remains untouched
- verify revision persistence
- verify host launch and session-end capture
- verify second Continue loads the newly committed state

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

Replace development CLI-only interaction with the first usable desktop flow.

Target screens/actions:

- installed supported games
- discovered/importable saves
- Worlds library
- Continue
- World details
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

Run the Factorio vertical slice on the user's actual Windows PC.

Reality test before more architecture.
