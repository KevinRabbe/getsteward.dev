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

| Requirement | State | Remaining work |
|---|---|---|
| Games -> selected game -> Worlds -> World details | **DONE** | PR #147 restores the approved game-first hierarchy. |
| All registered games visible even with zero Worlds | **DONE** | #147 renders every registered first-party adapter and managed-World count. |
| Game workspace search/sort | **MISSING** | `UI_ROADMAP.md` requires search/sort; selected-game workspace does not expose it yet. |
| Game banner/header | **PARTIAL** | Selected game title exists; richer header/banner presentation remains incomplete. |
| Game attention indicator | **MISSING** | Library should surface active / Waiting to sync / Action required / Recovery needed Worlds without hiding them one level down. |
| Global Settings surface | **MISSING** | Approved global navigation includes Settings; current device setting remains embedded in the World sidebar. |
| Localization-ready complete UI | **PARTIAL** | `DesktopText` infrastructure exists but many direct English strings remain in current Desktop surfaces. |

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

| Requirement | State | Remaining work |
|---|---|---|
| Friends Build invite by human-readable configured name | **DONE** | Existing Friends Build lobby roster dropdown. |
| Production Steam invite UX | **MISSING** | Current Desktop still requires manual numeric SteamID64 entry. Replace that user-facing plumbing with the smallest Steam-owned identity/friend selection mechanism available at the real release boundary; do not build a Steward friends graph. |
| Share World choose/add identities -> review -> publish | **MISSING** | Current Share immediately publishes, then users invite later through Manage access. Approved UI contract still describes choosing/reviewing invitees as part of Share. Reconcile by either implementing that small pre-publication selection or deliberately simplifying the contract with product evidence. |
| Stop sharing / delete shared World | **MISSING** | UI contract mentions it subject to backend deletion policy; no complete user/backend deletion contract exists yet. Decide and implement the smallest safe terminal ownership rule before release or explicitly remove it from first-release scope. |

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
| V3-F real Steam acceptance | **EMPIRICAL** | Real AppID/depot/publisher credential, SteamPipe upload/install/update, real Web API tickets, two independent installations, real advertised game/action handoff. |
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
- generic guided-manual Join lifecycle;
- generic NAT traversal before measured need;
- permanent Steward game servers;
- Steward self-updater;
- public email/password account platform;
- save branching/merging.

Steam, Discord, Windows, the game, or the deployment platform already own those concerns or the product deliberately does not need them.

## Current deterministic execution order

```text
Games Library #147                     DONE
-> small World Lobby #148             DONE after final docs qualification
-> close remaining UI completeness gaps
   (search/sort, attention, Settings, localization cleanup)
-> reconcile Share/invite/delete user journeys
-> resume V3-E real Windows evidence
-> run V3-F real Steam/provider/game gate
-> optimize only from measured bottlenecks
```

A new idea enters this list only when it is a genuine first-release requirement, concrete usability defect, or evidence-driven release blocker.
