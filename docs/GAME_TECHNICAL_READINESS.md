# Game Technical Readiness

Status: **CURRENT TECHNICAL EVIDENCE MAP — 19 REGISTERED FIRST-PARTY ADAPTERS; CAPABILITY, STATE-OWNERSHIP AND ENVIRONMENT-EXACTNESS BOUNDARIES RECHECKED THROUGH #192**

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
- a known unsupported mod-loader/content surface correctly causes exact-environment refusal;
- a missing required dedicated-server installation means that device cannot execute a server-dependent Host path.

Those are deterministic trust-boundary questions and should be settled from native/platform evidence plus adapter tests.

Real game execution is needed only when the claim itself is about execution: process replacement, native readiness, authoritative save completion, safe shutdown, client Join, firewall/NAT reachability, or cross-device continuation.

## Current catalog matrix

| Game | Proven native World/state boundary | Proven environment boundary | Current action depth | Genuine empirical/future boundary still relevant |
|---|---|---|---|---|
| **Factorio** | Native ZIP save; isolated write-data/mod workspace; capture selects the newest valid non-autosave state. | Steam/standalone discovery, exact game environment and required mod/startup-settings reproduction. | Start, Host, automatic Join, native Create; no advertised user-triggered Host Stop. | Real Internet UDP 34197 reachability, actual remote Join, repeated real safe save/server-end/capture, cross-device handoff. |
| **Palworld** | Dedicated-server World directory; canonical `WorldOption.sav` remains read-only; disposable runtime INI owns only management overrides. | Client + dedicated-server discovery, exact dedicated-server build, selected native World id/server configuration, and managed Host forces Pocketpair's native `-NoMods` mode so official server mods are disabled by PalServer rather than modeled by Steward. | Host + Host Stop; native direct-connect presentation; no automatic client Join. | Real two-network native Join/handoff and remaining external firewall/settings-materialization observations. |
| **7 Days to Die** | `Saves/<GameWorld>/<GameName>` plus matching `GeneratedWorlds/<GameWorld>`; exact opaque `SandboxCode` is a separate required World-specific reproduction input. | Client + dedicated-server discovery/build; isolated user-data workspace; bounded managed `serverconfig.xml`; game-native loopback-only empty-password Telnet mode with documented `shutdown`. | State/environment only; Host/Stop/Join frozen. | Restored-World readiness, real long-lived process exit after the documented Telnet stop path, final-save completion, capture/relaunch; Join separately. |
| **Project Zomboid** | Canonical multiplayer server bundle restored into adapter-owned isolated user data. | Client + dedicated-server discovery/build plus exact configured Workshop content identity; console `save` -> `quit` is the known safe-stop command path. | State/environment only; Host/Stop/Join frozen. | Actual isolated dedicated-server process ownership, real `save`/`quit` completion, no writes to live profile, capture/relaunch. |
| **Terraria** | Local vanilla top-level `.wld`; `.wld.bak`, Steam Cloud and tModLoader state excluded. | Exact Steam build; vanilla Terraria only. tModLoader is a separate product/state tree. | Import/state + exact game version. | None required for the current claimed slice. Runtime actions require separate evidence before promotion. |
| **Stardew Valley** | Host-owned save directory containing exactly the current same-named save + `SaveGameInfo`; `_old` recovery files excluded. | Exact Steam build; `StardewModdingAPI.exe`/SMAPI or active Mods cause refusal. | Import/state + exact game version. | None required for the current claimed slice. Multiplayer lifecycle is separate. |
| **Necesse** | Game-native compressed top-level World ZIP preserved byte-for-byte; uncompressed directory Worlds deliberately outside the slice. | Exact Steam build; any materialized local mod state under `%APPDATA%/Necesse/mods` fails closed. #178 also closes the pre-materialization Steam Workshop blind spot without claiming installed Workshop payload is active. | Import/state + exact game version. | None required for the current claimed slice. Dedicated-server lifecycle is separate. |
| **Core Keeper** | Exactly three slot-matched World-owned files: world, world-info and world-generation parameters; character/map/recovery state excluded. | Exact Steam build; manual Mods, Steam Workshop, per-profile mods, and official mod.io payload/redirect surfaces cause fail-closed vanilla refusal after #171. | Import/state + exact game version. | None required for the current claimed slice. |
| **The Planet Crafter** | Current top-level World `.json` preserved opaquely; `Backup.json` excluded. | Exact Steam build; current BepInEx bootstrap surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Satisfactory** | Steam-profile current top-level `.sav`; non-Steam profiles, backups and blueprints excluded. | Exact Steam build; SML/local mod and Workshop surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **ASTRONEER** | Current top-level `.savegame`; adjacent `.savecfg` account/custom-game state excluded. | Exact Steam build; Saved/Mods + Saved/Paks plus current install-root UE4SS/bootstrap and direct client PAK surfaces fail closed after #181. | Import/state + exact game version. | Current slice needs no gameplay test. Recheck native persistence after the announced Save Slots/save-system overhaul ships (#169). |
| **Enshrouded** | Bounded native index files select current data and `_info` generations; package contains only those selectors and selected bodies. | Exact Steam build; known unsupported mod-loader/mod surfaces cause refusal. | Import/state + exact game version. | Current slice needs no gameplay test. Recheck released 1.0 selector/generation layout after October 15, 2026 (#170). |
| **Conan Exiles Enhanced** | Current single-player/co-op slot SQLite database `game_0.db`..`game_9.db`; WAL/SHM/journal sidecars make capture unsafe and are refused. | Steam-manifest-derived install identity, exact build, active game-owned `modlist.txt` refusal. | Import/state + exact game version. | None required for the current idle-database slice. Live-database/runtime support would need separate evidence. |
| **Raft** | Current `World/<name>/<name>.rgd`; backup/history members and separate Player tree excluded. | Exact Steam build; game-root mods and the actual roaming RaftModLoader runtime installation cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **ICARUS** | One current Prospect `.json` under canonical SteamID64 profile; rolling backups and character/account/meta-inventory state excluded. | Exact Steam build; current `Icarus/Content/Paks/mods` activation directory must be absent/empty. | Import/state + exact game version. | None required for the current claimed slice. |
| **Smalland** | Direct `Worlds/<World>.wld`; separate `Players/*.plr` character state and root map-annotation `.sav` state are excluded. Personal Great Tree bases/tames are player-owned and portable between Worlds rather than canonical World state. | Exact Steam build; current stock client PAK names are restricted to `pakchunk0`..`pakchunk5`; extra/linked/unknown stock-like PAKs fail closed after #184. | Import/state + exact game version. | None required for the current claimed slice. |
| **Abiotic Factor** | Complete `Worlds/<World>/` subtree, including World-owned nested multiplayer `PlayerData` and sandbox settings; profile-level state above `Worlds` excluded. | Exact Steam build; UE4SS/third-party-loader surface causes refusal; developer still has no official mod support in the current audited period. | Import/state + exact game version. | None required for the current claimed slice. |
| **V Rising** | Non-cloud v4 session: latest native autosave generation + `ServerGameSettings.json` + session identity/start metadata; older recovery generations and infrastructure `ServerHostSettings.json` excluded. | Exact Steam build; current BepInEx/bootstrap surface causes refusal. | Import/state + exact game version. | None required for the current claimed slice. Cloud/dedicated lifecycle remains outside it. |
| **Space Engineers** | Complete direct SteamID64-profile World directory requiring native core World files; top-level native `Backup` history excluded. | Exact Steam build; bounded native `Sandbox_config.sbc` `<Mods>` boundary proves vanilla/no enabled mods or fails closed. | Import/state + exact game version. | None required for the current claimed slice. Dedicated/runtime/mod depth is separate. |

## Current-code capability audit

The audit re-opened current adapter declarations instead of relying only on historical PR prose.

Current executable capability shape is:

```text
Factorio
-> Mods
-> AutomaticLocalLaunch
-> AutomaticHostLaunch
-> AutomaticClientJoin
-> ExactGameVersion
-> NativeWorldCreation

Palworld
-> AutomaticHostLaunch
-> AutomaticHostStop
-> ExactGameVersion

7 Days to Die
-> Mods
-> ExactGameVersion

Project Zomboid
-> Mods
-> ExactGameVersion
-> ExactModVersions

Terraria through Space Engineers
-> ExactGameVersion only
```

For every one of the fifteen narrow adapters, the code also contains concrete installation/World discovery, environment inspection/verification, capture, preparation, restore, and finalization paths. Their lack of runtime action flags is therefore a deliberate product boundary, not absence of technical adapter work.

Do not create a second hard-coded capability matrix in production code or tests merely to mirror this document. `IGameAdapter.Capabilities` remains executable authority.

## Environment-exactness audit

A second pass checked a different failure mode from save ownership:

> an adapter can select the right World bytes and still overclaim exactness if it inspects the wrong mod/bootstrap/activation surface.

The rule is:

```text
installed payload != necessarily active payload

when the game exposes native activation authority
-> inspect that authority

when Steward cannot distinguish a potentially active external surface safely
-> fail closed on exactness
-> describe uncertainty truthfully
-> do not ask a human to perform a ceremonial gameplay test
```

### Defects found and closed

#### Core Keeper — #171

The previous vanilla proof checked manual Mods, Steam Workshop and per-profile mod state but missed current official mod.io storage.

#171 adds a bounded Core Keeper-owned guard for:

- default Windows mod.io storage;
- global `RootLocalStoragePath` redirect;
- per-local-profile redirect for mod.io game id `5289`;
- malformed/duplicate/relative/linked/unreadable metadata;
- bounded metadata/profile counts.

No mod reproduction capability was added.

#### Necesse — #178

Necesse's persisted enable/load-order authority is `%APPDATA%/Necesse/mods/modlist.data`. The existing adapter already rejected any non-empty local mods directory, so materialized mod state was already fail-closed.

The missing case was:

```text
Steam Workshop payload already installed
+ Necesse has not materialized local mod state yet
-> old vanilla proof could still see an empty local mods root
```

#178 derives `steamapps/workshop/content/1169040` from the already-known Steam appmanifest and refuses this pre-materialization ambiguity. Its error explicitly does **not** claim installed Workshop content is active.

No Necesse mod-list parser or Workshop manager was added.

#### ASTRONEER — #181

The previous vanilla proof checked only:

```text
%LOCALAPPDATA%/Astro/Saved/Mods
%LOCALAPPDATA%/Astro/Saved/Paks
```

Current modding also uses install-root UE4SS and direct PAK surfaces. #181 adds a bounded install-root guard for current UE4SS/bootstrap markers and `Astro/Content/Paks`, where current Steam owns only `pakchunk0-WindowsNoEditor.pak`.

The guard does not hash/read the multi-gigabyte stock PAK and does not become a Steam integrity verifier.

#### Smalland — #184

The previous stock-Pak heuristic accepted arbitrary numeric `pakchunkN` names even though released Smalland Windows depots consistently use chunks 0..5.

#184 narrows the existing generated regex from `[0-9]+` to `[0-5]` and directly proves `pakchunk6`/`pakchunk999` are rejected.

#### Palworld — #192

Palworld 1.0 added an official dedicated-server mod system. Steward's active managed Host path still launched `PalServer.exe` with no command-line arguments, so official server mods were not explicitly suppressed even though Palworld does not advertise a mod capability.

Pocketpair's native dedicated-server contract already provides the smaller authority: `-NoMods` forcibly disables all mods. #192 therefore changes only the active managed Host launch contract to:

```text
PalServer.exe -NoMods
```

The launch arguments are regression-tested without starting PalServer. Steward does not add a Workshop inventory, `PalModSettings.ini` parser, mod deployment/synchronization path, Palworld mod capability, or manual mod gameplay test.

### Boundaries rechecked without code changes

- **Stardew Valley:** current SMAPI installs `StardewModdingAPI.exe` in the game root even when Steam launches it or a custom `--mods-path` is used; Steward already checks that bootstrap executable before the ordinary Mods directory.
- **Satisfactory:** already checks SML/local and Workshop surfaces.
- **Conan Exiles Enhanced:** already uses game-owned `modlist.txt` activation truth rather than arbitrary Workshop-cache presence.
- **Space Engineers:** already validates the World's native `<Mods>` configuration boundary.
- **Raft:** current loader can place its launcher anywhere, but installs its actual runtime under `%APPDATA%/RaftModLoader`; Steward already checks that runtime tree plus game-root mods.
- **The Planet Crafter:** current BepInEx installation still uses the bootstrap markers Steward checks.
- **V Rising:** current mod bootstrap still uses the BepInEx/doorstop markers Steward checks.
- **ICARUS:** current client/server mods still use the lowercase `Icarus/Content/Paks/mods` directory Steward requires to be empty.
- **Abiotic Factor:** current audited developer position still has no official mod support; third-party UE4SS-loader refusal remains the right class of boundary.

This audit does **not** create a generic mod-detection framework. Each game keeps its own native truth boundary.

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

A manual launch becomes necessary only when Steward wants to claim a deeper runtime action such as Start, Host, Stop or Join.

The environment-exactness audit itself demonstrates the intended process: five real deterministic gaps were found and closed from current technical evidence without turning them into gameplay tests.

### Player-owned state is not missing World state

Some games deliberately separate portable/player-owned progression from the World.

Smalland makes that boundary especially visible:

```text
Worlds/<World>.wld
-> canonical World/open-world state for Steward's current slice

Players/*.plr
-> character-owned state
-> personal Great Tree base + associated portable tames
-> can travel with that player between Worlds

root map-annotation .sav
-> separate player/map presentation state
```

Steward must **not** pull the player file into the canonical Smalland World merely because a personal Great Tree base appears while that player is present. Doing so would mix player identity/progression into shared World authority.

A future feature that deliberately transfers player-owned progression would need its own product/identity boundary. It is not missing state from the current World adapter and does not justify a gameplay test of the `.wld` capture contract.

The same ownership discipline applies in other adapters that deliberately exclude character/account/map/meta-inventory state.

### The first four have real runtime questions because they claim or are preparing runtime depth

Factorio, Palworld, 7DTD and Project Zomboid have deeper lifecycle work. Their remaining empirical questions are narrow because most technical uncertainty has already been removed.

Examples of questions already eliminated instead of tested include:

- 7DTD `SandboxCode` is an explicit World-specific dedicated-server reproduction input; do not test whether save bytes magically make it unnecessary.
- 7DTD uses the game-native empty-password loopback-only Telnet mode and documented `shutdown`; do not invent a management credential or a custom command-framing discovery task.
- Project Zomboid's dedicated-server safe-stop command path is `save` -> `quit`; the real question is process/save completion in the managed isolated lifecycle, not command discovery.
- Palworld `WorldOption.sav` is read-only canonical input; do not reopen encoder/compressor experiments.
- Palworld managed Host delegates official server-mod suppression to native `-NoMods`; do not build a Steward Workshop/activation model or manual mod test for that boundary.
- a missing required dedicated-server installation is sufficient negative evidence that a server-dependent path cannot run on that device.

## Test-value rule

Before adding an empirical game test, classify the question:

| Question type | Default treatment |
|---|---|
| Installation/app/tool present? | Determine from platform-native metadata/discovery. |
| Which native files form the current World? | Determine from game-specific technical evidence and deterministic fixtures. |
| Which nearby files are player/account/backup/config state? | Classify technically; test capture exclusion deterministically. |
| Is downloaded mod content actually enabled? | Inspect native activation authority when it exists; do not equate cache presence with activation. |
| Is an unsupported loader/bootstrap installed? | Inspect its stable native/bootstrap markers deterministically. |
| Does a vanilla-only allowlist match real stock content? | Compare with current released platform/depot evidence and fail closed on unknown entries. |
| Is the native server stop/control command documented? | Treat the documented protocol/command as deterministic input; use real acceptance to observe lifecycle completion, not to discover alternatives by trial and error. |
| Exact game/mod/content version? | Inspect native manifests/content identity; fail closed if exactness is unavailable. |
| Can this device Host when required server tooling is absent? | No. Negative installation evidence is enough. |
| Does the real game replace/bootstrap processes? | Empirical only when session ownership depends on it. |
| What exact signal means the real server is ready? | Empirical unless a stable native protocol/documented signal already proves it. |
| Did native safe shutdown finish the authoritative save? | Empirical when file/process semantics cannot prove it beforehand. |
| Can another Internet connection reach the Host? | Empirical network boundary. |
| Does real client Join enter the intended Host? | Empirical game/network boundary. |

## Time-gated persistence candidates

Some technical boundaries should be rechecked **after a released format transition**, not manually tested now.

- **Valheim:** not currently registered. Wait for released 1.0 persistence, then inspect the final native save/server representation (#164).
- **ASTRONEER:** current adapter is truthful for the current build; inspect the released Save Slots/autosave-history representation after the announced save-system overhaul ships (#169).
- **Enshrouded:** current Early Access selector/generation boundary remains qualified; recheck released 1.0 persistence after October 15, 2026 (#170).

The rule is:

```text
native persistence contract is changing
-> do not guess the future representation
-> freeze speculative implementation
-> recheck the released representation later
```

## Relationship to V3 and V4

V3 remains the current release-evidence stage. This matrix does not make an unobserved runtime/network claim green.

V4 used this map to add one small read-only selected-game summary derived directly from existing capability truth. V4 is complete at that small boundary; it did not add a second support taxonomy, prerequisite model, backend state, or game-specific lifecycle logic.

The post-V4 technical audit did not reopen V4. It corrected five adapter-owned deterministic exactness boundaries exposed by current evidence:

```text
#171 Core Keeper official mod.io
-> #178 Necesse pre-materialization Workshop state
-> #181 ASTRONEER install-root UE4SS/direct PAKs
-> #184 Smalland exact stock PAK names
-> #192 Palworld native -NoMods managed Host
```

The active release work therefore remains V3-E/V3-F evidence. Future deterministic writes should still require a concrete demonstrated truth gap like the five above.

## Final rule

> **Do not test what is already knowable. Test only the uncertainty that remains at the actual boundary of the claim.**
