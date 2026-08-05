[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$EnvironmentFile,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$HostEnvironmentPath = '/etc/steward/backend.env',
    [switch]$RequireDeployable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Read-StrictEnvironmentFile([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    Require ([IO.File]::Exists($fullPath)) "Deployment environment file does not exist: $fullPath"
    $item = Get-Item -LiteralPath $fullPath
    Require ($null -eq $item.LinkType) 'Deployment environment file cannot be a symbolic link.'
    Require ($item.Length -gt 0 -and $item.Length -le 1MB) 'Deployment environment file must be 1 byte to 1 MiB.'

    $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $text = [IO.File]::ReadAllText($fullPath)
    Require ($text -notmatch '\x00') 'Deployment environment file contains a null byte.'

    $lineNumber = 0
    foreach ($rawLine in [Text.RegularExpressions.Regex]::Split($text, '\r?\n')) {
        $lineNumber++
        $line = $rawLine.Trim()
        if ([string]::IsNullOrEmpty($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }

        Require (-not $line.StartsWith('export ', [StringComparison]::Ordinal)) "Environment line $lineNumber cannot use shell export syntax."
        $separator = $line.IndexOf('=')
        Require ($separator -gt 0) "Environment line $lineNumber must use KEY=VALUE syntax."
        $key = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        Require ($key -match '^[A-Za-z_][A-Za-z0-9_]*$') "Environment line $lineNumber has an invalid key."
        Require ($entries.TryAdd($key, $value)) "Deployment environment key '$key' is duplicated."
    }

    Require ($entries.Count -gt 0 -and $entries.Count -le 512) 'Deployment environment key count is outside the supported bound.'
    return ,$entries
}

function Get-RequiredEnvironmentValue(
    [Collections.Generic.Dictionary[string, string]]$Entries,
    [string]$Key) {
    $value = $null
    Require ($Entries.TryGetValue($Key, [ref]$value)) "Required deployment environment key '$Key' is missing."
    Require (-not [string]::IsNullOrWhiteSpace($value)) "Deployment environment key '$Key' is empty."
    Require ($value.Length -le 8192) "Deployment environment key '$Key' exceeds the value bound."
    Require ($value -notmatch '^\s*<.*>\s*$') "Deployment environment key '$Key' still contains a placeholder."
    return $value
}

function Test-ReservedDnsHost([string]$HostName) {
    if ([string]::IsNullOrWhiteSpace($HostName)) {
        return $true
    }

    $normalized = $HostName.TrimEnd('.').ToLowerInvariant()
    if ($normalized -eq 'localhost' -or
        $normalized.EndsWith('.localhost', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('.invalid', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('.example', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('.test', [StringComparison]::Ordinal)) {
        return $true
    }

    $ipAddress = $null
    if ([Net.IPAddress]::TryParse($normalized, [ref]$ipAddress)) {
        return $true
    }

    return -not $normalized.Contains('.', [StringComparison]::Ordinal)
}

function ConvertTo-ShellSingleQuoted([string]$Value) {
    $singleQuote = [string][char]39
    $doubleQuote = [string][char]34
    $escapedQuote = $singleQuote + $doubleQuote + $singleQuote + $doubleQuote + $singleQuote
    return $singleQuote + $Value.Replace($singleQuote, $escapedQuote) + $singleQuote
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText(
        $Path,
        $Content.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false))
}

Require (-not [string]::IsNullOrWhiteSpace($HostEnvironmentPath)) 'HostEnvironmentPath is required.'
Require ($HostEnvironmentPath.StartsWith('/', [StringComparison]::Ordinal)) 'HostEnvironmentPath must be an absolute Linux path.'
Require ($HostEnvironmentPath.Length -le 260) 'HostEnvironmentPath is too long.'
Require ($HostEnvironmentPath -notmatch '[\x00-\x1F\x7F]') 'HostEnvironmentPath contains a control character.'
Require ($HostEnvironmentPath -notmatch '(^|/)\.\.?(/|$)') 'HostEnvironmentPath contains a traversal segment.'

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
& $verifierPath -BundleDirectory $bundleRoot

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$version = [string]$manifest.version
$commitSha = [string]$manifest.commitSha
$apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
$backendImageId = [string]$manifest.deployment.backendImageId
$backendImageTag = [string]$manifest.deployment.backendImageTag
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($backendImageId -match '^sha256:[0-9a-f]{64}$') 'Release backend image ID is malformed.'
Require (-not [string]::IsNullOrWhiteSpace($backendImageTag)) 'Release backend image tag is missing.'
Require ([int]$manifest.deployment.postgresMajorVersion -eq 17) 'Deployment plan requires PostgreSQL 17.'
Require ([string]$manifest.deployment.objectStorageProtocol -ceq 's3-compatible') 'Deployment plan requires S3-compatible object storage.'

$apiUri = $null
Require ([Uri]::TryCreate($apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) 'Release API base URL is malformed.'
Require ([string]::Equals($apiUri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase)) 'Release API base URL must use HTTPS.'
Require ([string]::IsNullOrEmpty($apiUri.UserInfo) -and
    [string]::IsNullOrEmpty($apiUri.Query) -and
    [string]::IsNullOrEmpty($apiUri.Fragment)) 'Release API base URL contains forbidden components.'
Require ($apiUri.AbsolutePath -ceq '/') 'Deployment-ready API base URL must use the origin root path.'
Require ($apiUri.IsDefaultPort) 'Deployment-ready API base URL must use the default HTTPS port.'
$apiHost = $apiUri.DnsSafeHost
$apiHostReserved = Test-ReservedDnsHost $apiHost

$backendArtifacts = @($manifest.artifacts | Where-Object {
    ([string]$_.path).StartsWith('backend/', [StringComparison]::Ordinal) -and
    ([string]$_.path).EndsWith('.tar', [StringComparison]::OrdinalIgnoreCase)
})
Require ($backendArtifacts.Count -eq 1) 'Release bundle must contain exactly one backend TAR.'
$backendArtifact = $backendArtifacts[0]
$backendTarRelativePath = [string]$backendArtifact.path
$backendTarSha256 = [string]$backendArtifact.sha256
$backendTarByteSize = [int64]$backendArtifact.byteSize
Require ($backendTarRelativePath -match '^backend/[A-Za-z0-9._-]+\.tar$') 'Backend TAR path is not deployment-safe.'
Require ($backendTarSha256 -match '^[0-9A-F]{64}$') 'Backend TAR SHA-256 is malformed.'
Require ($backendTarByteSize -gt 0) 'Backend TAR byte size must be positive.'

$entries = Read-StrictEnvironmentFile $EnvironmentFile
$allowedExactKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($key in @(
    'PORT',
    'ConnectionStrings__Steward',
    'ObjectStorage__ServiceUrl',
    'ObjectStorage__AuthenticationRegion',
    'ObjectStorage__BucketName',
    'ObjectStorage__AccessKeyId',
    'ObjectStorage__SecretAccessKey',
    'ObjectStorage__ForcePathStyle',
    'FriendsBuild__Enabled',
    'ReverseProxy__KnownProxyIp',
    'Cleanup__IntervalMinutes',
    'Cleanup__VerifiedCandidateRetentionDays',
    'Cleanup__BatchSize'
)) {
    [void]$allowedExactKeys.Add($key)
}

$identityFields = @{}
foreach ($entry in $entries.GetEnumerator()) {
    $key = $entry.Key
    Require (-not $key.StartsWith('Steam__', [StringComparison]::Ordinal)) 'Friends Build deployment cannot contain Steam configuration.'
    Require (-not [string]::Equals($key, 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', [StringComparison]::Ordinal)) 'Broad ASP.NET forwarded-header trust is forbidden.'
    if ($allowedExactKeys.Contains($key)) {
        continue
    }

    $identityMatch = [Text.RegularExpressions.Regex]::Match(
        $key,
        '^FriendsBuild__Identities__(?<index>[0-9]{1,2})__(?<field>Id|DisplayName|CredentialSha256)$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    Require $identityMatch.Success "Unsupported deployment environment key '$key'."
    $index = [int]$identityMatch.Groups['index'].Value
    Require ($index -ge 0 -and $index -lt 64) "Friends Build identity index $index is outside the supported bound."
    if (-not $identityFields.ContainsKey($index)) {
        $identityFields[$index] = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    }
    $field = $identityMatch.Groups['field'].Value
    Require ($identityFields[$index].TryAdd($field, $entry.Value)) "Friends Build identity $index field '$field' is duplicated."
}

Require ((Get-RequiredEnvironmentValue -Entries $entries -Key 'PORT') -ceq '8080') 'PORT must be exactly 8080 for the one-host Caddy deployment.'
Require ((Get-RequiredEnvironmentValue -Entries $entries -Key 'FriendsBuild__Enabled') -ceq 'true') 'FriendsBuild__Enabled must be exactly true.'
Require ((Get-RequiredEnvironmentValue -Entries $entries -Key 'ReverseProxy__KnownProxyIp') -ceq '127.0.0.1') 'ReverseProxy__KnownProxyIp must be exactly 127.0.0.1.'

$connectionString = Get-RequiredEnvironmentValue -Entries $entries -Key 'ConnectionStrings__Steward'
$connectionBuilder = [Data.Common.DbConnectionStringBuilder]::new()
try {
    $connectionBuilder.ConnectionString = $connectionString
}
catch {
    throw 'ConnectionStrings__Steward is not a valid connection string.'
}

$hostAliases = @('Host', 'Server', 'Data Source')
$databaseAliases = @('Database', 'Initial Catalog')
$usernameAliases = @('Username', 'User ID', 'User')
$passwordAliases = @('Password', 'Pwd')
$presentHostAliases = @($hostAliases | Where-Object { $connectionBuilder.ContainsKey($_) })
$presentDatabaseAliases = @($databaseAliases | Where-Object { $connectionBuilder.ContainsKey($_) })
$presentUsernameAliases = @($usernameAliases | Where-Object { $connectionBuilder.ContainsKey($_) })
$presentPasswordAliases = @($passwordAliases | Where-Object { $connectionBuilder.ContainsKey($_) })
Require ($presentHostAliases.Count -eq 1) 'PostgreSQL host is missing or ambiguous.'
Require ($presentDatabaseAliases.Count -eq 1) 'PostgreSQL database is missing or ambiguous.'
Require ($presentUsernameAliases.Count -eq 1) 'PostgreSQL username is missing or ambiguous.'
Require ($presentPasswordAliases.Count -eq 1) 'PostgreSQL password is missing or ambiguous.'
Require ($connectionBuilder.ContainsKey('SSL Mode')) 'PostgreSQL SSL Mode is missing.'

$postgresHost = [string]$connectionBuilder[$presentHostAliases[0]]
$postgresDatabase = [string]$connectionBuilder[$presentDatabaseAliases[0]]
$postgresUsername = [string]$connectionBuilder[$presentUsernameAliases[0]]
$postgresPassword = [string]$connectionBuilder[$presentPasswordAliases[0]]
$postgresSslMode = [string]$connectionBuilder['SSL Mode']
Require (-not [string]::IsNullOrWhiteSpace($postgresHost)) 'PostgreSQL host is empty.'
Require (-not [string]::IsNullOrWhiteSpace($postgresDatabase)) 'PostgreSQL database is empty.'
Require (-not [string]::IsNullOrWhiteSpace($postgresUsername)) 'PostgreSQL username is empty.'
Require (-not [string]::IsNullOrWhiteSpace($postgresPassword)) 'PostgreSQL password is empty.'
Require ([string]::Equals($postgresSslMode.Replace(' ', ''), 'VerifyFull', [StringComparison]::OrdinalIgnoreCase)) 'PostgreSQL SSL Mode must be VerifyFull.'
Require ($postgresHost.Length -le 255 -and
    $postgresDatabase.Length -le 128 -and
    $postgresUsername.Length -le 128) 'PostgreSQL connection identity exceeds a supported bound.'
Require ($postgresPassword.Length -ge 16) 'PostgreSQL password must contain at least 16 characters.'
if ($connectionBuilder.ContainsKey('Trust Server Certificate')) {
    Require ([string]::Equals(
        [string]$connectionBuilder['Trust Server Certificate'],
        'false',
        [StringComparison]::OrdinalIgnoreCase)) 'Trust Server Certificate must be false when configured.'
}

$objectStorageUriText = Get-RequiredEnvironmentValue -Entries $entries -Key 'ObjectStorage__ServiceUrl'
$objectStorageUri = $null
Require ([Uri]::TryCreate($objectStorageUriText, [UriKind]::Absolute, [ref]$objectStorageUri)) 'ObjectStorage__ServiceUrl is malformed.'
Require ([string]::Equals($objectStorageUri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase)) 'ObjectStorage__ServiceUrl must use HTTPS.'
Require ([string]::IsNullOrEmpty($objectStorageUri.UserInfo) -and
    [string]::IsNullOrEmpty($objectStorageUri.Query) -and
    [string]::IsNullOrEmpty($objectStorageUri.Fragment)) 'ObjectStorage__ServiceUrl contains forbidden components.'
$objectStorageRegion = Get-RequiredEnvironmentValue -Entries $entries -Key 'ObjectStorage__AuthenticationRegion'
$objectStorageBucket = Get-RequiredEnvironmentValue -Entries $entries -Key 'ObjectStorage__BucketName'
$objectStorageAccessKey = Get-RequiredEnvironmentValue -Entries $entries -Key 'ObjectStorage__AccessKeyId'
$objectStorageSecretKey = Get-RequiredEnvironmentValue -Entries $entries -Key 'ObjectStorage__SecretAccessKey'
$forcePathStyleText = Get-RequiredEnvironmentValue -Entries $entries -Key 'ObjectStorage__ForcePathStyle'
Require ($forcePathStyleText -in @('true', 'false')) 'ObjectStorage__ForcePathStyle must be exactly true or false.'
Require ($objectStorageRegion.Length -le 64 -and $objectStorageBucket.Length -le 255) 'Object-storage region or bucket exceeds a supported bound.'
Require ($objectStorageAccessKey.Length -ge 12) 'Object-storage access key is too short for a real deployment.'
Require ($objectStorageSecretKey.Length -ge 24) 'Object-storage secret key is too short for a real deployment.'

Require ($identityFields.Count -ge 1 -and $identityFields.Count -le 64) 'At least one and at most 64 Friends Build identities are required.'
$indices = @($identityFields.Keys | Sort-Object)
for ($expectedIndex = 0; $expectedIndex -lt $indices.Count; $expectedIndex++) {
    Require ($indices[$expectedIndex] -eq $expectedIndex) 'Friends Build identity indices must be contiguous from zero.'
}

$seenIdentityIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenIdentityHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($index in $indices) {
    $fields = $identityFields[$index]
    Require ($fields.Count -eq 3) "Friends Build identity $index must contain exactly Id, DisplayName, and CredentialSha256."
    foreach ($fieldName in @('Id', 'DisplayName', 'CredentialSha256')) {
        Require ($fields.ContainsKey($fieldName)) "Friends Build identity $index is missing $fieldName."
    }

    $identityId = $fields['Id']
    $displayName = $fields['DisplayName']
    $credentialHash = $fields['CredentialSha256']
    Require (-not [string]::IsNullOrWhiteSpace($identityId) -and
        $identityId.Length -le 128 -and
        $identityId -notmatch '[\x00-\x1F\x7F]') "Friends Build identity $index has an invalid Id."
    Require (-not [string]::IsNullOrWhiteSpace($displayName) -and
        $displayName.Length -le 128 -and
        $displayName -notmatch '[\x00-\x1F\x7F]') "Friends Build identity $index has an invalid DisplayName."
    Require ($credentialHash -match '^[0-9A-Fa-f]{64}$') "Friends Build identity $index has a malformed credential SHA-256."
    Require ($seenIdentityIds.Add($identityId)) "Friends Build identity Id '$identityId' is duplicated."
    Require ($seenIdentityHashes.Add($credentialHash)) "Friends Build identity $index reuses another credential digest."
}

foreach ($boundedKey in @(
    @{ Name = 'Cleanup__IntervalMinutes'; Minimum = 1; Maximum = 1440 },
    @{ Name = 'Cleanup__VerifiedCandidateRetentionDays'; Minimum = 1; Maximum = 365 },
    @{ Name = 'Cleanup__BatchSize'; Minimum = 1; Maximum = 1000 }
)) {
    $value = $null
    if ($entries.TryGetValue($boundedKey.Name, [ref]$value)) {
        $parsed = 0
        Require ([int]::TryParse(
            $value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) "Deployment environment key '$($boundedKey.Name)' must be an integer."
        Require ($parsed -ge $boundedKey.Minimum -and
            $parsed -le $boundedKey.Maximum) "Deployment environment key '$($boundedKey.Name)' is outside its supported bound."
    }
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($outputRoot)) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$caddyFileName = 'Caddyfile'
$deployScriptName = 'deploy-exact-candidate.sh'
$verifyScriptName = 'verify-public-https.sh'
$caddyText = "$apiHost {`n    reverse_proxy 127.0.0.1:8080`n}`n"
Write-Utf8NoBom -Path (Join-Path $outputRoot $caddyFileName) -Content $caddyText

$deployTemplate = @'
#!/usr/bin/env bash
set -euo pipefail

bundle_dir="${1:?usage: deploy-exact-candidate.sh <bundle-directory>}"
env_file=__ENV_FILE__
image_id=__IMAGE_ID__
image_tag=__IMAGE_TAG__
tar_relative=__TAR_RELATIVE__
tar_sha256=__TAR_SHA256__
tar_path="${bundle_dir}/${tar_relative}"

[[ -f "${tar_path}" ]] || { echo "backend TAR is missing: ${tar_path}" >&2; exit 1; }
[[ -r "${env_file}" ]] || { echo "backend environment is not readable: ${env_file}" >&2; exit 1; }
actual_hash="$(sha256sum "${tar_path}" | awk '{print tolower($1)}')"
[[ "${actual_hash}" == "${tar_sha256}" ]] || { echo 'backend TAR SHA-256 mismatch' >&2; exit 1; }

docker load --input "${tar_path}" >/dev/null
loaded_id="$(docker image inspect "${image_tag}" --format '{{.Id}}')"
[[ "${loaded_id}" == "${image_id}" ]] || { echo 'loaded backend image ID mismatch' >&2; exit 1; }
loaded_user="$(docker image inspect "${image_id}" --format '{{.Config.User}}')"
[[ "${loaded_user}" == 'app' ]] || { echo 'backend image does not use runtime user app' >&2; exit 1; }

docker rm --force steward-backend >/dev/null 2>&1 || true
docker run --detach \
  --name steward-backend \
  --restart unless-stopped \
  --network host \
  --env-file "${env_file}" \
  "${image_id}" >/dev/null

for attempt in $(seq 1 90); do
  if curl --fail --silent --show-error --max-time 2 http://127.0.0.1:8080/health/ready >/dev/null; then
    running_id="$(docker container inspect steward-backend --format '{{.Image}}')"
    [[ "${running_id}" == "${image_id}" ]] || { echo 'running backend image ID mismatch' >&2; exit 1; }
    echo 'exact backend candidate is ready on 127.0.0.1:8080'
    exit 0
  fi
  sleep 1
done

docker logs steward-backend >&2 || true
echo 'backend did not become ready' >&2
exit 1
'@
$deployText = $deployTemplate
$deployText = $deployText.Replace('__ENV_FILE__', (ConvertTo-ShellSingleQuoted $HostEnvironmentPath))
$deployText = $deployText.Replace('__IMAGE_ID__', (ConvertTo-ShellSingleQuoted $backendImageId))
$deployText = $deployText.Replace('__IMAGE_TAG__', (ConvertTo-ShellSingleQuoted $backendImageTag))
$deployText = $deployText.Replace('__TAR_RELATIVE__', (ConvertTo-ShellSingleQuoted $backendTarRelativePath))
$deployText = $deployText.Replace('__TAR_SHA256__', (ConvertTo-ShellSingleQuoted $backendTarSha256.ToLowerInvariant()))
Write-Utf8NoBom -Path (Join-Path $outputRoot $deployScriptName) -Content $deployText

$verifyTemplate = @'
#!/usr/bin/env bash
set -euo pipefail

api_base_url=__API_BASE_URL__
curl --fail --silent --show-error --proto '=https' --tlsv1.2 --max-time 15 "${api_base_url}health/live" >/dev/null
curl --fail --silent --show-error --proto '=https' --tlsv1.2 --max-time 15 "${api_base_url}health/ready" >/dev/null
echo "public HTTPS health passed for ${api_base_url}"
'@
$verifyText = $verifyTemplate.Replace('__API_BASE_URL__', (ConvertTo-ShellSingleQuoted $apiBaseUrl))
Write-Utf8NoBom -Path (Join-Path $outputRoot $verifyScriptName) -Content $verifyText

$deployable = -not $apiHostReserved
$deployabilityReason = if ($deployable) {
    'The release API host is a non-reserved DNS name and the supplied secret environment satisfies the strict deployment contract.'
}
else {
    'The release API host is reserved, local, an IP literal, or not a fully qualified DNS name. Generate a new candidate with the real public HTTPS API URL.'
}

$generatedFiles = @()
foreach ($name in @($caddyFileName, $deployScriptName, $verifyScriptName)) {
    $path = Join-Path $outputRoot $name
    $file = Get-Item -LiteralPath $path
    $generatedFiles += [ordered]@{
        path = $name
        byteSize = $file.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

$plan = [ordered]@{
    documentType = 'steward.closed-alpha-deployment-plan'
    schemaVersion = 1
    release = [ordered]@{
        version = $version
        commitSha = $commitSha
        apiBaseUrl = $apiBaseUrl
        backendImageTag = $backendImageTag
        backendImageId = $backendImageId
        backendTarPath = $backendTarRelativePath
        backendTarByteSize = $backendTarByteSize
        backendTarSha256 = $backendTarSha256
    }
    host = [ordered]@{
        apiHost = $apiHost
        backendPort = 8080
        knownProxyIp = '127.0.0.1'
        environmentFilePath = $HostEnvironmentPath
        caddyFile = $caddyFileName
    }
    configuration = [ordered]@{
        postgresMajorVersion = 17
        postgresTlsMode = 'VerifyFull'
        objectStorageServiceScheme = 'https'
        objectStorageForcePathStyle = [bool]::Parse($forcePathStyleText)
        friendsBuildIdentityCount = $identityFields.Count
        steamConfigurationPresent = $false
        broadForwardedHeadersEnabled = $false
        secretValuesCopied = $false
    }
    deployability = [ordered]@{
        deployable = $deployable
        reason = $deployabilityReason
    }
    generatedFiles = $generatedFiles
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$planPath = Join-Path $outputRoot 'deployment-plan.json'
Write-Utf8NoBom -Path $planPath -Content ($plan | ConvertTo-Json -Depth 8)

Write-Host
Write-Host '[OK] Closed-alpha deployment plan generated without copying secret values.'
Write-Host "  Release: $version ($commitSha)"
Write-Host "  API: $apiBaseUrl"
Write-Host "  Backend image ID: $backendImageId"
Write-Host "  Friends Build identities: $($identityFields.Count)"
Write-Host "  Output: $outputRoot"
if ($deployable) {
    Write-Host '  Deployment readiness: READY FOR OPERATOR EXECUTION'
}
else {
    Write-Host '  Deployment readiness: BLOCKED BY RESERVED API HOST'
}

if ($RequireDeployable.IsPresent -and -not $deployable) {
    throw "Deployment plan is structurally valid but not deployable: $deployabilityReason"
}
