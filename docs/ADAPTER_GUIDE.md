# Game Adapter Guide

Status: **CURRENT — stable adapter construction rules; changing per-game capability status lives in `PLATFORM_IMPLEMENTATION_STATUS.md`.**

## Purpose

A game adapter isolates everything specific to one game while Core runs the same safe World lifecycle.

> **Core knows what must happen. The adapter knows how this game makes it happen.**

The current Desktop catalog contains 19 first-party adapters. Their proven capabilities intentionally differ. Catalog membership means Steward can represent that adapter's proven slice; it does not mean every game can Start, Host, Join, Stop, or Create.

Do not duplicate the full changing capability matrix here. Use `PLATFORM_IMPLEMENTATION_STATUS.md` for current per-game claims.

## Core rule

A new game must not make Core, backend, Infrastructure, or normal Desktop behavior branch on the game name.

Game-specific complexity belongs behind adapter contracts unless evidence proves a missing rule is genuinely universal.

## Primary adapter contract

`IGameAdapter` owns game-specific behavior including:

- stable adapter ID and display name;
- capability flags;
- installation discovery;
- existing-World discovery;
- environment inspection;
- environment verification/repair where supported;
- import capture;
- environment preparation;
- state restore/capture;
- local launch where supported;
- managed Host launch where supported;
- automatic client Join where supported;
- managed Host stop where supported;
- session-end observation;
- workspace finalization;
- native World creation where supported.

Core owns ordering, one-writer semantics, revision authority, recovery, and durable commit.

## Capability truth

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

Capabilities describe **proven product behavior**, not useful code that happens to exist inside an adapter.

Action mapping is direct:

```text
Start World -> AutomaticLocalLaunch
Host World  -> AutomaticHostLaunch
Join        -> AutomaticClientJoin + current JoinCapabilityResult
Stop/Save   -> AutomaticHostStop
Create      -> NativeWorldCreation
```

An adapter that supports only discovery/import/environment/state can still be a valid product adapter. Missing runtime evidence is represented by absent flags, not fake implementations.

## Optional supporting contracts

Some game-owned behavior does not belong in the base capability enum.

### Managed Host endpoint

A Host-capable adapter may expose the game-owned endpoint material that an already-running managed Host actually uses.

That material can feed short-lived shared Host presence, but Host presence remains non-authoritative and tied to the exact writable reservation generation.

Do not expose management/RCON/REST secrets merely because they exist internally.

### Manual direct-connect presentation

`IManualDirectConnectProvider` is deliberately narrower than automatic Join.

It may format an already-ready `HostConnection` into native endpoint/instructions for a game whose own UI accepts direct connection but for which Steward does not own a validated client-launch lifecycle.

It does **not**:

- prepare a manual client workspace;
- launch the game;
- observe the manual client session;
- own cleanup;
- grant `AutomaticClientJoin`.

Palworld currently uses this presentation-only distinction.

The removed generic guided-manual Join lifecycle must not be recreated through this interface.

## Installation discovery

The adapter discovers supported installations from game/platform-native evidence such as:

- Steam libraries/app manifests;
- launcher metadata;
- registry values;
- conventional locations;
- portable/user-selected paths where genuinely required.

Prefer authoritative launcher/store metadata over duplicated hard-coded assumptions when the platform already owns installation identity.

### Negative installation evidence

Absence can be enough.

If a capability requires a dedicated-server installation and that tool is not installed, Steward already knows that device cannot execute that Host/creation path.

Do not build a generic “host eligibility” subsystem to rediscover an impossible runtime state.

## World discovery

Discovery is read-only.

The adapter must determine:

- which native object(s) form one current World;
- which adjacent state is player/account/configuration data instead;
- which backups/recovery generations are not current state;
- whether native selector/index metadata identifies the authoritative generation;
- whether journal/transaction sidecars make an otherwise copyable artifact currently unsafe;
- which storefront/account namespace belongs to the discovered installation;
- how duplicate native sources are collapsed.

Physical proximity is not ownership.

A save directory can contain World state, player state, maps, configuration, backups and transaction data together. Steward captures only the smallest complete **World-owned current state** proven by the game-specific evidence.

## Prefer opaque native state

Do not parse or re-encode native save formats merely because Steward can.

If the game already exposes a complete portable current artifact, preserve it byte-for-byte.

Examples of the principle used across current adapters include:

- native World files preserved as opaque bytes;
- native World directories archived without interpreting individual save bodies;
- current save generation selected through small bounded native selector metadata;
- SQLite-backed state accepted only while native journal/WAL sidecars prove the database is idle enough for the supported copy boundary.

A parser is justified only when interpretation is necessary to identify or safely reproduce the supported state boundary.

> **The smallest parser is often the safest parser. No parser is safer when opaque copying is enough.**

## Player/account state is not automatically World state

A profile namespace can contain both World-owned and person-owned persistence.

The adapter must intentionally classify and exclude data such as:

- character inventory/progression that belongs to one player;
- account metadata;
- exploration/map state that is player-owned;
- launcher/configuration state;
- native recovery history;
- host-machine infrastructure settings.

Moving a World must not silently move unrelated identity/persona state merely because the game stores it nearby.

## Environment inspection

The adapter returns the minimum exact `EnvironmentManifest` required by its proven capability.

Possible facts include:

- exact game/server build;
- enabled mods;
- exact mod/content versions;
- relevant startup/gameplay configuration;
- hosting mode/native tool requirement;
- opaque game-specific reproduction inputs.

Core stores the manifest but does not interpret game semantics.

### Refuse rather than fake exactness

An adapter must not silently omit a known environment input and still call the World exactly reproducible.

A narrower vanilla-only adapter can fail closed when it detects a known mod-loader/activation surface instead of building a universal mod manager.

Doing less is correct when the supported slice is truthful.

## Import capture

Import must:

```text
read discovered source
-> capture smallest complete World-owned current state into adapter-controlled package
-> validate package
-> leave source untouched
```

Native backups/recovery history are excluded unless evidence says they are required for the playable current state.

If the source changes across a validated capture race boundary, discard the temporary package rather than publishing mixed state.

## Environment preparation

The adapter creates/selects a controlled playable environment matching the required manifest.

Possible strategies are game-specific:

- isolated write-data/mod directories;
- workspace-local configuration;
- game-native server/user-data overrides;
- controlled file swapping where unavoidable and proven recoverable;
- separate dedicated-server installation.

User-owned live profiles should not become Steward workspaces by convenience.

Preparation must validate path ownership before destructive cleanup.

## Restore

Restore materializes the opaque package into the controlled prepared environment.

Rules:

- archive/package members are validated before writing;
- destination remains inside adapter-owned paths;
- unsafe links/reparse paths are rejected where relevant;
- required files/selector relationships are checked;
- exact restored bytes/state are verified where the adapter contract requires it;
- unexpected files cannot silently broaden the canonical World.

## Native World creation

When `NativeWorldCreation` is advertised, the adapter invokes the game's supported native creation path and captures the result through the same portable-state boundary used by imported Worlds.

The adapter owns game-specific creation settings. Core sees only an opaque settings dictionary plus returned exact environment/captured state.

Steward does not synthesize native save bytes merely to add a Create button.

See `NATIVE_WORLD_CREATION.md`.

## Local launch

`AutomaticLocalLaunch` means the adapter owns and has proven the normal Steward-managed local session lifecycle.

Local launch must not accidentally expose a multiplayer Host merely because the game supports multiplayer elsewhere.

## Managed Host

`AutomaticHostLaunch` means the adapter owns the temporary Host execution method required by that game.

The adapter must prove what actually owns writable state:

- listen-host game process;
- dedicated server;
- launcher-replaced process;
- server process tree;
- another game-specific execution shape.

A client process ending is not automatically proof that a dedicated server ended.

Host readiness must be evidence, not “process exists.”

## Automatic Join

`AutomaticClientJoin` means Steward can prepare the required read-only client environment and launch the game into the already-validated Ready Host without obtaining writable World authority.

Join capability may still be blocked by the current environment or identity condition.

No game-name branching is needed in Core/UI to interpret the result.

## Managed Host stop

`AutomaticHostStop` is a separate capability from Host launch.

An adapter may Host successfully but still lack a validated user-triggered Stop-and-Save path.

Advertising Host Stop means the adapter can request and prove a safe authoritative end before capture. Core does not equate it with killing a process and sleeping.

## Session observation

Adapters own the evidence for real session lifetime.

They must handle realities such as:

- bootstrap/launcher process exits before the actual game;
- process replacement;
- separate graphical client and authoritative server;
- server outliving a client;
- native save/shutdown commands;
- file/native state changing after a superficial process event.

A PID is an evidence input, not a universal session definition.

## Capture

After the adapter-established safe boundary:

```text
select current authoritative native state
-> copy/archive into controlled package
-> validate required package/state invariants
-> return CapturedState
```

Core then owns immutable storage/publication, expected-head commit and recovery semantics.

The adapter must report/obey disposable package/workspace ownership correctly.

## Workspace finalization

Core supplies `PreparedWorldDisposition`:

- `Discard` — controlled workspace no longer needed;
- `PreserveForRecovery` — potentially recoverable post-launch state must remain.

Rules:

- pre-launch temporary work may be cleaned when safe;
- successful completion may clean controlled workspace;
- uncertain post-launch state remains recovery material;
- recursive deletion requires validated adapter ownership;
- source saves/player profiles are never cleanup targets merely because a path resembles a workspace.

## What an adapter must never decide

An adapter does not redefine:

- one-active-writer semantics;
- canonical-head advancement ordering;
- revision immutability;
- shared access policy;
- distributed reservation generation semantics;
- generic recovery policy;
- product lifecycle terms;
- branch/Fork/merge behavior;
- social/ownership governance.

Those are Core/backend contracts or outside Steward.

## Adding a new game

Build the smallest truthful vertical slice first:

1. independent adapter project + stable ID/display name;
2. installation identity/discovery;
3. read-only World discovery;
4. classify World vs player/account/config/recovery state;
5. identify the smallest complete current native representation;
6. handle native selector/journal activity only as much as required for safe capture;
7. safe import capture;
8. environment inspection;
9. controlled restore;
10. environment preparation where required by that slice;
11. advertise only proven capabilities;
12. deterministic adapter tests;
13. dedicated CI lane where appropriate;
14. add one Desktop catalog entry;
15. qualify adapter head independently;
16. rerun the full five-workflow matrix on the exact integrated SHA.

Then add deeper capabilities independently:

```text
local launch
-> real session observation/capture proof

Host
-> authoritative Host ownership/readiness/capture proof

Host Stop
-> explicit safe user-triggered stop proof

automatic Join
-> exact read-only client preparation/launch proof

native creation
-> game-owned generator/capture proof
```

Do not make initial adapter acceptance wait for runtime features the game slice does not yet claim.

## Current catalog

The current 19 first-party adapters are:

- Factorio;
- Palworld;
- 7 Days to Die;
- Project Zomboid;
- Terraria;
- Stardew Valley;
- Necesse;
- Core Keeper;
- The Planet Crafter;
- Satisfactory;
- ASTRONEER;
- Enshrouded;
- Conan Exiles Enhanced;
- Raft;
- ICARUS;
- Smalland;
- Abiotic Factor;
- V Rising;
- Space Engineers.

The first four have deeper runtime/release-specific evidence work. The later fifteen are concrete examples of useful narrower state/import/environment slices that do not invent launch capabilities.

For the exact current capability matrix, use `PLATFORM_IMPLEMENTATION_STATUS.md` rather than copying it into this guide.

## Acceptance principle

Every exposed capability must have evidence at its actual trust/ownership boundary.

A discovery/import/state slice proves its storage/environment boundary.

A launch-capable slice additionally proves real session ownership and safe capture/replay.

A Host slice additionally proves Host readiness/authoritative server behavior.

A Host Stop slice proves explicit safe managed shutdown.

An automatic Join slice proves actual client launch into Ready Host without writable authority.

Shared release claims additionally require the relevant real network/two-device/game evidence.

## Final rule

> **Do not implement the game in Steward. Implement only the smallest adapter boundary Steward needs to move that game's latest valid World safely.**