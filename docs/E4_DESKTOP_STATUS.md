# E4 Windows Desktop Remote Composition Status

Status: **CODE COMPOSITION + PENDING-SYNC RECOVERY COMPLETE AND CI GREEN; LIVE STEAM/BACKEND ACCEPTANCE STILL REQUIRED.**

This checkpoint records the point where the production Windows Desktop stopped being structurally local-only. The existing `Only on this PC` path remains local, while authenticated shared Worlds can now use the same remote storage, authority, transfer, commit, and recovery components already proven by BE-5.

## What is now composed in Desktop

The authenticated shared-World runtime owns and connects:

- `StewardSessionClient` + rotating `StewardAccessSession`;
- `StewardWorldMetadataClient`;
- `StewardAuthorityClient`;
- `StewardReservationAbandonClient`;
- `StewardPackageDownloadClient` + `VerifiedPackageCache` + `StewardVerifiedPackageSource`;
- `StewardPackageUploadClient`;
- `StewardWritableReservationRegistry`;
- `StewardWorldSessionCoordinator`;
- `StewardWorldStorage`;
- one `ManagedWritableSessionGate` shared by normal lifecycle and pending-sync recovery;
- `WorldLifecycleService`;
- `StewardPendingSyncRecoveryService`.

Desktop owns those resources through `StewardDesktopRemoteRuntime` and disposes them when the window/runtime ends.

## Local and shared Worlds use different authoritative paths

Desktop no longer decides runtime behavior from a loose `SharingMode` flag alone.

```text
local World ID
    -> LocalWorldStorage
    -> LocalWorldSessionCoordinator

World ID returned by authenticated Steward backend
    -> StewardWorldStorage
    -> StewardWorldSessionCoordinator
```

When the same World ID exists in the old local store and in authenticated Steward metadata, the backend World is displayed as the canonical shared World. The old local bytes are left untouched rather than deleted or merged.

A remote outage does not make private local Worlds unusable. Desktop keeps the local library available and reports shared Worlds as temporarily unavailable.

A stale local record marked `Shared` never falls back to local writable authority. Continue/Host, Verify/Repair, and shared environment-policy mutation remain blocked until authenticated Steward authority for that World is present.

## Exact environment is a real shared-play gate

For a backend World, Verify/Repair reads the canonical structured `EnvironmentManifest` through `StewardWorldStorage`.

Shared Continue/Host remains disabled until verification reports `Ready`, and the action handlers independently reject an unverified shared World. Local-only Worlds retain their existing behavior.

Remote environment-version mutation is intentionally not faked through a local checkbox. The current canonical backend environment remains read-only until an explicit reviewed environment-transition operation exists.

## Installation-bound identity

Desktop device settings are schema version 2 and persist one stable Steward installation ID. Existing schema-v1 settings migrate while preserving the user's hosting preference.

The installation ID is used for Steward authentication and distributed reservation ownership. It is not regenerated on each launch.

If durable device settings cannot be loaded/created, the temporary fallback identity is never used for remote authority. Shared functionality fails closed for that launch while local Worlds remain usable.

Steward access/refresh credentials remain process-memory state. Desktop re-authenticates through Steam on a new launch instead of writing refresh credentials to the ordinary device settings JSON.

## Steam authentication boundary

Desktop now has the real client-side Steam Web API ticket path:

```text
SteamAPI.Init
-> verify configured AppID
-> SteamUser.GetAuthTicketForWebApi(identity)
-> pump Steam callbacks
-> exact GetTicketForWebApiResponse_t ticket bytes
-> StewardSessionClient.AuthenticateSteamAsync
-> server verifies ticket with Steam
-> Steward access + refresh credentials
-> authenticated remote runtime
```

No development AppID is silently guessed or hard-coded.

Remote sharing is currently enabled only when all three explicit deployment values are supplied:

- `STEWARD_API_BASE_URL`;
- `STEWARD_STEAM_APP_ID`;
- `STEWARD_STEAM_WEB_API_IDENTITY`.

With none configured, Steward stays local-only. Partial/invalid configuration fails closed for shared functionality without disabling local Worlds.

## Desktop pending-sync recovery

The existing deterministic `StewardPendingSyncRecoveryService` is now connected to the Desktop recovery surface.

For the selected authenticated shared World, `Retry sync` is exposed only from unresolved recovery responsibility. Before invoking the service Desktop confirms a matching `RecoveryPending` journal exists. The service remains the authority for reconciliation:

```text
canonical == journaled candidate
    -> original commit already succeeded
    -> no recapture/recommit
    -> finalize workspace + clear recovery

canonical == journaled base
    -> reacquire/reconnect exact authority
    -> reuse same candidate ID
    -> reuse published candidate or recapture preserved workspace
    -> expected-head commit

canonical != base && canonical != candidate
    -> no overwrite
    -> preserve evidence
    -> Recovery needed
```

Recovery journal reads and retries execute inside the normal Desktop operation boundary so UI exceptions do not escape an async event handler.

`WorldLifecycleResponsibilityTracker.InitializeFromRecoveryRecords` now reconciles both directions: durable recovery evidence creates the tray/Quit/action guard, and removal of the final durable record clears a stale recovery guard. `CleanupPending` remains guarded. Core tests cover `RecoveryPending`, journal removal, `CleanupPending`, and crash-found `Active` records.

After every pending-sync attempt Desktop re-reads the durable recovery journal and refreshes tray status, quit guard, responsibility banner, managed-game tiles, and writable actions. A successful recovery therefore cannot leave the process incorrectly stuck in `Recovery needed`.

## Failure behavior

Before a shared runtime is available:

- Steam native-load/init failure -> local Worlds remain usable;
- AppID mismatch -> remote auth stops;
- Web API ticket timeout/rejection -> remote auth stops;
- Steward authentication rejection -> remote auth stops;
- API/session/network/malformed remote metadata failure while loading shared Worlds -> local library remains usable.

After authenticated runtime composition, the existing remote one-writer and recovery rules remain authoritative. No Desktop shortcut bypasses generation, expected-head commit, recovery journal, or exact-environment verification.

## CI evidence

The first fully green checkpoint containing the full Desktop remote composition plus Steam ticket/authentication wiring was:

- commit `b0dc42b6218da217047177c806fba17227fda36d`;
- GitHub Actions run `29927422485`;
- Quality: green;
- Ubuntu build/tests: green;
- Windows build/tests: green;
- PostgreSQL integration: green;
- S3-compatible integration: green.

The follow-up robustness commit `6affc5536a3b17c331aca13cf91f5af7a2c4ff74` also passed the complete five-gate matrix in run `29927735994`.

The Desktop pending-sync recovery + responsibility reconciliation checkpoint is commit `560a66e7a5d6e6fc2170f9f643b42cd9d1132e41`; GitHub Actions run `29929981444` passed Quality, Ubuntu, Windows, PostgreSQL, and S3-compatible integration.

## What E4 still needs before product acceptance

Code composition and the Waiting-to-sync retry path are no longer the main gap. The remaining E4 acceptance work is evidence from the actual deployment boundary:

```text
real Windows Steward build launched under Steward's Steam AppID
-> real Steam Web API ticket
-> deployed Steward API configured for the same AppID/identity
-> server-verified Steam session
-> shared World listed from backend
-> canonical Factorio environment verified Ready
-> remote Continue/Host
-> reservation + verified download
-> capture + multipart upload
-> expected-head canonical commit
-> second installation observes the new canonical revision
```

Broader recovery UX still needs explicit treatment for a crash-found `Active` record and for `CleanupPending`; those states must keep preserving evidence and must not be mislabeled as a safe pending-sync retry.

Palworld shared play remains fail-closed until its adapter has a real exact-environment verifier. Factorio hosted-server readiness remains a separate adapter acceptance item and should ultimately prove the authoritative server process is actually ready, not merely that a launcher process started.
