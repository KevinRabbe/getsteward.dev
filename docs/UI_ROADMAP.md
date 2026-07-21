# UI and UX Roadmap

## Purpose

This roadmap defines the complete first-release user experience before production UI work resumes.

Steward is background-first. The user should enter briefly to select a World, start or host it, and then spend their time in the game.

> **One shared World. Different Steam players. Different times. No always-on game server.**

## Planning status

Status: **planning locked — UI-0 in progress**.

No production UI implementation or refactor begins until UI-0 and the master planning gate are complete.

Allowed during the lock:

- documentation and screen-flow diagrams;
- state/action and wording decisions;
- accessibility planning;
- read-only inspection of the current WPF application.

## Approved UI-0 decisions

### UI-D001: Explicit Start, Host, and Join actions

Status: **approved**.

The first release does not use one contextual `Play` action that guesses the user's intent.

```text
Ready, only on this PC
-> Start World

Ready, shared
-> Start World
-> Host World

World active on another device
-> Join, when supported
-> otherwise Wait until available
```

Rules:

- **Start World** means local/non-hosted play.
- **Host World** means temporary multiplayer hosting or dedicated-server operation.
- **Join** connects to the active host.
- Join replaces writable actions while another device owns the hosted session.
- Start and Host are unavailable whenever they would create a competing writer.
- Actions are capability-driven, never game-name UI branches.
- A contextual Play action may be reconsidered only after real usage proves the automatic choice dependable.

### UI-D002: Responsive master-detail World workspace

Status: **approved**.

World details remain inside the selected game's workspace. They are not another global navigation destination.

Wide windows show the World list beside selected-World details and actions.

Narrow windows use:

```text
World list
-> select World
-> focused full-width details
-> Back to Worlds
```

Rules:

- switching between Worlds stays fast;
- the visible hierarchy remains `Games -> Worlds`;
- responsive presentation does not change product semantics;
- advanced identifiers and diagnostics remain collapsed.

The details surface contains only operationally useful information:

- World name;
- concise status;
- Start World and Host World when valid;
- Join when another device hosts;
- current host/device only when useful;
- environment or recovery warnings;
- small secondary metadata such as last updated time where useful.

### UI-D003: Import locally before sharing

Status: **approved**.

Every imported World begins as a safe local Steward World on the importing PC. Import never uploads or shares automatically.

```text
select detected World
-> capture source safely
-> verify local managed state
-> World is Ready on this PC
-> optionally Share World as a separate action
```

Rules:

- a successful local import remains valid even when sharing later fails;
- the user may test the imported World before sharing it;
- Host World is unavailable until shared setup succeeds;
- sharing is a separate explicit **Share World** action;
- the primary UI uses **Only on this PC** instead of exposing the technical `LocalOnly` term;
- the original save is never described as moved, deleted, or replaced.

Post-import presentation:

```text
Status: Only on this PC
Primary action: Start World
Secondary action: Share World
```

### UI-D004: Shared Worlds require verification before writable play

Status: **approved**.

A cached shared World remains visible when Steward cannot verify the shared backend, current head, and reservation state, but cached data is not authoritative enough to begin a new writable session.

Before a session:

```text
shared World cached locally
+
shared state cannot be verified
-> Connection required
-> Start World unavailable
-> Host World unavailable
-> Join unavailable
-> Retry connection
```

The UI may still show the World/game name, last successful synchronization time, cached environment information, and last-known state information. It must warn that cached information may be outdated and must not label the World Ready.

Connectivity loss during an already active session follows a different rule:

```text
session already reserved and running
-> connection lost
-> gameplay continues
-> Steward preserves session evidence
-> session ends safely
-> capture updated state locally
-> Waiting to sync
-> reconnect
-> upload, verify, commit
-> Ready
```

Rules:

- a temporary backend outage does not automatically terminate a running game or server;
- captured changes remain preserved locally while synchronization is unresolved;
- another writable session must not begin until the handoff is resolved;
- Steward must not silently overwrite a newer remote state when connectivity returns;
- closing Steward while it owns unsynchronized state is blocked or strongly guarded;
- this deliberately avoids offline branches and later merge requirements.

Suggested wording:

> **Waiting to sync**
>
> Your changes are preserved on this PC. Steward will finish the handoff when the connection returns.

### UI-D005: One capability-driven Join action

Status: **approved**.

The first release exposes one user-facing **Join** action. Users do not choose between Steam Join, direct connect, command-line connect, or another technical connection method.

Priority:

```text
Steam/game-native automatic Join
-> otherwise adapter-controlled automatic Join
-> otherwise guided manual Join
-> otherwise Join unsupported
```

Rules:

- prefer Steam or the game's own native joining path when validated;
- use adapter-controlled automatic connection when the game exposes a reliable supported method;
- use a guided manual fallback when automation is not reliably supported;
- guided manual Join shows only the minimum required connection data and instruction;
- do not use brittle keyboard/mouse automation to imitate unsupported joining;
- the UI always says **Join** regardless of the underlying method;
- Core and UI never contain game-name branches for joining;
- Join remains unavailable while the host is still starting and becomes available only after runtime/adapter readiness is proven.

### UI-D006: Recovery exposes only proven-safe actions

Status: **approved**.

The first release does not present a generic recovery toolbox. Steward examines preserved evidence, the current head, session/reservation generation, adapter capability, and candidate validity, then exposes only actions whose preconditions are proven safe.

Supported first-release recovery actions:

#### Retry recovery

Preferred when Steward has a valid preserved candidate and the interrupted handoff can still be completed safely.

```text
Recovery needed
-> Retry recovery
-> validate candidate
-> verify current head/session generation
-> resume store/upload/verify/commit
-> Ready
```

Retry is unavailable when the candidate is stale, invalid, or cannot safely advance the current head.

#### Continue from last safe state

This abandons the incomplete candidate and returns the World to the last successfully committed state only after explicit confirmation.

Rules:

- the candidate is never silently discarded;
- the UI clearly states that newer local changes may be abandoned;
- shared reservation/recovery state must be resolved before another writer begins;
- this action never combines the candidate with the committed state.

#### Export recovery copy

This is an emergency preservation path when Steward cannot safely complete the canonical handoff.

```text
preserved candidate
-> adapter creates a safe export/package
-> user selects destination
-> canonical shared World remains unchanged
```

Export recovery copy does not create a Steward Fork, branch, second canonical World, or merge workflow.

Recovery never offers:

- Merge;
- Force overwrite remote;
- Use newest file automatically;
- Make branch/Fork;
- silent candidate promotion;
- silent candidate deletion.

Typical recovery presentation:

> **Recovery needed**
>
> Your last safe World is still protected. Steward also found newer changes from the interrupted session.

Show only applicable actions, normally ordered:

1. **Retry recovery** — preferred when safe;
2. **Export recovery copy** — preservation fallback when useful;
3. **Continue from last safe state** — explicit abandon action, visually separated.

### UI-D007: Persistent tray while Steward is running

Status: **approved**.

The tray is a compact status/control surface, **not a second application UI**.

Whenever the Steward user-session process is running, a tray icon is present. Closing the main window hides the window and leaves Steward available through the tray.

Normal idle behavior:

```text
Steward process running
-> window open or hidden
-> tray icon present
```

The idle tray exposes only small global controls such as:

- **Open Steward**;
- concise important status when one exists;
- **Quit Steward** when quitting is safe.

During an active World session the tray may show:

- game and World;
- Running or Hosting status;
- connectivity/synchronization warning when relevant;
- **Open Steward**;
- **Stop and Save** only when the adapter/runtime supports a controlled hosted stop.

During post-game work it may show statuses such as:

- Saving;
- Waiting to sync;
- Recovery needed.

Quit rules:

```text
no active or unresolved World responsibility
-> Quit Steward
-> process exits
-> tray disappears
```

```text
Running / Hosting / Saving / Waiting to sync / active recovery
-> ordinary Quit must not silently abandon the responsibility
```

Rules:

- closing the main window is not equivalent to quitting the process;
- no invisible user-session background process: if Steward is running, its tray presence is visible;
- the tray does not browse Games, Worlds, imports, settings, or diagnostics as a second application shell;
- active hosted sessions end through the correct adapter/runtime session-ending path;
- Saving and Waiting to sync cannot be silently abandoned;
- explicit exceptional exits, when later defined, must preserve recovery evidence;
- **Start Steward when I sign in** is a separate user setting and does not change lifecycle semantics.

Core principle:

> **The UI window may close. The responsibility may not.**

## UI boundaries

The first-release UI owns:

- supported game and World discovery presentation;
- import and sharing entry points;
- lifecycle status;
- Start World, Host World, Join, Stop and Save, Share World, retry, and recovery actions where supported;
- progress and failure communication;
- tray/background visibility;
- compact settings and diagnostics.

The UI does not own:

- game-specific lifecycle logic;
- save parsing;
- process detection;
- reservation rules;
- storage transactions;
- Steam friend or party systems;
- World ownership hierarchies;
- branches, Forks, or merges;
- public server discovery.

## UX principles

1. **World first:** the World is the primary selectable product object.
2. **Game-first navigation:** a game workspace contains only that game's Worlds.
3. **Explicit intent:** Start, Host, Join, and Share are distinct actions where supported.
4. **Private by default:** import stays on the current PC until sharing is explicit.
5. **Background first:** the main window may disappear while Steward remains visibly available in the tray and keeps working.
6. **No second tray application:** the tray communicates status and exposes only necessary global/session controls.
7. **No infrastructure exposure:** primary screens hide revisions, object keys, paths, and server folders.
8. **No false certainty:** uncertain state is never presented as Ready.
9. **No theatrical waiting:** progress corresponds to real lifecycle work.
10. **Capability driven:** actions come from generic state and adapter capabilities.
11. **Commercial clarity:** wording explains what happened, what is safe, and what happens next.
12. **Low interaction cost:** Steward asks only for decisions the product genuinely requires.
13. **Recovery is evidence-driven:** destructive or authority-changing actions appear only when their safety can be proven.

## Navigation model

First-release global navigation:

- **Games**;
- **Import**;
- **Settings**.

Activity or notifications become a separate section only when proven necessary.

### Games Library

Shows installed supported games as large cards with:

- game artwork and name;
- managed World count;
- attention indicator when a World is active, blocked, waiting to sync, or requires recovery.

### Game Workspace

Contains:

- game banner/header;
- search and sort;
- Import World action;
- Worlds belonging to that game;
- responsive selected-World details.

### Import Workspace

```text
choose installed supported game
-> search/filter detected Worlds
-> select candidate
-> review source details only when needed
-> Import
-> World becomes Only on this PC
-> optionally Share World later
```

Duplicate native World detection remains adapter-owned.

### Tray/status surface

The tray is present whenever Steward runs and exposes compact status plus only the controls required to reopen Steward, safely stop a supported hosted session, or quit when safe.

It is not another Games/Worlds navigation surface.

### Recovery Surface

Shows:

- affected game and World;
- whether the last committed state remains safe;
- failed lifecycle stage;
- whether a local recovery or unsynchronized candidate exists;
- only proven-safe recovery actions.

No recovery surface offers generic merging or forced canonical overwrite.

## Core user journeys

### UJ-01: First launch

```text
open Steward
-> scan installed supported games
-> show Games Library
-> tray remains available while Steward process runs
```

### UJ-02: Import existing World

```text
Import
-> choose game
-> choose detected World
-> capture source safely
-> verify managed local state
-> Ready / Only on this PC
```

### UJ-03: Share imported World

```text
Only on this PC
-> Share World
-> verify shared service
-> choose allowed Steam identities according to backend policy
-> upload and verify current state
-> shared setup commits
-> Ready / Shared
```

A failed share operation leaves the local World intact and usable locally.

### UJ-04: Start World locally

```text
Ready
-> Start World
-> Preparing
-> Running
-> main window may hide to tray
-> game ends
-> Saving
-> Ready
```

### UJ-05: Host World temporarily

```text
Ready / Shared
-> Host World
-> Preparing
-> Hosting
-> main window may hide to tray
-> Stop and Save or adapter-observed safe end
-> Saving
-> Ready
```

### UJ-06: Active on another device

```text
Someone is playing
-> host becomes ready
-> Join, when supported
or
-> Wait until available
```

### UJ-07: Switch host

```text
current host finishes and commits
-> Ready
-> another device selects same World
-> Host World
```

### UJ-08: Switch game

```text
finish current World
-> select another game
-> select one of its Worlds
-> Start World or Host World
```

### UJ-09: Shared World unavailable before start

```text
open shared World
-> backend verification fails
-> Connection required
-> no writable action
-> Retry
```

### UJ-10: Connection lost during active session

```text
Running/Hosting
-> connection lost
-> continue session
-> capture safely at end
-> Waiting to sync
-> tray remains visible
-> reconnect
-> commit
-> Ready
```

### UJ-11: Join active hosted World

```text
Someone is playing
-> host becomes ready
-> Join
-> Steward selects best validated connection method
-> game/session join begins
```

### UJ-12: Recovery needed

```text
open affected World
-> verify recovery evidence/current authority
-> expose only applicable safe actions
-> Retry recovery
or
-> Export recovery copy
or
-> explicitly Continue from last safe state
```

No silent promotion, stale overwrite, candidate deletion, or generic merge.

### UJ-13: Close Steward window during active work

```text
close main window
-> window hides
-> Steward remains in tray
-> active World responsibility continues
```

### UJ-14: Quit Steward

```text
no active/unresolved responsibility
-> Quit Steward
-> process exits
```

When an active or unresolved responsibility exists, ordinary Quit is blocked or redirected to the correct safe action rather than abandoning the World transaction.

## User-visible state and action contract

| State | Meaning | Primary action(s) | Secondary action |
|---|---|---|---|
| Ready, only on this PC | Safe local managed state, not shared | Start World | Share World |
| Sharing | Upload/access setup incomplete | None | Cancel only when rollback is safe |
| Ready, shared | Current shared state and reservation service verified | Start World; Host World | Manage access where needed |
| Connection required | Shared current head/reservation cannot be verified before start | Retry connection | Cached information only |
| Preparing | State/environment is being prepared | None | Cancel only before launch when safe |
| Running locally here | This device owns local writable session | Open | None |
| Hosting here | This device/server owns hosted session | Open | Stop and Save when supported |
| Host starting elsewhere | Another device owns hosted session but is not ready to accept players | Wait | Refresh/status |
| Active elsewhere | Another device owns a ready hosted session | Join when supported | Wait/refresh |
| Saving | Capture/store/commit incomplete | None | None |
| Waiting to sync | Updated state is preserved locally but remote handoff is incomplete | Automatic retry; manual Retry when useful | Diagnostics |
| Blocked | Required environment/capability unavailable | Resolve issue | Diagnostics |
| Recovery needed | Previous handoff did not finish safely | Best proven-safe recovery action | Other proven-safe recovery/export actions |

## Progress contract

Only real phases are shown:

- Checking latest state;
- Reserving World;
- Downloading World;
- Preparing game;
- Restoring World;
- Starting game/server;
- Waiting for host readiness;
- Waiting for session end;
- Stopping server;
- Capturing changes;
- Uploading changes;
- Waiting for connection;
- Verifying state;
- Finishing handoff.

## Error and warning contract

Every failure message answers:

1. What failed?
2. Is the last valid state safe?
3. Is a newer recovery/unsynchronized candidate preserved?
4. What action is safe now?
5. Where are optional technical details?

## UI roadmap milestones

### UI-0: Planning contract

Deliverables:

- approved user journeys;
- approved state/action matrix;
- approved navigation and responsive screen map;
- approved terminology;
- adapter capability presentation rules;
- explicit first-release exclusions;
- cross-workstream contract with backend and runtime.

### UI-1: Shell and navigation replacement

- global rail;
- Games Library;
- game workspace routing;
- persistent tray lifetime;
- remove transitional composition over obsolete UI where safe;
- intentional artwork slots and fallbacks.

### UI-2: World library and import

- per-game World list;
- responsive details;
- search/sort;
- full import workspace;
- explicit local-first import result;
- Share World entry point;
- empty, loading, partial-failure, and no-install states.

### UI-3: Lifecycle binding

- generic state-driven UI;
- explicit Start and Host;
- capability-driven Join;
- meaningful progress;
- persistent tray/background state;
- safe close/hide behavior;
- safe Quit gating.

### UI-4: Shared World states

- explicit sharing flow;
- remote head refresh;
- host-starting and active-elsewhere states;
- capability-driven Join behavior;
- Connection required and Waiting to sync behavior;
- minimal access/invitation surface after backend policy is approved.

### UI-5: Recovery and blocked states

- recovery evidence presentation;
- Retry recovery only when safe;
- explicit Continue from last safe state when safe;
- Export recovery copy as non-canonical preservation fallback;
- environment mismatch and repair capability;
- diagnostics reference/export;
- no destructive default action;
- no merge or forced overwrite path.

### UI-6: Commercial polish

- keyboard navigation;
- high-DPI behavior;
- screen-reader labels;
- responsive resizing and minimum size;
- localization-ready strings;
- installer/update behavior during active sessions;
- tray accessibility and understandable status text;
- acceptance testing on real Windows setups.

## Decisions still required before UI-0 completes

- Exact flat sharing/invitation UI after backend access policy is chosen.
- Final terminology for Stop and Save, Saving, Blocked, Recovery needed, Connection required, Waiting to sync, and related concise status wording.

The sharing/invitation question is intentionally blocked on the backend access-policy decision and must not be guessed by UI planning.

## UI planning completion gate

UI planning is complete only when:

- all remaining decisions are resolved or explicitly deferred without blocking implementation;
- backend and runtime expose every required state/action;
- no screen depends on social, ownership, branch, or merge models;
- first-release navigation and journeys are accepted;
- milestones have observable acceptance criteria;
- the master planning lock is explicitly lifted.