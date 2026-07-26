# E4 Live Acceptance Deployment

Reviewed: **2026-07-26**

Status: **DISPOSABLE ACCEPTANCE ENVIRONMENT SPECIFICATION. NOT A FINAL PRODUCTION-PROVIDER DECISION.**

## Purpose

Steward no longer needs another simulated backend. It needs one real EU deployment that the private Friends Build can reach through the same HTTPS/PostgreSQL/S3 boundaries used by the production architecture.

The acceptance environment exists to answer the real deployment questions without pulling Steam release credentials forward:

> Does the already-composed Steward product work end to end when Friends Build identity, PostgreSQL authority, S3-compatible object transfer, the Windows Desktop, and real game/network boundaries are separated by an actual Internet deployment?

Any failure discovered there becomes concrete engineering evidence. The deployment must not redefine Core, authority, transfer, adapter, or identity contracts merely to fit a provider.

Production Steam acceptance is a later E4-B gate. E4-A / V2 Friends Build deliberately runs with all `Steam__...` configuration absent.

## Provider-neutral deployment shape

```text
Windows Friends Build A/B
        |
        | HTTPS control requests
        v
one public TLS reverse proxy
        |
        | local/private HTTP
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

The first acceptance deployment uses exactly one API process and exactly one HTTPS proxy. Do not introduce load balancing, proxy fleets, scale-to-zero, or multiple cleanup workers while proving the first real handoff.

## First disposable candidate: Scaleway Paris Instance + Caddy

Scaleway `fr-par` remains the first provider/region to test, not an approved long-term vendor decision.

The first Host/Join candidate is now a small **Scaleway Instance with a flexible public IPv4**, not Serverless Containers.

Why:

- current Scaleway Serverless Container documentation says the platform supplies `X-Forwarded-For`, but Steward's qualified #124 boundary intentionally accepts that header only from one explicitly known proxy peer;
- the current Serverless documentation does not give this project a stable exact ingress-proxy peer address to pin for that purpose;
- broadening Steward to trust arbitrary forwarded-header senders, provider CIDR ranges, or an unspecified proxy fleet would add a new security problem merely to fit the first deployment;
- a normal Instance gives the acceptance environment a directly routed public IPv4 under our deployment control;
- Caddy can be the **only** public HTTPS proxy, on that same Instance, and Backend.Api can therefore trust one exact local peer: `127.0.0.1`.

The resulting trust shape is intentionally small:

```text
Internet client
-> Caddy :443 on the Instance public IPv4
-> Caddy sets the real X-Forwarded-For client address
-> Caddy -> 127.0.0.1:8080
-> Backend.Api raw peer = 127.0.0.1
-> ReverseProxy__KnownProxyIp=127.0.0.1
-> Steward accepts exactly one forwarded client-address hop
```

Caddy's default reverse-proxy behavior ignores spoofable incoming `X-Forwarded-*` values before constructing the upstream forwarding headers. Steward still performs its own exact-proxy trust check; neither component relies on arbitrary client-supplied forwarding metadata.

Current provider/reference material rechecked 2026-07-26:

- Scaleway Flexible IP: https://www.scaleway.com/en/docs/instances/reference-content/flexible-ips/
- Scaleway Instances networking: https://www.scaleway.com/en/docs/instances/reference-content/network/
- Scaleway Managed PostgreSQL / Private Networks: https://www.scaleway.com/en/docs/managed-databases/postgresql-and-mysql/how-to/connect-to-database/
- Scaleway Object Storage: https://www.scaleway.com/en/docs/object-storage/concepts/
- Scaleway Serverless forwarded headers: https://www.scaleway.com/en/docs/serverless-containers/reference-content/headers/
- Caddy `reverse_proxy`: https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
- Docker host networking: https://docs.docker.com/engine/network/drivers/host/

The provider remains replaceable because the application contract is still:

```text
containerized ASP.NET API
+ PostgreSQL
+ S3-compatible private object storage
+ one explicit HTTPS boundary
```

## Acceptance Instance shape

Use one small Linux Instance in `fr-par` with:

- one flexible public IPv4;
- public DNS name pointing to that IPv4;
- Caddy as the only Internet-facing HTTPS process;
- Docker for the exact qualified Backend.Api image;
- no public PostgreSQL requirement when a private/VPC endpoint is available;
- no public exposure of Backend.Api port `8080`.

Public network policy for the first proof should be only what the machine actually needs:

```text
80/tcp   -> Caddy certificate/bootstrap redirect path
443/tcp  -> Caddy HTTPS
22/tcp   -> restricted administrative source(s) only, if SSH is used
8080/tcp -> NOT public
```

Do not expose PostgreSQL or a management dashboard publicly merely for convenience.

## Backend image

Build the production image from the exact qualified repository head used for the Friends Build:

```bash
docker build \
  --file src/SharedWorlds.Backend.Api/Dockerfile \
  --tag steward-backend:<qualified-sha> \
  .
```

The runtime image:

- uses the official .NET 10 ASP.NET runtime;
- runs as the built-in non-root `app` user;
- defaults to port `8080`;
- validates a platform-provided `PORT` when one is supplied.

CI builds this exact Dockerfile and smoke-tests it against PostgreSQL before a head is considered qualified.

### Run one Backend.Api process

For this first Instance deployment, run the container with Linux host networking and keep `PORT=8080`:

```bash
docker run -d \
  --name steward-backend \
  --restart unless-stopped \
  --network host \
  --env-file /etc/steward/backend.env \
  steward-backend:<qualified-sha>
```

No Docker `-p` publication is needed with host networking.

Because Backend.Api listens on the host network, the cloud firewall/security group must not expose TCP `8080` to the Internet. The host firewall should likewise deny non-loopback access to `8080` where practical. Caddy reaches the API locally through `127.0.0.1:8080`.

This is an acceptance topology, not a rule that production must permanently use Docker host networking.

## Caddy HTTPS boundary

Minimal Caddy configuration:

```caddyfile
<acceptance-api-host> {
    reverse_proxy 127.0.0.1:8080
}
```

Caddy owns certificate issuance/renewal and the public TLS socket. Backend.Api remains ordinary HTTP on the same machine.

The corresponding Steward setting is:

```text
ReverseProxy__KnownProxyIp=127.0.0.1
```

Do **not** use `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, provider-wide trusted networks, or arbitrary proxy CIDRs. If a later deployment genuinely requires multiple proxies or changing proxy addresses, that becomes a new measured topology requirement rather than something added preemptively to this proof.

Before interpreting any Factorio/Palworld Host presence, confirm once from deployment evidence that Backend.Api actually observes Caddy as `127.0.0.1`. If that assumption is false on the chosen host configuration, stop and record the exact transport peer rather than widening trust.

## Health boundary

Backend.Api exposes two non-secret operational probes:

```text
GET /health/live
    200 when the ASP.NET process is serving requests

GET /health/ready
    SELECT 1 against the configured PostgreSQL data source
    200 when the database is reachable
    503 otherwise
```

`/health/ready` deliberately does not return database exception details. Concrete diagnostics belong in application/provider logs, not the public health response.

Object-storage viability is proven by the real immutable transfer path rather than by adding another storage-health abstraction.

## Required V2 / E4-A backend configuration

ASP.NET Core maps double underscores in environment-variable names to configuration sections.

### Secrets

These values must be injected outside the repository and must never be committed:

```text
ConnectionStrings__Steward
ObjectStorage__AccessKeyId
ObjectStorage__SecretAccessKey
FriendsBuild__Identities__<n>__CredentialSha256
```

The friend bootstrap credential itself is **not** backend configuration; only its SHA-256 digest is.

### Non-secret / deployment configuration

For the Scaleway Paris candidate:

```text
ObjectStorage__ServiceUrl=https://s3.fr-par.scw.cloud/
ObjectStorage__AuthenticationRegion=fr-par
ObjectStorage__BucketName=<private disposable Steward bucket>
ObjectStorage__ForcePathStyle=false

FriendsBuild__Enabled=true
FriendsBuild__Identities__0__Id=<opaque friend id>
FriendsBuild__Identities__0__DisplayName=<friend display name>
FriendsBuild__Identities__1__Id=<opaque friend id>
FriendsBuild__Identities__1__DisplayName=<friend display name>

ReverseProxy__KnownProxyIp=127.0.0.1

Cleanup__IntervalMinutes=15
Cleanup__VerifiedCandidateRetentionDays=7
Cleanup__BatchSize=100
```

Add the digest key corresponding to each configured Friends Build identity.

Do **not** configure any `Steam__...` value for the Friends-Build-only E4-A deployment. Backend startup deliberately treats complete Steam absence as Steam authentication unavailable while Friends Build authentication remains usable. Partial Steam configuration is invalid and must fail closed.

`ObjectStorage__ForcePathStyle=false` is the initial standard-S3 setting for Scaleway. The actual transfer proof, not this document, decides whether the selected storage endpoint behaves correctly.

## PostgreSQL and object storage

Use a disposable PostgreSQL database in the same EU region and prefer a Private Network/private endpoint between the Instance and database where the selected Scaleway product permits it.

Use TLS verification for PostgreSQL with the provider's current CA/certificate guidance. Do not weaken certificate validation merely to make the acceptance environment connect.

Create one private Object Storage bucket in `fr-par` and one dedicated least-privilege credential pair for Steward. The Windows clients continue to upload/download package bytes directly through authorized S3 URLs; Backend.Api does not proxy the package body.

## Provision Friends Build identities

Create one identity per participating person with the existing repository helper:

```powershell
./tools/v2-provision-friend.ps1 -DisplayName "Kevin" -Index 0
./tools/v2-provision-friend.ps1 -DisplayName "Alex"  -Index 1
```

Each invocation generates an opaque external identity, a plaintext `st_friend_...` bootstrap credential for that person, and the digest-only backend configuration.

Only the digest belongs in `/etc/steward/backend.env` or the equivalent secret/config store. Deliver the plaintext credential to the intended friend separately and delete any temporary plaintext credential file after delivery.

## E4-A backend preflight

Reuse the existing backend-only E4 preflight; do not create another checker:

```powershell
./tools/e4-live-acceptance.ps1 `
  -ApiBaseUrl "https://<acceptance-api-host>/" `
  -SteamAppId "" `
  -SteamWebApiIdentity ""
```

Pass requires at least:

```text
public HTTPS works
-> /health/live = 200
-> /health/ready = 200
```

Do not pass `-DesktopExecutable` for a Friends ZIP. That E4 helper path injects `STEWARD_API_BASE_URL`; a Friends package already carries its backend coordinate in adjacent `steward-friends-build.json`, and Steward intentionally rejects those two routing sources together as ambiguous.

## Build the exact Friends ZIP

After the real HTTPS coordinate exists, produce one immutable package from the same qualified code head.

Locally:

```powershell
./tools/v2-build-friends.ps1 `
  -ApiBaseUrl "https://<acceptance-api-host>/" `
  -Version "2.0.0-alpha.1"
```

Or manually dispatch the repository's **Windows acceptance package** workflow with:

```text
friends_api_base_url = https://<acceptance-api-host>/
friends_version      = 2.0.0-alpha.1
```

The workflow uses the same `v2-build-friends.ps1` code path already exercised by normal CI and uploads the resulting ZIP + SHA-256 only when a real URL is supplied.

Launch the extracted Friends ZIP normally. Do not inject repository-local `STEWARD_*` routing variables.

## Real Friends Build acceptance

The canonical real-machine execution plan is `V2_REAL_ACCEPTANCE_BATCH.md`.

Use that runbook rather than the older Steam-specific sequence that previously lived in this document.

The first high-information path remains:

```text
real HTTPS backend ready
-> exact Friends ZIP
-> A/B private identities
-> invitation / visible membership
-> Factorio A Hosts
-> B Joins from another real network
-> safe end/capture/upload/commit
-> B later Hosts returned state
-> optional C completes A -> B -> C -> A
```

The same setup window then gathers Palworld native `IP:port` evidence, Windows UI evidence, real package timings, and the optional current-V3 7DTD lifecycle trace where available.

Do not add UPnP, STUN, relay, another public-IP service, broader forwarded-header trust, or a load balancer during the batch. Record the first measured failure boundary first.

## E4-B — later Steam production acceptance

The same provider-neutral backend architecture can later be used for E4-B, but Steam production credentials are introduced only when Steward is actually entering Steam onboarding/release acceptance.

At that later gate configure the real Steward AppID/publisher verification material and prove real Steam ticket verification plus the two-installation release path.

Do not make E4-B credentials a prerequisite for E4-A or the private Friends Build.

## Disposable resource rules

For this acceptance environment:

1. keep API-adjacent compute, PostgreSQL, object storage, and backups in the intended EU boundary where the selected provider permits it;
2. create a private bucket dedicated to the acceptance deployment;
3. create dedicated least-privilege object-storage credentials rather than account-owner credentials;
4. prefer private database networking and never expose PostgreSQL merely for convenience;
5. require HTTPS for the public Backend.Api coordinate;
6. keep Backend.Api port `8080` non-public;
7. inject credentials through files/provider secrets/environment at deploy time, never Git;
8. run one API process for the first proof;
9. collect application/container/database logs without logging friend bootstrap credentials, Steward session tokens, object-storage secrets, game passwords, or World contents;
10. delete disposable credentials/resources after acceptance evidence is recorded.

## Evidence to record

Record without secrets:

- exact Backend.Api source/head SHA;
- exact Friends ZIP version + SHA-256;
- provider/region and resource classes;
- Instance/flexible-IP identity without private credentials;
- public API hostname;
- public HTTPS/live/ready results;
- confirmation that Backend.Api sees Caddy as the exact configured trusted proxy peer;
- PostgreSQL endpoint type (private/public) and TLS mode, without connection secrets;
- bucket region and direct-transfer result;
- World/revision/generation IDs when useful to diagnose a failure;
- package sizes and transfer timings;
- reservation/acquire/reclaim outcomes;
- Factorio/Palworld Host address and reachability observations;
- recovery outcomes;
- errors and operational friction;
- measured monthly-cost inputs from the acceptance workload.

## Provider decision remains separate

A successful Scaleway acceptance run proves that this deployment shape works. It does not prove Scaleway is the best long-term commercial provider.

The final provider decision belongs in `BE_PROVIDER_EVALUATION.md` after real workload, residency, operational-friction, and cost evidence exists.
