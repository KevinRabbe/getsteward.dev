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
$probeProject = Join-Path $repoRoot 'tools/SharedWorlds.BringHereProbe/SharedWorlds.BringHereProbe.csproj'
$kitSource = Join-Path $repoRoot 'tools/bring-here-two-pc-kit'

if (-not [IO.File]::Exists($desktopBuild)) {
    Fail "Desktop build script not found: $desktopBuild"
}
if (-not [IO.File]::Exists($probeProject)) {
    Fail "Bring Here evidence probe project not found: $probeProject"
}
if (-not [IO.Directory]::Exists($kitSource)) {
    Fail "Bring Here two-PC kit source directory not found: $kitSource"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/bring-here-two-pc-kit-$Runtime"
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
$publishOutput = @(& dotnet publish $probeProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output $toolOutput `
    --nologo `
    --verbosity minimal 2>&1)
$publishExitCode = $LASTEXITCODE
$publishOutput | ForEach-Object { Write-Host $_ }
if ($publishExitCode -ne 0) {
    Fail "Bring Here evidence probe publish failed with exit code $publishExitCode."
}

$probeExecutable = Join-Path $toolOutput 'SharedWorlds.BringHereProbe.exe'
if (-not [IO.File]::Exists($probeExecutable)) {
    Fail "Published Bring Here evidence probe is missing: $probeExecutable"
}

Copy-Item -LiteralPath (Join-Path $kitSource 'PC-A-SOURCE-BEFORE.cmd') -Destination $toolOutput -Force
Copy-Item -LiteralPath (Join-Path $kitSource 'PC-B-TARGET-AFTER.cmd') -Destination $toolOutput -Force
Copy-Item -LiteralPath (Join-Path $kitSource 'PC-A-SOURCE-AFTER.cmd') -Destination $toolOutput -Force
Copy-Item -LiteralPath (Join-Path $kitSource 'START-HERE-BRING-HERE-TWO-PC.txt') -Destination $output -Force

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
    Fail 'Bring Here two-PC acceptance kit contains no package files.'
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

& $probeExecutable --help
if ($LASTEXITCODE -ne 0) {
    Fail "Packaged Bring Here probe help smoke failed with exit code $LASTEXITCODE."
}
& $probeExecutable --self-test
if ($LASTEXITCODE -ne 0) {
    Fail "Packaged Bring Here probe self-test failed with exit code $LASTEXITCODE."
}

$requiredFiles = @(
    'SharedWorlds.Desktop.exe',
    'START-HERE-BRING-HERE-TWO-PC.txt',
    'acceptance-tools/PC-A-SOURCE-BEFORE.cmd',
    'acceptance-tools/PC-B-TARGET-AFTER.cmd',
    'acceptance-tools/PC-A-SOURCE-AFTER.cmd',
    'acceptance-tools/SharedWorlds.BringHereProbe.exe'
)
$manifestPaths = @($packageFiles | ForEach-Object { [string]$_.path })
foreach ($requiredFile in $requiredFiles) {
    if ($manifestPaths -notcontains $requiredFile) {
        Fail "Bring Here test-kit manifest is missing '$requiredFile'."
    }
}

Write-Host
Write-Host '[OK] Safe World private Bring Here two-PC acceptance kit is complete and byte-verifiable.'
Write-Host "  Output: $output"
Write-Host "  Desktop: $(Join-Path $output 'SharedWorlds.Desktop.exe')"
Write-Host "  Probe: $probeExecutable"
Write-Host "  Source-before: $(Join-Path $toolOutput 'PC-A-SOURCE-BEFORE.cmd')"
Write-Host "  Target-after: $(Join-Path $toolOutput 'PC-B-TARGET-AFTER.cmd')"
Write-Host "  Source-after: $(Join-Path $toolOutput 'PC-A-SOURCE-AFTER.cmd')"
Write-Host "  Hashed package files: $($packageFiles.Count)"
Write-Host "  Manifest: $manifestPath"
