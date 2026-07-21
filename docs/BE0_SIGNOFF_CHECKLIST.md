# BE-0 Sign-off Checklist

This is the final backend planning checkpoint before BE-1.

Status: **BE-0 approved. Master planning lock is lifted; BE-1 is active under E1.**

The authoritative backend decisions are BE-D001 through BE-D015 in `BACKEND_ROADMAP.md`.

## Contract coverage

| Area | Canonical decision/status |
|---|---|
| Backend shape | BE-D001 — hybrid Steward API + transactional relational DB + immutable object storage |
| Steam authentication | BE-D002 — server-verified Steam ticket bootstraps Steward session |
| World access | BE-D003 — flat members + exactly one Access Manager |
| World-access vs multiplayer invitation | BE-D003 — separate concepts |
| Control API | BE-D004 — versioned HTTPS/JSON control plane; direct object transfer |
| Reservation | BE-D005 — one writer; heartbeat; Uncertain; deliberate reclaim |
| Last-safe recovery | BE-D006 — authority resolved before candidate abandonment |
| Provider strategy | BE-D007 — provider-neutral BE-1; named vendor deferred |
| Active-session outage | BE-D008 — gameplay may continue; Waiting to sync; revalidate before commit |
| Candidate retention | BE-D009 — unresolved local candidate has no automatic time-based deletion |
| Canonical retention | BE-D010 — current + previous 2, plus pinned recovery dependencies |
| State/environment transfer | BE-D011 — one immutable package pipeline, logically separate revisions |
| Package limits | BE-D012 — 20 GiB hard ceiling/package; ~64 MiB multipart target |
| API result/error contract | BE-D013 — machine-readable domain results + idempotency |
| Security/privacy | BE-D014 — TLS, encryption at rest, minimal data, private-by-default |
| Geography | BE-D015 — one authoritative EU deployment; EU-resident primary data/backups |
| Cross-workstream mapping | Finalized in `CROSS_WORKSTREAM_CONTRACT.md` |

## Canonical operational defaults

### Reservation timing

- heartbeat: approximately **30 seconds**;
- Active -> Uncertain: approximately **2 minutes** without valid heartbeat;
- deliberate reclaim by another active member: approximately **15 minutes** in Uncertain.

These are tunable operational defaults. The no-auto-release/generation-invalidation rules are safety invariants.

### Candidate retention

| Data | First-release retention |
|---|---|
| unresolved local recovery/unsynchronized candidate | no automatic time-based deletion |
| explicitly abandoned local candidate | approximately 7-day recovery grace |
| verified remote candidate not committed | approximately 7 days, extended while legitimately referenced by active recovery |
| partial/incomplete upload | approximately 24 hours after abandonment/inactivity |
| successfully committed/Unchanged temporary candidate | cleanup-eligible after durable result |

Disk pressure on unresolved gameplay changes becomes **Action required**, not silent deletion.

### Canonical revision retention

- current canonical state;
- previous two successfully committed canonical states;
- any older state/environment revision still pinned by active transaction or unresolved recovery evidence.

Retained canonical revisions are recovery assets, not normal selectable branches/history.

### Package/transfer defaults

- global hard ceiling: **20 GiB per immutable State or Steward-hosted Environment package**;
- adapter may enforce a smaller validated limit;
- large transfer multipart target: approximately **64 MiB** parts where appropriate;
- expected size and content hash are verified before publication eligibility;
- resumable upload/download where package size justifies it;
- local disk-space preflight before large materialization;
- full immutable-package transfer first; delta/chunk reuse deferred until evidence justifies it.

## Provider/cost decision

Named production provider selection is explicitly deferred through BE-1.

BE-1 requires no provider guess because it is a deterministic provider-free simulation.

Before production BE-3 deployment, choose providers that satisfy:
- PostgreSQL-compatible transactional semantics or equivalent;
- private immutable object storage with scoped direct transfers;
- resumable large-object support;
- encryption at rest/TLS;
- EU deployment/residency requirements;
- measured durability, storage, egress, restore, and operational cost requirements.

Commercial pricing/plan entitlements are not required to unlock BE-1. Technical safety ceilings and cost telemetry requirements are already bounded.

## API/transaction verification

BE-1 must prove at minimum:

1. active membership required for shared operations;
2. expected-head reservation acquisition;
3. only one Active/Uncertain writer;
4. 30-second heartbeat / ~2-minute uncertainty behavior under deterministic clock;
5. same valid generation reconnects;
6. deliberate reclaim after grace atomically invalidates old generation;
7. late invalidated generation cannot commit;
8. candidate publication requires expected size/hash verification;
9. expected-head + still-valid generation commit succeeds exactly once;
10. stale head returns deterministic conflict without replacing canonical state;
11. idempotent retry returns the original logical result;
12. reused idempotency key with different logical input is rejected;
13. active-session connectivity loss preserves local responsibility;
14. offline session end enters Waiting to sync with durable local candidate;
15. reconnect revalidates auth/generation/head before commit;
16. Continue from last safe state resolves authority before making World available;
17. unresolved candidate retention is independent from authority rejection;
18. canonical retention and pinned recovery dependencies behave deterministically.

## Cross-workstream verification

- UI-0 terminology matches `UI_ROADMAP.md`.
- `Only on this PC` may Host when adapter/runtime supports temporary hosting; sharing is not a hosting prerequisite.
- `Connection required` blocks a new shared writer.
- an already valid active session may continue through backend outage.
- `Waiting to sync` means local candidate preserved and remote handoff incomplete.
- `Recovery needed` means authority/evidence must be resolved before another normal writable lifecycle.
- Join never acquires a second writable reservation.
- Access Manager has no gameplay/reservation priority.

## BE-0 gate

Status: **complete and approved**.

BE-1 implementation is allowed under E1 and must map to `BE1_LOCAL_SIMULATION.md` and its acceptance matrix.

## First implementation slice

Implement only provider-free BE-1:
- in-memory transactional records;
- deterministic clock/failure injection;
- World access checks;
- immutable candidate publication simulation;
- acquire/heartbeat/Uncertain/reconnect/reclaim;
- expected-head commit;
- idempotency;
- candidate preservation and last-safe recovery;
- two-client/stale-generation tests.

Do not add Steam integration, HTTP hosting, cloud SDKs, production credentials, or infrastructure in BE-1.