[CmdletBinding()]
param(
    [string]$ConfigurationPath = (Join-Path $PSScriptRoot 'steward-beta.json'),
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

function Assert-ExactProperties($Object, [string[]]$Expected, [string]$Name) {
    $actual = @($Object.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if (($actual -join "`n") -cne ($wanted -join "`n")) {
        Fail "$Name has an unsupported property set."
    }
}

function Verify-ImmutableClient([string]$AppRoot) {
    $manifestPath = Join-Path $AppRoot 'acceptance-build.json'
    $executablePath = Join-Path $AppRoot 'SharedWorlds.Desktop.exe'
    if (-not [IO.File]::Exists($manifestPath) -or -not [IO.File]::Exists($executablePath)) {
        Fail 'The Steward client package is incomplete.'
    }

    if ([IO.File]::Exists((Join-Path $AppRoot 'steward-friends-build.json')) -or
        [IO.File]::Exists((Join-Path $AppRoot 'steward-steam-release.json'))) {
        Fail 'The closed-beta client must not contain a build-time remote endpoint configuration.'
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        Fail "Client manifest is invalid JSON: $($_.Exception.Message)"
    }

    if ([string]$manifest.documentType -cne 'steward.e4-desktop-acceptance-build' -or
        [int]$manifest.schemaVersion -ne 2 -or
        [string]$manifest.executable -cne 'SharedWorlds.Desktop.exe') {
        Fail 'Client manifest identity is unsupported.'
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        $expectedHash = [string]$entry.sha256
        [int64]$expectedLength = $entry.byteSize
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $expectedHash -notmatch '^[0-9A-Fa-f]{64}$' -or
            $expectedLength -lt 0 -or
            -not $seen.Add($relative)) {
            Fail 'Client manifest contains an invalid or duplicate file entry.'
        }

        $nativeRelative = $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($nativeRelative)) {
            Fail "Client manifest contains rooted path '$relative'."
        }

        $fullPath = [IO.Path]::GetFullPath((Join-Path $AppRoot $nativeRelative))
        $rootPrefix = [IO.Path]::GetFullPath($AppRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Client manifest path escapes the app root: '$relative'."
        }
        if (-not [IO.File]::Exists($fullPath)) {
            Fail "Client file is missing: $relative"
        }
        if ((Get-Item -LiteralPath $fullPath).Length -ne $expectedLength) {
            Fail "Client file length changed: $relative"
        }
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Client file hash changed: $relative"
        }
    }

    Write-Host "[OK] Steward client verified: $($seen.Count) immutable files, commit $($manifest.commitSha)"
    return $executablePath
}

$configPath = [IO.Path]::GetFullPath($ConfigurationPath)
if (-not [IO.File]::Exists($configPath)) {
    Fail "Closed-beta configuration is missing: $configPath"
}

try {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
}
catch {
    Fail "Closed-beta configuration is invalid JSON: $($_.Exception.Message)"
}
Assert-ExactProperties $config @('schemaVersion', 'apiBaseUrl', 'authMode') 'Closed-beta configuration'
if ([int]$config.schemaVersion -ne 1 -or [string]$config.authMode -cne 'friends-build') {
    Fail 'Closed-beta configuration has an unsupported schema or authentication mode.'
}

[Uri]$apiUri = $null
if (-not [Uri]::TryCreate([string]$config.apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    -not [string]::Equals($apiUri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::IsNullOrEmpty($apiUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($apiUri.Query) -or
    -not [string]::IsNullOrEmpty($apiUri.Fragment)) {
    Fail 'Closed-beta apiBaseUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
}
$apiBaseUrl = $apiUri.AbsoluteUri
if (-not $apiBaseUrl.EndsWith('/', [StringComparison]::Ordinal)) {
    $apiBaseUrl += '/'
}

$appRoot = Join-Path $PSScriptRoot 'app'
$desktopExecutable = Verify-ImmutableClient $appRoot

$env:STEWARD_API_BASE_URL = $apiBaseUrl
$env:STEWARD_AUTH_MODE = 'friends-build'
Remove-Item Env:STEWARD_STEAM_APP_ID -ErrorAction SilentlyContinue
Remove-Item Env:STEWARD_STEAM_WEB_API_IDENTITY -ErrorAction SilentlyContinue

Write-Host "Steward Closed Beta -> $apiBaseUrl"
if ($VerifyOnly.IsPresent) {
    Write-Host '[OK] Closed-beta client configuration and immutable app package are valid.'
    return
}

Write-Host 'Launching Steward...'
Start-Process -FilePath $desktopExecutable -WorkingDirectory $appRoot
