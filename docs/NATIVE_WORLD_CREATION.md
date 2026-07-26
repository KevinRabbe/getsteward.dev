# Native World Creation

Status: **V2 ACTIVE PRODUCT DIRECTION**

Steward should not require a player to manually create a save in the game before Steward can manage it when the game already exposes a safe native World-generation path.

The product surface is:

```text
Game
├── Create new World
└── Import existing World
```

There is no permanent `Server` product object.

A server process is only an adapter-owned temporary execution method used later by `Host World` where that game requires it.

## Product rule

> **When a game provides a safe native World-generation path, Steward invokes that native path. Steward does not synthesize or reverse-engineer native save bytes merely to create a new World.**

Creation is therefore:

```text
user chooses the few meaningful World settings
-> adapter prepares an isolated native environment
-> game / dedicated-server executable creates its own native World
-> adapter verifies the expected native output exists
-> adapter captures that output through the normal portable-state boundary
-> Steward persists EnvironmentRevision 1 + StateRevision 1
-> canonical World head is written last
-> World is Ready
```

The existing import path remains for:

- Worlds that already exist;
- native creation options Steward does not yet expose;
- game versions/platforms without a validated native generator;
- deliberately unsupported creation cases such as a setting the native server cannot accept safely.

## Architectural boundary

Core knows only:

- the adapter advertises `NativeWorldCreation`;
- the requested Steward display name;
- an opaque adapter-owned creation-settings dictionary;
- the initial exact `EnvironmentManifest` returned by the adapter;
- the initial portable `CapturedState` returned by the adapter.

Core must not know:

- Factorio presets or map-generator JSON;
- Palworld INI keys;
- 7 Days to Die XML properties;
- Valheim command-line modifiers;
- native save serialization;
- server executable names or Steam tool AppIDs.

Those remain adapter responsibilities.

The initial persistence boundary is identical in spirit to import:

1. allocate World/environment/state IDs;
2. ask the adapter to create and capture the native World;
3. validate the returned environment belongs to the selected adapter;
4. persist immutable EnvironmentRevision 1;
5. persist immutable StateRevision 1 + package bytes;
6. save the canonical World head last;
7. remove disposable captured package bytes.

If native generation, capture, or immutable persistence fails, no canonical World may point at an incomplete initial state.

## Settings policy

Steward should expose only decisions the player actually cares about.

### World/gameplay settings

Examples:

- World name;
- seed where the native game supports specifying one;
- native preset/difficulty;
- resources;
- enemy/raid settings;
- death penalty;
- day length;
- other game-specific gameplay modifiers.

### Hosting preferences

Expose only when they are meaningful product decisions:

- private/public where applicable;
- crossplay where applicable;
- maximum players where the group needs to choose it.

### Adapter-managed runtime settings

Do not ask the normal player to configure:

- save paths;
- config-file locations;
- bind addresses;
- management/RCON/REST ports;
- management passwords;
- temporary server IDs;
- process arguments;
- disposable workspace paths.

Steward generates or chooses these internally where the adapter can do so safely.

## V2 game status

### Factorio — implementation reference

Current Factorio supports native map creation through `factorio --create <save>`.

Native parameters also support map-generation presets and a map seed, plus map-generation/map-settings JSON when deeper customization is needed.

V2 first slice:

- `Create new World` uses the exact installed Factorio environment;
- Steward reproduces the current exact mod environment into its existing isolated Factorio workspace;
- Factorio itself creates `world.zip` through `--create`;
- optional adapter settings are currently `preset` and `seed`;
- Steward captures the resulting native ZIP through the existing state-capture path;
- the disposable generation workspace is removed after capture;
- no Factorio save parser/writer is introduced.

Official evidence:

- https://wiki.factorio.com/Command_line_parameters
- https://wiki.factorio.com/Multiplayer

Future Factorio UI may expose a small curated subset of map-generation settings. Do not expose arbitrary file paths to `map-gen-settings.json` or `map-settings.json` as a normal-user feature.

### Palworld — native server generation, empirical qualification required

Pocketpair documents `PalWorldSettings.ini` as the server/game-balance configuration surface and states that the server-created configuration directories appear after PalServer has been started.

Intended Steward path:

```text
Steward chooses user-visible Palworld settings
-> adapter materializes PalWorldSettings.ini in an isolated server workspace
-> native PalServer starts against a fresh World identity
-> PalServer creates/materializes its native World
-> Steward proves effective settings through the existing server/runtime evidence where possible
-> safe save/shutdown
-> capture native World
```

Important constraint:

> Do not reintroduce a `WorldOption.sav` writer or native save re-encoding merely to create a World.

The remaining gate is empirical: prove the exact current Palworld version's fresh-World materialization and settings authority using PalServer itself.

Official evidence:

- https://docs.palworldgame.com/settings-and-operation/configuration/
- https://docs.palworldgame.com/settings-and-operation/arguments/

### 7 Days to Die — native dedicated-server generation

The native server configuration supports:

- `GameWorld=RWG`;
- `WorldGenSeed`;
- `WorldGenSize`;
- `GameName`;
- gameplay/difficulty properties;
- `UserDataFolder` / `SaveGameFolder` overrides for isolation.

The intended Steward path is therefore to create an adapter-owned server configuration, let the native dedicated server generate the World, stop it safely after the required initial state exists, then capture the native generated World/save.

This work should be combined with the existing 7DTD host-depth evidence rather than building an unrelated generator pipeline.

Official evidence:

- https://7daystodie.wiki.gg/wiki/Server:serverconfig.xml

### Valheim — native server generation

The official dedicated-server interface states that `-world <name>` creates the named World when it does not already exist.

The same native server interface supports a World modifier preset, individual modifiers, several modifier keys, crossplay, visibility, save location, and other hosting options.

Intended Steward path:

```text
fresh isolated save directory
-> valheim_server -world <native-id> ...
-> native server creates World
-> observe successful server readiness/native save materialization
-> safe stop
-> capture current native World files
```

Do not advertise an arbitrary custom seed unless the current native supported interface provides a validated way to supply one. Import remains the fallback for a pre-created seeded World.

Official evidence:

- https://valheim.com/support/a-guide-to-dedicated-servers/

Valheim 1.0 is scheduled for September 9, 2026. Re-check the final 1.0 save/server behavior before qualifying the V2 adapter rather than freezing assumptions from the pre-1.0 build.

## Relationship to Host World

Creation does not mean a permanent server starts existing.

```text
Create new World
-> native generator runs only as long as creation requires
-> Steward captures revision 1
-> generator/server process ends
-> World = Ready
```

Later:

```text
Host World
-> acquire one writable Steward reservation
-> prepare current World revision
-> start adapter-owned temporary multiplayer host/server
-> friends Join
-> safe stop
-> capture updated state
-> commit next revision
-> release writable authority
```

Creation and hosting may use the same game executable or dedicated-server tool internally, but they are different Steward lifecycle operations.

## Steam/tool installation rule

Steward should not implement its own game/server distribution system when Steam already owns installation.

Where a native dedicated-server tool is required:

```text
required Steam game/tool already installed
-> use it

required Steam game/tool missing
-> report the missing native requirement truthfully
-> later use the Steam-owned installation path where automation is proven
```

Negative installation evidence remains valuable: when a required server tool is absent, Steward can immediately eliminate that host/creation path instead of probing impossible runtime states.

## V2 acceptance

Native World creation is qualified per adapter only after proving:

1. the adapter advertises creation only when the native path is implemented;
2. unsupported settings fail before launching/mutating native state;
3. creation runs in adapter-owned/isolation-safe paths where the game permits it;
4. the game itself creates the native World bytes;
5. the expected current native state is verified before capture;
6. capture produces the same portable state format used by imported/continued Worlds;
7. initial immutable revisions persist before the canonical World head;
8. creation failure never publishes a broken World;
9. temporary native generation state is safely disposable after capture;
10. the created World can subsequently pass the ordinary Start/Host/Join lifecycle.

The user-level success criterion is intentionally simple:

> **Open Steward -> Create new World -> choose the settings that matter -> Ready.**
