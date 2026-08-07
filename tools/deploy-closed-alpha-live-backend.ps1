[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [Parameter(Mandatory = $true)]
    [string]$StagingEvidencePath,
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

$MaximumStagingAge = [TimeSpan]::FromMinutes(15)
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

function Require-BindingMatchesArtifact([object]$Binding, [object[]]$Artifacts, [string]$ExpectedPath, [string]$Context) {
    Require-ExactProperties $Binding @('byteSize', 'path', 'sha256') $Context
    Require ([string]$Binding.path -ceq $ExpectedPath) "$Context has the wrong path."
    $artifact = Get-ArtifactByPath $Artifacts $ExpectedPath
    Require ([int64]$Binding.byteSize -eq [int64]$artifact.byteSize) "$Context byte size does not match the release manifest."
    Require ([string]$Binding.sha256 -ceq [string]$artifact.sha256) "$Context SHA-256 does not match the release manifest."
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

foreach ($tool in @('ssh', 'ssh-keygen', 'ssh-keyscan')) { Require-Tool $tool }

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'
& $verifierPath -BundleDirectory $bundleRoot

$selfPath = [IO.Path]::GetFullPath($PSCommandPath)
$bundledSelfPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'deploy-closed-alpha-live-backend.ps1'))
Require ([string]::Equals($selfPath, $bundledSelfPath, [StringComparison]::OrdinalIgnoreCase)) 'Run live backend deployment from inside the verified release bundle.'

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$artifacts = @($manifest.artifacts)
$selfArtifact = Get-ArtifactByPath $artifacts 'deploy-closed-alpha-live-backend.ps1'
$selfItem = Get-Item -LiteralPath $selfPath
Require ($null -eq $selfItem.LinkType) 'Live backend executor cannot be a symbolic link.'
Require ($selfItem.Length -eq [int64]$selfArtifact.byteSize) 'Live backend executor byte size does not match the release manifest.'
Require ((Get-FileHash -LiteralPath $selfPath -Algorithm SHA256).Hash -ceq [string]$selfArtifact.sha256) 'Live backend executor SHA-256 does not match the release manifest.'

$version = [string]$manifest.version
$commitSha = [string]$manifest.commitSha
$apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
$imageId = [string]$manifest.deployment.backendImageId
$imageTag = [string]$manifest.deployment.backendImageTag
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($imageId -match '^sha256:[0-9a-f]{64}$') 'Release backend image ID is malformed.'
Require (-not [string]::IsNullOrWhiteSpace($imageTag)) 'Release backend image tag is missing.'
Require ([string]$manifest.deployment.targetPlatform -ceq 'linux/amd64') 'Live backend deployment requires linux/amd64.'
Require ([string]$manifest.deployment.backendRuntimeUser -ceq 'app') 'Live backend deployment requires runtime user app.'
Require (-not [bool]$manifest.publishAuthorization.publishAllowed) 'Live backend deployment cannot use a publish-authorized candidate.'
Require ([string]$manifest.publishAuthorization.physicalBringHere -ceq 'deferred') 'Live backend deployment expects physical Bring Here to remain deferred.'

$requestFile = Read-BoundedJson $RequestPath 1MB 'Live deployment request'
$request = $requestFile.Json
Require-ExactProperties $request @('createdAtUtc','documentType','executionContract','host','network','publication','release','schemaVersion','secretBoundary') 'Live deployment request'
Require ([string]$request.documentType -ceq 'steward.closed-alpha-live-deployment-request' -and [int]$request.schemaVersion -eq 1) 'Live deployment request identity is invalid.'
Require-ExactProperties $request.release @('apiBaseUrl','backendImageId','backendImageTag','backendTar','commitSha','version') 'Live deployment request release'
Require-ExactProperties $request.host @('apiHost','caddyConfigurationPath','expectedPublicIpv4','remoteDeploymentPlanDirectory','remoteEnvironmentPath','remoteReleaseDirectory','requiredTools','sshHost','sshHostKeySha256','sshPort','sshUser','targetPlatform','topology') 'Live deployment request host'
Require-ExactProperties $request.executionContract @('certificateBypassAllowed','deployScriptName','deploymentPlanner','environmentNeverLeavesLiveHost','environmentTemplate','exactImageIdRequired','liveHostPreflight','liveRequestPlanner','plannerRequireDeployable','plannerRunsOnLiveHost','publicTlsVerificationRequired','publicVerifierName','releaseVerifier') 'Live deployment request execution contract'
Require-ExactProperties $request.secretBoundary @('remoteEnvironmentPath','requiredRemoteMode','requiredRemoteOwner','secretEnvironmentBundled','secretEnvironmentTransferredByRequest','secretValuesPresent') 'Live deployment request secret boundary'
Require-ExactProperties $request.publication @('authorizationChanged','physicalBringHere','publishAllowed','requestAuthorizesPublication') 'Live deployment request publication'
Require ([string]$request.release.version -ceq $version -and [string]$request.release.commitSha -ceq $commitSha) 'Request release identity does not match the candidate.'
Require ([string]$request.release.apiBaseUrl -ceq $apiBaseUrl -and [string]$request.release.backendImageId -ceq $imageId -and [string]$request.release.backendImageTag -ceq $imageTag) 'Request deployment identity does not match the candidate.'
Require-BindingMatchesArtifact $request.executionContract.releaseVerifier $artifacts 'verify-closed-alpha-release.ps1' 'Request release verifier binding'
Require-BindingMatchesArtifact $request.executionContract.deploymentPlanner $artifacts 'prepare-closed-alpha-deployment.ps1' 'Request deployment planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveRequestPlanner $artifacts 'prepare-closed-alpha-live-deployment.ps1' 'Request live request planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveHostPreflight $artifacts 'preflight-closed-alpha-live-host.ps1' 'Request live-host preflight binding'
Require-BindingMatchesArtifact $request.executionContract.environmentTemplate $artifacts 'backend/deployment.env.example' 'Request environment template binding'
Require (-not [bool]$request.secretBoundary.secretValuesPresent -and -not [bool]$request.secretBoundary.secretEnvironmentBundled -and -not [bool]$request.secretBoundary.secretEnvironmentTransferredByRequest) 'Request crossed the protected environment boundary.'
Require ([string]$request.secretBoundary.remoteEnvironmentPath -ceq '/etc/steward/backend.env' -and [string]$request.secretBoundary.requiredRemoteOwner -ceq 'root' -and [string]$request.secretBoundary.requiredRemoteMode -ceq '0600') 'Request protected environment contract changed.'
Require (-not [bool]$request.publication.publishAllowed -and [string]$request.publication.physicalBringHere -ceq 'deferred' -and -not [bool]$request.publication.authorizationChanged -and -not [bool]$request.publication.requestAuthorizesPublication) 'Request publication state is incompatible with staged closed alpha.'

$apiUri = $null
Require ([Uri]::TryCreate($apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) 'Candidate API base URL is malformed.'
$apiHost = $apiUri.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
Require ([string]$request.host.topology -ceq 'single-linux-amd64-host') 'Unsupported live-host topology.'
Require ([string]$request.host.apiHost -ceq $apiHost -and [string]$request.host.sshHost -ceq $apiHost) 'Request API/SSH host does not match the candidate API host.'
Require ([string]$request.host.remoteReleaseDirectory -ceq "/srv/steward/releases/$commitSha") 'Request release directory changed.'
Require ([string]$request.host.remoteDeploymentPlanDirectory -ceq "/srv/steward/deployment-plans/$commitSha") 'Request deployment-plan directory changed.'
Require ([string]$request.host.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Request environment path changed.'
Require ([string]$request.host.caddyConfigurationPath -ceq '/etc/caddy/Caddyfile') 'Request Caddy path changed.'
Require ([string]$request.network.caddyUpstream -ceq '127.0.0.1:8080' -and [string]$request.network.backendKnownProxyIp -ceq '127.0.0.1') 'Request one-proxy contract changed.'
Require ([int]$request.network.backendPort -eq 8080 -and [int]$request.network.forbiddenPublicBackendPort -eq 8080 -and [bool]$request.network.publicBackendPortMustFailClosed) 'Request backend-port contract changed.'

$requestSha256 = (Get-FileHash -LiteralPath $requestFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()
$stagingFile = Read-BoundedJson $StagingEvidencePath 1MB 'Live plan staging evidence'
$staging = $stagingFile.Json
Require-ExactProperties $staging @(
    'apiBaseUrl','apiHost','backendContainerStarted','backendImageLoaded','backendImagePreexisting','backendPort8080Clear',
    'caddyModified','caddyStateAfter','caddyStateBefore','candidateArchiveSha256','candidateTransferred','completedAtUtc',
    'deploymentPlanGenerated','deploymentPlanSecretValuesCopied','deploymentPlanSha256','deploymentStarted','dnsReverified',
    'documentType','physicalBringHere','preflightAgeSecondsAtStart','preflightEvidenceSha256','preflightFresh',
    'preflightObservedAtUtc','protectedEnvironmentReadOnHost','protectedEnvironmentTransferred','publicationAuthorizationChanged',
    'publishAllowed','releaseCommitSha','releaseVersion','remoteDeploymentPlanDirectory','remoteEnvironmentPath',
    'remoteReleaseDirectory','requestSha256','resolvedPublicIpv4','schemaVersion','sshHostKeyReverified','sshHostKeySha256',
    'sshPort','sshUser'
) 'Live plan staging evidence'
Require ([string]$staging.documentType -ceq 'steward.closed-alpha-live-plan-staging' -and [int]$staging.schemaVersion -eq 1) 'Live plan staging evidence identity is invalid.'
Require ([string]$staging.requestSha256 -ceq $requestSha256) 'Staging evidence does not bind the exact live deployment request.'
Require ([string]$staging.releaseVersion -ceq $version -and [string]$staging.releaseCommitSha -ceq $commitSha) 'Staging evidence does not bind the exact release.'
Require ([string]$staging.apiBaseUrl -ceq $apiBaseUrl -and [string]$staging.apiHost -ceq $apiHost) 'Staging evidence API identity changed.'
Require ([string]$staging.resolvedPublicIpv4 -ceq [string]$request.host.expectedPublicIpv4) 'Staging evidence DNS address does not match the request.'
Require ([string]$staging.sshUser -ceq [string]$request.host.sshUser -and [int]$staging.sshPort -eq [int]$request.host.sshPort -and [string]$staging.sshHostKeySha256 -ceq [string]$request.host.sshHostKeySha256) 'Staging evidence SSH identity does not match the request.'
Require ([string]$staging.remoteReleaseDirectory -ceq [string]$request.host.remoteReleaseDirectory -and [string]$staging.remoteDeploymentPlanDirectory -ceq [string]$request.host.remoteDeploymentPlanDirectory -and [string]$staging.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Staging evidence remote paths changed.'
Require ([bool]$staging.preflightFresh -and [bool]$staging.dnsReverified -and [bool]$staging.sshHostKeyReverified) 'Staging evidence did not prove fresh host identity.'
Require ([bool]$staging.candidateTransferred -and [bool]$staging.remoteBundleVerified -and [bool]$staging.deploymentPlanGenerated -and [string]$staging.deploymentPlanSha256 -match '^[0-9A-F]{64}$') 'Staging evidence did not prove exact candidate/plan materialization.'
Require ([bool]$staging.protectedEnvironmentReadOnHost -and -not [bool]$staging.protectedEnvironmentTransferred -and -not [bool]$staging.deploymentPlanSecretValuesCopied) 'Staging evidence crossed the protected-value boundary.'
Require (-not [bool]$staging.backendImagePreexisting -and -not [bool]$staging.backendImageLoaded -and -not [bool]$staging.backendContainerStarted -and -not [bool]$staging.deploymentStarted -and [bool]$staging.backendPort8080Clear) 'Staging evidence already crossed the backend-deployment boundary.'
Require (-not [bool]$staging.caddyModified -and [string]$staging.caddyStateAfter -ceq [string]$staging.caddyStateBefore) 'Staging evidence changed Caddy state.'
Require (-not [bool]$staging.publishAllowed -and [string]$staging.physicalBringHere -ceq 'deferred' -and -not [bool]$staging.publicationAuthorizationChanged) 'Staging evidence changed publication state.'

$stagingCompletedAt = [DateTimeOffset]::MinValue
Require ([DateTimeOffset]::TryParse([string]$staging.completedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$stagingCompletedAt)) 'Staging evidence completedAtUtc is malformed.'
$now = [DateTimeOffset]::UtcNow
$stagingAge = $now - $stagingCompletedAt.ToUniversalTime()
Require ($stagingAge -ge $MaximumClockSkew.Negate()) 'Staging evidence timestamp is too far in the future.'
Require ($stagingAge -le $MaximumStagingAge) 'Live plan staging evidence is stale; restage the exact plan before backend deployment.'
$stagingAgeSeconds = [Math]::Max(0, [int][Math]::Floor($stagingAge.TotalSeconds))
$stagingEvidenceSha256 = (Get-FileHash -LiteralPath $stagingFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()

$expectedAddress = $null
Require ([Net.IPAddress]::TryParse([string]$request.host.expectedPublicIpv4, [ref]$expectedAddress)) 'Request expected public IPv4 is malformed.'
Require (Test-PublicIpv4 $expectedAddress) 'Request expected IPv4 is not globally routable.'
$resolved = @([Net.Dns]::GetHostAddresses($apiHost) | Sort-Object -Property IPAddressToString -Unique)
Require ($resolved.Count -eq 1 -and $resolved[0].AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) 'Live API hostname must still resolve to exactly one IPv4 address before backend deployment.'
Require ([string]$resolved[0].ToString() -ceq [string]$expectedAddress.ToString()) 'Live API DNS changed after plan staging.'

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

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-backend-deploy-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$knownHostsPath = Join-Path $workRoot 'known_hosts'

try {
    $scanOutput = @(& ssh-keyscan -4 -T 10 -p $sshPort -t ed25519 $apiHost 2>$null)
    Require ($LASTEXITCODE -eq 0 -and $scanOutput.Count -gt 0) 'Could not re-observe the live SSH Ed25519 host key before backend deployment.'
    $hostKeyLines = @($scanOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    Require ($hostKeyLines.Count -eq 1) 'Expected exactly one live SSH Ed25519 host key before backend deployment.'
    Write-Utf8NoBom -Path $knownHostsPath -Content (([string]$hostKeyLines[0]).Trim() + "`n")
    $fingerprintText = Invoke-RequiredNative -Tool 'ssh-keygen' -Arguments @('-E','sha256','-lf',$knownHostsPath) -Context 'SSH host-key fingerprint revalidation' -Capture
    $fingerprintMatch = [Text.RegularExpressions.Regex]::Match($fingerprintText, 'SHA256:[A-Za-z0-9+/]{43}')
    Require $fingerprintMatch.Success 'Re-observed SSH host-key fingerprint is malformed.'
    $observedFingerprint = $fingerprintMatch.Value
    Require ($observedFingerprint -ceq $expectedFingerprint) 'SSH host key changed after plan staging; backend deployment refused.'

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
image_tag="${5:?image tag required}"
release_version="${6:?release version required}"
release_commit="${7:?release commit required}"
api_base_url="${8:?API base URL required}"
expected_plan_sha="${9:?deployment plan SHA required}"
caddy_path="${10:?Caddy path required}"
expected_caddy_state="${11:?Caddy state required}"

cleanup_failed_deployment() {
  docker rm --force steward-backend >/dev/null 2>&1 || true
  docker image rm --force "${image_tag}" >/dev/null 2>&1 || true
  docker image rm --force "${image_id}" >/dev/null 2>&1 || true
}
fail_after_mutation() {
  cleanup_failed_deployment
  echo "$1" >&2
  exit 1
}

[[ -d "${release_dir}" ]] || { echo 'staged release directory is missing' >&2; exit 1; }
[[ -d "${plan_dir}" ]] || { echo 'staged deployment-plan directory is missing' >&2; exit 1; }
[[ -f "${env_path}" && ! -L "${env_path}" ]] || { echo 'protected backend environment is missing, not regular, or is a symlink' >&2; exit 1; }
[[ "$(stat -Lc '%u' "${env_path}")" == '0' ]] || { echo 'protected backend environment is not root-owned' >&2; exit 1; }
[[ "$(stat -Lc '%a' "${env_path}")" == '600' ]] || { echo 'protected backend environment mode is not 0600' >&2; exit 1; }
env_size="$(stat -Lc '%s' "${env_path}")"
[[ "${env_size}" =~ ^[0-9]+$ && "${env_size}" -gt 0 && "${env_size}" -le 65536 ]] || { echo 'protected backend environment size is outside the supported bound' >&2; exit 1; }

if ! pwsh -NoLogo -NoProfile -File "${release_dir}/verify-closed-alpha-release.ps1" -BundleDirectory "${release_dir}" >/dev/null 2>&1; then
  echo 'staged release verification failed before backend deployment' >&2
  exit 1
fi
actual_plan_sha="$(sha256sum "${plan_dir}/deployment-plan.json" | awk '{print toupper($1)}')"
[[ "${actual_plan_sha}" == "${expected_plan_sha}" ]] || { echo 'staged deployment-plan SHA-256 changed before backend deployment' >&2; exit 1; }

export STEWARD_PLAN_PATH="${plan_dir}/deployment-plan.json"
export STEWARD_PLAN_DIR="${plan_dir}"
export STEWARD_EXPECTED_VERSION="${release_version}"
export STEWARD_EXPECTED_COMMIT="${release_commit}"
export STEWARD_EXPECTED_API="${api_base_url}"
export STEWARD_EXPECTED_IMAGE="${image_id}"
export STEWARD_EXPECTED_ENV="${env_path}"
if ! pwsh -NoLogo -NoProfile -Command '
$ErrorActionPreference = "Stop"
$plan = [IO.File]::ReadAllText($env:STEWARD_PLAN_PATH) | ConvertFrom-Json
if ([string]$plan.documentType -cne "steward.closed-alpha-deployment-plan" -or [int]$plan.schemaVersion -ne 1) { throw "deployment plan identity is invalid" }
if ([string]$plan.release.version -cne $env:STEWARD_EXPECTED_VERSION -or [string]$plan.release.commitSha -cne $env:STEWARD_EXPECTED_COMMIT) { throw "deployment plan release identity mismatch" }
if ([string]$plan.release.apiBaseUrl -cne $env:STEWARD_EXPECTED_API -or [string]$plan.release.backendImageId -cne $env:STEWARD_EXPECTED_IMAGE) { throw "deployment plan deployment identity mismatch" }
if ([string]$plan.host.environmentFilePath -cne $env:STEWARD_EXPECTED_ENV -or -not [bool]$plan.deployability.deployable -or [bool]$plan.configuration.secretValuesCopied) { throw "deployment plan host/deployability boundary mismatch" }
$generated = @($plan.generatedFiles)
if ($generated.Count -ne 3) { throw "deployment plan generated-file count mismatch" }
$expectedNames = @("Caddyfile","deploy-exact-candidate.sh","verify-public-https.sh") | Sort-Object
$actualNames = @($generated | ForEach-Object { [string]$_.path } | Sort-Object)
if (@(Compare-Object $expectedNames $actualNames).Count -ne 0) { throw "deployment plan generated-file set mismatch" }
foreach ($entry in $generated) {
  $name = [string]$entry.path
  if ($name -notmatch "^[A-Za-z0-9._-]+$") { throw "unsafe generated-file path" }
  $path = Join-Path $env:STEWARD_PLAN_DIR $name
  if (-not [IO.File]::Exists($path)) { throw "generated file missing" }
  $item = Get-Item -LiteralPath $path
  if ($null -ne $item.LinkType -or $item.Length -ne [int64]$entry.byteSize) { throw "generated file metadata mismatch" }
  if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne [string]$entry.sha256) { throw "generated file hash mismatch" }
}
' >/dev/null 2>&1; then
  echo 'staged deployment-plan validation failed before backend deployment' >&2
  exit 1
fi

if docker image inspect "${image_id}" >/dev/null 2>&1; then echo 'exact backend image unexpectedly preexists before backend deployment' >&2; exit 1; fi
if docker container inspect steward-backend >/dev/null 2>&1; then echo 'steward-backend unexpectedly exists before backend deployment' >&2; exit 1; fi
if ss -ltnH 'sport = :8080' | grep -q .; then echo 'backend port 8080 is occupied before backend deployment' >&2; exit 1; fi
if [[ -e "${caddy_path}" ]]; then caddy_state_before="sha256:$(sha256sum "${caddy_path}" | awk '{print toupper($1)}')"; else caddy_state_before='absent'; fi
[[ "${caddy_state_before}" == "${expected_caddy_state}" ]] || { echo 'Caddy state changed after live plan staging' >&2; exit 1; }

if ! bash "${plan_dir}/deploy-exact-candidate.sh" "${release_dir}" >/dev/null 2>&1; then
  fail_after_mutation 'generated exact-image backend deployment failed'
fi

loaded_id="$(docker image inspect "${image_tag}" --format '{{.Id}}')"
[[ "${loaded_id}" == "${image_id}" ]] || fail_after_mutation 'loaded backend image ID mismatch after deployment'
loaded_user="$(docker image inspect "${image_id}" --format '{{.Config.User}}')"
[[ "${loaded_user}" == 'app' ]] || fail_after_mutation 'loaded backend image runtime user mismatch after deployment'
running_id="$(docker container inspect steward-backend --format '{{.Image}}')"
[[ "${running_id}" == "${image_id}" ]] || fail_after_mutation 'running backend container image ID mismatch'
running_state="$(docker container inspect steward-backend --format '{{.State.Running}}')"
[[ "${running_state}" == 'true' ]] || fail_after_mutation 'backend container is not running after deployment'

curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health/live >/dev/null || fail_after_mutation 'backend liveness failed after deployment'
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/health/ready >/dev/null || fail_after_mutation 'backend readiness failed after deployment'
ss -ltnH 'sport = :8080' | grep -q . || fail_after_mutation 'backend port 8080 is not listening after deployment'

actual_plan_sha_after="$(sha256sum "${plan_dir}/deployment-plan.json" | awk '{print toupper($1)}')"
[[ "${actual_plan_sha_after}" == "${expected_plan_sha}" ]] || fail_after_mutation 'deployment-plan bytes changed during backend deployment'
if ! pwsh -NoLogo -NoProfile -File "${release_dir}/verify-closed-alpha-release.ps1" -BundleDirectory "${release_dir}" >/dev/null 2>&1; then
  fail_after_mutation 'staged release bytes changed during backend deployment'
fi
if [[ -e "${caddy_path}" ]]; then caddy_state_after="sha256:$(sha256sum "${caddy_path}" | awk '{print toupper($1)}')"; else caddy_state_after='absent'; fi
[[ "${caddy_state_after}" == "${caddy_state_before}" ]] || fail_after_mutation 'backend deployment modified the Caddy configuration'
[[ "$(stat -Lc '%u' "${env_path}")" == '0' && "$(stat -Lc '%a' "${env_path}")" == '600' ]] || fail_after_mutation 'backend deployment changed protected environment metadata'

printf 'remoteBundleReverified=true\n'
printf 'deploymentPlanReverified=true\n'
printf 'backendImageLoaded=true\n'
printf 'backendImageId=%s\n' "${loaded_id}"
printf 'backendRuntimeUser=%s\n' "${loaded_user}"
printf 'backendContainerStarted=true\n'
printf 'backendContainerRunning=true\n'
printf 'backendLocalLive=true\n'
printf 'backendLocalReady=true\n'
printf 'backendPort8080Listening=true\n'
printf 'caddyStateBefore=%s\n' "${caddy_state_before}"
printf 'caddyStateAfter=%s\n' "${caddy_state_after}"
printf 'caddyModified=false\n'
printf 'protectedEnvironmentMetadataPreserved=true\n'
'@
    $remoteArgs = @(
        [string]$request.host.remoteReleaseDirectory,
        [string]$request.host.remoteDeploymentPlanDirectory,
        [string]$request.host.remoteEnvironmentPath,
        $imageId,$imageTag,$version,$commitSha,$apiBaseUrl,
        [string]$staging.deploymentPlanSha256,
        [string]$request.host.caddyConfigurationPath,
        [string]$staging.caddyStateAfter
    ) | ForEach-Object { ConvertTo-ShellSingleQuoted ([string]$_) }
    $remoteCommand = 'sudo -n bash -s -- ' + ($remoteArgs -join ' ')
    $remoteOutput = @($remoteScript | & ssh @sshOptions $remoteTarget $remoteCommand 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Remote live backend deployment failed. $(($remoteOutput -join [Environment]::NewLine).Trim())"
    }

    $values = @{}
    $allowedKeys = @(
        'backendContainerRunning','backendContainerStarted','backendImageId','backendImageLoaded','backendLocalLive',
        'backendLocalReady','backendPort8080Listening','backendRuntimeUser','caddyModified','caddyStateAfter','caddyStateBefore',
        'deploymentPlanReverified','protectedEnvironmentMetadataPreserved','remoteBundleReverified'
    )
    foreach ($lineObject in $remoteOutput) {
        $line = ([string]$lineObject).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $separator = $line.IndexOf('=')
        Require ($separator -gt 0) 'Remote backend deployment emitted an unexpected line.'
        $name = $line.Substring(0,$separator)
        $value = $line.Substring($separator + 1)
        Require ($name -in $allowedKeys) "Remote backend deployment emitted an unexpected evidence key '$name'."
        Require (-not $values.ContainsKey($name)) "Remote backend deployment emitted duplicate evidence key '$name'."
        $values[$name] = $value
    }
    Require ($values.Count -eq $allowedKeys.Count -and @(Compare-Object @($values.Keys | Sort-Object) @($allowedKeys | Sort-Object)).Count -eq 0) 'Remote backend deployment evidence contains missing or unexpected keys.'
    foreach ($trueKey in @('remoteBundleReverified','deploymentPlanReverified','backendImageLoaded','backendContainerStarted','backendContainerRunning','backendLocalLive','backendLocalReady','backendPort8080Listening','protectedEnvironmentMetadataPreserved')) {
        Require ([string]$values[$trueKey] -ceq 'true') "Remote backend deployment evidence '$trueKey' is not true."
    }
    Require ([string]$values.backendImageId -ceq $imageId) 'Remote backend deployment evidence image ID does not match the candidate.'
    Require ([string]$values.backendRuntimeUser -ceq 'app') 'Remote backend deployment evidence runtime user is not app.'
    Require ([string]$values.caddyStateBefore -ceq [string]$staging.caddyStateAfter -and [string]$values.caddyStateAfter -ceq [string]$staging.caddyStateAfter -and [string]$values.caddyModified -ceq 'false') 'Remote backend deployment changed Caddy state.'

    $evidence = [ordered]@{
        documentType = 'steward.closed-alpha-live-backend-deployment'
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        stagingCompletedAtUtc = $stagingCompletedAt.ToUniversalTime().ToString('O')
        stagingAgeSecondsAtStart = $stagingAgeSeconds
        requestSha256 = $requestSha256
        stagingEvidenceSha256 = $stagingEvidenceSha256
        releaseVersion = $version
        releaseCommitSha = $commitSha
        apiBaseUrl = $apiBaseUrl
        apiHost = $apiHost
        resolvedPublicIpv4 = [string]$resolved[0].ToString()
        sshUser = $sshUser
        sshPort = $sshPort
        sshHostKeySha256 = $observedFingerprint
        stagingFresh = $true
        dnsReverified = $true
        sshHostKeyReverified = $true
        remoteReleaseDirectory = [string]$request.host.remoteReleaseDirectory
        remoteDeploymentPlanDirectory = [string]$request.host.remoteDeploymentPlanDirectory
        remoteEnvironmentPath = [string]$request.host.remoteEnvironmentPath
        remoteBundleReverified = $true
        deploymentPlanReverified = $true
        deploymentPlanSha256 = [string]$staging.deploymentPlanSha256
        backendImageLoaded = $true
        backendImageId = $imageId
        backendRuntimeUser = 'app'
        backendContainerStarted = $true
        backendContainerRunning = $true
        backendLocalLive = $true
        backendLocalReady = $true
        backendPort8080Listening = $true
        protectedEnvironmentMetadataPreserved = $true
        protectedEnvironmentTransferred = $false
        caddyStateBefore = [string]$values.caddyStateBefore
        caddyStateAfter = [string]$values.caddyStateAfter
        caddyModified = $false
        publicHttpsVerified = $false
        externalPort8080ClosedVerified = $false
        publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
        physicalBringHere = [string]$manifest.publishAuthorization.physicalBringHere
        publicationAuthorizationChanged = $false
    }
    $evidenceText = $evidence | ConvertTo-Json -Depth 6
    Require ($evidenceText.Length -gt 0 -and $evidenceText.Length -le 64KB) 'Live backend deployment evidence is empty or exceeds 64 KiB.'
    [IO.File]::WriteAllText($evidenceFullPath,$evidenceText,[Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Exact staged backend image is running and locally ready without changing public ingress.'
    Write-Host "  Release: $version ($commitSha)"
    Write-Host "  Backend image ID: $imageId"
    Write-Host '  Local liveness/readiness: passed'
    Write-Host '  Caddy modified: no'
    Write-Host '  Public HTTPS verified: no'
    Write-Host '  External port-8080 closure verified: no'
    Write-Host '  Publication authorization changed: no'
}
finally {
    if ([IO.Directory]::Exists($workRoot)) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
