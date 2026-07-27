# Game Technical Readiness

Status: **CURRENT TECHNICAL EVIDENCE MAP — 19 REGISTERED FIRST-PARTY ADAPTERS**

## Purpose

This document answers one narrow question:

> **What does Steward already know about each game from code, native file/configuration boundaries, platform metadata, and deterministic tests — and what still genuinely requires a real game/network observation?**

It exists to prevent low-value manual testing.

A real-machine/game test is justified only when the claim crosses a boundary that static inspection, native documentation, deterministic adapter tests, or controlled CI cannot observe.

The evidence rule is:

```text
native/platform fact already establishes the boundary
-> encode it deterministically
-> test the boundary in CI
-> do not ask a human to rediscover it

real process/network/game behavior remains unknown
-> record the exact empirical question
-> freeze only the dependent capability
-> test that boundary later
```

This does not replace `GameAdapterCapabilities`. Executable capabilities remain the product source of truth. This file explains why the current capability shape is technically justified.

## What does not need ceremonial manual testing

For an adapter that currently claims only discovery/import/environment/state support, Steward does **not** need a person to open the game and visually confirm that:

- the Steam installation/app manifest exists where the adapter already discovers it;
- the adapter selected the exact native files/directories defined by the game-specific boundary;
- excluded backup/player/account/configuration files stayed excluded;
- capture/restore preserved those native bytes;
- a known unsupported mod-loader surface correctly causes exact-environment refusal;
- a missing required dedicated-server installation means that device cannot execute a server-dependent Host path.

Those are deterministic trust-boundary questions and are already covered by adapter code/tests.

Real game execution is needed only when the claim itself is about execution: process replacement, native readiness, authoritative save completion, safe shutdown, client Join, firewall/NAT reachability, or cross-device continuation.

## Current catalog matrix

| Game | Proven native World/state boundary | Proven environment boundary | Current action depth | Genuine empirical boundary still relevant |
|---|---|---|---|---|
| **Factorio** | Native ZIP save; isolated write-data/mod workspace; capture selects the newest valid non-autosave state. | Steam/standalone discovery, exact game environment and required mod/startup-settings reproduction. | Start, Host, automatic Join, native Create; no advertised user-triggered Host Stop. | Real Internet UDP 34197 reachability, actual remote Join, repeated real safe save/server-end/capture, cross-device handoff. |
| **Palworld** | Dedicated-server World directory; canonical `WorldOption.sav` remains read-only; disposable runtime INI owns only management overrides. | Client + dedicated-server discovery, exact dedicated-server build, selected native World id and server configuration. | Host + Host Stop; native direct-connect presentation; no automatic client Join. | Real two-network native Join/handoff and remaining external firewall/settings-materialization observations. |
| **7 Days to Die** | `Saves/<GameWorld>/<GameName>` plus matching `GeneratedWorlds/<GameWorld>`; exact opaque `SandboxCode` is a separate required World-specific reproduction input. | Client + dedicated-server discovery/build; isolated user-data workspace; bounded managed `serverconfig.xml`; game-native loopback-only empty-password service mode. | State/environment only; Host/Stop/Join frozen. | Actual restored-World readiness signal, minimal `shutdown` framing, long-lived process exit, final-save completion, capture/relaunch; Join separately. |
| **Project Zomboid** | Canonical multiplayer server bundle restored into adapter-owned isolated user data. | Client + dedicated-server discovery/build plus exact configured Workshop content identity. | State/environment only; Host/Stop/Join frozen. | Actual isolated dedicated-server process ownership, safe stop, no writes to live profile, capture/relaunch. |
| **Terraria** | Local vanilla top-level `.wld`; `.wld.bak`, Steam Cloud and tModLoader state excluded. | Exact Steam build; vanilla-only supported slice. | Import/state + exact game version. | None required for the current claimed slice. Runtime actions require separate evidence before promotion. |
| **Stardew Valley** | Host-owned save directory containing exactly the current same-named save + `SaveGameInfo`; `_old` recovery files excluded. | Exact Steam build; SMAPI or active Mods cause refusal. | Import/state + exact game version. | None required for the current claimed slice. Multiplayer lifecycle is a separate future capability question. |
| **Necesse** | Game-native compressed top-level World ZIP preserved byte-for-byte; uncompressed directory Worlds deliberately outside the slice. | Exact Steam build; local active mods cause refusal. | Import/state + exact game version. | None required for the current claimed slice. Dedicated-server lifecycle is separate. |
| **Core Keeper** | Exactly three slot-matched World-owned files: world, world-info and world-generation parameters; character/map/recovery state excluded. | Exact Steam build; known manual/Workshop/profile mod surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **The Planet Crafter** | Current top-level World `.json` preserved opaquely; `Backup.json` excluded. | Exact Steam build; known BepInEx bootstrap surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Satisfactory** | Steam-profile current top-level `.sav`; non-Steam profiles, backups and blueprints excluded. | Exact Steam build; active SML/mod/Workshop surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **ASTRONEER** | Current top-level `.savegame`; adjacent `.savecfg` account/custom-game state excluded. | Exact Steam build; active Mods/Paks surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Enshrouded** | Bounded native index files select current data and `_info` generations; package contains only those two selectors and selected bodies. | Exact Steam build; known mod-loader/mod surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Conan Exiles Enhanced** | Current single-player/co-op slot SQLite database `game_0.db`..`game_9.db`; WAL/SHM/journal sidecars make capture unsafe and are refused. | Steam-manifest-derived install identity, exact build, active `modlist.txt` refusal. | Import/state + exact game version. | None required for the current idle-database slice. Live-database/runtime support would need separate evidence. |
| **Raft** | Current `World/<name>/<name>.rgd`; backup/history members and separate Player tree excluded. | Exact Steam build; known Raft mod-loader surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **ICARUS** | One current Prospect `.json` under canonical SteamID64 profile; rolling backups and character/account/meta-inventory state excluded. | Exact Steam build; active Paks mods cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Smalland** | Direct `Worlds/<World>.wld`; player and map-annotation persistence excluded. | Exact Steam build; extra/linked gameplay Paks cause vanilla-exact refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Abiotic Factor** | Complete `Worlds/<World>/` subtree, including World-owned nested multiplayer `PlayerData` and sandbox settings; profile-level state above `Worlds` excluded. | Exact Steam build; UE4SS loader surface causes refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **V Rising** | Non-cloud v4 session: latest native autosave generation + World/session metadata; older recovery generations and `ServerHostSettings.json` excluded. | Exact Steam build; BepInEx surface causes refusal. | Import/state + exact game version. | None required for the current claimed slice. Cloud/dedicated lifecycle remains outside it. |
| **Space Engineers** | Complete direct SteamID64-profile World directory requiring native core World files; top-level native `Backup` history excluded. | Exact Steam build; bounded native config proves vanilla/no enabled mods or fails closed. | Import/state + exact game version. | None required for the current claimed slice. Dedicated/runtime/mod depth is separate. |

## What the matrix means

### Fifteen narrow adapters are already technically useful

Terraria through Space Engineers are not incomplete merely because they do not launch gameplay.

Their current product contract is:

```text
find the supported native World
-> classify World-owned vs adjacent non-World state
-> prove the supported environment boundary
-> capture native state safely
-> restore it transactionally
-> preserve exact version truth
```

That contract can be qualified without pretending that a game session occurred.

A manual launch of each game would only become necessary when Steward wants to claim a deeper action such as Start, Host, Stop or Join.

### The first four have real runtime questions because they claim or are preparing runtime depth

Factorio, Palworld, 7DTD and Project Zomboid have deeper lifecycle work. Their remaining empirical questions are narrow because most technical uncertainty has already been removed.

Examples of questions already eliminated instead of tested include:

- 7DTD `SandboxCode` is known to be an explicit World-specific dedicated-server reproduction input; do not test whether save bytes magically make it unnecessary.
- 7DTD can use the game-native empty-password loopback-only service mode; do not build/test a Steward management credential protocol.
- Palworld `WorldOption.sav` is read-only canonical input; do not reopen encoder/compressor experiments.
- a missing required dedicated-server installation is sufficient negative evidence that a server-dependent path cannot run on that device.

## Test-value rule

Before adding an empirical game test, classify the question:

| Question type | Default treatment |
|---|---|
| Installation/app/tool present? | Determine from platform-native metadata/discovery. |
| Which native files form the current World? | Determine from game-specific technical evidence and deterministic fixtures. |
| Which nearby files are player/account/backup/config state? | Classify technically; test capture exclusion deterministically. |
| Exact game/mod/content version? | Inspect native manifests/content identity; fail closed if exactness is unavailable. |
| Can this device Host when required server tooling is absent? | No. Negative installation evidence is enough. |
| Does the real game replace/bootstrap processes? | Empirical only when session ownership depends on it. |
| What exact signal means the real server is ready? | Empirical unless a stable native protocol/documented signal already proves it. |
| Did native safe shutdown finish the authoritative save? | Empirical when file/process semantics cannot prove it beforehand. |
| Can another Internet connection reach the Host? | Empirical network boundary. |
| Does real client Join enter the intended Host? | Empirical game/network boundary. |

## Valheim candidate

Valheim is intentionally **not** one of the current 19 registered adapters. The repository has already frozen implementation until the released 1.0 save representation can be rechecked rather than coding against a known transition.

That is another example of useful technical negative evidence:

```text
native persistence contract is changing
-> do not guess the future representation
-> freeze implementation
-> recheck the released representation later
```

## Relationship to V3 and V4

V3 remains the current release-evidence stage. This matrix does not make an unobserved runtime/network claim green.

It provides the foundation for a small later V4 goal: make Steward surface the technical truth it already has so users do not discover support limitations by trial and error.

V4 must derive that presentation from existing capability, installation, environment and responsibility truth. It must not create a second support taxonomy or another backend/state machine.

## Final rule

> **Do not test what is already knowable. Test only the uncertainty that remains at the actual boundary of the claim.**
