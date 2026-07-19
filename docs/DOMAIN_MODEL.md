# Domain Model

## World

`World` is the main product object.

A World represents the shared canonical playable reality for a group, independent of the particular game implementation.

Current fields:

- `WorldId`
- name
- `GameAdapterId`
- members
- current environment revision id
- current state revision id

The World stores references to its current heads rather than embedding all game data directly.

## EnvironmentRevision

An environment revision represents the reproducible game environment required to play a World state.

It contains:

- revision id
- World id
- optional parent environment revision
- creation time
- creator
- `EnvironmentManifest`
- temporary fingerprint

An environment revision changes when relevant environment requirements change, for example:

- game version
- enabled mods
- exact mod versions where known
- configuration
- launch settings

## StateRevision

A state revision represents a captured save/world state.

It contains:

- revision id
- World id
- optional parent state revision
- creation time
- creator
- adapter id
- storage package id

A normal play session should generally produce one new canonical state revision at clean session end.

## EnvironmentManifest

The manifest is the adapter-produced description of the environment.

Current structure:

- schema version
- adapter id
- game version
- components
- configuration

Each `EnvironmentComponent` contains:

- kind
- id
- optional version
- optional source
- optional metadata

Examples of components include mods, DLC, modpacks, runtime dependencies, or other adapter-defined environment elements.

The Core does not interpret game-specific component meaning. The adapter does.

## EnvironmentFingerprint

The fingerprint is SHA-256 over a canonicalized representation of the adapter-produced manifest.

It exists for fast comparison and cache decisions.

It is **not** the source of truth and does **not** imply full installation integrity.

The manifest remains authoritative.

## GameInstallation

A discovered game installation contains:

- installation id
- root path
- source
- optional adapter-owned metadata

The metadata allows an adapter to carry information such as executable paths or user-data paths without adding game-specific fields to Core types.

## DetectedWorld

A detected world is an existing game save/world that has not necessarily been imported into the product yet.

It contains:

- adapter-local id
- display name
- source path

Importing it converts adapter-owned state into a canonical `World` plus initial environment and state revisions.

## PreparedWorld

A prepared world represents an isolated local working environment ready for restore and launch.

It contains:

- the selected `GameInstallation`
- working directory
- required `EnvironmentManifest`

The working directory is intentionally separate from the original imported save wherever possible so normal product operations do not mutate the user's source save directly.

## CapturedState and StatePackage

A `CapturedState` contains:

- a `StatePackage`
- capture timestamp

A `StatePackage` points to the adapter-produced package containing the captured game state.

The package format is adapter-defined. Factorio currently uses a copied save ZIP. A future game may require a directory archive or another representation.

This is why the Core must not assume that every game state is a single file.

## GameSessionHandle

A session handle identifies a launched game session.

It currently includes:

- process id
- start time

The adapter is responsible for interpreting this handle correctly when waiting for session end. The Core must not assume that waiting on one PID is universally correct because some launchers spawn or hand off to another process.

## HostConnection

Represents the adapter-facing information needed to join a host.

Current fields:

- address
- optional port
- optional join token

Different adapters may use these fields differently.

## UserIdentity

A generic external identity:

- provider
- external id
- optional display name

This avoids hard-coding Steam identities into the World model.
