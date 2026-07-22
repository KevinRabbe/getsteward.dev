[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [switch]$FrameworkDependent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
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

$selfContained = -not $FrameworkDependent.IsPresent
$selfContainedText = if ($selfContained) { 'true' } else { 'false' }

Write-Host 'Building Steward E4 Windows acceptance desktop'
Write-Host "  Project: $project"
Write-Host "  Runtime: $Runtime"
Write-Host "  Configuration: $Configuration"
Write-Host "  Self-contained: $selfContainedText"
Write-Host "  Output: $output"
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

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed with exit code $LASTEXITCODE."
}

$desktopExecutable = Join-Path $output 'SharedWorlds.Desktop.exe'
$steamNative = Join-Path $output 'steam_api64.dll'

if (-not [IO.File]::Exists($desktopExecutable)) {
    Fail "Published desktop executable is missing: $desktopExecutable"
}

if (-not [IO.File]::Exists($steamNative)) {
    Fail "Published Steam native runtime is missing: $steamNative"
}

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

$metadata = [ordered]@{
    documentType = 'steward.e4-desktop-acceptance-build'
    schemaVersion = 1
    commitSha = $commit
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runtime = $Runtime
    configuration = $Configuration
    selfContained = $selfContained
    executable = [IO.Path]::GetFileName($desktopExecutable)
    steamNativeRuntime = [IO.Path]::GetFileName($steamNative)
}
$metadataPath = Join-Path $output 'acceptance-build.json'
$metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8

Write-Host
Write-Host '[OK] Steward desktop acceptance build is structurally complete.'
Write-Host "  Executable: $desktopExecutable"
Write-Host "  Steam runtime: $steamNative"
Write-Host "  Metadata: $metadataPath"
Write-Host
Write-Host 'No Steam AppID, API URL, Web API identity, tickets, or backend secrets are embedded by this script.'
Write-Host 'Use tools/e4-live-acceptance.ps1 to supply the non-secret runtime coordinates and launch the build.'
