# Backend Schema and Data Lifecycle

This document defines the logical persistence contract behind the Steward API.
It is provider-neutral and does not authorize a production database schema while
the planning lock is active.

## Logical records

### ExternalIdentity

- internal identity id;
- provider (`Steam` in the first release);
- stable external id;
- optional display name cache;
- created, last-seen, and revoked timestamps.

Authentication tickets, access tokens, refresh handles, and provider secrets are
not stored in this record.

### World

- opaque World id;
- adapter id;
- display name;
- current environment revision id;
- current state revision id;
- lifecycle status only where durable recovery requires it;
- created/updated timestamps;
- optimistic version.

There is exactly one current state head per World. The World record does not
contain gameplay ownership, social roles, branches, or merge history.

### WorldAccess

- World id;
- external identity id;
- membership state (`Pending`, `Member`, `Revoked`);
- invitation id and timestamps;
- invited/accepted/revoked timestamps;
- actor identity for access changes.

The unique key is `(WorldId, ExternalIdentityId)` with state transitions recorded
in audit events. Revocation prevents new sessions but does not silently terminate
an active reservation.

### EnvironmentRevision and StateRevision

Both revision types are immutable and include:

- revision id and World id;
- adapter id and kind;
- previous revision/head used for diagnostics;
- object key/reference;
- SHA-256 and byte size;
- publication status;
- created/verified timestamps.

State revisions additionally record the expected starting state head and the
publishing session id/generation when applicable. A candidate must be verified
before it can be referenced by the World head.

### SessionReservation

- World id;
- session id and generation;
- holder external identity and device id;
- starting state revision id;
- mode (`Local` or `Host`);
- state (`Active`, `Uncertain`, `RecoveryNeeded`, `Completed`);
- acquired, heartbeat, uncertainty, and grace timestamps;
- completion/reclaim actor and timestamps.

Only one non-terminal reservation may exist per World. Generation is never reused
for a different session.

### Transfer

- transfer id;
- World/revision target;
- direction and kind;
- expected size and SHA-256;
- provider upload id/object reference;
- completed parts or provider resume metadata;
- state (`Created`, `Uploading`, `Verified`, `Expired`, `Abandoned`);
- expiry and finalization timestamps.

Transfer records never make a candidate canonical by themselves.

### IdempotencyRecord and AuditEvent

Idempotency records bind a caller, operation, key, request fingerprint, and
durable result. Audit events record access, authentication outcome, transfer
publication, reservation transitions, recovery, and head commits without save
contents, tokens, or unnecessary personal data.

## Invariants

The database layer must enforce or transactionally guarantee:

1. every World head references a verified immutable revision belonging to that
   World and adapter;
2. a state head never advances without a matching expected previous head;
3. at most one active/uncertain/recovery-needed reservation exists per World;
4. a reservation generation is unique and cannot be revived after invalidation;
5. only an authorized member may read World metadata or initiate transfers;
6. only the matching reservation holder/generation may heartbeat or commit;
7. a verified candidate may remain unreferenced, but an unverified candidate may
   never become current;
8. idempotency-key reuse with a different request is rejected;
9. revocation does not delete current heads or recovery evidence;
10. cleanup cannot remove a current head, active transfer, or retained recovery
    artifact.

## Transaction boundaries

### Acquire

Read the current head and check membership, then atomically create the unique
reservation with the expected starting head. A race returns `AlreadyReserved` or
`HeadChanged`; it never creates two writers.

### Publish candidate

Verify object existence, size, hash, World, adapter, and transfer completion,
then insert immutable revision metadata. Do not update the World head in this
transaction.

### Commit candidate

In one transaction:

```text
lock/check World head
-> check verified candidate and adapter/World match
-> check reservation session/generation and expected starting head
-> advance World current state head
-> record commit audit/idempotency result
```

Reservation release is a separate safe transition after the commit result is
known. If the transaction fails, the previous head remains authoritative.

### Reclaim

In one transaction:

```text
check reservation is RecoveryNeeded and grace expired
-> invalidate old generation
-> record reclaim actor/audit event
-> make World available from current committed head
```

The next writer acquires in a separate operation and must read the new current
head. Reclaim does not promote or delete a candidate.

## Schema and API versioning

- API routes use an explicit major version such as `/v1`.
- Persisted documents and records carry a schema version.
- Additive fields are preferred within a major API version.
- Removing or changing the meaning of a field requires a new API/schema version
  or a controlled migration with compatibility tests.
- Every migration has forward validation, rollback/restore notes, and a tested
  backup taken before production execution.
- Old persisted revisions and opaque packages remain readable for the declared
  retention period even when current metadata schemas evolve.
- Core domain objects do not gain provider-specific columns or Steam SDK types.

## Retention and cleanup

Cleanup classifies data before deletion:

1. current World heads: retain;
2. previous revisions required by declared recovery policy: retain;
3. active/uncertain transfers and recovery evidence: retain;
4. verified but unreferenced candidates inside retention: retain;
5. expired candidates/transfers with no recovery hold: eligible for cleanup;
6. audit and legal-retention records: retain according to policy.

Age alone never proves that a package is safe to delete. Cleanup is bounded,
observable, idempotent, and independent from gameplay transactions.

## Backup and restore

- Database backups and object-storage recovery copies remain encrypted and within
  the approved EU residency boundary.
- A restore produces an isolated environment first; it never writes directly to
  the live authority during testing.
- Restore validation checks World/head references, revision hashes, reservation
  safety, access membership, idempotency records, and audit continuity.
- After an incomplete restore, the service must fail closed for new shared
  writable sessions rather than present uncertain state as Ready.

## Completion criteria

This contract is ready for implementation when BE-1 tests cover the invariants,
transaction boundaries, migrations, cleanup classification, and restore-failure
behavior without requiring a named provider.
