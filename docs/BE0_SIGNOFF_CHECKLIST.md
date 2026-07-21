# BE-0 Sign-off Checklist

This is the final backend planning checkpoint before BE-1 implementation. It
does not lift the master planning lock. Production code begins only after the
remaining proposed decisions are verified and the user explicitly approves the
transition to implementation.

## Contract coverage

| Area | Evidence | Status |
|---|---|---|
| Backend shape | Hosted Steward API; one authoritative EU deployment | Proposed BE-D015 |
| Steam authentication | Server-validated Steam session ticket; backend-only publisher credential | Proposed BE-D001 |
| World access | Flat membership; World-access invitation; creator-managed invite/revoke | Proposed BE-D002/003 |
| Multiplayer joining | Steam/game/session invitations remain outside Steward | Aligned |
| Control API | Versioned HTTPS/JSON; explicit command endpoints | Locked BE-D004 |
| Package transfer | Scoped resumable object transfer; size/hash verification | Specified |
| Immutable revisions | Verified candidate before publication; current head is mutable pointer only | Specified |
| Current-head commit | Expected-head compare-and-swap plus session generation | Specified |
| Reservation | One active writer; heartbeat; uncertainty; recovery; reclaim | Proposed BE-D005 |
| Abandon recovery | Continue-from-last-safe-state invalidates authority before availability | Proposed BE-D006 |
| Offline behavior | No new shared writer offline; active session may continue; candidate preserved | Specified |
| Security | BE-D014 baseline and threat model | Approved baseline |
| Geography | EU authority and EU-resident primary data/backups | Proposed BE-D015 |
| Schema/lifecycle | Logical records, invariants, migrations, retention, restore | Specified |
| Operations | Health, metrics, alerts, rollback, incidents, disaster recovery | Specified |
| Provider choice | PostgreSQL-compatible plus S3-compatible contract; named vendor deferred | Explicitly deferred |
| Package limits | 10 GiB state, 2 GiB environment, 64 MiB chunks, 30-day candidate/recovery retention | Provisional BE-D008; validate before BE-3 |
| BE-1 proof | Deterministic two-client simulation and failure matrix | Specified |
| Cross-workstream mapping | UI/runtime/backend state and action matrix | Drafted; UI/runtime sign-off required |

## Verification required before coding

The user/product owner must verify these choices:

1. Hosted Steward API is the intended backend shape.
2. BE-D001 through BE-D003 are accepted as the first-release identity/access
   model.
3. BE-D005 timing is accepted: 30-second heartbeat, 90-second uncertainty,
   15-minute reconnect grace, authorized-member reclaim after grace.
4. BE-D006 candidate-preservation behavior is accepted.
5. BE-D015 one-authoritative-EU deployment is accepted as the first-release
   geography and residency boundary.
6. Provider selection may remain deferred through BE-1 and be decided only after
   measured package, transfer, restore, and cost evidence.
7. BE-D008 limits are accepted as provisional safety defaults, not final
   commercial entitlements.
8. The shared contract agrees with the final UI-0 and AR-0 decisions.

## Implementation release gate

BE-1 may begin only after all of the following are true:

- UI-0 is complete or its remaining decisions are explicitly deferred;
- AR-0 is complete or its remaining decisions are explicitly deferred;
- the cross-workstream contract has no contradictory state/action meaning;
- this checklist is verified;
- the user explicitly says the master planning lock is lifted and BE-1 is
  approved;
- the implementation maps to BE-1 and the BE-1 acceptance matrix.

## First implementation slice

After approval, implement only the provider-free deterministic simulation in
[BE1_LOCAL_SIMULATION.md](BE1_LOCAL_SIMULATION.md):

- in-memory transactional records;
- deterministic clock and failure injection;
- World access checks;
- immutable transfer/publication behavior;
- acquire/heartbeat/uncertain/recovery/reclaim;
- expected-head commit;
- idempotency;
- two-client and stale-generation tests.

Do not add Steam integration, HTTP hosting, cloud SDKs, production credentials,
or infrastructure in the first slice.
