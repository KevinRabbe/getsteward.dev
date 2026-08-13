# Architecture

Status: **CURRENT — peer-hosted SafeWorld product architecture.**

## Product kernel

The product is the **World**.

> **One shared World. Different Steam players. Different times. No always-on game server.**

A World is the latest valid playable state plus the environment information required to reproduce it. SafeWorld moves responsibility for that World between trusted Steam players and PCs while preserving one writable authority and one canonical history.

Steam is the platform. Games are adapters. SafeWorld owns World continuity.

The central code rule is:

> **Core knows what must happen. Adapters know how a specific game makes it happen.**

Core must not contain game-name branches merely because one adapter has unusual runtime or save semantics.

## Normal product topology

Ordinary SafeWorld use is peer-hosted. There is no permanent central SafeWorld backend required for normal Host, Join, access management, handoff, or Leave behavior.

```text
SafeWorld.Desktop.exe
   |
   +-- SharedWorlds.Core
   |      universal lifecycle/revision/safety contracts
   |
   +-- SharedWorlds.Infrastructure
   |      local durable storage
   |      peer authority fences
   |      bootstrap/catch-up
   |      membership/removal/Leave
   |      generation-fenced session coordination
   |
   +-- SharedWorlds.Desktop
   |      Windows UI/background runtime
   |      one process-lifetime Steam runtime
   |      private lobby Host/Join
   |      peer World/control transport
   |      game traffic bridge
   |
   `-- SharedWorlds.GameAdapters/*
          game-specific behavior
```

There is one SafeWorld product distribution. If no Host is active, the World is inactive.

## Dependency direction

```text
Windows Desktop / background runtime / engineering tools
                    |
                    v
             SharedWorlds.Core
              |             |
              |             `-> session/lifecycle contracts
              |
              `-> IGameAdapter
                     `-> independently compiled game-specific adapters

SharedWorlds.Infrastructure
  implements peer/local coordination and durable persistence around Core contracts

SharedWorlds.Desktop
  composes Steam platform/transport behavior around Infrastructure/Core
```

Core does not depend on Steamworks, Desktop, launcher SDKs, or concrete games.

## Three authority layers

SafeWorld separates durable World authority, temporary Steam platform state, and game/session evidence.

### 1. Durable World authority

A peer-shared World contains persistent authority:

```text
WorldPeerAuthority
  Holder: UserIdentity
  Generation: ulong > 0
```

Rules:

- Local Worlds have no peer authority;
- explicit Share creates generation 1 only after durable World/fence preparation succeeds;
- ordinary Host stop/restart preserves the same generation;
- successful deliberate handoff increments generation exactly once;
- stale or conflicting generation evidence fails closed;
- no live Host means the World is inactive.

### 2. Steam platform state

Steam provides:

- SafeWorld distribution/update;
- Steam identity;
- friend enumeration;
- private lobby creation;
- invites and lobby discovery;
- peer networking transport.

A Steam lobby is **not** canonical World membership or persistent World authority.

Before writable, transfer, or game operations, SafeWorld requires temporary Steam state to agree with durable peer authority.

### 3. Game/session evidence

`IGameAdapter` owns facts such as:

- installation and World discovery;
- environment inspection/preparation;
- state restore/capture;
- local/Host/Join launch behavior;
- server/client process lifecycle;
- readiness;
- safe stop/save boundary;
- capture validity.

Neither durable authority nor Steam presence may invent game-specific readiness/capture truth.

## Local -> Shared cutover

Sharing is explicit.

```text
Local World
-> capture/validate current durable head
-> persist Shared membership/authority prerequisites
-> create durable authority fence
-> publish WorldPeerAuthority(holder = local Steam identity, generation = 1)
-> World is peer-shared
```

Authority is published only after the required durable state exists.

## Host lifecycle

Only the current persistent authority holder may host a peer World as writable authority.

Managed Host performs conceptually:

```text
load canonical World
-> validate holder + generation + current state
-> validate durable authority fence
-> create private Steam lobby
-> bind lobby metadata to World + generation
-> launch/observe managed game Host through adapter
-> publish Ready presence only when actually usable
-> invite canonical Steam members
-> serve peer World/control traffic
-> bridge admitted game traffic
-> on stop/end: safe save/capture
-> store immutable revision
-> verify
-> advance current World head
-> finalize managed-host presence/lobby state
```

The Host PC contains the live authoritative save state while the World is active.

## Join/bootstrap/catch-up

A member may join from a Steam invitation or lobby target.

The Join path conceptually performs:

```text
resolve World + generation + holder
-> confirm Steam lobby owner against persistent authority
-> authenticate remote Steam identity
-> confirm canonical membership
-> bootstrap or catch up exact World state
-> verify revision/package integrity
-> prepare local game environment
-> launch graphical Join client
-> connect through the SafeWorld bridge
```

The graphical game client uses the player's normal game profile where the adapter requires player identity/preferences. SafeWorld must not replace language, account identity, controls, or equivalent player-profile state merely to isolate authoritative server data.

For games such as Factorio, authoritative dedicated-server write-data may be isolated while the graphical Host/Join client uses the player's normal profile.

## Peer networking

SafeWorld separates World/control traffic from bridged game traffic.

Authorization is bound to the current World, authority generation, authenticated Steam identity, canonical membership, and current Host state.

Removing a member closes only the matching live sessions for that World/generation/member. Whole-session retirement belongs to managed Host end, not an individual member kick.

## Safe host handoff

Host handoff is not live process migration.

```text
request next Host
-> serialize authority mutation
-> stop outgoing Host safely
-> final save/capture
-> commit exact final revision
-> transfer/verify that exact revision
-> target activates durable generation N+1
-> acknowledge activation
-> move temporary Steam lobby ownership last
```

The protocol rechecks membership and presence immediately before authority mutation.

## Membership and revocation

Canonical membership is stored on the World and controlled by the current persistent authority holder.

### Add person

The holder selects a Steam friend. SafeWorld persists canonical membership before relying on the Steam invitation flow.

### Remove access

For a live World, removal is fail-closed and ordered:

```text
revoke exact (World, generation, member)
-> cancel matching World/control work
-> close matching game sessions
-> persist canonical membership removal
-> verify holder/generation/current head stayed coherent
```

A failed or ambiguous persistence step must not silently reopen access.

### Leave World

A non-holder requests canonical removal from the current authority holder before deleting its local replica.

The current holder must hand off authority before using the normal member Leave path.

## State safety

SafeWorld keeps the same core invariants regardless of adapter:

- one current valid World state;
- at most one writable authority;
- immutable published revisions;
- canonical head advances only after replacement bytes are captured, stored, verified, and committed;
- failed work preserves the previous valid state and recovery evidence;
- adapters own game-specific lifecycle/save/capture truth;
- no generic save merging;
- no live process/memory migration.

## Windows application lifetime

SafeWorld is a normal installed Windows desktop application.

The public executable is `SafeWorld.Desktop.exe`.

Managed child processes that belong to a SafeWorld-controlled session must not survive SafeWorld unexpectedly when the adapter/runtime contract says they are owned by SafeWorld.

Public beta/release does not require a command-script launcher.

## Steam package boundary

New SafeWorld packages use `safeworld-steam.json` and may accept `SAFEWORLD_STEAM_APP_ID` as an environment override.

Public beta/release must not ship development `steam_appid.txt` or use AppID 480.

Older configuration names may be read only as explicit compatibility inputs. New SafeWorld beta packages must not emit them.

## Installer boundary

The SafeWorld installer owns application files and Windows integration, not user World data.

Uninstall must:

- remove package-owned application files;
- remove SafeWorld shortcuts/registration;
- preserve unrelated files in the installation directory;
- preserve World data outside the installation directory.

In-place upgrade may remove known package-owned legacy configuration files when needed to prevent ambiguous runtime configuration.

## Release qualification

A source revision is qualified only when all required checks pass on that exact revision.

The public-beta path validates:

- product build/tests;
- Windows acceptance;
- installer construction;
- installed launch boundary;
- uninstall/data preservation;
- exact-head aggregation.

The real release candidate additionally uses the real SafeWorld Steam AppID and final Windows code signing before the physical two-PC beta test.

## Final architectural invariant

```text
one active peer Host
-> one authoritative live state
-> one writer
-> stop/save before authority moves
-> Steam discovery/connectivity
-> no permanent central SafeWorld authority
```
