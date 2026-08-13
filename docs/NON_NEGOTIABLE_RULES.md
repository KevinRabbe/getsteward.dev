# Non-Negotiable Rules

These rules protect SafeWorld's product identity and architecture. They are constraints, not suggestions.

A change that violates one of these rules requires an explicit decision to replace the rule first. It must never happen accidentally through implementation convenience.

## 1. The World is the product

Users choose a World, not a save folder, server installation, mod directory, revision hash, or host machine.

Internal representations must not become the user-facing product model.

## 2. Steam is the platform; games are adapters

SafeWorld uses Steam for platform capabilities where useful.

Game-specific behavior remains behind adapters. Core must never contain game-name branches merely because one adapter is unusual.

## 3. One shared World state, one active writer

A SafeWorld-managed World has one current valid state and at most one active writable SafeWorld authority.

Other players may join the active game, but SafeWorld must not start a competing writable session from the same current state.

## 4. The canonical state advances last

The previous valid state remains authoritative until the replacement state has been completely captured, durably stored, verified, and committed.

A failed capture, verification, persistence, or metadata update must not replace the last valid state.

## 5. Never destroy recoverable user state for cleanup convenience

After gameplay begins, a prepared workspace may contain the newest recoverable World state.

On uncertain failure, preserve recovery evidence rather than deleting it.

## 6. Session observation is adapter-owned

Core does not assume that one process ID equals one session.

The adapter determines:

- which process or process group represents the session;
- whether the session is still active;
- how it should stop safely;
- when the save is safe to capture;
- what must be validated before publication.

## 7. SafeWorld must complete the handoff

Launching the game is not enough.

The required lifecycle is:

```text
latest valid state
-> prepare
-> restore
-> launch
-> observe
-> safe save/capture
-> durable store
-> verification
-> commit
-> available for the next session
```

A feature that stops at launch does not complete SafeWorld's job.

## 8. No live host migration

Changing Host happens through a controlled state transition, not memory/process migration.

```text
save
-> stop
-> capture
-> commit
-> transfer/restore on the next device
-> launch
```

## 9. No universal save merging

SafeWorld must not promise generic merging of independently changed game saves.

Game saves may be binary, compressed, referential, database-backed, checksum-protected, or semantically conflicting. Treat them as opaque unless an adapter exposes a validated game-native operation.

## 10. Do not become a social or governance platform

SafeWorld does not need to own:

- chat;
- a social graph;
- public discovery;
- complex ownership hierarchies;
- granular gameplay roles;
- dispute resolution;
- Git-style branches and merge workflows.

Use Steam and the game for social/platform features wherever possible.

## 11. Groups organize themselves

SafeWorld should not solve social questions that do not block safe World continuity.

It does not need to decide who morally owns a copied save, track every external copy, or resolve disputes between players.

## 12. External copies are outside SafeWorld's authority

Once World data reaches another device, SafeWorld cannot guarantee physical uniqueness or deletion of every copy.

SafeWorld manages the current shared state inside its own workflow. It does not act as DRM.

## 13. Switching games and switching Hosts reuse the lifecycle

Switching Host means the same World and adapter continue on another device.

Switching game means the user selects another World and SafeWorld uses that World's adapter.

SafeWorld never converts a World from one game into another.

## 14. The application is background-first

Users should spend little time inside SafeWorld.

The normal interaction is:

```text
select World
-> Start World or Host World
-> play
```

SafeWorld remains active in the background because it must observe, save, capture, verify, and complete the World handoff.

Background-first must never be misinterpreted as launcher-only.

## 15. One product distribution

Ordinary SafeWorld use must not require a separate SafeWorld backend/server distribution.

The active Host carries the live authoritative state. If there is no Host, the World is inactive.

Do not reintroduce a permanent central authority to solve a problem that the peer-hosted model already makes unnecessary.

## 16. Steam lobby state is not durable World authority

Steam owns lobby/discovery/network transport state.

SafeWorld owns durable World authority and canonical membership.

Writable operations require those views to agree; lobby ownership alone never grants durable World authority.

## 17. Host authority moves only after final state is safe

A deliberate handoff follows this ordering:

```text
stop outgoing Host
-> final save/capture
-> commit exact revision
-> transfer and verify
-> activate next authority generation
-> move temporary Steam ownership last
```

No shortcut may move writable responsibility earlier.

## 18. Revocation must be generation-scoped and fail closed

Removing access from a live World must revoke the matching current member/session before canonical membership is considered removed.

Stale generation or ambiguous persistence must not silently reopen access.

## 19. Preserve the player's real game profile when appropriate

SafeWorld may isolate authoritative server state, but it must not replace a graphical player's normal local game identity/preferences unless the adapter has a demonstrated reason to do so.

Language, account identity, controls, and equivalent user profile state should remain the player's normal game profile for graphical local/Host/Join clients.

## 20. Public distribution is a real installed product

Public beta/release must launch as SafeWorld from the installed executable/shortcut.

Do not require `.cmd` or PowerShell bootstrap launchers as the normal user path.

The installed user-facing executable identity is `SafeWorld.Desktop.exe`.

## 21. User-facing naming is SafeWorld

The product name presented to users, installers, release artifacts, UI, and current documentation is **SafeWorld**.

Internal historical implementation names must not leak into the release surface.

## 22. Prefer removing work over rebuilding existing platforms

Before adding a subsystem, ask:

1. Does Steam already provide it?
2. Does the game already provide it?
3. Can an adapter reuse an existing game/server/launcher capability?
4. Can the problem be made irrelevant instead of solved?

Do not rebuild legitimate existing infrastructure without a demonstrated product requirement.

## 23. Do not generalize before real adapters prove the pattern

A universal abstraction should be justified by repeated real behavior across supported games.

Keep one game's edge cases inside its adapter until multiple adapters demonstrate a stable shared concept.

## 24. Commercial quality without uncontrolled scope

State safety must be conservative, failures recoverable, contracts testable, and the architecture maintainable across adapters.

Commercial quality does not mean building every imaginable feature.

## 25. Build the smallest system that creates the full product effect

The essential effect is:

> **One shared World. Different Steam players. Different times. No always-on game server.**

Every addition must justify itself against that promise.

## 26. Preserve these operating principles

- Do not solve a problem that can be made irrelevant.
- Do more by doing less.
- Think first. Remove second. Build last.
- Keep only what is still necessary for the next stage.
- Respect previous work by keeping what it taught us, not necessarily every implementation it produced.
- The cheapest code to maintain, secure, debug, test, and scale is code that never needed to exist.

## 27. The final boundary test

Before accepting a feature, abstraction, service, or workflow, ask:

> **Does this directly help SafeWorld move the latest valid World into a playable session and return the updated valid state for the next player?**

If the answer is no, the default decision is remove, defer, or delegate.
