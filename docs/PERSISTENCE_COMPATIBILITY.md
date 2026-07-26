# Persistence Compatibility

Status: **CURRENT — LOCAL WORLD/RECOVERY DOCUMENTS USE VERSIONED INTEGRITY-PROTECTED SCHEMAS; DESKTOP DEVICE SETTINGS HAVE A SEPARATE V1→V2 MIGRATION CONTRACT.**

## Goal

Persisted Steward product data must remain readable, integrity-checked, and safely migratable as the application evolves.

A build must never:

- silently reinterpret an unknown future schema;
- accept a document stored under the wrong identity merely because its JSON/hash is valid;
- overwrite incompatible data with defaults;
- drop unresolved recovery state to make startup succeed;
- mutate user-owned persistence merely because it was inspected.

Persisted schema is a product contract.

## Local World/revision/recovery envelope

`SharedWorlds.Infrastructure` uses a common bounded persisted-document codec for local World/revision/recovery JSON.

Current new-write envelope shape is conceptually:

```json
{
  "documentType": "sharedworlds.world",
  "schemaVersion": 2,
  "integrityVersion": 1,
  "contentSha256": "...",
  "payload": {
    "...": "document-specific data"
  }
}
```

The exact current schema version depends on document type.

Fields:

- `documentType` — stable persisted contract identity;
- `schemaVersion` — version of that document's payload representation;
- `integrityVersion` — version of the persisted integrity mechanism;
- `contentSha256` — integrity proof over the document identity/version/logical payload;
- `payload` — current persisted domain data.

This envelope version is independent from domain-specific versions such as `EnvironmentManifest.SchemaVersion`.

## Current local storage document types

Current generic storage schemas are:

| Document type | Current schema | Integrity required from | Purpose |
|---|---:|---:|---|
| `sharedworlds.world` | 2 | 2 | mutable World/current-head metadata |
| `sharedworlds.environment-revision` | 2 | 2 | immutable environment revision metadata |
| `sharedworlds.state-revision` | 4 | 3 | immutable state revision metadata + current payload binding |
| `sharedworlds.workspace-recovery` | 2 | 2 | durable prepared-workspace/recovery responsibility |

New writes use the current schema and current integrity mechanism.

## Bounded reads

Persisted JSON is a trust boundary even when it is local.

The common codec rejects a JSON document larger than the configured hard safety ceiling before unbounded parsing/allocation. Current ceiling is:

> **16 MiB per persisted JSON document**

Non-seekable streams are also bounded while buffering rather than bypassing the limit.

Large game package bytes do not pass through this JSON codec.

## Integrity semantics

For schema versions that require integrity, a missing or malformed integrity proof is a hard failure.

Current integrity validation checks:

- supported integrity version;
- well-formed SHA-256 digest;
- fixed-time digest comparison;
- exact document type/schema/payload content relationship.

Older explicitly supported schema versions that predate in-envelope integrity remain readable through defined migrations/legacy handling. A current-format document is never accepted without its required proof merely to preserve compatibility.

## State revision payload binding

State revisions need a stronger relationship than “revision metadata JSON is valid.” The revision metadata must also identify the immutable `payload.bin` it belongs to.

Current state-revision schema v4 stores the payload SHA-256 inside the integrity-protected `revision.json` payload representation.

Conceptually:

```text
revision.json
    protected document identity/version
    revision metadata
    payloadSha256

payload.bin
    immutable game state bytes
```

This prevents a valid payload from one revision directory being silently transplanted under another valid revision metadata document.

Historical state-revision schemas remain explicitly readable:

- schema 0 — pre-envelope legacy representation;
- schema 1 — initial envelope era;
- schema 2 — payload integrity companion existed but revision JSON itself predated protected envelope integrity;
- schema 3 — revision JSON protected, payload digest still represented by the legacy companion path;
- schema 4 — current payload digest is bound inside protected revision metadata.

The old `payload.sha256` form is legacy compatibility, not the new-write design.

## Storage identity binding

Valid contents do not make a document valid everywhere.

Local storage also verifies that persisted identity matches the location/World/revision/workspace being loaded.

Examples of the rule:

- a valid World document stored beneath the wrong World identity is rejected;
- a valid revision document cannot be transplanted into another revision identity/path and become trusted;
- a recovery record is not accepted merely because its JSON/integrity is valid if the surrounding storage identity contradicts it.

Paths are locators plus trust-boundary context, not a substitute for domain identity.

## Migration model

Each common storage document schema declares:

- stable document type;
- current schema version;
- first version that requires current integrity;
- explicit migration readers for supported older versions.

The original pre-envelope representation is treated as schema version `0` for supported document types.

Migration happens in memory unless an explicit write/operation requires persistence of the new representation.

Do not make deserializers “best effort” unknown fields/versions into a guessed current object.

## Unknown future versions

If a persisted common-storage document declares a schema newer than the running build supports, loading fails as a controlled compatibility problem rather than corruption/default data.

The application must not:

- delete the file;
- overwrite it with a new empty/default document;
- pretend the World/recovery record does not exist;
- partially interpret the future representation and continue.

A common real cause is opening data written by a newer Steward build with an older one.

## Document-type mismatch

A document whose `documentType` does not match the storage location's expected contract is invalid.

For example, a valid state-revision envelope must not be accepted as World metadata merely because fields happen to deserialize.

Integrity does not authorize type confusion.

## Device settings are a separate persisted contract

Desktop device settings are intentionally not the same document codec as World/revision/recovery storage.

Current Desktop settings document:

```text
documentType = sharedworlds.device-settings
schemaVersion = 2
```

Current v2 payload contains:

- `allowHosting`;
- `hostingPreferenceExplicit`;
- stable Steward `installationId`.

### v1 -> v2 migration

Schema v1 predates the stable installation identity used by remote Steward authority.

Migration:

```text
read supported v1 envelope
-> preserve hosting preference fields
-> create exactly one stable installation ID
-> close source read handle
-> atomically replace settings with v2
```

The source read boundary matters on Windows. Real V3-E acceptance exposed that attempting to replace the same file while its read stream remained open fails under Windows file-sharing semantics. PR #138 corrected the lifecycle by closing the read handle before the migration write and added a Windows-relevant regression.

That fix did not change the schema meaning. It fixed the persistence operation ordering required to make the existing migration contract work on the real target OS.

Unsupported/malformed device-settings versions fail closed for remote authority. Desktop may keep local-only behavior usable, but a temporary fallback installation identity is never used as durable shared authority.

## Device-settings write policy

Current device settings writes use:

```text
serialize current v2 envelope to unique temporary file
-> close write handle
-> replace/move temporary file over destination
-> remove leftover temporary file on failure where safe
```

The stable installation ID is validated before write/use.

## Friends Build credential is not a normal persisted session document

The long-lived private Friends Build bootstrap credential is a separate Windows-protected credential artifact, not a normal Steward access/refresh token and not a World metadata document.

Current private-test rule:

```text
Friends bootstrap credential
-> protect for current Windows user
-> persist protected binary artifact
-> exchange on launch for fresh normal Steward session credentials
```

Normal rotating Steward access/refresh credentials remain process-memory state.

Do not expand World persistence schemas to store authentication secrets.

## Release configuration is not mutable product persistence

Adjacent package-owned release configuration such as `steward-steam-release.json` is immutable release input, not user-data migration state.

It has its own strict bounded package/configuration validation and must not be silently rewritten by normal Desktop persistence migration.

## Write/migration policy

For persisted product documents:

1. validate trust boundary and current identity before mutation;
2. read only explicitly supported versions;
3. migrate through explicit code, not guessed deserialization;
4. preserve data semantics/identity across migration;
5. write current representation through an owned temporary/atomic replacement boundary where appropriate;
6. add regression tests using real representative old data;
7. fail closed when safe migration is impossible;
8. preserve unknown/newer data rather than overwriting it.

## Compatibility vs integrity

Compatibility and integrity are different questions:

```text
Is this schema version supported?
AND
Is this document/payload integrity valid for that version?
AND
Does its identity match the storage location/resource being loaded?
```

All required checks must pass.

A legacy version may be supported without having the same integrity metadata as a current write. A current-version document missing required integrity is still invalid.

## Testing rule

Every persistence evolution should include, where applicable:

- current write/read round trip;
- supported legacy migration fixture;
- unknown future version rejection;
- document-type mismatch rejection;
- integrity removal/tamper rejection;
- storage-identity transplant rejection;
- oversize document rejection;
- interrupted/atomic-write behavior;
- target-OS behavior when filesystem replacement semantics matter.

The #138 Windows device-settings migration defect is the model for why target-platform persistence behavior belongs in acceptance/regression evidence rather than being inferred from Linux-only success.

## Final rule

> **Persisted data is user/product state, not disposable serialization output. Version it explicitly, verify it, migrate it deliberately, and never guess when the evidence is insufficient.**