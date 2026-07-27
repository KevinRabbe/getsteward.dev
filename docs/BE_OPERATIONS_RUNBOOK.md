# Backend Operations and Recovery

Status: **CURRENT OPERATING CONTRACT — DETERMINISTIC PROVIDER BOUNDARIES ARE IMPLEMENTED; REAL PRODUCTION OPERATIONS/RESTORE EVIDENCE REMAINS EXTERNAL.**

This document defines how the Steward backend must be operated safely. It is not a cloud-provider click-through guide; `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` owns the current first disposable deployment topology.

## Service boundary

First release uses one logical Steward authority in the EU:

- HTTPS Backend.Api control plane;
- transactional PostgreSQL-compatible metadata/authority;
- private immutable S3-compatible object storage;
- provider secret/key boundary;
- logs/metrics/audit/backup operations.

Steam/game realtime multiplayer traffic remains outside this backend.

Multiple service instances/AZs may support availability later without creating active-active multi-region World authority.

## Current deterministic operational evidence

CI already proves important provider/runtime mechanics:

- Backend.Api production container builds/runs as the intended non-root runtime user;
- startup fails closed on invalid/partial required configuration;
- `/health/live` and `/health/ready` exercise the composed API/dependency boundary;
- real PostgreSQL integration stores authority/access/revision/session data;
- PostgreSQL-native `pg_dump`/`pg_restore` restores Steward records into a fresh database;
- S3-compatible multipart transfer resumes and reproduces exact bytes/SHA-256;
- synthetic 256 MiB transfer remains a permanent provider-integration gate;
- accelerated 24-hour reservation-heartbeat behavior is proven against PostgreSQL;
- cleanup/retention workers are bounded/configured;
- control/transfer credential paths enforce the secure-remote transport rules.

These are deterministic/provider-integration proofs, not production-provider operational acceptance.

## Health checks

### Liveness

```text
GET /health/live
```

Confirms the API process can respond.

It must not expose database/object keys, credentials, internal exception details, or claim dependencies are healthy merely because the process is alive.

### Readiness

```text
GET /health/ready
```

Current readiness checks the real database/object-store boundary without mutating World authority.

Dependency failure returns not-ready and must block new shared work that cannot be served safely.

A readiness probe must never:

- create a reservation;
- mutate a World head;
- publish a revision;
- consume user package data;
- disclose secrets.

## Deployment shape

The application contract remains provider-neutral:

```text
public HTTPS boundary
-> Backend.Api
-> PostgreSQL
-> private S3-compatible object storage
```

The first disposable acceptance runbook currently narrows that to one Instance + Caddy + localhost Backend.Api exact-proxy trust. That is deployment evidence, not a requirement for every future provider.

## Deployment/preflight requirements

Before promoting a real deployment/candidate:

- exact qualified Steward image/content SHA is known;
- database/object-store region/access policy matches the intended EU boundary;
- connection/object-store credentials are injected as provider secrets, not source/package content;
- production Friends Build authentication is disabled for Steam release deployments;
- Steam publisher credential is backend-secret only when V3-F is opened;
- reverse-proxy trust matches the exact actual topology;
- health/live + health/ready succeed;
- schema initialization/migration path is known;
- representative authorized transfer succeeds;
- logs can be inspected without exposing credentials/save contents;
- rollback/redeploy procedure preserves durable authority/data.

## Rollback/migration rules

- Prefer application rollback when persisted schema remains compatible.
- Do not improvise destructive schema rollback during an incident.
- Incompatible schema changes use deliberate expand/migrate/contract-style sequencing where needed.
- Take/verify backup before production schema changes where the migration risk requires it.
- If authority is uncertain after deploy/migration failure, block new shared writable sessions until current heads/reservations are verified.
- Never “fix” migration failure by deleting/reinitializing user World authority.

## Structured diagnostics

Operational events should carry enough bounded context to correlate failures:

- UTC timestamp;
- correlation/incident ID;
- operation/lifecycle phase;
- adapter ID;
- protected internal World/revision/session identifiers where necessary;
- result category/duration;
- dependency/HTTP status where relevant.

Never log:

- Steam ticket bytes;
- Friends bootstrap secret;
- Steward access/refresh credentials;
- game join/admin/RCON/REST secrets;
- object-store/database/publisher credentials;
- arbitrary World package/save contents;
- unnecessary personal data.

Local Desktop diagnostics and backend/provider logs are separate trust boundaries.

## Metrics/operational observations

Useful service-level observations include:

- API request count/latency/failure class;
- authentication success/failure/provider errors;
- access changes/authorization denials;
- reservation acquire conflicts/heartbeat misses/Uncertain/reclaim/late-generation rejection;
- upload/download bytes/duration/resume/integrity failure;
- candidate publication/commit/unchanged/stale-head/idempotency conflict;
- database/object-store readiness/latency;
- cleanup backlog/retention age;
- backup age/restore-test result;
- storage/request/egress cost trends.

Do not add a telemetry platform merely to satisfy a list. Use provider-native/basic operational primitives first and add machinery only when real operation requires it.

Metrics must not contain package contents or plaintext credentials.

## Alert/escalation classes

A real deployment needs an observable response for at least:

- Backend.Api cannot serve authenticated control work;
- PostgreSQL/object-store readiness failure;
- abnormal reservation uncertainty/conflict/late-generation rates;
- package integrity failure;
- cleanup backlog threatening configured storage limits;
- backup age/restore validation failure;
- unexpected request/storage/egress growth;
- security/audit delivery failure where configured.

The response must state whether new shared writable sessions are safe or blocked.

## Incident handling

### Dependency outage

```text
detect dependency/readiness failure
-> block new shared work that requires authoritative dependency
-> do not treat cached shared metadata as fresh authority
-> allow already-valid local gameplay to continue where runtime contract permits
-> preserve reservation/candidate/recovery evidence
-> restore dependency
-> reconcile idempotent/ambiguous operations
```

### Integrity failure

```text
reject/quarantine invalid candidate/transfer
-> do not publish/advance head
-> keep previous canonical state
-> record bounded diagnostic/audit evidence
-> investigate transfer/provider/adapter boundary
```

### Reservation uncertainty

```text
retain/enter Uncertain
-> no competing writer
-> same valid generation may reconnect
-> deliberate reclaim only after policy conditions
-> invalidate old generation before future acquisition
```

### Suspected credential/access compromise

```text
revoke affected normal Steward sessions/access where safe
-> preserve audit/recovery evidence
-> inspect World/reservation activity
-> rotate provider/publisher/bootstrap secrets as appropriate
-> never delete gameplay recovery evidence merely to clear authority
```

A membership/admin action must not arbitrarily destroy a healthy active writer's responsibility.

## Backup and disaster recovery

CI proof is not the production restore exercise.

Before commercial release and on a real operational cadence after launch:

1. create provider-native encrypted database backups and appropriate object-storage protection within the approved EU boundary;
2. restore into an isolated environment;
3. do **not** rely on normal Steward startup/schema initialization to silently repair the restored database;
4. verify World/current-head/revision/access/reservation/idempotency relationships;
5. verify retained current/recovery object bytes/integrity;
6. keep the restored service fail-closed until validation passes;
7. exercise a representative shared-World transaction/handoff against the restored authority;
8. record recovery time/point and any missing-data/operational finding;
9. destroy isolated test credentials/resources safely.

A backup that has never been restored is not sufficient evidence.

## Cost/limit response

Operational cost limits must map to safe product behavior.

Examples:

- package above hard ceiling -> reject before publication;
- disposable cache pressure -> bounded eviction of cache-only data;
- cleanup backlog -> operational alert/action;
- unresolved gameplay/recovery data -> preserve; do not delete to meet a cost target;
- measured egress problem -> evaluate delivery optimization after evidence.

Do not silently trade World correctness for lower storage cost.

## Production-release boundary

The final V3-F operational proof requires the real provider/Steam environment:

```text
real public HTTPS deployment
+ real PostgreSQL
+ real private S3-compatible storage
+ matching production Steam public config
+ secret publisher credential
+ Steam-installed clients
-> real auth/access/transfer/reservation/commit/recovery evidence
```

Friends Build authentication is not a production fallback.

## Final rule

> **When backend authority is uncertain, preserve current heads/reservations/recovery evidence and block unsafe new work. Availability never outranks correctness of the shared World.**