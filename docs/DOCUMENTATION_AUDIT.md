# Documentation State Audit

Status: **PEER PRODUCT RECONCILED — AUTHORITATIVE ENTRY DOCUMENTS NOW MATCH THE CURRENT APPID-ONLY PEER ARCHITECTURE.**

## Current executable baseline

Current peer closed-beta release candidate:

`9acf25f7696dfe9056028e4201ca380a94883f0e`

Release candidate branch:

`release/steward-peer-closed-beta-rc1`

Automated exact-head qualification is green for the RC. Physical two-PC Steam/game qualification remains pending in issue #349.

Repository trunk/default-branch cleanup remains intentionally deferred to issue #350 until physical qualification succeeds.

## Current product truth

The normal Steward product is now:

```text
one Steward.exe distribution
+ local durable World/revision storage
+ persistent peer World authority
+ one active peer host while a World is live
+ private FriendsOnly Steam lobby
+ Steam peer transport
+ exact bootstrap/catch-up/replication
+ safe generation-fenced host handoff
+ canonical membership/revocation/Leave
```

The normal product does **not** require:

- a central Steward Backend.Api service;
- PostgreSQL authority;
- S3/object-storage authority;
- Friends Build routing;
- a permanent Steward server process;
- a second Steward distribution;
- a Web API identity/API URL in the ordinary Steam package.

Legacy remote/backend code remains available for explicit migration/history and is not ordinary product authority.

## Peer authority summary

A Shared World has persistent peer authority:

`WorldPeerAuthority(holder, generation)`

Current rules:

- LocalOnly -> Share creates peer generation 1;
- only the persistent holder may create writable Host authority;
- no live host means the World is inactive;
- normal restart does not increment generation;
- successful deliberate handoff increments generation exactly once;
- Steam lobby ownership is ephemeral and must agree with persistent Steward authority;
- Steam lobby membership is not canonical World membership;
- stale/ambiguous authority fails closed.

Live handoff ordering is:

```text
request target
-> safe outgoing stop
-> final save/capture
-> exact revision commit
-> exact revision transfer
-> target activates generation N+1
-> acknowledgement
-> Steam lobby ownership moves
```

Live removal revokes matching peer work before canonical membership removal. Leave World removes canonical access through the current authority holder before the former member deletes its local replica.

## Current documentation authority

The following documents are the primary current product/architecture entry points:

| Document | Status | Current note |
|---|---|---|
| `README.md` | **CURRENT** | Peer product state, beta RC, normal topology and package boundary. |
| `ARCHITECTURE.md` | **CURRENT** | Persistent peer authority, Steam lobby/transport, port 71/72, handoff, membership and migration boundary. |
| `PRODUCT_BOUNDARY.md` | **CURRENT** | Product definition, non-goals and World-continuity scope. |
| `NON_NEGOTIABLE_RULES.md` | **CURRENT** | Stable product/safety rules. |
| `DOCUMENTATION_AUDIT.md` | **CURRENT** | Documentation classification after peer cutover. |

If those documents disagree with older milestone/runbook prose, inspect current qualified code and these peer entry documents first.

## Current execution/release documents

| Surface | Status | Note |
|---|---|---|
| issue #349 | **CURRENT PHYSICAL RELEASE GATE** | Two PCs, two Steam accounts, actual private AppID, exact peer RC. |
| `tools/steam-peer-two-pc-kit/START-HERE-STEAM-PEER-TWO-PC.txt` | **CURRENT PHYSICAL RUNBOOK** | Share -> Host/Join -> handoff -> restart -> Remove/re-add -> Leave. |
| `tools/build-steam-peer-two-pc-kit.ps1` | **CURRENT BETA ARTIFACT TOOLING** | Builds exact AppID-only product + external evidence tooling. |
| `.github/workflows/steam-peer-two-pc-test-kit.yml` | **CURRENT MANUAL ENGINEERING WORKFLOW** | Manual-only physical-kit artifact generation. |
| issue #350 | **POST-BETA REPOSITORY CUTOVER** | Establish permanent peer-product trunk/default branch after physical pass. |

## Normal CI authority

Normal peer-product qualification is deliberately small:

- `Peer product CI`;
- `Windows acceptance package`;
- `Peer exact-head qualification` aggregate.

Game-adapter workflows are path-aware. Portable/physical engineering workflows are manual-only. Legacy backend CI is separate and backend/migration scoped.

A queued, cancelled, skipped, superseded, or different-head run is not qualification success.

## Legacy/migration documentation classification

The repository contains a large body of Backend.Api/PostgreSQL/S3/Friends-Build/closed-alpha/Bring-Here material that was once active product architecture.

That material is now **LEGACY MIGRATION / HISTORICAL ENGINEERING EVIDENCE** unless a document explicitly says it is describing an active migration path.

This includes, in particular, documentation whose normal topology assumes one or more of:

```text
Desktop -> HTTPS Backend.Api
Backend.Api -> PostgreSQL reservation/canonical authority
Backend.Api -> private S3-compatible object storage
Friends Build credentials/routing
Owned Private remote catalog
Bring Here central reservation/location flow
closed-alpha central deployment topology
```

Those systems may remain useful to migrate old shared Worlds or preserve engineering provenance. They do not override the peer product architecture.

### Backend-era documents

Documents such as the following must be read as legacy/migration/historical unless deliberately reactivated for migration work:

- `BACKEND_ROADMAP.md`;
- `BE_API_CONTRACT.md`;
- `BE_SCHEMA_AND_LIFECYCLE.md`;
- `BE_PROVIDER_EVALUATION.md`;
- `BE_OPERATIONS_RUNBOOK.md`;
- `BE_SECURITY_THREAT_MODEL.md`;
- closed-alpha deployment/ingress/publication runbooks;
- V2 Friends Build material;
- Bring Here/Owned Private remote acceptance material.

They remain in the repository because deleting history is not required to make the peer product simple.

## Explicit migration boundary

Normal AppID-only packaging emits only the platform AppID configuration required by the peer runtime.

Legacy remote behavior requires deliberate activation, for example:

- explicit migration package files such as `steward-steam-release.json` or `steward-friends-build.json`;
- explicit engineering/operator environment opt-in `STEWARD_ENABLE_LEGACY_REMOTE_MIGRATION=true`.

Ambient stale backend variables by themselves do not activate legacy remote mode.

Normal peer startup does not compose legacy invitation inbox, Owned Private/Bring Here UI, or owned-location publication worker unless an authenticated migration runtime is actually established.

## Stable product documents not invalidated by peer cutover

`PRODUCT_BOUNDARY.md` and `NON_NEGOTIABLE_RULES.md` were already largely topology-independent and remain authoritative.

Their stable principles still apply:

- the World is the product;
- Steam is the platform and games are adapters;
- one current valid World state;
- at most one writable Steward authority;
- canonical state advances last;
- preserve recoverable state on uncertainty;
- adapters own game-specific session/capture truth;
- no live process migration;
- no universal save merging;
- do not become a social/governance platform;
- use Steam/game capabilities instead of rebuilding them;
- build the smallest system that creates the full product effect.

## Game-adapter documentation

The repository still contains 19 first-party adapters with different capability/evidence levels.

Adapter-specific documents remain authoritative for game-specific facts only when they agree with current adapter implementation/capability contracts. Peer authority does not make an adapter automatically support Host, Join, Stop/Save, Create, or environment operations.

`GameAdapterCapabilities` remains the executable action boundary.

Time-gated or game-version-specific empirical work stays game-owned and must not be converted into generic Core/peer architecture without repeated evidence.

## Historical milestones

Old PR numbers, V2/V3/V4 phase names, closed-alpha checkpoints, provider trials, deployment rehearsals, and backend qualification evidence remain useful provenance.

They are not the current product baseline simply because their prose says “current,” “production,” or “release.”

Historical statements that must not override current peer truth include:

- “shared Backend.Api/PostgreSQL/S3 authority is the product architecture”;
- “Steam only authenticates and does not participate in peer authority transport”;
- “normal Desktop must authenticate a Steward remote session”;
- “Owned Private/Bring Here is normal World movement”;
- “Friends Build is the normal distribution path”;
- “a central backend is required while a World is active/inactive.”

## Documentation reconciliation rule

When prose and production behavior disagree:

```text
inspect current qualified authority/caller contract
-> preserve correct behavior
-> classify old milestone text as historical/migration if still useful
-> update active entry documents
-> do not rewrite runtime merely to match stale prose
```

This project has accumulated substantial useful history. Respecting that history means keeping its evidence while making current product truth unambiguous.

## Next documentation boundary

Do not perform another broad documentation rewrite merely to remain busy.

Before physical beta:

- current peer entry documents are authoritative;
- the RC remains frozen;
- issue #349 owns real Steam/game evidence.

After #349 passes:

- issue #350 owns permanent trunk/default-branch cleanup;
- any remaining user-facing docs can be reconciled against the physically qualified peer product;
- legacy backend/migration documents may then receive explicit header banners where useful, without deleting their technical history.

If #349 fails, document and fix only the observed owning boundary before changing product claims.