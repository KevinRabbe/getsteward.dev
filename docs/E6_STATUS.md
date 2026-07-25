# E6 Commercial UI Status

Status: **CODE/CI COMPLETE — REAL WINDOWS ASSISTIVE-TECHNOLOGY, KEYBOARD-FOCUS, AND MIXED-DPI ACCEPTANCE REMAINS DEFERRED.**

This checkpoint records the commercial Windows Desktop surface after PR #27. It proves that the first-release UI is composed against the real Steward runtime/backend state machines and that the resulting Windows package builds and passes deterministic acceptance checks. It does **not** claim that Narrator, another real UI Automation client, or a physical mixed-DPI monitor transition has been observed.

## Checkpoint discipline

The last repository checkpoint known to have all five workflows green together remains:

> `62517139`

The E6 stack is still based on the older remote branch that contains two Factorio Windows tests already fixed in the newer unpushed workspace:

- `FactorioModCatalogLinkedPathTests.DiscoverRejectsLinkedStartupSettings`;
- `FactorioModInputSafetyTests.ReproductionRejectsLinkedStartupSettingsBeforeVersionWork`.

On the PR #27 head, Windows proves:

- Core: 84/84;
- Infrastructure: 237/237;
- Backend: 97/97;
- Backend API: 23/23;
- Palworld: 47/47;
- 7 Days to Die: 26/26;
- Project Zomboid: 30/30;
- Architecture: 3/3;
- Windows Desktop build and byte-verifiable acceptance package.

The only Windows failures are the two inherited stale-base Factorio tests above. Do not reimplement those fixes in this E6 stack. Reconcile E6/E8 onto the newer Factorio tree later and establish a new true all-workflows-green checkpoint there.

## Games Library / workspace

Desktop presents one game-first managed-World library rather than separate product shells per adapter.

Implemented behavior:

- supported managed Worlds are grouped/navigable by game;
- World search filters by World/game name;
- selecting a World shows its game, sharing state, exact version, Play actions, settings, environment readiness, responsibility state, and technical details;
- narrow-window behavior switches between the World navigation and selected-World details instead of compressing both into an unusable layout;
- technical identifiers remain available under the explicit **Technical details** surface instead of being required for normal play.

All first-release production adapters are registered in Desktop startup:

- Factorio;
- Palworld;
- 7 Days to Die;
- Project Zomboid.

Registration does not grant capabilities. Start/Host/Join/Stop remain driven by each adapter's actual `GameAdapterCapabilities` flags, so 7DTD/Project Zomboid/Palworld do not gain unsupported product behavior merely because they appear in the Games Library.

## Import

The import workspace is composed against the real adapters and their discovery boundaries:

```text
choose game
-> scan discovered installations
-> discover native Worlds
-> search/select detected World
-> import through WorldLifecycleService
-> create private Steward World
```

Properties:

- import is private by default;
- game tiles and detected World list are driven by real adapter discovery;
- scanning can be repeated;
- detected Worlds can be searched by display/native identity;
- successful import returns to the managed library;
- importing does not silently share the World.

## Start / Host / Join / Stop and Save

### Start

`Start World` is enabled only when the selected adapter exposes automatic local launch and the selected shared World has reached exact-environment `Ready`.

The lifecycle remains the authoritative path:

```text
prepare exact environment
-> restore canonical state
-> launch
-> observe session
-> capture
-> store immutable candidate
-> commit canonical head last
-> cleanup / preserve recovery evidence
```

### Host

`Host World` additionally requires the device hosting preference and the adapter's truthful automatic-host capability.

Desktop does not invent a generic dedicated-server implementation. The adapter owns the concrete host lifecycle.

### Join

Join remains a deliberately read-only lifecycle:

```text
canonical shared environment
-> verify local environment
-> read short-lived host presence
-> launch client against current Ready host
-> no writable reservation
-> no canonical state download/restore
-> no candidate capture/commit
```

PR #26 closes the commercial presentation gap around this existing lifecycle. Join readiness is now visibly presented for shared Worlds instead of living only in a disabled button tooltip.

Visible states include:

- reconnect Steward;
- adapter does not support one-click Join;
- exact environment not ready;
- host-readiness check unavailable;
- nobody is hosting;
- host is starting;
- host is not ready for connections;
- another Steward operation is running;
- host ready — Join available.

The status is a polite UI Automation live region. Local-only Worlds keep it collapsed.

### Stop and Save

The action is visible only while the selected hosted World is in the exact running responsibility state and the adapter advertises automatic host stop.

PR #27 makes its commercial wording adapter-neutral. The UI no longer promises a dedicated-server shutdown mechanism when the active adapter host path may be different.

## Share / Manage access / Invites

The commercial Share surface is composed against the real backend:

```text
private canonical World
-> exact environment preflight
-> durable local Shared intent marker
-> create same World remotely
-> publish exact EnvironmentRevision
-> upload exact StateRevision
-> verify remote publication
-> remote World becomes authoritative
```

If publication becomes ambiguous, Desktop keeps the local shadow locked and exposes **Retry sharing** using the same immutable identities. It never silently returns to local writable authority.

For a complete shared World, **Manage access** exposes the BE-2 flat access model:

- invite Steam ID64;
- list active/revocation-pending members;
- remove access;
- transfer Access Manager responsibility;
- leave after transferring Access Manager responsibility.

The Invites surface accepts or declines real backend invitations.

PR #27 removes backend/canonical-authority vocabulary from the normal successful Share message while leaving the actual authority invariant unchanged.

## Lifecycle progress

E6 does not introduce a second UI-only progress state machine.

`WorldLifecycleResponsibilityTracker` projects the real Core lifecycle into commercial states:

- **Preparing**;
- **Running**;
- **Saving World**;
- **Interrupted session**;
- **Recovery needed**;
- **Action required**.

The same authoritative lifecycle changes refresh:

- the World responsibility banner;
- writable action guards;
- managed game tiles;
- tray status;
- Stop and Save availability.

PR #27 also removes immutable revision GUIDs and `commit/canonical revision` vocabulary from ordinary successful Start/Host messages. Exact IDs remain available under Technical details and precise recovery diagnostics where they are operationally meaningful.

## Tray / background behavior

Closing the main window hides Steward to the tray rather than abandoning responsibility.

The tray:

- exposes accessible **Open Steward** and **Quit Steward** actions;
- reflects Preparing / Running / Saving / recovery/interrupted state;
- blocks Quit while any active or unresolved World responsibility remains;
- restores the main window on open/double-click.

Presentation failure is non-authoritative: it cannot change capture, commit, reservation release, or recovery behavior.

## Recovery / action-required UI

Desktop consumes the durable recovery journal and responsibility tracker before presenting Worlds as ordinary Ready state.

Implemented user-facing recovery states include:

- pending sync / retry recovery;
- restart-found interrupted session;
- **Recover changes**;
- confirmed **Continue from last safe state** / discard interrupted workspace;
- cleanup-only retry;
- export recovery copy.

Recovery action guards prevent ordinary Start/Host while Steward has unresolved responsibility. A recovery export does not mutate canonical state or clear the journal.

## Deterministic accessibility and responsive evidence

CI currently proves the code/configuration side of accessibility:

- meaningful UI Automation names/help text on the primary surfaces and dynamic recovery/import/tray actions;
- polite live-region wiring for status, environment readiness, import results, responsibility changes, and Join readiness;
- keyboard-focus restoration for narrow-layout navigation/import close paths;
- neutral `DesktopText` resources resolve on Windows and safely fall back when a localized catalog is absent;
- responsive narrow-window navigation/detail switching;
- application manifest declares `PerMonitorV2,PerMonitor` DPI awareness;
- exact Windows acceptance package builds successfully.

This is deterministic metadata/wiring evidence. It is not substituted for real assistive-technology observation.

## Commercial wording boundary — PR #27

Normal product success/status text now speaks in user concepts rather than internal persistence implementation:

- `Saved '<World>'.` instead of `committed as revision <GUID>`;
- hosted-session completion reports that the World was saved rather than exposing canonical revision terminology;
- Stop and Save describes safely stopping the hosted session rather than assuming a dedicated server;
- successful Share describes cross-device sharing rather than `backend copy` / `canonical authority` terminology.

Safety-critical recovery language remains deliberately precise. Commercial polish does not hide information needed to prevent history divergence or data loss.

## Deferred live Windows acceptance

`DEFERRED_EMPIRICAL_TESTS.md` remains authoritative for what CI cannot prove.

Still required later on a real Windows desktop:

1. keyboard Tab/Shift+Tab traversal and visible focus inspection across the full first-release surface;
2. Narrator/another Windows UI Automation client observing the intended names and `LiveRegionChanged` announcements;
3. real mixed-DPI monitor transitions under `PerMonitorV2` with no clipped/unreachable primary action.

Those tests do not block independent backend/adapter/storage work, but E6 must not be called fully empirically accepted until that evidence exists.

## E6 completion boundary

E6 is now **code/CI complete**.

Do not continue adding subjective UI polish merely because more refinement is possible. Reopen E6 implementation only for one of these reasons:

- a concrete usability/accessibility defect is observed;
- CI exposes a deterministic presentation defect;
- a first-release product requirement is genuinely missing;
- live Windows acceptance identifies a specific failure.

Otherwise keep the current product surface stable and move development to the next independent roadmap boundary.
