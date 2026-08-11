# Steam Closed-Beta Setup

Status: **CURRENT OPERATOR RUNBOOK for the peer closed-beta RC.**

This runbook prepares Steamworks so two real tester accounts can install and launch the **same private Steward AppID/build** used by physical qualification issue #349.

It does not change Steward authority or introduce a central backend.

## Why use the main Steward AppID for this test

Valve currently recommends Steam Playtest for broader closed-beta programs. Steam Playtest uses a separate child AppID, which is useful when scale/isolation matters.

For the current two-account Steward RC qualification, use the real private Steward AppID instead so the test proves the exact AppID embedded in `steward-steam.json` and observed by Steamworks at runtime.

Valve's Release State Override (beta testing) package/keys are intended for small pre-release beta tests and allow an unreleased application to be playable by the accounts that activate those keys.

After this exact-AppID qualification passes, a later larger external beta may use Steam Playtest if desired.

## Valve documentation used by this runbook

Current official Steamworks references:

- **Testing On Steam** — Steam Playtest recommendation and Release State Override testing path;
- **Steam Keys** — Release State Override keys for small pre-release beta tests;
- **Packages** — Release Override Beta Testing package semantics;
- **Branches (Betas)** — private/password-protected branch behavior;
- **Uploading to Steam** — SteamPipe, build account permissions, depots, launch options, build scripts;
- **Builds** — setting uploaded builds live on branches.

Always prefer current Steamworks App Admin wording if Valve changes labels.

## Fixed Steward beta baseline

Physical RC branch:

`release/steward-peer-closed-beta-rc1`

Exact qualified SHA:

`9acf25f7696dfe9056028e4201ca380a94883f0e`

Do not move or rebuild the RC from a later source branch during one #349 qualification run.

## Required Steamworks values

Record these outside the repository:

```text
STEWARD_APP_ID=<actual private Steward AppID>
STEWARD_WINDOWS_DEPOT_ID=<Windows content DepotID>
STEWARD_BETA_PACKAGE_ID=<Release State Override/Beta Testing package>
STEWARD_BETA_BRANCH=closed-beta-rc1
STEWARD_BETA_BRANCH_PASSWORD=<private branch password>
```

Do **not** commit:

- Steam account passwords;
- Steam Guard codes;
- publisher credentials;
- branch passwords;
- product keys;
- Web API publisher keys.

The normal Steward peer product does not need a Steward API URL or Web API identity.

## 1. Configure the application

In Steamworks App Admin for the real Steward AppID:

1. Open **General Installation**.
2. Set a stable install directory, for example `Steward`.
3. Add a Windows launch option whose executable is:

   `SharedWorlds.Desktop.exe`

4. Do not add a leading slash/dot to the executable path.
5. Save the change.

Steward is currently win-x64 for this physical kit.

## 2. Configure one Windows depot

In the application's SteamPipe/Depots configuration:

1. Create or reuse one Windows/base-content depot.
2. Record its DepotID.
3. Ensure the depot is included in the packages used by:
   - the build/developer account as needed;
   - the Release State Override/Beta Testing tester package.
4. Publish the Steamworks configuration changes.

A common Steam installation failure is an app/package that does not actually include the depot containing the executable. Verify the package/depot relationship explicitly.

## 3. Prepare tester access

For this **two-person exact-AppID test**, use the app's Release State Override/Beta Testing package.

1. Verify that package includes the Steward application and required Windows depot.
2. Request two Release State Override keys.
3. Give one key to tester A and one key to tester B through a private channel.
4. Each tester activates the key on their intended Steam account.
5. Verify the application appears in each account's Steam Library even though Steward is unreleased.

Do not use Developer Comp keys as ordinary external tester distribution.

Release State Override keys are beta/test access credentials and must not be sold.

## 4. Create the private RC branch

In App Admin -> Builds/Branches:

1. Create branch:

   `closed-beta-rc1`

2. Give it a clear description such as:

   `Steward peer closed beta RC1`

3. Set a branch password **before** making the build live if branch contents must remain private.
4. Keep the password outside the repository.

A branch password protects branch selection/content; it does not grant application ownership. Tester ownership comes from the beta package/key above.

## 5. Build the exact Steward kit

Check out exactly:

`release/steward-peer-closed-beta-rc1`

On a Windows build machine with the required .NET SDK/PowerShell tooling:

```powershell
./tools/build-steam-peer-two-pc-kit.ps1 `
  -SteamAppId <STEWARD_APP_ID> `
  -Version 3.0.0-peer-beta.rc1 `
  -OutputDirectory C:\steward-beta\rc1
```

The resulting directory contains:

```text
C:\steward-beta\rc1\
  product\
  acceptance-tools\
  START-HERE-STEAM-PEER-TWO-PC.txt
  LIST-PEER-WORLDS.cmd
  CAPTURE-PEER-EVIDENCE.cmd
  peer-test-kit.json
```

Only `product\` is Steam application content.

`acceptance-tools\` and the test procedure remain external test evidence tooling and must not be mixed into the product depot merely to make testing convenient.

Before uploading, verify:

```powershell
C:\steward-beta\rc1\acceptance-tools\SharedWorlds.PeerWorldProbe.exe `
  --verify-package `
  --package-root C:\steward-beta\rc1\product
```

Required result:

- package verification succeeds;
- `steward-steam.json` contains the real AppID;
- no `steward-steam-release.json`;
- no `steward-friends-build.json`.

## 6. Prepare SteamPipe

Use the current Steamworks SDK `tools\ContentBuilder` directory.

Valve recommends a dedicated Steam build account with only the permissions needed to edit app metadata and publish app changes.

Do not store that account password in this repository.

For one Windows depot, a minimal build script can map the complete Steward `product\` directory into the depot root.

Example `app_build_steward_rc1.vdf`:

```text
"AppBuild"
{
    "AppID" "<STEWARD_APP_ID>"
    "Desc" "Steward peer closed beta RC1 - 9acf25f7"
    "ContentRoot" "C:\steward-beta\rc1\product"
    "BuildOutput" "C:\steward-beta\steampipe-output"

    "Depots"
    {
        "<STEWARD_WINDOWS_DEPOT_ID>"
        {
            "FileMapping"
            {
                "LocalPath" "*"
                "DepotPath" "."
                "Recursive" "1"
            }
        }
    }
}
```

This maps every file already selected by Steward's release packaging boundary. Do not independently curate a second file list unless a real SteamPipe requirement proves it necessary.

## 7. Upload the build

From the Steamworks SDK ContentBuilder `builder` directory:

1. Start `steamcmd.exe`.
2. Log in with the dedicated Steamworks build account interactively.
3. Complete Steam Guard authentication if requested.
4. Run the application build using the VDF above.

Equivalent SteamCMD operation:

```text
run_app_build <path-to-app_build_steward_rc1.vdf>
```

After the upload completes, Steamworks assigns a BuildID. Record it in issue #349.

Do not put the build account password into a committed `.vdf`, `.cmd`, `.ps1`, issue, log attachment, or GitHub secret unless a later deliberate automated deployment design requires it.

## 8. Set the build live on the private branch

In App Admin -> Builds:

1. Find the uploaded BuildID.
2. Select `closed-beta-rc1` as the target branch.
3. Preview the change.
4. Set the build live on that branch.
5. Confirm the branch still has its password protection.

Do not set another build live on `closed-beta-rc1` while physical issue #349 is in progress.

The purpose of the branch is to keep both PCs on one exact Steam-delivered build.

## 9. Install on tester A and B

On both Steam accounts:

1. Confirm the Release State Override key has been activated.
2. Install Steward through Steam.
3. Open Steward -> Properties -> Betas / Game Versions & Betas.
4. Enter the private branch password when required.
5. Select `closed-beta-rc1`.
6. Let Steam finish updating.
7. Launch Steward **through Steam**.

Do not start one tester from a loose local build and the other from Steam; #349 requires the same Steam-delivered AppID/product baseline.

## 10. Verify the installed product before gameplay

The evidence probe lives outside the Steam depot. Copy/use the `acceptance-tools` directory from the same RC kit on each PC without altering the Steam-installed product directory.

Run package verification against each installed Steward directory.

Then record on both PCs:

- product commit SHA;
- acceptance manifest SHA-256;
- Steam AppID;
- Steam BuildID/branch;
- tester Steam account identity;
- machine name.

Both PCs must agree on the package identity before proceeding to World testing.

## 11. Run issue #349 exactly

Proceed with the physical sequence already defined in #349:

```text
Local World on A
-> Share generation 1
-> add B from Steam friends
-> A Host
-> private invite
-> B Join/bootstrap
-> exact A/B head evidence
-> A -> B handoff
-> generation 2 exact head evidence
-> stop/restart
-> generation remains 2
-> live Remove A
-> re-add/catch-up
-> authoritative Leave World
```

Do not change Steam build, branch, product bytes, AppID, game version, or test World mid-run.

## 12. Record Steamworks evidence

Attach/record in issue #349:

```text
Steward AppID
Windows DepotID
Beta package type: Release State Override
Steam branch: closed-beta-rc1
Steam BuildID
RC git SHA
product acceptance manifest SHA-256
A/B package probe outputs
```

Do not paste:

- product keys;
- branch password;
- Steam credentials;
- publisher secrets.

## Fail conditions before World testing

Do not begin #349 if any of these occur:

- tester cannot own/launch the unreleased app;
- required depot is missing from tester package;
- Steam reports invalid content configuration;
- `SharedWorlds.Desktop.exe` is not the configured launch executable;
- the build live on `closed-beta-rc1` is not the intended RC;
- A and B receive different product bytes;
- installed `steward-steam.json` contains the wrong AppID;
- normal package contains legacy remote/Friends Build configuration.

Fix Steamworks/package setup first. These are distribution/setup failures, not peer World authority failures.

## Why not Steam Playtest for this exact run?

Steam Playtest is a strong choice for a larger external beta and Valve recommends it for closed testing generally.

For issue #349, however, its separate child AppID would change the exact Steam identity namespace/AppID being qualified. The goal of RC1 is to test the real Steward AppID with two controlled accounts.

After RC1 passes, opening a Playtest app for broader testing is compatible with the product architecture; build it deliberately with the Playtest AppID and treat it as a separate beta distribution target rather than evidence for the main AppID.

## No Steward backend setup

This Steamworks runbook intentionally contains no instructions for:

- deploying Backend.Api;
- PostgreSQL;
- S3/object storage;
- Caddy/proxy deployment;
- Friends Build credentials;
- Web API ticket identity for normal operation.

If normal RC1 Host/Join/access/handoff/Leave requires any of those, physical qualification fails and the observed peer-product defect must be fixed rather than restoring the old central topology.