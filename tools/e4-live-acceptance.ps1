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

    $env:STEWARD_API_BASE_URL = $base
    $env:STEWARD_STEAM_APP_ID = $parsedAppId.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:STEWARD_STEAM_WEB_API_IDENTITY = $SteamWebApiIdentity

    Write-Host "Launching Steward: $desktopPath"
    Start-Process -FilePath $desktopPath
}
