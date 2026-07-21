# BE-3 S3-Compatible Transfer Checkpoint

Status: **BE-3 active; provider-neutral transfer contracts, durable transfer persistence, and the generic S3-compatible production adapter are implemented and green. The S3-compatible protocol gate is complete. HTTP/JSON control-plane exposure is the active slice.**

## Completed before this checkpoint

### Provider-neutral transfer authority

- private immutable object-storage contract;
- direct client/object-store part authorization;
- 20 GiB first-release hard package ceiling;
- approximately 64 MiB default multipart target;
- 24-hour transfer record lifetime and short-lived transfer authorizations;
- exact expected byte size and SHA-256 declared before transfer;
- active-member authorization before a new upload begins;
- state upload requires its referenced EnvironmentRevision metadata;
- transfer IDs are private to their owner;
- a previously authorized writer may finish its already-owned transfer after membership moves to `RevocationPending`;
- native/reproducible environments may remain references without pretending a hosted package exists;
- successful object publication creates immutable revision metadata only and does not advance the canonical World head.

### Integrity and immutability

- object storage never decides which revision is canonical;
- full stored-object byte count and SHA-256 are verified before package publication;
- multipart ETag is not treated as whole-object SHA-256;
- exact pre-existing immutable object may be reused only when object key, size, and hash all match;
- conflicting immutable revision reuse is rejected;
- integrity failure and publication conflict become explicit durable transfer states.

### Durable transfer persistence

PostgreSQL now persists resume-critical transfer metadata:

- World / Revision / package kind;
- owner identity;
- deterministic object key;
- opaque durable provider upload handle;
- expected byte size / SHA-256;
- required environment revision where applicable;
- part size/count;
- created/expiry timestamps;
- transfer state and finalization timestamp.

Transfer state changes use owner + expected-state compare-and-set semantics.

### Generic S3-compatible adapter

Production adapter project:

`SharedWorlds.Backend.ObjectStorage.S3`

It is configured only by:

- service endpoint;
- authentication region;
- bucket;
- access key;
- secret key;
- path-style option.

The adapter is not named after or coupled to Hetzner, AWS, MinIO, or another storage vendor.

The multipart provider handle is self-contained and durable. For S3-compatible storage it encodes the object key, native multipart upload ID, expected size, and expected SHA-256 so a backend restart does not require an in-memory upload-ID lookup table.

Implemented S3 behavior:

- initiate multipart upload;
- paginated part listing;
- presigned part PUT authorization;
- multipart completion using provider part ETags;
- idempotent completion recovery when the native upload is already gone but the exact completed object exists;
- idempotent abort;
- private presigned GET authorization;
- object deletion;
- streamed full-object SHA-256 inspection with bounded memory;
- presigned PUT/GET protocol explicitly follows the configured S3 endpoint scheme.

## S3-compatible protocol gate — COMPLETE AND GREEN

A separate S3-compatible integration test project exercises a disposable MinIO instance in CI.

MinIO is used only as a local S3 protocol compatibility harness. It is **not** a production runtime dependency and is **not** the selected commercial storage provider.

The integration proof now passes:

```text
begin multipart
-> authorize part PUTs
-> desktop-style HTTP PUT directly to object storage
-> observe uploaded parts
-> dispose first backend adapter/client
-> create fresh adapter/client
-> resume using only the persisted opaque provider handle
-> complete multipart
-> stream/verify full object size + SHA-256
-> repeat completion idempotently
-> authorize private GET
-> download identical bytes
-> delete object
```

A separate abort proof confirms repeated abort is safe and does not create a completed object.

The integration gate exposed and fixed one real provider-compatibility defect: presigned URLs had initially defaulted to HTTPS independently of an HTTP custom S3 endpoint. The production adapter now carries the configured endpoint protocol explicitly into every presigned PUT/GET request.

### Five-gate evidence

CI run `29871686999` on commit `e6995c1f1366fc95d0e7848581844ec405326559` passed:

- Quality;
- Ubuntu build/tests;
- Windows build/tests;
- PostgreSQL integration;
- S3-compatible direct-transfer integration.

## Provider selection status

Current research shows Hetzner Object Storage is a plausible first EU deployment target because it offers EU endpoints and an S3-compatible private object-storage surface suitable for this contract.

That is deployment evidence, not an architectural dependency. Production-provider selection remains subordinate to the generic BE-3 contract and must not change World authority, revision semantics, transfer limits, or recovery rules.

## Remaining BE-3 work

- add HTTPS/JSON control-plane endpoints over the already-tested application contracts;
- integrate partial/orphan cleanup and BE-D009/BE-D010 retention eligibility;
- define/implement the desktop download verification/cache/materialization boundary;
- perform provider-targeted deployment verification only after the generic transfer stack is green.

Canonical-head compare-and-swap and distributed writer reservation remain BE-4/E3 and must not leak into this milestone.
