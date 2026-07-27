# Product Completeness

Status: **CURRENT RELEASE COMPLETENESS CHECKLIST**

This file answers a different question from `DOCUMENTATION_AUDIT.md`.

`DOCUMENTATION_AUDIT.md` asks whether documentation is current. This file asks whether every approved first-release product requirement is actually implemented and, where implementation exists, whether it is only deterministic/CI-proven or has real release evidence.

Use these states:

- **DONE** — implemented at the current deterministic product line.
- **IN PROGRESS** — implementation exists on the current stacked work line but is not yet the frozen qualified checkpoint.
- **MISSING** — approved/current product behavior still has no complete implementation.
- **PARTIAL** — some of the approved behavior exists, but the user-facing contract is not complete.
- **EMPIRICAL** — deterministic implementation exists; the remaining work requires a real machine/provider/game/Steam boundary.
- **DEFERRED/REMOVED** — intentionally outside the current product boundary; do not resurrect it as forgotten work.

## Current deterministic product foundation

| Area | State | Current truth |
|---|---|---|
| World/revision/one-writer lifecycle | **DONE** | One current valid World state, immutable revisions, expected-head commit, no branch/merge model. |
| Local + shared storage | **DONE** | Local persistence plus Backend.Api/PostgreSQL/private S3-compatible immutable transfer. |
| Shared membership/access authority | **DONE** | Flat membership, invitations, one Access Manager, remove/transfer/leave. |
| Writable reservation/generation authority | **DONE** | Acquire/heartbeat/Uncertain/reclaim/commit/abandon with idempotency. |
| Recovery responsibility | **DONE** | Waiting to sync, interrupted session, recovery/export/continue-safe-state boundaries. |
| Tray/background lifetime | **DONE** | Closing hides the window; Quit is guarded while responsibility remains. |
| Adapter capability truthfulness | **DONE** | Catalog registration never grants Start/Host/Join/Stop/Create. |
| Import | **DONE** | Adapter-driven discovery/import; imports remain local until explicit Share. |
| Factorio native Create | **DONE** | Native `--create` path; game generates revision-1 state. |
| World membership/access management | **DONE** | Existing access dialog lists members and manages invitations/removal/Access Manager transfer/leave. |

## Games Library / Desktop presentation

| Requirement | State | Current truth |
|---|---|---|
| Games -> selected game -> Worlds -> World details | **DONE** | PR #147 restores the approved game-first hierarchy. |
| All registered games visible even with zero Worlds | **DONE** | #147 renders every registered first-party adapter and managed-World count. |
| Game workspace search | **DONE** | Repository audit during #149 found the existing `MainWindow.WorldSearch.cs` implementation already wired by unified startup; no replacement search was added. |
| Game workspace sort | **DONE** | #149 adds only the missing name A–Z / Z–A projection on the same WPF collection view used by search. Canonical World order/state is unchanged. |
| Game banner/header | **DONE** | The selected-game workspace already has an operational header: Back to Games, selected game name, Worlds context, and Refresh. The roadmap requires a banner/header, not decorative artwork; no additional banner subsystem is required. |
| Game attention indicator | **DONE** | #150 projects the existing `WorldLifecycleResponsibilityTracker` into the affected game tile summary: Preparing/Running/Hosting/Saving World/Recovery needed/Action required. No second status cache exists. |
| Global Settings surface | **DONE** | #149 adds top-level Settings and re-homes the existing device-hosting control; the same `DeviceSettingsStore` and WPF control instances remain owners. |
| Localization-ready fixed product vocabulary | **DONE** | #151 routes fixed first-release navigation/action/state terms through the existing `DesktopText` resource catalog and proves neutral fallback under another UI culture. Shipping translated catalogs is later localization content, not missing UI architecture. |

## World Lobby — current first-release boundary

The World Lobby is intentionally small. It is **not** a social network.

Canonical rule:

> **World Lobby = World membership + ephemeral players currently in that World + authoritative current Host.**

Steam/Discord continue to own friends, profiles, chat, voice, party/social organization, and game/platform invitations where supported.

Steward does not add a friends graph, chat, voice, matchmaking, public lobby browser, rich profiles, party roles, or permanent presence history.

| Lobby fact | State | Authority |
|---|---|---|
| Who belongs to this World? | **DONE** | Existing World membership/access records. |
| Who is the Access Manager? | **DONE** | Existing access authority. |
| Who is the current Host? | **DONE** | Lobby snapshot composes the existing reservation-backed Host-presence holder/state; player presence never decides Host. |
| Who is currently playing? | **DONE — OBSERVED SESSIONS** | Host is always included from Host truth; automatic Join publishes 15-second ephemeral presence with a 45-second visibility TTL and clears after the observed client ends. |
| Manual/native unobserved Join | **DELIBERATELY NOT CLAIMED** | Steward does not fake presence for client sessions it cannot observe. |
| Presence history | **DEFERRED/REMOVED** | No history. Presence expires automatically and is presentation-only. |

Presence is non-authoritative. It may never acquire/release a reservation, change membership, grant Join, commit state, clear recovery, or decide who is Host.

The lobby is a read-only card on selected shared-World details. The existing Manage access dialog remains the only membership mutation surface.

## Sharing / invitation completeness

| Requirement | State | Current truth / remaining evidence |
|---|---|---|
| Initial Share World publication | **DONE** | The existing executable transaction verifies the exact canonical environment, journals remote-authority intent, publishes one immutable creator-owned World, and makes the authenticated sharer the sole Access Manager. |
| Add/invite people after sharing | **DONE** | Existing Manage access creates World-access invitations after the shared World exists. Pending invitations grant no package/reservation/commit authority and acceptance creates membership. |
| Friends Build invite by human-readable configured name | **DONE** | Existing Friends Build lobby roster dropdown. |
| Production Steam invite UX | **EMPIRICAL — V3-F** | The deterministic fallback accepts stable SteamID64 identity. Select the smallest Steam-owned friend/identity/invitation surface only with the real AppID/provider boundary; do not build a Steward friends graph. |
| Pre-publication invite selection/review | **DEFERRED/REMOVED** | It adds staging/UI coupling without changing canonical publication or membership authority. First release publishes the World first; Manage access owns invitations afterward. |
| Stop sharing / destructive shared-World deletion | **DEFERRED/REMOVED** | No current backend terminal-deletion contract exists. First release does not manufacture one. Access Manager responsibility can be transferred, then the former manager can leave when safe. |

## Adapter/runtime depth still requiring real evidence

| Area | State | Remaining evidence |
|---|---|---|
| Factorio Internet Host/Join/handoff | **EMPIRICAL** | Real two-network reachability, Join, safe capture/commit, cross-device continuation. |
| Palworld native Join/handoff | **EMPIRICAL** | Real two-network native IP:port Join plus safe Stop/capture/handoff. Automatic client Join is not advertised, so manual clients are intentionally absent from lobby player presence. |
| 7 Days to Die managed runtime | **EMPIRICAL** | Readiness, minimal local shutdown framing, clean long-lived exit, final-save boundary, capture/relaunch; Join separate. |
| Project Zomboid managed runtime | **EMPIRICAL** | Isolated dedicated-server lifecycle, safe shutdown, capture/relaunch without live-profile ownership. |
| Valheim | **EMPIRICAL/DEFERRED** | Recheck released 1.0 save/server behavior before implementing/promoting lifecycle depth. |
| Other 15 adapters | **DEFERRED/REMOVED for gameplay actions** | Intentionally state/import/environment slices until evidence justifies deeper capabilities. |

## Release evidence still open

| Gate | State | Remaining work |
|---|---|---|
| V3-E real Windows acceptance | **EMPIRICAL** | Keyboard/focus, Narrator/UI Automation, mixed DPI, tray/background, suspend/restart/logoff, real timings/endurance. PR #138 closed only the first observed Windows defect. |
| V3-F real Steam acceptance | **EMPIRICAL** | Real AppID/depot/publisher credential, SteamPipe upload/install/update, real Web API tickets, two independent installations, production Steam invite/identity UX, real advertised game/action handoff. |
| Real EU provider deployment | **EMPIRICAL** | Actual API/PostgreSQL/object-storage deployment, backups/restore/logging and residency evidence. |
| Friends Build real-use batch | **EMPIRICAL** | Real friend download/auth/lobby/Host/Join/A->B->C->A/recovery evidence. |
| Real large-World/endurance measurements | **EMPIRICAL** | Representative real packages, timings, long managed sessions and practical transfer behavior. |

## Explicitly not missing

Do not put these back on the roadmap merely because Steward could implement them:

- Steward friends/social graph;
- chat or voice;
- public server/lobby browser;
- matchmaking;
- generic party system;
- global online/offline presence;
- permanent player-presence history;
- pre-publication invitation staging/review;
- destructive shared-World deletion before a real terminal-deletion requirement and backend contract exist;
- decorative game-banner artwork as a separate product subsystem;
- translated locale catalogs before a locale is actually selected for release;
- generic guided-manual Join lifecycle;
- generic NAT traversal before measured need;
- permanent Steward game servers;
- Steward self-updater;
- public email/password account platform;
- save branching/merging.

Steam, Discord, Windows, the game, or the deployment platform already own those concerns or the product deliberately does not need them.

## Current execution order

```text
Deterministic first-release product completeness   CLOSED by #147-#153
-> V3-E real Windows acceptance
-> V3-F real Steam/provider/game acceptance
   including production Steam invite/identity UX
-> real EU provider deployment evidence
-> Friends Build real-use batch
-> real large-World/endurance measurements
-> optimize only from measured bottlenecks
```

A new deterministic feature enters this list only when it is a genuine first-release requirement, concrete usability defect, or evidence-driven release blocker.
