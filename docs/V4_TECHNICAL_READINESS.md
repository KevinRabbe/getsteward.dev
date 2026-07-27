# V4 — Technical Readiness

Status: **FUTURE PRODUCT GOAL — SMALL, DERIVED, NO NEW AUTHORITY**

## Goal

> **Steward should explain what it already knows before asking the user to discover support by trial and error.**

V3 remains the active release-evidence stage. V4 does not supersede V3-E/V3-F and must not be used to hide missing real release evidence.

V4 is intentionally small. It turns existing technical truth into clearer product guidance.

## Why this adds value

The current platform already knows a surprising amount before a game is launched:

- whether the game installation exists;
- whether required dedicated-server tooling exists;
- exact game/build identity where supported;
- known mod/environment compatibility;
- whether a World is technically reproducible;
- which actions the adapter actually advertises;
- whether the current World/device/environment blocks an otherwise supported action;
- whether an unavailable action is simply outside the adapter's proven slice.

Without a clear projection of that information, a user can interpret a disabled action as something they should experiment with manually.

V4 should make the existing answer explicit instead.

## Product rule

```text
existing adapter capabilities
+ installation discovery
+ current environment verification
+ existing World/responsibility state
-> read-only technical readiness explanation
```

No new domain state is created.

## V4-A — Game technical readiness summary

Add one small read-only summary to the selected-game/workspace experience.

The summary should answer only useful questions such as:

```text
Installed                         Yes / No
Managed Worlds                    <existing count>
Import                            Available / limitation
Start World                       Available / unsupported / blocked reason
Host World                        Available / unsupported / missing server tool / blocked reason
Join                              Available when a Host is Ready / unsupported
Stop and Save                     Available / unsupported
Create World                      Available / unsupported
Environment                       Ready / exact mismatch / unsupported modded environment
```

The exact presentation can be smaller than this list. The implementation should reuse existing labels/results where possible rather than creating a dashboard framework.

### Important distinction

The UI must distinguish:

- **unsupported by the currently proven adapter slice**;
- **supported, but unavailable on this device/World right now**;
- **requires a real release/network proof before Steward may advertise it more broadly**.

It must not imply that an empirical release gate can be solved by clicking Retry.

## V4-B — Explain negative installation evidence

When an action requires native tooling that is not installed, that absence is already useful evidence.

Example:

```text
Palworld Dedicated Server not installed
-> this device cannot Host through Steward's Palworld dedicated-server path
```

Steward should say that directly when the existing adapter discovery can already prove it.

Do not add:

- generic host-eligibility scoring;
- background server-tool installers;
- automatic SteamCMD ownership;
- a new machine-capability database.

A missing prerequisite is an explanation, not a new subsystem.

## V4-C — Technical evidence map stays canonical

`GAME_TECHNICAL_READINESS.md` records the engineering evidence boundary for every registered first-party adapter.

When a game gains a deeper capability, update that map in the same slice.

The map is documentation/evidence authority only. Product behavior still comes from executable adapter contracts.

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
- broad per-game settings editors.

## Implementation constraint

The first implementation should be possible entirely as a projection over data Steward already owns.

If adding V4-A requires a new persistence schema, new authority state, a polling service, or a second capability model, the design is too large and should be reduced.

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

- exact missing dedicated-server installation: known;
- adapter does not advertise automatic Join: known;
- current exact environment mismatch: known when verification reports it;
- whether a friend's router passes the game's UDP traffic: not knowable until the real network boundary is exercised.

## Definition of done

V4 is complete when:

1. the technical evidence map is canonical for the registered adapter set;
2. the selected-game experience can explain useful action availability from existing truth without a second state model;
3. missing native prerequisites can be presented as concrete negative evidence where adapters already expose them;
4. unsupported actions remain capability-driven and are never promoted by presentation logic;
5. no new generic platform/backend authority is introduced;
6. documentation and deterministic tests prevent the readiness projection from drifting from the existing capability/environment owners.

## Priority

V4 is deliberately lower priority than completing V3 release evidence.

It is a good independent deterministic slice when real V3-E/V3-F work is blocked on external setup because it adds user/support value without changing World authority or game lifecycle behavior.

## Final rule

> **V4 should make Steward easier to understand, not make Steward own more things.**
