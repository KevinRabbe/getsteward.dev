# Palworld Adapter

## Purpose

Palworld is one of Steward's initial commercial validation adapters.

It stresses a different path from Factorio:

- a normal Steam client installation;
- a separate dedicated-server installation;
- directory-based World state;
- server configuration selecting one World id;
- a dedicated server process that owns the writable session;
- player identity differences between local co-op and dedicated-server play.

The adapter must keep those details out of Core.

## Validated lifecycle

The following path has been validated on a real Windows Steam installation:

```text
discover Palworld client
-> discover Palworld dedicated server
-> discover local and dedicated Worlds
-> select one local World
-> copy the World directory unchanged into the dedicated-server save layout
-> configure the server to select that World id
-> launch PalServer
-> connect through the normal Palworld client
-> preserve the original World state
```

The key result is:

> **The World format did not require a generic rewrite before dedicated hosting.**

The adapter coordinates the existing Palworld client, dedicated server, save layout, and configuration instead of teaching Core about Palworld serialization.

## Discovery

The adapter identifies:

- Palworld client installations;
- Palworld dedicated-server installations;
- local player-profile save roots;
- dedicated-server save roots;
- native World ids;
- the dedicated-server Steam app manifest when Steam exposes it;
- the server configuration used to select the active World.

When the same native World appears in local and dedicated locations, discovery must not silently assume the copies are interchangeable if they may have diverged. Source preference/deduplication remains Palworld-adapter logic and must be evidence-driven.

## Exact environment

New Palworld imports now capture the exact discovered **Palworld Dedicated Server Steam build ID** from app `2394010`'s Steam manifest into `EnvironmentManifest.GameVersion`.

Shared writable play verifies, on Windows:

- environment schema and adapter identity;
- dedicated-server hosting mode;
- safe native dedicated World id;
- dedicated-server root and executable still exist;
- the canonical environment contains an exact build ID;
- the current dedicated-server Steam manifest exposes the same build ID;
- PalServer has initialized its Windows server configuration;
- `DedicatedServerName` is present in that configuration.

A legacy Palworld environment whose canonical version is still `unknown` is deliberately **Blocked** for shared writable play. Steward does not guess that whatever PalServer happens to be installed is compatible.

Repair currently re-verifies only. Steward does not silently update PalServer or move the canonical environment revision.

Initial **Share World** also performs this exact-environment preflight before it writes the local `Shared` authority marker or creates anything remotely. A preflight failure therefore leaves the World genuinely local rather than creating an unusable half-shared World.

## Preparation and restore

For dedicated hosting, the adapter prepares the server World location and configures the server to select the intended native World id.

Canonical restore has been validated through a controlled staging/rollback flow:

```text
open canonical package
-> stage restored World
-> validate required contents
-> replace prepared server World safely
-> verify restored files against package bytes
-> reject unexpected files
-> launch PalServer from restored state
```

The original imported local source remains outside normal managed-session mutation.

## Session observation

The writable hosted session is represented by the Palworld dedicated-server process, not by one player's graphical client.

The adapter must therefore:

- launch and track `PalServer`;
- determine readiness using Palworld/server-relevant signals;
- keep the Steward World reserved while the server remains active;
- avoid treating a client exit as server-session completion;
- stop the server safely before final capture;
- capture only after the server has completed its writes.

Core must not hard-code the process name, network port, save path, or shutdown behavior.

### Current server-control direction

Palworld's current official server documentation exposes a REST management API and marks RCON deprecated. The API provides the exact small lifecycle surface Steward needs: server info/readiness, explicit World save, and graceful shutdown.

A minimal adapter-owned REST protocol client now covers:

```text
GET  /v1/api/info
POST /v1/api/save
POST /v1/api/shutdown
```

with HTTP Basic authentication and no secret values in errors.

This protocol client is **not yet evidence that Steward may safely rewrite PalWorldSettings.ini or that the REST endpoint is safely isolated on every host**. Configuration ownership, credential lifetime, endpoint exposure, readiness timing, and shutdown/save observation still require controlled Palworld acceptance evidence before they are wired into the authoritative host lifecycle.

Until that evidence exists, Steward must not replace the current conservative process behavior with an assumed REST configuration.

## Capture

The adapter captures the authoritative dedicated-server World directory into a portable package.

Validated behavior includes:

- inclusion of the required World state;
- exclusion of the server backup subtree from the canonical package;
- package restore into a clean prepared location;
- byte-for-byte verification of restored files;
- rejection of unexpected restored files;
- confirmation that required save content such as `Level.sav` is present.

Backups generated by the game may remain useful local recovery material, but they are not automatically canonical World payload.

## Canonical commit

Captured Palworld state has been committed through Steward's canonical state transaction boundary.

Required invariant:

```text
previous canonical state
-> capture candidate
-> durable immutable storage
-> atomic head advancement
```

A failed capture, store, verification, or head update leaves the previous valid state authoritative.

## Player identity limitation

Palworld may represent the same human player differently between:

- a local/co-op host save;
- a dedicated-server save;
- Steam identity;
- Palworld's native player GUID files.

World migration and player identity migration are separate problems.

Current status:

```text
World state migration: validated
Generic World format rewrite: not required
Player identity migration: unresolved Palworld-specific edge case
```

This limitation must remain inside the Palworld adapter or a validated Palworld-specific migration tool. It must not expand the universal World model.

## Product acceptance criteria

Palworld support is commercially ready only when it repeatedly proves:

1. correct client and server discovery;
2. correct World discovery without confusing duplicate sources;
3. safe import that leaves the source untouched;
4. deterministic preparation and restore;
5. reliable server launch and readiness;
6. background observation of the real server session;
7. graceful explicit save/stop and safe capture;
8. durable store, verification, and commit;
9. recovery after interrupted capture/store;
10. cross-device latest-state handoff;
11. clear handling or disclosure of player identity limitations.

## Non-goals

The Palworld adapter does not require Steward to provide:

- generic save merging;
- branch or Fork workflows;
- gameplay-semantic editing;
- permanent game-server hosting;
- ownership or party systems;
- conversion of Palworld Worlds into another game's Worlds.

Its job is to make the latest valid Palworld World state portable, temporarily hostable, safely capturable, and ready for the next session.
