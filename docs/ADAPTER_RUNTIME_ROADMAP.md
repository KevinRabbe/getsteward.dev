# Adapter and Background Runtime Roadmap

Status: **CURRENT RUNTIME CONTRACT — GENERIC LIFECYCLE IMPLEMENTED; NEW CAPABILITIES REMAIN EVIDENCE-DRIVEN.**

## Purpose

The generic runtime carries one World through the complete Steward responsibility lifecycle while game-specific adapters prove how their game makes each supported step safe.

```text
UI chooses World/action
-> runtime resolves local/shared authority
-> adapter prepares/restores/launches/observes
-> adapter proves game-specific completion/capture boundary
-> runtime stores/verifies/commits
-> runtime finalizes or preserves recovery responsibility
-> UI/tray reflects resulting state
```

Steward is background-first because responsibility continues while the user is inside the game and may continue after the main window is hidden.

## Current runtime boundary

The generic runtime owns:

- one writable Steward session per World;
- one active managed writable lifecycle per device in the first release;
- local/shared authority composition;
- exact state/environment materialization through `IWorldStorage`;
- writable reservation acquisition/heartbeat/release/recovery through `IWorldSessionCoordinator`;
- durable workspace recovery registration;
- generic lifecycle ordering and typed failure mapping;
- local candidate preservation until authority is resolved;
- read-only automatic Join orchestration;
- background/tray responsibility.

The adapter owns:

- installation and World discovery;
- environment inspection/verification/preparation;
- native creation where advertised;
- restore/capture package shape;
- local/Host/client launch details;
- the process/server that actually owns the writable session;
- Host readiness evidence;
- game-owned connection endpoint material;
- safe Host stop when advertised;
- safe capture timing;
- package/game-specific validation;
- automatic Join capability or explicit unsupported/blocked result;
- game-specific identity/environment limitations.

Backend owns shared durable authority. UI owns presentation and user commands.

## Process model

> **One user-session Desktop process with tray/background lifetime; no Windows Service.**

The current model is deliberate:

- game/Steam interaction happens in the signed-in user's session;
- user-visible tray/background state is required;
- durable recovery records protect against process/OS failure;
- a privileged service would add IPC/security/update/debug ownership without current evidence of need.

Closing the main window hides it. Ordinary Quit must not abandon active/unresolved responsibility.

A Windows Service is reconsidered only if real reliability evidence proves the user-session process insufficient.

Steam owns actual Steward installation/update. Runtime still prevents any controlled application replacement/restart path from pretending active World responsibility can be abandoned safely.

## Concurrency model

First release enforces:

```text
at most one writable Steward session per World globally
AND
at most one active managed writable Steward lifecycle per device
```

Multiple cached/downloaded Worlds may exist. One Desktop does not supervise several writable World lifecycles concurrently.

## Generic writable lifecycle

The implemented lifecycle is conceptually:

```text
Idle
-> resolve World/current heads
-> acquire local/distributed writable authority
-> materialize/download required immutable state/environment
-> adapter prepares environment
-> adapter restores state
-> persist durable recovery responsibility
-> launch local or Host session
-> prove session start/readiness where required
-> Running / Hosting
-> observe or request safe end
-> establish game-specific capture boundary
-> capture candidate
-> store/upload immutable candidate
-> verify durable result
-> expected-head + valid-generation commit
-> finalize workspace
-> resolve reservation/recovery responsibility
-> Completed / Ready
```

Internal implementation names may be more granular. User-facing terms remain the product state contract rather than internal enum names.

## State projection

Typical user-facing mapping:

| Runtime meaning | Product term |
|---|---|
| safe/available | Ready |
| preparing/materializing/restoring/starting | Preparing |
| local writable session active | Running |
| hosted writable session active here | Hosting |
| another current Host owns authority but is not Ready | Host is starting |
| another ready writer/Host owns the active session | Someone is playing |
| capture/store/verify/commit/finalize incomplete | Saving World |
| local candidate preserved; remote handoff unresolved | Waiting to sync |
| capability/environment/identity condition blocks safe work | Action required |
| prior authority/evidence remains unresolved | Recovery needed |
| restart found an `Active` workspace whose gameplay state is unknown | Interrupted session |

Presentation is a projection. UI/tray failure cannot change authority, commit, capture, or recovery behavior.

## Local-only writable start

```text
load local canonical head
-> acquire local exclusivity
-> adapter prepares environment
-> restore canonical state
-> persist recovery record
-> launch local play or temporary Host where capability exists
-> observe/prove session
```

Persistent Steward sharing is not required merely to Host a local-only World when the adapter/runtime supports that Host path.

## Shared writable start

```text
authenticate/refresh metadata
-> read exact state/environment head
-> verify exact local environment as required
-> acquire expected-head distributed reservation
-> download/materialize immutable packages as required
-> adapter prepares/restores
-> persist recovery record with exact starting heads
-> launch local play or temporary Host
-> prove session/readiness
-> maintain reservation heartbeat
```

If preparation/launch fails before gameplay is proven, Core may use the narrow pre-launch abandon/cleanup path when exact authority ownership makes that safe.

Once gameplay may have started, workspace/candidate state is recovery evidence rather than disposable temporary data.

## Session evidence contract

Adapters expose enough evidence for the runtime to distinguish at least:

- launch requested but no real session started;
- local/Host session actually started;
- Host became ready for clients;
- session still runs;
- client process ended while authoritative server may still run;
- graceful stop requested/completed where supported;
- session ended normally;
- session disappeared unexpectedly;
- safe capture boundary established;
- capture incomplete/blocked;
- recovery evidence must be preserved.

Evidence may use process identity/start time, launcher handoff, local control APIs, native logs/readiness, shutdown responses, file/package checks, or other adapter-owned signals.

A PID alone is never the universal definition of a game session.

## Running/Hosting responsibilities

While a writable session is active, runtime must:

- maintain shared heartbeat while connectivity permits;
- keep the same reservation generation/starting heads associated with responsibility;
- reject another managed writable lifecycle on the device;
- observe adapter-defined authoritative session ownership;
- keep recovery metadata durable;
- keep UI/tray status available without making presentation authoritative;
- not infer gameplay end from one missed heartbeat/network failure;
- not allow app exit/update/restart to silently abandon the lifecycle.

## Host endpoint and Host presence

An adapter with a managed network endpoint may expose game-owned connection material through the optional Host-endpoint contract.

Typical flow:

```text
exact writable reservation
-> managed Host starts
-> adapter proves readiness
-> adapter exposes game-owned port/token/address material it actually knows
-> shared coordinator publishes short-lived Host presence tied to exact session/generation
-> existing heartbeat refreshes presence
-> Join/presentation reads it
-> presence clears before capture/commit completes
```

Host presence is not writable authority.

Adapters do not need to invent public-IP/NAT traversal just to implement this contract. Deployment/backend may derive the observed HTTPS client address under the narrow trusted-proxy rule; game-network reachability remains empirical.

## Automatic Join lifecycle

First-release `IGameAdapter` Join capability remains automatic or unavailable:

```text
SupportedAutomatic
Unsupported
BlockedByEnvironment
BlockedByIdentityLimitation
```

Automatic Join is deliberately read-only:

```text
shared World metadata
-> exact environment
-> verify/prepare local client environment
-> read current Ready Host presence
-> adapter confirms automatic Join capability
-> adapter launches client against current Host
-> discard read-only preparation
```

It does **not**:

- acquire another writable reservation;
- restore canonical writable state merely for joining;
- register writable recovery responsibility;
- capture/commit candidate state.

The active Host remains the writer.

### Removed guided-manual lifecycle

The earlier concept of a generic guided manual Join lifecycle remains removed.

Steward has no generic contract for:

```text
prepare manual client workspace
-> user independently launches/plays
-> Steward somehow proves manual client completion
-> Steward owns cleanup timing
```

Do not reintroduce that lifecycle without a real adapter need and complete ownership/evidence contract.

### Manual direct-connect presentation is different

Steward also has the narrower optional `IManualDirectConnectProvider` contract.

It is **presentation only** for a game whose native UI can consume an already-ready published endpoint while Steward lacks a validated automatic client-launch path.

```text
Ready HostConnection
-> adapter formats native endpoint + instruction
-> UI shows/copies guidance
```

It does not prepare a manual session, launch a client, observe client completion, or own cleanup.

Palworld currently uses this distinction: it does not advertise `AutomaticClientJoin`, but it can present its ready game-owned direct-connect endpoint without pretending Steward owns the client's manual multiplayer lifecycle.

## Stop and Save

User-triggered **Stop and Save** is available only when `AutomaticHostStop` is advertised and the adapter can perform/validate the safe Host stop contract.

```text
request adapter-controlled safe stop
-> prove authoritative Host ended
-> prove final save/capture boundary
-> capture candidate
-> store/upload + verify
-> expected-head/generation commit
-> resolve authority/recovery
```

The runtime never assumes “kill process + fixed delay” is a universal save protocol.

An adapter may have an internal completion mechanism used after its own Host session naturally ends without advertising `AutomaticHostStop`; that does not create a user-triggered Stop capability automatically.

Factorio is the current example: its active hosted completion path uses RCON `/server-save`, proves the save changed, and ends the managed server after the host client ends, but it does not currently advertise `AutomaticHostStop`.

Palworld currently advertises `AutomaticHostStop` because its proven localhost REST/process-tree lifecycle owns an explicit safe managed stop.

## Safe capture contract

Before capture, the adapter establishes the minimum game-specific conditions required for a restorable package.

Possible evidence includes:

- authoritative process/server ended normally;
- native save/shutdown command completed;
- required files exist;
- native transaction/lock/temp state is absent;
- files/selectors remain stable under a validated rule;
- server API confirmed a save;
- package-specific invariants pass.

Core asks for a safe result. It does not implement a universal sleep/file-timestamp assumption.

## Capture/commit contract

```text
adapter captures smallest complete game-owned package
-> adapter validates required contents
-> runtime stores/publishes immutable candidate
-> runtime verifies durable result
-> current expected state/environment head is rechecked
-> exact writable generation remains valid
-> canonical head advances atomically
```

Generic outcomes preserve safety:

- committed -> new canonical state;
- unchanged -> previous canonical state remains;
- stale head -> candidate cannot overwrite current state;
- invalid reservation/generation -> candidate cannot commit;
- capture/store/verification failure -> preserve previous canonical state and required recovery evidence;
- connectivity loss after gameplay -> durable local candidate may enter Waiting to sync.

Cleanup follows durability/authority, never the reverse.

## Connectivity loss

### Before a new shared session/Join decision

If identity/current heads/authority cannot be verified:

```text
Connection required
-> no new Start
-> no new Host
-> no automatic Join
```

### During an already-valid writable session

Gameplay may continue locally while backend connectivity is temporarily unavailable.

Remote authority may become Uncertain; no competing writer becomes automatically available.

### Session ends while disconnected

```text
adapter proves safe capture
-> capture/validate local candidate
-> persist candidate durably
-> Waiting to sync
```

Reconnect revalidates identity, exact reservation generation, and starting/current state/environment heads.

Only a still-valid generation + unchanged expected head may resume automatic handoff. Divergence becomes Recovery needed with candidate preserved.

## Application restart/recovery

Startup scans durable recovery records before presenting affected Worlds as ordinary Ready.

A restart-found `Active` workspace becomes **Interrupted session**, because the record proves Steward owned a workspace but not whether gameplay actually began or reached a safe capture point.

User decisions remain explicit:

```text
Recover changes
-> exact journaled environment/workspace required
-> Active -> RecoveryPending
-> stable candidate identity
-> deterministic recovery

Discard interrupted session
-> destructive confirmation
-> Active -> CleanupPending
-> canonical World remains unchanged
-> adapter-owned cleanup only
```

Unknown state is preserved rather than guessed.

## Adapter capability model

Current executable flags are:

```text
Mods
AutomaticHostLaunch
AutomaticClientJoin
ExactGameVersion
ExactModVersions
EnvironmentIsolation
AutomaticLocalLaunch
AutomaticHostStop
NativeWorldCreation
```

Capabilities describe proven product behavior, not code that happens to exist somewhere in the adapter.

Release/action mapping:

```text
Start World -> AutomaticLocalLaunch
Host World  -> AutomaticHostLaunch
Join        -> AutomaticClientJoin + current JoinCapabilityResult
Stop/Save   -> AutomaticHostStop
Create      -> NativeWorldCreation
```

UI/Core do not branch on game name to interpret those capabilities.

## Current reference adapters

### Factorio

Current capability includes:

- Mods;
- AutomaticLocalLaunch;
- AutomaticHostLaunch;
- AutomaticClientJoin;
- ExactGameVersion;
- NativeWorldCreation.

Active `IGameAdapter` Host path:

```text
private dedicated Factorio server
-> game UDP 34197
-> ephemeral loopback RCON
-> authenticated RCON readiness
-> host player's normal graphical client joins locally
-> publish Host endpoint
-> host-client session ends
-> /server-save
-> observe save refresh
-> end managed server
-> capture/commit
```

`AutomaticHostStop` is not advertised.

Remaining release evidence is real Internet reachability/Join, complete real Windows managed completion/capture, and cross-device handoff.

### Palworld

Current capability includes:

- AutomaticHostLaunch;
- AutomaticHostStop;
- ExactGameVersion.

Its proven managed Host lifecycle uses the dedicated server, disposable runtime configuration, localhost REST save/shutdown control, and complete Palworld process-tree exit before canonical runtime inputs are restored and capture begins.

`WorldOption.sav` remains canonical read-only input.

`AutomaticClientJoin` is not advertised. `IManualDirectConnectProvider` may present an already-ready native IP:port endpoint; that presentation does not create automatic Join or manual-session lifecycle ownership.

### 7 Days to Die

Current adapter provides its proven discovery/import/environment/state/mod/exact-version slice.

Automatic Host/Stop/Join remains frozen until the recorded current-V3 empirical trace establishes readiness, loopback `shutdown` framing, long-lived process exit, final authoritative save completion, and captured-state relaunch.

### Project Zomboid

Current adapter provides discovery/import/environment/state plus exact game/mod-version boundaries.

Managed runtime remains frozen until an isolated real dedicated-server lifecycle proves process ownership, safe shutdown, capture and relaunch without mutating the player's live Zomboid state tree.

### Other first-party adapters

The remaining catalog adapters deliberately expose narrower discovery/environment/state capabilities. They are not required to implement fake launch/Host/Join paths merely to appear in the Games Library.

## Adapter acceptance rule

An adapter must prove only the capabilities it advertises, but every advertised capability must be proven at its actual ownership boundary.

A state/import-only adapter proves:

1. installation/source identity without mutation;
2. intended World discovery;
3. smallest complete World-owned state vs player/account/recovery state;
4. exact supported environment boundary;
5. safe import capture;
6. controlled restore;
7. package/integrity invariants.

A writable launch capability additionally proves:

1. controlled preparation;
2. real session start/ownership;
3. safe session end/capture boundary;
4. capture/commit/replay;
5. failure/recovery preservation.

Hosted capability additionally proves authoritative server/client separation where relevant.

Automatic Join proves the actual client-launch path into a Ready Host.

Automatic Host Stop proves the user-triggered safe stop path.

Shared release claims additionally require the real cross-device/network handoff evidence recorded for that adapter.

## Test strategy

Deterministic coverage includes:

- lifecycle state/failure transitions;
- fake adapters/storage/coordinators;
- stale-head/generation rejection;
- crash/restart recovery;
- bounded retry/idempotency behavior;
- device-wide concurrency;
- adapter filesystem/package boundaries;
- process handoff/control simulations;
- Desktop capability/action guards.

Real-system validation covers what deterministic tests cannot truthfully prove:

- actual game/server process behavior;
- native readiness/save/shutdown boundaries;
- Internet Host/Join reachability;
- long sessions;
- real Windows lifecycle behavior;
- real cross-device handoff.

When a real test is required, record it in `DEFERRED_EMPIRICAL_TESTS.md`, freeze the dependent capability, and continue independent deterministic work.

## Working rule

> **Do not make Core smarter because one game is weird. Make the adapter own the weirdness, or remove the problem if the game/platform already owns it.**