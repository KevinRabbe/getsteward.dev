# V2 Friends Build real acceptance batch

Status: **READY FOR REAL-MACHINE EXECUTION**

This runbook collapses the current V2 empirical gates into the smallest useful real-world batch.

The goal is not to test every feature independently. The goal is to pay the expensive setup cost once:

```text
one exact Friends Build
+ one real HTTPS backend
+ the same friend identities
+ the same Windows PCs
+ the same real networks
-> answer as many remaining V2 questions as possible
```

Do not add code merely to make this runbook pass. Run the current product first, record the first measured failure, and only then implement the smallest missing mechanism.

## What this batch can prove

One batch can collect real evidence for:

- V2-A download / extract / run;
- visible exact Friends Build version identity;
- V2-B Friends Build authentication and restart behavior;
- V2-C invitation + visible World membership;
- Factorio real Internet Host/Join reachability;
- Factorio safe Host -> capture -> commit -> next-host handoff;
- Palworld published native `IP:port` Join guidance;
- Palworld real Internet reachability and safe stop/capture;
- stale host-presence clearing after session end;
- real Windows keyboard/UI Automation/DPI observations while ordinary use is already happening;
- representative real package sizes and transfer/capture timings without creating a separate benchmark session.

7 Days to Die V3 lifecycle observation can use the same machine/backend installation window when the client and dedicated server are already available. Its old sandbox-authority experiment is retired: the exact opaque `SandboxCode` is already a required World-specific reproduction input. Automatic 7DTD Host/Stop remains intentionally unpromoted until the real lifecycle is observed and then reproduced through Steward.

Valheim is intentionally excluded until the released 1.0 save representation is rechecked.

## Do not batch these later release gates into V2

Do not block this batch on:

- production Steward Steam AppID;
- Steam publisher credentials;
- public Steam distribution;
- installer/updater infrastructure;
- payment/licensing;
- Valheim pre-1.0 state implementation;
- generic UPnP/STUN/relay/public-IP machinery.

## Required setup

### Backend

Use one real HTTPS Steward deployment with:

- real PostgreSQL;
- real S3-compatible object storage;
- Friends Build authentication enabled;
- at least the identities required for the participating PCs;
- the intended proxy/network topology rather than a localhost substitute.

If Backend.Api receives client HTTPS connections directly, leave the forwarded-address feature disabled and use the raw authenticated connection peer.

If one reverse proxy terminates HTTPS before Backend.Api, configure only the exact proxy peer Steward actually sees:

```text
ReverseProxy__KnownProxyIp=<exact proxy peer IP>
```

That qualified path processes only one `X-Forwarded-For` client-address hop from that exact trusted proxy. Do not enable broad forwarded-header trust or add a public-IP service for the batch.

Record only non-secret deployment identity:

```text
API host / deployment label
region
proxy/load-balancer shape
backend build/commit
trusted proxy peer IP configured? yes/no
```

Never copy private bootstrap credentials, access/refresh tokens, database credentials, object-store credentials, or game session secrets into the evidence log.

### Friends Build artifact

Produce one exact package through the normal V2 packager:

```powershell
./tools/v2-build-friends.ps1 `
  -ApiBaseUrl https://<real-backend>/ `
  -Version <version>
```

Keep together:

```text
Steward-<version>-win-x64.zip
Steward-<version>-win-x64.zip.sha256
```

Publish that same immutable ZIP to the private distribution path. Do not rebuild separately for each friend.

### Friend identities

Provision identities through the existing operator tool. Give each friend only their own plaintext bootstrap credential; keep the server configuration material separate.

Use at least two real identities:

```text
A = initial World owner / first host
B = invited friend / second host
```

For the full V2 handoff target, add:

```text
C = third host
```

### PCs

Minimum useful network run:

- PC A on real Internet connection A;
- PC B on a different real Internet connection B.

Preferred full handoff run:

- PC A;
- PC B;
- PC C on a third installation, preferably not sharing A's local network.

Record for each PC:

```text
Windows version
Steward visible version
Steward ZIP SHA-256
Steam installation location only when relevant to a defect
installed primary V2 games
installed dedicated-server tools
```

Do not collect hardware inventory unless a measured performance/resource issue makes it relevant.

## Batch evidence header

Create one evidence note for the entire run:

```text
Batch ID:
Date/time + timezone:
Steward version:
Steward ZIP SHA-256:
Qualified Steward head:
Backend deployment label:
Backend/proxy topology:
PC A network label:
PC B network label:
PC C network label (if used):
```

Use simple labels such as `home-a`, `home-b`, or `mobile-hotspot`; do not record public IP addresses unless the networking test specifically requires comparing the address Steward published with the address actually reachable from the other PC.

# Phase 1 — download, bytes, and visible build identity

On every participating PC:

1. Download the same ZIP through the actual private distribution path.
2. Verify SHA-256 against the published checksum.
3. Extract to a fresh directory.
4. Run `SharedWorlds.Desktop.exe` without a source checkout, Visual Studio, or repository-local environment variables.
5. Record the visible Steward window version.

Pass requires:

```text
ZIP hash matches
-> application launches
-> visible Steward version exactly matches ZIP version
-> no developer environment is required
```

A Windows security prompt, SmartScreen warning, ZIP extraction friction, or missing runtime dependency is evidence. Record the exact user-visible blocker instead of preemptively adding an installer.

# Phase 2 — private identity and restart

On PC A and PC B:

1. Enter each PC's own private Friends Build credential when prompted.
2. Reach an authenticated shared-World-capable desktop.
3. Close Steward normally.
4. Start it again.

Pass requires:

```text
first launch accepts valid private code
-> identity display name is correct
-> restart reconnects without re-entering the bootstrap code
-> no plaintext code is shown/stored in ordinary UI
```

Then test one invalid/revoked credential on a disposable identity or after intentionally revoking one test identity.

Pass requires rejection without weakening local World usability.

Do not intentionally expose or copy another friend's valid credential merely to test rejection.

# Phase 3 — invitation and minimal lobby

Use one shared World.

1. A owns/manages access.
2. A invites B through the Friends Build roster surface.
3. B accepts.
4. Both PCs refresh/reopen the World.
5. Confirm both member display names are visible.

If C participates, invite C now so the later handoff can use the same World.

Pass requires:

```text
no SteamID64 entry
-> invitation reaches the intended private identity
-> acceptance adds that identity through existing World authority
-> member names are visible
-> no extra party/friends/social model is required
```

Do not test chat, voice, online presence, friends graphs, or matchmaking; those are deliberately not V2 lobby requirements.

# Phase 4 — Factorio real network proof

## Preflight

On the intended host PC:

- Factorio installed;
- exact required environment reports Ready;
- hosting is allowed on that device.

On the joining PC:

- Factorio installed;
- exact required environment reports Ready.

Do not edit host-presence rows or supply an external public-IP helper.

If the HTTPS deployment uses the qualified one-proxy path, confirm `ReverseProxy__KnownProxyIp` names the exact peer Backend.Api sees before interpreting any published host address.

## First run: router unchanged

1. A selects the shared Factorio World and Hosts.
2. Record when Steward changes host presence from Starting to Ready.
3. Record the Ready address exposed to B.
4. Confirm the published game port is UDP `34197` and a per-session password is present internally for automatic Join.
5. B uses Steward Join.
6. Confirm B reaches the exact managed server and same World.

Pass path:

```text
A Host
-> Ready(address, 34197, session password)
-> B Join
-> B enters exact World
```

Record whether Windows Firewall prompted or blocked the server.

## If direct reachability fails

Do **not** add Steward traversal code during the test.

First identify whether the failure is the ordinary game/network prerequisite:

1. Keep the same Steward architecture.
2. Configure one router forward for Factorio UDP `34197` on A's network.
3. Repeat Host -> Ready -> B Join.

Record separately:

```text
published address
address actually reachable from B
direct peer or trusted-proxy-derived address source
router unchanged result
UDP 34197 forward result, if attempted
Windows Firewall state
```

Interpretation:

- direct works: no networking feature is missing;
- direct fails, one fixed 34197 forward works: Steward has a simple documented-network prerequisite, not proof that it needs NAT traversal;
- fixed forwarding still fails because the published address differs from the public address B can reach: investigate only the qualified direct/exact-one-proxy address boundary;
- correct address + reachable/forwarded port still fails: investigate the smallest measured Factorio-specific cause before adding generic networking machinery.

## Safe end and handoff

After B has visibly changed the World:

1. End A's hosted session through the normal Steward/game path.
2. Confirm host presence disappears before the session becomes fully Ready again.
3. Confirm capture/upload/commit completes.
4. On B, refresh and Host the resulting current revision.
5. A joins/continues the state returned by B.

Preferred full sequence:

```text
A Host
-> B participates
-> save/commit
-> B Host
-> C participates
-> save/commit
-> C Host
-> A participates
-> save/commit
-> A sees the returned current state
```

The decisive evidence is not merely that multiplayer worked. It is that nobody manually chose, copied, or renamed the newest save between sessions.

# Phase 5 — Palworld native manual Join proof

## Preflight

On the intended host PC:

- Palworld client installed;
- Palworld Dedicated Server / PalServer installed;
- exact environment Ready;
- device allowed to host.

A machine without PalServer is not a Palworld host candidate. Treat that negative installation evidence as the answer; do not probe impossible host states.

On B:

- Palworld client installed;
- exact required environment Ready.

B does not need PalServer merely to Join.

## Run

1. A Hosts the shared Palworld World.
2. Wait for Steward Ready host presence.
3. On B, record the exact native endpoint Steward displays.
4. Confirm it is `address:8211`.
5. B opens Palworld and enters that exact endpoint in Palworld's native Join Multiplayer IP:port field.
6. Confirm B reaches the exact managed World.

Pass path:

```text
A Steward Host
-> PalServer real readiness
-> Ready(address, 8211)
-> B sees address:8211
-> B uses native Palworld Join Multiplayer field
-> same World
```

Do not replace a failed result with `steam://connect`, keyboard/mouse automation, UPnP, STUN, relay, or another guessed mechanism during the batch.

Record:

```text
published address
whether UDP 8211 was already forwarded/reachable
Windows Firewall state
whether failure happened before the Palworld client reached the server
```

## Safe end

1. Make one visible multiplayer World change.
2. A uses Steward's normal safe host end.
3. Observe save/shutdown.
4. Confirm host presence clears.
5. Confirm capture/upload/commit completes.
6. Reopen/host the committed result and verify the visible World change remains.

Player-identity behavior must be recorded exactly as observed. Do not convert an identity limitation into a guessed migration algorithm during this run.

# Phase 6 — Windows UI evidence piggyback

Do not create a separate synthetic UI session if the normal friend run already traverses the real surfaces.

While completing Phases 1–5, record:

- visible keyboard focus while tabbing through the World list and primary actions;
- whether every action used in the run can be reached without a mouse;
- Narrator/UI Automation names for representative World rows, Join status, invitations, and recovery/status messages when assistive-technology evidence is being collected;
- any clipping/unreachable action observed on the real monitor scaling used by a participant.

If a mixed-DPI setup is available, move the Steward window between those monitors once during ordinary use and record the result.

Do not add subjective polish tasks unless the run exposes a concrete usability defect.

# Phase 7 — collect real size/time evidence for free

For every real capture/restore/transfer already occurring above, record when easy to obtain:

```text
game
World/package size
capture duration
upload duration
materialization/download duration
restore duration
```

Do not instrument a new telemetry system merely for this first batch. Existing logs/timestamps/manual observation are enough to establish whether a resource problem exists.

Only add deeper instrumentation if the measured result creates a real performance question.

# Optional same-window 7 Days to Die V3 lifecycle trace

Run this only when a current V3 test World and the dedicated server are already installed during the same machine/setup window. It is not required to complete the Factorio/Palworld Friends Build proof, and it is **not** a 7DTD Host capability acceptance run yet.

Do not rerun the retired sandbox-authority experiment. Start with one exact deliberately non-default `SandboxCode` supplied explicitly for the disposable World.

Prepare an isolated managed `serverconfig.xml` with:

```text
GameWorld / GameName = exact disposable restored World
UserDataFolder / SaveGameFolder = Steward-owned isolated paths
SandboxCode = exact explicit World code
TelnetEnabled = true
TelnetPort = known bounded test port
TelnetPassword = ""
```

The empty password is intentional: current V3 server documentation defines that mode as local-loopback-only, so no Steward management credential or password-authentication protocol is needed.

Observe only the remaining real boundaries:

1. Start the current dedicated server against the isolated config/workspace.
2. Record the actual long-lived server PID/process tree.
3. Confirm the management listener is loopback-only; if current V3 contradicts its documented empty-password behavior, stop treating the documentation as sufficient evidence and record the contradiction.
4. Identify the smallest observable readiness signal that proves the intended restored World is actually ready for players—not merely that the process or local management socket exists.
5. With an ordinary local raw/Telnet client, determine only the minimum line framing accepted for the documented `shutdown` command. Do not build product parsing around welcome/banner text.
6. Observe whether `shutdown` cleanly terminates the actual long-lived server process.
7. Record the final isolated World file/log boundary that proves the authoritative save is complete before capture.
8. Capture the resulting existing canonical `Saves/...` + `GeneratedWorlds/...` bundle.
9. Restore that captured result into a second disposable isolated run and confirm it loads as the same updated World.

A useful observation is:

```text
known explicit SandboxCode
+ isolated launch
+ observed readiness
+ local raw shutdown
+ clean process exit
+ observable final save
+ second launch from captured bytes
```

Do not promote automatic 7DTD Host/Stop from this manual trace alone. The trace exists to make the next implementation deterministic. After it exists, implement only the smallest bounded local control/lifecycle code, add CI regression coverage, and repeat this exact scenario through Steward before promoting capabilities.

# Failure handling during the batch

When a step fails:

1. Preserve the last safe Steward/game state.
2. Record the first observable failure boundary.
3. Record relevant logs/incident ID.
4. Do not stack speculative fixes while the exact cause is unknown.
5. Continue with independent phases when safe.

Use this classification:

```text
product defect
missing ordinary game/platform prerequisite
backend deployment/proxy issue
router/firewall reachability issue
game-specific empirical limitation
usability defect
unknown — evidence insufficient
```

A negative result is successful research when it removes uncertainty.

# Evidence record per scenario

For each scenario keep only:

```text
Scenario:
Steward version:
PC(s):
Game/build:
Starting state:
Action:
Observed result:
Expected result:
Pass/fail:
First failure boundary if failed:
Relevant incident/log reference:
Network prerequisite changed during retry? yes/no + exact change:
Conclusion / next smallest question:
```

Do not paste credentials or transient secrets.

# Batch stopping rule

The batch is complete when every attempted scenario has either:

- observable pass evidence; or
- one narrow recorded failure boundary that can drive the next implementation slice.

Do not keep testing cosmetic variants of a scenario once its uncertainty is removed.

The preferred development loop after this batch is:

```text
real failure
-> smallest causal explanation
-> smallest deterministic fix
-> CI regression proof
-> repeat only the failed real scenario
```

not:

```text
real failure
-> add generic subsystem
-> add fallbacks
-> add more abstractions
-> hope one of them fixes it
```

# V2 behavioral success signal

The strongest evidence remains:

> The group stops asking who has the newest save and starts opening Steward instead.
