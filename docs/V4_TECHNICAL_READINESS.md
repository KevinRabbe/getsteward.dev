# V4 — Technical Readiness

Status: **DETERMINISTIC V4 GOAL COMPLETE — SMALL, DERIVED, NO NEW AUTHORITY. V3-E/V3-F REMAIN THE ACTIVE RELEASE GATES.**

## Goal

> **Steward should explain what it already knows before asking the user to discover support by trial and error.**

V3 remains the active release-evidence stage. V4 does not supersede V3-E/V3-F and must not be used to hide missing real release evidence.

V4 was intentionally kept small. It turns existing technical truth into clearer product guidance without making Steward own another support system.

## Completed V4 shape

```text
current adapter capability truth
+ canonical engineering evidence map
-> small read-only selected-game technical summary
```

No new domain state, backend API, persistence schema, support taxonomy, polling service, or game-specific Desktop branching exists.

### V4-A — selected-game technical readiness summary — DONE

Qualified PR #160 adds one small read-only line beneath the selected-game heading.

It derives directly from `IGameAdapter.Capabilities` and says:

```text
World state/import supported
+ currently advertised managed actions
```

Examples:

```text
Factorio
World state/import supported • Adapter supports: Start World, Host World, Join, Create World
```

```text
Terraria
World state/import supported • No managed play actions yet
```

The summary deliberately does **not** try to duplicate World/device-specific readiness. Existing World details already own:

- exact environment verification;
- Start/Host enabled state and reasons;
- hosting-device preference;
- Join capability/readiness;
- recovery/responsibility state.

That separation avoids a second capability engine.

Exact qualified V4-A head:

> `c93e3606e7990bed1fb8cdec68575e706ac2f1ec`

All five top-level workflow groups passed on that exact head.

### V4-B — negative prerequisite explanation — NOT A REQUIRED SUBSYSTEM

The original V4 sketch considered a generic game-level explanation such as:

```text
Palworld Dedicated Server not installed
-> this device cannot Host through Steward's Palworld dedicated-server path
```

The implementation audit found an important boundary: `IGameAdapter` exposes capability truth and installation discovery, but it does not expose one universal structured model for every game-specific native prerequisite.

Creating a new cross-game prerequisite schema merely to enrich this summary would violate the V4 constraint.

Therefore V4 does **not** add:

- generic host-eligibility scoring;
- a machine-capability database;
- a prerequisite taxonomy;
- server-tool installers;
- SteamCMD ownership;
- another readiness cache.

When an existing adapter/environment/action result already has a concrete negative reason, Steward may present that reason at the surface that owns it. A future adapter may expose a stronger generic prerequisite only if real product evidence proves that the contract is genuinely universal.

A missing prerequisite is useful negative evidence, but it is not automatically a reason to create a new subsystem.

## Canonical technical evidence map — DONE

`GAME_TECHNICAL_READINESS.md` records the engineering evidence boundary for every registered first-party adapter.

The map was checked against the current adapter declarations after V4 planning:

- Factorio advertises Mods + Start + Host + automatic Join + ExactGameVersion + native Create; no Host Stop.
- Palworld advertises Host + Host Stop + ExactGameVersion; no automatic Join.
- 7 Days to Die advertises Mods + ExactGameVersion; runtime actions remain frozen.
- Project Zomboid advertises Mods + ExactGameVersion + ExactModVersions; runtime actions remain frozen.
- all fifteen remaining registered adapters advertise `ExactGameVersion` only while implementing concrete discovery/environment/capture/restore behavior.

That distinction is intentional:

```text
state/import/environment support
!=
runtime action support
```

When a game gains a deeper capability, update the technical evidence map in the same slice.

The map is documentation/evidence authority only. Product behavior still comes from executable adapter contracts.

## Manual-test reduction

V4 formalizes this rule:

```text
native/platform fact already establishes the boundary
-> encode/test it deterministically
-> do not ask a human to rediscover it

real process/network/game behavior remains unknown
-> record the exact empirical question
-> freeze only the dependent capability
-> test that boundary later
```

For the fifteen state-only adapters, no gameplay launch is required merely to re-prove the current advertised state/import/environment slice.

Real game execution becomes relevant when Steward wants to claim behavior that inherently depends on real execution, for example:

- process/bootstrap ownership;
- server readiness;
- safe native shutdown;
- final authoritative save completion;
- automatic client Join;
- Internet/NAT/firewall reachability;
- cross-device continuation.

## Deliberate non-goals

V4 does **not** add:

- a second release-support or maturity taxonomy;
- user-editable capability flags;
- another backend endpoint merely for capability descriptions;
- a support database that can drift from adapter code;
- game-name branching in Core or generic Desktop lifecycle logic;
- automatic dedicated-server/tool installation;
- speculative NAT traversal;
- telemetry merely to populate a readiness page;
- runtime tests for state-only adapters that do not claim runtime actions;
- broad per-game settings editors;
- a generic prerequisite/host-eligibility model.

## Evidence discipline

V4 follows the same rule as the adapters:

```text
known fact
-> explain it

unknown real boundary
-> label/freeze the dependent claim
-> do not guess
```

Examples:

- adapter does not advertise automatic Join: known;
- current exact environment mismatch: known when verification reports it;
- missing native tooling discovered by the owning adapter: useful negative evidence at that boundary;
- whether a friend's router passes the game's UDP traffic: not knowable until the real network boundary is exercised.

## Definition of done — SATISFIED

V4 is complete at its intentionally small boundary because:

1. the technical evidence map is canonical for the 19 registered adapters;
2. the selected-game workspace explains existing managed action depth without a second state model;
3. unsupported actions remain capability-driven and are never promoted by presentation logic;
4. the presentation contains no game-name branching and performs no duplicate environment/install probing;
5. no new generic platform/backend authority was introduced;
6. focused deterministic tests protect the projection boundary;
7. the implementation passed the full five-workflow qualification matrix.

Anything beyond this must earn its own product/evidence justification rather than being treated as unfinished V4 work.

## Priority after V4

V4 does not create a new active feature phase.

The active sequence remains:

```text
V3-E real Windows evidence
-> smallest evidence-driven correction if needed
-> V3-F real Steam/provider/game acceptance
-> measurement-driven changes only when real evidence justifies them
```

## Final rule

> **V4 should make Steward easier to understand, not make Steward own more things.**
