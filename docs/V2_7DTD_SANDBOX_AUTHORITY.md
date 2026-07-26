# V2 7 Days to Die V3 sandbox authority

Status: **DETERMINISTIC DECISION — EXPLICIT SANDBOX INPUT REQUIRED BEFORE HOSTING**

Rechecked: 2026-07-26

## Decision

Steward must not treat an imported 7 Days to Die V3 World bundle as sufficient authority for the World's dedicated-server sandbox gameplay configuration.

For future Steward-managed dedicated hosting, the portable product input is:

```text
7DTD World state
+ exact opaque V3 SandboxCode
= reproducible dedicated-server gameplay configuration
```

The `SandboxCode` is World-specific product configuration. It is not a machine-wide default, not a value Steward should infer from an unrelated local dedicated-server config, and not something Steward should decode or regenerate.

This decision supersedes the earlier deferred question recorded after #74/#76 that asked whether the captured `Saves/...` + `GeneratedWorlds/...` bundle might itself be enough to recover the dedicated-server sandbox settings.

## Why the old empirical question is no longer useful

V3.0 changed the dedicated-server configuration contract.

The official V3.0 release notes tell server administrators that legacy gameplay properties moved into a new `SandboxCode` XML property. For an existing V2.6 save, administrators are explicitly instructed to recreate the intended settings in the in-game Sandbox Options UI, copy the generated code, and paste it into the updated V3.0 `serverconfig.xml`.

A Fun Pimps developer separately confirmed that the generated sandbox code is placed in `serverconfig.xml` and that the shipped config already contains the property.

That establishes the supported dedicated-server authority boundary strongly enough for Steward's architecture:

```text
save bytes alone
!= supported dedicated-server sandbox configuration source

server launch configuration
-> SandboxCode
```

We therefore do not need a real-machine experiment whose purpose is to discover whether Steward can avoid capturing/providing a configuration input that the game's supported dedicated-server flow explicitly requires.

## Existing Steward state

The current 7DTD portable state package intentionally captures only game-owned World state:

```text
Saves/<GameWorld>/<GameName>/...
GeneratedWorlds/<GameWorld>/...   (when present)
```

It does not capture a machine-local `serverconfig.xml`.

That remains correct.

A whole local `serverconfig.xml` is a bad portable World input because it mixes unrelated machine/server concerns such as identity, networking, passwords, slots, administration interfaces, storage paths, and other deployment-specific settings with the sandbox gameplay rules.

The existing managed server-config transformer is also directionally correct: it rewrites Steward-owned `GameWorld`, `GameName`, `UserDataFolder`, and `SaveGameFolder` coordinates while leaving an existing `SandboxCode` opaque and byte-level uninterpreted at the semantic layer.

What is missing is not another World-state parser. What is missing is a truthful product source for the exact World-specific `SandboxCode` during import/creation before 7DTD Host can be promoted.

## Product rule

### Import existing World

An imported V3 World is allowed to exist in Steward without a sandbox code because import/state portability is already useful independently of hosting.

But it is **not eligible for Steward-managed 7DTD dedicated hosting** until an exact `SandboxCode` has been associated with that World through an explicit 7DTD configuration-input path.

Do not silently use:

- the shipped/default dedicated-server `SandboxCode`;
- whichever `serverconfig.xml` happens to exist on the current PC;
- another World's config;
- a decoded/re-encoded approximation;
- guessed defaults;
- settings inferred from unrelated machine state.

Missing sandbox configuration is negative evidence:

```text
no exact SandboxCode
-> Steward does not know the intended dedicated-server rules
-> this World cannot truthfully enter automatic 7DTD Host yet
```

That is preferable to launching a technically valid server with silently wrong gameplay rules.

### Create new World

When Steward eventually exposes a validated native 7DTD creation path, the adapter may obtain the exact code from the native creation/settings flow or from an explicit user-selected sandbox configuration input.

The same rule applies afterward: persist the exact opaque code as the World's required 7DTD configuration input rather than deriving it later from machine-local state.

## Representation rule

Do not put the entire source `serverconfig.xml` into the canonical World-state ZIP merely to retain one gameplay configuration value.

Prefer the smallest durable game-specific configuration representation that preserves the exact opaque `SandboxCode` and can become an input to the managed server-config transformer.

The existing `EnvironmentManifest.Configuration` surface may be a candidate because the sandbox code is required reproduction configuration rather than mutable save bytes, but this document does **not** force that storage choice before the import/edit UI and revision semantics are audited together.

The invariant is more important than the container:

```text
exact code is versioned with the World requirement
-> code survives handoff
-> host device does not substitute its local code
-> managed serverconfig receives that exact code
```

## Security / privacy boundary

Treat `SandboxCode` as game configuration, not a Steward secret.

Still do not copy unrelated `serverconfig.xml` fields into portable metadata. In particular, server passwords, telnet/web-management credentials, admin tokens, and machine paths must remain outside the World-specific sandbox input.

## What remains empirical

The sandbox **authority/source** question is removed.

The following are still real-game lifecycle questions and remain deferred:

1. Can the current dedicated server launch completely inside the Steward-owned isolated user-data tree with the exact managed config?
2. What log/process signal is the smallest reliable readiness boundary?
3. What supported safe-stop path proves the final save is complete before capture?
4. After a real managed stop, does the existing canonical World bundle capture every authoritative save change needed for the next host?
5. What client Join path should Steward expose after Host/Stop are proven?

Those tests should use an explicitly supplied known non-default `SandboxCode`. They no longer need to withhold configuration and ask whether the save somehow reconstructs it.

## Promotion rule

Do not advertise 7DTD automatic Host/Stop merely because this authority question is resolved.

Promotion sequence is now:

```text
explicit World-specific SandboxCode input
-> managed serverconfig receives exact opaque code
-> real isolated launch/readiness/safe-stop proof
-> capture/restore proof
-> only then promote Host/Stop
```

Join remains a separate capability proof.

## Explicitly not doing

- no SandboxCode decoder;
- no SandboxCode encoder;
- no guessed/default sandbox rules for imported Worlds;
- no whole-machine `serverconfig.xml` capture;
- no destructive change to the canonical World save bundle;
- no Host capability promotion from documentation alone;
- no empirical test whose only question is whether supported dedicated hosting can omit the game's documented sandbox configuration input.

## Evidence rechecked 2026-07-26

Primary/current sources:

- 7 Days to Die Help Center — `V3.0 Dead Hot Summer Release Note` (June 2026): server-admin notes state that legacy gameplay options moved to `SandboxCode`; continuing V2.6 saves require recreating intended settings, copying the code, and placing it in the updated V3.0 `serverconfig.xml`.
- The Fun Pimps developer forum, Fun Pimps staff response (June 11, 2026): generated sandbox code can be put into `serverconfig.xml`, whose shipped V3 config already contains the `SandboxCode` property.

Current supporting documentation also describes V3 dedicated-server gameplay settings as being supplied through that code rather than the removed legacy properties.
