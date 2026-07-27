# Steward Documentation

Status: **CURRENT DOCUMENTATION INDEX**

Steward's generic platform architecture, deterministic V3 release preparation, deterministic first-release UI/product reconciliation, small V4 technical-readiness presentation, and the current 19-game deterministic exactness audit are complete. Real V3-E/V3-F acceptance remains evidence-driven.

Use [Documentation State Audit](DOCUMENTATION_AUDIT.md) when deciding whether an older checkpoint file is current guidance or historical evidence.

## Current project mode

> **No known deterministic product discrepancy remains at the current audited boundary. V3-E real Windows evidence is active; V3-F remains the real provider/Steam/game release gate. Do not invent generic features merely because external evidence is pending.**

Current non-documentation executable head:

> `f89743f51dceb6ee3a107c94b73b547af94ef663` — PR #192

The deterministic first-release product checkpoint remains qualified PR #153. V4 #158/#160 adds only a derived technical-evidence map and read-only capability summary; it does not redefine Core/backend/adapter authority or make any empirical V3 gate green.

A later evidence-driven adapter audit found five concrete deterministic vanilla/exactness gaps and closed them without reopening V4 or requiring gameplay tests:

```text
#171 Core Keeper official mod.io boundary
-> #178 Necesse pre-materialization Workshop boundary
-> #181 ASTRONEER install-root UE4SS/direct PAK boundary
-> #184 Smalland exact stock PAK set
-> #192 Palworld native -NoMods managed Host boundary
```

The next meaningful work is:

```text
real V3-E observation
-> smallest evidence-driven correction if needed
-> real V3-F Steam/provider/game gate
```

[V4 Technical Readiness](V4_TECHNICAL_READINESS.md) is complete at its intentionally small boundary. It is not a new active feature phase.

## Start here

For current work, read in this order:

1. [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md)
2. [Product Boundary](PRODUCT_BOUNDARY.md)
3. [Design Decisions](DECISIONS.md)
4. [Architecture](ARCHITECTURE.md)
5. [Master Roadmap](ROADMAP.md)
6. [Product Completeness](PRODUCT_COMPLETENESS.md)
7. [V3 Steam Release Candidate](V3_STEAM_RELEASE_CANDIDATE.md)
8. [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md)
9. [Game Technical Readiness](GAME_TECHNICAL_READINESS.md)
10. [V4 Technical Readiness](V4_TECHNICAL_READINESS.md)
11. [Documentation State Audit](DOCUMENTATION_AUDIT.md)

For the final external release batch use [Steam Release Gate](STEAM_RELEASE_GATE.md).

## Documentation authority

When active documents disagree, use this order:

1. `NON_NEGOTIABLE_RULES.md`
2. `PRODUCT_BOUNDARY.md`
3. `DECISIONS.md`
4. `ARCHITECTURE.md`
5. `ROADMAP.md` + current V3/platform status
6. subsystem contracts
7. adapter-specific contracts/evidence maps
8. empirical runbooks

Historical checkpoint documents are evidence, not current design authority.

Production code/executable contracts still win over stale prose when a direct conflict is discovered; fix the documentation rather than bending code to an obsolete status paragraph.

## Core product/architecture contracts

- [Non-Negotiable Rules](NON_NEGOTIABLE_RULES.md) — safety/product boundaries that must not be weakened accidentally.
- [Product Boundary](PRODUCT_BOUNDARY.md) — what Steward is/is not; Steam/game ownership boundaries.
- [Design Decisions](DECISIONS.md) — active durable architecture/product decisions only.
- [Architecture](ARCHITECTURE.md) — current local/shared storage, backend authority, adapter/runtime separation.
- [Domain Model](DOMAIN_MODEL.md) — World/revision/session/recovery concepts; no permanent Server domain object.
- [World Lifecycle](WORLD_LIFECYCLE.md) — writable lifecycle, capture/commit/recovery ordering.
- [Cross-Workstream Contract](CROSS_WORKSTREAM_CONTRACT.md) — shared UI/backend/runtime states/actions/authority rules.

## Current planning/execution

- [Master Roadmap](ROADMAP.md) — deterministic first-release reconciliation closed; V3-E then V3-F are the active evidence sequence.
- [Product Completeness](PRODUCT_COMPLETENESS.md) — implemented vs empirical vs deliberately removed first-release requirements.
- [V3 Steam Release Candidate](V3_STEAM_RELEASE_CANDIDATE.md) — deterministic V3 #133-#137 complete; V3-E started with #138.
- [Platform Implementation Status](PLATFORM_IMPLEMENTATION_STATUS.md) — stable generic platform checkpoint + current 19-adapter/action-capability state.
- [Game Technical Readiness](GAME_TECHNICAL_READINESS.md) — what each adapter already proves technically vs what genuinely needs a real game/network observation; deterministic environment-exactness audit current through #192.
- [V4 Technical Readiness](V4_TECHNICAL_READINESS.md) — completed small derived-readiness goal; no new authority or support taxonomy.
- [Steam Release Gate](STEAM_RELEASE_GATE.md) — one-pass real Steam/provider/Windows/game acceptance.
- [Deferred Empirical Tests](DEFERRED_EMPIRICAL_TESTS.md) — exact real-system questions that deterministic CI does not claim to answer.

V2 remains useful private acceptance evidence, not the active deterministic development stage:

- [V2 Friends Build](V2_FRIENDS_BUILD.md)
- [V2 Friends Deployment](V2_FRIENDS_DEPLOYMENT.md)
- [V2 Real Acceptance Batch](V2_REAL_ACCEPTANCE_BATCH.md)
- [V2 7DTD Sandbox Authority](V2_7DTD_SANDBOX_AUTHORITY.md)
