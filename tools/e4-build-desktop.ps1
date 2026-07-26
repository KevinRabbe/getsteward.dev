[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [string]$FriendsBuildApiBaseUrl,
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

function Get-NormalizedFriendsBuildApiBaseUrl([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        -not [string]::Equals($uri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        Fail 'FriendsBuildApiBaseUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
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

$friendsBuildApiBaseUrl = Get-NormalizedFriendsBuildApiBaseUrl $FriendsBuildApiBaseUrl
$selfContained = -not $FrameworkDependent.IsPresent
$selfContainedText = if ($selfContained) { 'true' } else { 'false' }

Write-Host 'Building Steward E4 Windows acceptance desktop'
Write-Host "  Project: $project"
Write-Host "  Runtime: $Runtime"
Write-Host "  Configuration: $Configuration"
Write-Host "  Self-contained: $selfContainedText"
Write-Host "  Output: $output"
if ($null -ne $friendsBuildApiBaseUrl) {
    Write-Host '  Deployment: Friends Build package'
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

if ($null -ne $friendsBuildApiBaseUrl) {
    $friendsBuildConfiguration = [ordered]@{
        schemaVersion = 1
        apiBaseUrl = $friendsBuildApiBaseUrl
    }
    $friendsBuildConfigurationJson = $friendsBuildConfiguration | ConvertTo-Json -Compress
    $friendsBuildConfigurationPath = Join-Path $output 'steward-friends-build.json'
    [IO.File]::WriteAllText(
        $friendsBuildConfigurationPath,
        $friendsBuildConfigurationJson,
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
$metadataPath = Join-Path $output 'acceptance-build.json'
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8

Write-Host
Write-Host '[OK] Steward desktop acceptance build is structurally complete and byte-verifiable.'
Write-Host "  Executable: $desktopExecutable"
Write-Host "  Steam runtime: $steamNative"
Write-Host "  Production adapters: Factorio, Palworld, 7 Days to Die, Project Zomboid"
Write-Host "  Hashed package files: $($packageFiles.Count)"
Write-Host "  Metadata: $metadataPath"
Write-Host
if ($null -ne $friendsBuildApiBaseUrl) {
    Write-Host 'The Friends Build HTTPS API coordinate is embedded in steward-friends-build.json and covered by the package manifest.'
    Write-Host 'No private friend credential, Steam AppID, Web API identity, ticket, or backend secret is embedded.'
}
else {
    Write-Host 'No Steam AppID, API URL, Web API identity, tickets, or backend secrets are embedded by this script.'
    Write-Host 'Use tools/e4-live-acceptance.ps1 to verify this exact package and supply the non-secret runtime coordinates.'
}
