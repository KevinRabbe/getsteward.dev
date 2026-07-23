# E4 Live Acceptance Deployment

Reviewed: **2026-07-22**

Status: **DISPOSABLE ACCEPTANCE ENVIRONMENT SPECIFICATION. NOT A FINAL PRODUCTION-PROVIDER DECISION.**

## Purpose

E4 no longer needs another simulated backend. It needs one real EU deployment that the Windows Desktop can reach through the same public HTTPS boundary a released Steward client will use.

The acceptance environment exists to answer one question:

> Does the already-composed Steward product work end to end when Steam identity, PostgreSQL authority, S3-compatible object transfer, the Windows Desktop, and a real Factorio session are separated by actual deployment/network boundaries?

Any failure discovered there becomes concrete engineering evidence. The deployment must not redefine Core, authority, transfer, or adapter contracts merely to fit a provider.

## Provider-neutral deployment shape

```text
Windows Steward installation A/B
        |
        | HTTPS control requests
        v
Steward Backend.Api container
        |
        +---- PostgreSQL
        |       sessions
        |       World/access metadata
        |       revision metadata
        |       reservation/generation authority
        |       idempotency
        |
        +---- private S3-compatible object storage
                 immutable World packages

Windows Desktop <-----------------------> object storage
             direct authorized upload/download
```

Object storage still never decides which revision is canonical. Backend authority and PostgreSQL transactions remain the source of truth.

## First disposable candidate: Scaleway Paris

Scaleway `fr-par` is the first deployment shape to test, not an approved long-term vendor decision.

Current official documentation confirms the pieces needed for the disposable acceptance environment:

- Serverless Containers accepts ordinary container images, injects a `PORT` environment variable, supports secret environment variables, health checks, configurable min/max scaling, and VPC/private-network integration;
- Managed PostgreSQL is available in the Paris `fr-par` region;
- Object Storage exposes an S3-compatible Paris endpoint at `https://s3.fr-par.scw.cloud/` with region `fr-par`.

References:

- https://www.scaleway.com/en/docs/serverless-containers/reference-content/port-parameter-variable/
- https://www.scaleway.com/en/docs/serverless-containers/concepts/
- https://www.scaleway.com/en/docs/serverless-containers/reference-content/containers-autoscaling/
- https://www.scaleway.com/en/docs/serverless-containers/how-to/manage-a-container/
- https://www.scaleway.com/en/developers/api/managed-database-postgre-mysql
- https://www.scaleway.com/en/docs/object-storage/concepts/

The provider remains replaceable because the application contract is still:

```text
containerized ASP.NET API
+ PostgreSQL
+ S3-compatible private object storage
```

### Acceptance scaling rule

For the first E4 deployment set:

```text
min scale = 1
max scale = 1
```

This is deliberate, not a permanent scalability limit.

`Backend.Api` currently contains periodic cleanup hosted services. A scale-to-zero deployment would suspend that periodic work while no instance exists, and multiple replicas would run multiple cleanup loops. PostgreSQL authority itself is designed for concurrent API processes, but E4 is not the milestone to introduce another deployment variable while proving the first real handoff.

After live acceptance we can separately prove multi-instance cleanup behavior or move periodic maintenance to an explicit scheduled worker before raising max scale. Until then, one always-available acceptance replica removes an unnecessary variable.

## Backend image

The production image is built from the repository root:

```bash
docker build \
  --file src/SharedWorlds.Backend.Api/Dockerfile \
  --tag steward-backend:acceptance \
  .
```

The runtime image:

- uses the official .NET 10 ASP.NET runtime;
- runs as the built-in non-root `app` user;
- defaults to port `8080` locally;
- honors a platform-provided `PORT` value after validating `1..65535`.

CI builds the exact Dockerfile and starts it against a real disposable PostgreSQL service, then requires both health probes to succeed. Deployment packaging therefore cannot silently drift away from the normal code matrix.

## Health boundary

The container exposes two non-secret operational probes:

```text
GET /health/live
    200 when the ASP.NET process is serving requests

GET /health/ready
    SELECT 1 against the configured PostgreSQL data source
    200 when the database is reachable
    503 otherwise
```

`/health/ready` deliberately does not return database exception details. Concrete diagnostics belong in provider/application logs, not the public health response.

Object-storage viability is proven by the actual immutable transfer acceptance flow rather than by adding a second fake authority or broad storage-health abstraction.

## Required backend configuration

ASP.NET Core maps double underscores in environment-variable names to configuration sections.

### Secrets

These values must be provider secrets and must never be committed:

```text
ConnectionStrings__Steward
Steam__PublisherApiKey
ObjectStorage__AccessKeyId
ObjectStorage__SecretAccessKey
```

### Non-secret deployment configuration

```text
Steam__AppId=<real Steward Steam AppID>
Steam__Identity=<real Steward Web API ticket identity>

ObjectStorage__ServiceUrl=https://s3.fr-par.scw.cloud/
ObjectStorage__AuthenticationRegion=fr-par
ObjectStorage__BucketName=<private disposable Steward bucket>
ObjectStorage__ForcePathStyle=false

Cleanup__IntervalMinutes=15
Cleanup__VerifiedCandidateRetentionDays=7
Cleanup__BatchSize=100
```

`ObjectStorage__ForcePathStyle=false` is the initial standard-S3 setting for the candidate deployment; the acceptance transfer test, not this document, decides whether that setting is valid for the chosen endpoint.

Serverless Containers owns `PORT`; do not create a second competing port variable.

## Required Desktop configuration

Both Windows acceptance installations must use the same deployment identity values:

```text
STEWARD_API_BASE_URL=https://<acceptance-api-host>/
STEWARD_STEAM_APP_ID=<same real Steward Steam AppID>
STEWARD_STEAM_WEB_API_IDENTITY=<same Web API ticket identity expected by backend>
```

The two PCs must retain different durable Steward installation IDs. Do not copy the Desktop device-settings file from A to B.

## Disposable resource rules

For the acceptance environment:

1. use one EU region for API-adjacent infrastructure, PostgreSQL, object storage, and backups where the provider permits it;
2. create a private bucket dedicated to the acceptance deployment;
3. create dedicated least-privilege object-storage credentials rather than account-owner credentials;
4. keep the database non-public where the selected container/network configuration can reach it privately;
5. require HTTPS for the public Backend.Api endpoint;
6. inject credentials through provider secrets only;
7. run one always-available API replica for the first acceptance proof;
8. collect application/container/database logs without logging Steam tickets, refresh credentials, object-storage secrets, RCON passwords, game passwords, or World contents;
9. delete disposable users/credentials/resources after acceptance evidence is recorded.

## Acceptance bootstrap

Before opening Steward on either test PC:

```text
1. PostgreSQL exists and its TLS connection string is stored as a secret.
2. Private S3-compatible bucket exists.
3. Scoped object-storage credentials exist.
4. Real Steward Steam AppID / publisher key / Web API identity are available.
5. Backend image is deployed with min scale = max scale = 1.
6. GET /health/live -> 200.
7. GET /health/ready -> 200.
8. Desktop A and B receive the three STEWARD_* deployment variables.
```

A healthy API is necessary but not sufficient. The real transfer and Steam verification still have to succeed.

## E4 live acceptance sequence

Use Factorio first because its local/host lifecycle was already experimentally characterized before the generic architecture was extracted.

```text
PC A launches Steward under real Steam
-> SteamAPI.Init succeeds for Steward AppID
-> Web API ticket returned for configured identity
-> deployed backend verifies ticket with Steam
-> authenticated shared World catalog loads
-> import/share or prepared acceptance World becomes canonical
-> exact Factorio environment Verify = Ready
-> PC A acquires generation
-> canonical package downloads and verifies
-> authoritative dedicated Factorio server reaches authenticated RCON readiness
-> host client plays
-> host client exits
-> RCON /server-save
-> save refresh is observed
-> server stops
-> candidate captured
-> direct multipart upload/finalization succeeds
-> expected-head commit advances canonical state
-> PC B authenticates with its own installation ID
-> PC B lists the same World and sees A's new revision
-> PC B verifies exact environment
-> PC B continues/hosts that revision
-> PC B commits the next revision
-> PC A observes the new canonical head
```

## Failure injections worth running before E4 sign-off

After the happy path works, deliberately test at least:

- kill Steward after workspace journal creation;
- kill Steward after gameplay but before candidate publication;
- lose the commit response after backend commit succeeds;
- temporarily break API connectivity during an active generation;
- attempt a second writer while A owns authority;
- allow reclaim after the configured uncertainty/grace behavior;
- change local Factorio environment and confirm Verify blocks writable shared play;
- create `CleanupPending` and prove cleanup never changes canonical state;
- restart with a crash-found `Active` record and prove **Recover changes** / confirmed **Discard interrupted session** behave as documented.

No test may resolve ambiguity by deleting the recovery journal or by force-moving a canonical head.

## Evidence to record

Record without secrets:

- deployed backend image commit SHA;
- provider/region and resource classes;
- API health results;
- Steam AppID only (never publisher key/ticket);
- PC A and PC B installation IDs in redacted/hashed form if needed;
- World/revision/generation IDs;
- package sizes and transfer timings;
- reservation/acquire/reclaim outcomes;
- Factorio server readiness/save evidence;
- recovery outcomes;
- errors and operational friction;
- measured monthly-cost inputs from the acceptance workload.

The final provider decision belongs in `BE_PROVIDER_EVALUATION.md` only after this evidence exists. A successful Scaleway acceptance run proves the deployment shape; it does not by itself prove that Scaleway is the best long-term commercial provider.
