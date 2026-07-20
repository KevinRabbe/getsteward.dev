# Factorio Adapter

## Status

Factorio is the first real vertical-slice adapter.

Current implementation covers:

- installation discovery
- save discovery
- detected-save capture for import
- environment inspection
- local prepared workspace creation
- state restore
- local single-player launch
- host launch
- client launch
- adapter-owned session-end observation
- state recapture after play

The adapter has now been compiled and exercised on a real Windows Steam installation. Real-machine discovery and import succeeded. The first local Continue attempt exposed a Steam bootstrap/process-handoff bug before the save could load; the adapter now launches from Factorio's executable directory and treats rapid Steam process replacement as a launcher handoff rather than a completed game session.

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

References:

- [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)
- [Factorio installing mods / automatic mod sync](https://wiki.factorio.com/Installing_Mods)

## Prepared workspace

`PrepareEnvironmentAsync` creates an adapter-owned local working directory.

The canonical state package is restored into that workspace as `world.zip`.

The goal is to keep product-managed play isolated from the original imported source save.

Environment isolation is not yet complete because the adapter currently does not create a fully isolated Factorio user-data/mod directory per World.

## Local launch

A `LocalOnly` World uses:

```text
--load-game <save-file>
```

This is the Factorio single-player launch path. It is intentionally separate from hosting.

The Steam build must be launched with the Factorio executable directory as its process working directory. Launching the executable from the installation root can cause Steam's restart/bootstrap behavior, where the first process exits before the playable game process exists.

## Host launch

A World must be explicitly marked `Shared` before the Core permits the host path.

The adapter launches Factorio using:

```text
--host <save-file>
```

Factorio documents `--host FILE` as starting a hosted multiplayer game.

Reference: [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)

## Client launch

The adapter launches a client using:

```text
--mp-connect <address[:port]>
```

Reference: [Factorio command line parameters](https://wiki.factorio.com/Command_line_parameters)

## Session end

The adapter owns session-end observation through `WaitForSessionEndAsync`.

A real-machine Steam test proved that the initially launched PID is not always the playable session: Steam may terminate a bootstrap process and start a replacement Factorio process. The adapter therefore:

1. launches Factorio from the executable directory to avoid unnecessary Steam restart behavior
2. tracks Factorio processes that existed before launch
3. waits on the launched PID
4. if that PID exits almost immediately, looks for a newly created Factorio replacement process
5. follows the replacement process instead of declaring the session complete
6. fails conservatively if no playable replacement can be observed

A failed bootstrap/session observation must preserve the prepared workspace rather than advance the canonical World as though gameplay completed.

## State capture after play

After a local or hosted canonical session ends, the adapter captures the prepared `world.zip` into a new state package.

The Core then creates a new `StateRevision` and moves the World's canonical state head forward only after durable storage succeeds.

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

1. Arbitrary custom Factorio write-data paths are not fully resolved.
2. Steam Flatpak-specific Linux paths are not yet handled comprehensively.
3. Exact automated mod synchronization is not yet wired into preparation.
4. Fully isolated per-World mod/config directories are not yet implemented.
5. The Steam process-handoff fix still requires a second real Windows runtime validation.
6. Live shared host coordination is not yet implemented.

## Real-machine test checklist

Completed:

1. Build the solution on the target Windows machine.
2. Run `discover`.
3. Confirm the non-default Steam library installation is found.
4. Confirm expected saves are listed and autosaves are hidden from import discovery.
5. Import disposable save `newme`.
6. Confirm imported World defaults to `LocalOnly`.
7. Record the original save SHA-256 before product-managed play.

Next:

8. Pull the Steam bootstrap/session-handoff fix.
9. Close any already-running Factorio process.
10. Run local Continue again.
11. Confirm the prepared `world.zip` loads successfully.
12. Make a visible in-game change and save normally.
13. Exit Factorio cleanly.
14. Confirm a new canonical state revision is created.
15. Confirm the original source save SHA-256 is unchanged.
16. Continue again and verify the visible change is present.

Only after this succeeds should automated mod synchronization be added to the canonical preparation path.
