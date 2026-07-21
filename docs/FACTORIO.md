# Factorio Adapter

## Purpose

Factorio is one of Steward's initial commercial validation adapters.

It validates:

- Steam and standalone installation discovery;
- ZIP-based save discovery and import;
- isolated write-data and mod preparation;
- exact game/mod environment checks;
- separate local, hosted, and client launch paths;
- Steam bootstrap/process handoff observation;
- clean state capture, commit, and replay.

All Factorio-specific details remain inside the adapter.

## Validated lifecycle

The local and hosted lifecycle has been exercised on a real Windows Steam installation:

```text
discover installation and save
-> import without mutating source
-> inspect exact environment
-> prepare isolated workspace
-> restore canonical save
-> launch local play or hosted Factorio
-> follow Steam process handoff
-> observe real session end
-> capture newest valid non-autosave
-> durably store new state
-> advance World head last
-> restore committed result in the next session
```

The original imported source save remained outside Steward-managed session writes.

## Installation and user-data discovery

The adapter discovers Factorio through:

- Steam library metadata, including non-default libraries;
- the current user's Steam registry location on Windows;
- conventional standalone locations;
- user-selected paths where required.

It resolves the real Factorio user-data location, including supported `config-path.cfg` and `[path] write-data` behavior, without moving or rewriting the user's live profile.

## Save discovery and import

The adapter discovers top-level `*.zip` saves and hides normal `_autosave*` entries from import selection.

Import copies the selected save into an adapter-owned state package. It does not mutate the source save.

A newly imported World starts `LocalOnly` until shared handoff is explicitly enabled.

## Environment inspection

The adapter records the minimum environment required to reproduce the World:

- exact Factorio game version;
- enabled mods;
- exact mod versions where available;
- built-in versus user-provided components;
- relevant startup-settings fingerprint where available.

`EnvironmentManifest` is authoritative. Its fingerprint is only a cheap comparison aid.

Preparation fails conservatively when the exact required game version, required mod artifact, or verified startup settings are unavailable.

## Isolated preparation

A prepared Factorio workspace uses its own configuration, mod directory, and write-data location:

```text
<workspace>/
  config/config.ini
  mods/
    mod-list.json
    mod-settings.dat
    <exact required mod artifacts>
  user-data/
    saves/
```

Factorio is launched with the workspace configuration and mod directory.

Redirecting `write-data` is essential. Loading a save from another path alone does not guarantee that later in-game saves remain outside the user's normal Factorio profile.

## Mod handling

The adapter currently:

1. reads required mod components from the environment manifest;
2. keeps built-in mods in the game installation;
3. finds exact required user-mod artifacts;
4. copies only those artifacts into the workspace;
5. generates a workspace-local `mod-list.json`;
6. verifies startup-settings state where a trusted fingerprint exists;
7. fails safely instead of mutating the live mod profile when requirements cannot be reproduced.

Factorio's native mod synchronization features should be used where they can be validated safely. Steward should not become a second universal mod manager.

Automatic acquisition or repair of missing historical game/mod versions remains adapter-hardening work and must be tested in disposable environments before becoming normal behavior.

## Local launch

Local play uses Factorio's single-player load path inside the isolated workspace.

It is intentionally separate from hosted play.

## Hosted launch

Hosted play uses Factorio's host/server behavior with the same isolated save, configuration, and mod boundaries.

A World must be eligible for shared handoff before Core permits the hosted path.

The host process owns the writable session until Factorio has ended and the adapter has reached a safe capture point.

## Client joining

The adapter supports Factorio's native multiplayer connection arguments where applicable.

Steam and Factorio should handle players and joining. Steward's shared-state work still requires remote durable storage and distributed one-writer coordination before genuine cross-device World handoff is complete.

## Session observation

Steam may terminate the initially launched Factorio bootstrap process and start a replacement Factorio process.

The adapter therefore:

- records relevant Factorio processes before launch;
- observes the initial process;
- detects immediate bootstrap exit;
- follows a newly created replacement process;
- fails conservatively when no real playable session can be proven.

Core must not assume that the first PID is the complete session.

## Capture

After a safe local or hosted session end, the adapter captures the newest valid non-autosave ZIP from the isolated workspace.

Core then:

```text
stores immutable StateRevision
-> verifies durable result
-> advances current World head last
```

A failed observation, capture, store, or commit preserves the previous valid state and the prepared workspace when recovery may be possible.

## Verify and repair

Environment verification reuses the same real preparation path used before play without launching Factorio or advancing the World.

Automatic repair remains conservative. Unsupported or unsafe repairs are reported rather than performed against the user's live installation or mod profile.

## Product acceptance criteria

Factorio support is commercially ready only when it repeatedly proves:

1. correct installation and user-data discovery;
2. safe import with source preservation;
3. exact environment preparation or clear mismatch failure;
4. isolated local play;
5. reliable hosted play where supported;
6. correct Steam process-handoff observation;
7. safe state capture;
8. durable storage, verification, and commit;
9. recovery after interruption;
10. cross-device latest-state handoff.

## Non-goals

The Factorio adapter does not require Steward to provide:

- generic save merging;
- branch or Fork workflows;
- permanent hosted servers;
- a second social or party platform;
- ownership governance;
- universal mod management.

Its job is to reproduce, launch, observe, capture, and hand off the latest valid Factorio World state.