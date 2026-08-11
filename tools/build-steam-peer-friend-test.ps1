[CmdletBinding()]
param(
    [string]$Version = '3.0.0-peer-friend-test.1',
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

$steamAppId = [uint32]480
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baseBuilder = Join-Path $repoRoot 'tools/build-steam-peer-two-pc-kit.ps1'
if (-not [IO.File]::Exists($baseBuilder)) {
    Fail "Required peer kit builder not found: $baseBuilder"
}

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version.Length -gt 64 -or
    $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    Fail 'Version must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts/steward-peer-friend-test-win-x64'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)

& $baseBuilder `
    -SteamAppId $steamAppId `
    -Version $Version `
    -OutputDirectory $output
if ($LASTEXITCODE -ne 0) {
    Fail "Base peer two-PC kit build failed with exit code $LASTEXITCODE."
}

$product = Join-Path $output 'product'
$tools = Join-Path $output 'acceptance-tools'
$manifestPath = Join-Path $product 'acceptance-build.json'
$probe = Join-Path $tools 'SharedWorlds.PeerWorldProbe.exe'
foreach ($required in @($product, $tools, $manifestPath, $probe)) {
    if (-not ([IO.Directory]::Exists($required) -or [IO.File]::Exists($required))) {
        Fail "Required friend-test input is missing: $required"
    }
}

# Valve documents steam_appid.txt as a development-only way to tell SteamAPI_Init which
# AppID to use when an executable is launched outside the Steam client. AppID 480 is
# Valve's SpaceWar SDK example application. This file must never be promoted into a
# real Steward Steam depot.
$developmentAppIdPath = Join-Path $product 'steam_appid.txt'
[IO.File]::WriteAllText(
    $developmentAppIdPath,
    "480`n",
    [Text.UTF8Encoding]::new($false))

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    Fail "Could not parse product acceptance-build.json: $($_.Exception.Message)"
}
if ($manifest.documentType -ne 'steward.e4-desktop-acceptance-build' -or
    [int]$manifest.schemaVersion -ne 2) {
    Fail 'Product acceptance-build.json is not the expected schema-2 Steward package manifest.'
}
if (@($manifest.files | Where-Object { [string]$_.path -eq 'steam_appid.txt' }).Count -ne 0) {
    Fail 'Base peer package unexpectedly already contains steam_appid.txt.'
}

$appIdFile = Get-Item -LiteralPath $developmentAppIdPath
$appIdEntry = [pscustomobject][ordered]@{
    path = 'steam_appid.txt'
    byteSize = $appIdFile.Length
    sha256 = (Get-FileHash -LiteralPath $developmentAppIdPath -Algorithm SHA256).Hash
}
$manifest.files = @($manifest.files) + $appIdEntry
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

# The old outer kit manifest was created before the development AppID file was added.
# Remove it rather than leave stale evidence; a friend-test-specific manifest is sealed below.
$oldKitManifest = Join-Path $output 'peer-test-kit.json'
if ([IO.File]::Exists($oldKitManifest)) {
    Remove-Item -LiteralPath $oldKitManifest -Force
}

$startScript = @'
@echo off
setlocal
pushd "%~dp0product"
start "Steward Friend Test" "SharedWorlds.Desktop.exe"
popd
endlocal
'@
[IO.File]::WriteAllText(
    (Join-Path $output 'START-STEWARD-FRIEND-TEST.cmd'),
    $startScript.Replace("`n", "`r`n"),
    [Text.UTF8Encoding]::new($false))

$friendGuide = @'
STEWARD PEER FRIEND TEST — DEVELOPMENT BUILD
============================================

This kit is intentionally configured for Valve's Steamworks example AppID 480 (SpaceWar).
It is for development testing with a trusted Steam friend. It is NOT the eventual Steward
Steam release/depot build.

BOTH PCS
--------
1. Extract the SAME ZIP to a normal writable folder.
2. Start the normal Steam client and sign in to your own Steam account.
3. Make sure the two test accounts are Steam friends.
4. Do not run Steward as Administrator if Steam itself is not running as Administrator.
5. Launch START-STEWARD-FRIEND-TEST.cmd on both PCs.
6. Keep both Steward instances running before sending/accepting the Steam lobby invite.

FIRST USE
---------
PC A:
- Import/create a disposable World.
- Share it.
- Manage access -> add PC B's Steam friend.
- Host the World.

PC B:
- Accept/use the Steam invite while this Steward friend-test build is already running.
- Join/bootstrap the World.

Then exercise the existing physical sequence:
- compare the same World/state on A and B;
- make a recognizable game change;
- hand off host A -> B;
- confirm authority generation advances once;
- stop/restart and confirm generation does not advance again;
- Remove access / re-add;
- test true Leave World.

EVIDENCE
--------
Use LIST-PEER-WORLDS.cmd and CAPTURE-PEER-EVIDENCE.cmd from this kit.
The development steam_appid.txt is included in acceptance-build.json, so package verification
still covers the exact bytes both PCs are executing.

IMPORTANT LIMITATION
--------------------
AppID 480 is Valve's shared SDK/example application. It proves Steward's peer/lobby/network
behavior in a development environment, but it does NOT replace the later exact private Steward
AppID qualification. If SteamAPI_Init or invite behavior is blocked specifically by account/license
handling for AppID 480, record that as a development-environment limitation rather than redesigning
Steward authority around it.

Do not upload this development package to a real Steward Steam depot. Valve's documentation says
steam_appid.txt is for development and should be removed from shipped Steam builds.
'@
[IO.File]::WriteAllText(
    (Join-Path $output 'START-HERE-FRIEND-TEST.txt'),
    $friendGuide.Replace("`n", "`r`n"),
    [Text.UTF8Encoding]::new($false))

& $probe --verify-package --package-root $product
if ($LASTEXITCODE -ne 0) {
    Fail "Friend-test product package verification failed with exit code $LASTEXITCODE."
}

$kitFiles = @(Get-ChildItem -LiteralPath $output -Recurse -File |
    Where-Object { -not [string]::Equals($_.Name, 'friend-test-kit.json', [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/')
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
if ($kitFiles.Count -eq 0) {
    Fail 'Friend-test kit contains no files.'
}

$friendManifest = [ordered]@{
    documentType = 'steward.peer-friend-test-kit'
    schemaVersion = 1
    developmentSteamAppId = $steamAppId
    version = $Version
    sourceCommitSha = [string]$manifest.commitSha
    productManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    warning = 'Development only: Valve SpaceWar AppID 480 + steam_appid.txt; never publish as Steward release depot.'
    files = $kitFiles
}
$friendManifestPath = Join-Path $output 'friend-test-kit.json'
[IO.File]::WriteAllText(
    $friendManifestPath,
    ($friendManifest | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

Write-Host
Write-Host '[OK] Steward Steam peer friend-test kit built and verified.'
Write-Host "  Development Steam AppID: $steamAppId (Valve SpaceWar example)"
Write-Host "  Source commit: $($manifest.commitSha)"
Write-Host "  Product: $product"
Write-Host "  Start: $(Join-Path $output 'START-STEWARD-FRIEND-TEST.cmd')"
Write-Host "  Kit manifest: $friendManifestPath"
Write-Host
Write-Host 'Give the exact same ZIP/artifact to both PCs. This is development evidence, not final AppID release evidence.'
