# Design Decisions

This file records active durable decisions. Obsolete exploratory directions are removed rather than kept as competing product plans.

A decision may be replaced deliberately, but implementation convenience must not silently reverse it.

## D-001: The World is the product

**Decision:** Users select a World, not a save file, server folder, mod profile, revision package, or host machine.

**Reason:** Those are implementation details required to reproduce and continue the playable reality.

## D-002: Steam is the platform; games are adapters

**Decision:** Steam is the primary commercial product platform. Game-specific behavior remains behind independently compiled adapters.

**Reason:** Steam already provides identity, distribution, game ownership, launching, friends, invitations, Workshop content, and dedicated-server tooling. Steward should reuse those facilities without contaminating Core with game-specific Steam behavior.

## D-003: Core contains no game-specific branches

**Decision:** Core must never branch on Factorio, Palworld, or another game identity.

**Consequence:** Discovery, environment preparation, restore, launch, session observation, safe shutdown, capture, and validation belong to the adapter.

## D-004: The product completes a full World handoff

**Decision:** Launching the game is not completion.

**Required flow:** latest state -> reserve -> prepare -> restore -> launch -> observe -> capture -> store -> verify -> commit -> available to next player.

**Reason:** Continuity between different players, devices, and times is the product value.

## D-005: One shared World state has one active writer

**Decision:** A Steward-managed World has one current valid state and at most one active writable Steward session.

**Reason:** Generic merging of independently changed game saves is unsafe or impossible.

**Consequence:** Other players join the active host through Steam or the game, or wait for the World to become available.

## D-006: The current state advances last

**Decision:** The World head changes only after the new state is completely captured, durably stored, verified, and committed.

**Reason:** A failed capture or upload must leave the last known-good World intact.

## D-007: Published revisions are immutable

**Decision:** Environment and state revisions may not be overwritten after publication. Only the World head is mutable.

**Reason:** Integrity, recovery, compatibility, diagnostics, and safe commit ordering require stable revision identities.

## D-008: Environment and state are versioned separately

**Decision:** A World references one environment revision and one state revision.

**Example:** `E7 + S143 -> E7 + S144` for normal play.

**Reason:** Most sessions change the save without changing the required game/mod environment.

## D-009: The manifest is authoritative

**Decision:** `EnvironmentManifest` is the source of truth. A fingerprint is only a disposable comparison/cache aid.

**Reason:** A small canonical manifest can be compared cheaply without pretending every installation file was deeply verified.

## D-010: Do not routinely hash complete game installations

**Decision:** Full-install hashing is not part of the normal path.

**Allowed uses:** explicit verification or repair, corruption investigation, first-download integrity, or targeted adapter validation.

## D-011: Session observation is adapter-owned

**Decision:** `WaitForSessionEndAsync` and equivalent safe-capture decisions belong to the adapter.

**Reason:** Launchers may replace processes, clients may close while dedicated servers continue, and games have different save-completion semantics.

## D-012: Adapters own state shape

**Decision:** Adapters capture and restore opaque game state packages.

**Reason:** A World may be a ZIP, directory tree, database, multiple files, or launcher-managed structure. Core must not assume one universal format.

## D-013: Temporary package ownership is explicit

**Decision:** Core deletes a captured package only when the adapter explicitly marks it disposable.

**Reason:** A path may point to a temporary copy, cache, workspace, or user-owned data. Cleanup authority must not be guessed.

## D-014: Prepared workspaces become recovery assets after launch

**Decision:** Prepared workspaces are registered before launch and preserved after uncertain post-launch failure.

**Reason:** They may contain the newest recoverable gameplay state even when canonical commit did not complete.

## D-015: Failure handling is conservative

**Decision:** Stable boundaries classify failures; unknown failures stop the current operation rather than being swallowed.

**Reason:** Continuing after an unknown state-handling failure is more dangerous than entering recovery.

## D-016: Storage and session coordination are separate

**Decision:** `IWorldStorage` stores durable state. `IWorldSessionCoordinator` protects transient one-writer session state.

**Reason:** Their lifecycles, failure modes, and possible Steam implementations differ.

## D-017: Host switching is a controlled restart between sessions

**Decision:** Another device may host only after the previous session safely captured and committed its result.

**Flow:** stop -> capture -> commit -> restore on another device -> launch.

**Reason:** Live process migration is unnecessary and unreliable.

## D-018: No universal save merging

**Decision:** Steward does not merge independently modified arbitrary game saves.

**Reason:** Saves may contain binary serialization, object references, checksums, databases, duplicated resource use, and semantic conflicts with no universal correct resolution.

**Exception:** An adapter may expose a validated game-native transfer operation, but that remains game-specific and is not generic World merging.

## D-019: No Git-style World product model

**Decision:** Forks, branches, merge requests, rebasing, and conflict-resolution workflows are not active product concepts.

**Reason:** Steward coordinates gameplay continuity, not software-development history.

## D-020: No ownership or governance platform

**Decision:** Steward does not build complex World ownership, role, party, social graph, dispute-resolution, or public-discovery systems.

**Reason:** Groups organize themselves. Steward needs only the minimum identity/access information required to distribute and advance the shared state safely.

## D-021: External copies are outside Steward's authority

**Decision:** Steward does not promise physical uniqueness, DRM, or deletion of every copy after state reaches another device.

**Reason:** The product only needs an agreed current state inside the Steward workflow.

## D-022: Shared does not mean public

**Decision:** `Shared` means eligible for cross-device state handoff and coordination. It does not imply a public listing or social community.

## D-023: Background-first is not passive

**Decision:** Steward should remain mostly out of the user's way while actively observing the session and completing capture, storage, verification, commit, and recovery.

**Reason:** Users should spend time in the game, but the background lifecycle is essential product work.

## D-024: Use existing infrastructure before building new infrastructure

**Decision:** Prefer Steam, game-native multiplayer, dedicated servers, Workshop, launchers, mod managers, and native export/import functions where they solve the problem.

**Reason:** Steward should coordinate legitimate endpoints rather than recreate them.

## D-025: Generalize only after real adapters prove the pattern

**Decision:** Keep an edge case inside one adapter until multiple adapters demonstrate a stable universal concept.

**Reason:** One unusual game must not expand the Core product model.

## D-026: Factorio and Palworld are the current validation set

**Decision:** Use Factorio and Palworld to prove the generic lifecycle against materially different save and hosting behavior before expanding breadth.

**Reason:** Both already provide valuable real-world evidence for discovery, preparation, launch, process observation, state capture, restore, and canonical commit.

## D-027: The next decisive milestone is a two-device handoff

**Decision:** Prioritize shared durable state storage and distributed one-writer coordination sufficient for PC A -> PC B -> PC A continuation.

**Required proof:**

```text
PC A commits state N+1
-> PC B retrieves and continues N+1
-> PC B commits N+2
-> PC A retrieves N+2
```

A competing writable start must be rejected while one session is active.

## D-028: Commercial quality does not justify uncontrolled scope

**Decision:** Reliability, recovery, testability, maintainability, and stable boundaries are mandatory. Unnecessary platform, social, governance, and speculative features remain out of scope.

**Reason:** Steward is moving beyond prototype validation, but the smallest dependable product is still the target.

## D-029: Active documentation must be consistent

**Decision:** When a product direction is abandoned, remove it from active architecture, lifecycle, domain, decision, and roadmap documents.

**Reason:** A commercial codebase cannot rely on readers guessing which contradictory document is current.