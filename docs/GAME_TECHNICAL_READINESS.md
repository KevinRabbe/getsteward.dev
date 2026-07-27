# Game Technical Readiness

Status: **CURRENT TECHNICAL EVIDENCE MAP — 19 REGISTERED FIRST-PARTY ADAPTERS; CAPABILITY, STATE-OWNERSHIP AND ENVIRONMENT-ACTIVATION BOUNDARIES RECHECKED**

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

Those are deterministic trust-boundary questions and are already covered by adapter code/tests when the native authority is known.

Real game execution is needed only when the claim itself is about execution: process replacement, native readiness, authoritative save completion, safe shutdown, client Join, firewall/NAT reachability, or cross-device continuation.

## Current catalog matrix

| Game | Proven native World/state boundary | Proven environment boundary | Current action depth | Genuine empirical or unresolved boundary still relevant |
|---|---|---|---|---|
| **Factorio** | Native ZIP save; isolated write-data/mod workspace; capture selects the newest valid non-autosave state. | Steam/standalone discovery, exact game environment and required mod/startup-settings reproduction. | Start, Host, automatic Join, native Create; no advertised user-triggered Host Stop. | Real Internet UDP 34197 reachability, actual remote Join, repeated real safe save/server-end/capture, cross-device handoff. |
| **Palworld** | Dedicated-server World directory; canonical `WorldOption.sav` remains read-only; disposable runtime INI owns only management overrides. | Client + dedicated-server discovery, exact dedicated-server build, selected native World id and server configuration. | Host + Host Stop; native direct-connect presentation; no automatic client Join. | Real two-network native Join/handoff and remaining external firewall/settings-materialization observations. |
| **7 Days to Die** | `Saves/<GameWorld>/<GameName>` plus matching `GeneratedWorlds/<GameWorld>`; exact opaque `SandboxCode` is a separate required World-specific reproduction input. | Client + dedicated-server discovery/build; isolated user-data workspace; bounded managed `serverconfig.xml`; game-native loopback-only empty-password service mode. | State/environment only; Host/Stop/Join frozen. | Actual restored-World readiness signal, minimal `shutdown` framing, long-lived process exit, final-save completion, capture/relaunch; Join separately. |
| **Project Zomboid** | Canonical multiplayer server bundle restored into adapter-owned isolated user data. | Client + dedicated-server discovery/build plus exact configured Workshop content identity. | State/environment only; Host/Stop/Join frozen. | Actual isolated dedicated-server process ownership, safe stop, no writes to live profile, capture/relaunch. |
| **Terraria** | Local vanilla top-level `.wld`; `.wld.bak`, Steam Cloud and tModLoader state excluded. | Exact Steam build; vanilla-only supported slice. | Import/state + exact game version. | None required for the current claimed slice. Runtime actions require separate evidence before promotion. |
| **Stardew Valley** | Host-owned save directory containing exactly the current same-named save + `SaveGameInfo`; `_old` recovery files excluded. | Exact Steam build; SMAPI or active Mods cause refusal. | Import/state + exact game version. | None required for the current claimed slice. Multiplayer lifecycle is a separate future capability question. |
| **Necesse** | Game-native compressed top-level World ZIP preserved byte-for-byte; uncompressed directory Worlds deliberately outside the slice. | Exact Steam build; local jar-mod directory causes refusal. Steam Workshop payload can be installed but disabled, so Workshop presence alone is not activation authority. | Import/state + exact game version. | **Deterministic environment gap, not a gameplay test:** identify Necesse's persisted enabled/load-order authority or fail closed when Workshop payload makes vanilla exactness unprovable. Tracked in #175. Dedicated-server lifecycle remains separate. |
| **Core Keeper** | Exactly three slot-matched World-owned files: world, world-info and world-generation parameters; character/map/recovery state excluded. | Exact Steam build; manual Mods, Steam Workshop, per-profile mods, and official mod.io payload/redirect surfaces cause fail-closed vanilla refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **The Planet Crafter** | Current top-level World `.json` preserved opaquely; `Backup.json` excluded. | Exact Steam build; known BepInEx bootstrap surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Satisfactory** | Steam-profile current top-level `.sav`; non-Steam profiles, backups and blueprints excluded. | Exact Steam build; active SML/mod/Workshop surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **ASTRONEER** | Current top-level `.savegame`; adjacent `.savecfg` account/custom-game state excluded. | Exact Steam build; active Mods/Paks surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. Recheck native persistence after the announced save-slot overhaul ships (#169). |
| **Enshrouded** | Bounded native index files select current data and `_info` generations; package contains only those two selectors and selected bodies. | Exact Steam build; known mod-loader/mod surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. Recheck the released 1.0 selector/generation layout after October 15, 2026 (#170). |
| **Conan Exiles Enhanced** | Current single-player/co-op slot SQLite database `game_0.db`..`game_9.db`; WAL/SHM/journal sidecars make capture unsafe and are refused. | Steam-manifest-derived install identity, exact build, active `modlist.txt` refusal. | Import/state + exact game version. | None required for the current idle-database slice. Live-database/runtime support would need separate evidence. |
| **Raft** | Current `World/<name>/<name>.rgd`; backup/history members and separate Player tree excluded. | Exact Steam build; game-root mods and roaming RaftModLoader surfaces cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **ICARUS** | One current Prospect `.json` under canonical SteamID64 profile; rolling backups and character/account/meta-inventory state excluded. | Exact Steam build; active Paks mods cause refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Smalland** | Direct `Worlds/<World>.wld`; separate `Players/*.plr` character state and root map-annotation `.sav` state are excluded. Personal Great Tree bases/tames are player-owned and portable between Worlds rather than canonical World state. | Exact Steam build; extra/linked gameplay Paks cause vanilla-exact refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **Abiotic Factor** | Complete `Worlds/<World>/` subtree, including World-owned nested multiplayer `PlayerData` and sandbox settings; profile-level state above `Worlds` excluded. | Exact Steam build; UE4SS loader surface causes refusal. | Import/state + exact game version. | None required for the current claimed slice. |
| **V Rising** | Non-cloud v4 session: latest native autosave generation + World/session metadata; older recovery generations and `ServerHostSettings.json` excluded. | Exact Steam build; BepInEx surface causes refusal. | Import/state + exact game version. | None required for the current claimed slice. Cloud/dedicated lifecycle remains outside it. |
| **Space Engineers** | Complete direct SteamID64-profile World directory requiring native core World files; top-level native `Backup` history excluded. | Exact Steam build; bounded native `Sandbox_config.sbc` Mods boundary proves vanilla/no enabled mods or fails closed. | Import/state + exact game version. | None required for the current claimed slice. Dedicated/runtime/mod depth is separate. |

## Current-code capability audit

The V4 audit re-opened the current adapter declarations instead of relying only on historical PR prose.

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

Do not create a second hard-coded capability matrix in production code or tests merely to mirror this documentation. `IGameAdapter.Capabilities` remains executable authority; this section is an audit record.

## Environment-activation audit

A later pass checked a different failure mode from save ownership: **an adapter can select the right World bytes and still overclaim exactness if it looks at the wrong mod-activation surface.**

The rule is:

```text
installed payload != necessarily active payload

when the game exposes a native activation authority
-> inspect that authority

when active vs inactive cannot yet be distinguished deterministically
-> fail closed or record the deterministic gap
-> do not ask a human to perform a ceremonial gameplay test
```

Concrete results:

- **Core Keeper:** the previous vanilla proof checked manual Mods, Steam Workshop, and per-profile mod state but missed the game's official mod.io storage. #171 closes that gap by inspecting the default mod.io store plus documented global/per-profile `RootLocalStoragePath` redirects, with bounded/fail-closed metadata handling. The adapter remains vanilla-only.
- **Necesse:** the current adapter correctly rejects local jar mods, but Steam Workshop subscriptions are a distinct client path and installed Workshop mods can be disabled. Therefore Workshop-directory presence alone is not truthful activation evidence. #175 owns the remaining deterministic question: locate the persisted enabled/load-order authority, or conservatively refuse exact vanilla proof when installed Workshop payload cannot be classified.
- **Satisfactory:** already checks both the SML/local mod surface and Steam Workshop content; no new gap found in this pass.
- **Conan Exiles Enhanced:** already uses the game-owned `modlist.txt` activation surface rather than treating arbitrary Workshop cache presence as active state.
- **Space Engineers:** already validates the World's bounded native `<Mods>` configuration boundary, which is stronger than scanning a download cache.
- **Raft:** already checks both game-root mods and the roaming RaftModLoader surface.
- **The Planet Crafter:** current BepInEx bootstrap markers remain the relevant unsupported-mod boundary.
- **Abiotic Factor:** as of July 2026 the developer still states there is no official mod support; UE4SS/third-party-loader refusal remains the correct class of boundary.

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

A manual launch of each game would only become necessary when Steward wants to claim a deeper action such as Start, Host, Stop or Join.

A narrow adapter can still have a **deterministic environment gap** without needing a gameplay test. Necesse #175 is the current example: the unresolved question is native activation metadata, not whether the game can be launched.

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

Steward therefore must **not** pull the player file into the canonical Smalland World merely because a personal Great Tree base appears while that player is present. Doing so would mix player identity/progression into shared World authority.

A future feature that deliberately transfers player-owned progression would need its own product/identity boundary. It is not missing state from the current World adapter and does not justify a gameplay test of the `.wld` capture contract.

The same ownership discipline already applies in other adapters that deliberately exclude character/account/map/meta-inventory state.

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
| Is a downloaded mod actually enabled? | Inspect the game's native activation authority when one exists; do not equate cache presence with activation. |
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
- **ASTRONEER:** current adapter remains truthful for the current build; inspect the released Save Slots/autosave-history representation after the announced save-system overhaul ships (#169).
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

The active work therefore stays evidence-driven: close deterministic trust-boundary gaps when real technical evidence exposes them; otherwise return to V3-E/V3-F external evidence rather than inventing more V4 scope.

## Final rule

> **Do not test what is already knowable. Test only the uncertainty that remains at the actual boundary of the claim.**
