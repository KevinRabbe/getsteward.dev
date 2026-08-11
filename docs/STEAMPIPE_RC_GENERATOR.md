# SteamPipe RC Generator

Status: **CURRENT OPERATOR HELPER for the peer closed-beta RC.**

Use this helper after building the exact two-PC RC kit and before running `steamcmd`.

It removes the need to hand-edit SteamPipe VDF files while keeping Steam account credentials, Steam Guard codes, branch passwords, product keys, and branch promotion outside the repository/tool.

## Input boundary

The generator accepts only:

- the real Steward Steam AppID;
- the Windows DepotID;
- the exact `product/` directory from the RC kit;
- an external output directory for generated SteamPipe files;
- an optional external SteamPipe build-output directory;
- an internal build description.

It does **not** accept credentials or `SetLive` configuration.

## Before generating anything

The helper fails closed unless the supplied product is the ordinary peer package:

- `acceptance-build.json` must be the expected schema-2 Steward package manifest;
- every manifested file must exist with the exact byte length and SHA-256 recorded by the package build;
- no extra/unmanifested product file may exist;
- `SharedWorlds.Desktop.exe` must exist;
- `steward-steam.json` must contain exactly `schemaVersion` + `steamAppId`;
- the embedded AppID must equal the requested AppID;
- `steward-steam-release.json` must be absent;
- `steward-friends-build.json` must be absent;
- generator/build-output directories must not overlap the immutable product tree in either direction.

The generator validates all of this **before** clearing/creating its output directory.

## Command

Example for RC1:

```powershell
./tools/new-steam-peer-beta-steampipe.ps1 `
  -SteamAppId <STEWARD_APP_ID> `
  -WindowsDepotId <STEWARD_WINDOWS_DEPOT_ID> `
  -ProductDirectory C:\steward-beta\rc1\product `
  -OutputDirectory C:\steward-beta\rc1-steampipe `
  -BuildOutputDirectory D:\steward-steampipe-cache\rc1 `
  -BuildDescription 'Steward peer closed beta RC1 - 9acf25f7'
```

The generated directory contains:

```text
app_build_<APPID>_preview.vdf
app_build_<APPID>_upload.vdf
steampipe-rc-input.json
```

Both VDFs map the complete already-qualified `product/` directory into the selected Windows depot root with recursive `FileMapping`.

The preview VDF contains:

```text
"Preview" "1"
```

The upload VDF deliberately does not.

Neither file contains `SetLive`.

## Preview first

Run the preview VDF through the Steamworks SDK SteamPipe builder first:

```text
run_app_build <path-to-app_build_<APPID>_preview.vdf>
```

SteamPipe preview mode produces logs/file-manifest information without uploading the build. Verify that the mapped content is the expected Steward product before running the upload VDF.

Then run:

```text
run_app_build <path-to-app_build_<APPID>_upload.vdf>
```

Promotion of the resulting BuildID to the password-protected `closed-beta-rc1` branch remains an explicit Steamworks App Admin action described in `STEAM_CLOSED_BETA_SETUP.md`.

## Evidence

`steampipe-rc-input.json` records the non-secret local inputs that bound the upload configuration:

- Steam AppID;
- Windows DepotID;
- product commit SHA from `acceptance-build.json`;
- product manifest SHA-256;
- build description;
- preview VDF filename + SHA-256;
- upload VDF filename + SHA-256;
- product/build-output paths used by the build machine.

Record/attach the non-secret evidence needed by physical issue #349. Do not attach credentials, keys, or branch passwords.

## CI boundary

Windows Desktop acceptance executes this PowerShell helper against a synthetic byte-verifiable peer product. The acceptance coverage proves:

- PowerShell parsing/execution on Windows;
- successful preview/upload VDF generation;
- AppID + DepotID mapping;
- no automatic `SetLive`;
- evidence generation;
- tampered-byte refusal before output mutation;
- legacy migration configuration refusal before output mutation;
- no credential/promotion parameter surface.

This is structural/operator qualification only. It does not upload to Valve and does not replace the real SteamPipe + two-account/two-PC evidence required by #349.
