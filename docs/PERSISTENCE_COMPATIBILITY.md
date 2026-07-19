# Persistence Compatibility

## Goal

Persisted product data must remain readable and safely migratable as the application evolves. A newer build must not silently interpret an unknown document shape as if it were current data.

## Outer document envelope

Every JSON document written by the local persistence layer is wrapped in an outer envelope:

```json
{
  "documentType": "sharedworlds.world",
  "schemaVersion": 1,
  "payload": {
    "...": "document-specific data"
  }
}
```

The envelope is separate from domain-level versions such as `EnvironmentManifest.SchemaVersion`.

- `documentType` identifies the persisted contract.
- `schemaVersion` versions the outer persisted representation for that document type.
- `payload` contains the current domain data.

Current document types:

- `sharedworlds.world`
- `sharedworlds.environment-revision`
- `sharedworlds.state-revision`
- `sharedworlds.workspace-recovery`

## Migration model

Each document type has a schema definition containing:

- its stable document type identifier
- its current schema version
- explicit migrations from supported older versions

The initial pre-envelope format is treated as **schema version 0**. Version 0 consists of the raw domain object at the JSON root. A registered migration reads that payload into the current schema-1 domain model.

This means data written by the initial foundation can still be read after envelopes are introduced.

Future migrations should be added explicitly to the schema registry. Do not make deserializers guess at unknown historical formats.

## Unknown future versions

If persisted data has a schema version newer or otherwise unsupported by the running build, loading fails with `PersistedDataCompatibilityException`.

The application must surface this as a controlled compatibility problem. It must not:

- discard the file
- overwrite it with defaults
- pretend the World does not exist
- partially deserialize it and continue

A common real-world cause is opening data written by a newer application build with an older build.

## Document-type mismatch

A file whose envelope declares a different document type than the location expects is treated as invalid data. For example, a state-revision document must never be accepted as World metadata merely because some fields happen to deserialize.

## Write policy

New writes always use the current envelope version.

Legacy documents are migrated in memory when read. Automatic rewrite-on-read is intentionally not required. This avoids mutating durable user data merely because it was inspected. A future explicit storage migration command may rewrite validated legacy documents transactionally.

## Compatibility rule

Persisted schemas are product contracts.

Changes should prefer additive evolution. When an incompatible change is necessary:

1. increment the relevant outer schema version
2. add an explicit migration when safe
3. add compatibility tests using representative old data
4. fail with a typed compatibility error when no safe migration exists
5. never silently reinterpret unknown data
