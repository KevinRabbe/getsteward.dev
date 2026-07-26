# Backend Provider Evaluation

Status: **CURRENT EVALUATION CONTRACT — FINAL PRODUCTION PROVIDER REMAINS OPEN; FIRST DISPOSABLE TOPOLOGY IS QUALIFIED FOR ACCEPTANCE PREPARATION.**

This document defines how Steward evaluates production infrastructure. Passing one disposable deployment does not automatically create vendor lock-in.

## Fixed constraints

Every serious provider shape must support:

- one authoritative EU deployment boundary;
- transactional PostgreSQL-compatible relational authority for heads, reservations, access, sessions and idempotency;
- private durable S3-compatible/equivalent object storage for opaque World packages;
- encryption for primary data/backups and standard TLS transport;
- direct authorized resumable package transfer;
- server/provider-side object-size/integrity verification compatible with Steward's expected SHA-256 contract;
- scoped credentials and least-privilege service access;
- health/readiness, logs, metrics/audit support;
- tested backup and restore into an isolated environment;
- documented data location, retention, deletion and subprocessor/support-access behavior.

The provider must not require:

- active-active multi-region World writes;
- provider object storage to become current-head/reservation authority;
- provider-specific types in Core/domain contracts;
- a permanent game-server fleet;
- broad proxy/header trust merely to make the first deployment work.

## Evaluation areas

| Area | Evidence required | Failure condition |
|---|---|---|
| EU residency | named region, backup locations, subprocessors/support-access model | required authority data cannot remain inside the approved boundary |
| Database transactions | atomic/serializable-enough reservation/head/idempotency semantics; backup/restore | cannot guarantee one authoritative commit/writer decision |
| Object transfer | multipart/resume, scoped authorization, streamed download, integrity | large packages must be proxied through Backend.Api or stored as mutable shared objects |
| Security | TLS, encryption, secret management/IAM, private objects, audit | credentials/private objects cannot be narrowly scoped |
| Networking | stable HTTPS ingress and explicit client-address trust model where Host presence needs it | topology requires trusting arbitrary forwarding headers/CIDRs without concrete control |
| Reliability | durability assumptions, maintenance/outage signals, restore behavior | no practical recovery/outage test is possible |
| Operations | health, logs, metrics, alerting, rollback/deploy process | failures cannot be diagnosed safely without secrets/save contents |
| Privacy | export/deletion/retention/legal/process documentation | user data lifecycle cannot be explained/operated |
| Cost | compute, DB, object storage, requests, egress, backups, support | real World workload cost cannot be bounded/observed |
| Portability | PostgreSQL/S3 compatibility, export/migration path | provider semantics leak into Core/authority contracts |

## Workload evidence

Final selection should use measured adapter-produced packages and real usage rather than only roadmap ceilings.

Record at least:

- representative small/median/large World package sizes for release-advertised games;
- capture/restore/materialization duration;
- upload/download frequency per World;
- retained canonical/recovery/candidate object volume;
- concurrent transfer rate;
- active user/World counts;
- geographic distribution relevant to EU egress/latency;
- backup frequency/retention/restore-test frequency;
- operational/logging/monitoring load.

A monthly estimate should include:

```text
metadata database
+ API compute/network
+ primary object storage
+ retained/recovery object storage
+ backup storage
+ object/API requests
+ internet egress
+ monitoring/logging/secrets/support
+ optional measured delivery optimization only if actually used
```

The estimate also needs a conservative high-usage case and an explicit technical/operational response to limits. Steward must not silently promise unlimited storage/egress.

## Provider selection proof

Before final vendor selection, the selected/shortlisted shape should demonstrate:

1. disposable EU deployment;
2. production Backend.Api startup/readiness against real PostgreSQL + private object storage;
3. provider-backed authority/metadata behavior;
4. upload/resume/finalize/download/verify of representative immutable packages;
5. concurrent reservation/expected-head races;
6. database backup/restore into an isolated environment;
7. object retention/deletion/export behavior for a test World;
8. actual throughput/latency/egress/request/failure measurements;
9. real Windows shared-World handoff through the deployment;
10. documented rollback/migration/secret/access model.

The final provider decision is recorded only after this evidence exists.

## Current decision

**Final production-provider selection remains open.**

The first disposable acceptance candidate remains **Scaleway Paris (`fr-par`)**, but the qualified repository topology is no longer the older Serverless Containers plan.

Current first Host/Join acceptance shape from qualified #131:

```text
Internet clients
    |
    | HTTPS
    v
one small Scaleway Instance + flexible public IPv4
    |
    | Caddy :443
    | one public TLS boundary
    v
127.0.0.1:8080
Backend.Api container using Linux host networking
    |
    +-> managed PostgreSQL in the same EU region
    `-> private S3-compatible Object Storage in the same EU region

Desktop <----------------------------> Object Storage
        direct authorized package transfer
```

Steward configuration for the one-proxy boundary is intentionally exact:

```text
ReverseProxy__KnownProxyIp=127.0.0.1
```

Public exposure should be limited to the actual ingress/administration requirement. Backend.Api port 8080 is not an Internet endpoint in this topology.

## Why the first candidate changed

The older evaluation text chose Serverless Containers because its high-level primitives matched Steward's provider-neutral shape.

That was not sufficient for the first real Host/Join acceptance topology because Host presence needs a truthful originating client IPv4 when no adapter supplies an explicit public address, and Steward's qualified forwarded-header boundary deliberately trusts only one explicitly known proxy peer for one hop.

The accepted response was **not** to broaden Steward trust to arbitrary forwarding headers/provider CIDRs.

Instead:

```text
need one truthful proxy client-address boundary
-> choose one machine under deployment control
-> Caddy is the only public reverse proxy
-> Backend.Api observes Caddy locally
-> trust exactly 127.0.0.1 for one forwarded hop
```

That removes the provider-ingress ambiguity without creating a generic proxy-network subsystem.

The Serverless option may still be reconsidered later if the exact deployment contract can be satisfied safely and it provides a measured operational/cost advantage. It is not required architecture.

## Current provider-neutral application contract

Even with the first Scaleway topology, Steward still depends only on:

```text
containerized ASP.NET Backend.Api
+ PostgreSQL-compatible transactional authority
+ private S3-compatible immutable object storage
+ one explicit HTTPS ingress/trust boundary
```

No Scaleway SDK/type is required in Core, Desktop, adapter or backend domain contracts.

## First disposable deployment runbook

Use `E4_LIVE_ACCEPTANCE_DEPLOYMENT.md` as the current exact deployment/runbook authority.

It owns:

- Instance/network shape;
- Caddy boundary;
- Backend.Api container launch;
- `ReverseProxy__KnownProxyIp` configuration;
- PostgreSQL/object-storage placement;
- Friends/E4-A authentication/deployment mode;
- health/readiness and acceptance sequencing.

This evaluation document should not duplicate those operational steps.

## Final-selection rule

A successful first deployment answers:

> **Can this provider shape run Steward's already-defined contracts in the real world?**

It does not answer automatically:

> **Is this the best long-term production vendor?**

Final selection waits for real measurements of:

- package throughput/latency;
- egress/request/storage cost;
- DB/object operational behavior;
- backup/restore/deletion friction;
- residency/subprocessor/security review;
- support/observability experience;
- migration/rollback practicality.

Use evidence to choose the vendor. Do not reshape Steward's World authority merely to fit one provider.