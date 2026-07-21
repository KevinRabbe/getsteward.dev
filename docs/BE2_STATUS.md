# BE-2 Current Status

Status: **COMPLETE AND GREEN**.

BE-2 replaced the provider-free identity/access/metadata portions of BE-1 with real backend application contracts and durable relational persistence while preserving the frozen BE-0 semantics.

## Implemented behavior

### Verified Steam identity

- server-side Steam Web API ticket verification;
- SteamID64 is derived from Steam's verified response, never trusted from client input;
- malformed tickets are rejected locally;
- Steam provider/transport/protocol failures remain distinct from an invalid ticket;
- publisher/Web API credentials remain server-side and are excluded from error output.

### Steward authentication sessions

- cryptographically random opaque access and refresh credentials;
- approximately 15-minute access and 30-day refresh defaults remain tunable operational values;
- only SHA-256 token hashes are persisted;
- refresh credentials are installation-bound and rotate on refresh;
- reauthentication on the same installation revokes the previous active identity session;
- explicit revocation invalidates access and refresh use;
- authentication-session expiry never releases or reassigns World writer authority.

### Shared World metadata and access

- shared World creation preserves the existing World/state/environment IDs;
- creator becomes the first active member and sole Access Manager;
- only active members receive normal World metadata access;
- World-access invitations are separate from multiplayer invitations;
- invitation acceptance creates membership atomically;
- duplicate pending invitations and reinviting existing members are rejected deterministically;
- only the Access Manager may invite, revoke, or transfer management;
- Access Manager transfer is atomic and grants no gameplay/reservation priority;
- members may leave only when they hold no unresolved writable responsibility;
- removing an active writer becomes `RevocationPending` rather than terminating or deleting its unresolved responsibility;
- pending revocation is completed only after writable responsibility is resolved.

### State and environment revision metadata

- immutable state/environment metadata is scoped by `(WorldId, RevisionId)`;
- state metadata may reference a required environment revision;
- required environment metadata must already exist;
- environment metadata supports either Steward-hosted package integrity metadata or an opaque reproducible/native artifact reference;
- immutable replay is idempotent while conflicting reuse is rejected;
- normal revision metadata queries require active World membership;
- package bytes are not stored in the relational database and backend code does not interpret game-save contents.

## PostgreSQL persistence

BE-2 persistence lives in `SharedWorlds.Backend.PostgreSql` using Npgsql and PostgreSQL-compatible semantics without selecting a cloud vendor.

Persisted records include:

- shared Worlds;
- flat World members;
- World-access invitations;
- state revision metadata;
- environment revision metadata;
- Steward authentication sessions;
- Steward access-credential hashes.

Important persistence invariants:

- World creation + first Access Manager membership are one transaction;
- revision keys remain World-scoped;
- state-to-environment compatibility is reinforced by a composite foreign key;
- access mutations serialize on the owning World row;
- invitation acceptance + membership creation are atomic;
- Access Manager transfer validates expected current authority inside the transaction;
- one non-revoked Steward authentication session exists per installation;
- same-installation authentication/refresh mutation is serialized;
- plaintext access/refresh credentials never enter PostgreSQL.

## Validation evidence

Latest BE-2 persistence validation: GitHub Actions run `29866444653`.

All required gates passed:

- Quality / formatter verification;
- Ubuntu Release build + tests;
- Windows Release build + tests;
- live PostgreSQL integration tests using an isolated PostgreSQL service.

The PostgreSQL integration suite covers World/member atomicity, revision immutability/scoping, invitation/access administration, pending revocation, manager transfer, member leave, session token hashing, refresh rotation, installation binding, reauthentication, revocation, and expiry behavior.

## Explicit BE-2 boundary

BE-2 does **not** implement:

- package-byte upload/download;
- object-store signed/scoped transfer authorization;
- multipart/resumable object transfer;
- object integrity finalization;
- canonical-head compare-and-swap commit;
- distributed World reservation/heartbeat/reclaim authority.

Those remain separate milestones so identity/access metadata cannot accidentally become package or writer authority.

## Next backend milestone

**BE-3 — Immutable object transfer**.

BE-3 starts from a provider-neutral immutable object-storage/transfer boundary and then implements authorized upload/download, resumable transfer, exact size/hash verification, immutable publication, partial/orphan cleanup, and retention integration. Canonical-head commit and distributed writer coordination remain BE-4.
