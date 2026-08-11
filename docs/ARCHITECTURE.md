# Architecture

Status: **CURRENT — peer-hosted Steward product architecture.**

## Product kernel

The product is the **World**.

> **One shared World. Different Steam players. Different times. No always-on game server.**

A World is the latest valid playable state plus the environment information required to reproduce it. Steward moves responsibility for that World between trusted Steam players/PCs while preserving one writable authority and one canonical history.

Steam is the platform. Games are adapters. Steward owns World continuity.

The central code rule remains:

> **Core knows what must happen. Adapters know how a specific game makes it happen.**

Core must never contain game-name branches merely because one adapter has unusual runtime or save semantics.

## Normal product topology

Ordinary Steward use is peer-hosted. There is no permanent central Steward authority/backend required for normal Host/Join/access/handoff/Leave behavior.

```text
Steward.exe
   |
   +-- SharedWorlds.Core
   |      universal lifecycle/revision/safety contracts
   |
   +-- SharedWorlds.Infrastructure
   |      local durable storage
   |      peer authority fences
   |      bootstrap/catch-up/replication
   |      membership/removal/Leave
   |      generation-fenced session coordination
   |
   +-- SharedWorlds.Desktop
   |      Windows UI/background runtime
   |      one process-lifetime Steam runtime
   |      private lobby Host/Join
   |      port 71 World/control exchange
   |      port 72 game bridge
   |
   +-- SharedWorlds.GameAdapters/*
   |      game-specific behavior
   |
   `-- explicit legacy migration compatibility
          Backend.Api/PostgreSQL/S3 paths retained only when deliberately activated
```

There is one Steward product distribution. A second Steward server executable is not part of the ordinary topology.

## Dependency direction

```text
Windows Desktop / background runtime / engineering tools
                    |
                    v
             SharedWorlds.Core
              |      |      |
              |      |      `-> session/lifecycle contracts
              |      |
              |      `-> IWorldStorage
              |            `-> local durable World/revision storage
              |
              `-> IGameAdapter
                     `-> independently compiled game-specific adapters

SharedWorlds.Infrastructure
  implements peer/local coordination and durable persistence around Core contracts

SharedWorlds.Desktop
  composes Steamworks transport/platform behavior around Infrastructure/Core
```

Core does not depend on Steamworks, Desktop, PostgreSQL, S3, launcher SDKs, or concrete games.

Legacy remote/backend implementations remain compiled for explicit migration compatibility but are outside normal peer-product activation.

## Three authority layers

Steward separates durable World authority, ephemeral Steam transport state, and game/session evidence.

### 1. Durable World authority

A peer-shared World contains persistent authority:

```text
WorldPeerAuthority
  Holder: UserIdentity
  Generation: ulong > 0
```

This is the durable statement of who may create/continue writable peer authority for the World.

Rules:

- LocalOnly Worlds have no peer authority;
- explicit Share creates generation 1 only after durable World/fence preparation succeeds;
- ordinary Host stop/restart preserves the same generation;
- successful deliberate handoff increments generation exactly once;
- stale or conflicting generation evidence fails closed;
- no live host means the World is inactive, not centrally hosted elsewhere.

### 2. Steam lobby/transport state

Steam owns platform primitives:

- Steward distribution/update;
- Steam identity;
- friend enumeration;
- private lobby creation;
- invites and `+connect_lobby` delivery;
- peer networking transport.

A Steam lobby is **not** canonical World membership or persistent World authority.

Before writable/transfer/game operations, Steward requires the live lobby owner/generation to agree with durable peer authority.

### 3. Game/session evidence

`IGameAdapter` owns facts such as:

- installation and World discovery;
- environment inspection/preparation;
- state restore/capture;
- local/host/join launch behavior;
- server/client process lifecycle;
- readiness;
- safe stop/save boundary;
- capture validity.

Neither durable authority nor Steam presence may manufacture game-specific readiness/capture truth.

## Local -> Shared cutover

Sharing is explicit.

```text
LocalOnly World
-> capture/validate current durable head
-> persist Shared membership/authority prerequisites
-> create durable active authority fence
-> publish WorldPeerAuthority(holder = local Steam identity, generation = 1)
-> World is peer-shared
```

Authority is published only after the required durable state exists. Steward does not create a backend World or upload state to a central authority during normal Share.

## Host lifecycle

Only the current persistent authority holder may host a peer World as writable authority.

Managed Host performs conceptually:

```text
load canonical World
-> validate holder + generation + current state
-> validate durable authority fence
-> create private FriendsOnly Steam lobby
-> bind lobby metadata to World + generation
-> launch/observe managed game Host through adapter
-> publish Ready managed-host presence only when actually usable
-> invite canonical Steam members
-> serve peer World/control traffic
-> bridge admitted game traffic
-> on stop/end: safe save/capture
-> store immutable revision
-> verify
-> advance current World head
-> finalize managed-host presence/lobby state
```

The host PC contains the live authoritative save state while the World is active.

## Peer networking

Steward uses two Steam virtual-port responsibilities:

### Port 71 — World/control traffic

Used for bounded reliable Steward protocol work such as:

- bootstrap/catch-up;
- revision transfer;
- observer synchronization;
- handoff control/activation acknowledgement;
- authenticated Leave request/result.

Authorization is bound to:

- World ID;
- exact authority generation;
- authenticated remote Steam identity;
- canonical World membership;
- current holder/lobby state.

### Port 72 — game traffic bridge

Used to bridge game traffic through Steward/Steam networking while keeping the game-facing endpoint local to the joining client/host bridge.

Admission rechecks canonical membership, holder/generation, lobby state, and live revocation state before granting a bridge.

Member revocation closes only matching `(World, generation, remote Steam identity)` sessions. Whole-listener reset is reserved for managed Host end, not individual member kicks.

## Join/bootstrap/catch-up

A member may join from a Steam invite or cold `+connect_lobby` launch.

The Join path:

```text
Steam lobby target
-> resolve World + generation + holder
-> confirm lobby owner against persistent authority
-> authenticate remote Steam identity
-> confirm canonical membership
-> bootstrap or catch up exact World/environment state
-> verify revision/package integrity
-> attach local game bridge
-> launch/join through adapter
```

A joining replica never becomes writable authority merely because it possesses state bytes or is present in the lobby.

Steward may also import/share a World ZIP outside live hosting. The product deliberately does not own how users transfer that ZIP to each other.

## Safe host handoff

Host handoff reuses the managed lifecycle; it does not migrate a live process.

The authority transaction is:

```text
current holder generation N
-> request canonical live member as next host
-> serialize against membership mutations
-> record requested host on confirmed live lobby
-> safely stop outgoing managed Host
-> final save/capture
-> commit exact final revision
-> finalize outgoing workspace
-> transfer/verify exact committed revision to target
-> target activates durable authority generation N+1
-> target acknowledges exact activation
-> move Steam lobby ownership
```

The outgoing holder does not directly write generation N+1 in Desktop UI code. The lifecycle/session coordinator owns the transaction.

If final save, commit, transfer, verification, activation, or acknowledgement fails, Steward does not pretend handoff completed.

## Handoff candidate presentation

The handoff dialog is presentation only.

Candidates are:

```text
canonical World members
∩ valid Steam identities
∩ current exact-generation live lobby participants
- current holder
```

The real protocol rechecks live membership again immediately before requested-host mutation. Therefore a disconnect after the dialog opens is refused rather than converted into stale authority.

## Membership authority

`World.Members` is canonical access authority.

Steam friends/lobby members are not automatically World members.

### Add person

The holder selects an immediate Steam friend. Steward persists canonical membership first. Invitation delivery through the private lobby happens afterward and can be retried.

### Remove access while inactive

Canonical membership can be removed directly after holder/fence validation because no live peer work exists to revoke.

### Remove access while live

Live removal executes under the shared live-authority mutation gate:

```text
validate exact holder/generation/lobby/current head
-> refuse if handoff requested/in progress
-> set exact member revocation deny
-> cancel matching port-71 work
-> notify/close matching port-72 contexts
-> persist canonical member removal
-> reload/verify member absent and holder/generation/head unchanged
```

If canonical persistence becomes ambiguous after revocation, the deny remains active. Safety is preferred over silently reopening access.

Explicit holder-controlled re-add restores the revocation key only after canonical membership exists again.

## Leave World

Leave is an authoritative membership operation, not local deletion.

The request contains only:

- World ID;
- expected authority generation.

The authenticated Steam port-71 connection supplies the requester identity, preventing one participant from naming another member as the Leave target.

Requester ordering:

```text
validate local non-holder replica
-> require confirmed current holder/generation/no handoff
-> send authenticated Leave request
-> holder performs exact-generation safe canonical removal
-> receive exact acknowledgement
-> switch to non-cancellable local cleanup
-> delete/verify local replica
-> best-effort detach from Steam lobby
```

The current holder must hand off first.

## State/revision persistence

`LocalWorldStorage` is the normal durable persistence implementation for peer Worlds on each participating PC.

Published state/environment revisions remain immutable. Current World metadata points at the selected canonical head.

Peer replication/install paths validate identity, revision relationships, package availability, and payload integrity before advancing local replica state.

A stale replica cannot become writable merely by being copied back onto a machine because persistent authority/fence/generation checks remain required.

## Background responsibility

Steward is background-first, not launcher-only.

After Start/Host begins, Steward remains responsible for:

- managed game/session observation;
- safe capture boundaries;
- immutable state persistence;
- verification;
- canonical head commit;
- authority/lobby cleanup;
- recovery preservation on uncertainty.

Closing or minimizing UI must not manufacture a completed handoff while Steward still owns unresolved writable responsibility.

## Steam package/configuration boundary

The normal Steam package contains one platform file:

`steward-steam.json`

Normal schema:

```json
{
  "schemaVersion": 1,
  "steamAppId": 123456789
}
```

The normal package contains no central API coordinate or remote-session credential.

Legacy remote migration can be enabled only through explicit compatibility configuration, including explicit migration package files or the explicit environment gate:

`STEWARD_ENABLE_LEGACY_REMOTE_MIGRATION=true`

Ambient stale `STEWARD_API_BASE_URL`, auth mode, AppID, or Web API identity variables cannot activate legacy remote mode by themselves.

## Legacy backend boundary

Backend.Api, PostgreSQL, S3-compatible storage, Friends Build, Owned Private catalog, and Bring Here code remain in the repository for old-World migration, engineering evidence, and historical provenance.

They are not ordinary peer-product authority.

Normal AppID-only startup:

- initializes peer runtime independently;
- does not construct the legacy invitation inbox;
- does not construct Owned Private/Bring Here UI;
- does not allocate the legacy owned-location publication worker;
- does not establish remote runtime from ambient legacy variables.

Explicit migration activation may still construct/use those compatibility paths.

## Adapter architecture

Game adapters continue to own all game-specific behavior. `GameAdapterCapabilities` is the executable capability boundary.

Catalog registration alone never grants Start/Host/Join/Stop/Create claims.

The repository currently contains 19 first-party adapters with intentionally different capability/evidence levels. Peer authority logic remains game-agnostic.

## CI architecture

The normal peer product is qualified separately from legacy/backend/physical tooling.

Always-required ordinary peer workflows:

1. `Peer product CI`;
2. `Windows acceptance package`.

`Peer exact-head qualification` observes the exact PR head and requires those plus only path-relevant adapter workflows.

Adapter workflows are path-scoped. Portable and Steam physical two-PC kits are manual-only. Legacy backend CI is path-scoped/manual for compatibility work.

The exact-head status must belong to the same SHA being qualified; superseded/cancelled/different-head evidence is not accepted.

## Closed-beta release boundary

The current peer closed-beta RC is frozen at:

`9acf25f7696dfe9056028e4201ca380a94883f0e`

The remaining release gate is physical Steam/game evidence on two Windows PCs/two Steam accounts. See issue #349 and the two-PC acceptance kit.

Repository branch/trunk cleanup is intentionally deferred until that physical evidence succeeds so repository administration cannot disturb the product bytes being tested.

## Stable invariants

- one current valid World state;
- at most one writable Steward authority;
- immutable revisions;
- current head advances last;
- persistent peer authority is not inferred from Steam lobby ownership;
- Steam lobby presence is not canonical membership;
- generation increments only for deliberate successful handoff;
- ordinary restart preserves generation;
- stale/ambiguous authority fails closed;
- live member removal revokes active work before canonical removal;
- Leave removes canonical access before local replica deletion;
- no generic save merging;
- no live process migration;
- no permanent Steward-owned game-server fleet;
- no required central Steward backend for normal product use;
- one Steward product distribution.