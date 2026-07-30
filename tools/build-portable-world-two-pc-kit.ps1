[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopBuild = Join-Path $repoRoot 'tools/e4-build-desktop.ps1'
$probeProject = Join-Path $repoRoot 'tools/SharedWorlds.PortableWorldProbe/SharedWorlds.PortableWorldProbe.csproj'
$kitSource = Join-Path $repoRoot 'tools/portable-world-two-pc-kit'

if (-not [IO.File]::Exists($desktopBuild)) {
    Fail "Desktop build script not found: $desktopBuild"
}
if (-not [IO.File]::Exists($probeProject)) {
    Fail "Portable World evidence probe project not found: $probeProject"
}
if (-not [IO.Directory]::Exists($kitSource)) {
    Fail "Portable World two-PC kit source directory not found: $kitSource"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/portable-world-two-pc-kit-$Runtime"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)

& $desktopBuild `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -FrameworkDependent `
    -OutputDirectory $output
if ($LASTEXITCODE -ne 0) {
    Fail "Safe World desktop package build failed with exit code $LASTEXITCODE."
}

$toolOutput = Join-Path $output 'acceptance-tools'
[IO.Directory]::CreateDirectory($toolOutput) | Out-Null

$publishArguments = @(
    'publish',
    $probeProject,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'false',
    '--output', $toolOutput,
    '--nologo',
    '--verbosity', 'minimal'
)
$publishOutput = @(& dotnet @publishArguments 2>&1)
$publishExitCode = $LASTEXITCODE
$publishOutput | ForEach-Object { Write-Host $_ }
if ($publishExitCode -ne 0) {
    Fail "Portable World evidence probe publish failed with exit code $publishExitCode."
}

$probeExecutable = Join-Path $toolOutput 'SharedWorlds.PortableWorldProbe.exe'
if (-not [IO.File]::Exists($probeExecutable)) {
    Fail "Published Portable World evidence probe is missing: $probeExecutable"
}

Copy-Item -LiteralPath (Join-Path $kitSource 'PC-A-CREATOR-CHECK.cmd') -Destination $toolOutput -Force
Copy-Item -LiteralPath (Join-Path $kitSource 'PC-B-VIEWER-CHECK.cmd') -Destination $toolOutput -Force
Copy-Item -LiteralPath (Join-Path $kitSource 'START-HERE-TWO-PC-TEST.txt') -Destination $output -Force

$manifestPath = Join-Path $output 'acceptance-build.json'
if (-not [IO.File]::Exists($manifestPath)) {
    Fail "Desktop acceptance manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$packageFiles = @(Get-ChildItem -LiteralPath $output -Recurse -File |
    Where-Object { -not [string]::Equals($_.FullName, $manifestPath, [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/')
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })

if ($packageFiles.Count -eq 0) {
    Fail 'Portable World two-PC test kit contains no package files.'
}

$metadata = [ordered]@{
    documentType = [string]$manifest.documentType
    schemaVersion = [int]$manifest.schemaVersion
    commitSha = [string]$manifest.commitSha
    builtAtUtc = [string]$manifest.builtAtUtc
    runtime = [string]$manifest.runtime
    configuration = [string]$manifest.configuration
    selfContained = [bool]$manifest.selfContained
    executable = [string]$manifest.executable
    steamNativeRuntime = [string]$manifest.steamNativeRuntime
    files = $packageFiles
}
[IO.File]::WriteAllText(
    $manifestPath,
    ($metadata | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

$helpOutput = @(& $probeExecutable --help 2>&1)
$helpExitCode = $LASTEXITCODE
$helpOutput | ForEach-Object { Write-Host $_ }
if ($helpExitCode -ne 0) {
    Fail "Packaged Portable World evidence probe help smoke failed with exit code $helpExitCode."
}

$requiredKitFiles = @(
    'START-HERE-TWO-PC-TEST.txt',
    'acceptance-tools/PC-A-CREATOR-CHECK.cmd',
    'acceptance-tools/PC-B-VIEWER-CHECK.cmd',
    'acceptance-tools/SharedWorlds.PortableWorldProbe.exe'
)
$manifestPaths = @($packageFiles | ForEach-Object { [string]$_.path })
foreach ($requiredKitFile in $requiredKitFiles) {
    if ($manifestPaths -notcontains $requiredKitFile) {
        Fail "Portable World test-kit manifest is missing '$requiredKitFile'."
    }
}

Write-Host
Write-Host '[OK] Safe World portable World two-PC test kit is complete and byte-verifiable.'
Write-Host "  Output: $output"
Write-Host "  Desktop: $(Join-Path $output 'SharedWorlds.Desktop.exe')"
Write-Host "  Creator check: $(Join-Path $toolOutput 'PC-A-CREATOR-CHECK.cmd')"
Write-Host "  Viewer check: $(Join-Path $toolOutput 'PC-B-VIEWER-CHECK.cmd')"
Write-Host "  Hashed package files: $($packageFiles.Count)"
Write-Host "  Manifest: $manifestPath"
