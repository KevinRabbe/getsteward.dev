# Design Decisions

This file records active durable decisions. Obsolete exploratory directions are removed rather than kept as competing product plans.

A decision may be replaced deliberately, but implementation convenience must not silently reverse it.

## D-001: The World is the product

**Decision:** Users select a World, not a save file, server folder, mod profile, revision package, or host machine.

**Reason:** Those are implementation details required to reproduce and continue the playable reality.

## D-002: Steam is the platform; games are adapters

**Decision:** Steam is the primary commercial product platform. Game-specific behavior remains behind independently compiled adapters.

**Reason:** Steam already provides identity, distribution, game ownership, launching, friends, invitations, Workshop content, and dedicated-server tooling. Steward should reuse those facilities without contaminating Core with game-specific behavior.

## D-003: Core contains no game-specific branches

**Decision:** Core must never branch on Factorio, Palworld, or another game identity.

**Consequence:** Discovery, environment preparation, restore, launch, session observation, safe shutdown, Join specifics, capture, and validation belong to the adapter.

## D-004: The product completes a full World handoff

**Decision:** Launching the game is not completion.

**Required flow:** latest state -> reserve -> prepare -> restore -> launch -> observe -> capture -> store -> verify -> commit -> available to next player.

**Reason:** Continuity between different players, devices, and times is the product value.

## D-005: One shared World state has one active writer

**Decision:** A Steward-managed World has one current valid state and at most one active writable Steward session.

**Reason:** Generic merging of independently changed game saves is unsafe or impossible.

**Consequence:** Other players join the active host through Steam/the game or wait for availability.

## D-006: The current state advances last

**Decision:** The World head changes only after the new state is completely captured, durably stored, verified, and committed.

**Reason:** A failed capture/upload/verification must leave the last known-good World authoritative.

## D-007: Published revisions are immutable

**Decision:** Environment and state revisions may not be overwritten after publication. Only the World head is mutable.

**Reason:** Integrity, recovery, compatibility, diagnostics, and safe commit ordering require stable revision identities.

## D-008: Environment and state are versioned separately

**Decision:** A World references one environment revision and one state revision.

**Example:** `E7 + S143 -> E7 + S144` for normal play.

**Reason:** Most sessions change state without changing the required game/mod environment.

## D-009: The manifest is authoritative

**Decision:** `EnvironmentManifest` is the source of truth. A fingerprint is only a disposable comparison/cache aid.

**Reason:** A small canonical manifest can be compared cheaply without pretending every installation file was deeply verified.

## D-010: Do not routinely hash complete game installations

**Decision:** Full-install hashing is not part of the normal path.

**Allowed uses:** explicit verification/repair, corruption investigation, first-download integrity, or targeted adapter validation.

## D-011: Session observation is adapter-owned

**Decision:** `WaitForSessionEndAsync` and equivalent safe-capture/readiness decisions belong to the adapter.

**Reason:** Launchers may replace processes, clients may close while dedicated servers continue, and games have different save-completion semantics.

## D-012: Adapters own state shape

**Decision:** Adapters capture and restore opaque game state packages.

**Reason:** A World may be a ZIP, directory tree, database, multiple files, or launcher-managed structure. Core must not assume one universal format.

## D-013: Temporary package ownership is explicit

**Decision:** Core deletes a captured package only when the adapter explicitly marks it disposable or the higher-level retention contract makes cleanup eligibility explicit.

**Reason:** A path may point to temporary, cached, recovery, workspace, or user-owned data. Cleanup authority must not be guessed.

## D-014: Prepared workspaces become recovery assets after launch

**Decision:** Prepared workspaces are registered before launch and preserved after uncertain post-launch failure.

**Reason:** They may contain the newest recoverable gameplay state even when canonical commit did not complete.

## D-015: Failure handling is conservative

**Decision:** Stable boundaries classify failures; unknown failures stop the current operation rather than being swallowed.

**Reason:** Continuing after unknown state-handling failure is more dangerous than entering recovery.

## D-016: Storage and session coordination are separate

**Decision:** `IWorldStorage` stores durable state. `IWorldSessionCoordinator` protects one-writer session state.

**Reason:** Their lifecycles and failure modes differ even when one backend eventually provides both implementations.

## D-017: Host switching is a controlled restart between sessions

**Decision:** Another device may host only after the previous writable session safely resolves its handoff or is deliberately recovered.

**Normal flow:** stop -> capture -> commit -> restore on another device -> launch.

**Reason:** Live process migration is unnecessary and unreliable.

## D-018: No universal save merging

**Decision:** Steward does not merge independently modified arbitrary game saves.

**Reason:** Saves may be binary, compressed, referential, database-backed, checksum-protected, or semantically conflicting with no universal correct resolution.

**Exception:** An adapter may expose a validated game-native operation, but that remains game-specific and is not generic World merging.

## D-019: No Git-style World product model

**Decision:** Forks, branches, merge requests, rebasing, and conflict-resolution workflows are not active product concepts.

**Reason:** Steward coordinates gameplay continuity, not software-development history.

## D-020: No ownership or governance platform

**Decision:** Steward does not build complex World ownership, role, party, social graph, dispute-resolution, or public-discovery systems.

**Reason:** Groups organize themselves. Steward needs only the minimum identity/access information required for safe World continuity.

## D-021: External copies are outside Steward's authority

**Decision:** Steward does not promise physical uniqueness, DRM, or deletion of every external copy after state reaches another device.

**Reason:** The product only needs an agreed current state inside the Steward workflow.

## D-022: Shared does not mean public

**Decision:** `Shared` means eligible for Steward cross-device handoff and coordination. It does not imply public listing/community discovery.

## D-023: Background-first is not passive

**Decision:** Steward should remain mostly out of the user's way while actively observing the session and completing capture, storage, verification, commit, synchronization, and recovery.

**Reason:** Users should spend time in the game, but the background lifecycle is essential product work.

## D-024: Use existing infrastructure before building new infrastructure

**Decision:** Prefer Steam, game-native multiplayer, dedicated servers, Workshop, launchers, mod managers, and native export/import where they solve the problem.

**Reason:** Steward should coordinate legitimate endpoints rather than recreate them.

## D-025: Generalize only after real adapters prove the pattern

**Decision:** Keep one game's edge cases inside its adapter until multiple adapters demonstrate a stable universal concept.

**Reason:** One unusual game must not expand the Core product model.

## D-026: Factorio and Palworld are the current validation set

**Decision:** Use Factorio and Palworld to prove the generic lifecycle against materially different save and hosting behavior before expanding breadth.

## D-027: The next decisive implementation proof is a two-device handoff

**Decision:** Prioritize shared durable state storage and distributed one-writer coordination sufficient for PC A -> PC B -> PC A continuation.

```text
PC A commits N+1
-> PC B retrieves/continues N+1
-> PC B commits N+2
-> PC A retrieves N+2
```

A competing writable start must be rejected while one session is Active or Uncertain.

## D-028: Commercial quality does not justify uncontrolled scope

**Decision:** Reliability, recovery, testability, maintainability, and stable boundaries are mandatory. Unnecessary platform/social/governance/speculative features remain out of scope.

**Reason:** Steward is moving beyond prototype validation, but the smallest dependable product remains the target.

## D-029: Active documentation must be consistent

**Decision:** When a product direction is abandoned or superseded, active architecture, lifecycle, domain, decision, roadmap, sign-off, and cross-workstream documents are reconciled.

**Reason:** A commercial codebase cannot rely on readers guessing which contradictory document is current.

## D-030 / BE-D004: Versioned HTTPS/JSON API with direct package transfer

**Decision:** Steward uses a versioned HTTPS request/response API with JSON for authentication, metadata, World access, reservations, recovery, and commit operations. Large World/environment packages transfer directly between authorized clients and private object storage through scoped resumable transfer targets rather than through JSON or normally through the API service.

Retryable mutations use idempotency semantics. WebSockets, gRPC, and custom binary protocols are not first-release dependencies.

## D-031 / BE-D014: First-release security baseline

**Decision:** First release uses server-verified Steam identity, TLS for control/transfer traffic, private encrypted object storage/backups, per-resource authorization, short-lived scoped transfer targets, idempotency/generation checks, bounded payload/rate/timeout/retry controls, redacted diagnostics, audit events, secret isolation, and tested backup/restore.

Steward uses established cryptographic primitives/provider security features rather than creating custom cryptography.

## D-032 / BE-D015: One authoritative EU backend deployment

**Decision:** First release uses one authoritative Steward backend deployment in the EU. Transactional metadata, primary object storage, identity/access metadata, reservation state, and canonical World coordination remain inside the documented EU residency boundary. Encrypted disaster-recovery backups may use another suitable EU location.

Active-active multi-region World authority is deferred. Immutable package delivery may later use replicas/cache/CDN/peer assistance without moving canonical authority.

## D-033 / AR-0: Runtime and adapter contract approved

**Decision:** First-release runtime uses one user-session desktop/tray process, one active writable managed session per device, runtime-owned generic orchestration, adapter-owned game evidence/safe capture, conservative cancellation, capability-driven Stop and Save/Join behavior, preserved recovery evidence, and the Factorio/Palworld validation boundaries recorded in `AR0_SIGNOFF_CHECKLIST.md`.

## D-034 / BE-D001: Hybrid backend owns only missing shared authority

**Decision:** Steam remains platform/identity infrastructure while one small Steward backend owns shared World metadata, membership, canonical heads, reservation generations, and commit/recovery authority. Object storage stores immutable bytes only.

## D-035 / BE-D002: Steam ticket verification bootstraps Steward authentication

**Decision:** The desktop obtains a Steam authentication ticket; the backend verifies it directly and derives SteamID64. Short-lived Steward credentials handle normal API calls. Client-supplied SteamID alone is never trusted.

## D-036 / BE-D003: Flat shared membership plus one Access Manager

**Decision:** Accepted World members have equal World usage rights. Exactly one Access Manager manages membership only. World-access invitations are distinct from multiplayer-session invitations. Revocation of an active writer remains pending until responsibility resolves safely.

## D-037 / BE-D005: Reservation timeouts create uncertainty, not availability

**Decision:** Initial defaults are approximately 30-second heartbeat, ~2-minute `Active -> Uncertain`, and ~15-minute deliberate reclaim grace. Uncertain never auto-releases. Reclaim atomically invalidates the old generation before another writer may start.

## D-038 / BE-D006: Last-safe continuation resolves authority before abandonment

**Decision:** `Continue from last safe state` may abandon a candidate as canonical work only after reservation/generation/head authority is deliberately resolved. The candidate is preserved under retention policy rather than deleted by the authority transaction.

## D-039 / BE-D007: First backend implementation is provider-neutral

**Decision:** BE-1 is deterministic/in-memory and proves contracts without Steam HTTP hosting, cloud SDKs, or production provider assumptions. Named DB/object-storage vendors are chosen later from measured evidence while preserving PostgreSQL-compatible/S3-compatible or equivalent required semantics.

## D-040 / BE-D008: Active gameplay may continue through backend outage

**Decision:** A valid already-running shared session may continue locally when backend connectivity is lost. Remote authority becomes Uncertain rather than Available. Session end captures a durable local candidate and enters `Waiting to sync`; reconnect must revalidate auth, generation, and expected head before commit. Invalidated/stale authority becomes `Recovery needed`.

## D-041 / BE-D009: Unresolved gameplay candidates are not time-deleted

**Decision:** Unresolved local candidates have no automatic time-based deletion. Explicitly abandoned local candidates use ~7-day recovery grace; verified uncommitted remote candidates use ~7-day retention while not actively pinned; incomplete transfers use ~24-hour cleanup. Disk pressure becomes `Action required` rather than silent loss.

## D-042 / BE-D010: Retain three canonical states plus pinned dependencies

**Decision:** Shared Worlds normally retain current canonical state + previous two committed states. Older state/environment revisions remain pinned while active transactions or unresolved recovery require them. Retained revisions are recovery assets, not user-facing branches/history.

## D-043 / BE-D011: State and environment share one immutable transfer pipeline

**Decision:** State/environment remain logically distinct revision references but use the same immutable authorized transfer/verification infrastructure. Adapters determine environment semantics. Prefer native/Steam/Workshop reproducible references over duplicating third-party content where reliable.

## D-044 / BE-D012: Package transfer is bounded before publication

**Decision:** Initial hard ceiling is 20 GiB per immutable State or Steward-hosted Environment package; adapters may set smaller limits. Large transfers use resumable/multipart behavior with ~64 MiB initial part target where appropriate. Expected size/hash must verify before publication eligibility; local disk preflight occurs before large materialization.

## D-045 / BE-D013: API outcomes are deterministic and idempotent

**Decision:** Expected transaction results use stable machine-readable operation-specific outcomes; clients never parse human-readable prose. Actual API failures use structured HTTP/Problem Details-style responses. Important mutations are idempotent, ambiguous network outcomes are resolved by retry/status lookup, and authentication credential expiry never automatically releases a World session generation.

## Planning decision status

UI-0, BE-0, AR-0, cross-workstream reconciliation, and the first-release acceptance plan are complete.

The master planning lock remains active until the product owner explicitly approves the transition from planning to implementation.