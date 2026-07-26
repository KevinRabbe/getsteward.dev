# Storage

Status: **CURRENT — local and authenticated shared storage are both implemented.**

## Purpose

Storage exists to preserve the latest valid World state between sessions, players, devices, and time periods.

It supports the product promise:

> **One shared World. Different Steam players. Different times. No always-on game server.**

Storage is not a branching, merging, social, hosting, or ownership system.

## Boundary

Core depends on `IWorldStorage` rather than a specific filesystem, database, cloud provider, or Steam API.

The storage boundary provides the World/revision data Core needs:

- load/list World metadata;
- load/save immutable environment revisions;
- load/store immutable state revisions and opaque package bytes;
- expose the current canonical head;
- verify integrity at trust boundaries;
- preserve previously valid state when a replacement fails.

Live writable-session availability is separate and belongs to `IWorldSessionCoordinator`.

For shared Worlds, the backend transaction that advances the current head is also separate from the object store that holds package bytes.

## Current storage implementations

### Local-only Worlds

`LocalWorldStorage` stores local Steward data beneath the platform local application-data root.

A representative local layout is:

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

The exact physical layout is an implementation detail. Required semantics include:

- mutable World/current-head metadata is distinct from immutable revisions;
- environment revisions are immutable after publication;
- state revision metadata binds the stored payload/integrity information;
- opaque payload bytes are not interpreted by Core;
- persisted metadata is versioned and integrity-protected under the current persistence contract;
- path/storage identity is validated rather than trusting only valid JSON/checksum content.

A Factorio payload may be a native save ZIP. A Palworld payload may represent an archived World directory. Other adapters define their own smallest complete portable state.

### Shared Worlds

Shared Worlds already use authenticated remote storage through `StewardWorldStorage` and the Backend.Api contracts.

The logical shape is:

```text
Desktop / StewardWorldStorage
        |
        | HTTPS metadata/authority
        v
Backend.Api
        |
        +-> PostgreSQL
        |     World metadata
        |     environment/state revision metadata
        |     current canonical heads
        |     access / reservation / commit authority
        |
        `-> transfer authorization
                |
                v
          private S3-compatible object storage
                opaque immutable package bytes

Desktop <--------------------------> object storage
        direct authorized verified transfer
```

The responsibilities are deliberately split:

- **PostgreSQL/backend authority** decides which revision is published/current and whether a caller may perform the operation;
- **object storage** stores immutable opaque bytes and never advances a World head;
- **Desktop/Infrastructure** downloads/uploads through short-lived authorization and verifies expected size/hash before using the package;
- **adapters** interpret only their own game-specific package semantics at restore/capture boundaries.

This shared layer is implemented, not a future architecture milestone.

## Publication and atomicity

The universal logical ordering is:

```text
produce candidate
-> store/publish immutable candidate safely
-> verify durable candidate
-> validate expected current head + writable authority
-> advance mutable canonical World head last
```

The order must never be reversed.

If candidate storage/upload/publication fails, the current head remains unchanged.

If immutable candidate publication succeeds but canonical head advancement does not, the candidate may remain unreferenced/recovery material. That is safer than a current head pointing to incomplete or unverified bytes.

A stale expected head or invalid reservation generation cannot overwrite a newer canonical state.

## Local canonical transaction

The local implementation preserves the same safety semantics without a remote backend:

- hash/package data while bringing it into controlled storage;
- write immutable revision content before changing the World head;
- serialize conflicting local commits for one World;
- use expected-head checks;
- atomically replace mutable persisted head metadata where required;
- return `Unchanged` for an identical candidate where the contract permits;
- return `HeadChanged` for a stale writer;
- clean temporary candidate material only after durable outcome is known;
- leave the previous head authoritative on failure.

The user does not see these transaction concepts. They exist so the next session receives a complete valid World.

## Shared publication and commit

Shared storage separates package publication from canonical commit.

Conceptually:

```text
caller is authorized
-> declare candidate revision + expected size/hash
-> obtain resumable scoped upload authorization
-> upload missing parts directly to object storage
-> backend/provider verifies complete object
-> publish immutable revision metadata
-> commit through exact reservation generation + expected-head transaction
```

An uploaded object cannot make itself canonical.

A verified revision may remain non-canonical if commit fails or authority changed.

A successful canonical commit is decided transactionally by backend/PostgreSQL, not by S3 object existence, timestamps, or “newest file” heuristics.

## Verified download/cache/materialization

Shared package retrieval uses a verified local cache/materialization boundary.

Important rules:

- download authorization is scoped to an authorized immutable package;
- remote plaintext non-loopback package URLs are rejected;
- redirects are not used to silently move credentials/authorized transfer to another origin;
- download writes are bounded by the authorized package size;
- interrupted downloads may remain as bounded resumable partials;
- final byte count and SHA-256 must match before the package is opened/restored;
- disposable verified cache data has an explicit capacity/eviction policy;
- recovery candidates/workspaces are not cache entries and are never evicted merely to satisfy cache pressure.

The cache is an optimization/materialization layer, not canonical authority.

## Immutability

Published environment and state revisions are immutable.

The mutable authority is the World's current pointer:

```text
World
  CurrentEnvironmentRevisionId -> E7
  CurrentStateRevisionId       -> S144
```

Previous revisions remain addressable when required for:

- recovery;
- retention/current+prior safety policy;
- diagnostics/support investigation;
- compatibility checks;
- pinned in-flight/recovery dependencies;
- orphan/cleanup decisions.

They are not a user-facing Git history, branch graph, or merge source.

## Retention and cleanup

Cleanup is evidence/authority-driven, not age-only deletion.

The system distinguishes at least:

- current canonical revisions;
- previous canonical revisions retained by policy;
- revisions/packages pinned by active authority or recovery;
- unresolved local recovery candidates/workspaces;
- verified but uncommitted remote candidates within their grace/hold;
- partial/incomplete transfers;
- disposable verified cache entries;
- truly unreferenced cleanup-eligible data.

Current shared canonical retention normally keeps:

> **current canonical revision + previous two successfully committed canonical revisions**

with additional pinning where active recovery/transactions require older dependencies.

Unresolved local gameplay candidates are never deleted merely because time passed or retries failed.

Deletion happens after authority/durability classification, never as a shortcut for making a stuck World look Ready.

## Storage versus transfer

Durable World authority and byte transport are separate concerns.

Current implementation already uses direct authorized object transfer so large package bytes normally do not pass through Backend.Api JSON/control responses.

Future transfer optimizations—CDN, peer assistance, delta/chunk reuse, alternative replication—require measurement. Any faster transport must still preserve:

- backend authorization;
- immutable package identity;
- size/hash verification;
- canonical commit ordering;
- recovery retention.

Do not add a peer-to-peer path merely because one is possible.

## Storage versus session coordination

Do not use storage presence as proof that a World is currently active.

Do not use a live Steam lobby, Host-presence row, process, or transient peer connection as the durable copy/authority of World state.

```text
IWorldStorage
-> durable World/revision data

IWorldSessionCoordinator
-> writable-session authority

IGameAdapter
-> game/session readiness + safe-capture evidence
```

All three cooperate in a safe handoff but own different facts.

For shared Worlds, Steam identity authentication does not make session coordination “Steam storage” or “Steam locking.” The reservation generation and canonical commit live in Steward's backend authority.

## Provider boundary

The production architecture remains provider-neutral at Core/contract level:

- PostgreSQL-compatible transactional authority;
- private S3-compatible immutable package storage;
- HTTPS control plane/direct authorized transfer;
- EU-capable deployment;
- backup/restore, retention, security, and operational evidence.

The first disposable acceptance topology currently documented uses a Scaleway Instance + Caddy + managed PostgreSQL + private S3-compatible Object Storage. That is an acceptance candidate, not a provider-specific Core/storage contract.

## Explicit non-goals

Storage does not provide:

- generic save merging;
- branch/Fork graphs;
- merge conflict resolution;
- permanent host ownership;
- social roles;
- public server browsing;
- tracking or deleting every external authorized copy;
- a permanently running game server;
- object-store-based lock/authority semantics;
- speculative P2P/CDN machinery without measured need.

Its job is smaller and stricter:

> **Preserve one latest valid World state and make the exact durable state safely available to the next authorized session.**