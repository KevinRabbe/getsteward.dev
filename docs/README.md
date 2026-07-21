# Steward Documentation

Steward is a commercial product moving from validated technical foundations toward a dependable user-facing system.

This directory is the canonical home for product, architecture, engineering, lifecycle, adapter, storage, recovery, and decision documentation.

## Documentation authority

When documents disagree, use this order:

1. [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md)
2. [Product Boundary](PRODUCT_BOUNDARY.md)
3. [Design Decisions](DECISIONS.md)
4. [Architecture](ARCHITECTURE.md)
5. Detailed subsystem and adapter documents
6. Roadmap and exploratory notes

The first two documents define what Steward is and what it must not become. A lower-level document must not silently override them.

Older documents may still contain exploratory concepts that were useful during foundation work. When they conflict with the current boundary, the boundary and non-negotiable rules are authoritative until the older document is reconciled.

## Product definition

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steam is the platform. Games are adapters. Worlds are the product.

Steward's job is to move the latest valid World state into a playable session and return the updated valid state for the next player.

## Product and architecture

- [Product Boundary](PRODUCT_BOUNDARY.md) — commercial product definition, essential lifecycle, Steam boundary, adapter boundary, background-first behavior, and explicit non-goals.
- [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md) — constraints that implementation and product work must not accidentally violate.
- [Architecture](ARCHITECTURE.md) — dependency direction, platform boundaries, adapters, storage, session coordination, revisions, and host handoff.
- [Domain Model](DOMAIN_MODEL.md) — persisted and runtime concepts used by the system.
- [Design Decisions](DECISIONS.md) — durable architectural decisions and their reasons.
- [World Lifecycle](WORLD_LIFECYCLE.md) — import, preparation, restore, launch, session observation, capture, commit, and recovery behavior.

## Engineering and reliability

- [Engineering Standards](ENGINEERING.md) — build, dependency, testing, filesystem safety, compatibility, and definition-of-done rules.
- [Error Handling](ERROR_HANDLING.md) — exception boundaries, cancellation, diagnostics, exit codes, and conservative failure semantics.
- [Storage](STORAGE.md) — local persistence, immutable revisions, atomic writes, and the remote-storage boundary.
- [Persistence Compatibility](PERSISTENCE_COMPATIBILITY.md) — versioned document envelopes, migrations, and controlled compatibility failures.
- [Workspace Recovery](WORKSPACE_RECOVERY.md) — prepared workspace states and recovery behavior after interrupted sessions.

## Adapter documentation

- [Game Adapter Guide](ADAPTER_GUIDE.md) — adapter responsibilities, contract rules, and adding a game without contaminating Core.
- [Factorio Adapter](FACTORIO.md) — Factorio discovery, environment handling, state capture, restore, launch, limitations, and validation.

Additional adapter documents should live in this directory as their behavior becomes product-relevant.

## Planning

- [Roadmap](ROADMAP.md) — ordered product development milestones.

Roadmap items do not override product boundaries or architectural rules. A planned feature still needs to justify itself against the product promise.

## Required review for changes

Before accepting a meaningful product or architecture change, check:

- Does it preserve one current valid World state and one active writer?
- Does it keep game-specific behavior inside the adapter?
- Does it complete the full handoff through capture, durable storage, verification, and commit?
- Does it preserve the previous valid state on failure?
- Is Steam or the game already responsible for the proposed feature?
- Does it directly help different players continue the same World across devices or times?
- Does it add commercial reliability rather than uncontrolled scope?

When the answer exposes a boundary change, update the authoritative documentation in the same change as the code.
