[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [string]$Version = '2.0.0-alpha.1',
    [string]$OutputDirectory,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$builder = Join-Path $repoRoot 'tools/e4-build-desktop.ps1'
if (-not [IO.File]::Exists($builder)) {
    Fail "Desktop package builder not found: $builder"
}

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version.Length -gt 64 -or
    $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    Fail 'Version must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
}

$uri = $null
if (-not [Uri]::TryCreate($ApiBaseUrl, [UriKind]::Absolute, [ref]$uri) -or
    -not [string]::Equals($uri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::IsNullOrEmpty($uri.UserInfo) -or
    -not [string]::IsNullOrEmpty($uri.Query) -or
    -not [string]::IsNullOrEmpty($uri.Fragment)) {
    Fail 'ApiBaseUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts/v2-friends'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null

$artifactBaseName = "Steward-$Version-win-x64"
$zipPath = Join-Path $output "$artifactBaseName.zip"
$checksumPath = "$zipPath.sha256"
if ([IO.File]::Exists($zipPath)) {
    Remove-Item -LiteralPath $zipPath -Force
}
if ([IO.File]::Exists($checksumPath)) {
    Remove-Item -LiteralPath $checksumPath -Force
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-v2-friends-" + [Guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $temporaryRoot $artifactBaseName
try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

    & $builder `
        -Configuration $Configuration `
        -Runtime 'win-x64' `
        -OutputDirectory $packageDirectory `
        -FriendsBuildApiBaseUrl $uri.AbsoluteUri
    if ($LASTEXITCODE -ne 0) {
        Fail "Desktop package builder failed with exit code $LASTEXITCODE."
    }

    $friendsConfigurationPath = Join-Path $packageDirectory 'steward-friends-build.json'
    $manifestPath = Join-Path $packageDirectory 'acceptance-build.json'
    $executablePath = Join-Path $packageDirectory 'SharedWorlds.Desktop.exe'
    foreach ($requiredPath in @($friendsConfigurationPath, $manifestPath, $executablePath)) {
        if (-not [IO.File]::Exists($requiredPath)) {
            Fail "Friends Build package is incomplete: $requiredPath"
        }
    }

    $configurationDocument = Get-Content -LiteralPath $friendsConfigurationPath -Raw | ConvertFrom-Json
    if ($configurationDocument.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$configurationDocument.apiBaseUrl)) {
        Fail 'Generated Friends Build deployment configuration is invalid.'
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $configurationEntry = @($manifest.files | Where-Object { $_.path -eq 'steward-friends-build.json' })
    if ($configurationEntry.Count -ne 1) {
        Fail 'Friends Build configuration is not covered exactly once by the package manifest.'
    }

    $actualConfigurationHash = (Get-FileHash -LiteralPath $friendsConfigurationPath -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            [string]$configurationEntry[0].sha256,
            $actualConfigurationHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        Fail 'Friends Build configuration hash does not match the package manifest.'
    }

    Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
    if (-not [IO.File]::Exists($zipPath) -or (Get-Item -LiteralPath $zipPath).Length -le 0) {
        Fail 'Friends Build ZIP was not created.'
    }

    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
    [IO.File]::WriteAllText(
        $checksumPath,
        "$zipHash  $([IO.Path]::GetFileName($zipPath))`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Steward V2 Friends Build package is ready.'
    Write-Host "  ZIP: $zipPath"
    Write-Host "  SHA-256: $zipHash"
    Write-Host "  Checksum file: $checksumPath"
    Write-Host '  Private friend credentials are not part of the package.'
}
finally {
    if ([IO.Directory]::Exists($temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
