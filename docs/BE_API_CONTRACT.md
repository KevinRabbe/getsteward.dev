# Steward Backend API Contract

This is the proposed first-release wire contract for the Steward control plane.
It is provider-neutral and remains planning documentation until the master lock
is lifted.

## Protocol boundary

- Base path: `/v1`.
- Control operations use HTTPS with JSON request and response bodies.
- Authentication uses a short-lived Steward access token created after the
  backend validates a Steam session ticket.
- Large package bytes do not travel through normal JSON endpoints. The API
  authorizes a short-lived, scoped transfer target for object storage.
- All timestamps are UTC ISO 8601 values. All ids are opaque strings.
- Clients must send a correlation id. Mutating operations must also send an
  idempotency key.

## Common headers

| Header | Required | Meaning |
|---|---|---|
| `Authorization` | authenticated routes | `Bearer <StewardAccessToken>` |
| `X-Correlation-Id` | all requests | caller-generated diagnostic id |
| `Idempotency-Key` | retryable mutations | stable key for one logical operation |
| `If-Match` | optional metadata reads/commands | expected resource version when supplied |

The server returns its correlation id in every response. Correlation ids and
idempotency keys must not contain secrets or save contents.

## Error envelope

Expected failures use one stable shape:

```json
{
  "type": "https://api.getsteward.dev/problems/head-changed",
  "title": "World head changed",
  "status": 409,
  "code": "HeadChanged",
  "detail": "The candidate started from an older current state.",
  "correlationId": "corr_opaque",
  "retryable": false,
  "currentHead": "rev_opaque",
  "safeState": "CurrentHeadPreserved"
}
```

`detail`, `currentHead`, and resource existence must be minimized or omitted
when revealing them would disclose unauthorized World information. Stable codes
include:

`Unauthorized`, `Forbidden`, `NotFound`, `InvalidMetadata`, `LimitExceeded`,
`AlreadyReserved`, `HeadChanged`, `ReservationMismatch`, `RecoveryRequired`,
`RecoveryNotReady`, `InvalidCandidate`, `IntegrityMismatch`, `Expired`,
`IdempotencyConflict`, `DependencyUnavailable`, and `Conflict`.

## Idempotency rules

1. The server stores the caller, operation, key, request fingerprint, and final
   response before treating a retryable mutation as complete.
2. Repeating the same key and identical request returns the original result.
3. Reusing a key with a different request returns `IdempotencyConflict`.
4. A timeout is unknown outcome, not automatic failure. The client queries the
   operation/result before issuing a new mutation.
5. Idempotency records remain long enough to cover the operation retry window and
   are retained longer for commit/recovery diagnostics where required.

## Resource shapes

### World summary

```json
{
  "worldId": "world_opaque",
  "adapterId": "factorio",
  "displayName": "Shared World",
  "access": "Member",
  "state": "ReadyShared",
  "currentEnvironmentRevisionId": "env_opaque",
  "currentStateRevisionId": "rev_opaque",
  "lastCommittedAt": "2026-07-21T12:00:00Z",
  "reservation": {
    "state": "Available",
    "holderDisplayName": null,
    "hostReady": false
  }
}
```

`state` is a backend summary for UI/runtime mapping, not a replacement for
adapter lifecycle state. A cached summary must identify that it is not
authoritative for starting a shared writable session.

### Reservation

```json
{
  "sessionId": "session_opaque",
  "generation": 42,
  "worldId": "world_opaque",
  "startingStateRevisionId": "rev_opaque",
  "holderSteamId": "steam_opaque",
  "deviceId": "device_opaque",
  "mode": "Host",
  "state": "Active",
  "heartbeatDeadline": "2026-07-21T12:00:30Z",
  "recoveryGraceUntil": null
}
```

Holder identity is returned only to authorized World members and must be
redacted in logs and unauthorized responses.

### Revision metadata

```json
{
  "revisionId": "rev_opaque",
  "worldId": "world_opaque",
  "kind": "State",
  "adapterId": "factorio",
  "previousRevisionId": "rev_previous",
  "sha256": "base64url_digest",
  "byteSize": 123456789,
  "publication": "Verified",
  "createdAt": "2026-07-21T12:00:00Z"
}
```

Revision metadata is immutable after `Verified`. The current World head is the
only mutable pointer.

## Control operations

### Authenticate

```text
POST /v1/auth/steam/session-ticket
```

Request: Steam session ticket and client metadata needed for device binding.
Response: short-lived Steward access token, refresh handle, verified Steam
identity summary, and token expiry. The Steam ticket is not retained after
validation except for bounded security diagnostics where necessary.

### Acquire reservation

```text
POST /v1/worlds/{worldId}/reservations
```

Request:

```json
{
  "expectedStateRevisionId": "rev_opaque",
  "deviceId": "device_opaque",
  "mode": "Local"
}
```

Response: reservation metadata including session id, generation, starting head,
heartbeat deadline, and retry guidance. The operation succeeds only if the
caller is authorized, the expected head is current, and no safe reservation is
active.

### Heartbeat

```text
POST /v1/reservations/{sessionId}/heartbeat
```

Request: generation and client timestamp. The server uses its own clock for
expiry decisions. A mismatched generation never extends the reservation.

### Commit

```text
POST /v1/reservations/{sessionId}/commit
```

Request:

```json
{
  "generation": 42,
  "candidateRevisionId": "rev_candidate",
  "expectedStateRevisionId": "rev_start"
}
```

Response:

```json
{
  "result": "Committed",
  "currentStateRevisionId": "rev_candidate",
  "reservationState": "Active",
  "candidate": "Canonical"
}
```

`Committed`, `Unchanged`, `HeadChanged`, `ReservationMismatch`,
`InvalidCandidate`, `Unauthorized`, and `NotFound` are stable outcomes. A
successful commit does not release the reservation until the client or a
separate completion command confirms the lifecycle boundary.

### Continue from last safe state

```text
POST /v1/reservations/{sessionId}/recover
```

Request:

```json
{
  "generation": 42,
  "action": "ContinueFromLastSafeState",
  "confirmation": "explicit-user-confirmation"
}
```

The server verifies current head and recovery authority, invalidates the old
generation where required, marks the candidate non-canonical, and returns the
last committed head. Candidate cleanup is a separate retention operation.

## Direct package transfer

```text
request transfer authorization
-> API checks World access, revision metadata, limits, and expected head
-> API returns scoped upload/download target and transfer id
-> client transfers bytes directly to object storage
-> client resumes missing parts using transfer id
-> client finalizes with ordered parts, byte size, and SHA-256
-> API/provider verifies integrity
-> API publishes immutable revision metadata
```

An upload target cannot publish a revision by itself. A download target cannot
be issued before World/revision authorization. Transfer expiration, revocation,
and cleanup are independent from canonical head advancement.

## Contract completion criteria

This wire contract is ready for implementation when:

- BE-1 tests cover each command and stable result;
- UI/runtime mappings use backend summaries without owning backend rules;
- provider-backed transfer tests preserve the same request/result semantics;
- security review confirms that errors, logs, tokens, and transfer targets do
  not disclose private World state;
- versioning and compatibility policy are recorded before the first production
  endpoint is deployed.
