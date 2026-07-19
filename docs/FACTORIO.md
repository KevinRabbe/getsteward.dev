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
- host launch
- client launch
- adapter-owned session-end observation
- state recapture after play

This is not yet considered production-tested. The code still needs compile and runtime validation on a real machine with Factorio installed.

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

## Host launch

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

For the current Factorio implementation this can follow the launched Factorio process. The abstraction remains adapter-owned because that assumption will not be valid for every future game or launcher.

## State capture after play

After the hosted session ends, the adapter captures the prepared `world.zip` into a new state package.

The Core then creates a new `StateRevision` and moves the World's canonical state head forward.

## Current CLI workflow

The development CLI exposes:

```text
discover
import-factorio <save-name>
continue-factorio <world-id>
```

Intended use:

### Discover

```text
discover
```

Lists installed supported games and detected Factorio saves.

### Import

```text
import-factorio MySave
```

Imports the named discovered save into a new World and persists its initial environment and state revisions.

### Continue

```text
continue-factorio <world-id>
```

Loads the canonical World, prepares it, launches Factorio as host, waits for session end, captures the resulting save, and commits the next state revision.

## Known limitations

1. The branch has not yet been compile-tested with the .NET SDK in the current execution environment.
2. Runtime behavior has not yet been validated on the user's Windows machine.
3. Arbitrary custom Factorio write-data paths are not fully resolved.
4. Steam Flatpak-specific Linux paths are not yet handled comprehensively.
5. Exact automated mod synchronization is not yet wired into preparation.
6. Fully isolated per-World mod/config directories are not yet implemented.
7. Crash-safe save stabilization and recovery logic is not yet implemented.
8. Live shared host coordination is not yet implemented.

## First real-machine test checklist

1. Build the solution.
2. Run `discover`.
3. Confirm the correct Factorio installation is found.
4. Confirm expected saves are listed and autosaves are hidden.
5. Import a disposable test save.
6. Verify the original save is unchanged.
7. Inspect persisted World, environment revision, and state revision files.
8. Run Continue on the imported World.
9. Make a visible in-game change and save normally.
10. Exit Factorio cleanly.
11. Confirm a new canonical state revision is created.
12. Continue again and verify the visible change is present.

Only after this succeeds should automated mod synchronization be added to the canonical preparation path.
