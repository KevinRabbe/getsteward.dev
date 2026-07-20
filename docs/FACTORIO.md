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
- state restore
- local single-player launch
- host launch
- client launch
- Steam bootstrap/process-handoff tracking
- adapter-owned session-end observation
- state recapture after play

The adapter has now been compiled and exercised on a real Windows Steam installation. Real-machine discovery and import succeeded. Runtime testing exposed two adapter-specific issues in sequence:

1. Steam may restart the initially launched Factorio process, so the first PID is not always the playable session.
2. Loading a save from an arbitrary workspace path is not sufficient to isolate later save writes; Factorio's normal user-data directory still owns its saves unless `write-data` is redirected.

Both issues are now handled in the adapter. A second real-machine validation is still required before the Factorio vertical slice is considered complete.

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

The manifest is authoritative.

The environment fingerprint is only a fast comparison value derived from that manifest.

## Mod handling

Factorio already provides significant native mod synchronization behavior.

The current command-line interface includes:

- `--sync-mods FILE`
- `--mod-directory PATH`

Factorio also supports synchronizing mods with saves and multiplayer servers through its own UI. Multiplayer synchronization can download the exact server mod versions; save synchronization has additional exact-version behavior available through the game's interface.

The project should delegate as much of this behavior as practical to Factorio rather than rebuilding a second independent mod manager.

However, automated exact environment reproduction is **not yet implemented** in the adapter. The behavior of `--sync-mods` must be tested carefully before it becomes part of canonical World preparation.

For the current vertical slice, the isolated session still points `--mod-directory` at the user's existing Factorio mod directory. This preserves the currently installed mod set but means mod storage itself is not yet isolated. The adapter therefore still does **not** claim full `EnvironmentIsolation`.

References:

- [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)
- [Factorio installing mods / automatic mod sync](https://wiki.factorio.com/Installing_Mods)

## Prepared workspace and save isolation

`PrepareEnvironmentAsync` creates an adapter-owned local working directory with:

```text
<workspace>/
  config/config.ini
  user-data/saves/
```

The workspace config is based on the user's current Factorio config when available, but its `[path] write-data` value is rewritten to the adapter-owned `user-data` directory.

The canonical state package is restored into:

```text
<workspace>/user-data/saves/world.zip
```

Factorio is launched with:

```text
--config <workspace>/config/config.ini
```

This is the critical save-isolation boundary. A real-machine test proved that merely loading `world.zip` from an arbitrary external path is insufficient: a normal in-game save can still write into the default `%APPDATA%\Factorio\saves` directory. Redirecting `write-data` makes later save writes session-local instead.

After session end, state capture scans only the isolated workspace `saves` directory, ignores `_autosave*`, and captures the newest non-autosave save. This allows Factorio to preserve or change the save name internally without causing Core to read from the user's original save directory.

## Local launch

A `LocalOnly` World uses:

```text
--config <workspace-config>
--mod-directory <current-mod-directory>
--load-game <workspace-save>
```

This is the Factorio single-player launch path. It is intentionally separate from hosting.

## Host launch

A World must be explicitly marked `Shared` before the Core permits the host path.

The adapter launches Factorio using the same isolated config boundary plus:

```text
--host <workspace-save>
```

Factorio documents `--host FILE` as starting a hosted multiplayer game.

Reference: [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)

## Client launch

The adapter launches a client using the isolated config boundary plus:

```text
--mp-connect <address[:port]>
```

Reference: [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)

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

## Current CLI workflow

The development CLI exposes:

```text
discover
import-factorio <save-name>
continue-factorio <world-id>
share-world <world-id>
host-factorio <world-id>
unshare-world <world-id>
recovery
```

`continue-factorio` is local/single-player canonical play. `host-factorio` is blocked until the World has been explicitly shared.

## Known limitations

1. Arbitrary custom Factorio write-data paths are not fully resolved during discovery.
2. Steam Flatpak-specific Linux paths are not yet handled comprehensively.
3. Exact automated mod synchronization is not yet wired into preparation.
4. The mod directory is still shared with the user's current Factorio installation; full per-World environment isolation is not yet implemented.
5. The isolated write-data/save-capture fix requires real Windows runtime validation.
6. Live shared host coordination is not yet implemented.

## Real-machine test history and next validation

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
10. Hash validation showed the change went to the original `%APPDATA%\Factorio\saves\newme.zip`, while the SharedWorlds workspace payload remained unchanged.
11. That proved save isolation required a redirected Factorio `write-data` directory, not only an externally located `--load-game` file.
12. Per-session workspace config/write-data isolation and isolated save capture were added with a regression test.

Next:

13. Pull the isolated write-data fix.
14. Record the current original `newme.zip` hash as the new baseline.
15. Run local Continue on the same SharedWorlds World.
16. Make another visible in-game change and save normally.
17. Exit Factorio cleanly.
18. Confirm the original source hash remains at the baseline from step 14.
19. Confirm the newly committed SharedWorlds payload hash differs from the previous canonical payload.
20. Continue again and verify the new visible change is present.

Only after this succeeds should the Factorio local vertical slice be treated as end-to-end validated.
