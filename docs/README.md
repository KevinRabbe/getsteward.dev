# Steward Documentation

Steward is a commercial Windows desktop product with its generic platform code/CI boundary complete and its V2 Friends Build now the active product goal.

This directory is the canonical home for active product, architecture, engineering, lifecycle, adapter, storage, recovery, and planning documentation.

## Current project mode

> **V2 Friends Build is the active execution goal. The generic Steward platform and broad adapter architecture are already proven. Normal implementation work now optimizes the real friend journey: private download/run, non-Steam Friends Build identity, visible World membership/lobby, Host/Join, safe cross-device handoff, and recovery for Factorio, Palworld, 7 Days to Die, and Valheim. Do not add unrelated adapters merely to increase game count. Production Steam publication/authentication and final release acceptance remain later gates and must not be weakened by the private V2 path.**

Start with the [V2 Friends Build](V2_FRIENDS_BUILD.md) for the active product goal and execution rule. Use the [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) for the qualified technical checkpoint and stable extension boundary. Use the [Master Roadmap](ROADMAP.md) and workstream roadmaps for detailed product contracts and historical implementation sequencing:

1. [UI and UX Roadmap](UI_ROADMAP.md)
2. [Backend Roadmap](BACKEND_ROADMAP.md)
3. [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md)

The roadmaps are separate for clarity but define one product. Their states, actions, failure semantics, and acceptance criteria must continue to agree. Older milestone/checkpoint wording is historical where the V2 Friends Build or Platform Implementation Status explicitly supersedes it.

Current completion evidence and execution direction:

- [V2 Friends Build](V2_FRIENDS_BUILD.md) — active product goal, private distribution/authentication boundary, minimal World lobby, four primary games, real friend handoff acceptance, and the rule that adapter count no longer defines progress;
- [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) — current all-workflows-green platform checkpoint, stable extension boundary, historical adapter-addition workflow, and remaining empirical release gates;
- [BE-2 Status](BE2_STATUS.md) — authenticated World/access/revision metadata with durable PostgreSQL persistence;
- [BE-3 S3 Checkpoint](BE3_S3_CHECKPOINT.md) — immutable transfer, S3-compatible protocol proof, cleanup/retention, and desktop verified caching/materialization;
- [BE-4 Status](BE4_STATUS.md) — durable one-writer reservation/generation authority, canonical commit, reclaim, late-writer rejection, and transactional idempotency;
- [BE-5 Status](BE5_STATUS.md) — real PostgreSQL-backed PC A -> PC B -> PC A handoff, adverse authority/reclaim proof, and deterministic Waiting-to-sync recovery after a lost successful commit response;
- [E4 Windows Desktop Status](E4_DESKTOP_STATUS.md) — production Desktop remote composition, stable installation identity, Steam Web API ticket bootstrap code, merged local/shared World routing, exact-environment gate, deterministic pending/cleanup/interrupted recovery, and Share/access surfaces;
- [E4 Live Acceptance Deployment](E4_LIVE_ACCEPTANCE_DEPLOYMENT.md) — provider-neutral container/PostgreSQL/S3 deployment contract and the disposable live-acceptance shape;
- [Steam Release Gate](STEAM_RELEASE_GATE.md) — explicit decision to introduce the real Steward AppID/publisher credentials only after the rest of the product is good enough to publish, followed by genuine final Steam acceptance with no production auth bypass.

## Documentation authority

When documents disagree, use this order:

1. [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md)
2. [Product Boundary](PRODUCT_BOUNDARY.md)
3. [Design Decisions](DECISIONS.md)
4. [Architecture](ARCHITECTURE.md)
5. [V2 Friends Build](V2_FRIENDS_BUILD.md) for the active execution goal and V2 scope
6. [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) for the current qualified implementation/checkpoint boundary
7. Detailed subsystem and adapter documents
8. [Master Roadmap](ROADMAP.md) and its workstream roadmaps

The first two documents define what Steward is and what it must not become. Lower-level documents may add detail but may not silently expand or contradict the product boundary.

Active documentation must remain internally consistent. Obsolete product concepts should be removed rather than preserved as parallel plans. Git-style branching, generic save merging, ownership hierarchies, social-platform features, and permanent game-server infrastructure are not active Steward product directions.

## Product definition

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steam is the intended commercial platform. Games are adapters. Worlds are the product. V2 temporarily uses private distribution/authentication only to prove the product with trusted friends before public Steam release acceptance.

Steward moves the latest valid World state into a playable session and returns the updated valid state for the next player.

## Product and architecture

- [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md) — constraints product and implementation work must not accidentally violate.
- [Product Boundary](PRODUCT_BOUNDARY.md) — commercial product definition, essential lifecycle, Steam boundary, adapter boundary, background-first behavior, and explicit non-goals.
- [Design Decisions](DECISIONS.md) — current durable architectural decisions and their reasons.
- [Architecture](ARCHITECTURE.md) — dependency direction, adapters, storage, session coordination, revisions, and background runtime boundaries.
- [Domain Model](DOMAIN_MODEL.md) — active persisted and runtime concepts.
- [World Lifecycle](WORLD_LIFECYCLE.md) — import, preparation, launch, session observation, capture, commit, handoff, and recovery.

## Planning and execution

- [V2 Friends Build](V2_FRIENDS_BUILD.md) — active execution target: private clean-PC distribution, Friends Build identity, minimal World lobby, Factorio/Palworld/7DTD/Valheim depth, real friend handoff, and observed recovery.
- [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) — qualified code/CI boundary and stable adapter/platform extension rules; its former adapter-expansion default is superseded by V2.
- [Master Roadmap](ROADMAP.md) — workstream dependencies, drift-control rules, implementation order, and release boundary.
- [UI and UX Roadmap](UI_ROADMAP.md) — navigation, user journeys, state/action contract, tray/background experience, recovery UX, milestones, and unresolved UI decisions.
- [UI-0 Sign-off Checklist](UI0_SIGNOFF_CHECKLIST.md) — approved flat sharing flow, tray behavior, terminology, and UI planning checks.
- [Backend Roadmap](BACKEND_ROADMAP.md) — authentication, minimal access, immutable transfer, current-head commit, distributed reservation, offline behavior, security, operations, and backend milestones.
- [BE-1 Local Contract Simulation](BE1_LOCAL_SIMULATION.md) — provider-free deterministic reservation, transfer, head-commit, retry, and recovery proof.
- [Backend Provider Evaluation](BE_PROVIDER_EVALUATION.md) — EU residency, security, transfer, restore, workload, cost criteria, and evidence required before final provider selection.
- [Backend API Contract](BE_API_CONTRACT.md) — versioned HTTPS/JSON control operations, resource shapes, error semantics, idempotency, and direct package transfer.
- [Backend Schema and Data Lifecycle](BE_SCHEMA_AND_LIFECYCLE.md) — logical records, invariants, transaction boundaries, migrations, retention, cleanup, and restore behavior.
- [Backend Operations and Recovery](BE_OPERATIONS_RUNBOOK.md) — health, metrics, diagnostics, deployment, incident handling, backup, restore, and disaster-recovery rules.
- [Backend Security Threat Model](BE_SECURITY_THREAT_MODEL.md) — assets, trust boundaries, threats, controls, residual risks, and security acceptance tests.
- [BE-0 Sign-off Checklist](BE0_SIGNOFF_CHECKLIST.md) — final backend planning verification items that unlocked implementation.
- [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md) — lifecycle state machine, process model, adapter capabilities, safe capture, background lifetime, Factorio/Palworld completion contracts, and runtime milestones.
- [AR-0 Sign-off Checklist](AR0_SIGNOFF_CHECKLIST.md) — approved runtime process, lifecycle, cancellation, capability, recovery, and adapter acceptance decisions.
- [Cross-Workstream Contract](CROSS_WORKSTREAM_CONTRACT.md) — shared UI/backend/runtime states, actions, authorities, recovery rules, and first-release acceptance plan.
- [Steam Release Gate](STEAM_RELEASE_GATE.md) — final production Steam identity/distribution acceptance deliberately held until release-candidate quality.

Roadmap items do not override product rules. A planned feature still has to support shared World continuity directly.

## Engineering and reliability

- [Engineering Standards](ENGINEERING.md) — build, dependency, testing, filesystem safety, compatibility, and definition-of-done rules.
- [Error Handling](ERROR_HANDLING.md) — exception boundaries, cancellation, diagnostics, exit codes, and conservative failure semantics.
- [Storage](STORAGE.md) — local persistence, immutable revisions, atomic writes, and the remote-storage boundary.
- [Persistence Compatibility](PERSISTENCE_COMPATIBILITY.md) — versioned document envelopes, migrations, and controlled compatibility failures.
- [Workspace Recovery](WORKSPACE_RECOVERY.md) — prepared workspace states and recovery after interrupted sessions.

## Adapter documentation

- [Game Adapter Guide](ADAPTER_GUIDE.md) — adapter responsibilities, contract rules, and adding a game without contaminating Core.
- [Factorio Adapter](FACTORIO.md) — Factorio discovery, environment handling, state capture, restore, launch, limitations, and validation.
- [Palworld Adapter](PALWORLD.md) — Palworld client/server discovery, dedicated hosting, capture, restore verification, canonical commit, and player-identity limitation.

Additional adapter documents belong here when their behavior becomes product-relevant.

## Required review for changes

Before accepting a meaningful product or architecture change, check:

- Does it preserve one current valid World state and one active writer?
- Does it keep game-specific behavior inside the adapter?
- Does it complete the handoff through capture, durable storage, verification, and commit?
- Does it preserve the previous valid state on failure?
- Is Steam, Discord, or the game already responsible for the proposed feature?
- Does it directly help the initial friend group obtain Steward, understand a shared World, Host/Join, hand off safely, or recover?
- Does it remove a direct blocker on the path from the Friends Build to Steam Early Access?
- Does it add commercial reliability rather than uncontrolled scope?
- If it adds an adapter, is it one of the four V2 primary games or required by a concrete V2 blocker?

When a boundary changes deliberately, update the authoritative documentation before implementation.
