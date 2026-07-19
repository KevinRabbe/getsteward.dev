# Storage

## Boundary

Durable persistence is defined by `IWorldStorage`.

The Core depends on this abstraction rather than on a specific filesystem, Steam, database, or cloud implementation.

Current responsibilities:

- save/load World metadata
- save/load environment revisions
- store state revisions with their payload
- open a stored state revision payload for restore

Live session state is deliberately not part of this boundary. That belongs to `IWorldSessionCoordinator`.

## Current implementation: LocalWorldStorage

`LocalWorldStorage` is the first persistence backend.

The development CLI creates it under the platform local application-data root:

```text
<LocalApplicationData>/SharedWorlds/data
```

On Windows, `LocalApplicationData` normally resolves to the current user's local AppData directory.

## Current layout

```text
SharedWorlds/
  data/
    worlds/
      <world-id>/
        world.json
        environments/
          <environment-revision-id>.json
        states/
          <state-revision-id>/
            revision.json
            payload.bin
```

### `world.json`

Stores the current `World` metadata, including references to the current environment and state revision heads.

### `environments/<revision-id>.json`

Stores one immutable `EnvironmentRevision`, including its manifest and fingerprint.

### `states/<revision-id>/revision.json`

Stores metadata for one `StateRevision`.

### `states/<revision-id>/payload.bin`

Stores the opaque adapter-produced state package.

The `.bin` extension is intentionally generic. The Core does not interpret the payload format.

For the current Factorio adapter the payload content is effectively a copied Factorio save ZIP, but another adapter may produce a completely different package format.

## Atomic writes

`LocalWorldStorage` writes JSON metadata through temporary files and then moves the completed temporary file over the destination.

State payloads are also written to a temporary file before being moved into place.

This reduces the chance of leaving a partially written canonical file after an interrupted write.

It does not yet provide full transactional semantics across multiple files. For example, storing a new state revision and then advancing `world.json` are separate operations.

Crash-hardening and transactional commit semantics remain future work.

## Immutability model

Revisions should be treated as immutable once committed.

The mutable object is the World's current-head metadata:

```text
World
  CurrentEnvironmentRevisionId -> E7
  CurrentStateRevisionId       -> S144
```

Historical revisions should remain addressable even when the current head changes.

This is important for:

- Restore
- Fork
- recovery
- audit/history
- debugging

## Future remote storage

A future Steam-backed or other remote `IWorldStorage` implementation should preserve the same logical model.

Preferred properties:

- immutable revision objects where practical
- explicit canonical head selection
- no assumption that all members destructively overwrite one shared object
- local caching
- resumable/retriable transfer
- integrity validation at trust boundaries

The exact Steam UGC/Workshop object model must be tested with multiple real accounts before it becomes a permanent storage design.

## Storage versus transport

Durable storage and fast transfer are separate concerns.

A future design may use:

- durable backend for canonical history
- direct P2P transfer for fast synchronization of the newest state

Using P2P for speed does not remove the need for durable canonical storage.

## Storage versus session coordination

Do not use storage presence alone as proof that a World is currently hosted.

Likewise, do not use a live lobby as the only durable copy of World state.

The two lifecycles are different:

```text
IWorldStorage
-> durable history

IWorldSessionCoordinator
-> transient host/session ownership
```
