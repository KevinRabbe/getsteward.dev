[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [uint32]$SteamAppId,
    [string]$Version = '3.0.0-peer-beta.1',
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

if ($SteamAppId -eq 0) {
    Fail 'SteamAppId must be a positive UInt32.'
}
if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version.Length -gt 64 -or
    $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    Fail 'Version must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
}
if (-not [string]::Equals($Runtime, 'win-x64', [StringComparison]::Ordinal)) {
    Fail 'The physical Steam peer two-PC kit currently supports only win-x64.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopBuild = Join-Path $repoRoot 'tools/e4-build-desktop.ps1'
$probeProject = Join-Path $repoRoot 'tools/SharedWorlds.PeerWorldProbe/SharedWorlds.PeerWorldProbe.csproj'
$kitSource = Join-Path $repoRoot 'tools/steam-peer-two-pc-kit'

foreach ($required in @($desktopBuild, $probeProject)) {
    if (-not [IO.File]::Exists($required)) {
        Fail "Required peer-kit build input not found: $required"
    }
}
if (-not [IO.Directory]::Exists($kitSource)) {
    Fail "Peer two-PC kit source directory not found: $kitSource"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/steward-steam-peer-two-pc-kit-$Runtime"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output)) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
[IO.Directory]::CreateDirectory($output) | Out-Null

$productOutput = Join-Path $output 'product'
$toolOutput = Join-Path $output 'acceptance-tools'
[IO.Directory]::CreateDirectory($productOutput) | Out-Null
[IO.Directory]::CreateDirectory($toolOutput) | Out-Null

Write-Host 'Building exact normal Steward Steam peer product...'
$buildArguments = @{
    Configuration = $Configuration
    Runtime = $Runtime
    OutputDirectory = $productOutput
    AcceptanceManifestOutputPath = (Join-Path $productOutput 'acceptance-build.json')
    ReleaseContentOnly = $true
    SteamReleaseAppId = $SteamAppId
    BuildVersion = $Version
}
& $desktopBuild @buildArguments
if ($LASTEXITCODE -ne 0) {
    Fail "Steward Steam peer product build failed with exit code $LASTEXITCODE."
}

$steamConfigurationPath = Join-Path $productOutput 'steward-steam.json'
$productManifestPath = Join-Path $productOutput 'acceptance-build.json'
if (-not [IO.File]::Exists($steamConfigurationPath) -or
    -not [IO.File]::Exists($productManifestPath)) {
    Fail 'Normal peer product is missing steward-steam.json or acceptance-build.json.'
}
foreach ($forbidden in @('steward-steam-release.json', 'steward-friends-build.json')) {
    if ([IO.File]::Exists((Join-Path $productOutput $forbidden))) {
        Fail "Normal physical peer product unexpectedly contains migration file '$forbidden'."
    }
}

$steamConfiguration = Get-Content -LiteralPath $steamConfigurationPath -Raw | ConvertFrom-Json
$steamProperties = @($steamConfiguration.PSObject.Properties.Name | Sort-Object)
$expectedSteamProperties = @('schemaVersion', 'steamAppId') | Sort-Object
if ($steamProperties.Count -ne $expectedSteamProperties.Count -or
    @(Compare-Object $steamProperties $expectedSteamProperties).Count -ne 0 -or
    [int]$steamConfiguration.schemaVersion -ne 1 -or
    [uint32]$steamConfiguration.steamAppId -ne $SteamAppId) {
    Fail 'Normal peer product does not contain the exact AppID-only steward-steam.json contract.'
}

Write-Host 'Publishing read-only physical evidence probe...'
$publishArguments = @(
    'publish',
    $probeProject,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '--output', $toolOutput,
    '--nologo',
    '--verbosity', 'minimal',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
$publishOutput = @(& dotnet @publishArguments 2>&1)
$publishExitCode = $LASTEXITCODE
$publishOutput | ForEach-Object { Write-Host $_ }
if ($publishExitCode -ne 0) {
    Fail "Peer evidence probe publish failed with exit code $publishExitCode."
}

$probeExecutable = Join-Path $toolOutput 'SharedWorlds.PeerWorldProbe.exe'
if (-not [IO.File]::Exists($probeExecutable)) {
    Fail "Published peer evidence probe is missing: $probeExecutable"
}
Get-ChildItem -LiteralPath $toolOutput -Recurse -File -Filter '*.pdb' | Remove-Item -Force

foreach ($fileName in @(
    'START-HERE-STEAM-PEER-TWO-PC.txt',
    'LIST-PEER-WORLDS.cmd',
    'CAPTURE-PEER-EVIDENCE.cmd')) {
    $source = Join-Path $kitSource $fileName
    if (-not [IO.File]::Exists($source)) {
        Fail "Peer two-PC kit source file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $fileName) -Force
}

Write-Host 'Verifying exact AppID-only product through the packaged probe...'
$verifyOutput = @(& $probeExecutable --verify-package --package-root $productOutput 2>&1)
$verifyExitCode = $LASTEXITCODE
$verifyOutput | ForEach-Object { Write-Host $_ }
if ($verifyExitCode -ne 0) {
    Fail "Packaged peer product verification failed with exit code $verifyExitCode."
}

$helpOutput = @(& $probeExecutable --help 2>&1)
$helpExitCode = $LASTEXITCODE
$helpOutput | ForEach-Object { Write-Host $_ }
if ($helpExitCode -ne 0) {
    Fail "Packaged peer evidence probe help smoke failed with exit code $helpExitCode."
}

$productManifest = Get-Content -LiteralPath $productManifestPath -Raw | ConvertFrom-Json
if (-not [string]::Equals(
        [string]$productManifest.documentType,
        'steward.e4-desktop-acceptance-build',
        [StringComparison]::Ordinal) -or
    [int]$productManifest.schemaVersion -ne 2 -or
    [string]::IsNullOrWhiteSpace([string]$productManifest.commitSha)) {
    Fail 'Product acceptance manifest is not the expected exact-build contract.'
}

$kitManifestPath = Join-Path $output 'peer-test-kit.json'
$kitFiles = @(Get-ChildItem -LiteralPath $output -Recurse -File |
    Where-Object { -not [string]::Equals($_.FullName, $kitManifestPath, [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/')
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
if ($kitFiles.Count -eq 0) {
    Fail 'Steam peer two-PC acceptance kit contains no files.'
}

$productManifestSha256 = (Get-FileHash -LiteralPath $productManifestPath -Algorithm SHA256).Hash
$kitMetadata = [ordered]@{
    documentType = 'steward.steam-peer-two-pc-test-kit'
    schemaVersion = 1
    commitSha = [string]$productManifest.commitSha
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runtime = $Runtime
    configuration = $Configuration
    version = $Version
    steamAppId = $SteamAppId
    productManifestSha256 = $productManifestSha256
    productDirectory = 'product'
    evidenceTool = 'acceptance-tools/SharedWorlds.PeerWorldProbe.exe'
    files = $kitFiles
}
[IO.File]::WriteAllText(
    $kitManifestPath,
    ($kitMetadata | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

$requiredKitPaths = @(
    'product/SharedWorlds.Desktop.exe',
    'product/steward-steam.json',
    'product/acceptance-build.json',
    'acceptance-tools/SharedWorlds.PeerWorldProbe.exe',
    'START-HERE-STEAM-PEER-TWO-PC.txt',
    'LIST-PEER-WORLDS.cmd',
    'CAPTURE-PEER-EVIDENCE.cmd'
)
$actualKitPaths = @($kitFiles | ForEach-Object { [string]$_.path })
foreach ($requiredPath in $requiredKitPaths) {
    if ($actualKitPaths -notcontains $requiredPath) {
        Fail "Steam peer two-PC kit manifest is missing '$requiredPath'."
    }
}

Write-Host
Write-Host '[OK] Steward Steam peer two-PC acceptance kit is complete and byte-verifiable.'
Write-Host "  Exact product: $productOutput"
Write-Host "  Steam AppID: $SteamAppId"
Write-Host "  Product commit: $([string]$productManifest.commitSha)"
Write-Host "  Product manifest SHA-256: $productManifestSha256"
Write-Host "  Evidence probe: $probeExecutable"
Write-Host "  Procedure: $(Join-Path $output 'START-HERE-STEAM-PEER-TWO-PC.txt')"
Write-Host "  Kit manifest: $kitManifestPath"
Write-Host
Write-Host 'Use the same product/ bytes on both Steam accounts. No Steward backend is required for this acceptance.'
