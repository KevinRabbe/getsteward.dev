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

## 7 Days to Die — V3 managed dedicated-server lifecycle

**Frozen product state:** 7 Days to Die does not advertise automatic host launch, automatic host stop, or automatic client join.

The earlier V3 sandbox-authority experiment is retired. Current V3 game documentation establishes `SandboxCode` as an explicit dedicated-server configuration input; `docs/V2_7DTD_SANDBOX_AUTHORITY.md` records that deterministic rule. Do not spend a real-machine test asking whether the canonical save bundle can make that required input irrelevant.

**Already deterministic/CI-proven:**

- Steam client and dedicated-server installation discovery.
- Exact dedicated-server Steam build inspection and verification.
- Canonical `Saves/<GameWorld>/<GameName>` plus matching `GeneratedWorlds/<GameWorld>` bundle capture/restore.
- Isolated adapter-owned user-data workspace preparation and recovery-preserving finalization.
- Bounded managed `serverconfig.xml` transformation for `GameWorld`, `GameName`, `UserDataFolder`, and `SaveGameFolder`.
- Exact V3 `SandboxCode` is an explicit World-specific reproduction input; Steward does not decode, regenerate, guess, or replace it from unrelated machine-local configuration.
- Current V3 server documentation states that an empty `TelnetPassword` makes the service interface listen only on local loopback.
- The managed-host transform therefore enables the built-in service interface on a bounded Steward-selected port and deliberately sets `TelnetPassword` to empty. There is no Steward management credential or authentication exchange to own.
- Current server documentation exposes the built-in local service interface and documents `shutdown` as the supported server-stop command; the shipped Windows launcher demonstrates the interface through local raw PuTTY/Telnet rather than a custom RCON protocol.

**Empirical questions:**

1. What observable log/process/network boundary proves the restored named World is fully ready for players rather than merely that the process or local management socket exists?
2. What exact minimal raw line framing does the current V3 local service interface accept for `shutdown`, and after that command does the actual long-lived server process exit reliably?
3. What observable boundary proves the final authoritative save is complete before Steward capture begins?
4. Does the resulting isolated `Saves/...` + `GeneratedWorlds/...` bundle capture every authoritative change needed for a second launch of the same updated World?

**Test setup:**

- Windows machine with the current 7 Days to Die client and dedicated server installed.
- One disposable known V3 World and one exact deliberately non-default `SandboxCode` supplied explicitly for that World.
- An adapter-owned isolated user-data workspace; never use the live player World as the writable test target.
- A managed `serverconfig.xml` with `TelnetEnabled=true`, one known test port, and an empty `TelnetPassword` so the game uses its documented loopback-only mode.
- Record the dedicated-server PID/process tree, management-port listeners, server log, and isolated World file timestamps/sizes before and after stop.
- For the first control observation, use an ordinary local raw/Telnet client and record only the bytes/text needed to determine accepted command-line framing. There is no password to record or redact.

**Acceptance evidence:**

- The server loads the intended restored `GameWorld`/`GameName` with the supplied non-default `SandboxCode` rather than silently substituting defaults.
- The management listener is observed on loopback only, matching the current documented empty-password mode; any contradictory real behavior is recorded as a game-version finding before Steward relies on it.
- A specific readiness signal is observed before the test client is considered able to join.
- The minimum accepted raw `shutdown` line framing is recorded without depending on cosmetic welcome/banner text.
- The actual long-lived dedicated-server process is identified and is not confused with a bootstrap/launcher process.
- `shutdown` reaches a clean terminal state without Steward killing the process.
- The final save/capture boundary is observable rather than inferred from a fixed sleep.
- The captured result restores and launches as the same updated World in a second disposable run.

**Promotion rule:** First turn the observed readiness/raw-shutdown/final-save trace into the smallest bounded local control/lifecycle implementation with regression tests. Then repeat this real scenario through Steward. Only after that second proof may 7DTD promote automatic Host/Stop. Join remains a separate capability proof.

## Palworld — native Join, REST exposure, and decoder-elimination boundary

**Frozen product state:** Palworld advertises automatic Host + Host Stop + exact game version. It does not advertise `AutomaticClientJoin`. `IManualDirectConnectProvider` is presentation-only: Steward publishes the Ready host address + native UDP game port `8211`, and the friend enters that endpoint in Palworld's own Join Multiplayer UI.

The managed Host/save/shutdown/capture lifecycle itself already has real-machine evidence. `WorldOption.sav` remains canonical read-only input; Steward has no writer/compressor path.

**Already deterministic/empirically proven:**

- Client and dedicated-server installation discovery and exact dedicated-server build verification.
- Managed Host owns a dedicated Palworld server session and publishes only native game endpoint `8211` as Join material; REST/admin control is never published.
- Host presence composition reaches `Starting` then `Ready` and clears before capture/commit.
- The Desktop can present the exact Ready `IP:8211` through the native manual-direct-connect contract without pretending Steward owns a client session.
- Real managed-host evidence already proves authenticated localhost REST info/settings, `POST /save`, `POST /shutdown`, full Palworld process-tree exit, byte-exact restoration of user-owned runtime inputs, and the final capture boundary.
- Production PlM handling is read-only decompression only. Oodle lookup is restricted to usable regular runtime files already installed beneath discovered Palworld roots; Steward does not download/copy/redistribute Oodle.

**Empirical questions:**

1. On two normal Windows PCs on separate real Internet connections, can PC B reach PC A's exact managed Palworld server through the published address + UDP `8211` and enter the intended World through Palworld's native Join Multiplayer UI?
2. Palworld's REST listener has been observed binding to `0.0.0.0`. Under the intended release/Windows Firewall environment, can a genuinely external LAN peer reach the ephemeral REST management port, or is the host-local management boundary effectively blocked as intended?
3. Can Palworld itself materialize every non-management effective `WorldOption.sav` setting into a disposable game-written `PalWorldSettings.ini`, allowing the shipped PlM/Oodle decode dependency to be deleted entirely?

**Two-network Join setup:**

- Exact qualified Steward build on PC A and PC B on separate real Internet connections.
- Real HTTPS backend using the intended deployment/proxy topology.
- Matching Palworld client environment on B and required Palworld Dedicated Server installation/build on A.
- One shared Palworld World with both identities authorized.
- Use the current managed Host path unchanged; do not manually edit presence or substitute another public-IP service.
- First try the host router/firewall as-is. If UDP `8211` is unreachable, record the failure boundary before changing anything; only then may one ordinary native-port router/firewall prerequisite be tested to distinguish configuration from missing Steward functionality.

**Two-network Join acceptance evidence:**

```text
PC A Host
-> backend presence becomes Starting then Ready
-> Ready exposes host-observed address + UDP 8211
-> PC B sees the same endpoint in Steward
-> PC B enters it in Palworld Join Multiplayer
-> PC B reaches the exact managed dedicated server / intended World
-> PC A Stop and Save
-> host presence disappears before capture/commit
-> captured World remains valid for the next session
```

Record whether Windows Firewall/router configuration was already sufficient and the exact boundary at which a failure occurs. Do not add `steam://connect`, UI automation, public-IP lookup, forwarded-header broadening, UPnP, STUN, relay, or generic NAT traversal before this proof demonstrates a concrete missing mechanism.

**REST exposure setup/evidence:**

- During a disposable managed Host session, record the ephemeral REST port selected by Steward without exposing the transient admin credential.
- From the host itself, prove Steward's existing localhost-authenticated REST lifecycle still works.
- From a second machine on the same reachable LAN, attempt only the minimum connection needed to determine whether the REST listener is externally reachable through Windows Firewall.
- Record listener binding, Windows Firewall behavior, and whether an external peer can reach the port at all.
- If the external peer is blocked, keep the current host-local management assumption and no extra firewall subsystem is needed.
- If it is reachable, treat that as measured security evidence and implement only the smallest owning mitigation before release; do not redesign the World lifecycle.

**Settings-materialization setup/evidence:**

Use the already-defined disposable acceptance probe shape from historical PR #3 rather than modifying production state:

```text
clone selected dedicated World to disposable World ID
-> redirect GameUserSettings.ini only to the clone
-> leave cloned WorldOption.sav byte-exact/intact
-> launch PalServer without Steward REST management
-> observe normal Shipping-process startup
-> request disposable process-group shutdown
-> wait for full Palworld process tree exit
-> capture game-written PalWorldSettings.ini
-> compare every non-management setting semantically against the read-only decoder oracle
-> restore exact original configuration only after Palworld exits
-> delete disposable World
-> prove canonical selected WorldOption.sav hash never changed
```

Exclude only Steward's known transient management overrides (`AdminPassword`, `RESTAPIEnabled`, `RESTAPIPort`) from the semantic equivalence requirement. Forced cleanup makes the translator proof fail.

**Promotion/elimination rule:**

- Passing the two-network proof validates the current manual native direct-connect release path; it does **not** add `AutomaticClientJoin` because Steward still does not own the client lifecycle.
- Resolve any demonstrated external REST exposure at the smallest owning Windows/game boundary before relying on host-local management isolation for release.
- If settings materialization proves semantic equivalence, delete the shipped PlM/Oodle production decoder/lookup path rather than preserving it. If it fails, keep the read-only decoder and prove a usable ordinary-user Palworld Oodle runtime path during V3-F instead of inventing a writer/compressor.

## Factorio Friends Build — direct Internet Host/Join reachability

**Frozen product state:** CI may prove the managed Host -> short-lived host-presence -> existing direct Join composition, but Steward does **not** claim that a friend on another real network can reach the host's Factorio UDP endpoint until this test passes.

**Already deterministic/CI-proven:**

- A shared writable reservation is acquired before managed Host launch.
- Factorio creates a private dedicated-server session on its standard UDP game port `34197` with a per-session random password.
- The adapter exposes only that game-owned standard port/password as managed-host connection material; loopback RCON remains on a separate ephemeral TCP port.
- The shared-session coordinator can publish `Starting`, then `Ready`, and refresh the same exact reservation generation through the existing reservation heartbeat rather than a second timer.
- Backend.Api can fill a missing Ready address from `HttpContext.Connection.RemoteIpAddress` while preserving an explicit supplied address.
- Direct Backend.Api deployments keep the raw authenticated connection peer. A one-proxy deployment may instead configure one exact `ReverseProxy__KnownProxyIp`; Steward then processes only one `X-Forwarded-For` hop from that trusted peer and ignores spoofed forwarding headers from unknown peers.
- Existing Join consumes the resulting `HostConnection` and launches Factorio with direct `--mp-connect <address>:34197 --password <token>` arguments.
- Ending the managed host removes host presence before state capture/commit continues.

The standard-port choice is deliberate. Factorio documents UDP `34197` as its normal server port and the ordinary router-forwarding target when NAT punching is insufficient. Steward therefore does not generate a different external networking problem on every hosted session merely to avoid a fixed game-owned coordinate.

**Empirical questions:**

1. In the selected real HTTPS topology, does the direct connection peer—or the one explicitly trusted proxy's rightmost client-address hop—produce the same public IPv4 that PC B can actually use to reach PC A?
2. Can PC B reach PC A's Factorio UDP `34197` through the real router/NAT/firewall topology without Steward adding UPnP, STUN, relay, Steam listing, or another traversal mechanism?
3. If direct/NAT-punched reachability fails, does one ordinary router forward of Factorio's documented UDP `34197` make the same managed host reachable without any Steward networking code change?
4. Does the existing direct Factorio client launch successfully join the exact managed private server using the published address, standard port, and per-session password?

**Test setup:**

- The exact qualified Friends Build ZIP on two normal Windows PCs on separate real Internet connections.
- A real HTTPS Steward backend using the intended deployment/proxy topology.
- If HTTPS terminates at one reverse proxy, configure only that exact proxy through `ReverseProxy__KnownProxyIp` and record the peer IP Backend.Api actually sees for the proxy.
- Factorio installed on both PCs with matching verified environment.
- One shared Factorio World with both Friends Build identities authorized.
- No manual edit of host-presence rows and no external public-IP helper added for the test.
- First run with the host router's existing configuration unchanged. Only if UDP `34197` is unreachable, optionally repeat after one explicit UDP `34197` forward to distinguish a normal router prerequisite from a missing Steward mechanism.

**Acceptance evidence:**

```text
PC A Host
-> backend presence becomes Starting then Ready
-> Ready contains the direct/trusted-proxy-derived address + UDP 34197 + A's random session password
-> PC B Join reads that Ready endpoint
-> Factorio on B reaches A's managed private server and enters the same World
-> A ends the hosted session
-> host presence disappears
-> Steward captures/uploads/commits the resulting World normally
```

Also record:

- the address returned by host presence;
- the address actually visible/reachable from PC B;
- whether the trusted proxy, if present, supplied that same address as its rightmost client-address hop;
- whether Factorio's direct/NAT-punched path worked with the router unchanged;
- whether a one-time UDP `34197` router forward was required and sufficient;
- whether Windows Firewall or another host firewall blocked UDP before Factorio could authenticate.

**Promotion rule:** Do not broaden proxy trust or add generic public-IP lookup, UPnP, STUN, relay, Steam-server listing, or other NAT traversal merely because such mechanisms exist. First run this exact two-network proof using the stable game-owned port and the already-qualified direct/exact-one-proxy address boundary. If it fails even with the smallest ordinary network prerequisite identified above, implement only the smallest mechanism that removes the measured remaining failure, then repeat the same acceptance sequence.

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
