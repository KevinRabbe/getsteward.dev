# UI and UX Roadmap

## Purpose

This roadmap defines the complete first-release user experience before production UI work resumes.

Steward is background-first. The user should enter Steward briefly to select a World, start or host it, and then spend their time in the game.

The UI must express the product promise:

> **One shared World. Different Steam players. Different times. No always-on game server.**

## Planning status

Status: **planning locked — UI-0 in progress**.

No production UI implementation or refactor begins until the UI planning gate is complete and the master roadmap explicitly lifts the lock.

During the lock, allowed work is limited to documentation, screen-flow diagrams, state/action definitions, content wording, and read-only inspection of the existing WPF application.

## Approved UI-0 decisions

### UI-D001: Explicit Start, Host, and Join actions

Status: **approved**.

The first release does not use one contextual `Play` action that guesses the user's intent.

Action model:

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
- **Join** connects to the currently active host through Steam, the game, or adapter-supported connection behavior.

Rules:

- Start World and Host World are separate explicit choices because the runtime cannot always infer whether the user wants private/local play or multiplayer hosting.
- Join replaces writable start actions while another device owns the active hosted session.
- Start World and Host World are hidden or disabled whenever either would create a competing writable session.
- Unsupported actions are omitted or explained through adapter capability state; the UI never branches on a game name.
- A future contextual `Play` action may be reconsidered only after real usage proves a reliable automatic choice across supported adapters.

## UI boundaries

The first release UI owns:

- game and World discovery presentation;
- import selection;
- World readiness and lifecycle status;
- Start World, Host World, Join, Stop and Save, and recovery actions where supported;
- progress and error communication;
- background/tray visibility;
- compact settings and diagnostics.

The UI does not own:

- game-specific lifecycle logic;
- save parsing;
- process detection;
- session reservation rules;
- storage transactions;
- Steam friend or party systems;
- World ownership hierarchies;
- branch, Fork, or merge workflows;
- a public server browser.

## UX principles

1. **World first:** the World is the primary selectable product object.
2. **Game first navigation:** users enter a game workspace and see only that game's Worlds.
3. **Explicit intent:** Ready Worlds expose Start World and Host World as separate actions where supported; the UI does not guess the user's desired mode.
4. **Background first:** the application becomes quiet after launch but remains operational.
5. **No infrastructure exposure:** primary screens do not show revision hashes, object keys, save paths, server folders, or adapter internals.
6. **No false certainty:** uncertain state appears as Recovery needed, never Ready.
7. **No theatrical waiting:** progress communicates real work only.
8. **Adapter capabilities, not game-name branches:** available actions come from generic capability and lifecycle state.
9. **Commercial clarity:** wording must state what happened, what is safe, and what the user can do next.
10. **Low interaction cost:** normal play should require as few decisions as the game actually requires.

## Navigation model

The first-release shell uses a compact global navigation rail:

- **Games**
- **Import**
- **Settings**

Activity or notifications may be added only when the background lifecycle proves that a separate surface is necessary.

### Games Library

The Games Library shows installed supported games as large cards using appropriate Steam/game artwork.

Each card shows only useful summary information:

- game name;
- managed World count;
- attention state when one World requires recovery or is currently active.

### Game Workspace

Selecting a game opens its workspace:

- wide game header/banner;
- search and sort;
- Import World action;
- Worlds belonging only to that game;
- selected World details and available actions.

The first release does not use a cramped horizontal game-filter strip inside the Worlds list.

### World Details

World details remain inside the selected game's workspace rather than becoming another global navigation root.

The primary area shows:

- World name;
- concise status;
- current host/device identity only when operationally useful;
- Start World and Host World when both are valid;
- Join instead of writable start actions when another device hosts;
- environment or recovery warning when blocked.

Advanced identifiers and diagnostics remain collapsed.

### Import Workspace

Import is a full workspace, not a dropdown.

Flow:

```text
choose supported installed game
-> search/filter detected Worlds for that game
-> select one candidate
-> review source name/location only when needed
-> Import
```

Duplicate discovery is adapter-owned. The UI receives one intentional candidate per native World wherever possible.

### Active Session Surface

While a session runs, Steward stays mostly in the background.

A compact tray/status surface shows:

- game and World;
- local or hosted mode;
- current lifecycle state;
- Open Steward;
- Stop and Save only where the adapter/runtime supports an explicit safe hosted stop.

The tray is not a second complete application UI.

### Recovery Surface

Recovery has a dedicated focused surface because it protects user state.

It shows:

- affected game and World;
- last known-good state remains safe;
- what stage failed;
- whether a local recovery candidate exists;
- only actions that the runtime/backend can perform safely.

No recovery action may imply generic save merging.

## Core user journeys

### UJ-01: First launch

```text
open Steward
-> scan installed supported games
-> show Games Library
-> show Import when no managed Worlds exist
```

Acceptance:

- no empty technical dashboard;
- no unsupported games presented as ready;
- scan failure for one adapter does not misrepresent other adapters.

### UJ-02: Import existing World

```text
Import
-> choose game
-> choose detected World
-> Steward captures source safely
-> managed World appears Ready
```

Acceptance:

- original source is not implied to be moved or deleted;
- import progress reflects real capture/store work;
- failure leaves no fake managed World.

### UJ-03: Start World locally

```text
select Ready World
-> Start World
-> Preparing
-> Running
-> game closes
-> Saving
-> Ready
```

Acceptance:

- UI may minimize after launch;
- closing the game does not mark Ready before capture and commit complete;
- failure after gameplay becomes Recovery needed.

### UJ-04: Host World temporarily

```text
select Ready shared World
-> Host World
-> Preparing server/environment
-> Running as host
-> Stop and Save or adapter-observed safe end
-> Saving
-> Ready
```

Acceptance:

- hosted session remains active when a client closes if the server still runs;
- Stop and Save is available only when the runtime can perform a controlled stop;
- World remains unavailable until commit succeeds.

### UJ-05: World active on another device

```text
select World
-> status: Someone is playing
-> Join when supported
or
-> Wait until available
```

Acceptance:

- Start World and Host World do not appear when either would create a competing writer;
- Join uses Steam/game/adapter capabilities;
- UI does not invent a social party workflow.

### UJ-06: Switch host

```text
current host finishes and commits
-> World becomes Ready
-> another device selects same World
-> Host World
```

Acceptance:

- no live migration language;
- next host always starts from the latest committed state;
- uncertain previous session blocks a new writer until recovery policy resolves it.

### UJ-07: Switch game

```text
finish current World
-> select another game
-> select one of that game's Worlds
-> Start World or Host World
```

Acceptance:

- same user method across games;
- adapter differences appear only as supported/unsupported actions and game-specific limitations;
- no implication of converting Worlds between games.

### UJ-08: Recovery needed

```text
open affected World
-> show last safe state and recovery candidate status
-> Retry safe handoff action
or
-> explicitly continue from last safe state when policy permits
```

Acceptance:

- no silent candidate promotion;
- no generic merge option;
- stale recovery candidate cannot overwrite a newer state silently.

## User-visible state and action contract

| State | Meaning | Primary action(s) | Secondary action |
|---|---|---|---|
| Ready, local-only | Safe local state, not shared | Start World | Enable sharing only when shared backend is available |
| Ready, shared | Safe current shared state | Start World; Host World | None by default |
| Preparing | Environment/state is being prepared | None | Cancel only before launch when safe |
| Running locally here | This device owns the writable local session | Open game/Steward | None |
| Hosting here | This device/server owns the writable hosted session | Open | Stop and Save when supported |
| Active elsewhere | Another device owns the writable session | Join when supported | Wait/refresh |
| Saving | Capture/store/commit is incomplete | None | None |
| Blocked | Required environment or capability is unavailable | Resolve issue | Diagnostics |
| Recovery needed | Last session did not complete safely | Recover | Continue from last safe state only when explicitly safe |
| Offline shared World | Shared reservation/current head cannot be verified | None for writable play | Use cached read-only information |

The first-release action model is explicit. Steward does not collapse Start World and Host World into a contextual Play action.

## Progress contract

Only real lifecycle phases are shown:

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

Do not create decorative pseudo-progress that cannot correspond to real work.

## Error and warning contract

Every user-facing failure message must answer:

1. What failed?
2. Is the last valid World state safe?
3. Is a newer local recovery candidate preserved?
4. What action is safe now?
5. Is technical detail available without forcing it into the main screen?

## UI roadmap milestones

### UI-0: Planning contract

Deliverables:

- approved user journeys;
- approved state/action matrix;
- approved navigation and screen map;
- wording for Ready, active, saving, blocked, and recovery states;
- adapter capability presentation rules;
- explicit first-release exclusions;
- cross-workstream UI contract with backend and runtime.

No production UI code starts before UI-0 and the master planning gate are complete.

### UI-1: Shell and navigation replacement

- stable global rail;
- Games Library;
- game workspace routing;
- removal of transitional runtime layering over obsolete UI where safe;
- intentional artwork slots for icon, banner, and fallback.

### UI-2: World library and import

- per-game World list;
- selected World details;
- search/sort;
- full import workspace;
- duplicate-free adapter candidate presentation;
- empty, loading, partial-failure, and no-install states.

### UI-3: Lifecycle binding

- UI driven by generic lifecycle state;
- explicit Start World and Host World actions;
- Join replacing writable start actions when another device hosts;
- meaningful progress;
- disabling conflicting actions;
- background/tray state;
- safe close/minimize behavior.

### UI-4: Shared World states

- remote current-head refresh;
- active-elsewhere state;
- Join when supported;
- offline/connection failure behavior;
- minimal flat access/invitation surface after backend policy is decided.

### UI-5: Recovery and blocked states

- recovery candidate presentation;
- retry/last-safe actions;
- environment mismatch and repair capability;
- diagnostics export/reference;
- no destructive default action.

### UI-6: Commercial polish

- keyboard navigation;
- high-DPI behavior;
- screen-reader labels for primary controls;
- responsive resizing and minimum-size policy;
- clear loading/error states;
- localization-ready strings;
- visual consistency;
- installer/update interaction during active sessions;
- acceptance testing on real Windows setups.

## Decisions still required before UI-0 completes

- Exact layout of World details inside the selected game's workspace: persistent details pane, route-like focused view, or responsive combination.
- Exact flat sharing/invitation UI after backend access policy is chosen.
- Whether local-only Worlds remain a first-release user concept or import immediately offers shared setup.
- Exact behavior when a shared World is offline but cached locally.
- Whether Join opens through Steam automatically, adapter connection data, or a manual instruction fallback per capability.
- Which recovery actions are safe enough for first release.
- Whether tray presence is always enabled or only during active work.
- Final terminology for Stop and Save, Saving, and Recovery needed; Start World, Host World, and Join are approved.

## UI planning completion gate

UI planning is complete only when:

- all decisions above are resolved or explicitly deferred without blocking the first implementation slice;
- backend and runtime expose every state/action the UI requires;
- no screen depends on a social, ownership, branch, or merge model;
- the first-release navigation and user journeys are accepted;
- every milestone has observable acceptance criteria;
- the master roadmap lifts the planning lock.