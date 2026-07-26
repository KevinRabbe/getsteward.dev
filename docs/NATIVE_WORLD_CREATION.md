# Native World Creation

Status: **CURRENT PRODUCT CONTRACT — FACTORIO IS THE CURRENT `NativeWorldCreation` REFERENCE IMPLEMENTATION; OTHER GAMES REMAIN UNADVERTISED UNTIL PROVEN.**

## Product rule

Steward should not require the player to pre-create a save manually when the game already exposes a safe native World-generation primitive that the adapter can own and prove.

The product surface is:

```text
Game
├── Create new World    (only when NativeWorldCreation is advertised)
└── Import existing World
```

There is no permanent `Server` product object.

> **When a safe native generator exists, Steward invokes that generator. Steward does not synthesize or reverse-engineer save bytes merely to implement Create.**

## Generic creation contract

`NativeWorldCreation` is opt-in.

Core knows only:

- selected adapter/installation;
- requested Steward display name;
- opaque adapter-owned creation settings;
- returned exact `EnvironmentManifest`;
- returned portable `CapturedState`.

Core does **not** know game-specific:

- presets/seeds;
- INI/XML keys;
- executable arguments;
- dedicated-server tool IDs;
- native save serialization.

Those belong to the adapter.

## Persistence ordering

Creation follows the same “head last” safety rule as import:

```text
allocate World/environment/state identities
-> adapter invokes native generator in controlled environment
-> adapter validates native output
-> adapter captures initial portable state
-> persist immutable EnvironmentRevision 1
-> persist immutable StateRevision 1 + package
-> save canonical World head last
-> remove disposable generation/capture resources
-> Ready
```

If generation, capture, validation, or immutable persistence fails, no canonical World may point at incomplete initial state.

## Creation is not a playable session

Native generation is a bounded creation operation, not a writable gameplay session.

It does not create:

- a long-lived Steward reservation merely because a server executable was used internally;
- gameplay recovery responsibility;
- a permanent hosted server;
- Host presence;
- another World history model.

The adapter owns cleanup of its disposable generation workspace once the captured initial state is safely returned/persisted.

Later Start/Host uses the ordinary World lifecycle independently.

## Settings policy

Expose only settings the user needs to make a meaningful creation decision.

Potential user-visible settings are game-specific, such as:

- World name;
- native preset/difficulty;
- seed where the native generator safely accepts one;
- a small curated set of gameplay-generation options.

Do not expose adapter/runtime infrastructure as normal creation settings:

- filesystem paths;
- config paths;
- management/RCON/REST ports;
- management credentials;
- bind addresses;
- temporary server IDs;
- process arguments;
- workspace paths.

An unsupported setting must fail before Steward launches/mutates native generation state. Do not silently ignore it.

## Current executable implementation — Factorio

Factorio currently advertises `NativeWorldCreation` and is the reference implementation.

The adapter does:

```text
inspect exact installed Factorio environment
-> prepare existing isolated Factorio config/mod workspace
-> invoke the installed Factorio executable with native --create
-> optionally pass supported adapter-owned preset/seed values
-> require successful process exit
-> require expected non-empty regular save output
-> capture through normal Factorio state-capture path
-> return exact environment + CapturedState
-> delete disposable creation workspace
```

The game itself creates the save bytes.

Current internal Factorio creation settings are deliberately bounded:

```text
preset
seed
```

Unknown settings are rejected.

`seed` must be an unsigned 32-bit integer. Preset input is bounded/validated before use.

The generated save must:

- exist at the expected adapter-owned destination;
- be non-empty;
- remain inside the owned workspace;
- not be a linked/reparse file.

Cancellation attempts to terminate the native generation process tree and then preserves the creation operation's cleanup semantics.

### Current Desktop exposure

The current first user-visible Create flow intentionally asks for only the installed game and World name. Factorio's internal optional preset/seed support does not require the Desktop to expose those fields yet.

That is deliberate scope control:

> **Prove Create -> native World -> revision 1 before turning creation into a full game-settings editor.**

## Factorio empirical boundary

Deterministic code/tests prove the native creation contract and Factorio implementation shape.

Real Windows acceptance must still prove the complete installed-game product path:

```text
Create new World
-> actual Factorio native ZIP appears
-> Steward persists revision 1
-> resulting World behaves like an imported World
-> ordinary Start/Host lifecycle can use it
```

Do not expand creation settings merely because more Factorio CLI options exist.

## Other games are evidence-gated candidates, not current promises

Palworld, 7 Days to Die, Valheim, or another adapter may eventually obtain `NativeWorldCreation` if the game exposes a safe native generator and the complete generation/capture boundary is proven.

Until then they remain **unadvertised** for Create.

### Palworld

A plausible native path may use PalServer to materialize a fresh World from controlled server settings.

However:

- do not reintroduce a `WorldOption.sav` writer/re-encoder;
- the game should create its native state;
- settings authority/fresh-World materialization must be proven empirically;
- no capability flag is granted before that proof.

### 7 Days to Die

A future path may reuse the native dedicated-server World generation inputs in an isolated workspace.

That work should share evidence with the existing 7DTD managed-server lifecycle rather than invent a separate generator/control subsystem.

The exact opaque World-specific `SandboxCode` rule remains independent and must not be guessed/decoded merely for creation.

### Valheim

Valheim creation remains frozen until the released 1.0 save/server behavior is rechecked. Do not qualify creation from pre-release assumptions.

## Dedicated-server installation rule

If native creation requires a game/server tool that is not installed, that absence is definitive negative evidence for the current device's creation path.

```text
required native tool installed
-> adapter may attempt proven creation

required native tool missing
-> Create unavailable / Action required as appropriate
```

Steward should use Steam/game-owned installation primitives when automation is eventually justified. It should not become another game/server package distributor.

## Acceptance contract for any new creation adapter

Before advertising `NativeWorldCreation`, prove:

1. the adapter exposes the flag only for an implemented path;
2. unsupported settings fail before native mutation/launch;
3. generation runs only in adapter-owned/isolation-safe locations;
4. the game/tool itself creates native World bytes;
5. expected native output is identified/validated;
6. capture uses the same portable state contract as imported/continued Worlds;
7. exact environment is returned;
8. EnvironmentRevision 1 and StateRevision 1 persist before canonical World head;
9. failure never publishes an incomplete World;
10. disposable generation state is cleaned only under adapter ownership;
11. the created result can enter the ordinary supported World lifecycle.

A game does not need Create merely because it is present in the catalog.

## User-level success criterion

> **Open Steward -> choose a game that actually supports Create -> Create new World -> Ready.**

Everything below that sentence is implementation safety, not additional user complexity.