# Cross-Workstream Contract

This document is the shared planning contract for UI, backend, runtime, and
adapters. It remains proposed while the master planning lock is active.

## State matrix

| User-facing state | Backend meaning | Runtime meaning | Allowed action | Adapter requirement | Authority |
|---|---|---|---|---|---|
| Ready, only on this PC | Local World/current head available | Local reservation can be acquired | Start World; Share World | Import and local launch | Local storage |
| Sharing | World is being registered, access is being changed, or initial state is uploading/verifying | Local World remains usable; shared setup is incomplete | None; cancel only when rollback is safe | Published local package and share-capable adapter | Local storage plus backend operation result |
| Ready, shared | Remote head and reservation verified; World available | Shared reservation can be acquired | Start World; Host World; manage access | Local/host capability as selected | Backend plus local capability check |
| Connection required | Remote head or reservation cannot be verified | No safe new shared writer | Retry connection | None | Backend verification result |
| Preparing | Reservation held; no confirmed gameplay yet | Materializing, preparing, or restoring | Cancel only before launch when safe | Environment and restore capability | Runtime phase with backend reservation |
| Running locally here | This device owns the local writer | Adapter session is active | Open Steward/game | Session observation | Runtime and adapter evidence |
| Hosting here | This device owns the shared writer | Hosted session is active and observed | Open; Stop and Save when supported | Host launch, readiness, observation | Runtime and adapter evidence |
| Host starting elsewhere | Another device owns reservation but host is not ready | Remote session is active or starting | Wait; refresh | Remote host/readiness status when available | Backend reservation plus runtime status |
| Active elsewhere | Another device owns an active reservation | Competing writable start is blocked | Join when adapter capability allows; wait | Validated adapter Join capability | Backend reservation |
| Saving | Reservation remains held; candidate is being captured/transferred/committed | Session ended or stop requested; handoff incomplete | None; retry where safe | Safe capture and validation | Runtime phase plus backend result |
| Waiting to sync | Candidate is preserved locally; remote publication/commit is unresolved | Retry is bounded and recovery evidence exists | Automatic retry; Retry; diagnostics | Candidate remains restorable | Backend operation status plus local recovery |
| Blocked | Required authorization, environment, or capability is unavailable | Lifecycle cannot safely proceed | Resolve issue; diagnostics | Explicit limitation/failure result | Failing boundary |
| Recovery needed | Reservation/candidate/head authority is unresolved | Previous lifecycle cannot be marked complete | Only proven recovery action; export recovery copy | Recovery evidence and safe capture facts | Backend reservation/head plus local recovery |

## Action contracts

### Start World

```text
verify current head and reservation availability
-> acquire expected-head reservation
-> download and verify state/environment as needed
-> prepare and restore through adapter
-> launch local session
-> observe, capture, publish, commit, release
```

### Host World

The flow is the same as Start World, but the adapter starts a temporary hosted
session and proves readiness before the UI presents the host as available.
Steam or the game remains responsible for multiplayer/session invitations and
joining. Steward handles the separate World access invitation that grants
authorization to download, reserve, and commit the shared World.

### Join

Join is capability-driven. The UI exposes one Join action only when the adapter
has a validated Join capability and the remote host is ready. The capability
may use a Steam/game-native path, an adapter-controlled path, or a guided manual
fallback. Join does not acquire a second writable World reservation.

### Stop and Save

Stop and Save is available only when the adapter can perform or validate a safe
hosted stop. The runtime waits for the adapter-defined capture boundary, then
publishes and commits the candidate before releasing the reservation.

## Failure and recovery rules

- A failed transfer, verification, or commit never replaces the last valid head.
- A missed heartbeat moves a reservation toward uncertainty; it never directly
  makes the World available to a competing writer.
- A stale expected head or invalidated generation preserves the candidate for
  recovery/diagnostics and returns a stable conflict result.
- `Continue from last safe state` is available only after the reservation and
  candidate authority are resolved. It explicitly abandons the preserved
  candidate and returns the World to the last committed head; it never merges or
  silently deletes the candidate.
- A cached World may be shown as known information, but not as verified Ready
  for a new shared writable session.
- Closing the UI does not abandon an active lifecycle. The desktop remains in the
  background where possible and startup scans unresolved recovery records.
- No workstream may invent branches, merging, permanent hosting, social roles,
  or game-specific behavior in Core.

## First-release acceptance plan

The release proof must include all of the following:

1. Import a local Factorio World and reach Ready.
2. Start and safely capture a Factorio local session.
3. Host a Factorio session, stop/save, publish, and commit.
4. Repeat the equivalent lifecycle with Palworld dedicated hosting.
5. PC A commits `N+1`; PC B downloads/verifies `N+1` and commits `N+2`; PC A
   downloads/verifies `N+2`.
6. Reject a competing writer while PC A or PC B owns the reservation.
7. Resume an interrupted upload without duplicating or mutating a revision.
8. Lose backend connectivity during an active session and preserve the local
   candidate until the result is known.
9. Move a stale reservation through uncertainty and recovery, then reject a
   late old-generation commit.
10. Crash/restart the desktop and surface recovery before presenting the World
    as Ready.
11. Block safely when environment, adapter capability, or Palworld identity
    limitations prevent continuation.
12. Verify security, redacted diagnostics, metadata backup, and restore.

Each scenario must record: starting head, reservation generation, adapter
capability, user-visible state sequence, final authoritative head, preserved
recovery evidence, and whether any candidate became eligible for cleanup.

## Planning gate status

- Backend contract: proposed and detailed in `BACKEND_ROADMAP.md`; BE-0 sign-off pending.
- UI contract: proposed complete; sharing, tray, and terminology defaults are
  recorded in `UI0_SIGNOFF_CHECKLIST.md` and await product approval.
- Runtime/adapter contract: substantially defined; remaining lifecycle,
  capability, and game-specific decisions are recorded in
  `ADAPTER_RUNTIME_ROADMAP.md`.
- Master sign-off: not complete. Production implementation remains blocked
  until UI-0/BE-0 review, cross-workstream approval, and explicit lock-lift
  approval.
