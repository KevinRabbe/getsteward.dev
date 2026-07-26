# V2 Friends Build

Status: **ACTIVE PRODUCT GOAL**

V2 is the first Steward version intended to be used repeatedly by a real group of friends rather than only proving architecture, adapters, or deterministic subsystem behavior.

Its purpose is not to be the Steam Early Access release. Its purpose is to move Steward onto the same path by proving the product privately with real users first.

## Goal

> **A friend receives one download link, runs Steward on a normal Windows PC, joins the shared World group, and can Host or Join without manually moving save files.**

The decisive product question is now:

> **Do real friends prefer using Steward for persistent co-op Worlds over manually synchronizing save files?**

Adapter count is no longer a useful proxy for progress toward that question.

## V2 acceptance journey

The target end-to-end journey is:

```text
Steward build is produced
-> versioned Windows package is uploaded to private distribution
-> friend clicks the Google Drive link from Discord
-> friend extracts/runs Steward without a development environment
-> friend obtains a private Friends Build identity
-> friend accepts an invitation to a shared World
-> World shows the visible lobby membership
-> one member Hosts
-> other members Join
-> Steward keeps exactly one writable World session
-> host finishes
-> Steward captures, verifies, uploads, and commits the updated World
-> another member later Hosts the new current state
-> original host can later continue the state returned by the group
```

The first complete real acceptance sequence should include at least:

```text
PC A -> PC B -> PC C -> PC A
```

across separate real play sessions.

## Primary V2 games

V2 depth work is limited to the games the initial friend group actually plays most often:

1. Factorio
2. Palworld
3. 7 Days to Die
4. Valheim

The existing broader adapter set remains valid product/research work, but adding unrelated games does not advance V2.

### Adapter expansion rule during V2

Do not add another game merely to increase first-party adapter count.

A new adapter slice is justified during V2 only when one of these is true:

- it is required for one of the four primary V2 games, including Valheim;
- real Friends Build usage exposes a missing adapter contract or universal defect;
- the work directly removes a blocker to the V2 end-to-end acceptance journey.

Otherwise prefer depth, usability, distribution, identity, Host/Join, handoff, and recovery work.

## Private distribution

V2 does not require public Steam distribution.

The initial distribution contract is intentionally small:

```text
publish self-contained Windows build
-> create versioned ZIP
-> upload ZIP to Google Drive
-> post download link in the private Discord server
-> friend downloads
-> extract
-> run Steward.exe
```

Use immutable/versioned package names such as:

```text
Steward-2.0.0-alpha.1-win-x64.zip
```

Publish the SHA-256 digest next to the private download link so the exact tested build can be identified.

V2 does not require an updater, Microsoft Store packaging, public installer infrastructure, payment, licensing, or Steam release publication.

A conventional installer may be added only if real friend testing shows ZIP extraction itself is a material usability problem.

## Friends Build identity

Production Steam identity remains the intended commercial release boundary, but V2 must not be blocked on buying/publishing a Steward Steam AppID or obtaining production publisher credentials.

V2 therefore requires one deliberately narrow private authentication path that can issue the same normal Steward session credentials after a friend proves possession of a private invitation/credential.

Requirements:

- no Steam publisher credential is required for Friends Build login;
- production Steam verification remains fail-closed and is not weakened;
- private Friends Build identities use the existing generic `ExternalIdentityRef` / `VerifiedExternalIdentity` boundary;
- the backend still issues normal opaque Steward access/refresh session credentials;
- private bootstrap credentials are high-entropy, revocable, bounded, and the backend configuration stores only their one-way digests rather than plaintext;
- the desktop protects the long-lived Friends Build bootstrap credential with the Windows user credential boundary and exchanges it for fresh normal Steward session credentials on each application launch;
- rotating Steward access/refresh credentials remain process-memory state for V2 rather than creating a second durable session-token store;
- Friends Build authentication must be explicitly configured and disabled by default in production/release deployments;
- there is no password-reset, email-verification, public registration, social-account, or account-profile system in V2.

The goal is to remove the Steam publication dependency, not to create a second permanent identity platform.

## World lobby

The World itself is the lobby. Do not create a separate party/group/social domain.

The existing flat World access model is authoritative for membership. The existing Access Manager responsibility is the only management distinction needed.

The visible V2 lobby is intentionally limited to membership:

```text
Factorio World
--------------------------------

Members
Kevin — Access Manager
Alex
Max
Sarah

[ Invite player ]
```

Semantics:

- member list = existing authorized World membership;
- names = presentation labels resolved from the configured Friends Build identity roster;
- Invite player = existing World invitation/access model;
- Access Manager may invite/remove/transfer management responsibility using the existing access rules.

Host/session state and Join remain ordinary World lifecycle surfaces outside the lobby. The lobby does not need another query or state model merely to answer who belongs to the World.

Explicitly absent from the lobby:

- online/offline presence;
- gameplay presence;
- host-status polling;
- voice chat;
- text chat;
- friends graph;
- public profiles;
- matchmaking;
- public lobby/server browser;
- party roles;
- moderator/admin hierarchy;
- Discord replacement behavior.

Discord remains the group's communication/social surface.

## Existing product work V2 reuses

V2 must compose existing proven boundaries rather than rebuilding them:

- immutable World revisions;
- one-writer reservation/generation authority;
- shared PostgreSQL metadata;
- direct object-storage transfer;
- flat World membership and invitations;
- Access Manager responsibility;
- host presence for the existing Host/Join lifecycle where needed;
- read-only Join lifecycle;
- Windows Desktop Share/Manage access/Invites surfaces;
- recovery journal and responsibility tracking;
- self-contained Windows acceptance packaging work;
- game-adapter capability truthfulness.

## V2 workstreams

### V2-A — Download and run

Prove a friend with no source checkout, Visual Studio, or repository-specific setup can obtain one private package and launch Steward.

Acceptance:

- self-contained `win-x64` package;
- stable version displayed in diagnostics/about surface;
- package SHA-256 available;
- configuration errors are understandable and do not require editing source code;
- clean-machine launch test.

### V2-B — Private identity bootstrap

Add the smallest safe private identity path needed for Friends Build operation without Steam publisher credentials.

Acceptance:

- friend can authenticate from a clean client;
- backend derives a stable opaque private external identity;
- normal Steward access/refresh sessions are issued;
- reconnect/restart works without re-entering the bootstrap credential until revocation;
- invalid/revoked bootstrap credentials fail closed;
- production Steam path remains unchanged.

### V2-C — Lobby and invitations

Render the existing flat World access authority as the minimal visible lobby.

Acceptance:

- members are visible by name on the selected shared World;
- Access Manager can invite a friend without entering a SteamID64 in Friends Build mode;
- invite acceptance adds that identity through the existing flat membership authority;
- existing remove/transfer/leave behavior remains intact;
- no presence, host-status, party, friends-graph, or other social-domain model is introduced.

### V2-D — Factorio complete friend loop

Factorio is the first V2 reference game because it already has the deepest proven automatic lifecycle.

Acceptance:

- real PC A imports/shares;
- real PC B accepts and receives the exact environment/state;
- A hosts and B joins;
- session saves and commits automatically;
- B later hosts the resulting state;
- A later continues the returned state;
- competing writable sessions remain impossible;
- disconnect/restart/retry paths preserve the last valid World.

### V2-E — Palworld depth

Promote only the remaining real lifecycle pieces required for the friend loop. Keep Palworld-specific identity/save semantics inside the adapter.

Acceptance requires real Host -> Join -> safe stop/capture -> handoff behavior before capability promotion.

### V2-F — 7 Days to Die depth

Resolve the already-recorded sandbox/configuration authority evidence, then prove the real dedicated-server readiness/save-stop lifecycle before enabling automatic Host/Stop/Join.

Do not infer hosting capability from deterministic configuration transforms alone.

### V2-G — Valheim

Valheim is the one justified new primary adapter because it is part of the actual friend-group game set.

Before implementation, re-check the current post/near-1.0 save format and dedicated-server behavior. The earlier pre-1.0 adapter attempt was deliberately deferred to avoid implementing a save representation already in transition.

Implement only the current format and only the capability depth needed for the V2 friend loop.

### V2-H — Real-use recovery

Use failures observed during friend sessions to drive recovery work.

Priority cases:

- client/backend disconnect while playing;
- host crash;
- Steward crash/restart;
- upload succeeds but response is lost;
- upload/capture fails;
- host exits unexpectedly;
- stale Join presence;
- game update/environment mismatch;
- friend machine lacks required game/server installation.

Prefer eliminating impossible/problematic states over adding recovery machinery when the product can avoid creating the state in the first place.

## V2 definition of done

V2 is complete when all of the following are true:

1. A real friend can obtain Steward from the private Discord/Google Drive path and run it without developer setup.
2. The friend can authenticate without a published Steward Steam AppID.
3. The friend can be invited to a World and appears in the minimal World lobby.
4. Factorio completes a real multi-device A -> B -> C -> A sequence with automatic safe handoff.
5. Palworld, 7 Days to Die, and Valheim have either completed the same usable loop or have only narrowly documented empirical gates that the friend group does not currently need to continue testing V2.
6. Real sessions demonstrate that normal usage no longer requires manually exchanging or deciding which save file is newest.
7. Recovery from the failures actually encountered by the friend group is safe and understandable.
8. No V2 shortcut weakens the production Steam authentication/release boundary.

The strongest product success signal is behavioral:

> The group stops asking who has the newest save and starts opening Steward instead.

## Relationship to Early Access

V2 is not required to be the Early Access build.

The intended progression is:

```text
V1: prove architecture
-> V2: prove product with friends
-> later versions: fix real-use defects and complete release requirements
-> Steam Early Access: expose an already-used product publicly
```

Work done for V2 should therefore be selected for reuse toward Early Access whenever possible: packaging, onboarding, identity/session boundaries, lobby clarity, Host/Join behavior, safe handoff, recovery, diagnostics, and the four primary games.

Steam publication, production Steward AppID/publisher credentials, public distribution/update infrastructure, store-page assets, payment/commercial operations, and final release acceptance remain later gates.

## Working rule

For each proposed V2 task ask:

> **Does this make Steward easier for the initial friend group to obtain, understand, Host/Join with, safely hand off, or recover?**

or:

> **Does it remove a direct blocker on the path from the Friends Build to Steam Early Access?**

If neither is true, defer it.
