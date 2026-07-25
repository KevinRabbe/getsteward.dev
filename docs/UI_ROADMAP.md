# UI and UX Roadmap

## Purpose

This roadmap defines the complete first-release Steward user experience.

Steward is background-first. The normal interaction is brief:

```text
select game
-> select World
-> Start World or Host World
-> play
```

Steward then remains responsible for observing the session, capturing the result, and completing the World handoff.

> **One shared World. Different Steam players. Different times. No always-on game server.**

## Planning status

Status: **UI-0 approved; implementation is active. First-release boundary changes must update this roadmap with executable evidence.**

UI-D001 through UI-D009 are the approved first-release UI product contract. Cross-workstream reconciliation is maintained in `CROSS_WORKSTREAM_CONTRACT.md`.

## Approved UI-0 decisions

### UI-D001: Explicit Start World, Host World, and Join

Status: **approved**.

The first release does not use one contextual Play action that guesses user intent.

- **Start World** means local/non-hosted play.
- **Host World** means temporary multiplayer hosting or dedicated-server operation.
- **Join** connects to an already active host.
- Start and Host are unavailable when they would create a competing writer.
- Join replaces writable actions while another device owns a hosted session.
- Actions are capability-driven; UI/Core never branch on game name.

A contextual Play action may be reconsidered only after real usage proves the automatic choice dependable.

### UI-D002: Responsive master-detail World workspace

Status: **approved**.

The visible hierarchy remains:

```text
Games
-> Worlds
```

World details remain inside the selected game's workspace.

Wide layout:
- World list beside selected-World details.

Narrow layout:

```text
World list
-> select World
-> focused details
-> Back to Worlds
```

The details surface shows only operationally useful information such as World name, status, valid actions, host/device when relevant, environment/recovery warnings, and small secondary metadata.

Technical identifiers and diagnostics remain secondary/collapsed.

### UI-D003: Import locally before sharing

Status: **approved**.

Every imported World first becomes a local Steward-managed World.

```text
select detected World
-> capture source safely
-> verify managed state
-> Only on this PC
```

Rules:
- import never uploads or shares automatically;
- original source data is preserved;
- Start World remains available after successful import;
- Host World is independent of persistent Steward sharing and is available for an `Only on this PC` World whenever the adapter/runtime supports temporary hosting;
- Share World is a separate explicit action;
- failed sharing never destroys the usable local World.

The three concepts remain independent:

1. local Steward management;
2. temporary game hosting;
3. persistent Steward sharing.

### UI-D004: Shared Worlds require verification before writable play

Status: **approved**.

Before a new shared writable session begins, Steward must be able to verify authenticated shared authority, current head, and reservation state.

If it cannot:

```text
Connection required
-> Start World unavailable
-> Host World unavailable
-> Join unavailable
-> Retry connection
```

Cached information may remain visible but is not presented as verified Ready.

Connectivity loss during an already valid active session follows a different rule:

```text
Running / Hosting
-> backend connection lost
-> gameplay continues
-> reservation may become Uncertain
-> session ends safely
-> candidate captured locally
-> Waiting to sync
-> reconnect and revalidate authority
-> commit when still valid
or
-> Recovery needed
```

Steward never creates an offline branch or silently overwrites a newer canonical state.

### UI-D005: One capability-driven Join action

Status: **approved with a smaller first-release executable boundary**.

The first release exposes one user-facing **Join** action.

Priority:

```text
Steam/game-native automatic Join
-> adapter-controlled automatic Join
-> unsupported
```

Rules:
- Join is shown only after host readiness is proven;
- the adapter must expose a validated automatic Join path;
- brittle keyboard/mouse automation is never used to fake unsupported joining;
- UI always says **Join** regardless of the automatic mechanism;
- Core/UI never contain game-name Join branches.

Earlier planning included a guided-manual fallback. It is removed from the first-release executable contract because no current adapter uses it and Steward has no truthful generic lifecycle for keeping a prepared environment alive while a user manually launches/joins and then proving manual-session completion for cleanup.

Guided manual Join may be reconsidered only when a real adapter needs it and the adapter/runtime contract can prove preparation ownership, minimal user guidance, session completion, and cleanup without brittle input automation.

### UI-D006: Recovery exposes only proven-safe actions

Status: **approved**.

Recovery is evidence-driven. Steward exposes only actions whose preconditions are proven safe.

Supported first-release recovery actions:

#### Retry recovery

Use when a preserved candidate can still safely complete the interrupted handoff.

#### Export recovery copy

Use as a non-canonical preservation path when Steward cannot safely complete the handoff.

It does not create a Steward branch, Fork, merge source, or second canonical World.

#### Continue from last safe state

Explicitly abandons the unresolved candidate only after backend/session authority is safely resolved and the last committed head is verified.

The action:
- clearly warns that newer local changes may be abandoned;
- never merges;
- never force-overwrites;
- never silently deletes the candidate;
- follows BE-D006/BE-D009 retention and authority rules.

Recovery never offers:
- Merge;
- Force overwrite remote;
- Use newest file automatically;
- Make branch/Fork;
- silent candidate promotion;
- silent candidate deletion.

### UI-D007: Persistent tray while Steward is running

Status: **approved**.

Whenever the Steward user-session process is running, a tray icon is present.

Closing the main window hides the window; it does not quit Steward.

Idle tray:
- **Open Steward**;
- concise important status when present;
- **Quit Steward** when safe.

During an active lifecycle, the tray may additionally show:
- game and World;
- Running or Hosting;
- connectivity/synchronization warning;
- **Stop and Save** only when the adapter/runtime supports a controlled safe hosted stop.

During post-session work it may show:
- Saving World;
- Waiting to sync;
- Recovery needed.

Ordinary Quit must not silently abandon Running, Hosting, Saving World, Waiting to sync, or active recovery responsibility.

Core principle:

> **The UI window may close. The responsibility may not.**

### UI-D008: Fixed first-release terminology

Status: **approved**.

#### Actions

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

#### User-facing states

| Term | Meaning |
|---|---|
| **Ready** | The World can safely begin a new session. |
| **Only on this PC** | Managed locally and not persistently shared through Steward. |
| **Shared** | Available through Steward to accepted members/devices. |
| **Sharing** | Initial shared setup/upload/access operation is incomplete. |
| **Preparing** | Steward is preparing state/environment for launch. |
| **Running** | A local/non-hosted writable session is active on this device. |
| **Hosting** | A hosted writable session is active on this device/server. |
| **Host is starting** | Another device owns a hosted session but it is not ready for Join yet. |
| **Someone is playing** | Another device owns the active writable session. |
| **Saving World** | Capture/store/verify/commit is incomplete. |
| **Waiting to sync** | New state is preserved locally but remote handoff cannot finish yet. |
| **Connection required** | Shared authority cannot currently be verified before a new writable session. |
| **Action required** | Environment/capability/identity issue requires user intervention. |
| **Recovery needed** | The previous handoff did not complete safely and requires resolution. |

Internal state names do not automatically become UI terminology.

### UI-D009: Flat Share World and Manage access surface

Status: **approved**.

First release reflects the backend's flat membership model directly.

#### Share World

For an `Only on this PC` World:

```text
Share World
-> choose/add Steam identities
-> review
-> Sharing
-> upload and verify initial shared state
-> shared authority commits
-> sharer becomes sole Access Manager
-> World-access invitations created
-> Shared / Ready
```

Rules:
- sharing is explicit;
- sharer becomes Access Manager without role configuration;
- pending World-access invitation grants no package/reservation/commit access;
- invited identity becomes a member only after acceptance;
- World-access invitation is separate from Steam/game multiplayer-session invitation;
- failed sharing leaves the local World usable and exposes Retry sharing where safe.

#### Manage access

Normal member may see:
- people with access;
- current Access Manager;
- Leave World when no unresolved responsibility exists.

Access Manager may additionally:
- Add person;
- Remove access;
- Transfer access management;
- Stop sharing/delete only according to the backend deletion contract.

There is no Reader/Writer/Host/Admin/Moderator permission hierarchy.

Removing a member who owns an active writable responsibility becomes pending until that responsibility resolves safely.

Transfer of Access Manager is atomic and changes administrative membership responsibility only. It grants no gameplay, reservation, hosting, or revision privilege.

Core principle:

> **Members use the World. The Access Manager manages only who is a member.**

## Navigation model

First-release global navigation:
- **Games**;
- **Import**;
- **Settings**.

Activity/notifications become a separate global surface only when evidence proves it necessary.

### Games Library

Shows supported installed games with managed World count and a small attention indicator where a World is active, waiting to sync, requires action, or requires recovery.

### Game workspace

Contains:
- game banner/header;
- search/sort;
- Import World;
- Worlds belonging to that game;
- responsive selected-World details.

### Import workspace

```text
choose installed supported game
-> search/filter detected Worlds
-> select candidate
-> Import
-> Only on this PC
```

Duplicate native World detection remains adapter-owned.

### Manage access

World-local surface only. It is not a social/friends/party system.

### Recovery

Shows the affected World, last safe-state status, preserved candidate/evidence, failed lifecycle stage, and only proven-safe actions.

## Core user journeys

### Import

```text
Import
-> choose game
-> choose World
-> capture source safely
-> verify managed state
-> Ready / Only on this PC
```

### Start locally

```text
Ready
-> Start World
-> Preparing
-> Running
-> game ends
-> Saving World
-> Ready
```

### Host temporarily

```text
Ready, whether Only on this PC or Shared
-> Host World when supported
-> Preparing
-> Hosting
-> Stop and Save or adapter-observed safe end
-> Saving World
-> Ready
```

Persistent Steward sharing is not a prerequisite for temporary hosting.

### Share

```text
Only on this PC
-> Share World
-> Sharing
-> upload/verify shared state
-> invitations pending until accepted
-> Shared / Ready
```

### Another device is hosting

```text
Host is starting
-> wait
-> Someone is playing
-> Join when supported
or
-> wait until available
```
