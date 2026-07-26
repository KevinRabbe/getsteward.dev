# Steward Documentation

Steward is a commercial Windows desktop product with its generic platform code/CI boundary complete, its deterministic V2 Friends Build engineering stage complete, and its V3 Steam Release Candidate now the active product goal.

This directory is the canonical home for active product, architecture, engineering, lifecycle, adapter, storage, recovery, and planning documentation.

## Current project mode

> **V3 Steam Release Candidate is the active execution goal. The V2 Friends Build deterministic product work is complete; its remaining real-provider, real-machine, and real-friend acceptance stays recorded as empirical evidence rather than blocking independent release-candidate work. Normal implementation now removes development/private-build assumptions from the qualified product until a normal Steam-installed Steward build can authenticate, connect, and run the same safe shared-World lifecycle without developer environment setup. Steam remains responsible for distribution, updates, and production identity; Steward must not rebuild those platform functions.**

Start with the [V3 Steam Release Candidate](V3_STEAM_RELEASE_CANDIDATE.md) for the active product goal and execution rule. Use the [V2 Friends Build](V2_FRIENDS_BUILD.md) for the completed private-product engineering stage and its still-open empirical acceptance evidence. Use [Native World Creation](NATIVE_WORLD_CREATION.md) for the rule that Steward should invoke a game's supported native generator rather than require manual pre-creation or synthesize save bytes. Use the [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) for the qualified technical checkpoint and stable extension boundary. Use the [Master Roadmap](ROADMAP.md) and workstream roadmaps for detailed product contracts and historical implementation sequencing:

1. [UI and UX Roadmap](UI_ROADMAP.md)
2. [Backend Roadmap](BACKEND_ROADMAP.md)
3. [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md)

The roadmaps are separate for clarity but define one product. Their states, actions, failure semantics, and acceptance criteria must continue to agree. Older milestone/checkpoint wording is historical where the V3 Steam Release Candidate, V2 Friends Build, or Platform Implementation Status explicitly supersedes it.

Current completion evidence and execution direction:

- [V3 Steam Release Candidate](V3_STEAM_RELEASE_CANDIDATE.md) — active product goal: eliminate private/development runtime assumptions, create depot-ready production configuration/content, preserve Steam as distribution/update/identity owner, and close the genuine release gate only with real Steam evidence;
- [V2 Friends Build](V2_FRIENDS_BUILD.md) — completed deterministic private-product engineering stage; private distribution/authentication, minimal World lobby, four primary games, real friend handoff runbook, and deferred empirical evidence remain reusable toward release;
- [Native World Creation](NATIVE_WORLD_CREATION.md) — active creation rule: Create new World invokes the game's native generator, Import existing World remains available, and a temporary game/server process never becomes a permanent Steward Server object;
- [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) — current all-workflows-green platform checkpoint, stable extension boundary, historical adapter-addition workflow, and remaining empirical release gates;
- [BE-2 Status](BE2_STATUS.md) — authenticated World/access/revision metadata with durable PostgreSQL persistence;
- [BE-3 S3 Checkpoint](BE3_S3_CHECKPOINT.md) — immutable transfer, S3-compatible protocol proof, cleanup/retention, and desktop verified caching/materialization;
- [BE-4 Status](BE4_STATUS.md) — durable one-writer reservation/generation authority, canonical commit, reclaim, late-writer rejection, and transactional idempotency;
- [BE-5 Status](BE5_STATUS.md) — real PostgreSQL-backed PC A -> PC B -> PC A handoff, adverse authority/reclaim proof, and deterministic Waiting-to-sync recovery after a lost successful commit response;
- [E4 Windows Desktop Status](E4_DESKTOP_STATUS.md) — production Desktop remote composition, stable installation identity, Steam Web API ticket bootstrap code, merged local/shared World routing, exact-environment gate, deterministic pending/cleanup/interrupted recovery, and Share/access surfaces;
- [E4 Live Acceptance Deployment](E4_LIVE_ACCEPTANCE_DEPLOYMENT.md) — first disposable EU Friends/E4-A deployment contract and the exact-proxy trust shape prepared by qualified #131;
- [Steam Release Gate](STEAM_RELEASE_GATE.md) — explicit decision to introduce the real Steward AppID/publisher credentials only after the rest of the product is good enough to publish, followed by genuine final Steam acceptance with no production auth bypass.

## Documentation authority

When documents disagree, use this order:

1. [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md)
2. [Product Boundary](PRODUCT_BOUNDARY.md)
3. [Design Decisions](DECISIONS.md)
4. [Architecture](ARCHITECTURE.md)
5. [V3 Steam Release Candidate](V3_STEAM_RELEASE_CANDIDATE.md) for the active execution goal and V3 scope
6. [V2 Friends Build](V2_FRIENDS_BUILD.md) for the completed private-product engineering stage and its empirical acceptance boundary
7. [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) for the current qualified implementation/checkpoint boundary
8. Detailed subsystem and adapter documents
9. [Master Roadmap](ROADMAP.md) and its workstream roadmaps

The first two documents define what Steward is and what it must not become. Lower-level documents may add detail but may not silently expand or contradict the product boundary.

Active documentation must remain internally consistent. Obsolete product concepts should be removed rather than preserved as parallel plans. Git-style branching, generic save merging, ownership hierarchies, social-platform features, and permanent game-server infrastructure are not active Steward product directions.

## Product definition

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steam is the intended commercial platform. Games are adapters. Worlds are the product. V2 used private distribution/authentication only to prove the product with trusted friends before public Steam release acceptance; V3 removes that private-build assumption from the commercial release path.

Steward moves the latest valid World state into a playable session and returns the updated valid state for the next player.

## Product and architecture

- [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md) — constraints product and implementation work must not accidentally violate.
- [Product Boundary](PRODUCT_BOUNDARY.md) — commercial product definition, essential lifecycle, Steam boundary, adapter boundary, background-first behavior, and explicit non-goals.
- [Design Decisions](DECISIONS.md) — current durable architectural decisions and their reasons.
- [Architecture](ARCHITECTURE.md) — dependency direction, adapters, storage, session coordination, revisions, and background runtime boundaries.
- [Domain Model](DOMAIN_MODEL.md) — active persisted and runtime concepts.
- [World Lifecycle](WORLD_LIFECYCLE.md) — import, preparation, launch, session observation, capture, commit, handoff, and recovery.
- [Native World Creation](NATIVE_WORLD_CREATION.md) — game-native creation, initial revision persistence, settings ownership, and the explicit absence of a permanent Server domain object.

## Planning and execution

- [V3 Steam Release Candidate](V3_STEAM_RELEASE_CANDIDATE.md) — active execution target: production package configuration, depot-ready content, production Steam/backend matching, release-capable adapter evidence, real Windows acceptance, and final Steam gate.
- [V2 Friends Build](V2_FRIENDS_BUILD.md) — completed deterministic private-product stage and still-open empirical friend/provider evidence.
- [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) — qualified code/CI boundary and stable adapter/platform extension rules; its former adapter-expansion default is superseded by V2/V3.
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
- Is Steam, Discord, Windows, or the game already responsible for the proposed feature?
- Does it directly remove a development/private-test assumption between the qualified product and a real Steam release?
- Does it remove a direct blocker on the path to Steam Early Access?
- Does it add commercial reliability rather than uncontrolled scope?
- If it changes an adapter capability, is that capability backed by deterministic or empirical evidence?

When a boundary changes deliberately, update the authoritative documentation before implementation.
