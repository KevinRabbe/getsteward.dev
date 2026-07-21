# Backend Operations and Recovery

This document defines the first-release operating contract for the Steward
backend. It is a planning artifact, not a deployment guide or infrastructure
implementation.

## Service boundaries

The first release has one logical backend authority in the EU, which may run
multiple API instances and availability zones:

- HTTPS API/control plane;
- transactional metadata database;
- private immutable object storage;
- secret/key management;
- metrics, logs, tracing, and audit delivery.

The game, Steam multiplayer, and realtime gameplay traffic remain outside this
service boundary.

## Health checks

### Liveness

Confirms the process can respond. It must not report dependency health or expose
secrets, database details, or object keys.

### Readiness

Confirms the API can safely serve a specific class of work:

- metadata reads;
- reservation transactions;
- object authorization;
- transfer finalization;
- audit/event delivery.

If the database or object store is unavailable, readiness must identify the
affected dependency. The API must fail closed for new shared writable sessions.

### Dependency checks

Checks are bounded and read-only where possible. A failed dependency check must
not create reservations, mutate heads, or publish candidates.

## Structured diagnostics

Every operational event includes:

- UTC timestamp;
- correlation id;
- operation and lifecycle phase;
- World id and revision ids only in protected internal logs;
- adapter id;
- session id/generation where relevant;
- result category and duration;
- dependency/HTTP status where relevant.

Never log Steam tickets, access/refresh tokens, object-store credentials, private
join data, arbitrary package bytes, or unnecessary personal data. Logs and traces
must redact request bodies for transfer and authentication operations.

## Metrics

Minimum metrics:

- API request count, latency, and error code;
- authentication success/failure and Steam dependency errors;
- World access changes and authorization denials;
- reservation acquire conflicts, heartbeat misses, uncertainty, reclaim, and
  late-generation rejection;
- upload/download bytes, duration, resume count, integrity failures, and expiry;
- candidate publication, head commits, unchanged commits, stale-head conflicts,
  and idempotency replay/conflict;
- database/object-store health and latency;
- cleanup backlog and retention age;
- backup age, restore-test result, and audit-delivery failures.

Metrics are aggregate and must not contain save contents or raw identity tokens.

## Alerts

Page or escalate for:

- inability to serve authenticated control operations;
- database or object-store readiness failure;
- abnormal head-commit conflict or reservation-uncertainty rates;
- integrity verification failures;
- backup age beyond policy or failed restore validation;
- cleanup backlog threatening storage limits;
- unexpected egress, request, or transfer-cost growth;
- audit or security-event delivery failure.

Alerts must link to a correlation/incident reference and state whether new
shared writable sessions are blocked.

## Deployment and rollback

Deployment environments are isolated:

```text
development -> staging -> production
```

Promotion requires:

- API contract/conformance tests;
- migration dry run and rollback/restore notes;
- secret and permission verification;
- health/readiness checks;
- representative transfer and commit tests;
- no unresolved active migration against production data.

Rollback rules:

- application rollback is preferred when the persisted schema remains compatible;
- destructive schema rollback is never improvised during an incident;
- incompatible migrations use expand, migrate, contract sequencing;
- if authority is uncertain, block new shared writable sessions and preserve
  current heads/reservations until state is verified.

## Incident classes

### Dependency outage

```text
detect failed readiness
-> block new shared writable sessions
-> keep cached shared data explicitly non-authoritative
-> allow active local gameplay to continue where runtime permits
-> preserve candidate/recovery evidence
-> restore dependency and reconcile idempotent operations
```

### Integrity failure

```text
quarantine candidate/transfer
-> do not publish or advance head
-> retain previous valid head
-> record audit and diagnostic event
-> investigate provider/transport/adapter boundary
```

### Reservation authority uncertainty

```text
mark or retain Uncertain
-> do not allow competing acquire
-> permit only matching-generation recovery during grace
-> reclaim only after policy threshold and explicit authority
-> invalidate old generation before new acquire
```

### Suspected credential or access compromise

```text
revoke affected access sessions/credentials
-> preserve audit evidence
-> review World access and reservation activity
-> rotate secrets/keys according to incident policy
-> do not delete recovery evidence as a cleanup shortcut
```

## Backup and disaster recovery exercise

Before commercial release and on a scheduled cadence:

1. create encrypted database and object backups within the EU boundary;
2. restore them into an isolated EU environment;
3. verify head/revision references, access membership, reservations, idempotency,
   audit continuity, and object hashes;
4. confirm the restored service fails closed until all checks pass;
5. run the BE-1 two-client handoff against the restored environment;
6. record recovery time, recovery point, missing-data findings, and follow-up;
7. destroy the isolated test environment and temporary credentials safely.

The restore test is a release requirement, not merely a provider checkbox.

## Support diagnostics

The desktop may provide a diagnostic bundle containing selected correlation ids,
operation phases, error codes, adapter id, and timestamps. It must exclude
tokens, private join data, save/package contents, and full filesystem paths when
not required. User-selected upload of diagnostics is separate from automatic
World-state transfer.

## Operational completion criteria

BE-0 operations are complete when the team has documented alert ownership,
dependency outage behavior, migration/rollback handling, incident escalation,
backup/restore evidence, cost-limit response, and redacted diagnostic rules.
