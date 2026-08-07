[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [Parameter(Mandatory = $true)]
    [string]$BackendDeploymentEvidencePath,
    [Parameter(Mandatory = $true)]
    [string]$SshPrivateKeyPath,
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$MaximumBackendDeploymentAge = [TimeSpan]::FromMinutes(15)
$MaximumClockSkew = [TimeSpan]::FromMinutes(2)

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Require-Tool([string]$Name) {
    Require ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue)) "Required local tool '$Name' is not available."
}

function Require-ExactProperties([object]$Value, [string[]]$Expected, [string]$Context) {
    Require ($null -ne $Value) "$Context is null."
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    Require ($actual.Count -eq $expectedSorted.Count) "$Context has an unexpected property count."
    Require (@(Compare-Object $actual $expectedSorted).Count -eq 0) "$Context contains missing or unexpected properties."
}

function Get-ArtifactByPath([object[]]$Artifacts, [string]$Path) {
    $matches = @($Artifacts | Where-Object { [string]$_.path -ceq $Path })
    Require ($matches.Count -eq 1) "Release artifact '$Path' must appear exactly once."
    return $matches[0]
}

function Test-PublicIpv4([Net.IPAddress]$Address) {
    if ($Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $bytes = $Address.GetAddressBytes()
    if ($bytes.Length -ne 4) { return $false }
    $a = [int]$bytes[0]; $b = [int]$bytes[1]; $c = [int]$bytes[2]
    if ($a -eq 0 -or $a -eq 10 -or $a -eq 127 -or $a -ge 224) { return $false }
    if ($a -eq 100 -and $b -ge 64 -and $b -le 127) { return $false }
    if ($a -eq 169 -and $b -eq 254) { return $false }
    if ($a -eq 172 -and $b -ge 16 -and $b -le 31) { return $false }
    if ($a -eq 192 -and $b -eq 0 -and ($c -eq 0 -or $c -eq 2)) { return $false }
    if ($a -eq 192 -and $b -eq 168) { return $false }
    if ($a -eq 198 -and ($b -eq 18 -or $b -eq 19)) { return $false }
    if ($a -eq 198 -and $b -eq 51 -and $c -eq 100) { return $false }
    if ($a -eq 203 -and $b -eq 0 -and $c -eq 113) { return $false }
    return $true
}

function ConvertTo-ShellSingleQuoted([string]$Value) {
    $singleQuote = [string][char]39
    $doubleQuote = [string][char]34
    $escapedQuote = $singleQuote + $doubleQuote + $singleQuote + $doubleQuote + $singleQuote
    return $singleQuote + $Value.Replace($singleQuote, $escapedQuote) + $singleQuote
}

function Invoke-RequiredNative([string]$Tool, [string[]]$Arguments, [string]$Context, [switch]$Capture) {
    $output = @(& $Tool @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $text = ($output -join [Environment]::NewLine).Trim()
        throw "$Context failed with exit code $LASTEXITCODE. $text"
    }
    if ($Capture.IsPresent) { return ($output -join [Environment]::NewLine).Trim() }
    foreach ($line in $output) { Write-Host $line }
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Read-BoundedJson([string]$Path, [int64]$MaximumBytes, [string]$Context) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    Require ([IO.File]::Exists($fullPath)) "$Context does not exist: $fullPath"
    $item = Get-Item -LiteralPath $fullPath
    Require ($null -eq $item.LinkType) "$Context cannot be a symbolic link."
    Require ($item.Length -gt 0 -and $item.Length -le $MaximumBytes) "$Context is empty or exceeds its byte bound."
    $text = [IO.File]::ReadAllText($fullPath)
    return [ordered]@{ FullPath = $fullPath; Text = $text; Json = ($text | ConvertFrom-Json) }
}

foreach ($tool in @('ssh','ssh-keygen','ssh-keyscan')) { Require-Tool $tool }

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'
& $verifierPath -BundleDirectory $bundleRoot

$selfPath = [IO.Path]::GetFullPath($PSCommandPath)
$bundledSelfPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'activate-closed-alpha-live-ingress.ps1'))
Require ([string]::Equals($selfPath, $bundledSelfPath, [StringComparison]::OrdinalIgnoreCase)) 'Run live ingress activation from inside the verified release bundle.'

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$artifacts = @($manifest.artifacts)
$selfArtifact = Get-ArtifactByPath $artifacts 'activate-closed-alpha-live-ingress.ps1'
$selfItem = Get-Item -LiteralPath $selfPath
Require ($null -eq $selfItem.LinkType) 'Live ingress activator cannot be a symbolic link.'
Require ($selfItem.Length -eq [int64]$selfArtifact.byteSize) 'Live ingress activator byte size does not match the release manifest.'
Require ((Get-FileHash -LiteralPath $selfPath -Algorithm SHA256).Hash -ceq [string]$selfArtifact.sha256) 'Live ingress activator SHA-256 does not match the release manifest.'

$version = [string]$manifest.version
$commitSha = [string]$manifest.commitSha
$apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
$imageId = [string]$manifest.deployment.backendImageId
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($imageId -match '^sha256:[0-9a-f]{64}$') 'Release backend image ID is malformed.'
Require ([string]$manifest.deployment.targetPlatform -ceq 'linux/amd64') 'Live ingress activation requires linux/amd64.'
Require ([string]$manifest.deployment.backendRuntimeUser -ceq 'app') 'Live ingress activation requires runtime user app.'
Require (-not [bool]$manifest.publishAuthorization.publishAllowed) 'Live ingress activation cannot use a publish-authorized candidate.'
Require ([string]$manifest.publishAuthorization.physicalBringHere -ceq 'deferred') 'Live ingress activation expects physical Bring Here to remain deferred.'

$requestFile = Read-BoundedJson $RequestPath 1MB 'Live deployment request'
$request = $requestFile.Json
Require-ExactProperties $request @('createdAtUtc','documentType','executionContract','host','network','publication','release','schemaVersion','secretBoundary') 'Live deployment request'
Require ([string]$request.documentType -ceq 'steward.closed-alpha-live-deployment-request' -and [int]$request.schemaVersion -eq 1) 'Live deployment request identity is invalid.'
Require-ExactProperties $request.release @('apiBaseUrl','backendImageId','backendImageTag','backendTar','commitSha','version') 'Live deployment request release'
Require-ExactProperties $request.host @('apiHost','caddyConfigurationPath','expectedPublicIpv4','remoteDeploymentPlanDirectory','remoteEnvironmentPath','remoteReleaseDirectory','requiredTools','sshHost','sshHostKeySha256','sshPort','sshUser','targetPlatform','topology') 'Live deployment request host'
Require-ExactProperties $request.network @('backendKnownProxyIp','backendPort','caddyUpstream','forbiddenPublicBackendPort','publicBackendPortMustFailClosed','publicCertificateBootstrapPort','publicTlsPort','restrictedAdministrativePort') 'Live deployment request network'
Require-ExactProperties $request.publication @('authorizationChanged','physicalBringHere','publishAllowed','requestAuthorizesPublication') 'Live deployment request publication'
Require ([string]$request.release.version -ceq $version -and [string]$request.release.commitSha -ceq $commitSha) 'Request release identity does not match the candidate.'
Require ([string]$request.release.apiBaseUrl -ceq $apiBaseUrl -and [string]$request.release.backendImageId -ceq $imageId) 'Request deployment identity does not match the candidate.'
Require ([string]$request.host.topology -ceq 'single-linux-amd64-host') 'Unsupported live-host topology.'
Require ([string]$request.host.remoteReleaseDirectory -ceq "/srv/steward/releases/$commitSha") 'Request release directory changed.'
Require ([string]$request.host.remoteDeploymentPlanDirectory -ceq "/srv/steward/deployment-plans/$commitSha") 'Request deployment-plan directory changed.'
Require ([string]$request.host.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Request environment path changed.'
Require ([string]$request.host.caddyConfigurationPath -ceq '/etc/caddy/Caddyfile') 'Request Caddy path changed.'
Require ([int]$request.network.publicTlsPort -eq 443 -and [int]$request.network.publicCertificateBootstrapPort -eq 80) 'Request public ingress ports changed.'
Require ([int]$request.network.backendPort -eq 8080 -and [int]$request.network.forbiddenPublicBackendPort -eq 8080 -and [bool]$request.network.publicBackendPortMustFailClosed) 'Request backend-port contract changed.'
Require ([string]$request.network.caddyUpstream -ceq '127.0.0.1:8080' -and [string]$request.network.backendKnownProxyIp -ceq '127.0.0.1') 'Request one-proxy contract changed.'
Require (-not [bool]$request.publication.publishAllowed -and [string]$request.publication.physicalBringHere -ceq 'deferred' -and -not [bool]$request.publication.authorizationChanged -and -not [bool]$request.publication.requestAuthorizesPublication) 'Request publication state is incompatible with staged closed alpha.'

$apiUri = $null
Require ([Uri]::TryCreate($apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) 'Candidate API base URL is malformed.'
Require ([string]$apiUri.Scheme -ceq 'https') 'Live ingress activation requires an HTTPS API base URL.'
$apiHost = $apiUri.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
Require ([string]$request.host.apiHost -ceq $apiHost -and [string]$request.host.sshHost -ceq $apiHost) 'Request API/SSH host does not match the candidate API host.'

$requestSha256 = (Get-FileHash -LiteralPath $requestFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()
$backendFile = Read-BoundedJson $BackendDeploymentEvidencePath 1MB 'Live backend deployment evidence'
$backend = $backendFile.Json
Require-ExactProperties $backend @(
    'apiBaseUrl','apiHost','backendContainerRunning','backendContainerStarted','backendDeploymentAgeSecondsAtStart',
    'backendImageId','backendImageLoaded','backendLocalLive','backendLocalReady','backendPort8080Listening','backendRuntimeUser',
    'caddyModified','caddyStateAfter','caddyStateBefore','completedAtUtc','deploymentPlanReverified','deploymentPlanSha256',
    'dnsReverified','documentType','externalPort8080ClosedVerified','physicalBringHere','protectedEnvironmentMetadataPreserved',
    'protectedEnvironmentTransferred','publicationAuthorizationChanged','publicHttpsVerified','publishAllowed','releaseCommitSha',
    'releaseVersion','remoteBundleReverified','remoteDeploymentPlanDirectory','remoteEnvironmentPath','remoteReleaseDirectory',
    'requestSha256','resolvedPublicIpv4','schemaVersion','sshHostKeyReverified','sshHostKeySha256','sshPort','sshUser',
    'stagingAgeSecondsAtStart','stagingCompletedAtUtc','stagingEvidenceSha256','stagingFresh'
) 'Live backend deployment evidence'
Require ([string]$backend.documentType -ceq 'steward.closed-alpha-live-backend-deployment' -and [int]$backend.schemaVersion -eq 1) 'Live backend deployment evidence identity is invalid.'
Require ([string]$backend.requestSha256 -ceq $requestSha256) 'Backend deployment evidence does not bind the exact live deployment request.'
Require ([string]$backend.releaseVersion -ceq $version -and [string]$backend.releaseCommitSha -ceq $commitSha) 'Backend deployment evidence does not bind the exact release.'
Require ([string]$backend.apiBaseUrl -ceq $apiBaseUrl -and [string]$backend.apiHost -ceq $apiHost) 'Backend deployment evidence API identity changed.'
Require ([string]$backend.resolvedPublicIpv4 -ceq [string]$request.host.expectedPublicIpv4) 'Backend deployment DNS address does not match the request.'
Require ([string]$backend.sshUser -ceq [string]$request.host.sshUser -and [int]$backend.sshPort -eq [int]$request.host.sshPort -and [string]$backend.sshHostKeySha256 -ceq [string]$request.host.sshHostKeySha256) 'Backend deployment SSH identity does not match the request.'
Require ([string]$backend.remoteReleaseDirectory -ceq [string]$request.host.remoteReleaseDirectory -and [string]$backend.remoteDeploymentPlanDirectory -ceq [string]$request.host.remoteDeploymentPlanDirectory -and [string]$backend.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Backend deployment remote paths changed.'
Require ([bool]$backend.stagingFresh -and [bool]$backend.dnsReverified -and [bool]$backend.sshHostKeyReverified) 'Backend deployment evidence did not prove staged host identity.'
Require ([bool]$backend.remoteBundleReverified -and [bool]$backend.deploymentPlanReverified -and [string]$backend.deploymentPlanSha256 -match '^[0-9A-F]{64}$') 'Backend deployment evidence did not prove exact staged bytes.'
Require ([bool]$backend.backendImageLoaded -and [bool]$backend.backendContainerStarted -and [bool]$backend.backendContainerRunning) 'Backend deployment evidence did not prove a running backend.'
Require ([string]$backend.backendImageId -ceq $imageId -and [string]$backend.backendRuntimeUser -ceq 'app') 'Backend deployment evidence lost exact image/runtime identity.'
Require ([bool]$backend.backendLocalLive -and [bool]$backend.backendLocalReady -and [bool]$backend.backendPort8080Listening) 'Backend deployment evidence did not prove local backend readiness.'
Require ([bool]$backend.protectedEnvironmentMetadataPreserved -and -not [bool]$backend.protectedEnvironmentTransferred) 'Backend deployment evidence crossed the protected environment boundary.'
Require (-not [bool]$backend.caddyModified -and [string]$backend.caddyStateBefore -ceq [string]$backend.caddyStateAfter) 'Backend deployment evidence changed Caddy state.'
Require (-not [bool]$backend.publicHttpsVerified -and -not [bool]$backend.externalPort8080ClosedVerified) 'Backend deployment evidence already claims external ingress acceptance.'
Require (-not [bool]$backend.publishAllowed -and [string]$backend.physicalBringHere -ceq 'deferred' -and -not [bool]$backend.publicationAuthorizationChanged) 'Backend deployment evidence changed publication state.'
Require ([string]$backend.caddyStateAfter -match '^sha256:[0-9A-F]{64}$') 'Live ingress activation requires a pre-existing byte-bound Caddy configuration for rollback.'

$backendCompletedAt = [DateTimeOffset]::MinValue
Require ([DateTimeOffset]::TryParse([string]$backend.completedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$backendCompletedAt)) 'Backend deployment completedAtUtc is malformed.'
$now = [DateTimeOffset]::UtcNow
$backendAge = $now - $backendCompletedAt.ToUniversalTime()
Require ($backendAge -ge $MaximumClockSkew.Negate()) 'Backend deployment evidence timestamp is too far in the future.'
Require ($backendAge -le $MaximumBackendDeploymentAge) 'Live backend deployment evidence is stale; redeploy the exact backend before ingress activation.'
$backendAgeSeconds = [Math]::Max(0, [int][Math]::Floor($backendAge.TotalSeconds))
$backendEvidenceSha256 = (Get-FileHash -LiteralPath $backendFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()

$expectedAddress = $null
Require ([Net.IPAddress]::TryParse([string]$request.host.expectedPublicIpv4, [ref]$expectedAddress)) 'Request expected public IPv4 is malformed.'
Require (Test-PublicIpv4 $expectedAddress) 'Request expected IPv4 is not globally routable.'
$resolved = @([Net.Dns]::GetHostAddresses($apiHost) | Sort-Object -Property IPAddressToString -Unique)
Require ($resolved.Count -eq 1 -and $resolved[0].AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) 'Live API hostname must still resolve to exactly one IPv4 address before ingress activation.'
Require ([string]$resolved[0].ToString() -ceq [string]$expectedAddress.ToString()) 'Live API DNS changed after backend deployment.'

$sshPort = [int]$request.host.sshPort
$sshUser = [string]$request.host.sshUser
$expectedFingerprint = [string]$request.host.sshHostKeySha256
Require ($sshPort -ge 1 -and $sshPort -le 65535) 'SSH port is outside the valid range.'
Require ($sshUser -match '^[a-z_][a-z0-9_-]{0,31}$') 'SSH user is malformed.'
Require ($expectedFingerprint -match '^SHA256:[A-Za-z0-9+/]{43}$') 'Request SSH host-key fingerprint is not canonical.'

$keyFullPath = [IO.Path]::GetFullPath($SshPrivateKeyPath)
Require ([IO.File]::Exists($keyFullPath)) "SSH private key does not exist: $keyFullPath"
$keyItem = Get-Item -LiteralPath $keyFullPath
Require ($null -eq $keyItem.LinkType) 'SSH private key cannot be a symbolic link.'
Require ($keyItem.Length -gt 0 -and $keyItem.Length -le 64KB) 'SSH private key is empty or exceeds 64 KiB.'

$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = [IO.Path]::GetDirectoryName($evidenceFullPath)
Require (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) 'EvidencePath must include a parent directory.'
[IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-ingress-activation-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$knownHostsPath = Join-Path $workRoot 'known_hosts'

try {
    $scanOutput = @(& ssh-keyscan -4 -T 10 -p $sshPort -t ed25519 $apiHost 2>$null)
    Require ($LASTEXITCODE -eq 0 -and $scanOutput.Count -gt 0) 'Could not re-observe the live SSH Ed25519 host key before ingress activation.'
    $hostKeyLines = @($scanOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    Require ($hostKeyLines.Count -eq 1) 'Expected exactly one live SSH Ed25519 host key before ingress activation.'
    Write-Utf8NoBom -Path $knownHostsPath -Content (([string]$hostKeyLines[0]).Trim() + "`n")
    $fingerprintText = Invoke-RequiredNative -Tool 'ssh-keygen' -Arguments @('-E','sha256','-lf',$knownHostsPath) -Context 'SSH host-key fingerprint revalidation' -Capture
    $fingerprintMatch = [Text.RegularExpressions.Regex]::Match($fingerprintText, 'SHA256:[A-Za-z0-9+/]{43}')
    Require $fingerprintMatch.Success 'Re-observed SSH host-key fingerprint is malformed.'
    $observedFingerprint = $fingerprintMatch.Value
    Require ($observedFingerprint -ceq $expectedFingerprint) 'SSH host key changed after backend deployment; ingress activation refused.'

    $sshOptions = @(
        '-4','-i',$keyFullPath,
        '-p',$sshPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-o',"UserKnownHostsFile=$knownHostsPath",
        '-o','StrictHostKeyChecking=yes',
        '-o','BatchMode=yes',
        '-o','IdentitiesOnly=yes',
        '-o','PasswordAuthentication=no',
        '-o','KbdInteractiveAuthentication=no',
        '-o','ConnectTimeout=10'
    )
    $remoteTarget = "$sshUser@$apiHost"

    $remoteScript = @'
set -euo pipefail
release_dir="${1:?release directory required}"
plan_dir="${2:?plan directory required}"
env_path="${3:?environment path required}"
image_id="${4:?image ID required}"
release_version="${5:?release version required}"
release_commit="${6:?release commit required}"
api_base_url="${7:?API base URL required}"
expected_plan_sha="${8:?deployment plan SHA required}"
caddy_path="${9:?Caddy path required}"
expected_caddy_state="${10:?Caddy baseline state required}"

backup=''
old_uid=''
old_gid=''
old_mode=''
mutation_started=false

rollback_caddy() {
  [[ "${mutation_started}" == 'true' ]] || return 0
  [[ -n "${backup}" && -f "${backup}" ]] || return 1
  restore_tmp="$(mktemp /etc/caddy/.Caddyfile.steward.restore.XXXXXX)" || return 1
  if ! install -m "${old_mode}" -o "${old_uid}" -g "${old_gid}" "${backup}" "${restore_tmp}"; then rm -f "${restore_tmp}"; return 1; fi
  if ! mv -f "${restore_tmp}" "${caddy_path}"; then rm -f "${restore_tmp}"; return 1; fi
  if ! caddy validate --config "${caddy_path}" --adapter caddyfile >/dev/null 2>&1; then return 1; fi
  if ! systemctl reload caddy >/dev/null 2>&1; then return 1; fi
  systemctl is-active --quiet caddy
}

fail_after_mutation() {
  message="$1"
  if rollback_caddy; then
    echo "${message}; exact Caddy rollback completed" >&2
  else
    echo "${message}; exact Caddy rollback FAILED" >&2
  fi
  exit 1
}

[[ -d "${release_dir}" ]] || { echo 'staged release directory is missing' >&2; exit 1; }
[[ -d "${plan_dir}" ]] || { echo 'staged deployment-plan directory is missing' >&2; exit 1; }
[[ -f "${env_path}" && ! -L "${env_path}" ]] || { echo 'protected backend environment is missing, not regular, or is a symlink' >&2; exit 1; }
[[ "$(stat -Lc '%u' "${env_path}")" == '0' && "$(stat -Lc '%a' "${env_path}")" == '600' ]] || { echo 'protected backend environment metadata changed before ingress activation' >&2; exit 1; }

if ! pwsh -NoLogo -NoProfile -File "${release_dir}/verify-closed-alpha-release.ps1" -BundleDirectory "${release_dir}" >/dev/null 2>&1; then
  echo 'staged release verification failed before ingress activation' >&2
  exit 1
fi
actual_plan_sha="$(sha256sum "${plan_dir}/deployment-plan.json" | awk '{print toupper($1)}')"
[[ "${actual_plan_sha}" == "${expected_plan_sha}" ]] || { echo 'staged deployment-plan SHA-256 changed before ingress activation' >&2; exit 1; }

export STEWARD_PLAN_PATH="${plan_dir}/deployment-plan.json"
export STEWARD_PLAN_DIR="${plan_dir}"
export STEWARD_EXPECTED_VERSION="${release_version}"
export STEWARD_EXPECTED_COMMIT="${release_commit}"
export STEWARD_EXPECTED_API="${api_base_url}"
export STEWARD_EXPECTED_IMAGE="${image_id}"
export STEWARD_EXPECTED_ENV="${env_path}"
if ! plan_check="$(pwsh -NoLogo -NoProfile -Command '
$ErrorActionPreference = "Stop"
$plan = [IO.File]::ReadAllText($env:STEWARD_PLAN_PATH) | ConvertFrom-Json
if ([string]$plan.documentType -cne "steward.closed-alpha-deployment-plan" -or [int]$plan.schemaVersion -ne 1) { throw "deployment plan identity invalid" }
if ([string]$plan.release.version -cne $env:STEWARD_EXPECTED_VERSION -or [string]$plan.release.commitSha -cne $env:STEWARD_EXPECTED_COMMIT) { throw "deployment plan release mismatch" }
if ([string]$plan.release.apiBaseUrl -cne $env:STEWARD_EXPECTED_API -or [string]$plan.release.backendImageId -cne $env:STEWARD_EXPECTED_IMAGE) { throw "deployment plan deployment mismatch" }
if ([string]$plan.host.environmentFilePath -cne $env:STEWARD_EXPECTED_ENV -or -not [bool]$plan.deployability.deployable -or [bool]$plan.configuration.secretValuesCopied) { throw "deployment plan boundary mismatch" }
$generated = @($plan.generatedFiles)
if ($generated.Count -ne 3) { throw "generated-file count mismatch" }
$expectedNames = @("Caddyfile","deploy-exact-candidate.sh","verify-public-https.sh") | Sort-Object
$actualNames = @($generated | ForEach-Object { [string]$_.path } | Sort-Object)
if (@(Compare-Object $expectedNames $actualNames).Count -ne 0) { throw "generated-file set mismatch" }
foreach ($entry in $generated) {
  $name = [string]$entry.path
  if ($name -notmatch "^[A-Za-z0-9._-]+$") { throw "unsafe generated-file path" }
  $path = Join-Path $env:STEWARD_PLAN_DIR $name
  if (-not [IO.File]::Exists($path)) { throw "generated file missing" }
  $item = Get-Item -LiteralPath $path
  if ($null -ne $item.LinkType -or $item.Length -ne [int64]$entry.byteSize) { throw "generated file metadata mismatch" }
  if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne [string]$entry.sha256) { throw "generated file hash mismatch" }
}
$caddy = @($generated | Where-Object { [string]$_.path -ceq "Caddyfile" })
if ($caddy.Count -ne 1) { throw "Caddy binding missing" }
Write-Output ([string]$caddy[0].sha256)
' 2>/dev/null)"; then
  echo 'staged deployment-plan validation failed before ingress activation' >&2
  exit 1
fi
[[ "${plan_check}" =~ ^[0-9A-F]{64}$ ]] || { echo 'staged Caddy SHA-256 binding is malformed' >&2; exit 1; }
expected_generated_caddy_sha="${plan_check}"
actual_generated_caddy_sha="$(sha256sum "${plan_dir}/Caddyfile" | awk '{print toupper($1)}')"
[[ "${actual_generated_caddy_sha}" == "${expected_generated_caddy_sha}" ]] || { echo 'staged Caddyfile does not match deployment-plan binding' >&2; exit 1; }

loaded_user="$(docker image inspect "${image_id}" --format '{{.Config.User}}' 2>/dev/null || true)"
[[ "${loaded_user}" == 'app' ]] || { echo 'exact backend image/runtime is not present before ingress activation' >&2; exit 1; }
running_id="$(docker container inspect steward-backend --format '{{.Image}}' 2>/dev/null || true)"
running_state="$(docker container inspect steward-backend --format '{{.State.Running}}' 2>/dev/null || true)"
[[ "${running_id}" == "${image_id}" && "${running_state}" == 'true' ]] || { echo 'exact backend container is not running before ingress activation' >&2; exit 1; }
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health/live >/dev/null || { echo 'backend liveness failed before ingress activation' >&2; exit 1; }
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health/ready >/dev/null || { echo 'backend readiness failed before ingress activation' >&2; exit 1; }
ss -ltnH 'sport = :8080' | grep -q . || { echo 'backend port 8080 is not listening before ingress activation' >&2; exit 1; }

command -v caddy >/dev/null 2>&1 || { echo 'Caddy is not installed on the live host' >&2; exit 1; }
command -v systemctl >/dev/null 2>&1 || { echo 'systemctl is not available on the live host' >&2; exit 1; }
systemctl is-active --quiet caddy || { echo 'managed caddy.service is not active before ingress activation' >&2; exit 1; }
[[ -f "${caddy_path}" && ! -L "${caddy_path}" ]] || { echo 'live Caddy baseline is missing, not regular, or is a symlink' >&2; exit 1; }
old_uid="$(stat -Lc '%u' "${caddy_path}")"
old_gid="$(stat -Lc '%g' "${caddy_path}")"
old_mode="$(stat -Lc '%a' "${caddy_path}")"
[[ "${old_uid}" == '0' && "${old_gid}" == '0' && "${old_mode}" == '644' ]] || { echo 'live Caddy baseline must be root:root mode 0644' >&2; exit 1; }
actual_caddy_state="sha256:$(sha256sum "${caddy_path}" | awk '{print toupper($1)}')"
[[ "${actual_caddy_state}" == "${expected_caddy_state}" ]] || { echo 'Caddy state changed after backend deployment' >&2; exit 1; }
caddy validate --config "${caddy_path}" --adapter caddyfile >/dev/null 2>&1 || { echo 'existing Caddy baseline is not valid' >&2; exit 1; }
caddy validate --config "${plan_dir}/Caddyfile" --adapter caddyfile >/dev/null 2>&1 || { echo 'staged generated Caddyfile is not valid' >&2; exit 1; }

backup="$(mktemp /etc/caddy/.Caddyfile.steward.backup.XXXXXX)"
cp --preserve=mode,ownership "${caddy_path}" "${backup}"
new_file="$(mktemp /etc/caddy/.Caddyfile.steward.new.XXXXXX)"
install -m 0644 -o root -g root "${plan_dir}/Caddyfile" "${new_file}"
mutation_started=true
mv -f "${new_file}" "${caddy_path}"

caddy validate --config "${caddy_path}" --adapter caddyfile >/dev/null 2>&1 || fail_after_mutation 'installed generated Caddyfile failed validation'
systemctl reload caddy >/dev/null 2>&1 || fail_after_mutation 'managed caddy.service reload failed'
systemctl is-active --quiet caddy || fail_after_mutation 'managed caddy.service is not active after reload'
installed_sha="$(sha256sum "${caddy_path}" | awk '{print toupper($1)}')"
[[ "${installed_sha}" == "${expected_generated_caddy_sha}" ]] || fail_after_mutation 'installed Caddyfile hash does not match staged plan'

running_id_after="$(docker container inspect steward-backend --format '{{.Image}}' 2>/dev/null || true)"
running_state_after="$(docker container inspect steward-backend --format '{{.State.Running}}' 2>/dev/null || true)"
[[ "${running_id_after}" == "${image_id}" && "${running_state_after}" == 'true' ]] || fail_after_mutation 'backend container changed during ingress activation'
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health/live >/dev/null || fail_after_mutation 'backend liveness failed after ingress activation'
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health/ready >/dev/null || fail_after_mutation 'backend readiness failed after ingress activation'
[[ "$(stat -Lc '%u' "${env_path}")" == '0' && "$(stat -Lc '%a' "${env_path}")" == '600' ]] || fail_after_mutation 'ingress activation changed protected environment metadata'

rm -f "${backup}"
backup=''
mutation_started=false
activated_state="sha256:${installed_sha}"
printf 'remoteBundleReverified=true\n'
printf 'deploymentPlanReverified=true\n'
printf 'backendContainerRunning=true\n'
printf 'backendLocalLive=true\n'
printf 'backendLocalReady=true\n'
printf 'backendPort8080Listening=true\n'
printf 'backendRuntimeUser=app\n'
printf 'caddyBaselineState=%s\n' "${actual_caddy_state}"
printf 'caddyActivatedState=%s\n' "${activated_state}"
printf 'caddyGeneratedSha256=%s\n' "${installed_sha}"
printf 'caddyConfigInstalled=true\n'
printf 'caddyValidated=true\n'
printf 'caddyReloaded=true\n'
printf 'caddyServiceActive=true\n'
printf 'protectedEnvironmentMetadataPreserved=true\n'
'@

    $remoteArgs = @(
        [string]$request.host.remoteReleaseDirectory,
        [string]$request.host.remoteDeploymentPlanDirectory,
        [string]$request.host.remoteEnvironmentPath,
        $imageId,$version,$commitSha,$apiBaseUrl,
        [string]$backend.deploymentPlanSha256,
        [string]$request.host.caddyConfigurationPath,
        [string]$backend.caddyStateAfter
    ) | ForEach-Object { ConvertTo-ShellSingleQuoted ([string]$_) }
    $remoteCommand = 'sudo -n bash -s -- ' + ($remoteArgs -join ' ')
    $remoteOutput = @($remoteScript | & ssh @sshOptions $remoteTarget $remoteCommand 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Remote live ingress activation failed. $(($remoteOutput -join [Environment]::NewLine).Trim())"
    }

    $allowedKeys = @(
        'backendContainerRunning','backendLocalLive','backendLocalReady','backendPort8080Listening','backendRuntimeUser',
        'caddyActivatedState','caddyBaselineState','caddyConfigInstalled','caddyGeneratedSha256','caddyReloaded','caddyServiceActive',
        'caddyValidated','deploymentPlanReverified','protectedEnvironmentMetadataPreserved','remoteBundleReverified'
    )
    $values = @{}
    foreach ($lineObject in $remoteOutput) {
        $line = ([string]$lineObject).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $separator = $line.IndexOf('=')
        Require ($separator -gt 0) 'Remote ingress activation emitted an unexpected line.'
        $name = $line.Substring(0,$separator)
        $value = $line.Substring($separator + 1)
        Require ($name -in $allowedKeys) "Remote ingress activation emitted an unexpected evidence key '$name'."
        Require (-not $values.ContainsKey($name)) "Remote ingress activation emitted duplicate evidence key '$name'."
        $values[$name] = $value
    }
    Require ($values.Count -eq $allowedKeys.Count -and @(Compare-Object @($values.Keys | Sort-Object) @($allowedKeys | Sort-Object)).Count -eq 0) 'Remote ingress activation evidence contains missing or unexpected keys.'
    foreach ($trueKey in @('remoteBundleReverified','deploymentPlanReverified','backendContainerRunning','backendLocalLive','backendLocalReady','backendPort8080Listening','caddyConfigInstalled','caddyValidated','caddyReloaded','caddyServiceActive','protectedEnvironmentMetadataPreserved')) {
        Require ([string]$values[$trueKey] -ceq 'true') "Remote ingress activation evidence '$trueKey' is not true."
    }
    Require ([string]$values.backendRuntimeUser -ceq 'app') 'Remote ingress activation lost backend runtime-user identity.'
    Require ([string]$values.caddyBaselineState -ceq [string]$backend.caddyStateAfter) 'Remote ingress activation baseline does not match backend evidence.'
    Require ([string]$values.caddyGeneratedSha256 -match '^[0-9A-F]{64}$') 'Activated Caddy SHA-256 is malformed.'
    Require ([string]$values.caddyActivatedState -ceq "sha256:$($values.caddyGeneratedSha256)") 'Activated Caddy state does not match generated Caddy SHA-256.'

    $evidence = [ordered]@{
        documentType = 'steward.closed-alpha-live-ingress-activation'
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        backendDeploymentCompletedAtUtc = $backendCompletedAt.ToUniversalTime().ToString('O')
        backendDeploymentAgeSecondsAtStart = $backendAgeSeconds
        requestSha256 = $requestSha256
        backendDeploymentEvidenceSha256 = $backendEvidenceSha256
        releaseVersion = $version
        releaseCommitSha = $commitSha
        apiBaseUrl = $apiBaseUrl
        apiHost = $apiHost
        resolvedPublicIpv4 = [string]$resolved[0].ToString()
        sshUser = $sshUser
        sshPort = $sshPort
        sshHostKeySha256 = $observedFingerprint
        backendDeploymentFresh = $true
        dnsReverified = $true
        sshHostKeyReverified = $true
        remoteReleaseDirectory = [string]$request.host.remoteReleaseDirectory
        remoteDeploymentPlanDirectory = [string]$request.host.remoteDeploymentPlanDirectory
        remoteEnvironmentPath = [string]$request.host.remoteEnvironmentPath
        remoteBundleReverified = $true
        deploymentPlanReverified = $true
        deploymentPlanSha256 = [string]$backend.deploymentPlanSha256
        backendImageId = $imageId
        backendRuntimeUser = 'app'
        backendContainerRunning = $true
        backendLocalLive = $true
        backendLocalReady = $true
        backendPort8080Listening = $true
        protectedEnvironmentMetadataPreserved = $true
        protectedEnvironmentTransferred = $false
        caddyBaselineState = [string]$values.caddyBaselineState
        caddyActivatedState = [string]$values.caddyActivatedState
        caddyGeneratedSha256 = [string]$values.caddyGeneratedSha256
        caddyConfigInstalled = $true
        caddyValidated = $true
        caddyReloaded = $true
        caddyServiceActive = $true
        publicHttpsVerified = $false
        externalPort8080ClosedVerified = $false
        publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
        physicalBringHere = [string]$manifest.publishAuthorization.physicalBringHere
        publicationAuthorizationChanged = $false
    }
    $evidenceText = $evidence | ConvertTo-Json -Depth 6
    Require ($evidenceText.Length -gt 0 -and $evidenceText.Length -le 64KB) 'Live ingress activation evidence is empty or exceeds 64 KiB.'
    [IO.File]::WriteAllText($evidenceFullPath,$evidenceText,[Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Exact staged Caddy configuration is active with the exact backend preserved.'
    Write-Host "  Release: $version ($commitSha)"
    Write-Host "  Caddy configuration SHA-256: $($values.caddyGeneratedSha256)"
    Write-Host '  Managed caddy.service: active'
    Write-Host '  Backend local liveness/readiness: preserved'
    Write-Host '  Public HTTPS verified: no'
    Write-Host '  External port-8080 closure verified: no'
    Write-Host '  Publication authorization changed: no'
}
finally {
    if ([IO.Directory]::Exists($workRoot)) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
