# Cross-Workstream Contract

This document is the shared first-release contract between UI, backend, runtime, and adapters.

Status: **complete and approved for implementation; first-release boundary changes must update this contract with their executable evidence.**

No workstream may assign a different meaning to a user-visible state/action without updating this contract first.

## State matrix

| User-facing state | Backend meaning | Runtime meaning | Allowed action | Adapter requirement | Authority |
|---|---|---|---|---|---|
| **Ready / Only on this PC** | No remote authority required | Local current head is valid and local reservation can be acquired | Start World; Host World when supported; Share World | Import/local launch; temporary host capability for Host | Local storage/runtime |
| **Sharing** | Shared World registration/access/package setup is incomplete | Local World remains usable; remote setup has not committed | Retry sharing; cancel only when rollback is proven safe | Valid local package; share-capable lifecycle | Local state plus backend operation result |
| **Ready / Shared** | Current remote head and reservation availability are verified | Shared reservation can be acquired | Start World; Host World when supported; Manage access | Selected local/host capability | Backend authority plus local capability check |
| **Connection required** | Current shared head/reservation cannot be verified before a new writable session | No safe shared writer or Join decision can be made | Retry connection | None | Backend verification result |
| **Preparing** | Reservation is held when shared; canonical head has not advanced | Download/materialize/environment/restore/start work is in progress | Cancel only before launch when safe | Environment/restore/launch capability | Runtime phase plus reservation when shared |
| **Running** | This device owns the valid writable session generation when shared | Local/non-hosted session is active and observed | Open Steward/game | Session observation and safe capture | Runtime + adapter evidence; backend reservation when shared |
| **Hosting** | This device owns the valid writable session generation when shared | Temporary hosted session is active and observed | Open Steward/game; Stop and Save when supported | Host launch/readiness/observation; safe stop for Stop and Save | Runtime + adapter evidence; backend reservation when shared |
| **Host is starting** | Another device owns the active reservation | Remote hosted session exists but readiness is not proven | Wait; refresh/status | Remote host/readiness evidence where available | Backend reservation + host status |
| **Someone is playing** | Another device owns the active writable reservation | Competing writable start is blocked | Join when automatic capability/readiness permits; otherwise wait | Validated automatic adapter Join capability | Backend reservation + adapter capability |
| **Saving World** | Reservation remains held while candidate publication/commit is unresolved | Session ended or safe stop completed; capture/store/verify/commit/finalize is incomplete | None; safe retry only where operation contract permits | Safe capture and package validation | Runtime phase + backend transaction result |
| **Waiting to sync** | Candidate is preserved locally; remote upload/commit cannot currently finish | Background retry/reconnect responsibility remains active | Automatic bounded retry; Retry when useful; diagnostics | Candidate remains valid/restorable | Local recovery evidence + backend operation status |
| **Action required** | Authority may be valid, but required environment/capability/identity condition blocks safe continuation | Lifecycle cannot safely proceed until condition is resolved | Resolve issue; diagnostics | Explicit adapter limitation/failure result | Failing boundary |
| **Recovery needed** | Reservation/generation/head/candidate authority is unresolved or a prior handoff failed safely | Previous writable lifecycle cannot be marked complete | Only proven-safe recovery action; Export recovery copy where safe | Preserved recovery evidence and adapter capture facts | Backend reservation/head + local recovery evidence |

## Action contracts

### Start World

For a local-only World, the runtime acquires local exclusivity and uses the local canonical head.

For a shared World:

```text
verify authenticated membership/current head/reservation
-> acquire expected-head reservation
-> download/verify state and environment as required
-> adapter prepares/restores
-> launch local session
-> observe session
-> safe capture
-> publish/verify candidate
-> expected-head + generation commit
-> release/finalize
```

### Host World

Host World uses the same World lifecycle but starts a temporary hosted session through the adapter.

Important:
- persistent Steward sharing is **not** a prerequisite for temporary hosting;
- an `Only on this PC` World may Host when the adapter/runtime supports it;
- a shared Host acquires the same one-writer reservation used by Start World;
- the adapter proves host readiness before Join is exposed;
- no permanent Steward game-server fleet is created.

### Join

Join is capability-driven and never acquires a second writable World reservation.

The first-release validated Join capability is deliberately smaller than the earlier planning draft:

```text
Steam/game-native automatic
-> adapter-controlled automatic
-> unsupported
```

The UI exposes the single action **Join** only when:
- another device owns the hosted writable session;
- host readiness is proven;
- the adapter exposes a validated automatic Join capability.

Guided manual Join is **not** a first-release executable capability. No current adapter uses it, and Steward does not have a truthful generic lifecycle for keeping an adapter-prepared environment alive while the user manually launches/joins and then proving when that manual session has ended so cleanup can occur. Carrying a `SupportedGuidedManual` result without that lifecycle made the contract promise behavior Core could not safely execute.

Reconsider guided manual Join only when a real adapter needs it and the adapter/runtime contract can prove preparation ownership, user guidance, session completion, and cleanup without brittle keyboard/mouse automation.

Steam/game multiplayer-session invitations remain outside the Steward World-access membership system.

### Share World

First release publishes the World before asking Steward to manage additional members:

```text
Only on this PC
-> authenticate/verify service
-> verify exact canonical environment
-> publish and verify immutable initial state/environment
-> establish shared World authority
-> sharer becomes sole Access Manager
-> Shared / Ready
```

Membership invitations are a separate **Manage access** operation after the World exists:

```text
Shared / Ready
-> Manage access
-> Add person
-> World-access invitation pending
-> invited identity accepts individually
```

This separation is deliberate:
- initial publication has one authenticated creator and one Access Manager;
- invitation selection cannot block or partially define canonical World creation;
- failed/declined invitations do not make an otherwise valid shared World incomplete;
- pending invitations grant no package/reservation/commit access;
- World-access invitations remain distinct from Steam/game multiplayer-session invitations.

Failed initial sharing never destroys the original local managed World. Once remote side effects may have occurred, the existing write-ahead Shared marker keeps local writable fallback locked and Retry sharing resumes the same immutable IDs.

### Manage access

Membership is flat.

Normal member:
- view members/current Access Manager;
- Leave World when no unresolved responsibility exists.

Access Manager additionally:
- Add person;
- Remove access;
- Transfer access management atomically.

**Destructive shared-World deletion / Stop sharing is not a first-release action.** There is no current backend terminal-deletion contract, and the UI must not manufacture one. An Access Manager who wants to stop being responsible can transfer Access Manager to another active member and then leave when responsibility is safely resolved. A future deletion feature requires an explicit backend rule for canonical metadata, immutable package retention, active reservations, pending invitations, and recovery references before UI work begins.

Revocation of a member with active writable responsibility becomes pending until that responsibility resolves safely.

### Stop and Save

Stop and Save is available only when the adapter can perform or validate a safe hosted stop.

```text
request graceful stop
-> prove authoritative hosted session ended
-> prove safe capture boundary
-> capture/validate
-> publish/verify
-> commit
-> release/finalize
```

### Retry recovery

Allowed only when the preserved candidate and current authority can still complete the original handoff safely.

### Export recovery copy

Creates a safe non-canonical export where the adapter can do so. It does not create a Steward branch, Fork, merge path, or second canonical head.

### Continue from last safe state

Allowed only after reservation/generation/head authority is deliberately resolved.

It:
- abandons the unresolved candidate as a canonical candidate;
- returns the World to the last committed safe head;
- never merges;
- never force-overwrites;
- never silently deletes preserved candidate data;
- follows BE-D006 and BE-D009 retention semantics.

## Failure and recovery rules

1. A failed capture, transfer, verification, or commit never replaces the last valid canonical head.
2. A timer may move `Active -> Uncertain`; a timer never manufactures `Available` while the old writer may still exist.
3. A stale expected head or invalidated generation cannot commit.
4. A late invalidated session preserves its candidate as recovery material.
5. A new shared writable session cannot start while shared authority cannot be verified.
6. A valid already-running session may continue locally through backend connectivity loss.
7. Session end while disconnected captures a durable local candidate and enters **Waiting to sync**.
8. Reconnect revalidates authentication, generation, and expected head before upload/commit.
9. Changed head or invalidated generation transitions to **Recovery needed** rather than newest-wins or overwrite behavior.
10. Closing the main UI never abandons an active lifecycle; tray/background responsibility continues.
11. No workstream may invent generic save merging, Git-style branches, complex social roles, permanent host ownership, or permanent Steward game-server infrastructure.

## Authority rules

### Backend owns
- verified Steam identity for shared operations;
- shared membership/Access Manager records;
- canonical remote state/environment head;
- shared reservation state/generation;
- expected-head compare-and-swap decision;
- remote package publication eligibility.

### Runtime owns
- generic lifecycle orchestration;
- one active managed writable lifecycle per device;
- local cache/materialization/recovery lifecycle;
- mapping backend/adapter facts into product state;
- keeping responsibility alive in the user session/tray.

### Adapter owns
- game/World discovery;
- environment facts/preparation;
- restore/capture package shape;
- launch/host/join specifics;
- session ownership/readiness evidence;
- graceful stop;
- safe capture timing;
- game-specific validation/identity limitations.

### UI owns
- presentation;
- user commands;
- approved first-release terminology;
- no authority inference beyond supplied runtime/backend/adapter state.

## First-release acceptance plan

The release proof must include:

1. Import a local Factorio World and reach **Ready / Only on this PC**.
2. Start and safely capture a Factorio local session.
3. Host a local-only Factorio World without requiring persistent Steward sharing where adapter capability supports it.
4. Share a World explicitly; then create a World-access invitation through Manage access and prove the pending invitation grants no access before acceptance.
5. Host a shared Factorio session, stop/save, publish, and commit.
6. Repeat the equivalent hosted lifecycle with Palworld dedicated hosting.
7. PC A commits `N+1`; PC B downloads/verifies `N+1` and commits `N+2`; PC A downloads/verifies `N+2`.
8. Reject a competing writer while another device owns Active or Uncertain authority.
9. Resume an interrupted large upload without mutating/duplicating the logical immutable revision.
10. Lose backend connectivity during an active session, continue gameplay, capture locally, and enter **Waiting to sync**.
11. Reconnect the same still-valid generation and complete the handoff.
12. Deliberately reclaim an Uncertain stale reservation after the grace window and invalidate the old generation.
13. Reject a late old-generation commit and preserve its candidate as recovery evidence.
14. Crash/restart Steward and surface unresolved recovery before presenting Ready.
15. Block safely when environment, adapter capability, or Palworld identity limitations prevent continuation.
16. Verify `Continue from last safe state` resolves authority without merge/force overwrite and retains abandoned candidate according to policy.
17. Verify canonical retention keeps current + previous two committed revisions, plus any pinned recovery dependencies.
18. Verify package limits, size/hash validation, resumable transfer, and local disk preflight behavior.
19. Verify security baseline, redacted diagnostics, metadata backup, restore, and EU residency/deployment assumptions.
20. Verify UI terminology/state sequence matches this contract exactly.

Each scenario records:
- starting canonical head;
- reservation/session generation where shared;
- adapter capability/result;
- user-visible state sequence;
- final authoritative head;
- preserved recovery evidence;
- cleanup eligibility.

## Planning gate status

- UI-0: **complete and approved**.
- BE-0: **complete and approved**.
- AR-0: **complete and approved**.
- Cross-workstream matrix: **complete and reconciled**.
- First-release acceptance plan: **specified; execution occurs during implementation/release validation**.
- Master planning lock: **lifted; implementation is active.**
