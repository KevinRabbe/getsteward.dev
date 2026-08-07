[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
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
    if ($bytes.Length -ne 4) {
        return $false
    }
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

foreach ($tool in @('ssh', 'ssh-keygen', 'ssh-keyscan')) {
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
$bundledSelfPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'preflight-closed-alpha-live-host.ps1'))
Require ([string]::Equals($selfPath, $bundledSelfPath, [StringComparison]::OrdinalIgnoreCase)) 'Run the live-host preflight from inside the verified release bundle.'

$requestFullPath = [IO.Path]::GetFullPath($RequestPath)
Require ([IO.File]::Exists($requestFullPath)) "Live deployment request does not exist: $requestFullPath"
$requestItem = Get-Item -LiteralPath $requestFullPath
Require ($null -eq $requestItem.LinkType) 'Live deployment request cannot be a symbolic link.'
Require ($requestItem.Length -gt 0 -and $requestItem.Length -le 1MB) 'Live deployment request is empty or exceeds 1 MiB.'
$requestText = [IO.File]::ReadAllText($requestFullPath)
$request = $requestText | ConvertFrom-Json

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

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$artifacts = @($manifest.artifacts)
Require ([string]$request.release.version -ceq [string]$manifest.version) 'Request release version does not match the candidate.'
Require ([string]$request.release.commitSha -ceq [string]$manifest.commitSha) 'Request release commit does not match the candidate.'
Require ([string]$request.release.apiBaseUrl -ceq [string]$manifest.deployment.apiBaseUrl) 'Request API base URL does not match the candidate.'
Require ([string]$request.release.backendImageId -ceq [string]$manifest.deployment.backendImageId) 'Request backend image ID does not match the candidate.'
Require ([string]$request.release.backendImageTag -ceq [string]$manifest.deployment.backendImageTag) 'Request backend image tag does not match the candidate.'

$backendArtifact = Get-ArtifactByPath $artifacts ([string]$request.release.backendTar.path)
Require ([int64]$request.release.backendTar.byteSize -eq [int64]$backendArtifact.byteSize) 'Request backend TAR byte size does not match the candidate.'
Require ([string]$request.release.backendTar.sha256 -ceq [string]$backendArtifact.sha256) 'Request backend TAR SHA-256 does not match the candidate.'
Require-BindingMatchesArtifact $request.executionContract.releaseVerifier $artifacts 'verify-closed-alpha-release.ps1' 'Request release verifier binding'
Require-BindingMatchesArtifact $request.executionContract.deploymentPlanner $artifacts 'prepare-closed-alpha-deployment.ps1' 'Request deployment planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveRequestPlanner $artifacts 'prepare-closed-alpha-live-deployment.ps1' 'Request live request planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveHostPreflight $artifacts 'preflight-closed-alpha-live-host.ps1' 'Request live-host preflight binding'
Require-BindingMatchesArtifact $request.executionContract.environmentTemplate $artifacts 'backend/deployment.env.example' 'Request environment template binding'

$apiUri = $null
Require ([Uri]::TryCreate([string]$request.release.apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) 'Request API base URL is malformed.'
Require ($apiUri.Scheme -ceq 'https' -and $apiUri.IsDefaultPort -and $apiUri.AbsolutePath -ceq '/') 'Request API base URL is not the required HTTPS origin root.'
$apiHost = $apiUri.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
Require ([string]$request.host.topology -ceq 'single-linux-amd64-host') 'Unsupported live-host topology.'
Require ([string]$request.host.apiHost -ceq $apiHost) 'Request API host does not match its API URL.'
Require ([string]$request.host.sshHost -ceq $apiHost) 'First live topology requires the API hostname as the SSH host.'
Require ([string]$request.host.targetPlatform -ceq 'linux/amd64') 'Live-host target platform must be linux/amd64.'
Require ([string]$request.host.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Live-host environment path changed.'
Require ([string]$request.secretBoundary.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Secret-boundary environment path changed.'
Require ([string]$request.secretBoundary.requiredRemoteOwner -ceq 'root') 'Live environment owner contract changed.'
Require ([string]$request.secretBoundary.requiredRemoteMode -ceq '0600') 'Live environment mode contract changed.'
Require (-not [bool]$request.secretBoundary.secretValuesPresent -and
    -not [bool]$request.secretBoundary.secretEnvironmentBundled -and
    -not [bool]$request.secretBoundary.secretEnvironmentTransferredByRequest) 'Live deployment request crossed the secret boundary.'
Require ([string]$request.host.remoteReleaseDirectory -ceq "/srv/steward/releases/$([string]$manifest.commitSha)") 'Live release directory does not match the release commit.'
Require ([string]$request.host.remoteDeploymentPlanDirectory -ceq "/srv/steward/deployment-plans/$([string]$manifest.commitSha)") 'Live deployment-plan directory does not match the release commit.'
Require ([string]$request.host.caddyConfigurationPath -ceq '/etc/caddy/Caddyfile') 'Caddy configuration path changed.'
Require ([int]$request.network.publicTlsPort -eq 443 -and [int]$request.network.publicCertificateBootstrapPort -eq 80) 'Public TLS/bootstrap port contract changed.'
Require ([int]$request.network.backendPort -eq 8080 -and [int]$request.network.forbiddenPublicBackendPort -eq 8080) 'Backend port contract changed.'
Require ([string]$request.network.caddyUpstream -ceq '127.0.0.1:8080' -and [string]$request.network.backendKnownProxyIp -ceq '127.0.0.1') 'One-proxy contract changed.'
Require ([bool]$request.network.publicBackendPortMustFailClosed) 'Public backend port must remain fail-closed.'
Require ([bool]$request.executionContract.plannerRunsOnLiveHost -and [bool]$request.executionContract.plannerRequireDeployable) 'Live deployment planner execution contract changed.'
Require ([bool]$request.executionContract.environmentNeverLeavesLiveHost) 'Protected environment must remain on the live host.'
Require ([bool]$request.executionContract.exactImageIdRequired -and [bool]$request.executionContract.publicTlsVerificationRequired) 'Exact-image or public-TLS verification contract changed.'
Require (-not [bool]$request.executionContract.certificateBypassAllowed) 'Certificate bypass cannot be allowed.'
Require (-not [bool]$request.publication.requestAuthorizesPublication -and -not [bool]$request.publication.authorizationChanged) 'Live request cannot authorize or change publication state.'
Require ([string]$request.publication.physicalBringHere -ceq [string]$manifest.publishAuthorization.physicalBringHere -and
    [bool]$request.publication.publishAllowed -eq [bool]$manifest.publishAuthorization.publishAllowed) 'Live request publication state does not match the candidate.'

$expectedAddress = $null
Require ([Net.IPAddress]::TryParse([string]$request.host.expectedPublicIpv4, [ref]$expectedAddress)) 'Expected public IPv4 is malformed.'
Require (Test-PublicIpv4 $expectedAddress) 'Expected IPv4 is not globally routable under the first live-host contract.'
$resolved = @([Net.Dns]::GetHostAddresses($apiHost) | Sort-Object -Property IPAddressToString -Unique)
Require ($resolved.Count -eq 1) "Live API hostname must resolve to exactly one address for the first acceptance topology; resolved $($resolved.Count)."
Require ($resolved[0].AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) 'Live API hostname resolved to IPv6; first acceptance topology requires exactly one IPv4 address.'
Require ([string]$resolved[0].ToString() -ceq [string]$expectedAddress.ToString()) 'Live API DNS does not resolve to the request-bound public IPv4.'

$sshPort = [int]$request.host.sshPort
Require ($sshPort -ge 1 -and $sshPort -le 65535) 'SSH port is outside the valid range.'
Require ([int]$request.network.restrictedAdministrativePort -eq $sshPort) 'Restricted administrative port does not match SSH port.'
$sshUser = [string]$request.host.sshUser
Require ($sshUser -match '^[a-z_][a-z0-9_-]{0,31}$') 'SSH user is malformed.'
$expectedFingerprint = [string]$request.host.sshHostKeySha256
Require ($expectedFingerprint -match '^SHA256:[A-Za-z0-9+/]{43}$') 'Request SSH host-key fingerprint is not canonical OpenSSH SHA-256 form.'

$keyFullPath = [IO.Path]::GetFullPath($SshPrivateKeyPath)
Require ([IO.File]::Exists($keyFullPath)) "SSH private key does not exist: $keyFullPath"
$keyItem = Get-Item -LiteralPath $keyFullPath
Require ($null -eq $keyItem.LinkType) 'SSH private key cannot be a symbolic link.'
Require ($keyItem.Length -gt 0 -and $keyItem.Length -le 64KB) 'SSH private key is empty or exceeds 64 KiB.'

$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = [IO.Path]::GetDirectoryName($evidenceFullPath)
Require (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) 'EvidencePath must include a parent directory.'
[IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-host-preflight-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$knownHostsPath = Join-Path $workRoot 'known_hosts'

try {
    $scanOutput = @(& ssh-keyscan -4 -T 10 -p $sshPort -t ed25519 $apiHost 2>$null)
    Require ($LASTEXITCODE -eq 0 -and $scanOutput.Count -gt 0) 'Could not observe the live SSH Ed25519 host key.'
    $hostKeyLines = @($scanOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    Require ($hostKeyLines.Count -eq 1) 'Expected exactly one live SSH Ed25519 host key.'
    Write-Utf8NoBom -Path $knownHostsPath -Content (([string]$hostKeyLines[0]).Trim() + "`n")

    $fingerprintText = Invoke-RequiredNative -Tool 'ssh-keygen' -Arguments @('-E', 'sha256', '-lf', $knownHostsPath) -Context 'SSH host-key fingerprint calculation' -Capture
    $fingerprintMatches = [Text.RegularExpressions.Regex]::Matches($fingerprintText, 'SHA256:[A-Za-z0-9+/]{43}')
    Require ($fingerprintMatches.Count -eq 1) 'Observed SSH host-key fingerprint is malformed or ambiguous.'
    $observedFingerprint = $fingerprintMatches[0].Value
    Require ($observedFingerprint -ceq $expectedFingerprint) 'Observed SSH host key does not match the request-bound fingerprint.'

    $sshOptions = @(
        '-4',
        '-i', $keyFullPath,
        '-p', $sshPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-o', "UserKnownHostsFile=$knownHostsPath",
        '-o', 'StrictHostKeyChecking=yes',
        '-o', 'BatchMode=yes',
        '-o', 'IdentitiesOnly=yes',
        '-o', 'PasswordAuthentication=no',
        '-o', 'KbdInteractiveAuthentication=no',
        '-o', 'ConnectTimeout=10'
    )
    $remoteTarget = "$sshUser@$apiHost"
    $remoteScript = @'
set -euo pipefail

env_path='/etc/steward/backend.env'
release_dir='__RELEASE_DIR__'
plan_dir='__PLAN_DIR__'

[[ "$(uname -s)" == 'Linux' ]] || { echo 'remote host is not Linux' >&2; exit 1; }
[[ "$(uname -m)" == 'x86_64' ]] || { echo 'remote host is not x86_64' >&2; exit 1; }
for tool in bash caddy curl docker pwsh sha256sum ss sudo tar; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "required remote tool is missing: ${tool}" >&2; exit 1; }
done
sudo -n true >/dev/null 2>&1 || { echo 'remote SSH user does not have non-interactive sudo' >&2; exit 1; }
docker version --format '{{.Server.Version}}' >/dev/null 2>&1 || { echo 'remote SSH user cannot access the Docker server' >&2; exit 1; }
[[ -f "${env_path}" && ! -L "${env_path}" ]] || { echo 'protected backend environment is missing, not regular, or is a symlink' >&2; exit 1; }
env_uid="$(stat -Lc '%u' "${env_path}")"
env_mode="$(stat -Lc '%a' "${env_path}")"
env_size="$(stat -Lc '%s' "${env_path}")"
[[ "${env_uid}" == '0' ]] || { echo 'protected backend environment is not root-owned' >&2; exit 1; }
[[ "${env_mode}" == '600' ]] || { echo 'protected backend environment mode is not 0600' >&2; exit 1; }
[[ "${env_size}" =~ ^[0-9]+$ && "${env_size}" -gt 0 && "${env_size}" -le 65536 ]] || { echo 'protected backend environment size is outside the supported bound' >&2; exit 1; }
[[ ! -e "${release_dir}" ]] || { echo 'exact release directory already exists on the live host' >&2; exit 1; }
[[ ! -e "${plan_dir}" ]] || { echo 'exact deployment-plan directory already exists on the live host' >&2; exit 1; }
if docker container inspect steward-backend >/dev/null 2>&1; then
  echo 'steward-backend container already exists before first live deployment' >&2
  exit 1
fi
if ss -ltnH 'sport = :8080' | grep -q .; then
  echo 'backend port 8080 is already listening before first live deployment' >&2
  exit 1
fi

printf 'os=Linux\n'
printf 'architecture=x86_64\n'
printf 'envOwnerUid=%s\n' "${env_uid}"
printf 'envMode=%s\n' "${env_mode}"
printf 'envSize=%s\n' "${env_size}"
printf 'sudoNonInteractive=true\n'
printf 'dockerServerAccessible=true\n'
printf 'backendPortClear=true\n'
printf 'releaseDirectoryAbsent=true\n'
printf 'planDirectoryAbsent=true\n'
printf 'requiredToolsPresent=true\n'
'@
    $remoteScript = $remoteScript.Replace('__RELEASE_DIR__', [string]$request.host.remoteReleaseDirectory)
    $remoteScript = $remoteScript.Replace('__PLAN_DIR__', [string]$request.host.remoteDeploymentPlanDirectory)

    $remoteOutput = @($remoteScript | & ssh @sshOptions $remoteTarget 'bash -s' 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Read-only live-host probe failed with exit code $LASTEXITCODE. $(($remoteOutput -join [Environment]::NewLine).Trim())"
    }
    $remoteValues = @{}
    foreach ($lineObject in $remoteOutput) {
        $line = ([string]$lineObject).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $separator = $line.IndexOf('=')
        Require ($separator -gt 0) "Remote preflight emitted an unexpected line: $line"
        $key = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        Require (-not $remoteValues.ContainsKey($key)) "Remote preflight emitted duplicate key '$key'."
        $remoteValues[$key] = $value
    }
    $expectedRemoteKeys = @(
        'architecture', 'backendPortClear', 'dockerServerAccessible', 'envMode', 'envOwnerUid',
        'envSize', 'os', 'planDirectoryAbsent', 'releaseDirectoryAbsent', 'requiredToolsPresent',
        'sudoNonInteractive'
    ) | Sort-Object
    $actualRemoteKeys = @($remoteValues.Keys | Sort-Object)
    Require ($actualRemoteKeys.Count -eq $expectedRemoteKeys.Count -and @(Compare-Object $actualRemoteKeys $expectedRemoteKeys).Count -eq 0) 'Remote preflight output contains missing or unexpected keys.'
    Require ([string]$remoteValues.os -ceq 'Linux' -and [string]$remoteValues.architecture -ceq 'x86_64') 'Remote platform evidence changed unexpectedly.'
    foreach ($booleanKey in @('backendPortClear', 'dockerServerAccessible', 'planDirectoryAbsent', 'releaseDirectoryAbsent', 'requiredToolsPresent', 'sudoNonInteractive')) {
        Require ([string]$remoteValues[$booleanKey] -ceq 'true') "Remote preflight evidence '$booleanKey' is not true."
    }
    Require ([string]$remoteValues.envOwnerUid -ceq '0') 'Remote environment owner evidence is not root.'
    Require ([string]$remoteValues.envMode -ceq '600') 'Remote environment mode evidence is not 0600.'
    $envSize = 0L
    Require ([int64]::TryParse([string]$remoteValues.envSize, [ref]$envSize) -and $envSize -gt 0 -and $envSize -le 65536) 'Remote environment size evidence is invalid.'

    $requestSha256 = (Get-FileHash -LiteralPath $requestFullPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $evidence = [ordered]@{
        documentType = 'steward.closed-alpha-live-host-preflight'
        schemaVersion = 1
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        requestSha256 = $requestSha256
        releaseVersion = [string]$manifest.version
        releaseCommitSha = [string]$manifest.commitSha
        apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
        apiHost = $apiHost
        resolvedPublicIpv4 = [string]$resolved[0].ToString()
        sshUser = $sshUser
        sshPort = $sshPort
        sshHostKeySha256 = $observedFingerprint
        remoteOs = 'Linux'
        remoteArchitecture = 'x86_64'
        requiredToolsPresent = $true
        sudoNonInteractive = $true
        dockerServerAccessible = $true
        remoteEnvironmentPath = '/etc/steward/backend.env'
        remoteEnvironmentOwnerUid = 0
        remoteEnvironmentMode = '0600'
        remoteEnvironmentByteSize = $envSize
        backendPort8080Clear = $true
        releaseDirectoryAbsent = $true
        deploymentPlanDirectoryAbsent = $true
        candidateTransferred = $false
        deploymentStarted = $false
        secretEnvironmentRead = $false
        publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
        physicalBringHere = [string]$manifest.publishAuthorization.physicalBringHere
        publicationAuthorizationChanged = $false
    }
    $evidenceText = $evidence | ConvertTo-Json -Depth 6
    Require ($evidenceText.Length -gt 0 -and $evidenceText.Length -le 64KB) 'Live-host preflight evidence is empty or exceeds 64 KiB.'
    [IO.File]::WriteAllText($evidenceFullPath, $evidenceText, [Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Live host passed the read-only exact-candidate preflight.'
    Write-Host "  Release: $([string]$manifest.version) ($([string]$manifest.commitSha))"
    Write-Host "  DNS: $apiHost -> $([string]$resolved[0].ToString())"
    Write-Host "  SSH host key: $observedFingerprint"
    Write-Host "  Remote platform: Linux/x86_64"
    Write-Host '  Required tools: present'
    Write-Host '  Protected environment: root / 0600 / contents not read'
    Write-Host '  Candidate transferred: no'
    Write-Host '  Deployment started: no'
    Write-Host '  Publication authorization changed: no'
}
finally {
    if ([IO.Directory]::Exists($workRoot)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
