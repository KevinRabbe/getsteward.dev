# Backend Security Threat Model

Status: **CURRENT FIRST-RELEASE THREAT MODEL — DETERMINISTIC CONTROLS IMPLEMENTED; REAL PROVIDER/STEAM SECURITY ACCEPTANCE REMAINS EXTERNAL.**

This threat model focuses on the assets Steward actually owns: verified identity/session authority, private shared World data, one-writer coordination, immutable transfer integrity, and recovery evidence.

## Assets

- verified external identity association;
- normal Steward access/refresh sessions;
- private Friends Build bootstrap credentials when that private mode is explicitly enabled;
- World names/membership/invitations/access activity;
- current environment/state heads and immutable revision metadata;
- opaque World package bytes/recovery candidates;
- reservation session IDs/generations/holder installation metadata;
- Host-presence endpoint/join material;
- idempotency/retention metadata;
- database/object-store/publisher/provider credentials;
- encrypted backups and operational audit evidence.

The backend does not claim authority over:

- external package copies already downloaded by an authorized user;
- Steam/game accounts beyond the verified identity needed by Steward;
- realtime multiplayer gameplay traffic;
- arbitrary player progression inside game saves.

## Trust boundaries

```text
untrusted Desktop/client
    -> HTTPS API authentication/authorization boundary
        |-- PostgreSQL transactional authority
        |-- private object-transfer authorization
        |-- secret/provider boundary
        `-- diagnostics/operations boundary

Desktop
    -> direct scoped object-storage transfer
       only after Backend.Api authorization
```

Production Steam is an external identity provider. Private Friends Build proof is a separately configured private-test identity source that still enters the same normal Steward session/authorization machinery.

Object storage is a byte store, not authorization/current-head authority.

Adapters produce/consume opaque packages and game-specific connection material but do not receive backend infrastructure credentials.

## Threat matrix

| Threat | Impact | Required control | Current/required evidence |
|---|---|---|---|
| client supplies another Steam identity | unauthorized World access/commit | server verifies ticket and derives identity | deterministic forgery/mismatch tests + real V3-F Steam verification |
| leaked Friends Build bootstrap secret | private-test impersonation | high-entropy credential, digest-only backend config, Windows user protection, revocation, disabled production mode | provisioning/auth tests; production release proves Friends disabled |
| stolen Steward access/refresh credential | account/World actions | short lifetime, refresh rotation/revocation, installation binding where required, TLS, redaction | session/revocation tests |
| guessed resource ID | metadata/package disclosure | opaque IDs + per-resource authorization + privacy-preserving absence | authorization/enumeration tests |
| leaked direct-transfer authorization | unauthorized package transfer | short-lived operation/resource-scoped target + API authorization first | expiry/scope/integrity tests |
| two clients acquire concurrently | competing writers | transactional one-reservation-generation authority | PostgreSQL race tests yield one safe winner |
| old client commits after reclaim | stale overwrite | generation invalidation + expected-head check | late-generation rejection tests |
| replayed authority mutation | duplicate transition | durable idempotency key + request fingerprint/result | same request replays; altered reuse conflicts |
| altered/truncated/oversized package | corrupt canonical state/resource exhaustion | declared size/hash, streamed bounds, verify before publication/restore | transfer/cache/integrity tests |
| valid metadata transplanted to wrong identity/path | state confusion | persisted integrity + storage identity binding | transplant/path-binding tests |
| malicious/oversized metadata | injection/resource exhaustion | bounded schema/value validation + parameterized persistence | boundary/oversize tests |
| forged forwarded client address | publish attacker-controlled Host endpoint | raw peer by default; exactly configured trusted one-proxy forwarded-header boundary only | proxy tests + real deployment observation |
| stale Host presence | Join points at old Host | bind presence to exact Active reservation session/generation/expiry | PostgreSQL Host-presence tests |
| provider/operator overreach | private data exposure/mutation | least privilege, private buckets/DB access, secrets, audit/backup policy | real provider IAM/security review |
| log/backup disclosure | token/save/secret leakage | redaction, encryption, bounded diagnostics, controlled retention | diagnostic/restore inspection |
| database/object outage | unavailable/uncertain authority | fail-closed readiness, durable reservation semantics, backup/restore | deterministic outage tests + real provider exercise |
| abuse/high usage | availability/cost exhaustion | package/control/cache/worker/time bounds + operational limits | limit/integration tests + real measurements |
| membership revocation during active write | lost gameplay/stale authority | revoke future access while preserving valid active generation until safe resolution | active-writer revocation tests |

## Authentication controls

### Production Steam

- `SteamAPI`/client obtains a Web API ticket for the packaged service identity.
- Backend verifies the ticket with the server-side publisher credential and configured AppID/identity.
- Backend derives SteamID64 from the verification result; request-body identity claims are not trusted.
- The publisher API key never enters Desktop/depot/evidence artifacts.
- Production Desktop normal sessions are bounded/rotating Steward credentials, not Steam tickets reused on every request.
- Production launch reauthenticates through Steam rather than storing ordinary Steward refresh credentials in device settings.

### Private Friends Build

Only when explicitly enabled/configured:

- bootstrap credential is high entropy;
- backend configuration stores its digest, not plaintext;
- Desktop protects the long-lived bootstrap artifact for the current Windows user;
- successful proof issues the same normal Steward session type used after other external identity proof;
- rotating normal access/refresh credentials remain process-memory;
- production Steam release configuration requires `FriendsBuild__Enabled=false`.

The private path must never become an automatic fallback when Steam verification fails.

### Common session rules

- bounded access/refresh lifetime;
- refresh rotation/revocation;
- server persists hashes rather than plaintext normal credentials;
- credentials are not logged;
- authentication-session expiry never releases/changes unresolved writable World authority.

## Authorization controls

Every protected operation checks the verified caller against the specific World/revision/transfer/reservation resource.

At minimum:

- World list returns only visible memberships;
- revision metadata/download authorization require current access;
- pending invitations cannot download/reserve/commit;
- revoked members cannot begin new protected work;
- an already-valid active writer may finish according to the revocation-pending contract rather than being destroyed administratively;
- reservation heartbeat/commit/Host presence require matching authority identity;
- Access Manager grants membership administration only, not write priority;
- transfer IDs/opaque resource IDs do not bypass caller/resource checks.

## Transfer controls

- Object storage remains private.
- Backend authorizes each logical transfer/resource before direct byte movement.
- Upload declares expected total size/SHA-256 before publication.
- Multipart parts are bounded and scoped.
- Finalization verifies provider/object result before revision publication.
- Download authorization returns expected size/hash; Desktop verifies before adapter restore.
- Remote plaintext non-loopback transfer/API targets are rejected.
- Credential-bearing/direct-transfer HTTP clients do not silently follow redirects.
- Backend does not parse arbitrary game save semantics as a generic security scanner.

## Reservation/commit controls

Canonical safety predicate:

```text
authorized caller
AND verified candidate
AND current state/environment head == expected starting head
AND exact reservation session/generation/installation remains valid
AND reservation is commit-eligible
-> atomic canonical head advancement
```

Any false condition preserves the previous canonical head.

A timeout is an unknown result, not evidence that a competing writer may start.

`Active -> Uncertain` does not create availability. Deliberate reclaim invalidates old generation authority before future acquisition.

## Host-presence/network controls

Host presence is convenience/read-only connection evidence, not authority.

- writes are bound to the exact Active reservation session/generation;
- stale/uncertain/superseded generations are rejected;
- Ready presence expires/refreshes under the owning authority;
- an adapter-supplied explicit address is preserved;
- otherwise Backend.Api may use `HttpContext.Connection.RemoteIpAddress` after the deployment's qualified peer normalization;
- Host-presence code does not parse arbitrary `X-Forwarded-For` itself;
- forwarded client address is accepted only through the exactly configured one-proxy trust boundary;
- no provider-wide CIDR/public-IP/STUN/UPnP/relay trust system is added without measured need.

## Local persistence/security boundary

Local metadata and recovery state are also trust boundaries:

- persisted JSON reads are bounded;
- current schemas require integrity where defined;
- state revision metadata binds payload SHA-256;
- storage path/resource identity is checked;
- unknown future schemas fail closed;
- unresolved recovery workspaces/candidates are not deleted to clear an error state;
- local diagnostics are bounded/redacted and unknown dispatcher failures remain fatal rather than continuing in unknown state.

## Residual risks accepted for first release

- Authorized users can retain/redistribute packages after download; Steward is not DRM.
- A compromised authorized Desktop can act with that user's granted authority until credentials/access/reservation responsibility are safely resolved.
- EU-central authority can be slower for distant users; CDN/replication/peer assistance is measurement-driven, not preemptive.
- Provider outages can leave authority Uncertain; Steward favors preserved evidence over instant takeover.
- Game-specific malicious mods/save vulnerabilities remain adapter/game/user-environment concerns unless Steward itself introduces a parsing/execution boundary.
- Direct Internet game hosting may require ordinary router/firewall configuration or later measured traversal work; Steward does not claim generic NAT traversal today.

## Deterministic security evidence already present

Repository tests/integration currently cover substantial parts of this model, including:

- Steam ticket/provider bounds and fail-closed configuration;
- normal session issuance/refresh/revocation;
- Friends Build proof/configuration isolation;
- access/invitation authorization;
- reservation races/generation/reclaim/late-writer rejection;
- idempotent authority mutations;
- package size/hash/oversize/direct-transfer security;
- persisted integrity/identity binding/oversize reads;
- transport HTTPS/non-loopback and redirect restrictions;
- exact trusted-proxy forwarded-address behavior;
- Host-presence authority binding;
- PostgreSQL backup/restore integration;
- bounded redacted diagnostics.

Passing deterministic tests does not replace real provider/Steam operational review.

## Remaining security acceptance

Before public Steam release, the real V3-E/V3-F environment must still provide evidence for:

1. real production Steam AppID/ticket/publisher-verification path with no fallback;
2. production Friends Build authentication disabled;
3. provider IAM/private DB/object-storage/secret access configuration;
4. public HTTPS and exact reverse-proxy trust on the deployed topology;
5. provider backup + isolated restore validation;
6. logs/metrics/backups inspected for credentials/package leakage;
7. two independent real installations/account authorization behavior;
8. real Host/Join endpoint/reachability behavior for advertised games;
9. representative resource/cost/abuse observations;
10. any residual risk discovered by the real acceptance batch is recorded and resolved/accepted explicitly.

## Final rule

> **Security mechanisms protect the small authority Steward actually owns. Do not expand the trusted surface merely because a provider/game exposes another feature.**