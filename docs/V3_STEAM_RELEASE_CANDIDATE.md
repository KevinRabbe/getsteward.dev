# V3 Steam Release Candidate

Status: **ACTIVE PRODUCT GOAL**

V3 begins after the deterministic V2 Friends Build line is complete. V2 proved and prepared the private-product path; its remaining real-provider, real-machine, and real-friend acceptance items stay recorded as empirical evidence and do not justify keeping V2 as the active coding goal.

V3 is the stage between a private Friends Build and public Steam Early Access.

Its purpose is not to add more platform architecture. Its purpose is to remove the remaining development/private-build assumptions from the already-composed product until Steward can be uploaded to Steam and exercised through the real production identity/distribution boundary without a parallel Steward-owned platform.

## Goal

> **A normal Steam-installed Steward build starts with no developer environment setup, authenticates through Steam, connects to the production Steward backend, and performs the same safe shared-World lifecycle already proven in development.**

The decisive V3 question is:

> **Is the product technically release-shaped before the final irreversible/external Steam publication gate is opened?**

V3 therefore optimizes the distance from the qualified product to a real Steam release candidate, not adapter count, speculative features, or a second distribution/account system.

## Relationship to V2

V2 remains valuable evidence and a private test surface.

The qualified deterministic V2 line provides:

- private Friends Build authentication without weakening Steam authentication;
- versioned self-contained Windows packaging;
- minimal World membership/lobby and invitations;
- generic Share/Manage access/Join surfaces;
- one-writer authority and immutable World handoff;
- recovery responsibility and deterministic failure handling;
- Factorio/Palworld/7DTD/Valheim-specific empirical gates;
- one real-acceptance batch and one disposable EU deployment contract.

The remaining V2 empirical work is not relabeled as completed evidence. It stays in `V2_REAL_ACCEPTANCE_BATCH.md` / `DEFERRED_EMPIRICAL_TESTS.md` and should still be executed when the external environment and machines are available.

V3 simply stops treating those external gates as a reason to keep inventing deterministic V2 code.

## V3 product boundary

V3 preserves the existing product definition:

```text
one shared World
+ one current valid state
+ at most one writable session
+ Steam players/devices at different times
+ no always-on Steward game server
```

Steam remains responsible for:

- Steward identity bootstrap at release;
- Steward distribution;
- Steward updates;
- game ownership/installations;
- Steam-native friends/invitations/join where a game supports them.

Steward remains responsible for:

- shared World authority;
- exact environment/state transfer;
- temporary Start/Host/Join lifecycle;
- safe capture and canonical commit;
- recovery when Steward owns unresolved responsibility.

V3 must not rebuild anything Steam already owns merely because the product is approaching release.

## V3 acceptance journey

The release-shaped path is:

```text
qualified Steward build
-> immutable release configuration is packaged with the build
-> build is uploaded as Steam depot content
-> Steam installs/updates Steward
-> user launches Steward through Steam
-> SteamAPI initializes the actual current AppID
-> Steward verifies that runtime AppID against its expected release configuration
-> Steward requests GetAuthTicketForWebApi for the configured service identity
-> production Backend.Api verifies the ticket with the matching AppID/identity and publisher credential
-> shared World catalog loads
-> user Starts / Hosts / Joins according to real adapter capabilities
-> updated World is captured, verified, uploaded, and committed
```

The final real AppID/publisher/depot proof remains the `STEAM_RELEASE_GATE.md` acceptance gate. V3 may prepare every deterministic prerequisite without pretending that gate has run.

## Core V3 rule: package owns non-secret release configuration

A production Steam user must not need repository-local environment variables to make Steward usable.

Current engineering configuration uses:

```text
STEWARD_API_BASE_URL
STEWARD_AUTH_MODE
STEWARD_STEAM_APP_ID
STEWARD_STEAM_WEB_API_IDENTITY
```

That remains useful for development and isolated acceptance, but it is not the commercial runtime contract.

The release candidate should instead carry immutable, non-secret release configuration beside the executable and inside the exact bytes uploaded to Steam.

Required release facts are:

```text
HTTPS Steward backend coordinate
+ expected Steward Steam AppID
+ GetAuthTicketForWebApi service identity
```

The publisher API key remains server-only and must never enter the client package.

Steam owns actual AppID discovery at runtime. Steward already calls `SteamAPI.Init()` and `SteamUtils.GetAppID()`; V3 preserves the explicit equality check between Steam's actual AppID and the expected release AppID rather than trusting package text alone.

## V3 workstreams

### V3-A — Steam release configuration

Remove developer environment setup from the commercial Desktop path.

Acceptance:

- a strict bounded adjacent release configuration supplies only non-secret production routing/identity facts;
- remote API URL is HTTPS and rejects credentials/query/fragment;
- expected Steam AppID is positive and explicit;
- Web API identity is bounded and whitespace-free;
- Steam's runtime AppID must equal the configured expected AppID;
- publisher credentials never enter Desktop/package configuration;
- environment-variable configuration remains engineering-only;
- simultaneous package + environment routing fails closed rather than selecting one implicitly.

### V3-B — Depot-ready Windows content

Produce exactly the directory Steam will install rather than inventing another updater.

Acceptance:

- release configuration is part of the byte-verifiable package/depot input;
- Desktop binary carries the requested product version;
- no Friends Build credential or backend secret is present;
- no `steam_appid.txt` is shipped as a production crutch;
- clean unpack/install content launches locally only under the expected Steam boundary when Steam mode is selected;
- existing acceptance-manifest integrity rules remain reusable.

Steam later owns transport, signing/distribution behavior, patching, and updates.

### V3-C — Production backend configuration

Make the release backend configuration explicit without changing backend authority.

Acceptance:

- real production mode has Friends Build authentication disabled unless deliberately running a private test deployment;
- exact Steam AppID/identity pair matches the client release configuration;
- publisher credential is server-secret only;
- PostgreSQL/S3/HTTPS configuration remains provider-neutral;
- reverse-proxy trust remains exact and topology-driven rather than globally enabled;
- health/readiness/backup/restore/retention boundaries remain the existing production contracts.

### V3-D — Release adapter set

Do not promise a game because an adapter exists in the catalog.

For each game exposed as release-capable:

```text
discovery/import
+ exact environment
+ restore
+ truthful Start/Host/Join capabilities
+ safe end/capture
+ handoff/recovery evidence
= release-capable adapter
```

Factorio remains the reference lifecycle. Other adapters enter the release-capable set only when their actual required lifecycle is proven. Unsupported capabilities remain unavailable instead of simulated.

V3 does not impose an arbitrary game-count target.

### V3-E — Real Windows release acceptance

Use real Windows machines to close the categories CI cannot prove:

- clean Steam installation/launch;
- keyboard navigation;
- assistive-technology labels;
- mixed-DPI behavior;
- tray/background behavior;
- suspend/restart/logoff interactions where relevant;
- package sizes and real capture/restore/transfer timings;
- long real game sessions.

Observed defects become narrow implementation work. Passing CI is not substituted for this evidence.

### V3-F — Steam release gate

Only when the product owner decides Steward is otherwise good enough to publish:

```text
obtain/configure real Steward AppID + publisher credential
-> upload release-candidate depot
-> launch through Steam
-> verify actual AppID
-> obtain real Web API ticket
-> backend verifies ticket
-> two independent installations authenticate
-> perform real shared-World handoff
-> verify Steam update/depot behavior
```

No fake production auth bypass is permitted.

## What V3 deliberately does not add

- Steward self-updater;
- public email/password account system;
- second identity platform;
- public lobby/server browser;
- social graph/chat;
- payment/licensing service outside Steam;
- generic NAT traversal before a measured game/network need;
- permanent game servers;
- new Core/UI abstractions without a concrete release defect;
- more adapters merely to advertise a larger number.

## Definition of done

V3 is complete when:

1. the Steam release candidate needs no developer environment variables for normal production routing/authentication;
2. the exact depot-ready content is reproducible and byte-verifiable before upload;
3. production client/backend Steam configuration is explicit, matched, and secrets remain server-only;
4. the intended release adapter set advertises only empirically supported capabilities;
5. real Windows/release acceptance has no unresolved release-blocking defect;
6. the genuine Steam AppID/publisher/ticket/two-installation handoff passes without bypasses;
7. Steam depot installation/update behavior is proven;
8. the private Friends Build remains a test tool, not a hidden production dependency.

At that point Steam Early Access is a publication/business decision rather than an architectural development milestone.

## Working rule

For every proposed V3 task ask:

> **Does this remove a development/private-test assumption between the qualified Steward product and a real Steam release?**

or:

> **Does concrete release evidence show this defect blocks safe shared-World use?**

If neither is true, defer it.

The higher-priority simplification rule still applies:

```text
need release mechanism X
-> Steam / Windows / the game already owns X
-> reuse that primitive
-> delete Steward-owned substitute work
```
