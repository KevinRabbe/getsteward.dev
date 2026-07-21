# UI and UX Roadmap

## Purpose

This roadmap defines the complete first-release user experience before production UI work resumes.

Steward is background-first. The user should enter briefly to select a World, start or host it, and then spend their time in the game.

> **One shared World. Different Steam players. Different times. No always-on game server.**

## Planning status

Status: **planning locked — UI-0 in progress**.

No production UI implementation or refactor begins until UI-0 and the master planning gate are complete.

Allowed during the lock:

- documentation;
- screen-flow diagrams;
- state/action definitions;
- wording and accessibility planning;
- read-only inspection of the existing WPF application.

## Approved UI-0 decisions

### UI-D001: Explicit Start, Host, and Join actions

Status: **approved**.

The first release does not use one contextual `Play` action that guesses the user's intent.

```text
Ready local-only World
-> Start World

Ready shared World
-> Start World
-> Host World

World active on another device
-> Join, when supported
-> otherwise Wait until available
```

Definitions:

- **Start World** starts the latest valid state as local/non-hosted play.
- **Host World** starts the latest valid state as a temporary multiplayer host or dedicated server.
- **Join** connects to the active host through Steam, the game, or adapter-supported behavior.

Rules:

- Start and Host remain separate because Steward cannot always infer the user's intended mode.
- Join replaces writable actions while another device owns the hosted session.
- Start and Host are unavailable whenever they would create a competing writer.
- Actions are driven by generic state and adapter capabilities, never game-name UI branches.
- A contextual Play action may be reconsidered only after real usage proves that automatic choice is dependable.

### UI-D002: Responsive master-detail World workspace

Status: **approved**.

World details remain inside the selected game's workspace. They are not another global navigation destination.

Wide-window layout:

```text
World list
+
selected World details and actions
```

Narrow-window layout:

```text
World list
-> select World
-> focused full-width details
-> Back to Worlds
```

Rules:

- Switching between Worlds stays fast.
- The visible hierarchy remains `Games -> Worlds`.
- The details area may have an internal route or identifier for navigation restoration, but that is not a separate user-facing product section.
- Advanced identifiers and diagnostics remain collapsed.
- Responsive behavior changes presentation, not product semantics.

The details surface shows only what is operationally useful:

- World name;
- concise status;
- Start World and Host World when valid;
- Join when another device hosts;
- current host/device only when useful;
- environment or recovery warning;
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

- Detection and import are read-only toward the original source except for creating Steward-owned copies.
- A successful local import remains valid even when shared setup later fails.
- The user may test the imported World before sharing it.
- **Host World** is unavailable until shared setup succeeds.
- Sharing requires a separate explicit **Share World** action and clear confirmation of who receives access.
- The primary UI uses **Only on this PC** rather than forcing users to understand `LocalOnly` as technical terminology.
- The original save is never described as moved, deleted, or replaced.

Post-import presentation:

```text
Status: Only on this PC
Primary action: Start World
Secondary action: Share World
```

## UI boundaries

The first-release UI owns:

- supported game and World discovery presentation;
- import selection;
- lifecycle status;
- Start World, Host World, Join, Stop and Save, Share World, and recovery actions where supported;
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
3. **Explicit intent:** Start, Host, Join, and Share are separate where supported.
4. **Private by default:** import creates a World only on the current PC until sharing is explicit.
5. **Background first:** the window may disappear while Steward keeps working.
6. **No infrastructure exposure:** primary screens hide revisions, object keys, paths, and server folders.
7. **No false certainty:** uncertainty appears as Recovery needed, never Ready.
8. **No theatrical waiting:** progress corresponds to real lifecycle work.
9. **Capability driven:** actions come from generic state and adapter capabilities.
10. **Commercial clarity:** wording explains what happened, what is safe, and what happens next.
11. **Low interaction cost:** Steward asks only for decisions the game or product genuinely requires.

## Navigation model

First-release global navigation:

- **Games**
- **Import**
- **Settings**

Activity or notifications become a separate section only when proven necessary.

### Games Library

Shows installed supported games as large cards with:

- game artwork and name;
- managed World count;
- attention indicator when one World is active, blocked, or requires recovery.

Unsupported or uninstalled games are not presented as immediately playable.

### Game Workspace

Contains:

- game banner/header;
- search and sort;
- Import World action;
- Worlds belonging to that game;
- responsive selected-World details.

The first release does not use a cramped horizontal game-filter strip inside one global World list.

### Import Workspace

Import is a full workspace:

```text
choose installed supported game
-> search/filter detected Worlds
-> select candidate
-> review source details only when needed
-> Import
-> World becomes Only on this PC
-> optionally Share World later
```

Duplicate native World detection is adapter-owned.

### Active Session Surface

While playing, a compact tray/status surface may show:

- game and World;
- local or hosted mode;
- lifecycle state;
- Open Steward;
- Stop and Save where a controlled hosted stop is supported.

The tray is not a second complete application.

### Recovery Surface

Shows:

- affected game and World;
- whether the last committed state remains safe;
- failed lifecycle stage;
- whether a local recovery candidate exists;
- only actions proven safe by runtime and backend contracts.

No recovery surface offers generic merging.

## Core user journeys

### UJ-01: First launch

```text
open Steward
-> scan installed supported games
-> show Games Library
-> guide to Import when no managed Worlds exist
```

### UJ-02: Import existing World

```text
Import
-> choose game
-> choose detected World
-> capture source safely
-> verify managed local state
-> World appears Ready and Only on this PC
```

The original source is not moved or deleted. A failed import creates no fake usable World.

### UJ-03: Share imported World

```text
Only on this PC
-> Share World
-> authenticate/verify shared service
-> choose allowed Steam identities according to backend policy
-> upload and verify current state
-> shared setup commits
-> World becomes Shared and Ready
```

A failed share operation leaves the local imported World intact and usable locally.

### UJ-04: Start World locally

```text
Ready
-> Start World
-> Preparing
-> Running
-> game ends
-> Saving
-> Ready
```

Closing the game does not mean Ready until capture, durable storage, verification, and commit complete.

### UJ-05: Host World temporarily

```text
Ready shared World
-> Host World
-> Preparing
-> Hosting
-> Stop and Save or adapter-observed safe end
-> Saving
-> Ready
```

A client exit does not end a dedicated-server session while the server remains active.

### UJ-06: Active on another device

```text
Someone is playing
-> Join, when supported
or
-> Wait until available
```

No competing writable action is offered.

### UJ-07: Switch host

```text
current host finishes and commits
-> Ready
-> another device selects same World
-> Host World
```

No live migration language or behavior.

### UJ-08: Switch game

```text
finish current World
-> select another game
-> select one of its Worlds
-> Start World or Host World
```

The interaction is shared; the Worlds are not converted between games.

### UJ-09: Recovery needed

```text
open affected World
-> show last safe state and candidate status
-> perform one proven-safe recovery action
```

No silent promotion, stale overwrite, or generic merge.

## User-visible state and action contract

| State | Meaning | Primary action(s) | Secondary action |
|---|---|---|---|
| Ready, only on this PC | Safe local managed state, not shared | Start World | Share World |
| Sharing | Upload/access setup is incomplete | None | Cancel only when rollback is safe |
| Ready, shared | Safe current shared state | Start World; Host World | Manage access where needed |
| Preparing | State/environment is being prepared | None | Cancel only before launch when safe |
| Running locally here | This device owns local writable session | Open | None |
| Hosting here | This device/server owns hosted session | Open | Stop and Save when supported |
| Active elsewhere | Another device owns hosted session | Join when supported | Wait/refresh |
| Saving | Capture/store/commit incomplete | None | None |
| Blocked | Required environment/capability unavailable | Resolve issue | Diagnostics |
| Recovery needed | Previous handoff did not finish safely | Recover | Last-safe action only when proven safe |
| Offline shared World | Current head/reservation cannot be verified | No writable play | Cached read-only information |

## Progress contract

Only real phases are shown:

- Checking latest state
- Reserving World
- Downloading World
- Preparing game
- Restoring World
- Starting game/server
- Waiting for session end
- Stopping server
- Capturing changes
- Uploading changes
- Verifying state
- Finishing handoff

## Error and warning contract

Every failure message answers:

1. What failed?
2. Is the last valid state safe?
3. Is a newer recovery candidate preserved?
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
- Join replacing conflicting writable actions;
- meaningful progress;
- tray/background state;
- safe close/minimize behavior.

### UI-4: Shared World states

- explicit sharing flow;
- remote head refresh;
- active-elsewhere state;
- Join capability handling;
- offline behavior;
- minimal access/invitation surface after backend policy is approved.

### UI-5: Recovery and blocked states

- recovery candidate presentation;
- proven-safe retry/last-safe actions;
- environment mismatch and repair capability;
- diagnostics reference/export;
- no destructive default action.

### UI-6: Commercial polish

- keyboard navigation;
- high-DPI behavior;
- screen-reader labels;
- responsive resizing and minimum size;
- localization-ready strings;
- installer/update behavior during active sessions;
- acceptance testing on real Windows setups.

## Decisions still required before UI-0 completes

- Exact flat sharing/invitation UI after backend access policy is chosen.
- Exact behavior when a shared World is offline but cached locally.
- Join behavior priority: Steam automatic, adapter connection action, or manual fallback.
- Recovery actions safe enough for first release.
- Tray always present or only during active/background work.
- Final terminology for Stop and Save, Saving, Blocked, and Recovery needed.

## UI planning completion gate

UI planning is complete only when:

- all remaining decisions are resolved or explicitly deferred without blocking implementation;
- backend and runtime expose every required state/action;
- no screen depends on social, ownership, branch, or merge models;
- first-release navigation and journeys are accepted;
- milestones have observable acceptance criteria;
- the master planning lock is explicitly lifted.