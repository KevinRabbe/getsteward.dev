# Steward Documentation

Steward is a commercial product moving from validated technical foundations toward a dependable user-facing system.

This directory is the canonical home for active product, architecture, engineering, lifecycle, adapter, storage, recovery, and planning documentation.

## Documentation authority

When documents disagree, use this order:

1. [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md)
2. [Product Boundary](PRODUCT_BOUNDARY.md)
3. [Design Decisions](DECISIONS.md)
4. [Architecture](ARCHITECTURE.md)
5. Detailed subsystem and adapter documents
6. [Roadmap](ROADMAP.md)

The first two documents define what Steward is and what it must not become. Lower-level documents may add detail but may not silently expand or contradict the product boundary.

Active documentation must remain internally consistent. Obsolete product concepts should be removed rather than preserved as parallel plans. Git-style branching, generic save merging, ownership hierarchies, social-platform features, and permanent game-server infrastructure are not active Steward product directions.

## Product definition

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steam is the platform. Games are adapters. Worlds are the product.

Steward moves the latest valid World state into a playable session and returns the updated valid state for the next player.

## Product and architecture

- [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md) — constraints product and implementation work must not accidentally violate.
- [Product Boundary](PRODUCT_BOUNDARY.md) — commercial product definition, essential lifecycle, Steam boundary, adapter boundary, background-first behavior, and explicit non-goals.
- [Design Decisions](DECISIONS.md) — current durable architectural decisions and their reasons.
- [Architecture](ARCHITECTURE.md) — dependency direction, adapters, storage, session coordination, revisions, and background runtime boundaries.
- [Domain Model](DOMAIN_MODEL.md) — active persisted and runtime concepts.
- [World Lifecycle](WORLD_LIFECYCLE.md) — import, preparation, launch, session observation, capture, commit, handoff, and recovery.

## Engineering and reliability

- [Engineering Standards](ENGINEERING.md) — build, dependency, testing, filesystem safety, compatibility, and definition-of-done rules.
- [Error Handling](ERROR_HANDLING.md) — exception boundaries, cancellation, diagnostics, exit codes, and conservative failure semantics.
- [Storage](STORAGE.md) — local persistence, immutable revisions, atomic writes, and the remote-storage boundary.
- [Persistence Compatibility](PERSISTENCE_COMPATIBILITY.md) — versioned document envelopes, migrations, and controlled compatibility failures.
- [Workspace Recovery](WORKSPACE_RECOVERY.md) — prepared workspace states and recovery after interrupted sessions.

## Adapter documentation

- [Game Adapter Guide](ADAPTER_GUIDE.md) — adapter responsibilities, contract rules, and adding a game without contaminating Core.
- [Factorio Adapter](FACTORIO.md) — Factorio discovery, environment handling, state capture, restore, launch, limitations, and validation.

Additional adapter documents belong here when their behavior becomes product-relevant.

## Planning

- [Roadmap](ROADMAP.md) — ordered commercial product milestones based on the current boundary.

Roadmap items do not override product rules. A planned feature must still directly support shared World continuity.

## Required review for changes

Before accepting a meaningful product or architecture change, check:

- Does it preserve one current valid World state and one active writer?
- Does it keep game-specific behavior inside the adapter?
- Does it complete the handoff through capture, durable storage, verification, and commit?
- Does it preserve the previous valid state on failure?
- Is Steam or the game already responsible for the proposed feature?
- Does it directly help different players continue the same World across devices or times?
- Does it add commercial reliability rather than uncontrolled scope?

When a boundary changes deliberately, update the authoritative documentation in the same change as the implementation.