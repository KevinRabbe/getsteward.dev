# Factorio Adapter

## Status

Factorio is the first real vertical-slice adapter.

Current implementation covers:

- installation discovery
- save discovery
- detected-save capture for import
- environment inspection
- local prepared workspace creation
- isolated session write-data configuration
- workspace-local mod-directory preparation
- exact installed user-mod artifact selection by recorded version
- generated workspace `mod-list.json`
- mod startup-settings fingerprint capture and verification for new EnvironmentRevisions
- state restore
- local single-player launch
- host launch
- client launch
- Steam bootstrap/process-handoff tracking
- adapter-owned session-end observation
- state recapture after play

The local Factorio vertical slice and explicit hosted lifecycle are **end-to-end validated on a real Windows Steam installation**. Real-machine discovery, import, Steam process handoff, isolated local play, explicit sharing, hosted launch, clean session commit, source-save isolation, canonical-head advancement, workspace cleanup, and replay of newly committed canonical state have all succeeded.

The newer workspace-local mod preparation path is implemented and covered by automated tests. It still requires a real-machine replay before it should be called runtime-validated.

Runtime testing exposed two adapter-specific issues in sequence:

1. Steam may restart the initially launched Factorio process, so the first PID is not always the playable session.
2. Loading a save from an arbitrary workspace path is not sufficient to isolate later save writes; Factorio's normal user-data directory still owns its saves unless `write-data` is redirected.

Both issues are handled in the adapter. A later hosted test also confirmed that the Steam replacement process retained `--host`, owned active UDP endpoints, committed a new canonical state revision on clean exit, left no recovery record, and did not modify the original imported save.

## Installation discovery

The adapter currently searches common Steam libraries and conventional standalone locations.

For Steam installations it:

1. finds likely Steam roots
2. parses `steamapps/libraryfolders.vdf`
3. checks each library for `steamapps/common/Factorio`
4. verifies that the Factorio executable exists

On Windows it also checks the current user's Steam registry location.

For standalone installations it checks conventional platform-specific paths.

The adapter stores discovered executable and user-data paths in adapter-owned `GameInstallation.Metadata`.

## User data

Factorio's user-data directory contains saves, mods, configuration, logs, and other player data.

Typical locations are:

- Windows: `%appdata%\Factorio`
- macOS: `~/Library/Application Support/factorio`
- Linux: `~/.factorio`

The portable ZIP distribution can keep saves and mods inside the unzipped Factorio directory.

Factorio can also be configured to use non-default write-data paths. The current discovery implementation does **not yet fully resolve arbitrary custom `write-data` configuration**, so this remains an explicit limitation.

Reference: [Factorio application directory](https://wiki.factorio.com/Application_directory)

## Save discovery

The adapter currently looks for top-level `*.zip` files in the Factorio `saves` directory.

It hides files whose names begin with `_autosave` from the normal import list.

Each discovered ZIP is represented as a `DetectedWorld`.

## Import safety

Import does not write directly into the user's original save.

The adapter copies the detected save into an adapter-owned state package. The Core then stores that package as the first canonical `StateRevision`.

This gives the product its own revision history while preserving the original imported save.

Imported Worlds default to `WorldSharingMode.LocalOnly`. Discovery or import alone never makes a save shared or hostable.

## Environment inspection

The adapter currently records:

- Factorio game version
- enabled mods from `mod-list.json`
- discovered mod versions where they can be resolved
- whether a component appears built-in or user-provided
- a SHA-256 fingerprint of `mods/mod-settings.dat` when that file exists

The manifest is authoritative.

The environment fingerprint is only a fast comparison value derived from that manifest.

For new EnvironmentRevisions, the mod-settings hash lets preparation detect startup-setting drift instead of silently borrowing changed settings from the live Factorio profile. Older EnvironmentRevisions created before this field existed retain compatibility behavior and cannot verify that drift.

## Mod handling

Factorio already provides significant native mod synchronization behavior.

The current command-line interface includes:

- `--sync-mods FILE`
- `--mod-directory PATH`

Factorio also supports synchronizing mods with saves and multiplayer servers through its own UI. Multiplayer synchronization can download exact server mod versions; save synchronization has additional exact-version behavior available through the game's interface.

The project should delegate as much of this behavior as practical to Factorio rather than rebuilding a second independent mod manager.

Current preparation is deliberately conservative:

1. create an adapter-owned workspace mod directory
2. read the required mod components from the World's EnvironmentManifest
3. leave built-in mods in the Factorio installation
4. locate each required user mod at the exact recorded version in the current Factorio mod catalog
5. copy only those exact artifacts into the workspace
6. generate a workspace-local `mod-list.json` containing only required enabled mods
7. verify and copy `mod-settings.dat` when the EnvironmentRevision contains a startup-settings fingerprint
8. fail with a controlled `EnvironmentReproductionException` when an exact required user mod or verified startup-settings file is unavailable

Unrelated mods in the user's live Factorio profile are not copied into the prepared World and are not passed to Factorio at launch.

Automatic `--sync-mods` execution is **not yet enabled**. Missing exact versions currently fail safely instead of downloading, upgrading, deleting, or otherwise mutating mod state automatically. That behavior must be tested on disposable environments before becoming canonical preparation logic.

The adapter still does **not** claim complete `EnvironmentIsolation`: preparation-time exact game-version enforcement, automatic repair/download of missing mod versions, and save-derived restoration of startup settings remain future hardening work.

References:

- [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)
- [Factorio modding / automatic mod sync](https://wiki.factorio.com/Mods)

## Prepared workspace and isolation

`PrepareEnvironmentAsync` creates an adapter-owned local working directory with:

```text
<workspace>/
  config/config.ini
  mods/
    mod-list.json
    mod-settings.dat        # when available/required
    <exact required user-mod artifacts>
  user-data/
    saves/
```

The workspace config is based on the user's current Factorio config when available, but its `[path] write-data` value is rewritten to the adapter-owned `user-data` directory.

The canonical state package is restored into:

```text
<workspace>/user-data/saves/world.zip
```

Factorio is launched with:

```text
--config <workspace>/config/config.ini
--mod-directory <workspace>/mods
```

This separates both save writes and the active user-mod catalog from the live Factorio profile for the duration of the prepared session.

A real-machine test previously proved that merely loading `world.zip` from an arbitrary external path is insufficient: a normal in-game save can still write into the default `%APPDATA%\Factorio\saves` directory. Redirecting `write-data` makes later save writes session-local instead.

After session end, state capture scans only the isolated workspace `saves` directory, ignores `_autosave*`, and captures the newest non-autosave save. This allows Factorio to preserve or change the save name internally without causing Core to read from the user's original save directory.

A real Windows runtime test confirmed the save-isolation boundary: the original source save remained at SHA-256 `9110897489D5CD73573A62DD949CBBDE5E1194972660EB5651C8D1E4DB2A879B`, while product-managed sessions advanced SharedWorlds state independently.

## Local launch

A local canonical World uses:

```text
--config <workspace-config>
--mod-directory <workspace-mod-directory>
--load-game <workspace-save>
```

This is the Factorio single-player launch path. It is intentionally separate from hosting.

## Host launch

A World must be explicitly marked `Shared` before the Core permits the host path.

The adapter launches Factorio using the same isolated config and workspace mod boundaries plus:

```text
--host <workspace-save>
```

A real-machine test confirmed that the Steam-restarted Factorio process retained this `--host` argument and owned active UDP endpoints.

Reference: [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)

## Client launch

The adapter launches a client using the isolated config boundary plus:

```text
--mp-connect <address[:port]>
```

Reference: [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)

The adapter launch primitive exists, but SharedWorlds multi-user Join is not yet implemented because remote state synchronization, membership, and live host coordination are still missing.

## Session end and Steam process handoff

The adapter owns session-end observation through `WaitForSessionEndAsync`.

A real-machine Steam test proved that the initially launched PID is not always the playable session: Steam may terminate a bootstrap process and start a replacement Factorio process. The adapter therefore:

1. tracks Factorio processes that existed before launch
2. waits on the launched PID
3. if that PID exits almost immediately, looks for a newly created Factorio replacement process
4. follows the replacement process instead of declaring the session complete
5. fails conservatively if no playable replacement can be observed

A failed bootstrap/session observation must preserve the prepared workspace rather than advance the canonical World as though gameplay completed.

## State capture after play

After a local or hosted canonical session ends, the adapter captures the newest non-autosave ZIP from the isolated workspace save directory into a new state package.

The Core then creates a new `StateRevision` and moves the World's canonical state head forward only after durable storage succeeds.

The user's original imported source save is outside the isolated write-data directory and must remain unchanged during product-managed play.

The validated hosted run advanced the World from revision `e8e0c36761934d0ca4ded2c0b125a378` to `669e8b7b10cc4b8da23a35b8f2ebd343`, left no prepared-workspace recovery record after clean shutdown, and preserved the original source-save SHA-256 baseline.

## Current CLI workflow

The development CLI exposes:

```text
discover
import-factorio <save-name>
worlds
world <world-selector>
continue-factorio <world-selector>
share-world <world-selector>
host-factorio <world-selector>
unshare-world <world-selector>
recovery
```

`continue-factorio` is local/single-player canonical play. `host-factorio` is blocked until the World has been explicitly shared.

## Known limitations

1. Arbitrary custom Factorio write-data paths are not fully resolved during discovery.
2. Steam Flatpak-specific Linux paths are not yet handled comprehensively.
3. Automatic `--sync-mods` repair/download is not yet wired into preparation.
4. Existing legacy EnvironmentRevisions without a mod-settings fingerprint cannot detect startup-setting drift.
5. Exact required Factorio game-version mismatch is not yet rejected during preparation.
6. Workspace-local mod preparation is implemented but still awaits real-machine runtime validation.
7. Live shared host coordination and genuine multi-user Join are not yet implemented.

## Real-machine validation history

Completed:

1. Build and automated tests passed on the target Windows machine.
2. Non-default Steam library discovery succeeded.
3. Expected saves were discovered and autosaves hidden from import discovery.
4. Disposable save `newme` was imported as a `LocalOnly` World.
5. Initial import preserved the original save hash.
6. First Continue exposed Steam PID handoff and produced a redundant unchanged revision rather than corrupting canonical state.
7. Process-handoff tracking was added.
8. Second Continue successfully followed the real Factorio session and loaded the World.
9. A visible in-game change was saved.
10. Hash validation showed that without `write-data` isolation the change went to the original `%APPDATA%\Factorio\saves\newme.zip`, while the SharedWorlds workspace payload remained unchanged.
11. Per-session workspace config/write-data isolation and isolated save capture were added with a regression test.
12. The fixed isolated runtime session loaded successfully and survived Steam process handoff.
13. A new visible in-game change was saved and committed from the isolated workspace.
14. The original source save remained unchanged at the recorded `91108974...` baseline.
15. No workspace recovery records remained after the successful clean session.
16. A subsequent Continue restored the newly committed canonical revision and the visible test change was confirmed present in-game.
17. `worlds` and `world <selector>` were validated on the target machine, removing raw-ID-only interaction.
18. Explicit `share-world` changed the World from `LocalOnly` to `Shared` without launching or uploading anything.
19. `host-factorio` launched the isolated World with `--host` through Steam process handoff.
20. The replacement Factorio process retained the host command line and owned active UDP endpoints.
21. Clean hosted exit committed revision `669e8b7b10cc4b8da23a35b8f2ebd343` with parent `e8e0c36761934d0ca4ded2c0b125a378`.
22. Hosted cleanup left no recovery records and the original source save remained unchanged.

The Factorio local/private and explicit hosted World lifecycles are therefore **fully end-to-end validated** for the tested Windows Steam configuration. Workspace-local mod-environment preparation is the next runtime-validation boundary.
