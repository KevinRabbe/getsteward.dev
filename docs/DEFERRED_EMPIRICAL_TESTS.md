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

## Desktop — real Windows UI acceptance

**Frozen product state:** CI proves compilation, packaging, deterministic accessibility metadata, live-region event wiring, and the per-monitor DPI manifest. CI does not claim that a real assistive technology or monitor transition has been observed.

**Empirical questions:**

1. Does a packaged Steward Desktop build instantiate its neutral `DesktopText` resources successfully on a real Windows machine?
2. Does keyboard focus remain visibly identifiable across the main World list, primary actions, import surface, access dialogs, and tray interaction?
3. Do Windows UI Automation clients/Narrator receive the intended control names and `LiveRegionChanged` announcements?
4. Does the `PerMonitorV2` declaration behave correctly while moving Steward between monitors with different scaling factors?

**Acceptance evidence:**

- Packaged Desktop launches without a `MissingManifestResourceException` and shows the expected neutral English action labels.
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

## Recording new deferred tests

Add a new section when deterministic work reaches an empirical boundary. State:

- the exact unknown;
- the capability/assumption that remains frozen;
- the minimum reproducible setup;
- observable pass/fail evidence; and
- the exact condition under which Steward may promote the capability.

Do not write “test manually later” without those details.
