# Storage

## Boundary

Durable persistence is defined by `IWorldStorage`.

The Core depends on this abstraction rather than on a specific filesystem, Steam, database, or cloud implementation.

Current responsibilities:

- save/load World metadata
- save/load environment revisions
- store/load state revision metadata
- store/open opaque state revision payloads

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

Stores mutable `World` metadata, including references to the current environment and state revision heads.

### `environments/<revision-id>.json`

Stores one immutable `EnvironmentRevision`, including its authoritative `EnvironmentManifest`.

### `states/<revision-id>/revision.json`

Stores immutable metadata for one `StateRevision`.

### `states/<revision-id>/payload.bin`

Stores the opaque adapter-produced state package belonging to that revision.

The `.bin` extension is intentionally generic. The Core does not interpret the payload format.

For the current Factorio adapter the payload content is effectively a copied Factorio save ZIP, but another adapter may produce a completely different package format.

## Publication and atomicity model

World-head metadata and immutable revisions have different write semantics.

### World metadata

`world.json` is mutable because the canonical revision heads advance over time.

The local backend writes a temporary JSON file first and replaces the destination only after serialization completes.

### Environment revisions

Environment revision IDs are immutable.

The backend writes through a temporary file and publishes it without overwrite. Attempting to store the same environment revision ID again fails rather than replacing history.

### State revisions

State revision metadata and payload are staged together in a temporary directory:

```text
<revision-id>.<random>.tmp/
  revision.json
  payload.bin
```

Only after both files are fully written is the directory moved to the final revision path:

```text
states/<revision-id>/
```

An existing final revision directory is never overwritten.

This means readers should not observe a published local state revision containing only metadata or only payload under normal operation.

The higher-level World lifecycle still performs two durable operations when committing a new state:

```text
store immutable StateRevision
-> update mutable world.json canonical head
```

That ordering is intentional. If revision storage fails, the canonical head remains unchanged. If the process stops after revision storage but before the head update, the result is an unreferenced immutable revision that can be garbage-collected or recovered later; the last canonical World remains valid.

## Immutability model

Revisions are immutable once published.

The mutable object is the World's current-head metadata:

```text
World
  CurrentEnvironmentRevisionId -> E7
  CurrentStateRevisionId       -> S144
```

Historical revisions remain addressable even when the current head changes.

This is important for:

- Restore
- Fork
- recovery
- audit/history
- debugging

The storage port exposes state revision metadata independently of payload bytes so Core can validate adapter identity and future lineage/history operations without interpreting game-specific data.

## Orphan handling

A failed multi-step operation can leave an immutable revision that no current World references. That is safer than advancing a canonical head to incomplete data.

A future maintenance subsystem should distinguish:

- referenced canonical/history revisions
- intentional Fork/Sandbox ancestry
- temporary transfer artifacts
- truly orphaned revisions eligible for garbage collection

Garbage collection must never infer deletion eligibility from age alone.

## Future remote storage

A future Steam-backed or other remote `IWorldStorage` implementation should preserve the same logical model.

Preferred properties:

- immutable revision objects
- explicit canonical head selection
- no assumption that all members destructively overwrite one shared object
- local caching
- resumable/retriable transfer
- integrity validation at trust boundaries
- idempotent publication where the remote API permits it

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
