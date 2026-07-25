# UI-0 Sign-off Checklist

This is the final UI planning checkpoint for the first commercial release.

Status: **UI-0 approved. Master planning lock is lifted; implementation is active.**

The authoritative UI decisions are UI-D001 through UI-D009 in `UI_ROADMAP.md`.

## Approved decision mapping

| Decision | Approved meaning |
|---|---|
| UI-D001 | Explicit Start World, Host World, and capability-driven Join |
| UI-D002 | Responsive Games -> Worlds master-detail workspace |
| UI-D003 | Import local-first; temporary Host is independent of persistent sharing |
| UI-D004 | Shared authority must be verified before a new writer; active session may continue through outage |
| UI-D005 | One generic Join action backed only by a validated automatic adapter Join path in first release |
| UI-D006 | Evidence-driven recovery with Retry recovery, Export recovery copy, and Continue from last safe state when safe |
| UI-D007 | Tray is present whenever the Steward user-session process is running |
| UI-D008 | Fixed first-release terminology |
| UI-D009 | Flat Share World / Manage access surface with one Access Manager |

## Final terminology

### Actions

- **Start World**
- **Host World**
- **Join**
- **Share World**
- **Manage access**
- **Stop and Save**
- **Retry connection**
- **Retry sharing**
- **Retry recovery**
- **Export recovery copy**
- **Continue from last safe state**
- **Open Steward**
- **Quit Steward**

### States

| Meaning | UI term |
|---|---|
| local managed World not shared | `Only on this PC` |
| World safe to begin a session | `Ready` |
| persistent Steward sharing active | `Shared` |
| initial shared setup incomplete | `Sharing` |
| shared authority cannot be verified before start | `Connection required` |
| lifecycle is preparing | `Preparing` |
| local writable session active here | `Running` |
| hosted writable session active here | `Hosting` |
| remote host owns the session but is not ready | `Host is starting` |
| another device owns the active writable session | `Someone is playing` |
| capture/store/verify/commit incomplete | `Saving World` |
| candidate preserved while remote handoff is unresolved | `Waiting to sync` |
| environment/capability/identity issue requires intervention | `Action required` |
| handoff authority/evidence is unresolved | `Recovery needed` |

Internal state names do not replace these terms.

## Tray contract

- whenever the Steward process is running, its tray icon is visible;
- closing the main window hides the window and leaves Steward running;
- ordinary Quit is available only when no active/unresolved World responsibility would be abandoned;
- Running, Hosting, Saving World, Waiting to sync, and active recovery remain visible through the tray/background lifecycle;
- application updates wait until no active or unresolved writable lifecycle remains.

## Sharing/access contract

- Import never shares automatically.
- `Only on this PC` may Start World and may Host World when the adapter/runtime supports temporary hosting.
- Share World is a separate explicit operation.
- Sharer becomes the sole Access Manager after shared setup succeeds.
- World-access invitation is distinct from Steam/game multiplayer-session invitation.
- Pending invitations grant no package/reservation/commit access.
- Membership is flat; there are no gameplay roles.
- Access Manager may add/remove members and atomically transfer access management.
- Revocation of an active writer remains pending until the responsibility resolves safely.

## Join boundary change

Earlier planning allowed a guided-manual fallback behind the same **Join** action. First-release implementation removed that dead contract instead of inventing a manual-session lifecycle no adapter uses today.

First release therefore requires a validated automatic Join path. Guided manual Join may be reconsidered only when a real adapter needs it and the adapter/runtime can prove preparation ownership, user guidance, manual session completion, and cleanup without brittle input automation.

## UI-0 acceptance checks

1. Import produces `Only on this PC` and never silently shares.
2. A local-only World may Host when the adapter/runtime supports temporary hosting.
3. Failed sharing preserves local usability.
4. World-access invitations remain separate from multiplayer invitations.
5. `Connection required` disables new writable and Join actions.
6. `Someone is playing` exposes Join only after host readiness and validated automatic capability proof.
7. `Waiting to sync` preserves the candidate and never presents Ready prematurely.
8. `Recovery needed` exposes only proven-safe recovery actions.
9. Tray/close/update behavior matches AR-0.
10. Every UI term maps to exactly one backend/runtime meaning in `CROSS_WORKSTREAM_CONTRACT.md`.
11. No screen depends on branches, merging, ownership hierarchy, gameplay roles, or permanent Steward game-server infrastructure.

## UI-0 gate

Status: **complete and approved with the documented first-release Join boundary change above**.

Implementation must remain inside this executable first-release contract.
