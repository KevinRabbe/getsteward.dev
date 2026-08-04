[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [string]$Version = '2.0.0-alpha.2',
    [string]$OutputDirectory,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw $Message
}

function Require-Tool([string]$Name) {
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Fail "Required tool '$Name' is not available."
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopBuilder = Join-Path $repoRoot 'tools/v2-build-friends.ps1'
$assembler = Join-Path $repoRoot 'tools/assemble-closed-alpha-release.ps1'
$backendDockerfile = Join-Path $repoRoot 'src/SharedWorlds.Backend.Api/Dockerfile'
foreach ($requiredPath in @($desktopBuilder, $assembler, $backendDockerfile)) {
    if (-not [IO.File]::Exists($requiredPath)) {
        Fail "Closed-alpha release input is missing: $requiredPath"
    }
}

Require-Tool 'git'
Require-Tool 'dotnet'
Require-Tool 'docker'

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version.Length -gt 64 -or
    $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    Fail 'Version must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
}

$apiUri = $null
if (-not [Uri]::TryCreate($ApiBaseUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    -not [string]::Equals($apiUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::IsNullOrEmpty($apiUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($apiUri.Query) -or
    -not [string]::IsNullOrEmpty($apiUri.Fragment)) {
    Fail 'ApiBaseUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
}
$normalizedApiBaseUrl = $apiUri.AbsoluteUri
if (-not $normalizedApiBaseUrl.EndsWith('/', [StringComparison]::Ordinal)) {
    $normalizedApiBaseUrl += '/'
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    Fail 'Could not resolve one exact lowercase Git commit SHA for the release candidate.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/closed-alpha-$Version"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-closed-alpha-" + [Guid]::NewGuid().ToString('N'))
$desktopStaging = Join-Path $temporaryRoot 'desktop'
$backendTarPath = Join-Path $temporaryRoot "steward-backend-$Version-linux-amd64.tar"

$tagVersion = $Version.ToLowerInvariant()
if ($tagVersion -notmatch '^[a-z0-9][a-z0-9._-]{0,63}$') {
    Fail 'Version cannot be normalized into one safe backend image tag.'
}
$backendImageTag = "steward-backend:$tagVersion"

Write-Host 'Building Safe World closed-alpha release candidate'
Write-Host "  Version: $Version"
Write-Host "  Commit: $commit"
Write-Host "  API: $normalizedApiBaseUrl"
Write-Host "  Output: $output"
Write-Host '  Physical Bring Here gate: deferred'
Write-Host

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

    & $desktopBuilder `
        -ApiBaseUrl $normalizedApiBaseUrl `
        -Version $Version `
        -Configuration $Configuration `
        -OutputDirectory $desktopStaging
    if ($LASTEXITCODE -ne 0) {
        Fail "Friends Build package failed with exit code $LASTEXITCODE."
    }

    $buildArguments = @(
        'build',
        '--platform', 'linux/amd64',
        '--file', $backendDockerfile,
        '--tag', $backendImageTag,
        '--label', "org.opencontainers.image.revision=$commit",
        '--label', "org.opencontainers.image.version=$Version",
        $repoRoot
    )
    & docker @buildArguments
    if ($LASTEXITCODE -ne 0) {
        Fail "Backend container build failed with exit code $LASTEXITCODE."
    }

    $backendImageId = (& docker image inspect $backendImageTag --format '{{.Id}}').Trim().ToLowerInvariant()
    $backendRuntimeUser = (& docker image inspect $backendImageTag --format '{{.Config.User}}').Trim()
    if ($LASTEXITCODE -ne 0 -or $backendImageId -notmatch '^sha256:[0-9a-f]{64}$') {
        Fail 'Built backend image did not expose one valid immutable image ID.'
    }
    if (-not [string]::Equals($backendRuntimeUser, 'app', [StringComparison]::Ordinal)) {
        Fail "Built backend image must run as non-root user 'app', not '$backendRuntimeUser'."
    }

    & docker save --output $backendTarPath $backendImageTag
    if ($LASTEXITCODE -ne 0 -or
        -not [IO.File]::Exists($backendTarPath) -or
        (Get-Item -LiteralPath $backendTarPath).Length -le 0) {
        Fail 'Backend image TAR was not created.'
    }

    & $assembler `
        -ApiBaseUrl $normalizedApiBaseUrl `
        -Version $Version `
        -CommitSha $commit `
        -DesktopInputDirectory $desktopStaging `
        -BackendTarPath $backendTarPath `
        -BackendImageTag $backendImageTag `
        -BackendImageId $backendImageId `
        -BackendRuntimeUser $backendRuntimeUser `
        -OutputDirectory $output
}
finally {
    if ([IO.Directory]::Exists($temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
