# Palworld Adapter

## Purpose

Palworld is one of Steward's initial commercial validation adapters.

It stresses a different path from Factorio:

- a normal Steam client installation;
- a separate dedicated-server installation;
- directory-based World state;
- server configuration selecting one World id;
- a dedicated server process that owns the writable session;
- player identity differences between local/co-op and dedicated-server play.

Those details remain adapter-owned. Core does not know Palworld process names, save paths, REST ports, or save serialization.

## Final runtime-input invariant

`WorldOption.sav` is canonical World state. Steward treats it as **read-only**.

Steward does not:

- patch `WorldOption.sav`;
- re-encode GVAS;
- compress Oodle Mermaid output;
- convert current `PlM` containers to legacy `PlZ`;
- delete World-owned settings to make management easier.

The proven managed-session lifecycle is:

```text
read canonical WorldOption.sav
-> read effective World settings
-> generate disposable PalWorldSettings.ini
-> override only Steward management fields
   - AdminPassword
   - RESTAPIEnabled
   - RESTAPIPort
-> park canonical WorldOption.sav
-> launch PalServer
-> authenticate only through localhost
-> verify active World and effective settings
-> run session
-> POST /save
-> POST /shutdown
-> wait for the complete Palworld process tree to exit
-> discard the disposable runtime INI, regardless of Palworld mutations
-> restore the untouched canonical WorldOption.sav
-> restore the original PalWorldSettings.ini byte-for-byte
-> verify hashes and transient-credential absence
-> allow capture
```

A canonical input is never restored underneath a still-running Palworld process.

## Empirical evidence

Real-machine acceptance on the current Windows Steam installation established:

- `WorldOption.sav` is startup input rather than continuously authoritative runtime configuration;
- restoring a byte-exact copy while Palworld is running does not reapply settings;
- Palworld does not mutate the restored `WorldOption.sav`;
- current `PlM/0x31` WorldOption data can be decoded read-only;
- 119 WorldOption settings were extracted structurally;
- the generated runtime INI serialized all 119 settings;
- 115 REST-observable World settings matched with zero mismatches and zero unexposed settings;
- `CrossplayPlatforms` and `DenyTechnologyList` are REST JSON arrays and compare semantically, not as INI text;
- Palworld may rewrite the disposable `PalWorldSettings.ini` during runtime;
- that rewrite is harmless because the disposable INI remains in place until the server process tree exits;
- `POST /save` succeeds;
- `POST /shutdown` succeeds;
- `PalServer.exe` and `PalServer-Win64-Shipping-Cmd.exe` both exit without forced cleanup;
- no unexpected `WorldOption.sav` is generated while the canonical file is parked;
- both original runtime-input files restore byte-for-byte after exit;
- the transient management credential is absent from restored files.

The authoritative capture boundary is therefore:

```text
POST /save
-> POST /shutdown
-> full Palworld process-tree exit
-> restore canonical runtime inputs
-> capture
```

## Discovery and environment

The adapter identifies:

- Palworld client installations;
- Palworld dedicated-server installations;
- local and dedicated World roots;
- native World ids;
- the dedicated-server Steam app manifest when Steam exposes it;
- the server configuration selecting the active World.

New imports capture the exact Palworld Dedicated Server Steam build ID from app `2394010` into `EnvironmentManifest.GameVersion`.

Shared writable play verifies:

- environment schema and adapter identity;
- dedicated-server hosting mode;
- safe native World id;
- dedicated-server root and executable;
- an exact required build ID;
- the installed dedicated-server build matches;
- PalServer has initialized its Windows configuration;
- `DedicatedServerName` exists.

A legacy environment with version `unknown` is blocked for shared writable play. Steward does not guess compatibility or silently update PalServer.

## Preparation and restore

The adapter prepares the dedicated-server World location and selects the required native World id.

Canonical restore uses staging and validation:

```text
open canonical package
-> stage World
-> validate required contents
-> replace prepared World safely
-> verify restored files against package bytes
-> reject unexpected files
```

The originally imported source remains outside normal managed-session mutation.

## Session ownership

The writable session belongs to the dedicated server, not to one graphical client.

The empirical process tree is:

```text
PalServer.exe
└─ PalServer-Win64-Shipping-Cmd.exe
```

The Shipping process owns the REST listener. Steward waits for the complete Palworld process tree, not merely the launcher PID.

Core exposes only the game-agnostic managed-host lifecycle. The Palworld adapter owns readiness, save, shutdown, process observation, runtime-input restoration, and abnormal-exit recovery.

An unexpected external/crash exit still restores user-owned runtime inputs after Palworld is gone, but it does not create a normal automatic commit. The workspace remains recovery-pending.

## REST management

The adapter uses only the minimal Palworld REST surface needed for lifecycle control:

```text
GET  /v1/api/info
GET  /v1/api/settings
POST /v1/api/save
POST /v1/api/shutdown
```

Authentication is HTTP Basic with a random transient admin credential. Steward connects to `127.0.0.1` only and never logs the secret.

The real server has been observed binding the REST listener to `0.0.0.0`. Palworld currently exposes a REST port but no documented REST bind-address setting. Network isolation therefore remains a separate deferred acceptance/security item; it is not a reason to alter the proven World lifecycle.

## Current PlM decoder boundary

Current `PlM` input needs Oodle Mermaid **decompression only**. Steward contains no Oodle compression API and no WorldOption writer.

Production lookup currently accepts only a usable Oodle runtime already installed beneath discovered Palworld client/server roots. It deliberately ignores the acceptance-only environment override and will not search unrelated games, download, copy, or redistribute Oodle.

A legitimate ordinary-user decode path remains a release gate unless it can be eliminated entirely.

A deferred acceptance experiment tests a stronger alternative: let Palworld itself materialize the effective World settings from an intact disposable copy of `WorldOption.sav`, then cache/use the game-written settings representation. If that proves equivalent, Steward can remove shipped PlM/Oodle decoding instead of solving the decoder-distribution problem.

No production dependency should be added until that choice is resolved.

## Capture and canonical commit

The adapter captures the authoritative dedicated-server World directory after the proven process-exit boundary.

Validated package behavior includes:

- required World state included;
- server backup subtree excluded from canonical payload;
- restore into a clean prepared location;
- byte-for-byte verification;
- unexpected restored files rejected;
- required save content such as `Level.sav` verified.

Canonical advancement remains transactional:

```text
previous canonical state
-> capture candidate
-> durable immutable storage
-> verify
-> atomic head advancement
```

Failure before head advancement leaves the previous canonical state authoritative.

## Player identity limitation

World migration and player identity migration are separate problems. Palworld may represent the same person differently between local/co-op play, dedicated-server play, Steam identity, and native player GUID files.

That edge case stays Palworld-specific. It must not expand Steward's universal World model.

## Deferred acceptance queue

Real-machine experiments are batched rather than allowed to block unrelated product work.

Current deferred items include:

1. prove the documented presentation-only manual Join path from a genuinely separate Internet connection: Steward publishes the Ready host address + native UDP `8211`, and the friend enters that exact endpoint in Palworld's own Join Multiplayer UI;
2. prove the Windows Firewall REST boundary from a genuinely external LAN peer;
3. run the Palworld-native settings-materialization experiment to determine whether shipped PlM decoding can be eliminated.

The exact setups/pass-fail evidence live in `DEFERRED_EMPIRICAL_TESTS.md`; `V2_PALWORLD_MANUAL_JOIN.md` preserves the deterministic manual-direct-connect composition.

None of these items justifies reopening WorldOption write/encode experiments or adding speculative NAT traversal before the measured network result exists.

## Non-goals

The Palworld adapter does not require Steward to provide:

- generic save merging;
- gameplay-semantic editing;
- permanent game-server hosting;
- conversion into another game's World format;
- a generic player-identity migration system.

Its job is to make the latest valid Palworld World state portable, temporarily hostable, safely capturable, and ready for the next session.
