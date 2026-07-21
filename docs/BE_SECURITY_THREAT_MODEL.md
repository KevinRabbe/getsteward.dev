# Backend Security Threat Model

This is the first-release threat model for the Steward backend. It supports
approved BE-D014 and is intentionally focused on identity, private World state,
one-writer authority, and transfer integrity.

## Assets

- Steam identity association and Steward access sessions;
- World names, membership, access invitations, and activity metadata;
- current environment/state heads and immutable revision metadata;
- opaque World package bytes and recovery candidates;
- reservation session ids, generations, and holder/device metadata;
- idempotency records and audit events;
- provider credentials, signing material, encryption keys, and backups.

The backend does not claim authority over external copies, game accounts beyond
the verified Steam identity, or realtime multiplayer traffic.

## Trust boundaries

```text
untrusted desktop/client
    -> HTTPS API authentication/authorization boundary
        |-- transactional metadata authority
        |-- private object transfer boundary
        |-- secret/key boundary
        `-- diagnostics/audit boundary
```

Steam is an external identity provider. Object storage is a byte store, not an
authorization or canonical-head authority. Adapters produce opaque packages but
do not receive backend credentials.

## Threat matrix

| Threat | Impact | Required control | Acceptance evidence |
|---|---|---|---|
| Client submits another Steam id | Unauthorized World access or commit | Server validates Steam ticket and derives identity | Modified client test fails |
| Stolen access token | Account/World actions | Short lifetime, refresh rotation/revocation, TLS, secret redaction | Revoked token is rejected |
| Guessable resource id | Metadata/package disclosure | Opaque ids plus per-resource authorization | Unauthorized enumeration test |
| Leaked transfer target | Package download/upload after access change | Short-lived, scoped, revocable targets; API authorization first | Expired/revoked target fails |
| Two clients acquire together | Competing writable sessions | Unique reservation transaction per World | Race test yields one winner |
| Old client commits after reclaim | Stale overwrite | Session generation invalidation and expected-head check | Late-generation test fails safely |
| Replay of a mutation | Duplicate access/commit/transfer action | Idempotency key and request fingerprint | Same request replays; altered request conflicts |
| Truncated or altered package | Corrupt restore or poisoned revision | Size/hash verification before publication and restore | Integrity test rejects bytes |
| Malicious metadata | Injection, resource abuse, misleading state | Schema validation, bounded fields, parameterized persistence | Fuzz/boundary validation tests |
| Provider/operator overreach | Private data exposure or mutation | Least privilege, private objects, audit, key rotation | IAM review and audit evidence |
| Log/backup disclosure | Token or save leakage | Redaction, encryption, retention/deletion policy | Diagnostic and restore inspection |
| Database/object outage | Loss of authority or data | Fail-closed readiness, backups, restore test | Outage and restore exercise |
| Abuse/resource exhaustion | Cost or availability impact | Size/rate/time/concurrency limits and alerts | Limit and high-usage tests |
| Account revocation/access removal | Unsafe interruption or stale authority | Revoke new access without silently killing active generation | Active-session revocation test |

## Authentication controls

- Accept only server-verifiable Steam authentication assertions/tickets.
- Derive the external identity from the verification response, never request
  body identity fields.
- Keep publisher/API credentials only in the backend secret boundary.
- Issue Steward sessions with bounded lifetime and refresh/revocation rules.
- Bind sensitive operations to the verified caller and, where approved, a
  registered device identity.
- Treat authentication-provider outage as inability to start a new shared
  writable session, not as permission to trust cached identity.

## Authorization controls

Every operation checks the caller against the World/revision/transfer/
reservation resource. In particular:

- listing only returns Worlds for which membership is visible;
- revision metadata and download authorization require current access;
- pending access invitations cannot download or reserve;
- revoked members cannot start new sessions or transfers;
- an active reservation may finish under its valid generation so revocation does
  not create an unsafe save interruption;
- only the matching reservation generation may heartbeat, commit, recover, or
  release.

## Transfer controls

- Object storage remains private and inaccessible through public bucket paths.
- Transfer targets identify one operation, resource, direction, and expiry.
- Upload finalization requires ordered parts, expected size, and SHA-256.
- Publication is a server-controlled state transition after verification.
- Download clients verify size/hash before adapter restore.
- Malware/archive handling is bounded at the provider/adapter boundary; the
  backend does not interpret arbitrary save semantics.

## Reservation and commit controls

The canonical safety predicate is:

```text
authorized caller
and verified candidate
and current head == expected head
and reservation session/generation matches
and reservation is commit-eligible
```

Any false condition returns a stable failure and preserves the previous head.
No timeout response is interpreted as permission to issue a competing writer.

## Residual risks accepted for first release

- Users can retain or redistribute packages after authorized download; Steward
  does not provide DRM or universal deletion.
- A compromised authorized desktop may act as that user's authority until its
  session/access is revoked and active recovery is resolved.
- EU-only authority may create slower large-package transfers for distant users;
  immutable distribution optimization is deferred until measured.
- Provider outages can leave an active session uncertain; the product preserves
  local evidence rather than promising immediate cross-device takeover.
- Game-specific package vulnerabilities or malicious mod content remain adapter
  and user-environment risks, not generic backend parsing responsibilities.

## Security acceptance gate

Before BE-2 production integration:

1. Complete a threat/control review against this matrix.
2. Test authentication forgery, token revocation, authorization enumeration,
   transfer expiry, hash mismatch, replay, and late-generation commit.
3. Review provider IAM, object privacy, encryption, backups, and key rotation.
4. Inspect logs, metrics, traces, diagnostics, and restored backups for secrets
   or package contents.
5. Exercise rate/size limits and confirm the hard-limit response is observable.
6. Record unresolved risks, owners, mitigations, and explicit release acceptance.
