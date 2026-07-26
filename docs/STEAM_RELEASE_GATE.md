# Steam Release Gate

Reviewed: **2026-07-26**

Status: **V3-F FINAL EXTERNAL RELEASE GATE — DETERMINISTIC RELEASE SHAPE IS PREPARED; REAL STEAM/PUBLISHER/DEPOT/WINDOWS/GAME EVIDENCE REMAINS INTENTIONALLY UNCLAIMED.**

## Decision

Steward development does not need a real production AppID, publisher credential, depot ID, or Steam builder account in order to complete independent deterministic release work.

Those external Steam values are introduced only after the product owner decides Steward is otherwise good enough to run the real release-candidate acceptance batch.

V3 has already removed the deterministic blockers that previously sat in front of that decision:

- V3-A packages the non-secret production API URL + expected AppID + Web API identity beside the Desktop;
- V3-B produces exact self-contained Release `win-x64` depot content with external byte/SHA-256 evidence;
- V3-C derives the matching public Backend.Api AppID/identity fragment from the same release inputs while keeping the publisher credential server-secret and Friends Build disabled;
- V3-D keeps release claims tied directly to existing adapter capabilities/evidence rather than inventing a second support taxonomy.

The remaining gate is therefore deliberately real:

```text
real Steam application/depot identity
+ real publisher credential
+ real SteamPipe upload/install/update
+ real Steam tickets
+ real Windows installations
+ real advertised game/network lifecycle
= release acceptance
```

No deterministic CI substitute is allowed to relabel that evidence as complete.

## Steam owns install and update

V3 does **not** add a Steward installer/updater requirement before this gate.

SteamPipe is the release transport/update system. Steward supplies the exact content root; Steam owns:

- depot upload;
- depot manifest generation;
- BuildID generation;
- branch distribution;
- installation;
- patch/update delivery;
- rollback/build selection through Steamworks.

Steward's deterministic evidence remains external to installed depot content.

Current Valve documentation rechecked for this gate:

- `https://partner.steamgames.com/doc/sdk/uploading` — SteamPipe `ContentRoot`, `FileMapping`, depot manifests, BuildIDs, beta branches, upload flow;
- `https://partner.steamgames.com/doc/api/steam_api?l=english` — `SteamAPI_Init()` returns success/failure, not an AppID;
- `https://partner.steamgames.com/doc/api/isteamutils?language=english` — `ISteamUtils::GetAppID()` returns the AppID of the current process;
- `https://partner.steamgames.com/doc/features/auth?language=english` — `GetAuthTicketForWebApi` + secure-backend `AuthenticateUserTicket` flow.

## Pre-gate deterministic checkpoint

Do not open the real Steam gate until the exact stacked V3 release line is qualified and there is no known deterministic release blocker.

The current qualified sequence entering this gate is:

```text
#133 V3-A release configuration
-> #134 V3-B exact depot content
-> #135 V3-C matched public backend Steam identity
-> #136 V3-D action-specific release claim contract
```

The real run must use a later exact qualified head containing that ancestry; never reconstruct the release candidate from unrelated PR numbers or unqualified local files.

## External inputs required to open the gate

The gate requires actual values/resources, not placeholders:

### Steam

- real Steward AppID;
- real Windows depot ID;
- Steamworks partner/builder account authorized for that app/depot;
- real Web API publisher credential associated with the Steward AppID;
- one chosen `GetAuthTicketForWebApi` service identity;
- private/beta branch for release-candidate testing before the default/public branch is changed.

### Steward deployment

- real public HTTPS Backend.Api URL;
- real PostgreSQL;
- real private S3-compatible object storage;
- exact reverse-proxy trust matching the actual deployment topology;
- production Friends Build authentication disabled;
- provider secret injection for `Steam__PublisherApiKey`.

### Real clients

- at least two independent Windows installations/users/Steam accounts;
- do not copy Steward device settings from one PC/user to another;
- the advertised game/action set installed on the machines needed for the acceptance run;
- representative real Worlds rather than synthetic fixture-only state.

## One-pass V3-E + V3-F acceptance batch

Do not pay the real Steam/deployment setup cost twice. Once the gate is opened, batch the remaining V3-E Windows observations into the same V3-F Steam run.

### 1. Generate the exact release candidate

Manually dispatch the existing **Windows acceptance package** workflow from the exact qualified release-candidate branch/head with real public values:

```text
steam_api_base_url      = https://<production-or-release-candidate-steward-api>/
steam_app_id             = <real Steward AppID>
steam_web_api_identity   = <real GetAuthTicketForWebApi service identity>
steam_version            = <exact release-candidate product version>
```

The resulting release artifacts are authoritative inputs to the rest of the batch:

```text
steward-v3-steam-depot-<version>
    exact self-contained Windows ContentRoot

steward-v3-steam-depot-evidence-<version>
    acceptance-build.json
    backend-steam-auth.public.json
```

Before upload, verify the evidence again:

- every depot file has the recorded byte length + SHA-256;
- `steward-steam-release.json` is inside depot content;
- `acceptance-build.json` is outside depot content;
- client expected AppID equals `backend-steam-auth.public.json` `Steam__AppId`;
- client Web API identity equals `backend-steam-auth.public.json` `Steam__Identity`;
- `FriendsBuild__Enabled=false`;
- no `steward-friends-build.json`;
- no `steam_appid.txt`;
- no `.pdb` files;
- no `steam_api64.lib`;
- no publisher key/ticket/session/object-storage/database secret appears in either artifact.

Do not rebuild the content manually after qualification. If any release byte changes, create and qualify a new release candidate.

### 2. Deploy the matching production-auth backend

Use the generated public backend fragment as the exact non-secret Steam identity source:

```text
Steam__AppId=<generated value>
Steam__Identity=<generated value>
FriendsBuild__Enabled=false
```

Inject separately as provider secrets/configuration:

```text
Steam__PublisherApiKey=<real secret>
ConnectionStrings__Steward=<real secret>
ObjectStorage__AccessKeyId=<real secret>
ObjectStorage__SecretAccessKey=<real secret>
```

Keep the existing provider-neutral object-storage/database/cleanup configuration and the exact reverse-proxy trust required by the actual topology.

Require before Steam-client acceptance:

```text
GET /health/live  -> 200
GET /health/ready -> 200
```

Do not put the publisher key into the depot, GitHub workflow inputs/artifacts, screenshots, issue text, or acceptance notes.

### 3. Upload the exact content root through SteamPipe

Use the current Steamworks SDK/SteamPipe builder outside Steward's product source tree.

The app/depot script should map the already-qualified depot content as the `ContentRoot`; it must not create a second copy/transformation pipeline for Steward files.

Conceptual mapping:

```text
AppID      = real Steward AppID
DepotID    = real Windows depot ID
ContentRoot= exact steward-v3-steam-depot-<version> directory
FileMapping LocalPath="*" -> DepotPath="." recursively
```

A SteamPipe preview build may be used first to inspect the mapped file list. The real upload must then use the same exact content root.

Record without secrets:

- Steward Git commit SHA;
- Steward product version;
- SHA-256 evidence file;
- AppID;
- depot ID;
- generated Steam depot manifest ID;
- generated Steam BuildID;
- test branch name.

Do not set the public/default branch live merely to test the candidate. Use an appropriate private/beta release-candidate branch until acceptance succeeds.

### 4. Install through Steam — do not sideload the candidate

PC A and PC B must install/update Steward through the Steam client from the uploaded test build.

For the production-path proof:

- no `STEWARD_*` routing/authentication environment variables are set;
- no adjacent Friends Build configuration exists;
- no `steam_appid.txt` exists in the installed depot;
- each installation retains its own Steward installation/device identity;
- the visible Steward window version matches the release-candidate version;
- installed product files correspond to the expected Steam build.

Launching the extracted CI artifact directly is useful only for deterministic package inspection. It does not replace installation/launch through Steam.

### 5. Prove the genuine Steam identity boundary

On each real installation:

```text
launch Steward through Steam
-> SteamAPI_Init() succeeds
-> SteamUtils.GetAppID() == expected AppID from steward-steam-release.json
-> GetAuthTicketForWebApi(expected identity)
-> wait for the real ticket callback
-> send ticket to Backend.Api
-> Backend.Api AuthenticateUserTicket succeeds using the real publisher credential
-> backend returns the verified Steam identity
-> normal Steward session is issued
```

Failure of the actual AppID equality check is a release failure. Do not bypass it by changing the expected package value on the machine.

Failure of Steam verification is a release failure. Do not enable Friends Build or another static token as a fallback.

### 6. Prove two independent Steward installations

PC A and PC B must authenticate independently and see consistent shared World/access state.

At minimum prove:

- distinct Steward installation IDs;
- same authenticated user receives stable identity on relaunch;
- invitations/access work between the two real Steam identities;
- shared World list/state agrees after refresh/restart;
- a competing writable session is rejected rather than creating a branch.

### 7. Run the advertised game/action acceptance

Release claims are action-specific, not catalog-wide.

For every game/action intended to be advertised at release, run the empirical gate required by that adapter.

Factorio remains the first reference because it currently exposes the complete automatic Start/Host/Join action set.

Minimum Factorio release proof:

```text
PC A imports/creates or selects a real World
-> exact environment Ready
-> A Hosts
-> published Host presence becomes genuinely reachable over the real Internet path
-> PC B Join becomes Ready and launches through the adapter
-> real gameplay changes World state
-> hosted session ends through the proven safe boundary
-> Steward captures exact updated state
-> immutable upload completes
-> expected-head commit advances canonical state
-> PC B later acquires and continues the resulting revision
-> PC B commits another revision
-> PC A observes/continues that returned current state
```

Use the current exact direct/trusted-one-proxy address path. Do not add STUN/UPnP/relay/public-IP machinery unless this real run demonstrates the concrete need.

Palworld/7DTD/PZ/other adapters may be named only for the exact slices/actions whose required empirical gates have actually passed. Catalog presence alone is not a full-play claim.

### 8. Batch V3-E real Windows observations

While the real Steam build is installed on the actual Windows machines, execute the deferred UI/OS observations instead of arranging another acceptance session later.

Record:

- keyboard Tab/Shift+Tab traversal and visible focus across primary surfaces;
- Narrator or another real UI Automation client announcing primary labels and live regions;
- mixed-DPI monitor movement/resizing under `PerMonitorV2` with no clipped/unreachable primary controls;
- tray Open/Quit behavior;
- close-window -> tray behavior;
- Quit guards during active/unresolved responsibility;
- normal relaunch after Steam/Windows restart;
- suspend/logoff behavior where it can affect an active Steward responsibility;
- actual package size and startup time;
- representative capture/restore/upload/download timing;
- any user-visible operational friction.

A failure here creates a narrow V3-E defect. Do not respond by adding subjective UI polish unrelated to the observed failure.

### 9. Prove Steam update behavior

Steam owns updates, so Steward must prove the Steam path rather than adding its own updater.

After the first candidate is installed and accepted enough to continue:

```text
qualify a second exact candidate
-> upload second exact ContentRoot as a new Steam build
-> move the private/beta test branch to the new BuildID
-> PC A/B receive the update through Steam
-> Steward still launches under the expected AppID
-> visible product version changes to the new candidate
-> local Steward data / unresolved-responsibility rules remain intact
-> authentication and World access still work
```

Record the old/new BuildIDs and observed download/update behavior. Do not infer update correctness from SteamPipe upload success alone.

### 10. Recovery/failure checks while the real boundary exists

After the happy path works, reuse the expensive real setup to exercise the highest-value failures that cannot be fully represented by isolated CI:

- backend connectivity loss during an active writable session;
- lost successful commit response / Waiting to sync recovery;
- Steward restart with preserved responsibility;
- second-writer attempt;
- stale Host presence / failed Join attempt;
- game/environment update mismatch;
- clean recovery after Steam client/Windows restart where applicable.

Use `V2_REAL_ACCEPTANCE_BATCH.md` and `DEFERRED_EMPIRICAL_TESTS.md` for adapter-specific real-machine gates that remain relevant. Do not repeat retired experiments.

## Evidence record

Record the final batch without secrets:

- exact Steward commit SHA and product version;
- `acceptance-build.json`;
- public backend auth fragment;
- real AppID and depot ID;
- Steam depot manifest ID + BuildID(s);
- test branch used;
- backend deployment commit/provider/region;
- health results;
- hashed/redacted installation IDs if needed;
- World/revision/generation IDs;
- package sizes/timings;
- game Host/Join/readiness/save evidence;
- recovery outcomes;
- Windows accessibility/DPI/tray observations;
- update results;
- concrete defects/friction found.

Never record:

- publisher API key;
- Steam auth tickets;
- Steward session/refresh credentials;
- Friends Build bootstrap credentials;
- database/object-storage secrets;
- private World contents unless explicitly required as a controlled test fixture.

## Pass/fail rule

The gate passes only when every release claim being made has corresponding real evidence and no release-blocking defect remains unresolved.

A failure is useful evidence:

```text
real gate fails
-> record exact failure
-> implement the smallest correction
-> qualify a new exact candidate
-> repeat only the affected real evidence plus any dependency chain it invalidates
```

Do not solve an imagined broader problem.

Examples:

- public address wrong behind the chosen proxy -> fix only the measured address/proxy boundary;
- Steam AppID mismatch -> fix release/package configuration, not authentication policy;
- updater problem -> there is no Steward updater; investigate Steam depot/build/update configuration;
- adapter cannot safely stop -> capability stays unavailable until the game-owned lifecycle is proven;
- state-only adapter cannot Start -> that is not a defect unless release material incorrectly promised Start.

## Non-negotiable constraints

Production Steam acceptance may not be replaced by:

- a hard-coded SteamID;
- a static development token;
- an authentication-disable switch;
- Friends Build fallback;
- a fake publisher key;
- a mocked ticket presented as live evidence;
- `steam_appid.txt` in depot content;
- launching only a sideloaded build instead of the Steam-installed candidate;
- manually copying Worlds between PCs to make a handoff appear successful;
- force-moving canonical state or deleting recovery evidence to resolve ambiguity.

Such mechanisms may exist only inside isolated automated tests where they model a specific boundary. They may never become a release path.

## Completion boundary

When this gate passes:

```text
V3 deterministic release shape
+ V3-E real Windows acceptance
+ V3-F real Steam identity/distribution/update
+ real evidence for every advertised game/action
= technically release-accepted Steward candidate
```

At that point Steam Early Access publication is a product/business decision rather than a missing engineering architecture milestone.

Until the external Steam/provider/machine inputs exist, do not manufacture more generic release code merely to create activity. Continue only on independent work justified by a concrete defect, game evidence, or a later explicitly chosen product goal.
