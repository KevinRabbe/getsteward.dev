# Design Decisions

Status: **CURRENT ACTIVE DURABLE DECISIONS**

This file contains active product/architecture decisions only. Completed sequencing choices and abandoned exploratory directions are removed rather than preserved as competing guidance.

A decision may be replaced deliberately, but implementation convenience must not silently reverse it.

## D-001 — The World is the product

**Decision:** Users select a World, not a save file, server folder, mod profile, revision package, or host machine.

Those are implementation details required to reproduce and continue the playable reality.

## D-002 — Steam is the commercial platform; games are adapters

**Decision:** Steam owns Steward distribution/update and production identity bootstrap. Game-specific behavior remains behind independently compiled adapters.

Steward reuses Steam/game primitives rather than rebuilding them.

## D-003 — Core contains no game-specific branches

**Decision:** Core must never branch on Factorio, Palworld, or another game identity.

Discovery, environment preparation, restore, launch, readiness, Join specifics, safe stop, capture and game-specific validation belong to adapters.

## D-004 — Steward completes the full World handoff

**Decision:** Launching is not completion.

```text
latest state
-> reserve
-> prepare
-> restore
-> launch
-> observe
-> safe capture boundary
-> capture
-> store
-> verify
-> commit
-> resolve authority/recovery
-> next player can continue
```

## D-005 — One current shared World has one writable Steward session

**Decision:** A Steward-managed World has one current valid state and at most one active writable Steward authority.

Steward does not solve divergent arbitrary saves with generic merging.

## D-006 — Canonical head advances last

**Decision:** The World head changes only after replacement state is completely captured, durably stored/published, verified, and authorized for commit.

Failure preserves the previous valid head.

## D-007 — Published revisions are immutable

**Decision:** Environment and state revisions are never overwritten after publication. Only the World's current pointers are mutable.

## D-008 — Environment and state are versioned separately

**Decision:** A World references one environment revision and one state revision.

Normal play can advance state without manufacturing a new environment revision.

## D-009 — `EnvironmentManifest` is authoritative

**Decision:** The structured manifest is the environment source of truth. Fingerprints are comparison/cache aids, not replacement authority.

## D-010 — Do not routinely hash complete game installations

**Decision:** Full-install hashing is not the normal path.

Use targeted verification where the adapter/product boundary actually requires it.

## D-011 — Session/readiness/safe-capture evidence is adapter-owned

**Decision:** Core never assumes one PID, one timeout, or one file timestamp universally proves session state.

## D-012 — Adapters own portable game-state shape

**Decision:** Core treats game state as opaque adapter-produced packages.

A World may be one file, multiple files, a directory archive, a database, or another proven native projection.

## D-013 — Temporary/package cleanup ownership is explicit

**Decision:** A package/workspace is deleted only when its ownership and cleanup eligibility are proven.

A path is not disposal authority.

## D-014 — Prepared workspaces become recovery assets after launch uncertainty

**Decision:** Durable recovery responsibility is registered before writable gameplay may begin. Uncertain post-launch workspaces are preserved.

## D-015 — Failure handling is conservative

**Decision:** Unknown state-handling failures stop the operation and preserve safety evidence rather than being swallowed as success.

## D-016 — Durable state and writable-session authority are separate

**Decision:** `IWorldStorage` owns durable World/revision data. `IWorldSessionCoordinator` owns one-writer session authority.

The shared implementation currently composes both through Steward infrastructure/backend, but their contracts/failure semantics remain distinct.

## D-017 — Host switching happens between sessions

**Decision:** Another device hosts only after prior writable authority safely completes or is deliberately recovered.

No live game-process migration is required.

## D-018 — No universal save merging

**Decision:** Steward does not merge independently modified arbitrary game saves.

A validated game-native operation may exist inside one adapter without becoming generic merging.

## D-019 — No Git-style World product model

**Decision:** Branches, Forks, merge requests, rebasing and conflict-resolution workflows are not Steward product concepts.

## D-020 — No ownership/governance/social platform

**Decision:** Steward keeps only the identity/access information required for safe World continuity.

No complex ownership hierarchy, party governance, social graph or public community system is required.

## D-021 — External copies are outside Steward authority

**Decision:** Steward does not promise physical uniqueness, DRM, or deletion of every external package already obtained by an authorized device.

## D-022 — Shared does not mean public

**Decision:** `Shared` means eligible for Steward cross-device authority/handoff. It does not imply public discovery/listing.

## D-023 — Background-first is active responsibility

**Decision:** Steward stays mostly out of the player's way while continuing session observation, heartbeat, capture, transfer, commit and recovery work in the user-session Desktop/tray process.

## D-024 — Reuse existing platform/game infrastructure first

**Decision:** Prefer Steam, Windows, game-native multiplayer, dedicated servers, Workshop/native content tooling, launchers and native import/export before adding Steward-owned substitutes.

## D-025 — Generalize only after evidence proves the pattern

**Decision:** One game's edge case stays in its adapter until evidence demonstrates a reusable universal concept.

## D-028 — Commercial quality does not justify uncontrolled scope

**Decision:** Reliability, recovery, testability, maintainability and stable boundaries are mandatory. Speculative platform/social/governance machinery is not.

## D-029 — Active documentation must be consistent

**Decision:** Active architecture, lifecycle, decision, roadmap and subsystem documents must describe current product truth.

Historical milestone evidence may remain, but its old next-step statements must be clearly non-authoritative.

## D-030 / BE-D004 — Versioned HTTPS/JSON control API + direct package transfer

**Decision:** Backend control uses versioned `/api/v1` HTTPS/JSON operations. Large World/environment bytes normally transfer directly between authorized Desktop and private object storage through scoped authorization.

Object storage never decides canonical authority.

## D-031 / BE-D014 — First-release security baseline

**Decision:** Use established security primitives:

- production Steam ticket verification;
- explicitly configured private Friends Build proof only for private testing;
- short-lived normal Steward sessions;
- TLS for remote credential/control/transfer traffic;
- private object storage;
- per-resource authorization;
- generation/expected-head checks;
- bounded metadata/control/package operations;
- redacted diagnostics;
- server-only infrastructure/publisher secrets;
- tested backup/restore.

Do not invent custom cryptography.

## D-032 / BE-D015 — One authoritative EU backend

**Decision:** First release uses one authoritative Steward backend authority in the EU for relational World/session/access authority and primary immutable package storage.

Multi-region active-active World authority is not required. Immutable delivery optimizations may be added later only if measured.

## D-033 — Runtime uses one user-session Desktop/tray process

**Decision:** First release uses one Desktop/background process, one active managed writable lifecycle per device, generic runtime orchestration and adapter-owned game evidence.

No Windows Service without measured need.

## D-034 / BE-D001 — Hybrid backend owns only missing shared authority

**Decision:** Steam/platform identity remains external infrastructure. Steward backend owns shared World metadata, membership, current heads, reservation generations, commit/recovery authority and Host-presence coordination.

Private object storage owns bytes only.

## D-035 / BE-D002 — Production Steam proof bootstraps normal Steward sessions

**Decision:** Desktop obtains a Steam Web API ticket; backend verifies it and derives SteamID64. Client-supplied SteamID alone is never trusted.

Normal Steward access/refresh credentials are short-lived/rotating session material. Production Desktop reauthenticates through Steam on a new launch rather than storing ordinary refresh credentials in device settings.

## D-036 / BE-D003 — Flat shared membership + one Access Manager

**Decision:** Accepted members have equal World usage authority. Exactly one Access Manager manages membership only.

Access Manager receives no gameplay/reservation/overwrite priority.

## D-037 / BE-D005 — Reservation timeouts create uncertainty, not availability

**Decision:** A missed heartbeat may move Active -> Uncertain. It never automatically produces another writer.

Deliberate reclaim invalidates old generation authority before future acquisition.

## D-038 / BE-D006 — Last-safe continuation resolves authority first

**Decision:** `Continue from last safe state` can abandon a candidate as canonical work only after reservation/generation/head authority is deliberately resolved.

Candidate evidence follows retention policy rather than being deleted by the authority transaction.

## D-039 / BE-D007 — Provider-neutral contracts; provider choice is evidence-driven

**Decision:** Steward's product contracts require PostgreSQL-compatible transactional authority and private S3-compatible/equivalent immutable object storage without provider types leaking into Core.

Concrete PostgreSQL/S3-compatible implementations already exist. Final production vendor selection remains evidence-driven through real deployment, restore, performance, privacy and cost measurements.

## D-040 / BE-D008 — Valid active gameplay may continue through backend outage

**Decision:** A valid already-running shared session may continue locally through temporary backend loss. Remote authority becomes Uncertain rather than Available.

Session end preserves a durable local candidate and enters Waiting to sync until authority/head can be revalidated.

## D-041 / BE-D009 — Unresolved gameplay candidates are not silently time-deleted

**Decision:** Unresolved local candidates have no automatic time-based deletion. Explicit abandonment/remote uncommitted candidates/partial transfers use bounded disclosed retention appropriate to their class.

Disk pressure becomes Action required rather than silent gameplay loss.

## D-042 / BE-D010 — Retain three canonical states plus pinned dependencies

**Decision:** Shared Worlds normally retain current canonical state + previous two committed states, with older state/environment dependencies pinned while active/recovery references require them.

This is recovery retention, not user-facing history.

## D-043 / BE-D011 — State/environment use one immutable transfer infrastructure

**Decision:** State and environment remain logically distinct revision references but can share authorized immutable package transfer/verification infrastructure.

Prefer reliable native/Steam/Workshop references over re-hosting third-party bytes Steward does not need to own.

## D-044 / BE-D012 — Package transfer is bounded before publication

**Decision:** First-release safety ceiling remains 20 GiB per immutable State or Steward-hosted Environment package, with stricter adapter limits allowed.

Expected size/hash, resumable transfer and local disk/cache boundaries apply before data becomes usable/published.

## D-045 / BE-D013 — Authority outcomes are deterministic and idempotent

**Decision:** Expected transaction outcomes use stable machine-readable results. Acquire/reclaim/commit use durable idempotency where ambiguous replay could duplicate authority.

A timeout is unknown outcome, not permission to perform a conflicting write.

## D-046 — Executable capabilities own action claims

**Decision:** Catalog registration does not mean full game support.

```text
Start World -> AutomaticLocalLaunch
Host World  -> AutomaticHostLaunch
Join        -> AutomaticClientJoin + current JoinCapabilityResult
Stop/Save   -> AutomaticHostStop
Create      -> NativeWorldCreation
```

Do not add a second release-support/maturity taxonomy merely for marketing or UI convenience.

## D-047 — First-release Join is automatic; manual direct-connect guidance is presentation only

**Decision:** Generic guided-manual Join lifecycle remains removed.

`IManualDirectConnectProvider` may present an already-ready native endpoint/instruction, but it does not prepare/launch/observe/clean a manual client session and does not grant `AutomaticClientJoin`.

## D-048 — Native creation uses the game's generator

**Decision:** `NativeWorldCreation` invokes a safe game-native creation primitive and captures its result through the normal state boundary.

Steward does not synthesize/re-encode native save formats merely to create new Worlds.

## D-049 — Steam owns Steward installation/update

**Decision:** Production non-secret routing/AppID/Web API identity is packaged with the release candidate; actual installation/update/depot BuildIDs are SteamPipe/Steam responsibilities.

Steward does not implement a second self-updater/distribution trust system.

## D-050 — Empirical uncertainty does not serialize development

**Decision:** When a question needs real hardware/game/provider evidence:

```text
record exact test
-> freeze only dependent capability/claim
-> continue independent deterministic work
-> batch expensive real tests later
```

A deferred test is not proof of success.

## D-051 — Eliminate ownership before solving it

**Decision:** Before adding a subsystem, ask whether Steward can make the problem irrelevant or leave it to the platform/game that already owns it.

Examples:

- no WorldOption writer when Palworld can own native serialization;
- no 7DTD SandboxCode decoder when exact opaque reuse is sufficient;
- no Steward self-updater when Steam owns updates;
- no generic Host-eligibility engine when missing required server installation already proves the path impossible;
- no NAT-traversal stack before real reachability evidence demonstrates a concrete missing mechanism.

## Decision ID history

`D-026` and `D-027` are intentionally absent from the active set. They were completed sequencing decisions (initial Factorio/Palworld validation emphasis and the then-next two-device proof), not durable product architecture. Their historical evidence remains in repository history/status documents.

## Current execution status

Planning locks are lifted. Generic platform architecture and deterministic V3 release preparation are complete.

Current development mode is:

```text
keep active documentation truthful
-> reconcile the known Games Library implementation drift
-> continue evidence-driven V3-E real Windows testing
-> fix only concrete release defects
-> run the real V3-F provider/Steam/game gate when external resources are available
```

These execution steps do not replace the durable decisions above.