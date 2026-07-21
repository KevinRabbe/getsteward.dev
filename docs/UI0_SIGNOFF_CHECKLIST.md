# UI-0 Sign-off Checklist

This is the final UI planning checkpoint. It preserves the approved UI-D001
through UI-D006 decisions and records defaults for the remaining UI-0 choices.
It does not lift the master planning lock.

## Proposed remaining decisions

### UI-D007: Flat World access flow

The first release uses one explicit **Share World** flow:

```text
Only on this PC
-> Share World
-> verify Steam identity/service
-> upload and verify initial state
-> Access Manager
-> invite Steam identities
-> pending/accepted/revoked membership
-> Ready, shared
```

The Access Manager shows only the minimum useful information: member display
name, membership state, invitation action, revoke action, and whether sharing
setup is still processing. It has no roles, ownership hierarchy, gameplay
permissions, public discovery, or multiplayer-session invitation controls.

World-access invitations authorize durable Steward membership. Steam/game
multiplayer invitations remain outside this surface.

### UI-D008: Tray visibility

The tray/status surface is present while active lifecycle work, unresolved
recovery evidence, or an unsynchronized candidate exists. When Steward is idle,
the main window may be the only visible surface. Closing the window during active
work minimizes to the background process and does not abandon the lifecycle.

Explicit Quit is guarded while a writable lifecycle or unresolved candidate
exists. Updates wait until no active or unresolved writable lifecycle remains.

### UI-D009: Final terminology

Use these first-release user-facing terms consistently:

| Meaning | UI term |
|---|---|
| local managed World not shared | `Only on this PC` |
| verified shared World available | `Ready, shared` |
| shared authority cannot be verified before start | `Connection required` |
| lifecycle is preparing | `Preparing` |
| local writable session | `Running locally here` |
| hosted writable session | `Hosting here` |
| remote host is starting | `Host starting elsewhere` |
| remote host is ready | `Active elsewhere` |
| capture/store/commit is in progress | `Saving` |
| candidate preserved while remote handoff is unresolved | `Waiting to sync` |
| required capability/environment is unavailable | `Blocked` |
| handoff authority or evidence is unresolved | `Recovery needed` |

Actions remain `Start World`, `Host World`, `Join`, `Stop and Save`, `Share
World`, `Retry recovery`, `Export recovery copy`, and `Continue from last safe
state` when safe.

## UI-0 acceptance checks

1. Import produces `Only on this PC` and never silently shares.
2. Share World preserves local usability if sharing fails.
3. Access Manager distinguishes World-access invitations from multiplayer
   invitations.
4. Ready shared actions match backend verification and adapter capabilities.
5. Connection required disables new writable and Join actions as specified.
6. Active elsewhere exposes Join only after host readiness and capability proof.
7. Waiting to sync preserves the candidate and does not show Ready.
8. Recovery needed exposes only proven-safe recovery actions.
9. Tray/close/update behavior matches AR-0.
10. Every term in the table maps to one backend/runtime meaning.

## UI-0 gate

UI-0 is ready for product approval when UI-D007 through UI-D009 are accepted or
explicitly deferred, the checks above are represented in the UI acceptance
plan, and the cross-workstream contract remains consistent.
