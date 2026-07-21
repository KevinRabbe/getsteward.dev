# Roadmap

## Product target

> **One shared World. Different Steam players. Different times. No always-on game server.**

The roadmap is organized around proving that product effect reliably. It does not include Git-style branching, generic save merging, ownership hierarchies, social-platform features, or permanent game-server infrastructure.

## Delivery principles

1. Complete the full World handoff, not only launch.
2. Preserve the last valid state on every uncertain failure.
3. Prove boundaries with real games and real devices.
4. Keep game-specific behavior inside adapters.
5. Generalize only after Factorio, Palworld, or later adapters demonstrate the same pattern.
6. Prefer Steam and game-native infrastructure over new Steward subsystems.
7. Treat commercial reliability as mandatory without expanding scope unnecessarily.

## Foundation status

### Generic lifecycle

Implemented foundation:

```text
discover
-> import
-> inspect environment
-> prepare workspace
-> restore state
-> launch local or hosted session
-> observe session end through adapter
-> capture updated state
-> store immutable revision
-> advance current World head last
-> preserve recovery state on failure
```

### State safety

Implemented and tested foundations include:

- immutable environment and state revisions;
- one active writable session boundary;
- current-head advancement after durable state storage;
- expected-head protection against stale commits;
- unchanged-state detection;
- workspace recovery records;
- conservative cleanup ownership;
- typed failure boundaries;
- persistence schema envelopes and migration rules.

### Factorio

Factorio has provided real-machine validation for:

- Steam/non-default-library discovery;
- save discovery and import;
- isolated preparation;
- local play;
- hosted launch paths;
- Steam process handoff observation;
- environment/mod handling;
- capture and replay of updated state.

Remaining Factorio work is adapter hardening and commercial UX integration, not creation of a separate product architecture.

### Palworld

Palworld has proven:

- client and dedicated-server discovery;
- local World discovery;
- unchanged World migration into the dedicated-server save location;
- dedicated server selection and launch;
- process/server observation;
- capture into a portable package;
- exclusion of server backup noise;
- canonical restore through staging and rollback;
- byte verification after restore;
- launch from the restored canonical state;
- canonical state commit through the transaction boundary.

Player identity migration between local co-op host and dedicated-server identities remains a game-specific edge case and is not part of the generic product kernel.

### Desktop

The desktop currently proves:

- one registered game-adapter pipeline;
- Factorio and Palworld discovery;
- game-first World browsing;
- unified import;
- adapter-driven lifecycle actions;
- Steam/game artwork resolution.

The current WPF layer still contains transitional runtime composition over older UI code. It should be simplified after the product contract is stable, not expanded with obsolete social or ownership workflows.

## Milestone 1: Documentation and product-boundary consistency

Status: **active**.

Required outcome:

- all active documents use the same product definition;
- Fork, branching, merge, ownership-governance, party, and public-discovery plans are removed from active architecture and roadmap documents;
- non-negotiable rules remain the highest product authority;
- implementation status and future work are clearly separated;
- obsolete documents are deleted instead of left as contradictory alternatives.

## Milestone 2: Shared durable World state

Add a remote/shared `IWorldStorage` implementation capable of moving the latest valid state between trusted Steam users or devices.

Required properties:

- immutable state publication;
- explicit current-head metadata;
- integrity verification;
- resumable/retryable transfers;
- expected-head commit protection;
- local caching;
- conservative failure behavior;
- state sizes tested with Factorio and Palworld Worlds.

The exact Steam storage mechanism must be tested before becoming permanent architecture.

## Milestone 3: Distributed one-writer coordination

Add a shared `IWorldSessionCoordinator` implementation.

It must support only the operational facts required by the product:

- World Ready or currently in use;
- one session reservation;
- starting state revision;
- active device/Steam identity;
- local or hosted session mode where relevant;
- safe release after commit;
- conservative expiry and recovery after crashes.

It must not become a party, ownership, role, or governance system.

## Milestone 4: Two-device handoff proof

This is the decisive product milestone.

```text
PC A imports or opens World at state N
-> PC A plays and commits N+1
-> PC B retrieves N+1
-> PC B plays and commits N+2
-> PC A retrieves N+2
```

While PC B holds the writable session reservation:

```text
PC A attempts another writable start
-> Steward rejects or waits
```

The proof must be completed for at least one game before broader product claims. Repeating it with both Factorio and Palworld validates the adapter boundary more strongly.

## Milestone 5: Background runtime hardening

Steward should remain mostly out of the user's way while completing essential work.

Required behavior:

- start with the desktop application;
- remain active during the game/server session;
- observe adapter-defined process/server lifecycle;
- expose clear Ready, Preparing, Running, Saving, and Recovery-needed states;
- survive UI minimization;
- prevent accidental application exit while a writable session still requires capture;
- resume recovery handling after application or OS interruption;
- notify the user only when action is required.

Background-first must never become launcher-only.

## Milestone 6: Simple commercial World UX

The normal user flow should remain small:

```text
Games
-> select game
-> select World
-> Start World or Host World
-> play
```

Required product surfaces:

- Games Library;
- one game-specific Worlds workspace;
- World status and primary action;
- import workspace;
- clear progress during preparation and saving;
- recovery action when needed;
- compact settings and diagnostics.

Do not expose revision hashes, storage packages, save paths, server folders, or adapter internals in the primary workflow.

## Milestone 7: Steam product integration

Use Steam for the facilities it already owns:

- stable user identity;
- game ownership/install detection;
- launching;
- friends/invitations where needed;
- native multiplayer joining;
- Workshop and dedicated-server tooling;
- Steward distribution and updates.

Steward should add only the smallest integration needed for shared World state and one-writer coordination.

## Milestone 8: Factorio and Palworld release hardening

For each initial adapter:

- repeatable clean import;
- environment preparation and mismatch reporting;
- reliable local launch;
- reliable hosted launch where supported;
- correct process/server observation;
- graceful shutdown;
- safe capture;
- restore verification;
- recovery after interrupted capture/store;
- two-device handoff;
- clear limitations for unresolved game-specific edge cases.

A supported game must complete the lifecycle reliably. A partially working large catalog is not a release advantage.

## Milestone 9: Performance optimization after correctness

Optimize only after the two-device handoff is reliable:

- content-addressed deduplication;
- resumable chunk transfer;
- compression tuning;
- direct peer-to-peer acceleration;
- background prefetching;
- cache limits and cleanup;
- bounded retries and transfer queues.

Performance work must not weaken durable commit, verification, or recovery guarantees.

## Initial commercial release boundary

A credible first release should provide:

- Windows desktop product;
- Steam identity/platform integration;
- Factorio and Palworld as reliable initial adapters;
- import of existing Worlds;
- local start and temporary hosting;
- full background session observation;
- automatic safe capture at session end;
- shared durable latest state;
- distributed one-writer protection;
- cross-device continuation;
- recovery from interrupted handoffs;
- clear environment mismatch handling;
- concise game-first UI.

Not required for the first release:

- generic save merging;
- Fork/branch workflows;
- parties, chat, public discovery, likes, or community feeds;
- complex ownership or role systems;
- permanent hosted game-server fleets;
- live host migration;
- universal support for every mod ecosystem;
- a large game catalog.

## Immediate next sequence

1. Finish reconciling active documentation with the current product boundary.
2. Define the smallest shared-storage contract needed by the existing lifecycle.
3. Define the smallest distributed session-reservation contract.
4. Implement and test PC A -> PC B -> PC A handoff with one real World.
5. Repeat against the other initial adapter.
6. Harden the background runtime and simplify the desktop around Start World / Host World.
7. Optimize transport only after correctness is proven.