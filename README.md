# SafeWorld

> **One shared World. Different Steam players. Different times. No always-on game server.**

SafeWorld is a Windows desktop application for continuing the same game World across trusted Steam players and PCs without requiring a permanent game server.

Steam is the platform. Games are adapters. Worlds are the product.

## Current status

SafeWorld is being prepared for its first public-beta candidate on branch `safeworld-public-beta-v1` in draft PR #359.

The next physical two-PC test is intentionally reserved for the real installable beta candidate. Development test packages and command-script launchers are not release candidates.

Before that candidate is produced we still need the real SafeWorld Steam AppID and a public Windows code-signing setup. Those are release inputs, not blockers for normal development.

## Product model

SafeWorld is one peer-hosted desktop product. There is no separate SafeWorld server distribution required for ordinary use.

A World can be local or shared. When a shared World is active, one peer is the Host and owns the authoritative live state. When no Host is active, the World is simply inactive.

The core lifecycle is:

```text
latest valid World state
-> prepare the correct game environment
-> start or host the game
-> play
-> stop safely
-> capture and verify the updated state
-> commit the new current World state
-> make it available for the next session
```

SafeWorld never tries to merge independently changed game saves and does not migrate a live game process between PCs.

## Sharing and multiplayer

Steam provides identity, friends, invitations, lobby discovery, networking, distribution, and updates.

SafeWorld keeps World membership and World continuity separate from temporary Steam lobby state. A joining friend receives the World state needed for that session and connects through the normal SafeWorld peer flow.

Users may also exchange a World ZIP themselves when the World is inactive. SafeWorld does not need to own that external file-transfer method.

## Host handoff

A deliberate host handoff uses the safe lifecycle:

```text
stop outgoing Host
-> final save/capture
-> commit exact revision
-> transfer and verify that revision
-> activate the next Host
```

Writable responsibility never moves before the previous Host has completed its final save and commit.

## Windows distribution

Public beta and release use a normal Windows installer.

The installed product exposes:

- `SafeWorld.Desktop.exe`;
- Start Menu and desktop shortcuts;
- normal Windows uninstall registration;
- preservation of World data outside the installation directory.

The release path does not depend on `.cmd` launchers.

## Steam configuration

New SafeWorld packages use:

`safeworld-steam.json`

```json
{
  "schemaVersion": 1,
  "steamAppId": 123456789
}
```

The canonical environment override is `SAFEWORLD_STEAM_APP_ID`.

Development AppID 480 is not accepted as a public-beta identity.

## Code structure

```text
SharedWorlds.Core
  universal World lifecycle and safety contracts

SharedWorlds.Infrastructure
  local persistence and peer coordination

SharedWorlds.Desktop
  Windows UI, Steam runtime, Host/Join, handoff, product composition

SharedWorlds.GameAdapters/*
  independently owned game-specific behavior
```

These project and namespace names are implementation details. The user-facing product is **SafeWorld**.

Core knows what must happen. Adapters know how a specific game makes it happen.

## Qualification

Normal beta-readiness changes are checked through:

1. `Peer product CI`;
2. `Windows acceptance package`;
3. `SafeWorld public beta package` structural and installer lifecycle verification;
4. `Peer exact-head qualification`.

A real public-beta candidate is produced from one qualified revision using the real SafeWorld Steam identity and the final Windows signing setup. That exact installer becomes the artifact used for the physical two-PC beta test.

## Architecture rules

See:

- `docs/ARCHITECTURE.md`;
- `docs/PRODUCT_BOUNDARY.md`;
- `docs/NON_NEGOTIABLE_RULES.md`.

When documentation and executable behavior disagree, the qualified executable safety contract wins and the documentation must be reconciled.
