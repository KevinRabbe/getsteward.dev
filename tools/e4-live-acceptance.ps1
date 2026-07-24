[CmdletBinding()]
param(
    [string]$ApiBaseUrl = $env:STEWARD_API_BASE_URL,
    [string]$SteamAppId = $env:STEWARD_STEAM_APP_ID,
    [string]$SteamWebApiIdentity = $env:STEWARD_STEAM_WEB_API_IDENTITY,
    [string]$DesktopExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

function Verify-AcceptancePackage([string]$DesktopPath) {
    $packageRoot = [IO.Path]::GetDirectoryName($DesktopPath)
    $manifestPath = Join-Path $packageRoot 'acceptance-build.json'
    if (-not [IO.File]::Exists($manifestPath)) {
        Fail "Acceptance package manifest not found beside the desktop executable: $manifestPath"
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        Fail "Acceptance package manifest is invalid JSON: $($_.Exception.Message)"
    }

    if ($manifest.documentType -ne 'steward.e4-desktop-acceptance-build' -or
        $manifest.schemaVersion -ne 2) {
        Fail 'Acceptance package manifest has an unsupported document type or schema version.'
    }

    if ([string]::IsNullOrWhiteSpace([string]$manifest.executable) -or
        $manifest.executable -ne [IO.Path]::GetFileName($DesktopPath)) {
        Fail 'Acceptance package manifest does not identify the selected desktop executable.'
    }

    $declaredFiles = @($manifest.files)
    if ($declaredFiles.Count -eq 0) {
        Fail 'Acceptance package manifest contains no file identities.'
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $declaredFiles) {
        $relative = [string]$entry.path
        $expectedHash = [string]$entry.sha256
        [Int64]$expectedLength = $entry.byteSize
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [string]::IsNullOrWhiteSpace($expectedHash) -or
            $expectedHash -notmatch '^[0-9A-Fa-f]{64}$' -or
            $expectedLength -lt 0) {
            Fail 'Acceptance package manifest contains an invalid file entry.'
        }

        if (-not $seen.Add($relative)) {
            Fail "Acceptance package manifest contains duplicate path '$relative'."
        }

        $nativeRelative = $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($nativeRelative)) {
            Fail "Acceptance package manifest contains rooted path '$relative'."
        }

        $fullPath = [IO.Path]::GetFullPath((Join-Path $packageRoot $nativeRelative))
        $rootPrefix = [IO.Path]::GetFullPath($packageRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Acceptance package manifest path escapes the package root: '$relative'."
        }

        if (-not [IO.File]::Exists($fullPath)) {
            Fail "Acceptance package file is missing: $relative"
        }

        $actualLength = (Get-Item -LiteralPath $fullPath).Length
        if ($actualLength -ne $expectedLength) {
            Fail "Acceptance package file length changed: $relative"
        }

        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Acceptance package file hash changed: $relative"
        }
    }

    $actualPackageFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
        Where-Object { -not [string]::Equals($_.FullName, $manifestPath, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/') })
    foreach ($actualFile in $actualPackageFiles) {
        if (-not $seen.Contains($actualFile)) {
            Fail "Acceptance package contains undeclared file '$actualFile'. Rebuild the package instead of modifying it in place."
        }
    }

    Write-Host "  [OK] Package manifest verified: $($declaredFiles.Count) files, commit $($manifest.commitSha)"
}

if ([string]::IsNullOrWhiteSpace($ApiBaseUrl)) {
    Fail 'STEWARD_API_BASE_URL is not configured.'
}

[Uri]$apiUri = $null
if (-not [Uri]::TryCreate($ApiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) {
    Fail 'STEWARD_API_BASE_URL is not an absolute URI.'
}

if ($apiUri.Scheme -ne 'https') {
    Fail 'Live E4 acceptance requires an HTTPS Steward API endpoint.'
}

[UInt32]$parsedAppId = 0
if (-not [UInt32]::TryParse($SteamAppId, [ref]$parsedAppId) -or $parsedAppId -eq 0) {
    Fail 'STEWARD_STEAM_APP_ID must be a positive Steam AppID.'
}

if ([string]::IsNullOrWhiteSpace($SteamWebApiIdentity) -or
    $SteamWebApiIdentity -match '\s' -or
    $SteamWebApiIdentity.Length -gt 128) {
    Fail 'STEWARD_STEAM_WEB_API_IDENTITY must be a non-empty identity without whitespace.'
}

$base = $apiUri.AbsoluteUri
if (-not $base.EndsWith('/')) {
    $base += '/'
}

Write-Host 'Steward E4 live acceptance preflight'
Write-Host "  API: $base"
Write-Host "  Steam AppID: $parsedAppId"
Write-Host "  Web API identity: $SteamWebApiIdentity"
Write-Host

foreach ($probe in @('health/live', 'health/ready')) {
    $uri = [Uri]::new($base + $probe)
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 15
    }
    catch {
        Fail "GET $uri failed: $($_.Exception.Message)"
    }

    if ($response.StatusCode -ne 200) {
        Fail "GET $uri returned HTTP $($response.StatusCode)."
    }

    Write-Host "  [OK] GET /$probe -> 200"
}

Write-Host
Write-Host 'Backend preflight passed.'
Write-Host 'The remaining proof is intentionally real: Steam ticket verification, Share/Invite, exact Factorio Verify, authority, transfer, host/save/commit, and the second installation handoff.'

if (-not [string]::IsNullOrWhiteSpace($DesktopExecutable)) {
    $desktopPath = [IO.Path]::GetFullPath($DesktopExecutable)
    if (-not [IO.File]::Exists($desktopPath)) {
        Fail "Desktop executable not found: $desktopPath"
    }

    Verify-AcceptancePackage $desktopPath

    $env:STEWARD_API_BASE_URL = $base
    $env:STEWARD_STEAM_APP_ID = $parsedAppId.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:STEWARD_STEAM_WEB_API_IDENTITY = $SteamWebApiIdentity

    Write-Host "Launching verified Steward package: $desktopPath"
    Start-Process -FilePath $desktopPath
}
