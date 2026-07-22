# BE-5 Status — Real Two-Device Handoff Proof

Status: **COMPLETE AND GREEN**

BE-5 proves the first real cross-device Steward handoff using the production contracts introduced by BE-2, BE-3, and BE-4. The proof does not use a second local authority model and does not let object storage decide what is canonical.

## What is proven

The composed development stack now executes the required handoff:

```text
canonical N
-> PC A downloads/verifies/restores N
-> PC A acquires the one writable reservation
-> PC A captures and uploads immutable candidate N+1
-> backend verifies N+1
-> PC A commits N+1 with the exact reservation generation
-> PC B downloads/verifies/restores N+1
-> PC B acquires the next writable reservation
-> PC B captures and uploads immutable candidate N+2
-> backend verifies N+2
-> PC B commits N+2
-> PC A downloads/verifies/restores canonical N+2
```

The development proof uses:

- the real Core `WorldLifecycleService`;
- the real Infrastructure `StewardWorldSessionCoordinator`;
- the real Infrastructure `StewardWorldStorage`;
- real verified desktop download/cache/materialization;
- real resumable desktop multipart upload logic;
- the real versioned HTTP endpoint implementations through ASP.NET TestServer;
- real PostgreSQL World, revision, transfer, session, authority, idempotency, and access stores;
- a byte-preserving object-store HTTP test transport at the external storage boundary only.

The object-store double stores and returns the actual package bytes. It does not decide head state, reservation state, membership, generation, publication, or recovery outcome.

## Runtime integration completed for the proof

### Authenticated desktop control plane

Infrastructure now provides typed desktop clients for:

- Steward session authentication/refresh/revoke;
- accessible World/current-head metadata;
- arbitrary immutable state/environment revision metadata;
- acquire/get/heartbeat/reclaim/commit authority operations;
- exact pre-launch reservation abandonment;
- direct package download authorization;
- resumable multipart package upload.

`StewardAccessSession` serializes refresh-token rotation and avoids duplicate concurrent refreshes.

### Distributed session coordinator

`StewardWorldSessionCoordinator` implements the existing Core `IWorldSessionCoordinator` against BE-4 authority.

It proves:

- canonical-head acquisition;
- one-writer enforcement;
- bounded same-idempotency-key retry when acquire outcome is ambiguous;
- refusal to adopt another Steward installation's reservation;
- periodic heartbeat;
- conservative mapping of remote `Uncertain` to Core recovery responsibility;
- exact reservation abandonment when writable gameplay never started;
- preservation of the reservation while a post-launch workspace remains `RecoveryPending`.

### Remote World storage

`StewardWorldStorage` implements the existing Core `IWorldStorage` boundary for shared Worlds.

It provides:

- shared World listing/loading;
- immutable environment-manifest publication/loading;
- immutable state metadata loading;
- verified package open/download;
- resumable candidate upload;
- exact-generation expected-head canonical commit;
- deterministic commit idempotency keys;
- exact local lease resolution only after a definitive `Committed` or `Unchanged` result.

An ambiguous commit response never resolves the local writable responsibility.

### Structured environment revisions

Remote continuation requires Core's full game-agnostic `EnvironmentManifest`, not merely an opaque object reference.

BE-5 therefore adds optional structured manifest metadata to shared environment revisions:

- PostgreSQL `manifest_json jsonb NULL`;
- authenticated immutable environment-manifest publication;
- semantic idempotency/conflict comparison;
- manifest-aware revision reads;
- fail-closed behavior for legacy remote environment rows that do not contain a structured manifest.

Hosted environment-package bytes remain optional. The structured manifest is the runtime contract adapters consume.

## Waiting-to-sync recovery

BE-5 closes the ambiguous-post-session recovery loop instead of merely preserving a blocked workspace.

The lifecycle now persists the candidate revision ID before candidate publication:

```text
capture
-> allocate candidate revision ID
-> persist CandidateStateRevisionId in workspace recovery
-> publish/verify immutable candidate
-> commit exact reservation generation
```

This recovery journal is monotonic for the session: once a candidate ID is known, the lifecycle keeps that same candidate identity through later `RecoveryPending`/`CleanupPending` status changes.

`StewardPendingSyncRecoveryService` resolves recovery from canonical backend truth:

```text
canonical == candidate
-> the original commit succeeded
-> do not recapture or recommit
-> finish local workspace cleanup

canonical == recorded base
-> candidate did not become canonical yet
-> acquire/reconnect authority
-> reuse the journaled candidate ID
-> if candidate is already published, reuse it
-> otherwise recapture from the preserved workspace and publish under the same candidate ID
-> commit normally

canonical != base AND canonical != candidate
-> another canonical head exists
-> do not overwrite it
-> release any newly acquired recovery reservation exactly
-> preserve recovery evidence for explicit resolution
```

This means a lost successful HTTP commit response cannot create a second logical candidate or cause the previous canonical state to be overwritten.

## Adverse-path evidence

BE-5 also proves the required failure behavior.

### Competing writer rejection

A second device is rejected while the first device has an active reservation.

### Outage and uncertainty

After missed heartbeat threshold, the reservation becomes `Uncertain`; Steward does not manufacture availability.

### Deliberate reclaim

After the configured uncertainty grace period, another active member can deliberately reclaim the exact uncertain generation.

### Generation invalidation

The next reservation receives a higher generation. The invalidated old writer can no longer heartbeat or commit.

### Interrupted transfer resume

Desktop upload tests prove:

- completed multipart parts are not uploaded again;
- an object-store failure leaves the transfer resumable;
- provider-completed progress skips redundant part writes;
- Steward bearer credentials are never sent to the direct object-storage URL;
- corrupt provider/backend progress fails closed.

### Ambiguous canonical commit

Storage tests prove:

- retry uses the same deterministic idempotency key and exact request;
- unknown outcome preserves the writable lease;
- definitive head conflict preserves candidate/recovery responsibility instead of releasing it.

### Lost successful response

The PostgreSQL/TestServer recovery proof deliberately allows the backend commit to succeed, discards the successful HTTP response, and forces the desktop lifecycle into `RecoveryPending`.

The recovery service then observes that the journaled candidate is already canonical and:

- performs no recapture;
- creates no additional state revision;
- cleans the preserved workspace;
- removes the recovery record;
- converges any stale local lease;
- leaves no backend reservation.

### Diverged canonical head

Recovery tests prove that when the current canonical head is neither the recorded base nor the recorded candidate, Steward refuses automatic overwrite and preserves evidence.

## Acceptance evidence

Key composed tests:

- `PostgreSqlTwoDeviceHandoffTests`
  - PC A -> PC B -> PC A verified canonical handoff;
- `PostgreSqlTwoDeviceAuthorityApiTests`
  - competing writer, Uncertain, credential refresh across grace time, deliberate reclaim, generation increase, late heartbeat/commit rejection;
- `PostgreSqlPendingSyncRecoveryTests`
  - lost successful commit response -> `RecoveryPending` -> deterministic completion without another revision.

Supporting fault/conformance coverage includes:

- `StewardPackageUploadClientTests`;
- `StewardWorldStorageTests`;
- `StewardWorldStorageRecoveryJournalTests`;
- `StewardPendingSyncRecoveryServiceTests`;
- BE-3 direct-transfer/S3 tests;
- BE-4 PostgreSQL authority/idempotency tests.

## CI proof

GitHub Actions CI run `29916875172` on commit `3fca52a9014b6799428a2882de73762ba97a3da4` passed:

- Quality / formatter verification ✅
- Ubuntu Release build + tests ✅
- Windows Release build + tests ✅
- live PostgreSQL integration ✅
- S3-compatible direct-transfer integration ✅

The PostgreSQL gate includes the real lost-response recovery proof after Core candidate journaling was fixed.

Earlier composed evidence on the same implementation line includes:

- happy-path PC A -> PC B -> PC A PostgreSQL handoff in CI run `29914756373`;
- adverse two-device authority/reclaim/late-writer proof in CI run `29915074063`.

## What BE-5 does not claim

BE-5 proves the development composition. It does **not** claim that the current Windows Desktop composition root already uses this remote stack.

At this checkpoint, `SharedWorlds.Desktop.MainWindow` still constructs:

```text
LocalWorldStorage
+ LocalWorldSessionCoordinator
+ LocalWorkspaceRecoveryStore
```

Therefore:

- BE-5 / E5 development acceptance is complete;
- E4 product/background runtime wiring remains active until the Windows Desktop application composes authenticated Steward sessions, `StewardWorldStorage`, `StewardWorldSessionCoordinator`, recovery, and the user-visible lifecycle states.

## Next product integration target

Continue E4 by replacing the Desktop's shared-World path with the proven remote composition while preserving local-only Worlds and the existing adapter boundary.

The next integration should not redesign authority or transfer. It should compose the already-proven contracts and expose their truthful states to the UI.

Before remote Join/Host is treated as product-ready, the exact-version environment lock and Verify/Repair path must also consume the structured environment manifest rather than assuming the local installation matches automatically.
