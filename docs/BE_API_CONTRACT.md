# Steward Backend API Contract

Status: **CURRENT IMPLEMENTED CONTROL-PLANE CONTRACT**

This document describes the current first-release Steward Backend.Api surface. Production code remains the final source of truth for exact DTO fields and validation; this document records the stable route/authority semantics that clients rely on.

## Protocol boundary

- Base path: `/api/v1`.
- Control operations use HTTPS/JSON for remote deployments.
- Loopback HTTP remains available only for explicit local development/integration boundaries.
- Authenticated routes use `Authorization: Bearer <StewardAccessToken>`.
- Large package bytes do not travel through normal control JSON; Backend.Api authorizes direct scoped object-storage transfer.
- Resource IDs are opaque GUID-shaped Steward identifiers at the wire boundary.
- Timestamps are UTC values serialized by the ASP.NET JSON contract.
- Control-plane JSON is bounded before client deserialization; package bytes use separate size/hash boundaries.

## Response and error shapes

Most normal/domain responses use:

```json
{
  "code": "MachineReadableResult",
  "data": {},
  "retryable": false
}
```

where `data` may be absent/null depending on the result.

Typical HTTP mapping:

- `200/201` — successful logical result;
- `401` — missing/invalid Steward access credential or invalid authentication proof;
- `404` — resource is absent or intentionally indistinguishable from unauthorized;
- `409` — domain/authority conflict;
- `422` — structurally valid request with invalid domain value;
- `503` — configured external identity provider unavailable;
- `500` — unexpected internal failure, returned without stack/provider/secret details.

Unhandled/validation/provider failures use Problem Details with extensions:

```json
{
  "type": "urn:steward:problem:<token>",
  "title": "...",
  "status": 503,
  "code": "IdentityProviderUnavailable",
  "retryable": true,
  "correlationId": "..."
}
```

A correlation ID is included where the problem middleware has one available. The API never intentionally returns a stack trace, SQL/provider credentials, Steam publisher key, or plaintext Steward credential in an error.

## Authentication

### Production Steam session

```text
POST /api/v1/auth/steam/session
```

Request contains:

- Steam Web API ticket bytes encoded in the accepted request representation;
- stable Steward installation ID.

Backend verifies the ticket through the configured Steam verifier. Client-supplied Steam identity is not trusted.

Success returns normal Steward access + refresh credentials.

### Refresh normal Steward session

```text
POST /api/v1/auth/refresh
```

Request contains refresh credential + installation ID.

Successful refresh rotates the normal Steward session credentials.

### Revoke normal Steward session

```text
POST /api/v1/auth/revoke
```

Request contains refresh credential + installation ID.

Session expiry/revocation does not by itself release or transfer an unresolved writable World reservation.

### Private Friends Build proof

Only when explicitly configured/enabled:

```text
POST /api/v1/auth/friends/session
GET  /api/v1/auth/friends/identities
```

The private bootstrap credential proves one configured `friends-build` external identity and then enters the same normal Steward session machinery.

This is not a production Steam fallback and is disabled/fail-closed unless explicitly configured.

## Shared World metadata

```text
GET  /api/v1/worlds
POST /api/v1/worlds
GET  /api/v1/worlds/{worldId}
GET  /api/v1/worlds/{worldId}/current-revision
```

Semantics:

- list/get returns only Worlds visible to the authenticated caller;
- creating a shared World preserves the supplied Steward World/revision identities and establishes backend authority;
- creator becomes the initial active member and Access Manager according to the access contract;
- current-revision returns the current World metadata plus current state/environment revision metadata where present;
- a cached response is not authority to begin a new writable session; reservation acquisition revalidates current head and membership.

## Revision metadata

```text
GET  /api/v1/worlds/{worldId}/revisions/{revisionId}/state
GET  /api/v1/worlds/{worldId}/revisions/{revisionId}/environment
POST /api/v1/worlds/{worldId}/revisions/{revisionId}/environment
```

Published revision metadata is immutable.

State revision metadata includes the expected byte size/hash and any required environment revision relationship used by shared storage/materialization.

An environment may be represented by a hosted package or a native/reproducible artifact reference depending on adapter/runtime semantics.

Backend does not interpret game-specific environment/save meaning.

## Membership and access management

```text
GET  /api/v1/worlds/{worldId}/members
GET  /api/v1/invitations
POST /api/v1/worlds/{worldId}/invitations
POST /api/v1/invitations/{invitationId}/accept
POST /api/v1/invitations/{invitationId}/decline
POST /api/v1/worlds/{worldId}/members/revoke
POST /api/v1/worlds/{worldId}/leave
POST /api/v1/worlds/{worldId}/access-manager
```

Rules:

- membership is flat;
- one active member is Access Manager;
- pending invitations do not grant package/reservation/commit access;
- only the Access Manager performs the administrative operations defined by the access service;
- an active writer's revocation becomes pending instead of destroying unresolved writable responsibility;
- Access Manager has no reservation/gameplay priority.

World-access invitations are distinct from Steam/game multiplayer invitations.

## Immutable package upload

### Begin/resume logical transfer

```text
POST /api/v1/worlds/{worldId}/transfers
```

The request declares at least:

- revision ID;
- package kind (`State` or `Environment`);
- expected byte size;
- expected SHA-256;
- required environment revision where applicable.

Backend validates membership/revision relationships/bounds and creates or resolves the resumable transfer state.

The logical response includes transfer ID, part size/count, expiry, expected identity and current transfer state.

### Observe transfer progress

```text
GET /api/v1/transfers/{transferId}
```

Transfer IDs are caller/resource-scoped; this endpoint does not create cross-World enumeration authority.

### Authorize one part

```text
POST /api/v1/transfers/{transferId}/parts/{partNumber}/authorization
```

Response contains a short-lived direct object-store authorization:

- URI;
- HTTP method;
- required headers;
- expiry;
- expected part byte size.

Desktop sends the package bytes to that direct target, not through Backend.Api JSON.

### Finalize upload

```text
POST /api/v1/transfers/{transferId}/finalize
```

Backend/provider verifies the completed object against the declared immutable identity before revision publication can succeed.

Stable conflict classes include expired/inactive transfer, integrity mismatch, publication conflict/block, and missing transfer.

Object existence alone never advances the canonical World head.

## Immutable package download authorization

```text
POST /api/v1/worlds/{worldId}/revisions/{revisionId}/{kind}/download-authorization
```

`kind` resolves only the supported package kinds.

Successful response contains:

- short-lived direct transfer authorization;
- expected total byte size;
- expected SHA-256.

Desktop independently enforces secure remote transport, write-size ceiling, resumable partial bounds, and final size/hash verification before adapter restore.

## Writable reservation authority

The implemented shared authority routes are:

```text
POST /api/v1/worlds/{worldId}/reservation/acquire
GET  /api/v1/worlds/{worldId}/reservation
POST /api/v1/worlds/{worldId}/reservation/heartbeat
POST /api/v1/worlds/{worldId}/reservation/reclaim
POST /api/v1/worlds/{worldId}/reservation/commit
POST /api/v1/worlds/{worldId}/reservation/abandon
```

### Acquire

Acquisition includes:

- authenticated caller;
- installation ID;
- expected state head;
- expected environment head where applicable;
- required `Idempotency-Key`.

Representative stable outcomes:

- `ReservationAcquired`;
- `ReservationAlreadyHeldByCaller`;
- `WorldBusy`;
- `WorldUncertain`;
- `HeadChanged`;
- `WorldNotFoundOrUnauthorized`;
- `IdempotencyKeyConflict`.

Only one safe active/uncertain reservation generation may own the World.

### Get

Returns the caller-visible current reservation state. Resource absence and unauthorized access remain privacy-preserving.

### Heartbeat

A heartbeat is accepted only for the matching authenticated reservation identity/generation. Server-observed time drives authority aging.

A stale/invalidated generation cannot extend its old authority.

### Reclaim

Reclaim is deliberate. It never means “timeout automatically unlocked the World.”

It validates the current Uncertain generation/grace/membership/head conditions and atomically invalidates old authority before a later writer can acquire.

The mutation uses durable idempotency because a lost successful response must not duplicate authority transitions.

### Commit

Commit requires the exact still-valid reservation identity/generation plus the expected starting state/environment head and verified candidate relationship.

The canonical predicate remains:

```text
authorized caller
AND verified candidate
AND current head == expected head
AND exact reservation generation is still commit-eligible
-> atomically advance compatible canonical head
```

A stale head or invalidated generation never overwrites the current World.

Commit is idempotent at the authority boundary so ambiguous response loss can be resolved safely.

### Abandon before gameplay

`reservation/abandon` exists for the narrow case where exact writable authority was acquired but Core can prove gameplay never started and there is no gameplay candidate to preserve.

It is not a generic post-launch force-release route.

## Host presence

Host presence is non-authoritative Join evidence tied to the exact writable reservation generation:

```text
PUT    /api/v1/worlds/{worldId}/host-presence
GET    /api/v1/worlds/{worldId}/host-presence
DELETE /api/v1/worlds/{worldId}/host-presence/{sessionId}/{generation}
```

Publish validates reservation session/generation and caller/installation authority.

A managed Host may publish Starting/Ready plus game-owned connection material. When Ready has no explicit public address, Backend.Api may use the authenticated HTTP connection peer address. Host-presence code itself never parses arbitrary forwarded headers; deployment middleware may normalize `RemoteIpAddress` only from the one explicitly trusted proxy configuration.

Stale/uncertain/superseded reservation generations cannot replace newer Host evidence.

Join reads Host presence without acquiring a writable reservation.

## Idempotency rule

Durable idempotency is required on authority mutations where replay after an unknown network outcome could duplicate an authority transition—currently acquire, reclaim, and commit.

For one idempotency scope:

```text
same key + same logical request
-> original durable logical result

same key + different logical request
-> conflict
```

A timeout is an unknown result. The client must not infer failure and issue a logically different competing mutation.

## Security/transport rules

- remote Steward API endpoints carrying credentials use HTTPS;
- HTTP is accepted only at explicit loopback development boundaries;
- automatic redirects are disabled for credential-bearing Steam/session/direct-transfer clients;
- authenticated resources require Bearer access token validation;
- unauthorized resource existence is minimized where appropriate;
- control response JSON is bounded;
- package transfer has independent expected-size/SHA boundaries;
- Steam publisher credentials, object-store credentials, database credentials, access/refresh credentials, Friends bootstrap credentials and game join secrets are not exposed in normal API errors/logs;
- private object storage cannot be accessed merely from a guessed canonical object key through Steward authority.

## Compatibility rule

`/api/v1` is a versioned product contract.

Additive behavior is preferred. Removing or changing the meaning of an existing route/result/field requires deliberate compatibility handling rather than silently changing clients and server independently.

Exact DTO implementation lives in the `SharedWorlds.Backend.Api` source and matching client/integration tests. This document owns the stable product/authority semantics, not a parallel hand-written schema that may drift from code.