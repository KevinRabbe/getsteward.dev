# UI and UX Roadmap

## Purpose

This document defines the complete first-release Steward user experience and the stable UI ownership rules that implementation must preserve.

Steward is background-first. The normal interaction is brief:

```text
select game
-> select World
-> Start World or Host World
-> play
```

Steward remains responsible for observing the session, capturing the result, and completing the World handoff.

> **One shared World. Different Steam players. Different times. No always-on game server.**

## Planning status

Status: **UI-0 approved; deterministic first-release UI implementation is substantially complete. First-release boundary changes must update this roadmap and `CROSS_WORKSTREAM_CONTRACT.md` with executable evidence.**

UI-D001 through UI-D009 are the approved first-release UI product contract.

## Approved UI-0 decisions

### UI-D001: Explicit Start World, Host World, and Join

Status: **approved and implemented by capability-driven actions**.

The first release does not use one contextual Play action that guesses user intent.

- **Start World** means local/non-hosted writable play.
- **Host World** means temporary multiplayer hosting or dedicated-server operation.
- **Join** connects to an already active Host through a validated automatic mechanism.
- Start and Host are unavailable when they would create a competing writer.
- Join does not acquire writable World authority.
- Actions are capability-driven; UI/Core never branch on game name.

A contextual Play action may be reconsidered only after real usage proves the automatic choice dependable.

### UI-D002: Responsive Games -> Worlds -> World details hierarchy

Status: **approved and implemented**.

The visible hierarchy is:

```text
Games Library
-> selected game
-> Worlds belonging to that game
-> selected World details
```

Wide layout:
- World list beside selected-World details.

Narrow layout:

```text
World list
-> select World
-> focused details
-> Back to Worlds
```

The selected-game workspace includes:
- Back to Games;
- selected game name;
- Worlds context;
- search;
- name sort;
- Refresh;
- Worlds for that game;
- responsive World details.

The existing functional selected-game header satisfies the banner/header requirement. Decorative game artwork is not a separate first-release subsystem.

World details show only operationally useful information such as:
- World name/game;
- sharing/version state;
- valid actions;
- small World Lobby for shared Worlds;
- environment readiness;
- active/recovery responsibility;
- secondary technical identifiers in a collapsed section.

### UI-D003: Import locally before sharing

Status: **approved and implemented**.

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
- failed initial sharing never destroys the last usable local canonical World.

The three concepts remain independent:

1. local Steward management;
2. temporary game hosting;
3. persistent Steward sharing.

### UI-D004: Shared Worlds require verification before writable play

Status: **approved and implemented**.

Before a new shared writable session begins, Steward must be able to verify authenticated shared authority, current head, and reservation state.

If it cannot:

```text
Connection required
-> Start World unavailable
-> Host World unavailable
-> Join unavailable when readiness cannot be verified
-> Retry connection
```

Cached information may remain visible but is not presented as verified Ready.

Connectivity loss during an already-valid active session follows a different rule:

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

Status: **approved with the smaller implemented first-release boundary**.

The first release exposes one user-facing **Join** action.

Priority:

```text
Steam/game-native automatic Join
-> adapter-controlled automatic Join
-> unsupported
```

Rules:
- Join is shown only after Host readiness is proven;
- the adapter must expose a validated automatic Join path;
- brittle keyboard/mouse automation is never used to fake unsupported joining;
- UI always says **Join** regardless of automatic mechanism;
- Core/UI never contain game-name Join branches.

Guided manual Join is not a first-release executable capability. Reconsider it only when a real adapter needs it and the adapter/runtime can prove preparation ownership, session completion, and cleanup without brittle input automation.

### UI-D006: Recovery exposes only proven-safe actions

Status: **approved and implemented**.

Recovery is evidence-driven. Steward exposes only actions whose preconditions are proven safe.

#### Retry recovery

Use when a preserved candidate can still safely complete the interrupted handoff.

#### Export recovery copy

Use as a non-canonical preservation path. It never creates a Steward branch, merge source, or second canonical World.

#### Continue from last safe state

Explicitly abandons the unresolved candidate as a canonical candidate only after backend/session authority is safely resolved and the last committed head is verified.

It:
- warns that newer local changes may be abandoned;
- never merges;
- never force-overwrites;
- never silently deletes preserved evidence;
- follows backend retention and authority rules.

Recovery never offers:
- Merge;
- Force overwrite remote;
- Use newest file automatically;
- Make branch/Fork;
- silent candidate promotion;
- silent candidate deletion.

### UI-D007: Persistent tray while Steward is running

Status: **approved and implemented**.

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

> **The UI window may close. The responsibility may not.**

### UI-D008: Fixed first-release terminology

Status: **approved and localization-ready**.

Fixed product vocabulary is owned by the existing `DesktopText` resource catalog. Shipping translated locale catalogs is later localization content and does not require UI-logic changes.

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
| **Sharing** | Initial shared publication is incomplete. |
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

Status: **approved with the smaller executable Share boundary**.

First release reflects the backend's flat membership model directly and separates canonical publication from membership administration.

#### Share World

For an `Only on this PC` World:

```text
Share World
-> authenticate/verify Steward
-> verify exact canonical environment
-> Sharing
-> publish and verify immutable initial state/environment
-> establish shared authority
-> sharer becomes sole Access Manager
-> Shared / Ready
```

Rules:
- sharing is explicit;
- initial publication has one authenticated creator and one Access Manager;
- sharing never depends on choosing future members first;
- a failed/declined future invitation cannot make a valid shared World incomplete;
- failures before remote side effects keep the local World usable;
- once remote side effects may have occurred, Steward keeps the local copy locked to remote authority and **Retry sharing** resumes the same immutable IDs.

#### Manage access

Membership invitations happen after the World exists:

```text
Shared / Ready
-> Manage access
-> Add person
-> World-access invitation pending
-> invited identity accepts
-> member gains access
```

Normal member may:
- view people with access;
- see the current Access Manager;
- Leave World when no unresolved responsibility exists.

Access Manager may additionally:
- Add person;
- Remove access;
- Transfer access management.

Rules:
- pending World-access invitation grants no package/reservation/commit access;
- invited identity becomes a member only after acceptance;
- World-access invitation is separate from Steam/game multiplayer-session invitation;
- there is no Reader/Writer/Host/Admin/Moderator permission hierarchy;
- removing a member who owns active writable responsibility becomes pending until that responsibility resolves safely;
- Access Manager transfer is atomic and changes administrative membership responsibility only; it grants no gameplay/reservation/hosting/revision privilege.

#### No destructive shared deletion in first release

**Stop sharing / destructive shared-World deletion is not a first-release action.**

There is no current backend terminal-deletion contract. The UI must not manufacture one merely because a Settings/Manage-access screen could contain a Delete button.

An Access Manager who wants to stop being responsible may:

```text
transfer Access Manager to another active member
-> resolve any remaining writable responsibility
-> Leave World
```

A future destructive deletion feature requires a deliberate backend contract for at least:
- canonical World metadata;
- immutable package retention;
- active/Uncertain reservations;
- pending invitations;
- recovery references/pins;
- replay/idempotency and ambiguous response loss;
- what remaining members observe.

Until that real requirement exists, deletion is deliberately absent.

> **Members use the World. The Access Manager manages only who is a member.**

## World Lobby boundary

The selected shared-World details surface contains a small operational World Lobby.

Canonical rule:

> **World Lobby = World membership + ephemeral current-player presence + authoritative current Host.**

The lobby is read-only presentation. Manage access remains the membership mutation surface.

Steward does not add:
- friends/social graph;
- chat or voice;
- generic party system;
- global online/offline presence;
- public lobby/server browser;
- matchmaking;
- permanent player-presence history.

Steam/Discord remain the social/platform surfaces.

## Navigation model

First-release global navigation:
- **Games**;
- **Import**;
- **Settings**.

Activity/notifications become a separate global surface only when evidence proves it necessary.

### Games Library

Shows every registered supported game, including games with zero managed Worlds, with:
- game name/icon where available;
- managed World count;
- small responsibility-backed attention summary when a World is Preparing/Running/Hosting/Saving/Action required/Recovery needed.

The attention projection reads the existing lifecycle responsibility authority. It owns no second status cache.

### Game workspace

Contains:
- functional game header;
- search;
- name sort;
- Import/Refresh affordances;
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

### Settings

Global Settings contains device-wide preferences only. The existing device-hosting preference and `DeviceSettingsStore` remain the single owner; Settings does not introduce a second configuration model.

World-specific policy remains on World details.

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
-> upload/verify shared state and environment
-> Shared / Ready
-> optional Manage access
-> invite people
-> invitations remain pending until individually accepted
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

### Switch host

```text
current Host finishes and commits
-> Ready
-> another device selects same World
-> Host World
```

### Switch game

```text
finish current World
-> select another game
-> select one of its Worlds
-> Start World or Host World
```

### Backend unavailable before start

```text
shared World
-> authority cannot be verified
-> Connection required
-> Retry connection
```

### Backend unavailable during active session

```text
Running / Hosting
-> connection lost
-> continue session
-> capture safely at end
-> Waiting to sync
-> reconnect and revalidate
-> Ready
or
-> Recovery needed
```

### Close main window

```text
close window
-> window hides
-> tray remains
-> responsibility continues
```

## User-visible state/action contract

| State | Primary action(s) | Secondary behavior |
|---|---|---|
| Ready / Only on this PC | Start World; Host World when supported | Share World |
| Sharing | None | Retry sharing/cancel only when rollback is proven safe |
| Ready / Shared | Start World; Host World when supported | Manage access |
| Connection required | Retry connection | Cached information only |
| Preparing | None | Cancel only before launch when safe |
| Running | Open Steward/game | None |
| Hosting | Open Steward/game | Stop and Save when supported |
| Host is starting | Wait | Refresh/status |
| Someone is playing | Join when supported | Wait/refresh |
| Saving World | None | None |
| Waiting to sync | Automatic bounded retry; Retry when useful | Diagnostics |
| Action required | Resolve issue | Diagnostics |
| Recovery needed | Best proven-safe recovery action | Other proven-safe recovery/export actions |

## Progress contract

Only real lifecycle work is shown, such as:
- Checking latest state;
- Reserving World;
- Downloading World;
- Preparing game;
- Restoring World;
- Starting game/server;
- Waiting for Host readiness;
- Waiting for session end;
- Stopping server;
- Capturing changes;
- Uploading changes;
- Waiting for connection;
- Verifying state;
- Finishing handoff.

No theatrical progress or fake animation is required.

## Error and warning contract

Every failure surface should answer:

1. What failed?
2. Is the last valid state safe?
3. Is a newer candidate preserved?
4. What action is safe now?
5. Where are optional technical details?

## Commercial polish boundary

Deterministic implementation includes:
- keyboard navigation/search affordances;
- screen-reader/automation labeling on core controls;
- resource-owned fixed product vocabulary;
- responsive workspace structure;
- tray accessibility boundaries;
- Windows acceptance packaging and automation tests.

Remaining commercial polish is empirical where appropriate:
- real keyboard/focus behavior;
- Narrator/UI Automation on a real desktop;
- mixed-DPI behavior;
- suspend/restart/logoff behavior;
- real timings/endurance;
- concrete translated locale catalogs when a locale is selected;
- real Steam-owned identity/friend/invitation UX under the production AppID.

## UI implementation checkpoints

- UI-1 Shell/navigation — **DONE** (#147 and existing tray work).
- UI-2 World library/import/search/sort — **DONE** (#147/#149 plus existing import/search).
- UI-3 Lifecycle binding — **DONE deterministically**.
- UI-4 Shared World/access/lobby — **DONE deterministically** (#148 plus existing access/share transaction; production Steam invite UX remains V3-F empirical).
- UI-5 Recovery/action required — **DONE deterministically**.
- UI-6 Commercial polish — **deterministic resource/accessibility/package boundaries implemented; remaining real-Windows/Steam evidence is V3-E/V3-F empirical**.

## UI-0 completion gate

Status: **complete with the documented first-release automatic-Join, publish-first Share, and non-destructive shared-World boundaries.**

UI-0 is approved because:
- UI-D001 through UI-D009 are reconciled with executable behavior;
- every visible state/action is mapped in `CROSS_WORKSTREAM_CONTRACT.md`;
- Host World remains independent of persistent sharing;
- Join is capability-driven and requires a validated automatic path in first release;
- Share publication and membership invitation are separate authority operations;
- destructive shared deletion is absent until a real backend terminal contract exists;
- tray/background semantics preserve unresolved responsibility;
- recovery remains evidence-driven;
- no UI path depends on social roles, ownership hierarchy, branches, merging, or permanent game-server infrastructure.

Deterministic UI feature work should stop here unless a real usability defect or release-gate result proves another first-release behavior is necessary.
