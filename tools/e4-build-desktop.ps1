[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [string]$FriendsBuildApiBaseUrl,
    [string]$SteamReleaseApiBaseUrl,
    [uint32]$SteamReleaseAppId = 0,
    [string]$SteamReleaseWebApiIdentity,
    [switch]$IncludeLegacyRemoteMigrationConfiguration,
    [string]$BuildVersion,
    [string]$AcceptanceManifestOutputPath,
    [switch]$ReleaseContentOnly,
    [switch]$FrameworkDependent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Add-Content -LiteralPath (Join-Path $repoRoot 'build.log') -Value "E4 package validation: $Message"
    }
    Write-Error $Message
    exit 1
}

function Get-NormalizedPackageApiBaseUrl([string]$Value, [string]$ParameterName) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        -not [string]::Equals($uri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        Fail "$ParameterName must be an absolute HTTPS URL without credentials, query, or fragment."
    }

    $absolute = $uri.AbsoluteUri
    if (-not $absolute.EndsWith('/', [StringComparison]::Ordinal)) {
        $absolute += '/'
    }

    return $absolute
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src/SharedWorlds.Desktop/SharedWorlds.Desktop.csproj'
if (-not [IO.File]::Exists($project)) {
    Fail "Desktop project not found: $project"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/e4-desktop-$Runtime"
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output)) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
[IO.Directory]::CreateDirectory($output) | Out-Null

$manifestOutputPath = if ([string]::IsNullOrWhiteSpace($AcceptanceManifestOutputPath)) {
    Join-Path $output 'acceptance-build.json'
}
else {
    [IO.Path]::GetFullPath($AcceptanceManifestOutputPath)
}
$manifestDirectory = [IO.Path]::GetDirectoryName($manifestOutputPath)
if ([string]::IsNullOrWhiteSpace($manifestDirectory)) {
    Fail 'AcceptanceManifestOutputPath must resolve to a file path with a containing directory.'
}
[IO.Directory]::CreateDirectory($manifestDirectory) | Out-Null

$normalizedFriendsBuildApiBaseUrl = Get-NormalizedPackageApiBaseUrl $FriendsBuildApiBaseUrl 'FriendsBuildApiBaseUrl'

# The normal Steam product now needs only the AppID. Legacy API coordinates remain accepted as
# compatibility parameters for old deployment callers, but they are not validated or emitted unless
# the explicit migration switch is present.
$steamReleaseRequested = $SteamReleaseAppId -ne 0
$legacyRemoteMigrationRequested = $IncludeLegacyRemoteMigrationConfiguration.IsPresent
$legacyRemoteCoordinatesSupplied =
    -not [string]::IsNullOrWhiteSpace($SteamReleaseApiBaseUrl) -or
    -not [string]::IsNullOrWhiteSpace($SteamReleaseWebApiIdentity)

if ($null -ne $normalizedFriendsBuildApiBaseUrl -and
    ($steamReleaseRequested -or $legacyRemoteMigrationRequested)) {
    Fail 'Friends Build and Steam package configuration are mutually exclusive.'
}

if ($legacyRemoteMigrationRequested -and -not $steamReleaseRequested) {
    Fail 'SteamReleaseAppId must be a positive UInt32 when legacy Steam remote migration configuration is requested.'
}

$normalizedSteamReleaseApiBaseUrl = $null
if ($legacyRemoteMigrationRequested) {
    if ([string]::IsNullOrWhiteSpace($SteamReleaseApiBaseUrl)) {
        Fail 'SteamReleaseApiBaseUrl is required only when legacy Steam remote migration configuration is requested.'
    }
    if ([string]::IsNullOrWhiteSpace($SteamReleaseWebApiIdentity) -or
        $SteamReleaseWebApiIdentity.Length -gt 128 -or
        $SteamReleaseWebApiIdentity -match '\s') {
        Fail 'SteamReleaseWebApiIdentity must be 1-128 characters without whitespace when legacy remote migration configuration is requested.'
    }

    $normalizedSteamReleaseApiBaseUrl = Get-NormalizedPackageApiBaseUrl $SteamReleaseApiBaseUrl 'SteamReleaseApiBaseUrl'
}
elseif ($legacyRemoteCoordinatesSupplied) {
    Write-Warning 'Legacy Steam API coordinates were supplied but will not be embedded. Add -IncludeLegacyRemoteMigrationConfiguration only for an explicit old-World migration package.'
}

$normalizedBuildVersion = if ([string]::IsNullOrWhiteSpace($BuildVersion)) { $null } else { $BuildVersion.Trim() }
if ($null -ne $normalizedBuildVersion -and
    ($normalizedBuildVersion.Length -gt 64 -or $normalizedBuildVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$')) {
    Fail 'BuildVersion must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
}

$selfContained = -not $FrameworkDependent.IsPresent
$selfContainedText = if ($selfContained) { 'true' } else { 'false' }

Write-Host 'Building Steward E4 Windows acceptance desktop'
Write-Host "  Project: $project"
Write-Host "  Runtime: $Runtime"
Write-Host "  Configuration: $Configuration"
Write-Host "  Self-contained: $selfContainedText"
Write-Host "  Release content only: $($ReleaseContentOnly.IsPresent)"
Write-Host "  Output: $output"
Write-Host "  Acceptance manifest: $manifestOutputPath"
if ($null -ne $normalizedFriendsBuildApiBaseUrl) {
    Write-Host '  Deployment: legacy Friends Build package'
}
if ($steamReleaseRequested) {
    Write-Host '  Deployment: Steam peer product package'
    Write-Host "  Expected Steam AppID: $SteamReleaseAppId"
}
if ($legacyRemoteMigrationRequested) {
    Write-Host '  Legacy remote migration compatibility: enabled explicitly'
    Write-Host "  Steam Web API identity: $SteamReleaseWebApiIdentity"
}
if ($null -ne $normalizedBuildVersion) {
    Write-Host "  Build version: $normalizedBuildVersion"
}
Write-Host

$publishArguments = @(
    'publish',
    $project,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', $selfContainedText,
    '--output', $output,
    '--nologo',
    '--verbosity', 'minimal'
)
if ($null -ne $normalizedBuildVersion) {
    $publishArguments += "-p:InformationalVersion=$normalizedBuildVersion"
    $publishArguments += '-p:IncludeSourceRevisionInInformationalVersion=false'
}

$publishOutput = @(& dotnet @publishArguments 2>&1)
$publishExitCode = $LASTEXITCODE
$publishOutput | ForEach-Object { Write-Host $_ }
if ($publishExitCode -ne 0) {
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Add-Content -LiteralPath (Join-Path $repoRoot 'build.log') -Value 'E4 acceptance package dotnet publish output:'
        $publishOutput | Add-Content -LiteralPath (Join-Path $repoRoot 'build.log')
    }
    Fail "dotnet publish failed with exit code $publishExitCode."
}

if ($ReleaseContentOnly.IsPresent) {
    Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.pdb' |
        Remove-Item -Force

    $steamImportLibrary = Join-Path $output 'steam_api64.lib'
    if ([IO.File]::Exists($steamImportLibrary)) {
        Remove-Item -LiteralPath $steamImportLibrary -Force
    }
}

$requiredFiles = @(
    'SharedWorlds.Desktop.exe',
    'steam_api64.dll',
    'SharedWorlds.GameAdapters.Factorio.dll',
    'SharedWorlds.GameAdapters.Palworld.dll',
    'SharedWorlds.GameAdapters.SevenDaysToDie.dll',
    'SharedWorlds.GameAdapters.ProjectZomboid.dll'
)

foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $output $requiredFile
    if ([IO.File]::Exists($requiredPath)) {
        continue
    }

    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName)
    Write-Host "Required published file is missing: $requiredFile"
    Write-Host 'Published files:'
    $files | ForEach-Object {
        Write-Host "  $([IO.Path]::GetRelativePath($output, $_.FullName))"
    }
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Add-Content -LiteralPath (Join-Path $repoRoot 'build.log') -Value "Missing E4 package file: $requiredFile"
    }
    Fail "Published acceptance package is incomplete: $requiredFile"
}

if ($null -ne $normalizedFriendsBuildApiBaseUrl) {
    $friendsBuildConfiguration = [ordered]@{
        schemaVersion = 1
        apiBaseUrl = $normalizedFriendsBuildApiBaseUrl
    }
    $friendsBuildConfigurationJson = $friendsBuildConfiguration | ConvertTo-Json -Compress
    $friendsBuildConfigurationPath = Join-Path $output 'steward-friends-build.json'
    [IO.File]::WriteAllText(
        $friendsBuildConfigurationPath,
        $friendsBuildConfigurationJson,
        [Text.UTF8Encoding]::new($false))
}

if ($steamReleaseRequested) {
    $steamPlatformConfiguration = [ordered]@{
        schemaVersion = 1
        steamAppId = $SteamReleaseAppId
    }
    $steamPlatformConfigurationJson = $steamPlatformConfiguration | ConvertTo-Json -Compress
    $steamPlatformConfigurationPath = Join-Path $output 'steward-steam.json'
    [IO.File]::WriteAllText(
        $steamPlatformConfigurationPath,
        $steamPlatformConfigurationJson,
        [Text.UTF8Encoding]::new($false))
}

if ($legacyRemoteMigrationRequested) {
    $steamReleaseConfiguration = [ordered]@{
        schemaVersion = 1
        apiBaseUrl = $normalizedSteamReleaseApiBaseUrl
        steamAppId = $SteamReleaseAppId
        steamWebApiIdentity = $SteamReleaseWebApiIdentity
    }
    $steamReleaseConfigurationJson = $steamReleaseConfiguration | ConvertTo-Json -Compress
    $steamReleaseConfigurationPath = Join-Path $output 'steward-steam-release.json'
    [IO.File]::WriteAllText(
        $steamReleaseConfigurationPath,
        $steamReleaseConfigurationJson,
        [Text.UTF8Encoding]::new($false))
}

$desktopExecutable = Join-Path $output 'SharedWorlds.Desktop.exe'
$steamNative = Join-Path $output 'steam_api64.dll'

$commit = $null
try {
    $commit = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
}
catch {
    $commit = $null
}
if ([string]::IsNullOrWhiteSpace($commit)) {
    $commit = 'unknown'
}

$packageFiles = @(Get-ChildItem -LiteralPath $output -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/')
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })

if ($packageFiles.Count -eq 0) {
    Fail 'Published acceptance package contains no files.'
}

$metadata = [ordered]@{
    documentType = 'steward.e4-desktop-acceptance-build'
    schemaVersion = 2
    commitSha = $commit
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runtime = $Runtime
    configuration = $Configuration
    selfContained = $selfContained
    executable = [IO.Path]::GetFileName($desktopExecutable)
    steamNativeRuntime = [IO.Path]::GetFileName($steamNative)
    files = $packageFiles
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestOutputPath -Encoding utf8

Write-Host
Write-Host '[OK] Steward desktop acceptance build is structurally complete and byte-verifiable.'
Write-Host "  Executable: $desktopExecutable"
Write-Host "  Steam runtime: $steamNative"
Write-Host "  Production adapters: Factorio, Palworld, 7 Days to Die, Project Zomboid"
Write-Host "  Hashed package files: $($packageFiles.Count)"
Write-Host "  Metadata: $manifestOutputPath"
Write-Host
if ($null -ne $normalizedFriendsBuildApiBaseUrl) {
    Write-Host 'This legacy Friends Build includes steward-friends-build.json for remote migration compatibility.'
    Write-Host 'No private friend credential, Steam AppID, Web API identity, ticket, or backend secret is embedded.'
}
elseif ($steamReleaseRequested -and $legacyRemoteMigrationRequested) {
    Write-Host 'The Steam peer AppID is embedded independently in steward-steam.json and covered by the package manifest.'
    Write-Host 'Legacy API coordinates are additionally embedded in steward-steam-release.json because migration compatibility was explicitly requested.'
    Write-Host 'No publisher API key, Steam ticket, Steward session credential, or other backend secret is embedded.'
}
elseif ($steamReleaseRequested) {
    Write-Host 'The Steam peer AppID is embedded independently in steward-steam.json and covered by the package manifest.'
    Write-Host 'No legacy API URL, Web API identity, backend credential, or remote-session configuration is embedded in the normal product package.'
}
else {
    Write-Host 'No Steam AppID, API URL, Web API identity, tickets, or backend secrets are embedded by this script.'
    Write-Host 'Use tools/e4-live-acceptance.ps1 to verify this exact engineering package and supply any explicit migration-only coordinates.'
}
