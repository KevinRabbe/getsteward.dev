[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [Parameter(Mandatory = $true)]
    [string]$PreflightEvidencePath,
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

$MaximumPreflightAge = [TimeSpan]::FromMinutes(15)
$MaximumClockSkew = [TimeSpan]::FromMinutes(2)

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
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
    if ($Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        return $false
    }
    $bytes = $Address.GetAddressBytes()
    if ($bytes.Length -ne 4) { return $false }
    $a = [int]$bytes[0]
    $b = [int]$bytes[1]
    $c = [int]$bytes[2]
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
    if ($Capture.IsPresent) {
        return ($output -join [Environment]::NewLine).Trim()
    }
    foreach ($line in $output) {
        Write-Host $line
    }
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

foreach ($tool in @('scp', 'ssh', 'ssh-keygen', 'ssh-keyscan', 'tar')) {
    Require-Tool $tool
}

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'
& $verifierPath -BundleDirectory $bundleRoot

$selfPath = [IO.Path]::GetFullPath($PSCommandPath)
$bundledSelfPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'stage-closed-alpha-live-deployment-plan.ps1'))
Require ([string]::Equals($selfPath, $bundledSelfPath, [StringComparison]::OrdinalIgnoreCase)) 'Run live plan staging from inside the verified release bundle.'

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$artifacts = @($manifest.artifacts)
$selfArtifact = Get-ArtifactByPath $artifacts 'stage-closed-alpha-live-deployment-plan.ps1'
$selfItem = Get-Item -LiteralPath $selfPath
Require ($null -eq $selfItem.LinkType) 'Live plan stager cannot be a symbolic link.'
Require ($selfItem.Length -eq [int64]$selfArtifact.byteSize) 'Live plan stager byte size does not match the release manifest.'
Require ((Get-FileHash -LiteralPath $selfPath -Algorithm SHA256).Hash -ceq [string]$selfArtifact.sha256) 'Live plan stager SHA-256 does not match the release manifest.'

$version = [string]$manifest.version
$commitSha = [string]$manifest.commitSha
$apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
$backendImageId = [string]$manifest.deployment.backendImageId
$backendImageTag = [string]$manifest.deployment.backendImageTag
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($backendImageId -match '^sha256:[0-9a-f]{64}$') 'Release backend image ID is malformed.'
Require (-not [string]::IsNullOrWhiteSpace($backendImageTag)) 'Release backend image tag is missing.'
Require ([string]$manifest.deployment.targetPlatform -ceq 'linux/amd64') 'Live plan staging requires linux/amd64.'
Require (-not [bool]$manifest.publishAuthorization.publishAllowed) 'Live plan staging cannot use a publish-authorized candidate.'
Require ([string]$manifest.publishAuthorization.physicalBringHere -ceq 'deferred') 'Live plan staging expects physical Bring Here to remain deferred.'

$requestFile = Read-BoundedJson $RequestPath 1MB 'Live deployment request'
$request = $requestFile.Json
Require-ExactProperties $request @(
    'createdAtUtc', 'documentType', 'executionContract', 'host', 'network',
    'publication', 'release', 'schemaVersion', 'secretBoundary'
) 'Live deployment request'
Require ([string]$request.documentType -ceq 'steward.closed-alpha-live-deployment-request') 'Unexpected live deployment request document type.'
Require ([int]$request.schemaVersion -eq 1) 'Unsupported live deployment request schema version.'
Require-ExactProperties $request.release @('apiBaseUrl', 'backendImageId', 'backendImageTag', 'backendTar', 'commitSha', 'version') 'Live deployment request release'
Require-ExactProperties $request.release.backendTar @('byteSize', 'path', 'sha256') 'Live deployment request backend TAR'
Require-ExactProperties $request.host @(
    'apiHost', 'caddyConfigurationPath', 'expectedPublicIpv4', 'remoteDeploymentPlanDirectory',
    'remoteEnvironmentPath', 'remoteReleaseDirectory', 'requiredTools', 'sshHost', 'sshHostKeySha256',
    'sshPort', 'sshUser', 'targetPlatform', 'topology'
) 'Live deployment request host'
Require-ExactProperties $request.network @(
    'backendKnownProxyIp', 'backendPort', 'caddyUpstream', 'forbiddenPublicBackendPort',
    'publicBackendPortMustFailClosed', 'publicCertificateBootstrapPort', 'publicTlsPort',
    'restrictedAdministrativePort'
) 'Live deployment request network'
Require-ExactProperties $request.executionContract @(
    'certificateBypassAllowed', 'deployScriptName', 'deploymentPlanner', 'environmentNeverLeavesLiveHost',
    'environmentTemplate', 'exactImageIdRequired', 'liveHostPreflight', 'liveRequestPlanner',
    'plannerRequireDeployable', 'plannerRunsOnLiveHost', 'publicTlsVerificationRequired',
    'publicVerifierName', 'releaseVerifier'
) 'Live deployment request execution contract'
Require-ExactProperties $request.secretBoundary @(
    'remoteEnvironmentPath', 'requiredRemoteMode', 'requiredRemoteOwner', 'secretEnvironmentBundled',
    'secretEnvironmentTransferredByRequest', 'secretValuesPresent'
) 'Live deployment request secret boundary'
Require-ExactProperties $request.publication @(
    'authorizationChanged', 'physicalBringHere', 'publishAllowed', 'requestAuthorizesPublication'
) 'Live deployment request publication'

Require ([string]$request.release.version -ceq $version) 'Request release version does not match the candidate.'
Require ([string]$request.release.commitSha -ceq $commitSha) 'Request release commit does not match the candidate.'
Require ([string]$request.release.apiBaseUrl -ceq $apiBaseUrl) 'Request API base URL does not match the candidate.'
Require ([string]$request.release.backendImageId -ceq $backendImageId) 'Request backend image ID does not match the candidate.'
Require ([string]$request.release.backendImageTag -ceq $backendImageTag) 'Request backend image tag does not match the candidate.'
$backendArtifact = Get-ArtifactByPath $artifacts ([string]$request.release.backendTar.path)
Require ([int64]$request.release.backendTar.byteSize -eq [int64]$backendArtifact.byteSize) 'Request backend TAR byte size does not match the candidate.'
Require ([string]$request.release.backendTar.sha256 -ceq [string]$backendArtifact.sha256) 'Request backend TAR SHA-256 does not match the candidate.'
Require-BindingMatchesArtifact $request.executionContract.releaseVerifier $artifacts 'verify-closed-alpha-release.ps1' 'Request release verifier binding'
Require-BindingMatchesArtifact $request.executionContract.deploymentPlanner $artifacts 'prepare-closed-alpha-deployment.ps1' 'Request deployment planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveRequestPlanner $artifacts 'prepare-closed-alpha-live-deployment.ps1' 'Request live request planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveHostPreflight $artifacts 'preflight-closed-alpha-live-host.ps1' 'Request live-host preflight binding'
Require-BindingMatchesArtifact $request.executionContract.environmentTemplate $artifacts 'backend/deployment.env.example' 'Request environment template binding'

$apiUri = $null
Require ([Uri]::TryCreate($apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) 'Candidate API base URL is malformed.'
$apiHost = $apiUri.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
Require ([string]$request.host.topology -ceq 'single-linux-amd64-host') 'Unsupported live-host topology.'
Require ([string]$request.host.apiHost -ceq $apiHost -and [string]$request.host.sshHost -ceq $apiHost) 'Request API/SSH host does not match the candidate API host.'
Require ([string]$request.host.targetPlatform -ceq 'linux/amd64') 'Request target platform changed.'
Require ([string]$request.host.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Request protected environment path changed.'
Require ([string]$request.secretBoundary.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Secret-boundary environment path changed.'
Require ([string]$request.secretBoundary.requiredRemoteOwner -ceq 'root' -and [string]$request.secretBoundary.requiredRemoteMode -ceq '0600') 'Protected environment metadata contract changed.'
Require (-not [bool]$request.secretBoundary.secretValuesPresent -and
    -not [bool]$request.secretBoundary.secretEnvironmentBundled -and
    -not [bool]$request.secretBoundary.secretEnvironmentTransferredByRequest) 'Request crossed the protected environment boundary.'
Require ([string]$request.host.remoteReleaseDirectory -ceq "/srv/steward/releases/$commitSha") 'Request release directory changed.'
Require ([string]$request.host.remoteDeploymentPlanDirectory -ceq "/srv/steward/deployment-plans/$commitSha") 'Request deployment-plan directory changed.'
Require ([string]$request.host.caddyConfigurationPath -ceq '/etc/caddy/Caddyfile') 'Request Caddy path changed.'
Require ([int]$request.network.backendPort -eq 8080 -and [int]$request.network.forbiddenPublicBackendPort -eq 8080 -and [bool]$request.network.publicBackendPortMustFailClosed) 'Request backend-port contract changed.'
Require ([string]$request.network.caddyUpstream -ceq '127.0.0.1:8080' -and [string]$request.network.backendKnownProxyIp -ceq '127.0.0.1') 'Request proxy contract changed.'
Require ([bool]$request.executionContract.plannerRunsOnLiveHost -and [bool]$request.executionContract.plannerRequireDeployable -and [bool]$request.executionContract.environmentNeverLeavesLiveHost) 'Request live planner contract changed.'
Require (-not [bool]$request.executionContract.certificateBypassAllowed) 'Certificate bypass cannot be allowed.'
Require (-not [bool]$request.publication.requestAuthorizesPublication -and -not [bool]$request.publication.authorizationChanged) 'Request cannot authorize or change publication state.'
Require (-not [bool]$request.publication.publishAllowed -and [string]$request.publication.physicalBringHere -ceq 'deferred') 'Request publication state is incompatible with staged closed alpha.'

$requestSha256 = (Get-FileHash -LiteralPath $requestFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()
$preflightFile = Read-BoundedJson $PreflightEvidencePath 1MB 'Live-host preflight evidence'
$preflight = $preflightFile.Json
Require-ExactProperties $preflight @(
    'apiBaseUrl', 'apiHost', 'backendPort8080Clear', 'candidateTransferred', 'deploymentPlanDirectoryAbsent',
    'deploymentStarted', 'dockerServerAccessible', 'documentType', 'observedAtUtc', 'physicalBringHere',
    'publicationAuthorizationChanged', 'publishAllowed', 'releaseCommitSha', 'releaseDirectoryAbsent',
    'releaseVersion', 'remoteArchitecture', 'remoteEnvironmentByteSize', 'remoteEnvironmentMode',
    'remoteEnvironmentOwnerUid', 'remoteEnvironmentPath', 'remoteOs', 'requestSha256',
    'requiredToolsPresent', 'resolvedPublicIpv4', 'schemaVersion', 'secretEnvironmentRead', 'sshHostKeySha256',
    'sshPort', 'sshUser', 'sudoNonInteractive'
) 'Live-host preflight evidence'
Require ([string]$preflight.documentType -ceq 'steward.closed-alpha-live-host-preflight') 'Unexpected live-host preflight evidence document type.'
Require ([int]$preflight.schemaVersion -eq 1) 'Unsupported live-host preflight evidence schema version.'
Require ([string]$preflight.requestSha256 -ceq $requestSha256) 'Preflight evidence does not bind the exact live deployment request.'
Require ([string]$preflight.releaseVersion -ceq $version -and [string]$preflight.releaseCommitSha -ceq $commitSha) 'Preflight evidence does not bind the exact candidate release.'
Require ([string]$preflight.apiBaseUrl -ceq $apiBaseUrl -and [string]$preflight.apiHost -ceq $apiHost) 'Preflight evidence API identity changed.'
Require ([string]$preflight.resolvedPublicIpv4 -ceq [string]$request.host.expectedPublicIpv4) 'Preflight evidence DNS address does not match the request.'
Require ([string]$preflight.sshUser -ceq [string]$request.host.sshUser -and [int]$preflight.sshPort -eq [int]$request.host.sshPort) 'Preflight evidence SSH endpoint does not match the request.'
Require ([string]$preflight.sshHostKeySha256 -ceq [string]$request.host.sshHostKeySha256) 'Preflight evidence SSH host key does not match the request.'
Require ([string]$preflight.remoteOs -ceq 'Linux' -and [string]$preflight.remoteArchitecture -ceq 'x86_64') 'Preflight evidence platform is incompatible.'
Require ([bool]$preflight.requiredToolsPresent -and [bool]$preflight.sudoNonInteractive -and [bool]$preflight.dockerServerAccessible) 'Preflight evidence does not prove required remote capabilities.'
Require ([string]$preflight.remoteEnvironmentPath -ceq '/etc/steward/backend.env' -and [int]$preflight.remoteEnvironmentOwnerUid -eq 0 -and [string]$preflight.remoteEnvironmentMode -ceq '0600') 'Preflight evidence does not prove protected environment metadata.'
Require ([int64]$preflight.remoteEnvironmentByteSize -gt 0 -and [int64]$preflight.remoteEnvironmentByteSize -le 65536) 'Preflight evidence protected environment size is invalid.'
Require ([bool]$preflight.backendPort8080Clear -and [bool]$preflight.releaseDirectoryAbsent -and [bool]$preflight.deploymentPlanDirectoryAbsent) 'Preflight evidence does not prove a clean first-deployment target.'
Require (-not [bool]$preflight.candidateTransferred -and -not [bool]$preflight.deploymentStarted -and -not [bool]$preflight.secretEnvironmentRead) 'Preflight evidence already crossed its read-only boundary.'
Require (-not [bool]$preflight.publishAllowed -and [string]$preflight.physicalBringHere -ceq 'deferred' -and -not [bool]$preflight.publicationAuthorizationChanged) 'Preflight evidence changed or overstated publication state.'

$observedAt = [DateTimeOffset]::MinValue
Require ([DateTimeOffset]::TryParse(
    [string]$preflight.observedAtUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::None,
    [ref]$observedAt)) 'Preflight evidence observedAtUtc is malformed.'
$now = [DateTimeOffset]::UtcNow
$age = $now - $observedAt.ToUniversalTime()
Require ($age -ge $MaximumClockSkew.Negate()) 'Preflight evidence timestamp is too far in the future.'
Require ($age -le $MaximumPreflightAge) 'Preflight evidence is stale; run the live-host preflight again before transfer.'
$preflightAgeSeconds = [Math]::Max(0, [int][Math]::Floor($age.TotalSeconds))
$preflightEvidenceSha256 = (Get-FileHash -LiteralPath $preflightFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()

$expectedAddress = $null
Require ([Net.IPAddress]::TryParse([string]$request.host.expectedPublicIpv4, [ref]$expectedAddress)) 'Request expected public IPv4 is malformed.'
Require (Test-PublicIpv4 $expectedAddress) 'Request expected IPv4 is not globally routable.'
$resolved = @([Net.Dns]::GetHostAddresses($apiHost) | Sort-Object -Property IPAddressToString -Unique)
Require ($resolved.Count -eq 1) "Live API hostname must still resolve to exactly one address before transfer; resolved $($resolved.Count)."
Require ($resolved[0].AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) 'Live API hostname no longer resolves to exactly one IPv4 address.'
Require ([string]$resolved[0].ToString() -ceq [string]$expectedAddress.ToString()) 'Live API DNS changed after preflight.'

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

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-plan-staging-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$knownHostsPath = Join-Path $workRoot 'known_hosts'
$archivePath = Join-Path $workRoot 'candidate.tar'
$remoteArchive = "/home/$sshUser/.steward-candidate-$commitSha-$([Guid]::NewGuid().ToString('N')).tar"
$remoteTarget = "$sshUser@$apiHost"
$sshOptions = $null
$remoteArchiveTransferred = $false

try {
    $scanOutput = @(& ssh-keyscan -4 -T 10 -p $sshPort -t ed25519 $apiHost 2>$null)
    Require ($LASTEXITCODE -eq 0 -and $scanOutput.Count -gt 0) 'Could not re-observe the live SSH Ed25519 host key before transfer.'
    $hostKeyLines = @($scanOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    Require ($hostKeyLines.Count -eq 1) 'Expected exactly one live SSH Ed25519 host key before transfer.'
    Write-Utf8NoBom -Path $knownHostsPath -Content (([string]$hostKeyLines[0]).Trim() + "`n")
    $fingerprintText = Invoke-RequiredNative -Tool 'ssh-keygen' -Arguments @('-E', 'sha256', '-lf', $knownHostsPath) -Context 'SSH host-key fingerprint revalidation' -Capture
    $fingerprintMatch = [Text.RegularExpressions.Regex]::Match($fingerprintText, 'SHA256:[A-Za-z0-9+/]{43}')
    Require $fingerprintMatch.Success 'Re-observed SSH host-key fingerprint is malformed.'
    $observedFingerprint = $fingerprintMatch.Value
    Require ($observedFingerprint -ceq $expectedFingerprint) 'SSH host key changed after preflight; candidate transfer refused.'

    $sshOptions = @(
        '-4', '-i', $keyFullPath,
        '-p', $sshPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-o', "UserKnownHostsFile=$knownHostsPath",
        '-o', 'StrictHostKeyChecking=yes',
        '-o', 'BatchMode=yes',
        '-o', 'IdentitiesOnly=yes',
        '-o', 'PasswordAuthentication=no',
        '-o', 'KbdInteractiveAuthentication=no',
        '-o', 'ConnectTimeout=10'
    )
    $scpOptions = @(
        '-4', '-i', $keyFullPath,
        '-P', $sshPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-o', "UserKnownHostsFile=$knownHostsPath",
        '-o', 'StrictHostKeyChecking=yes',
        '-o', 'BatchMode=yes',
        '-o', 'IdentitiesOnly=yes',
        '-o', 'PasswordAuthentication=no',
        '-o', 'KbdInteractiveAuthentication=no',
        '-o', 'ConnectTimeout=10'
    )

    $freshCheckScript = @'
set -euo pipefail
release_dir="${1:?release directory required}"
plan_dir="${2:?plan directory required}"
env_path="${3:?environment path required}"
image_id="${4:?image ID required}"
caddy_path="${5:?Caddy path required}"

[[ -f "${env_path}" && ! -L "${env_path}" ]] || { echo 'protected backend environment is missing, not regular, or is a symlink' >&2; exit 1; }
[[ "$(stat -Lc '%u' "${env_path}")" == '0' ]] || { echo 'protected backend environment is not root-owned' >&2; exit 1; }
[[ "$(stat -Lc '%a' "${env_path}")" == '600' ]] || { echo 'protected backend environment mode is not 0600' >&2; exit 1; }
env_size="$(stat -Lc '%s' "${env_path}")"
[[ "${env_size}" =~ ^[0-9]+$ && "${env_size}" -gt 0 && "${env_size}" -le 65536 ]] || { echo 'protected backend environment size is outside the supported bound' >&2; exit 1; }
[[ ! -e "${release_dir}" ]] || { echo 'exact release directory appeared after preflight' >&2; exit 1; }
[[ ! -e "${plan_dir}" ]] || { echo 'exact deployment-plan directory appeared after preflight' >&2; exit 1; }
if docker container inspect steward-backend >/dev/null 2>&1; then echo 'steward-backend container appeared after preflight' >&2; exit 1; fi
if docker image inspect "${image_id}" >/dev/null 2>&1; then echo 'exact backend image unexpectedly preexists before live plan staging' >&2; exit 1; fi
if ss -ltnH 'sport = :8080' | grep -q .; then echo 'backend port 8080 became occupied after preflight' >&2; exit 1; fi
if [[ -e "${caddy_path}" ]]; then
  [[ -f "${caddy_path}" && ! -L "${caddy_path}" ]] || { echo 'Caddy configuration path is not a regular non-symlink file' >&2; exit 1; }
  caddy_state="sha256:$(sha256sum "${caddy_path}" | awk '{print toupper($1)}')"
else
  caddy_state='absent'
fi
printf 'caddyState=%s\n' "${caddy_state}"
printf 'environmentByteSize=%s\n' "${env_size}"
printf 'targetStillClean=true\n'
printf 'backendImagePreexisting=false\n'
'@
    $freshArgs = @(
        [string]$request.host.remoteReleaseDirectory,
        [string]$request.host.remoteDeploymentPlanDirectory,
        [string]$request.host.remoteEnvironmentPath,
        $backendImageId,
        [string]$request.host.caddyConfigurationPath
    ) | ForEach-Object { ConvertTo-ShellSingleQuoted ([string]$_) }
    $freshCommand = 'sudo -n bash -s -- ' + ($freshArgs -join ' ')
    $freshOutput = @($freshCheckScript | & ssh @sshOptions $remoteTarget $freshCommand 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Fresh live-host recheck failed before candidate transfer. $(($freshOutput -join [Environment]::NewLine).Trim())"
    }
    $freshValues = @{}
    foreach ($lineObject in $freshOutput) {
        $line = ([string]$lineObject).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $separator = $line.IndexOf('=')
        Require ($separator -gt 0) "Fresh live-host recheck emitted an unexpected line: $line"
        $name = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        Require (-not $freshValues.ContainsKey($name)) "Fresh live-host recheck emitted duplicate key '$name'."
        $freshValues[$name] = $value
    }
    $freshExpectedKeys = @('backendImagePreexisting', 'caddyState', 'environmentByteSize', 'targetStillClean') | Sort-Object
    $freshActualKeys = @($freshValues.Keys | Sort-Object)
    Require ($freshActualKeys.Count -eq $freshExpectedKeys.Count -and @(Compare-Object $freshActualKeys $freshExpectedKeys).Count -eq 0) 'Fresh live-host recheck emitted missing or unexpected keys.'
    Require ([string]$freshValues.targetStillClean -ceq 'true') 'Fresh live-host recheck did not prove a clean target.'
    Require ([string]$freshValues.backendImagePreexisting -ceq 'false') 'Exact backend image preexists before live plan staging.'
    $freshEnvironmentByteSize = 0L
    Require ([int64]::TryParse([string]$freshValues.environmentByteSize, [ref]$freshEnvironmentByteSize) -and $freshEnvironmentByteSize -gt 0 -and $freshEnvironmentByteSize -le 65536) 'Fresh protected environment size evidence is invalid.'
    $caddyStateBefore = [string]$freshValues.caddyState
    Require ($caddyStateBefore -eq 'absent' -or $caddyStateBefore -match '^sha256:[0-9A-F]{64}$') 'Fresh Caddy state is malformed.'

    Invoke-RequiredNative -Tool 'tar' -Arguments @(
        '--sort=name', '--mtime=UTC 1970-01-01', '--owner=0', '--group=0', '--numeric-owner',
        '-C', $bundleRoot, '-cf', $archivePath, '.'
    ) -Context 'Deterministic candidate archive creation'
    $archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

    Invoke-RequiredNative -Tool 'scp' -Arguments ($scpOptions + @($archivePath, "${remoteTarget}:$remoteArchive")) -Context 'Exact candidate transfer after fresh preflight'
    $remoteArchiveTransferred = $true

    $remoteStageScript = @'
set -euo pipefail
archive_path="${1:?archive path required}"
archive_sha256="${2:?archive SHA-256 required}"
release_dir="${3:?release directory required}"
plan_dir="${4:?plan directory required}"
env_path="${5:?environment path required}"
image_id="${6:?image ID required}"
release_version="${7:?release version required}"
release_commit="${8:?release commit required}"
api_base_url="${9:?API base URL required}"
caddy_path="${10:?Caddy path required}"
caddy_state_before="${11:?Caddy state required}"

release_tmp="${release_dir}.partial.$$"
plan_tmp="${plan_dir}.partial.$$"
created_release=0
created_plan=0
success=0
cleanup() {
  rm -f "${archive_path}" >/dev/null 2>&1 || true
  rm -rf "${release_tmp}" "${plan_tmp}" >/dev/null 2>&1 || true
  if [[ "${success}" != '1' ]]; then
    if [[ "${created_release}" == '1' ]]; then rm -rf "${release_dir}" >/dev/null 2>&1 || true; fi
    if [[ "${created_plan}" == '1' ]]; then rm -rf "${plan_dir}" >/dev/null 2>&1 || true; fi
  fi
}
trap cleanup EXIT

actual_archive_sha256="$(sha256sum "${archive_path}" | awk '{print tolower($1)}')"
[[ "${actual_archive_sha256}" == "${archive_sha256}" ]] || { echo 'candidate archive SHA-256 mismatch after transfer' >&2; exit 1; }
[[ ! -e "${release_dir}" ]] || { echo 'exact release directory is no longer absent' >&2; exit 1; }
[[ ! -e "${plan_dir}" ]] || { echo 'exact deployment-plan directory is no longer absent' >&2; exit 1; }
if docker image inspect "${image_id}" >/dev/null 2>&1; then echo 'exact backend image unexpectedly preexists before remote plan generation' >&2; exit 1; fi
if docker container inspect steward-backend >/dev/null 2>&1; then echo 'steward-backend container exists before remote plan generation' >&2; exit 1; fi
if ss -ltnH 'sport = :8080' | grep -q .; then echo 'backend port 8080 is occupied before remote plan generation' >&2; exit 1; fi
[[ -f "${env_path}" && ! -L "${env_path}" ]] || { echo 'protected backend environment is missing, not regular, or is a symlink' >&2; exit 1; }
[[ "$(stat -Lc '%u' "${env_path}")" == '0' ]] || { echo 'protected backend environment is not root-owned' >&2; exit 1; }
[[ "$(stat -Lc '%a' "${env_path}")" == '600' ]] || { echo 'protected backend environment mode is not 0600' >&2; exit 1; }
if [[ -e "${caddy_path}" ]]; then caddy_state_now="sha256:$(sha256sum "${caddy_path}" | awk '{print toupper($1)}')"; else caddy_state_now='absent'; fi
[[ "${caddy_state_now}" == "${caddy_state_before}" ]] || { echo 'Caddy configuration changed before remote plan generation' >&2; exit 1; }

install -d -m 0755 -o root -g root "$(dirname "${release_dir}")" "$(dirname "${plan_dir}")"
mkdir "${release_tmp}" "${plan_tmp}"
tar -xf "${archive_path}" -C "${release_tmp}"
if ! pwsh -NoLogo -NoProfile -File "${release_tmp}/verify-closed-alpha-release.ps1" -BundleDirectory "${release_tmp}" >/dev/null 2>&1; then
  echo 'remote release verification failed' >&2
  exit 1
fi
if ! pwsh -NoLogo -NoProfile -File "${release_tmp}/prepare-closed-alpha-deployment.ps1" \
  -BundleDirectory "${release_tmp}" \
  -EnvironmentFile "${env_path}" \
  -OutputDirectory "${plan_tmp}" \
  -HostEnvironmentPath "${env_path}" \
  -RequireDeployable >/dev/null 2>&1; then
  echo 'remote deployment planner rejected the protected environment or release coordinates' >&2
  exit 1
fi

export STEWARD_PLAN_PATH="${plan_tmp}/deployment-plan.json"
export STEWARD_PLAN_DIR="${plan_tmp}"
export STEWARD_EXPECTED_VERSION="${release_version}"
export STEWARD_EXPECTED_COMMIT="${release_commit}"
export STEWARD_EXPECTED_API="${api_base_url}"
export STEWARD_EXPECTED_IMAGE="${image_id}"
export STEWARD_EXPECTED_ENV="${env_path}"
if ! pwsh -NoLogo -NoProfile -Command '
$ErrorActionPreference = "Stop"
$planPath = $env:STEWARD_PLAN_PATH
if (-not [IO.File]::Exists($planPath)) { throw "deployment-plan.json is missing" }
$plan = [IO.File]::ReadAllText($planPath) | ConvertFrom-Json
if ([string]$plan.documentType -cne "steward.closed-alpha-deployment-plan" -or [int]$plan.schemaVersion -ne 1) { throw "deployment plan identity is invalid" }
if ([string]$plan.release.version -cne $env:STEWARD_EXPECTED_VERSION) { throw "deployment plan version mismatch" }
if ([string]$plan.release.commitSha -cne $env:STEWARD_EXPECTED_COMMIT) { throw "deployment plan commit mismatch" }
if ([string]$plan.release.apiBaseUrl -cne $env:STEWARD_EXPECTED_API) { throw "deployment plan API mismatch" }
if ([string]$plan.release.backendImageId -cne $env:STEWARD_EXPECTED_IMAGE) { throw "deployment plan image mismatch" }
if ([string]$plan.host.environmentFilePath -cne $env:STEWARD_EXPECTED_ENV) { throw "deployment plan environment path mismatch" }
if (-not [bool]$plan.deployability.deployable) { throw "deployment plan is not deployable" }
if ([bool]$plan.configuration.secretValuesCopied) { throw "deployment plan reports copied secret values" }
$generated = @($plan.generatedFiles)
if ($generated.Count -ne 3) { throw "deployment plan must bind exactly three generated files" }
$expectedNames = @("Caddyfile", "deploy-exact-candidate.sh", "verify-public-https.sh") | Sort-Object
$actualNames = @($generated | ForEach-Object { [string]$_.path } | Sort-Object)
if (@(Compare-Object $expectedNames $actualNames).Count -ne 0) { throw "deployment plan generated-file set mismatch" }
foreach ($entry in $generated) {
  $name = [string]$entry.path
  if ($name -notmatch "^[A-Za-z0-9._-]+$") { throw "unsafe generated-file path" }
  $path = Join-Path $env:STEWARD_PLAN_DIR $name
  if (-not [IO.File]::Exists($path)) { throw "generated file is missing: $name" }
  $item = Get-Item -LiteralPath $path
  if ($null -ne $item.LinkType) { throw "generated file cannot be a symbolic link: $name" }
  if ($item.Length -ne [int64]$entry.byteSize) { throw "generated file size mismatch: $name" }
  $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
  if ($hash -cne [string]$entry.sha256) { throw "generated file hash mismatch: $name" }
}
' >/dev/null 2>&1; then
  echo 'generated deployment-plan validation failed' >&2
  exit 1
fi

if docker image inspect "${image_id}" >/dev/null 2>&1; then echo 'live plan staging unexpectedly loaded the backend image' >&2; exit 1; fi
if docker container inspect steward-backend >/dev/null 2>&1; then echo 'live plan staging unexpectedly created the backend container' >&2; exit 1; fi
if ss -ltnH 'sport = :8080' | grep -q .; then echo 'live plan staging unexpectedly occupied backend port 8080' >&2; exit 1; fi
if [[ -e "${caddy_path}" ]]; then caddy_state_after="sha256:$(sha256sum "${caddy_path}" | awk '{print toupper($1)}')"; else caddy_state_after='absent'; fi
[[ "${caddy_state_after}" == "${caddy_state_before}" ]] || { echo 'live plan staging modified the Caddy configuration' >&2; exit 1; }

mv "${release_tmp}" "${release_dir}"
created_release=1
mv "${plan_tmp}" "${plan_dir}"
created_plan=1
plan_sha256="$(sha256sum "${plan_dir}/deployment-plan.json" | awk '{print toupper($1)}')"
success=1

printf 'candidateArchiveSha256=%s\n' "$(printf '%s' "${archive_sha256}" | tr '[:lower:]' '[:upper:]')"
printf 'remoteBundleVerified=true\n'
printf 'deploymentPlanGenerated=true\n'
printf 'deploymentPlanSha256=%s\n' "${plan_sha256}"
printf 'protectedEnvironmentReadOnHost=true\n'
printf 'protectedEnvironmentTransferred=false\n'
printf 'deploymentPlanSecretValuesCopied=false\n'
printf 'backendImagePreexisting=false\n'
printf 'backendImageLoaded=false\n'
printf 'backendContainerStarted=false\n'
printf 'deploymentStarted=false\n'
printf 'backendPort8080Clear=true\n'
printf 'caddyStateBefore=%s\n' "${caddy_state_before}"
printf 'caddyStateAfter=%s\n' "${caddy_state_after}"
printf 'caddyModified=false\n'
printf 'releaseDirectory=%s\n' "${release_dir}"
printf 'deploymentPlanDirectory=%s\n' "${plan_dir}"
'@
    $stageArgs = @(
        $remoteArchive, $archiveSha256,
        [string]$request.host.remoteReleaseDirectory,
        [string]$request.host.remoteDeploymentPlanDirectory,
        [string]$request.host.remoteEnvironmentPath,
        $backendImageId, $version, $commitSha, $apiBaseUrl,
        [string]$request.host.caddyConfigurationPath, $caddyStateBefore
    ) | ForEach-Object { ConvertTo-ShellSingleQuoted ([string]$_) }
    $stageCommand = 'sudo -n bash -s -- ' + ($stageArgs -join ' ')
    $stageOutput = @($remoteStageScript | & ssh @sshOptions $remoteTarget $stageCommand 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Remote live plan staging failed. $(($stageOutput -join [Environment]::NewLine).Trim())"
    }
    $remoteArchiveTransferred = $false

    $remoteValues = @{}
    foreach ($lineObject in $stageOutput) {
        $line = ([string]$lineObject).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $separator = $line.IndexOf('=')
        Require ($separator -gt 0) "Remote staging emitted an unexpected line."
        $name = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        Require ($name -in @(
            'candidateArchiveSha256', 'remoteBundleVerified', 'deploymentPlanGenerated', 'deploymentPlanSha256',
            'protectedEnvironmentReadOnHost', 'protectedEnvironmentTransferred', 'deploymentPlanSecretValuesCopied',
            'backendImagePreexisting', 'backendImageLoaded', 'backendContainerStarted', 'deploymentStarted',
            'backendPort8080Clear', 'caddyStateBefore', 'caddyStateAfter', 'caddyModified', 'releaseDirectory',
            'deploymentPlanDirectory'
        )) "Remote staging emitted an unexpected evidence key '$name'."
        Require (-not $remoteValues.ContainsKey($name)) "Remote staging emitted duplicate evidence key '$name'."
        $remoteValues[$name] = $value
    }
    $expectedRemoteKeys = @(
        'backendContainerStarted', 'backendImageLoaded', 'backendImagePreexisting', 'backendPort8080Clear',
        'caddyModified', 'caddyStateAfter', 'caddyStateBefore', 'candidateArchiveSha256',
        'deploymentPlanDirectory', 'deploymentPlanGenerated', 'deploymentPlanSecretValuesCopied',
        'deploymentPlanSha256', 'deploymentStarted', 'protectedEnvironmentReadOnHost',
        'protectedEnvironmentTransferred', 'releaseDirectory', 'remoteBundleVerified'
    ) | Sort-Object
    $actualRemoteKeys = @($remoteValues.Keys | Sort-Object)
    Require ($actualRemoteKeys.Count -eq $expectedRemoteKeys.Count -and @(Compare-Object $actualRemoteKeys $expectedRemoteKeys).Count -eq 0) 'Remote live plan staging evidence contains missing or unexpected keys.'
    Require ([string]$remoteValues.candidateArchiveSha256 -ceq $archiveSha256.ToUpperInvariant()) 'Remote staging archive SHA-256 does not match the transferred candidate.'
    foreach ($trueKey in @('remoteBundleVerified', 'deploymentPlanGenerated', 'protectedEnvironmentReadOnHost', 'backendPort8080Clear')) {
        Require ([string]$remoteValues[$trueKey] -ceq 'true') "Remote staging evidence '$trueKey' is not true."
    }
    foreach ($falseKey in @('protectedEnvironmentTransferred', 'deploymentPlanSecretValuesCopied', 'backendImagePreexisting', 'backendImageLoaded', 'backendContainerStarted', 'deploymentStarted', 'caddyModified')) {
        Require ([string]$remoteValues[$falseKey] -ceq 'false') "Remote staging evidence '$falseKey' is not false."
    }
    Require ([string]$remoteValues.releaseDirectory -ceq [string]$request.host.remoteReleaseDirectory) 'Remote staging release directory changed.'
    Require ([string]$remoteValues.deploymentPlanDirectory -ceq [string]$request.host.remoteDeploymentPlanDirectory) 'Remote staging deployment-plan directory changed.'
    Require ([string]$remoteValues.caddyStateBefore -ceq $caddyStateBefore -and [string]$remoteValues.caddyStateAfter -ceq $caddyStateBefore) 'Remote staging Caddy state changed.'
    Require ([string]$remoteValues.deploymentPlanSha256 -match '^[0-9A-F]{64}$') 'Remote deployment-plan SHA-256 is malformed.'

    $evidence = [ordered]@{
        documentType = 'steward.closed-alpha-live-plan-staging'
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        preflightObservedAtUtc = $observedAt.ToUniversalTime().ToString('O')
        preflightAgeSecondsAtStart = $preflightAgeSeconds
        requestSha256 = $requestSha256
        preflightEvidenceSha256 = $preflightEvidenceSha256
        candidateArchiveSha256 = $archiveSha256.ToUpperInvariant()
        releaseVersion = $version
        releaseCommitSha = $commitSha
        apiBaseUrl = $apiBaseUrl
        apiHost = $apiHost
        resolvedPublicIpv4 = [string]$resolved[0].ToString()
        sshUser = $sshUser
        sshPort = $sshPort
        sshHostKeySha256 = $observedFingerprint
        preflightFresh = $true
        dnsReverified = $true
        sshHostKeyReverified = $true
        remoteReleaseDirectory = [string]$remoteValues.releaseDirectory
        remoteDeploymentPlanDirectory = [string]$remoteValues.deploymentPlanDirectory
        remoteEnvironmentPath = [string]$request.host.remoteEnvironmentPath
        candidateTransferred = $true
        remoteBundleVerified = $true
        deploymentPlanGenerated = $true
        deploymentPlanSha256 = [string]$remoteValues.deploymentPlanSha256
        protectedEnvironmentReadOnHost = $true
        protectedEnvironmentTransferred = $false
        deploymentPlanSecretValuesCopied = $false
        backendImagePreexisting = $false
        backendImageLoaded = $false
        backendContainerStarted = $false
        deploymentStarted = $false
        backendPort8080Clear = $true
        caddyStateBefore = [string]$remoteValues.caddyStateBefore
        caddyStateAfter = [string]$remoteValues.caddyStateAfter
        caddyModified = $false
        publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
        physicalBringHere = [string]$manifest.publishAuthorization.physicalBringHere
        publicationAuthorizationChanged = $false
    }
    $evidenceText = $evidence | ConvertTo-Json -Depth 6
    Require ($evidenceText.Length -gt 0 -and $evidenceText.Length -le 64KB) 'Live plan staging evidence is empty or exceeds 64 KiB.'
    [IO.File]::WriteAllText($evidenceFullPath, $evidenceText, [Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Exact candidate transferred, remotely verified, and converted into a live deployment plan without deployment.'
    Write-Host "  Release: $version ($commitSha)"
    Write-Host "  DNS reverified: $apiHost -> $([string]$resolved[0].ToString())"
    Write-Host "  SSH host key reverified: $observedFingerprint"
    Write-Host "  Remote release: $([string]$remoteValues.releaseDirectory)"
    Write-Host "  Remote plan: $([string]$remoteValues.deploymentPlanDirectory)"
    Write-Host '  Protected environment read on host: yes'
    Write-Host '  Protected environment transferred: no'
    Write-Host '  Backend image loaded: no'
    Write-Host '  Deployment started: no'
    Write-Host '  Caddy modified: no'
    Write-Host '  Publication authorization changed: no'
}
finally {
    if ($remoteArchiveTransferred -and $null -ne $sshOptions) {
        $cleanupCommand = 'rm -f ' + (ConvertTo-ShellSingleQuoted $remoteArchive)
        & ssh @sshOptions $remoteTarget $cleanupCommand *> $null
    }
    if ([IO.Directory]::Exists($workRoot)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
