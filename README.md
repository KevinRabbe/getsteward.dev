# Steward

> **One shared World. Different Steam players. Different times. No always-on game server.**

Steward is a Windows desktop application for continuing the same game World across trusted Steam players and PCs without operating a permanent Steward-owned game-server fleet.

Steam is the platform. Games are adapters. Worlds are the product.

## Current product state

The current closed-beta release candidate is the peer-hosted product at exact SHA:

`9acf25f7696dfe9056028e4201ca380a94883f0e`

Release-candidate branch:

`release/steward-peer-closed-beta-rc1`

Automated exact-head qualification is green for that SHA. The remaining release evidence is physical: two Windows PCs, two real Steam accounts, the private Steward Steam AppID, and one complete Host/Join/handoff/restart/revocation/Leave run.

See:

- issue #349 — physical Steam peer two-PC qualification;
- issue #350 — repository trunk/default-branch cutover after physical qualification;
- `tools/steam-peer-two-pc-kit/START-HERE-STEAM-PEER-TWO-PC.txt` — physical acceptance procedure.

Automated CI does **not** substitute for the real Steam/network/game boundary.

## Final product topology

Normal Steward use does not require a central Steward backend.

```text
Steward.exe
   |
   +-- Local World
   |      |
   |      `-- Share
   |             -> persistent peer authority generation 1
   |
   +-- Manage access
   |      +-- Add Steam friend
   |      +-- Remove access
   |      `-- Leave World
   |
   +-- Host
   |      +-- private FriendsOnly Steam lobby
   |      +-- canonical members invited through Steam
   |      +-- port 71: Steward World/control traffic
   |      `-- port 72: bridged game traffic
   |
   +-- Join
   |      +-- Steam invite / +connect_lobby
   |      +-- bootstrap or catch-up exact World state
   |      `-- local game connection through Steward bridge
   |
   `-- Hand off host
          -> safe final save
          -> exact revision transfer
          -> authority generation N -> N+1
          -> target activates
          -> Steam lobby ownership moves last
```

One Steward distribution contains the normal peer product and explicit legacy migration compatibility. There is no separate Steward server executable required for ordinary use.

## Authority model

A Shared World has durable peer authority:

```text
WorldPeerAuthority
  holder: stable Steward/Steam identity
  generation: nonzero monotonic authority generation
```

The durable World state, not Steam lobby ownership by itself, decides who may host the next writable generation.

Important rules:

- exactly one persistent authority holder exists for a peer-shared World;
- generation 1 is created when a LocalOnly World is explicitly shared;
- a normal stop/restart does **not** increment generation;
- generation increments exactly once for a deliberate successful host handoff;
- if no host is active, the World is inactive rather than being kept alive by a Steward server;
- Steam lobby ownership is ephemeral transport/platform state and must agree with persistent Steward authority before writable operations proceed;
- stale generation, stale holder, ambiguous persistence, or conflicting authority fails closed.

## Host lifecycle

When the current persistent holder hosts a Shared World, Steward:

1. validates durable peer authority and current World head;
2. creates/owns a private FriendsOnly Steam lobby for that exact generation;
3. publishes managed-host readiness only after the game/session is actually usable;
4. automatically invites canonical Steam World members;
5. serves bootstrap/catch-up and control traffic on Steward virtual port 71;
6. admits game traffic on virtual port 72 only after membership + authority checks;
7. observes the game-specific lifecycle through the adapter;
8. safely captures and commits the final World state before releasing writable responsibility.

The host machine contains the live authoritative save state while the World is active.

## Join and catch-up

A canonical member may join through the private Steam lobby.

Steward does not treat Steam lobby membership as canonical World membership. The Join path revalidates:

- World ID;
- canonical member identity;
- persistent holder;
- exact nonzero authority generation;
- confirmed live lobby owner/generation;
- current revision and transfer integrity.

A joining peer bootstraps or catches up the exact World state it needs. Outside live hosting, users may also exchange a World ZIP themselves; Steward does not need to own the external file-transfer channel.

## Safe host handoff

Host handoff is not a live process migration.

The existing lifecycle performs:

```text
request next host
-> serialize authority mutation
-> stop outgoing host safely
-> final save/capture
-> commit exact final revision
-> transfer/verify that exact revision
-> target activates durable generation N+1
-> acknowledge activation
-> move Steam lobby ownership
```

The UI only offers handoff targets that are:

`canonical World member ∩ valid Steam identity ∩ current live lobby participant`

The protocol rechecks membership/presence again immediately before authority mutation, so a disconnect after the picker opens is refused safely.

## Membership, revocation, and Leave World

Canonical membership is stored on the World and controlled by the current persistent authority holder.

### Add person

The holder selects an immediate Steam friend. Steward persists canonical membership first, then uses Steam to deliver the private lobby invitation. Steam is the picker/invite transport; it is not the membership authority.

### Remove access

For a live World, removal is fail-closed and ordered:

```text
revoke exact (World, generation, member)
-> cancel matching port-71 work
-> close matching port-72 sessions
-> persist canonical membership removal
-> reload and verify holder/generation/current head unchanged
```

A failed or ambiguous persistence step does not silently reopen the revoked member.

### Leave World

A non-holder does not simply delete its local replica.

```text
authenticated requester asks current holder to leave
-> current holder canonically removes requester
-> exact-generation acknowledgement returns
-> requester deletes local replica
-> requester detaches from Steam lobby best-effort
```

The current holder must hand off authority before using the member Leave path.

## State safety

Steward keeps the same core invariants regardless of game adapter:

- one current valid World state;
- at most one writable Steward authority;
- immutable published revisions;
- canonical head advances only after replacement bytes are captured, stored, verified, and committed;
- failed work preserves the previous valid state and recovery evidence;
- adapters own game-specific session/save/capture truth;
- no generic save merging or Git-style branching;
- no live process/memory migration.

See `docs/NON_NEGOTIABLE_RULES.md`.

## Steam package boundary

The normal product package is AppID-only.

`steward-steam.json` contains only:

```json
{
  "schemaVersion": 1,
  "steamAppId": 123456789
}
```

Normal peer packaging contains no Steward API URL, Web API identity, backend credential, Friends Build routing, or remote-session configuration.

Legacy compatibility remains available only through explicit migration activation, such as:

- explicit migration package configuration;
- explicit `STEWARD_ENABLE_LEGACY_REMOTE_MIGRATION=true` engineering/operator mode.

Old Backend.Api/PostgreSQL/S3 implementation remains in the repository for migration/history. It is not the ordinary product authority path.

## Code structure

```text
SharedWorlds.Core
  universal World lifecycle, revisions, adapters, safety contracts

SharedWorlds.Infrastructure
  local persistence
  peer authority fences
  bootstrap/catch-up/replication
  membership/removal/Leave
  generation-fenced session coordination
  explicit legacy remote migration compatibility

SharedWorlds.Desktop
  Windows UI/background lifecycle
  one process-lifetime Steam runtime
  private lobby Host/Join/invitations
  port-71 revision/control exchange
  port-72 game bridge
  Steam friend picker
  host handoff UI
  product composition

SharedWorlds.GameAdapters/*
  independently owned game-specific behavior
```

Core must not gain game-name branches merely because one adapter is unusual.

## Game adapters

The repository contains 19 first-party adapters. Catalog registration is not a promise that every adapter supports every action.

`GameAdapterCapabilities` remains the executable capability boundary for operations such as Start, Host, Join, Stop/Save, and native World creation. Game-specific uncertainty stays inside the adapter/evidence boundary rather than expanding Core.

## CI

Normal peer-product changes use a deliberately small automatic qualification surface:

1. `Peer product CI`;
2. `Windows acceptance package`;
3. `Peer exact-head qualification`.

Game-specific adapter workflows are path-aware and run only when their adapter/global build surface changes. Portable/physical engineering kits are manual-only. Legacy backend CI is separately scoped to migration/backend work.

Queued, cancelled, skipped, superseded, or different-head workflow results are not exact-head qualification.

## Closed-beta physical evidence

The peer RC includes a byte-verifiable two-PC kit builder and read-only evidence probe.

The physical acceptance run checks:

- identical product bytes/AppID on both PCs;
- Local -> Shared generation 1;
- Steam friend access selection;
- private Host/invite/Join/bootstrap;
- exact A/B revision + payload hash equality;
- safe A -> B host handoff and generation 1 -> 2;
- restart persistence with generation remaining 2;
- live Remove access and immediate targeted revocation;
- re-add/catch-up;
- true authoritative Leave World.

A failure should produce the smallest fix for the observed boundary. Do not redesign a working peer architecture speculatively.

## Documentation authority

The repository contains substantial historical closed-alpha/backend/Friends-Build material. Preserve it for migration and engineering provenance, but do not use old active-sounding backend documents to infer the normal product topology.

Current product/architecture entry points are:

- this README;
- `docs/ARCHITECTURE.md`;
- `docs/PRODUCT_BOUNDARY.md`;
- `docs/NON_NEGOTIABLE_RULES.md`;
- `docs/DOCUMENTATION_AUDIT.md`.

When documentation and executable behavior disagree, the qualified executable authority/safety contract wins and the documentation must be reconciled.