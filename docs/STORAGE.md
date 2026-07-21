# Storage

## Purpose

Storage exists to preserve the latest valid World state between sessions, players, devices, and time periods.

It must support the product promise:

> **One shared World. Different Steam players. Different times. No always-on game server.**

Storage is not a branching, merging, social, or ownership system.

## Boundary

Durable persistence is defined by `IWorldStorage` and the canonical state commit boundary.

Core depends on storage abstractions rather than a specific filesystem, database, cloud provider, or Steam API.

Durable responsibilities include:

- save and load World metadata;
- save and load environment revisions;
- store and load state revision metadata;
- store and open opaque state payloads;
- preserve immutable published revisions;
- advance the current World head only after successful storage;
- validate integrity at trust boundaries.

Live session availability is separate. It belongs to `IWorldSessionCoordinator`.

## Local filesystem implementation

The current local backend stores data under the platform local application-data root.

A representative layout is:

```text
<LocalApplicationData>/SharedWorlds/data/
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

The exact layout is an implementation detail, but these semantics are required:

- `world.json` contains mutable current-head metadata;
- environment revisions are immutable after publication;
- state revision metadata and payload are published together;
- opaque payload bytes are never interpreted by Core.

For Factorio, the payload may be a copied save ZIP. For Palworld, it may represent an archived World directory. Other adapters may use different portable package formats.

## Publication and atomicity

A state commit has two logical stages:

```text
store immutable candidate revision
-> advance mutable World head
```

The order must never be reversed.

If candidate storage fails, the current head remains unchanged.

If candidate storage succeeds but head advancement fails, the result may be an unreferenced immutable candidate. That is safer than a head pointing to incomplete or missing data.

The canonical commit implementation additionally protects head advancement with an expected-head check. A stale writer receives `HeadChanged` instead of overwriting a newer result.

## Canonical state transaction

The filesystem canonical state store follows these rules:

- hash the package while copying it into controlled storage;
- store immutable revision content keyed by its content identity;
- serialize concurrent commit attempts for one World;
- durably write candidate data before touching the head;
- atomically replace `head.json` or equivalent current-head metadata;
- return `Unchanged` for an identical candidate;
- return `HeadChanged` when the caller started from a stale head;
- delete a temporary candidate only after a durable committed or unchanged result;
- leave the previous head authoritative on any failure.

The user does not see these transaction concepts. They exist to guarantee that the next player receives a complete valid state.

## Immutability

Published environment and state revisions are immutable.

The mutable data is the World's current pointer:

```text
World
  CurrentEnvironmentRevisionId -> E7
  CurrentStateRevisionId       -> S144
```

Previous revisions remain addressable for:

- recovery;
- diagnostics;
- audit and support investigation;
- compatibility checks;
- safe rollback when explicitly required;
- orphan detection and retention decisions.

They are not a user-facing Git history, branch graph, or merge system.

## Orphans and retention

Interrupted operations may leave immutable revisions not referenced by the current World head.

A maintenance subsystem must distinguish:

- the current referenced revision;
- retained recovery or diagnostic revisions;
- temporary transfer artifacts;
- truly unreferenced data eligible for cleanup.

Deletion eligibility must never be inferred from age alone. Recovery evidence and user-owned data must be preserved conservatively.

## Shared durable storage

The next commercial storage milestone is a shared implementation that lets another trusted Steam identity or device retrieve the latest state.

Required properties:

- immutable state objects;
- explicit current-head metadata;
- resumable and retryable transfer;
- integrity validation;
- idempotent publication where possible;
- local caching;
- conservative failure behavior;
- support for the same expected-head commit rule across devices.

The exact Steam storage mechanism must be validated with real accounts and real state sizes before becoming permanent architecture.

## Storage versus transfer

Durable storage and fast transfer are separate concerns.

A future implementation may combine:

- durable shared storage for the current valid state and recovery;
- direct peer-to-peer transfer for speed;
- local cache to avoid repeated downloads.

A fast transport path must not bypass durable commit and integrity rules.

## Storage versus session coordination

Do not use storage presence as proof that a World is currently active.

Do not use a live Steam lobby or transient peer connection as the only durable copy of World state.

```text
IWorldStorage
-> durable World state

IWorldSessionCoordinator
-> transient one-writer reservation
```

Both are needed for a safe two-device handoff.

## Explicit non-goals

Storage does not provide:

- generic save merging;
- branch or Fork graphs;
- merge conflict resolution;
- permanent host ownership;
- social roles;
- tracking or deleting every external copy;
- a permanently running game server.

Its job is smaller and stricter: preserve one latest valid shared World state and make it safely retrievable for the next session.