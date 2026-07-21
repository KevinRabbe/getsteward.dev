# Backend Provider Evaluation

This worksheet defines how Steward selects production infrastructure after the
planning contract is approved. It does not select a vendor and does not create
infrastructure.

## Fixed constraints

Every candidate must support:

- one authoritative EU deployment;
- a transactional relational database for heads, reservations, access, and
  idempotency;
- private durable object storage in the EU for opaque World packages;
- encrypted primary data and encrypted backups;
- direct authorized resumable package transfers;
- server-side size and checksum verification;
- scoped credentials and least-privilege service access;
- health checks, metrics, audit support, and operational logs;
- tested backup and restore into an isolated environment;
- documented data location, retention, deletion, and subprocessor behavior.

The provider must not require active-active multi-region writes or make object
storage the source of truth for current-head or reservation decisions.

## Evaluation areas

| Area | Evidence required | Failure condition |
|---|---|---|
| EU residency | Named region, backup locations, subprocessors, support access model | Any required authority data leaves the documented boundary without control |
| Database transactions | Serializable/atomic head and reservation update behavior; backup restore | Cannot guarantee one authoritative commit decision |
| Object transfer | Multipart/resumable upload, range download, checksum, scoped access | Large packages require API-server proxying or mutable shared objects |
| Security | Encryption, secret manager, key rotation, IAM/service accounts, audit | Credentials or private objects cannot be narrowly scoped |
| Reliability | Availability zones, durability assumptions, dependency health signals | No practical restore or outage behavior can be tested |
| Operations | Logs, metrics, alerting, rollback, maintenance windows | Failures cannot be diagnosed without save contents or secrets |
| Privacy | Export, deletion, retention, legal hold, incident process | Data lifecycle cannot be explained to users |
| Cost | Storage, requests, egress, backups, transfer acceleration, support | Cost cannot be bounded for a large World or repeated handoffs |
| Portability | Export formats, S3/PostgreSQL compatibility, migration path | Provider-specific behavior leaks into Core contracts |

## Workload model

The comparison must use measured adapter-produced packages rather than the
provisional roadmap limits alone. Record at least:

- representative small, median, large, and worst-case Factorio packages;
- representative Palworld client and dedicated-server packages;
- package size after adapter cleanup and validation;
- upload/download frequency per World per month;
- average and peak active Worlds;
- retained current, prior, candidate, and recovery revisions;
- concurrent transfers and retry rate;
- expected active users and geographic distribution;
- backup frequency, retention, and restore-test frequency;
- support, monitoring, and alerting requirements.

Use this model for a monthly estimate:

```text
monthly cost
= metadata database
+ primary object storage
+ retained revision storage
+ backup storage
+ API requests
+ object requests
+ internet egress
+ transfer acceleration or CDN, if used
+ monitoring/logging
+ secrets/keys
+ support and fixed platform charges
```

The estimate must include a conservative high-usage scenario and a hard-limit
response: package rejection, retention cleanup eligibility, transfer throttling,
or an operational alert. Steward must not silently exceed a cost boundary while
continuing to promise unlimited World storage.

## Required proof before vendor selection

1. Create a disposable EU test deployment for each shortlisted provider shape.
2. Run the BE-1 contract simulation against the provider-backed repositories.
3. Upload, resume, finalize, download, and verify representative opaque packages.
4. Execute concurrent reservation and expected-head commit races.
5. Restore metadata and objects into an isolated EU environment.
6. Verify deletion/export behavior for a test World and its revisions.
7. Measure throughput, latency, egress, request counts, and failure behavior.
8. Record the final provider, region, limits, and rollback/migration plan in a
   new approved backend decision.

## Current decision

Provider selection is deferred. The first implementation remains provider-free,
and the production choice must satisfy BE-D014 security and BE-D015 EU authority
and residency constraints without changing Core or the BE-1 contract.
