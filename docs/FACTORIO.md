# Factorio Adapter

Status: **CURRENT — reconciled against the active `IGameAdapter` implementation on the qualified documentation-audit line.**

## Purpose

Factorio is Steward's reference adapter for a comparatively complete automatic lifecycle.

It validates:

- Steam and standalone installation discovery;
- ZIP-based save discovery and import;
- isolated write-data and mod preparation;
- exact game/environment checks;
- separate local, authoritative hosted-server, and client launch paths;
- Steam bootstrap/process handoff observation;
- native World creation;
- clean state capture, commit, and replay.

All Factorio-specific details remain inside the adapter.

## Current executable capability boundary

The adapter currently advertises:

- `Mods`;
- `AutomaticLocalLaunch`;
- `AutomaticHostLaunch`;
- `AutomaticClientJoin`;
- `ExactGameVersion`;
- `NativeWorldCreation`.

It does **not** advertise `AutomaticHostStop`.

Catalog registration or the existence of additional Factorio helper code does not grant another capability.

A subtle implementation detail matters when reading the code: `FactorioAdapter` has older/general public launch methods, but `FactorioAdapter.Hosting.cs` explicitly implements `IGameAdapter.LaunchHostAsync` and `IGameAdapter.WaitForSessionEndAsync`. Core and Desktop use adapters through `IGameAdapter`, so that explicit implementation is the active hosted product path.

## Current hosted lifecycle

The active managed Host path is the authoritative dedicated-server/RCON path:

```text
discover installation / select World
-> inspect exact environment
-> prepare isolated workspace
-> restore canonical save
-> start private dedicated Factorio server
-> prove server readiness through authenticated loopback RCON
-> launch the host player's normal Factorio client against that server
-> publish managed Host endpoint material
-> observe the graphical host client session
-> request /server-save through RCON
-> prove the isolated save changed
-> end the managed dedicated-server process
-> capture newest valid non-autosave
-> durably store and verify candidate
-> advance canonical World head last
-> preserve recovery evidence on failure
```

The original imported source save remains outside Steward-managed session writes.

The deterministic implementation is not a substitute for the remaining real Internet/Windows release evidence. In particular, real reachability, actual game joining, long-lived behavior, and the final managed stop/capture boundary remain empirical release gates.

## Installation and user-data discovery

The adapter discovers Factorio through:

- Steam library metadata, including non-default libraries;
- the current user's Steam registry location on Windows;
- conventional standalone locations;
- user-selected paths where required.

It resolves the real Factorio user-data location, including supported `config-path.cfg` and `[path] write-data` behavior, without moving or rewriting the user's live profile.

## Save discovery and import

The adapter discovers top-level `*.zip` saves and hides normal `_autosave*` entries from import selection.

Import copies the selected save into an adapter-owned state package. It does not mutate the source save.

A newly imported World starts `LocalOnly` until sharing is explicitly established.

## Environment inspection

The adapter records the Factorio environment needed to reproduce the World, including the exact game version and the relevant enabled-mod/startup-settings state.

`EnvironmentManifest` is authoritative. Any fingerprint is only a comparison aid.

Preparation fails conservatively when the required game version, required mod artifact, or trusted startup-settings state cannot be reproduced.

## Isolated preparation

A prepared Factorio workspace uses its own configuration, mod directory, and write-data location:

```text
<workspace>/
  config/config.ini
  mods/
    mod-list.json
    mod-settings.dat
    <required mod artifacts>
  user-data/
    saves/
```

Factorio is launched with the workspace configuration and mod directory.

Redirecting `write-data` is essential. Loading a save from another path alone does not prove that later in-game saves stay outside the user's normal Factorio profile.

## Mod handling

The adapter currently:

1. reads required mod components from the environment manifest;
2. keeps built-in mods in the game installation;
3. finds required user-mod artifacts;
4. copies only those artifacts into the workspace;
5. generates a workspace-local `mod-list.json`;
6. verifies startup-settings state where a trusted fingerprint exists;
7. fails safely instead of mutating the live mod profile when requirements cannot be reproduced.

Factorio's native mod functionality should be used where it can be validated safely. Steward should not become a second universal mod manager.

Automatic acquisition of missing historical game/mod versions is not an assumed product capability.

## Local launch

Local play uses Factorio's single-player load path inside the isolated workspace.

It remains separate from hosted play.

## Hosted launch details

The active interface path starts a private dedicated/headless Factorio server from the isolated World with:

- game UDP port **34197**, Factorio's normal managed server port;
- one ephemeral loopback-only RCON TCP port;
- a fresh random per-session game password;
- a fresh random per-session RCON password;
- an adapter-generated private server settings document;
- public, LAN, and Steam listing disabled for the managed server;
- RCON used only on `127.0.0.1` for local lifecycle control.

Steward resolves possible Steam bootstrap/process replacement and does not treat a PID alone as readiness. The server is considered ready only after authenticated RCON communication succeeds.

Only then does Steward launch the host player's normal graphical Factorio client and connect it to:

```text
127.0.0.1:34197
+ the per-session game password
```

The hosted session exposes the game-owned managed endpoint as port `34197` plus that session password. The shared host-presence layer supplies the externally usable address separately; the adapter does not invent public-IP/NAT-traversal infrastructure.

When the graphical host client ends, the current implementation:

```text
verify dedicated server still exists
-> issue /server-save through authenticated loopback RCON
-> wait until the isolated save timestamp/length proves a refresh
-> terminate the managed dedicated-server process tree
-> promote only host-client preference changes that belong back in the prepared workspace
-> continue normal capture/commit
```

That internal completion path is **not** an advertised `AutomaticHostStop` capability. Steward currently exposes no generic user-triggered Factorio Stop-and-Save promise from that flag.

The real release gate must still prove that the complete Windows/game boundary produces a safe authoritative capture repeatedly. Do not upgrade the capability merely because the deterministic RCON/save implementation exists.

## Client joining

The adapter supports Factorio's native direct multiplayer connection arguments.

For managed shared hosting, deterministic evidence uses the Host endpoint published from the active reservation/session. Joining is read-only Steward behavior: the active host remains the only writable World owner.

Current real-network acceptance intentionally begins with Factorio's normal UDP `34197` path. Steward does not preemptively add public-IP lookup, UPnP, STUN, relay, or another traversal subsystem. If real two-network acceptance fails, add only the smallest mechanism justified by the observed failure.

## Session observation

Steam may terminate an initially launched Factorio process and start a replacement Factorio process.

The adapter therefore:

- records relevant Factorio processes before launch;
- observes the initial process;
- detects rapid bootstrap exit;
- follows a newly created replacement process where required;
- resolves the dedicated-server process separately for hosted play;
- fails with redacted startup diagnostics when no real session/server process can be proven.

Core must not assume that the first PID is the complete session.

For hosted play, the graphical client and authoritative dedicated server are separate responsibilities. Client exit triggers the adapter's managed save/server-completion sequence; it is not itself proof that the server already ended safely.

## Capture

After the adapter establishes its session-completion boundary, it captures the newest valid non-autosave ZIP from the isolated workspace.

Core then:

```text
store immutable StateRevision
-> verify durable result
-> advance current World head last
```

A failed observation, save, capture, store, or commit preserves the previous valid state and the prepared workspace when recovery may be possible.

## Verify and repair

Environment verification reuses the real preparation rules without launching Factorio or advancing the World.

Automatic repair remains conservative. Unsupported or unsafe repairs are reported rather than performed against the user's live installation or mod profile.

## Native World creation

Factorio is the reference implementation for Steward's native creation contract.

The adapter lets Factorio itself create the native save in an isolated prepared environment rather than synthesizing save bytes. The initial environment and captured save are persisted before the new canonical World head is published.

Creation does not create a permanent server object and does not imply Host capability beyond the separately advertised adapter flags.

## Remaining empirical release gates

Before Steward advertises the complete Factorio shared-play experience as release-proven, real acceptance still needs to establish at least:

1. correct installation/user-data discovery on the release machines;
2. exact environment reproduction or clear fail-closed mismatch;
3. isolated local play and native creation where advertised;
4. dedicated-server startup and RCON readiness on the real Windows/Steam path;
5. real Host endpoint reachability over Factorio UDP `34197` from another Internet connection;
6. automatic Join into that exact managed server;
7. correct Steam/bootstrap process ownership;
8. authoritative `/server-save` followed by an observed save refresh;
9. a safe final server-end/capture boundary on the real machine;
10. immutable upload/verification/expected-head commit;
11. recovery after interruption;
12. cross-device latest-state handoff and return.

Capability promotion remains evidence-driven. A passing unit/integration test is not evidence for a real network/game boundary it cannot observe.

## Non-goals

The Factorio adapter does not require Steward to provide:

- generic save merging;
- branch or Fork workflows;
- permanent hosted servers;
- a second social or party platform;
- ownership governance;
- universal mod management;
- speculative NAT traversal.

Its job is narrower: reproduce, launch, observe, capture, and hand off the latest valid Factorio World state through Factorio's own primitives.