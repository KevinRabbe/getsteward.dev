# Deferred empirical tests

Steward does not stop deterministic development merely because a question requires a real machine, a real game process, or release-only credentials.

Workflow:

1. Record the exact empirical question and acceptance evidence here.
2. Freeze the dependent capability or assumption at its last proven state.
3. Continue on independent code paths and CI.
4. Run deferred empirical work later as a deliberate batch.
5. Promote a capability only after the recorded evidence exists.

A deferred test is not evidence that the behavior works. It is an explicit boundary around what Steward does **not** currently claim.

## Project Zomboid — isolated dedicated-server lifecycle

**Frozen product state:** Project Zomboid does not advertise automatic host launch, automatic host stop, or automatic client join.

**Already deterministic/CI-proven:**

- Steam client and dedicated-server installation discovery.
- Exact dedicated-server Steam build inspection and verification.
- Exact Workshop content-manifest identity for configured Workshop items.
- Multiplayer World discovery.
- Canonical server bundle capture.
- Isolated workspace preparation and restore.
- Recovery-preserving finalization.

**Empirical question:** Can Steward launch a Project Zomboid dedicated server against only an adapter-owned isolated user-data tree, observe the real long-lived server process correctly, request a safe stop, and then capture the resulting authoritative server bundle without reading or mutating the player's live `%USERPROFILE%/Zomboid` tree?

**Test setup:**

- Windows machine with Steam Project Zomboid and Project Zomboid Dedicated Server installed.
- One known multiplayer server World with matching `Server/<name>.ini` and, if used, known Workshop items.
- A Steward-prepared isolated workspace containing only the restored server bundle.
- Record the live user-data tree before launch so unintended writes can be detected.

**Acceptance evidence:**

- Launch uses the intended Project Zomboid dedicated-server executable and an explicit isolated user-data/cache location.
- The actual long-lived server process is identified; a bootstrap/launcher exit is not mistaken for session completion.
- The server loads the restored named World rather than a default/new World.
- The live player Project Zomboid user-data tree receives no authoritative World/config/database writes from the managed session.
- Steward can request a safe server shutdown through a supported mechanism and can distinguish clean shutdown from forced termination.
- After shutdown, the isolated bundle contains the expected save/config/database changes and can be captured/restored byte-preservingly by the existing adapter path.
- A second launch from the captured result loads the same updated World.

**Promotion rule:** Only after this evidence exists may the adapter add the corresponding automatic launch/stop capability flags.

## 7 Days to Die — V3 sandbox configuration authority

**Frozen product state:** 7 Days to Die does not advertise automatic host launch, automatic host stop, or automatic client join. Steward can deterministically transform a bounded `serverconfig.xml` template for an isolated restored World, but it does not yet claim where an imported V3 World's authoritative sandbox gameplay configuration comes from.

**Already deterministic/CI-proven:**

- Steam client and dedicated-server installation discovery.
- Exact dedicated-server Steam build inspection and verification.
- Canonical `Saves/<GameWorld>/<GameName>` plus matching `GeneratedWorlds/<GameWorld>` bundle capture/restore.
- Isolated adapter-owned user-data workspace preparation and recovery-preserving finalization.
- Bounded managed `serverconfig.xml` transformation for `GameWorld`, `GameName`, `UserDataFolder`, and `SaveGameFolder`.
- An existing `SandboxCode` is treated as opaque game-owned data: Steward does not decode, regenerate, or silently replace it.

**Empirical question:** For an existing V3 World with deliberately non-default sandbox settings, does the canonical World bundle itself carry enough authoritative information to reproduce the exact effective sandbox configuration when the original `serverconfig.xml` is withheld, or must Steward capture a separate authoritative sandbox configuration input during import?

**Test setup:**

- Windows machine with the current 7 Days to Die client and dedicated server installed.
- One known V3 World configured with several deliberately non-default sandbox options.
- Record the original generated `SandboxCode` and the game's reported effective sandbox settings as an oracle only.
- Capture the World through Steward's existing canonical bundle path.
- Prepare an isolated restored copy from that bundle without supplying the original `serverconfig.xml` or another external sandbox configuration source.
- Use only a disposable copy for any game launch needed to observe the effective settings; the source World remains read-only.

**Acceptance evidence:**

- The test identifies whether the isolated World alone reproduces the same effective sandbox settings/code as the oracle.
- If the World is authoritative, the exact stable recovery source and reproduction rule are identified so they can be implemented and regression-tested without depending on unrelated machine-local configuration.
- If the World is not authoritative, the failure is recorded as the result: Steward must define and capture an explicit separate sandbox configuration input during import rather than infer it from a local dedicated-server `serverconfig.xml`.
- In either outcome, a second isolated reproduction using the chosen authority rule reports the same effective sandbox settings as the oracle.
- No capability flag is promoted merely because configuration authority is resolved; launch/readiness/safe-stop behavior still requires its own evidence.

**Promotion rule:** Do not wire imported-World hosting to an assumed `SandboxCode` source. First establish and implement the authoritative configuration rule above; automatic host/stop capabilities remain frozen until the corresponding real lifecycle evidence also exists.

## Desktop — real Windows UI acceptance

**Frozen product state:** CI proves compilation, packaging, neutral `DesktopText` resource resolution on Windows, deterministic accessibility metadata, live-region event wiring, and the per-monitor DPI manifest. CI does not claim that a real assistive technology or monitor transition has been observed.

**Empirical questions:**

1. Does keyboard focus remain visibly identifiable across the main World list, primary actions, import surface, access dialogs, and tray interaction?
2. Do Windows UI Automation clients/Narrator receive the intended control names and `LiveRegionChanged` announcements?
3. Does the `PerMonitorV2` declaration behave correctly while moving Steward between monitors with different scaling factors?

**Acceptance evidence:**

- Tab/Shift+Tab traversal reaches every interactive first-release action in a sensible order with a visible focus indicator.
- World rows, import rows, invitation rows, access rows, tray actions, and recovery actions expose meaningful accessible names.
- Status, environment-readiness, import-result, dialog-status, and responsibility changes are announced by a Windows UI Automation client/Narrator.
- At mixed DPI/scaling values, text remains readable, controls remain operable, and no primary action is clipped or unreachable after moving the window between monitors.

## Steam production acceptance — release-only credentials

**Frozen product state:** Real Steam publisher credentials and the production Steward Steam AppID are not development prerequisites and must not be replaced by fake credentials or an authentication bypass.

**Run only when Steward is entering Steam onboarding/release acceptance.**

**Acceptance evidence:**

- Real production Steward Steam AppID is configured through the intended release/deployment path.
- Real publisher-side Steam Web API credentials remain server-side only.
- Steam authentication tickets from real Steam clients are verified by the deployed Steward backend through the production verification path.
- Two distinct Steam accounts/installations can exercise the intended shared-World authority flow without a development auth bypass.
- The release/depot/update path is validated through Steam; Steward does not add a second self-updater.

## E8 — real game endurance and measured large Worlds

**Frozen product state:** CI proves synthetic large-transfer behavior and accelerated backend authority endurance. Steward does **not** currently claim that a real game process has run under managed ownership for 24 hours or that the synthetic 256 MiB object represents the largest real first-release World package.

**Already deterministic/CI-proven:**

- One 256 MiB S3-compatible immutable package transfers as four 64 MiB parts.
- The transfer survives disposal/recreation of the client/store boundary after only the first two parts are complete.
- Final streamed download reproduces the exact byte count and SHA-256.
- One PostgreSQL writable reservation survives 2,880 accepted heartbeats at 30-second logical intervals with the same session/generation and one reservation row.

**Empirical questions:**

1. What are the observed capture/package sizes and capture/restore durations for representative large real Worlds from each first-release adapter?
2. Can a real managed game/server session run for an extended period, stop through the adapter's proven safe boundary, and produce a valid capture without lifecycle drift or lost responsibility?
3. Do real large packages transfer, resume, verify, materialize, and restore within acceptable disk/network behavior on representative user hardware and production-like connectivity?

**Test setup:**

- Representative Windows machine(s) with the real game/adapter installation.
- A deliberately large but known-good World for the adapter under test.
- Steward-managed isolated workspace and exact environment.
- For shared transfer evidence, a production-like S3-compatible endpoint/network path; do not replace measured game packages with generated bytes for this test.
- Record package size, capture time, restore/materialization time, transfer time, peak temporary disk use, and final SHA/integrity result.

**Acceptance evidence:**

- Managed game/server process remains associated with the same Steward responsibility for the full test session.
- Safe stop/capture boundary is observed rather than inferred from timeout.
- Capture produces a package accepted by the existing adapter preflight/integrity path.
- Restore from that captured package launches/loads the same updated World.
- Interrupted transfer can resume and final bytes pass exact size/SHA verification.
- Disk preflight/temporary usage behaves within the established hard limits and does not consume unbounded space.

**Promotion rule:** Synthetic CI remains permanent regression evidence, but claims about real-game endurance, representative package sizes, or practical large-World transfer behavior require this measured evidence.

## E8 — EU production deployment and residency

**Frozen product state:** Steward's provider-neutral backend is designed for an EU deployment and E4-A proves the deployment mechanics independently of Steam credentials. The project does **not** currently claim production EU residency merely because the software can be deployed there.

**Empirical question:** Does the selected production/staging provider configuration actually keep Steward-controlled persistent backend data, backups, object storage, and operational logs in the intended EU deployment boundary?

**Test setup:**

- Real disposable or staging Steward API deployment in the selected EU region.
- Real PostgreSQL instance/cluster selected for Steward.
- Real private S3-compatible bucket/storage selected for Steward.
- Provider control-plane/account evidence for configured regions, backup locations, replication, and log storage.
- No Steam publisher credentials are required for the E4-A infrastructure portion of this test.

**Acceptance evidence:**

- API compute is deployed in the intended EU region.
- PostgreSQL primary storage and configured backups/replicas used by Steward are located in the intended EU boundary.
- Object storage bucket and any configured replication used by Steward are located in the intended EU boundary.
- Steward-controlled operational log/diagnostic sinks configured for the backend are located in the intended EU boundary.
- Health/readiness, schema initialization, restart, backup/restore, and transfer probes pass against those real resources.
- Any provider feature that can create non-EU copies is either disabled for Steward data or explicitly documented before a residency claim is made.

**Promotion rule:** Provider capability or marketing documentation alone is not acceptance evidence. Record the actual deployed resource configuration before marking EU residency/deployment verification complete.

## Recording new deferred tests

Add a new section when deterministic work reaches an empirical boundary. State:

- the exact unknown;
- the capability/assumption that remains frozen;
- the minimum reproducible setup;
- observable pass/fail evidence; and
- the exact condition under which Steward may promote the capability.

Do not write “test manually later” without those details.
