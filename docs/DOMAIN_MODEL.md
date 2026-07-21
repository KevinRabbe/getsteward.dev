# Domain Model

## Purpose

The domain model exists to support one product effect:

> **One shared World. Different Steam players. Different times. No always-on game server.**

It must not grow into a social network, ownership hierarchy, gameplay-governance system, or Git-style save model.

## World

`World` is the main product object.

A World represents the current playable reality Steward manages for one game. It may remain local or become available to a trusted group through shared storage and coordination.

Current fields include:

- `WorldId`;
- display name;
- `GameAdapterId`;
- minimal authorized identities where sharing requires them;
- current environment revision id;
- current state revision id;
- `SharingMode`;
- `GameVersionPolicy`.

The access list is operational: it determines who may retrieve and advance a shared World through Steward. It is not an ownership, role, party, or governance system.

A World stores references to its current environment and state heads rather than embedding game data directly.

### WorldSharingMode

- `LocalOnly = 0`: the World remains on the current device and is not eligible for shared handoff.
- `Shared = 1`: the World is eligible for shared storage, session coordination, and temporary hosting.

`LocalOnly` deliberately has the zero value so newly imported and older persisted Worlds cannot become shared accidentally.

`Shared` does not mean publicly listed. It also does not promise that external copies can be deleted or made unique after another device receives the data.

### WorldGameVersionPolicy

Every World state belongs to one exact immutable `EnvironmentRevision`.

- `KeepExact = 0`: keep using the known-good environment.
- `AllowUpdateCandidates = 1`: a newer environment may later be offered through an explicit validated workflow.

Allowing candidates does not automatically update the game, mutate the current environment, or advance the World.

## EnvironmentRevision

An `EnvironmentRevision` describes the reproducible game environment required by a World state.

It contains:

- revision id;
- World id;
- optional previous environment revision id;
- creation time;
- `EnvironmentManifest`;
- disposable comparison fingerprint.

It changes only when relevant requirements change, such as:

- game version;
- enabled mods;
- exact mod versions where known;
- relevant configuration;
- launch requirements.

The optional previous revision link exists for compatibility, diagnostics, and recovery. It does not create a user-facing branch model.

## StateRevision

A `StateRevision` represents one completely captured and durably stored game World state.

It contains:

- revision id;
- World id;
- optional previous state revision id;
- creation time;
- adapter id;
- storage package id;
- integrity metadata where available.

A normal clean session produces one new state revision at session end.

The previous link supports commit validation, recovery, audit, and diagnostics. Steward does not merge independently modified revisions.

## EnvironmentManifest

The adapter produces the authoritative description of the required environment.

Its generic structure includes:

- schema version;
- adapter id;
- game version;
- components;
- configuration.

Components may represent mods, DLC, modpacks, runtime dependencies, or other adapter-defined requirements. Core stores them but does not interpret game-specific meaning.

## EnvironmentFingerprint

The fingerprint is a cheap comparison/cache aid computed from a canonicalized manifest.

It is not the source of truth and does not prove full installation integrity. Routine operation must not hash complete game installations.

## GameInstallation

A discovered installation contains:

- installation id;
- root path;
- source;
- adapter-owned metadata.

The metadata allows an adapter to carry executable paths, user-data locations, or launcher details without adding game-specific fields to Core.

## DetectedWorld

A `DetectedWorld` is read-only discovery metadata for an existing save or server World that has not yet been imported.

It contains:

- adapter-local id;
- display name;
- source path or adapter-owned locator.

Discovery does not upload, publish, host, or mutate anything.

Import converts the detected state into a managed `World` with initial environment and state revisions. The original source remains untouched.

## PreparedWorld

A `PreparedWorld` is an adapter-owned local workspace ready for restore and launch.

It contains:

- selected installation;
- working directory or equivalent workspace locator;
- required environment manifest;
- adapter-owned preparation metadata.

Where practical, the workspace is isolated from the original imported save.

After gameplay begins, it may contain the newest recoverable state and must not be deleted merely because a later capture or upload failed.

## CapturedState and StatePackage

A `CapturedState` describes the result of adapter capture.

A `StatePackage` points to the opaque adapter-produced payload. It may be a ZIP, directory archive, database export, or another validated representation.

Core never assumes a universal save-file shape.

The adapter explicitly states whether Core may delete the package after durable storage.

## GameSessionHandle

A `GameSessionHandle` identifies the adapter-observed session.

It may contain a process id and start time, but Core must not assume one process equals one complete session. Launchers may hand off processes, and hosted games may use separate server and client processes.

The adapter interprets the handle and determines when the session has safely ended.

## SessionReservation

A session reservation protects one shared World from competing writable Steward sessions.

It records only the operational facts needed for coordination:

- World id;
- session id;
- starting state revision;
- active device or external identity;
- local-play or hosted mode where needed;
- lifecycle state;
- recovery/expiry metadata.

It does not represent ownership of the World or permanent ownership of the host role.

## HostConnection

`HostConnection` contains adapter-facing information needed to join a currently running hosted session, such as:

- address;
- optional port;
- optional token or password reference.

Steam or the game should handle invitations and joining where possible. Steward does not build a second social system around this record.

## UserIdentity

`UserIdentity` is a minimal external identity reference:

- provider;
- stable external id;
- optional display name.

For the commercial product, Steam is the primary identity provider. The generic shape keeps platform SDK details outside Core.

## Explicitly absent concepts

The active domain model does not include:

- World ownership hierarchies;
- granular social roles;
- parties;
- public discovery;
- Git-style branches;
- merge requests;
- generic save merging;
- tracking every external copy;
- guaranteed deletion from other devices.

Those concepts do not help complete the safe World handoff and therefore do not belong in the current model.