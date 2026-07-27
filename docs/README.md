# Steward Documentation

Status: **CURRENT DOCUMENTATION INDEX**

Steward's generic platform architecture, deterministic V3 release preparation, and deterministic first-release UI/product reconciliation are complete. Real V3-E/V3-F acceptance remains evidence-driven.

Use [Documentation State Audit](DOCUMENTATION_AUDIT.md) when deciding whether an older checkpoint file is current guidance or historical evidence.

## Current project mode

> **Deterministic first-release product work is closed. V3-E real Windows evidence is active; V3-F remains the real provider/Steam/game release gate. Do not invent generic features merely because external evidence is pending.**

Current non-documentation executable/UI baseline:

> `01cb15927a9e056fb9a6784d037eabde66e7d819` — PR #151

Current deterministic product-contract/documentation line is PR #153. Later documentation-only reconciliation does not redefine Core/backend/adapter authority.

The next meaningful work is:

```text
real V3-E observation
-> smallest evidence-driven correction if needed
-> real V3-F Steam/provider/game gate
```

Independent deterministic work is allowed only when it creates concrete value without pretending to satisfy those empirical gates. [V4 Technical Readiness](V4_TECHNICAL_READINESS.md) is one such deliberately small future goal.

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
10. [Documentation State Audit](DOCUMENTATION_AUDIT.md)

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
- [Game Technical Readiness](GAME_TECHNICAL_READINESS.md) — what each adapter already proves technically vs what genuinely needs a real game/network observation.
- [Steam Release Gate](STEAM_RELEASE_GATE.md) — one-pass real Steam/provider/Windows/game acceptance.
- [Deferred Empirical Tests](DEFERRED_EMPIRICAL_TESTS.md) — exact real-system questions that deterministic CI does not claim to answer.
- [V4 Technical Readiness](V4_TECHNICAL_READINESS.md) — deliberately small future product goal that projects existing capability/install/environment truth instead of adding authority.

V2 remains useful private acceptance evidence, not the active deterministic development stage:

- [V2 Friends Build](V2_FRIENDS_BUILD.md)
- [V2 Friends Deployment](V2_FRIENDS_DEPLOYMENT.md)
- [V2 Real Acceptance Batch](V2_REAL_ACCEPTANCE_BATCH.md)
- [V2 7DTD Sandbox Authority](V2_7DTD_SANDBOX_AUTHORITY.md)

## UI/product experience

- [UI and UX Roadmap](UI_ROADMAP.md) — authoritative first-release navigation/information architecture, user journeys and product terminology.
- [Cross-Workstream Contract](CROSS_WORKSTREAM_CONTRACT.md) — action/state meaning.
- [Product Completeness](PRODUCT_COMPLETENESS.md) — deterministic Games Library/UI requirements are closed by #147-#153.

The approved hierarchy is implemented:

```text
Games Library
-> game workspace
-> Worlds for selected game
-> selected World details
```

Search, sort, global Settings, game attention, small World Lobby, localization-ready fixed vocabulary, and publish-first Share/access are also reconciled. There is no known current deterministic UI hierarchy discrepancy to implement merely to stay busy.

## Backend contracts

- [Backend Roadmap](BACKEND_ROADMAP.md) — current implemented authority model + remaining external provider/Steam gates.
- [Backend API Contract](BE_API_CONTRACT.md) — implemented `/api/v1` route families, result/error/idempotency/transfer semantics.
- [Backend Schema and Data Lifecycle](BE_SCHEMA_AND_LIFECYCLE.md) — current PostgreSQL logical stores/invariants/transactions/retention.
- [Backend Provider Evaluation](BE_PROVIDER_EVALUATION.md) — provider-neutral criteria + current qualified first disposable Instance/Caddy topology.
- [Backend Operations and Recovery](BE_OPERATIONS_RUNBOOK.md) — current health/deploy/incident/backup/restore operating contract.
- [Backend Security Threat Model](BE_SECURITY_THREAT_MODEL.md) — current identity/authority/transfer/network/security boundary.

## Runtime/adapter contracts

- [Adapter and Background Runtime Roadmap](ADAPTER_RUNTIME_ROADMAP.md) — current writable lifecycle, automatic Join, manual direct-connect presentation distinction, recovery/background behavior.
- [Game Adapter Guide](ADAPTER_GUIDE.md) — stable rules for safely adding/deepening an adapter; current detailed capability matrix lives in Platform Status.
- [Game Technical Readiness](GAME_TECHNICAL_READINESS.md) — canonical technical/evidence view across the 19 registered adapters.
- [Native World Creation](NATIVE_WORLD_CREATION.md) — game-native generator rule; Factorio is current reference implementation.
- [Factorio Adapter](FACTORIO.md) — active dedicated-server/RCON Host path, UDP 34197, no advertised AutomaticHostStop, remaining empirical gates.
- [Palworld Adapter](PALWORLD.md) — read-only `WorldOption.sav`, disposable management settings, REST/process-tree save-stop, identity limitations.

Current Desktop catalog contains 19 first-party adapters. Catalog presence never implies every action capability.

For the later fifteen state/import/environment adapters, real gameplay is not required merely to re-prove their current narrow advertised slice. Real execution becomes necessary only when promoting a runtime capability such as Start, Host, Stop or Join.

## Persistence/recovery/engineering

- [Storage](STORAGE.md) — implemented local + shared storage/transfer/current-head separation.
- [Persistence Compatibility](PERSISTENCE_COMPATIBILITY.md) — integrity-protected storage schemas, state-payload binding, device-settings v1->v2 migration.
- [Workspace Recovery](WORKSPACE_RECOVERY.md) — durable Active/RecoveryPending/CleanupPending/interrupted-session semantics.
- [Engineering Standards](ENGINEERING.md) — dependency/build/testing/bounds/filesystem/documentation rules.
- [Error Handling](ERROR_HANDLING.md) — conservative failure/cancellation/retry/diagnostic semantics.

## Historical checkpoints — evidence only

The following files intentionally preserve milestone evidence. **Do not use their old “current/next” wording as present-day implementation guidance.**

Planning/sign-off history:

- `UI0_SIGNOFF_CHECKLIST.md`
- `BE0_SIGNOFF_CHECKLIST.md`
- `AR0_SIGNOFF_CHECKLIST.md`
- `BE1_LOCAL_SIMULATION.md`

Implementation checkpoint history:

- `E1_STATUS.md`
- `BE2_STATUS.md`
- `BE3_S3_CHECKPOINT.md`
- `BE4_STATUS.md`
- `BE5_STATUS.md`
- `E4_DESKTOP_STATUS.md`
- `E6_STATUS.md`
- `E8_STATUS.md`

Important known stale historical statements are listed explicitly in `DOCUMENTATION_AUDIT.md`. In particular, `E4_DESKTOP_STATUS.md` contains an old Factorio “direct listen-host is current” paragraph and old production `STEWARD_*` client configuration wording; current Factorio/V3/code contracts supersede them.

## Current executable facts worth checking before changing scope

- Desktop catalog: 19 adapters.
- Action claims come from `GameAdapterCapabilities`, not catalog registration.
- Factorio active `IGameAdapter` Host path uses dedicated server + RCON; managed game endpoint is UDP 34197; no `AutomaticHostStop` flag.
- Palworld advertises Host + Host Stop + exact game version; no automatic Join; manual direct-connect guidance is presentation-only.
- 7DTD/PZ managed runtime remains evidence-gated/frozen.
- the other fifteen registered adapters intentionally expose narrower state/import/environment slices; absence of runtime actions is not an unimplemented promise.
- shared backend/storage/authority already exists: Backend.Api + PostgreSQL + private S3-compatible object storage + remote Desktop composition.
- production Steam release config is package-owned; ordinary `STEWARD_*` environment configuration is engineering/acceptance-only.
- Steam owns installation/update; Steward has no self-updater requirement.
- first disposable provider topology uses one Instance + Caddy + exact loopback trusted-proxy peer; final vendor selection remains open.
- V3-E has started; #138 is the first real Windows evidence-driven defect/fix.

## Change review

Before a meaningful product/architecture change ask:

- Does it preserve one current valid World and one writer?
- Does game-specific behavior stay in the adapter?
- Does canonical head still advance last?
- Does failure preserve previous valid state/recovery evidence?
- Does Steam/Windows/the game already own the proposed mechanism?
- Is the change backed by a concrete product requirement or real evidence?
- Can the problem be removed instead of solved with another subsystem?
- Is somebody proposing a manual test for a fact already knowable from native/platform/adapter evidence?

When a boundary deliberately changes, update active documentation in the same slice.
