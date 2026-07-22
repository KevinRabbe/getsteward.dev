# E4 Windows Desktop Remote Composition Status

Status: **DESKTOP REMOTE COMPOSITION + DETERMINISTIC SYNC/CLEANUP/INTERRUPTED RECOVERY COMPLETE AND CI GREEN; LIVE STEAM/BACKEND ACCEPTANCE STILL REQUIRED.**

This checkpoint records the point where the production Windows Desktop stopped being structurally local-only. The existing `Only on this PC` path remains local, while authenticated shared Worlds use the same remote storage, authority, transfer, commit, and recovery components already proven by BE-5.

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
- one `ManagedWritableSessionGate` shared by normal lifecycle and remote recovery;
- `WorldLifecycleService`;
- `StewardPendingSyncRecoveryService`.

Desktop also composes the corresponding local recovery path around `LocalWorldStorage`, `LocalWorldSessionCoordinator`, a shared local `ManagedWritableSessionGate`, `LocalPendingWorkspaceRecoveryService`, and the common cleanup/decision services.

## Local and shared Worlds use different authoritative paths

Desktop does not decide authority from a loose `SharingMode` flag alone.

```text
local World ID
    -> LocalWorldStorage
    -> LocalWorldSessionCoordinator

World ID returned by authenticated Steward backend
    -> StewardWorldStorage
    -> StewardWorldSessionCoordinator
```

When the same World ID exists in the old local store and authenticated Steward metadata, the backend World is displayed as the canonical shared World. The old local bytes are left untouched rather than deleted or merged.

A remote outage does not make private local Worlds unusable. A stale local record marked `Shared` never falls back to local writable authority.

## Exact environment is a real shared-play gate

For a backend World, Verify/Repair reads the canonical structured `EnvironmentManifest` through `StewardWorldStorage`.

Shared Continue/Host remains disabled until verification reports `Ready`, and the action handlers independently reject an unverified shared World. Local-only Worlds retain their existing behavior.

Remote environment-version mutation is intentionally not faked through a local checkbox. The canonical backend environment remains immutable until an explicit reviewed environment-transition operation exists.

## Installation-bound identity

Desktop device settings persist one stable Steward installation ID. Existing settings migrate while preserving the hosting preference.

The installation ID is used for authentication and distributed reservation ownership. It is not regenerated on each launch.

If durable device settings cannot be loaded/created, the temporary fallback identity is never used for remote authority. Shared functionality fails closed for that launch while local Worlds remain usable.

Steward access/refresh credentials remain process-memory state. Desktop re-authenticates through Steam on a new launch instead of writing refresh credentials to ordinary device settings.

## Steam authentication boundary

Desktop has the real client-side Steam Web API ticket path:

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

Remote sharing is enabled only when all three explicit deployment values are supplied:

- `STEWARD_API_BASE_URL`;
- `STEWARD_STEAM_APP_ID`;
- `STEWARD_STEAM_WEB_API_IDENTITY`.

With none configured, Steward stays local-only. Partial/invalid configuration fails closed for shared functionality without disabling local Worlds.

## Deterministic pending recovery

Both local and shared recovery now use the same state-machine idea while retaining different authority implementations:

```text
canonical == journaled candidate
    -> original commit already succeeded
    -> no recapture/recommit
    -> cleanup + clear journal

canonical == journaled base
AND current environment == journaled environment
    -> acquire exact writable authority
    -> re-check heads
    -> reuse same candidate ID
    -> reuse valid published candidate or recapture preserved workspace
    -> commit

canonical state diverged
OR environment diverged before candidate became canonical
    -> no overwrite / no environment mixing
    -> preserve evidence
```

Remote recovery additionally checks the reservation starting **state and environment heads** against the durable journal. A mismatched reservation is abandoned instead of used.

A published candidate is reused only if its adapter and parent revision match the journaled lineage.

Older recovery records that lack an exact environment fail closed whenever an existing workspace would need to be reconstructed. Steward never substitutes a later current environment.

After every recovery attempt Desktop re-reads the durable journal and refreshes tray status, Quit/update guards, the responsibility banner, managed-game tiles, and writable actions.

## Cleanup-only recovery

`CleanupPending` is separate from state recovery.

The Core cleanup service cannot capture, upload, commit, mutate canonical heads, or acquire writable authority.

New workspace journals record the exact immutable `EnvironmentRevisionId` that created the prepared workspace. Cleanup reconstructs adapter-owned context from that exact environment.

```text
CleanupPending + workspace exists
    -> exact EnvironmentRevisionId required
    -> local game installation required
    -> load immutable environment
    -> adapter-controlled Discard
    -> remove journal after success

CleanupPending + workspace already gone
    -> journal-only cleanup

legacy CleanupPending + existing workspace + no EnvironmentRevisionId
    -> fail closed
    -> preserve evidence
```

## Crash-found Active / interrupted-session UX

A restart-found `Active` record is no longer mislabeled as an ordinary pending-sync retry. `WorldLifecycleResponsibilityTracker` exposes a distinct guarded `InterruptedSession` responsibility because Steward cannot prove whether gameplay actually started before the crash.

Desktop presents two explicit actions:

### Recover changes

```text
preflight local game installation
-> require preserved workspace + exact journaled environment
-> Active -> RecoveryPending
-> assign/reuse stable candidate revision ID
-> deterministic local or remote pending recovery
```

The previous canonical World remains authoritative until that recovery commits successfully.

### Discard interrupted session

Desktop shows an explicit destructive confirmation explaining that uncommitted gameplay changes may be lost and that the canonical World will stay unchanged.

```text
confirm discard
-> Active -> CleanupPending
-> adapter-owned controlled cleanup
-> remove journal after cleanup succeeds
```

If Steward cannot identify the exact environment or safely invoke the adapter, it preserves the workspace and journal rather than guessing.

## Factorio hosted lifecycle

The existing Factorio adapter already implements the intended authoritative hosted path rather than merely launching a listen-host process:

```text
isolated canonical World
-> private dedicated Factorio server
-> authenticated loopback RCON readiness
-> launch normal graphical host client
-> host client ends
-> verify server still alive
-> RCON /server-save
-> observe save refresh
-> stop dedicated server
-> capture + commit
```

Code-level readiness and protocol tests exist. Commercial acceptance still requires repeating this lifecycle on the real Windows Steam installation as part of the live E4 handoff.

## Failure behavior

Before a shared runtime is available:

- Steam native-load/init failure -> local Worlds remain usable;
- AppID mismatch -> remote auth stops;
- Web API ticket timeout/rejection -> remote auth stops;
- Steward authentication rejection -> remote auth stops;
- API/session/network/malformed remote metadata failure while loading shared Worlds -> local library remains usable.

After authenticated runtime composition, remote one-writer and recovery rules remain authoritative. No Desktop shortcut bypasses generation, expected-head commit, recovery journals, or exact-environment verification.

## CI evidence

Earlier green checkpoints:

- full Desktop remote composition + Steam ticket/auth wiring: commit `b0dc42b6218da217047177c806fba17227fda36d`, run `29927422485`;
- follow-up robustness: commit `6affc5536a3b17c331aca13cf91f5af7a2c4ff74`, run `29927735994`;
- Desktop pending-sync recovery + responsibility reconciliation: commit `560a66e7a5d6e6fc2170f9f643b42cd9d1132e41`, run `29929981444`;
- exact-environment cleanup recovery: commit `a796734c746829a39caa30a26e93d162b70e6c21`, run `29931448332`.

The interrupted-session decision path, deterministic local recovery, and exact-environment remote recovery were green at commit `4ec21efec9a6542938fa4f32b2a5c3ecb2c2b424`, GitHub Actions run `29941402902`:

- Quality: green;
- Ubuntu build/tests: green;
- Windows build/tests: green;
- PostgreSQL integration: green;
- S3-compatible integration: green.

## What E4 still needs before product acceptance

The remaining E4 acceptance gap is now primarily evidence at the actual deployment boundary:

```text
real Windows Steward build launched under Steward's Steam AppID
-> real Steam Web API ticket
-> deployed Steward API configured for the same AppID/identity
-> server-verified Steam session
-> shared World listed from backend
-> canonical Factorio environment verified Ready
-> remote Continue/Host
-> exact reservation + verified download
-> authoritative Factorio dedicated host reaches RCON readiness
-> gameplay ends + server-save succeeds
-> capture + multipart upload
-> expected-head canonical commit
-> second Steward installation observes the new canonical revision
```

Palworld shared play remains fail-closed until its adapter has a real exact-environment verifier. That is adapter acceptance work, not a reason to weaken the shared-World gate.
