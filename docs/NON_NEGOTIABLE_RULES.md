# Non-Negotiable Rules

These rules protect Steward's product identity and architecture. They are constraints, not suggestions.

A change that violates one of these rules requires an explicit decision to replace the rule first. It must never happen accidentally through implementation convenience.

## 1. The World is the product

Users choose a World, not a save folder, server installation, mod directory, state package, revision hash, or host machine.

Internal representations must not become the user-facing product model.

## 2. Steam is the platform; games are adapters

Steward uses Steam as the primary product platform where useful.

Game-specific behavior remains behind adapters. Core must never contain branches such as:

```text
if game is Factorio
if game is Palworld
```

Adding one unusual game must not contaminate the universal product model.

## 3. One shared World state, one active writer

A Steward-managed World has one current valid state and at most one active writable Steward session.

Another player may join the active game through Steam or the game, but Steward must not start a competing writable session from the same current state.

## 4. The canonical state advances last

The previous valid state remains authoritative until the replacement state has been completely captured, durably stored, verified, and committed.

A failed capture, upload, verification, or metadata update must not replace the last valid state.

## 5. Never destroy recoverable user state to make cleanup easier

After gameplay begins, a prepared workspace may contain the newest recoverable World state.

On uncertain failure, preserve recovery evidence rather than deleting it.

Core may delete a captured package only when the adapter explicitly marks it as disposable.

## 6. Session observation is adapter-owned

The Core does not assume that one process ID equals one session.

The adapter determines:

- which game, launcher, child process, or server represents the session;
- whether the session is still active;
- how the process should stop safely;
- when the save is safe to capture;
- what must be validated before publication.

## 7. Steward must complete the handoff

Launching the game is not enough.

The required product lifecycle is:

```text
latest valid state
-> prepare
-> restore
-> launch
-> monitor
-> safe capture
-> durable store
-> verification
-> commit
-> available for the next player
```

A feature that stops at launch does not complete Steward's job.

## 8. No live host migration

Changing host happens between sessions through a controlled restart:

```text
save
-> stop
-> capture
-> commit
-> restore on another device
-> launch
```

Steward does not migrate a live game process or mutable memory state between computers.

## 9. No universal save merging

Steward must not promise generic merging of independently changed game saves.

Game saves may be binary, compressed, referential, database-backed, checksum-protected, or semantically conflicting. Treat them as opaque unless an adapter exposes a validated game-native operation.

## 10. Do not become a social or governance platform

Steward does not need to own:

- parties;
- chat;
- a social graph;
- public discovery;
- complex ownership hierarchies;
- granular gameplay roles;
- dispute resolution;
- Git-style branches and merge workflows.

Use Steam and the game for players, friends, invitations, and multiplayer joining wherever possible.

## 11. Groups organize themselves

Steward should not solve social questions that do not block safe World continuity.

It does not need to decide who morally owns a copied save, track every external copy, guarantee universal deletion, or resolve disagreements between players.

## 12. External copies are outside Steward's authority

Once World data reaches another device, Steward cannot guarantee physical uniqueness or deletion of every copy.

Steward manages the current shared state inside its own workflow. It does not act as DRM.

## 13. Switching games and switching hosts reuse the lifecycle

Switching host means the same World and adapter run later on another device.

Switching game means the user selects another World and Steward uses that World's adapter.

Steward never converts a World from one game into another.

## 14. The application is background-first

Users should spend little time inside Steward.

The normal interaction is:

```text
select World
-> Start World or Host World
-> play
```

Steward remains active in the background because it must observe, capture, store, verify, and complete the handoff.

Background-first must never be misinterpreted as launcher-only or passive behavior.

## 15. Prefer removing work over rebuilding existing platforms

Before adding a subsystem, ask:

1. Does Steam already provide it?
2. Does the game already provide it?
3. Can the adapter use an existing dedicated server, launcher, mod manager, or native export?
4. Can the problem be made irrelevant instead of solved?

Do not rebuild legitimate existing infrastructure without a demonstrated product requirement.

## 16. Do not generalize before real adapters prove the pattern

A universal abstraction should be justified by repeated real behavior across supported games.

Keep one game's edge cases inside its adapter until multiple adapters demonstrate a stable shared concept.

## 17. Commercial quality without uncontrolled scope

Steward is a commercial product moving beyond prototype validation.

Therefore:

- state safety must be conservative;
- failures must be recoverable;
- contracts must be testable;
- architecture must survive additional adapters;
- user-facing behavior must be dependable;
- temporary shortcuts must not corrupt the final boundaries.

Commercial quality does not mean building every imaginable feature.

## 18. Build the smallest system that creates the full product effect

The essential effect is:

> **One shared World. Different Steam players. Different times. No always-on game server.**

Every addition must justify itself against that promise.

## 19. Preserve these operating principles

- Do not solve a problem that can be made irrelevant.
- Do more by doing less.
- Think first. Remove second. Build last.
- Keep only what is still necessary for the next stage.
- Increase what Steward can achieve while decreasing how much Steward itself has to own.
- Respect previous work by keeping what it taught us, not necessarily every implementation it produced.
- The cheapest code to maintain, secure, debug, test, and scale is code that never needed to exist.

## 20. The final boundary test

Before accepting a feature, abstraction, service, or workflow, ask:

> **Does this directly help Steward move the latest valid World into a playable session and return the updated valid state for the next player?**

If the answer is no, the default decision is remove, defer, or delegate.
