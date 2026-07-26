# V2 Friends Build deployment

## Purpose

This is the smallest provider-neutral deployment path needed for the private Friends Build.

It deliberately does not require a published Steward Steam AppID, Steam publisher key, public account system, installer, or updater.

The runtime shape remains:

```text
Friends Build Windows clients
        |
        | HTTPS
        v
Steward Backend.Api
        |
        +-- PostgreSQL
        +-- private S3-compatible object storage
```

## 1. Backend infrastructure

Provide one reachable HTTPS `SharedWorlds.Backend.Api` deployment with:

- PostgreSQL;
- private S3-compatible object storage;
- one API instance for the first friend proof.

Required backend configuration:

```text
ConnectionStrings__Steward=<PostgreSQL connection string>

ObjectStorage__ServiceUrl=<absolute S3-compatible service URL>
ObjectStorage__AuthenticationRegion=<region>
ObjectStorage__BucketName=<private bucket>
ObjectStorage__AccessKeyId=<scoped access key>
ObjectStorage__SecretAccessKey=<scoped secret key>
ObjectStorage__ForcePathStyle=<true|false for the selected provider>

FriendsBuild__Enabled=true
```

`ConnectionStrings__Steward`, `ObjectStorage__AccessKeyId`, and `ObjectStorage__SecretAccessKey` are secrets.

Do not configure any `Steam__...` values for a Friends-Build-only deployment. Backend startup deliberately treats the complete absence of Steam settings as “Steam authentication unavailable” while Friends Build authentication remains usable.

The deployment is ready only when:

```text
GET /health/live  -> 200
GET /health/ready -> 200
```

Reuse the existing E4 live preflight for that proof rather than creating a V2-specific health checker:

```powershell
./tools/e4-live-acceptance.ps1 `
  -ApiBaseUrl "https://<your-steward-api>/" `
  -SteamAppId "" `
  -SteamWebApiIdentity ""
```

For Friends Build deployment this invocation is **backend-only**. Do not pass `-DesktopExecutable` for the Friends package. The E4 helper's executable-launch path intentionally injects `STEWARD_API_BASE_URL`; a Friends ZIP already carries its backend coordinate in adjacent `steward-friends-build.json`, and Steward correctly rejects package routing plus `STEWARD_*` routing together as ambiguous configuration.

The Friends Build desktop must therefore be launched normally from the extracted immutable ZIP, with no repository-local `STEWARD_*` routing environment variables.

### Reverse proxy client address — only when HTTPS terminates before Backend.Api

A normal HTTP reverse proxy/load balancer becomes Backend.Api's direct TCP peer. If the selected deployment terminates HTTPS at one proxy and Host presence needs the originating friend's IPv4, configure that exact proxy explicitly:

```text
ReverseProxy__KnownProxyIp=<exact IP address Backend.Api sees as its direct proxy peer>
```

The proxy must append/send the standard `X-Forwarded-For` client-address header.

With that setting Steward processes **only** `X-Forwarded-For`, from **only** that exact configured proxy, and consumes **one** forwarded hop. Requests arriving from any other peer cannot replace `HttpContext.Connection.RemoteIpAddress` through `X-Forwarded-For`.

If Backend.Api receives client connections directly, omit `ReverseProxy__KnownProxyIp`; the existing raw peer-address behavior remains unchanged.

Do not use `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` for Steward. That broad platform switch is intentionally unnecessary here because V2 needs one narrow client-address trust boundary, not general forwarded-header trust.

If the deployment uses a changing proxy fleet or multiple proxy hops, do not widen the trust model speculatively. Record that topology as the next deployment requirement and add only the smallest explicit trusted boundary it actually needs.

## 2. Provision the private identities

Create one identity per person with the repository helper:

```powershell
./tools/v2-provision-friend.ps1 -DisplayName "Kevin" -Index 0
./tools/v2-provision-friend.ps1 -DisplayName "Alex"  -Index 1
```

Each invocation generates:

- a random stable opaque external ID;
- a `st_friend_...` bootstrap credential containing 256 random bits;
- the SHA-256 digest required by backend configuration.

The command prints exact provider-neutral configuration keys such as:

```text
FriendsBuild__Identities__0__Id=<opaque id>
FriendsBuild__Identities__0__DisplayName=Kevin
FriendsBuild__Identities__0__CredentialSha256=<SHA-256 digest>
```

Only the digest belongs in backend configuration. Send the plaintext `st_friend_...` credential directly to the intended person; do not put it in the backend configuration, Friends ZIP, repository, Discord download post, or CI variables.

Indices are bounded to `0..63`, matching the backend's maximum configured Friends Build identities.

For automation or local verification, the helper can write the digest-only configuration and plaintext credential to separate files:

```powershell
./tools/v2-provision-friend.ps1 `
  -DisplayName "Alex" `
  -Index 1 `
  -ConfigurationOutputPath ./alex-backend-config.json `
  -CredentialOutputPath ./alex-private-code.txt `
  -Quiet
```

Treat the credential output file as a secret and delete it after delivery.

## 3. Build the exact Friends ZIP

Build locally:

```powershell
./tools/v2-build-friends.ps1 `
  -ApiBaseUrl "https://<your-steward-api>/" `
  -Version "2.0.0-alpha.1"
```

The result is:

```text
Steward-2.0.0-alpha.1-win-x64.zip
Steward-2.0.0-alpha.1-win-x64.zip.sha256
```

The ZIP contains the non-secret backend HTTPS coordinate in `steward-friends-build.json`. It contains no friend bootstrap credential and no backend secret.

The same artifact can be produced by manually dispatching the repository's `Windows acceptance package` workflow with:

```text
friends_api_base_url = https://<your-steward-api>/
friends_version      = 2.0.0-alpha.1
```

That workflow uses the same `tools/v2-build-friends.ps1` path exercised by normal CI.

## 4. Private distribution

The intended first distribution path is deliberately simple:

```text
upload versioned ZIP to Google Drive
-> post Drive link + SHA-256 in the private Discord server
-> friend downloads
-> extract
-> run SharedWorlds.Desktop.exe
```

Send each friend's private `st_friend_...` bootstrap credential separately from the public-to-the-group download post.

On first successful login the Desktop protects that long-lived bootstrap credential using Windows DPAPI for the current Windows user. Normal Steward access/refresh session credentials remain process-memory state.

## 5. First real proof

Use Factorio first.

The minimum real sequence is:

```text
PC A launches the exact Friends ZIP
-> authenticates with private code
-> creates or imports Factorio World
-> shares World
-> invites PC B by visible friend name
-> PC B accepts
-> both see the same World membership list
-> A Hosts
-> B Joins through the existing Factorio Host/Join path
-> A ends the session
-> Steward captures/uploads/commits the new World state
-> B later Hosts the resulting current state
-> A later continues the state returned by B
```

The first broader acceptance target remains `A -> B -> C -> A` across separate real sessions.

## Not added for this deployment

Do not add a public account service, email/password recovery, Steam publication, updater, installer, public server browser, social graph, chat, or a second World/lobby authority merely to run the private proof.
