[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedPublicIpv4,
    [Parameter(Mandatory = $true)]
    [string]$SshHostKeySha256,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$SshUser = 'steward',
    [ValidateRange(1, 65535)]
    [int]$SshPort = 22
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Get-ArtifactByPath([object[]]$Artifacts, [string]$Path) {
    $matches = @($Artifacts | Where-Object { [string]$_.path -ceq $Path })
    Require ($matches.Count -eq 1) "Release artifact '$Path' must appear exactly once."
    return $matches[0]
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

    $parsedAddress = $null
    if ([Net.IPAddress]::TryParse($normalized, [ref]$parsedAddress)) {
        return $true
    }

    return -not $normalized.Contains('.', [StringComparison]::Ordinal)
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

    if ($a -eq 0 -or $a -eq 10 -or $a -eq 127 -or $a -ge 224) {
        return $false
    }
    if ($a -eq 100 -and $b -ge 64 -and $b -le 127) {
        return $false
    }
    if ($a -eq 169 -and $b -eq 254) {
        return $false
    }
    if ($a -eq 172 -and $b -ge 16 -and $b -le 31) {
        return $false
    }
    if ($a -eq 192 -and $b -eq 0 -and ($c -eq 0 -or $c -eq 2)) {
        return $false
    }
    if ($a -eq 192 -and $b -eq 168) {
        return $false
    }
    if ($a -eq 198 -and ($b -eq 18 -or $b -eq 19)) {
        return $false
    }
    if ($a -eq 198 -and $b -eq 51 -and $c -eq 100) {
        return $false
    }
    if ($a -eq 203 -and $b -eq 0 -and $c -eq 113) {
        return $false
    }
    return $true
}

function Require-OpenSshSha256Fingerprint([string]$Fingerprint) {
    Require ($Fingerprint -match '^SHA256:[A-Za-z0-9+/]{43}=?$') 'SSH host key fingerprint must use OpenSSH SHA256:<base64> form.'
    $encoded = $Fingerprint.Substring('SHA256:'.Length)
    while (($encoded.Length % 4) -ne 0) {
        $encoded += '='
    }
    try {
        $bytes = [Convert]::FromBase64String($encoded)
    }
    catch {
        throw 'SSH host key fingerprint contains malformed base64.'
    }
    Require ($bytes.Length -eq 32) 'SSH host key fingerprint must encode exactly 32 SHA-256 bytes.'
}

function Require-ArtifactBinding([object[]]$Artifacts, [string]$BundleRoot, [string]$RelativePath) {
    $entry = Get-ArtifactByPath $Artifacts $RelativePath
    $fullPath = Join-Path $BundleRoot $RelativePath
    Require ([IO.File]::Exists($fullPath)) "Release artifact '$RelativePath' is missing."
    $item = Get-Item -LiteralPath $fullPath
    Require ($null -eq $item.LinkType) "Release artifact '$RelativePath' cannot be a symbolic link."
    Require ($item.Length -eq [int64]$entry.byteSize) "Release artifact '$RelativePath' byte size changed."
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    Require ([string]::Equals($actualHash, [string]$entry.sha256, [StringComparison]::Ordinal)) "Release artifact '$RelativePath' hash changed."
    return [ordered]@{
        path = $RelativePath
        byteSize = [int64]$entry.byteSize
        sha256 = [string]$entry.sha256
    }
}

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'

& $verifierPath -BundleDirectory $bundleRoot

$manifestText = [IO.File]::ReadAllText($manifestPath)
Require ($manifestText.Length -le 4MB) 'Closed-alpha release manifest exceeds the 4 MiB bound.'
$manifest = $manifestText | ConvertFrom-Json
$version = [string]$manifest.version
$commitSha = [string]$manifest.commitSha
$apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
$backendImageId = [string]$manifest.deployment.backendImageId
$backendImageTag = [string]$manifest.deployment.backendImageTag
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($backendImageId -match '^sha256:[0-9a-f]{64}$') 'Release backend image ID is malformed.'
Require (-not [string]::IsNullOrWhiteSpace($backendImageTag)) 'Release backend image tag is missing.'
Require ([string]$manifest.deployment.targetPlatform -ceq 'linux/amd64') 'Live deployment request requires linux/amd64.'
Require ([string]$manifest.deployment.backendRuntimeUser -ceq 'app') 'Live deployment request requires the non-root app runtime user.'
Require ([int]$manifest.deployment.postgresMajorVersion -eq 17) 'Live deployment request requires PostgreSQL 17.'
Require ([string]$manifest.deployment.objectStorageProtocol -ceq 's3-compatible') 'Live deployment request requires S3-compatible object storage.'

$apiUri = $null
Require ([Uri]::TryCreate($apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) 'Release API base URL is malformed.'
Require ([string]::Equals($apiUri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase)) 'Live deployment request requires HTTPS.'
Require ([string]::IsNullOrEmpty($apiUri.UserInfo) -and
    [string]::IsNullOrEmpty($apiUri.Query) -and
    [string]::IsNullOrEmpty($apiUri.Fragment)) 'Release API base URL contains forbidden components.'
Require ($apiUri.AbsolutePath -ceq '/') 'Live API base URL must use the origin root path.'
Require ($apiUri.IsDefaultPort) 'Live API base URL must use default HTTPS port 443.'
$apiHost = $apiUri.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
Require (-not (Test-ReservedDnsHost $apiHost)) 'Live API hostname is reserved, local, an IP literal, or not a FQDN.'
Require ($apiHost.Length -le 253 -and $apiHost -match '^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$') 'Live API hostname is malformed.'

$publicAddress = $null
Require ([Net.IPAddress]::TryParse($ExpectedPublicIpv4, [ref]$publicAddress)) 'ExpectedPublicIpv4 is malformed.'
Require (Test-PublicIpv4 $publicAddress) 'ExpectedPublicIpv4 must be a globally routable IPv4 address, not a private, local, documentation, benchmark, multicast, or reserved address.'
$canonicalPublicIpv4 = $publicAddress.ToString()

Require ($SshUser -match '^[a-z_][a-z0-9_-]{0,31}$') 'SSH user is malformed.'
Require-OpenSshSha256Fingerprint $SshHostKeySha256

$artifacts = @($manifest.artifacts)
$verifierBinding = Require-ArtifactBinding $artifacts $bundleRoot 'verify-closed-alpha-release.ps1'
$deploymentPlannerBinding = Require-ArtifactBinding $artifacts $bundleRoot 'prepare-closed-alpha-deployment.ps1'
$livePlannerBinding = Require-ArtifactBinding $artifacts $bundleRoot 'prepare-closed-alpha-live-deployment.ps1'
$environmentTemplateBinding = Require-ArtifactBinding $artifacts $bundleRoot 'backend/deployment.env.example'
$backendEntries = @($artifacts | Where-Object {
    ([string]$_.path).StartsWith('backend/', [StringComparison]::Ordinal) -and
    ([string]$_.path).EndsWith('.tar', [StringComparison]::OrdinalIgnoreCase)
})
Require ($backendEntries.Count -eq 1) 'Release bundle must contain exactly one backend TAR.'
$backendEntry = $backendEntries[0]
Require ([string]$backendEntry.path -match '^backend/[A-Za-z0-9._-]+\.tar$') 'Backend TAR path is not live-deployment safe.'
Require ([int64]$backendEntry.byteSize -gt 0) 'Backend TAR must be non-empty.'
Require ([string]$backendEntry.sha256 -match '^[0-9A-F]{64}$') 'Backend TAR SHA-256 is malformed.'

$selfPath = [IO.Path]::GetFullPath($PSCommandPath)
$bundledSelfPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'prepare-closed-alpha-live-deployment.ps1'))
Require ([string]::Equals($selfPath, $bundledSelfPath, [StringComparison]::OrdinalIgnoreCase)) 'Run the live deployment request planner from inside the verified release bundle.'

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
Require (-not [string]::IsNullOrWhiteSpace($outputDirectory)) 'OutputPath must include a parent directory.'
$bundlePrefix = $bundleRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Require (-not $outputFullPath.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) 'Live deployment request must be written outside the immutable release bundle.'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$remoteReleaseRoot = '/srv/steward/releases'
$remoteReleaseDirectory = "$remoteReleaseRoot/$commitSha"
$remotePlanDirectory = "/srv/steward/deployment-plans/$commitSha"
$remoteEnvironmentPath = '/etc/steward/backend.env'
$caddyConfigurationPath = '/etc/caddy/Caddyfile'

$request = [ordered]@{
    documentType = 'steward.closed-alpha-live-deployment-request'
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    release = [ordered]@{
        version = $version
        commitSha = $commitSha
        apiBaseUrl = $apiUri.AbsoluteUri
        backendImageId = $backendImageId
        backendImageTag = $backendImageTag
        backendTar = [ordered]@{
            path = [string]$backendEntry.path
            byteSize = [int64]$backendEntry.byteSize
            sha256 = [string]$backendEntry.sha256
        }
    }
    host = [ordered]@{
        topology = 'single-linux-amd64-host'
        apiHost = $apiHost
        expectedPublicIpv4 = $canonicalPublicIpv4
        sshHost = $apiHost
        sshPort = $SshPort
        sshUser = $SshUser
        sshHostKeySha256 = $SshHostKeySha256
        targetPlatform = 'linux/amd64'
        remoteReleaseDirectory = $remoteReleaseDirectory
        remoteDeploymentPlanDirectory = $remotePlanDirectory
        remoteEnvironmentPath = $remoteEnvironmentPath
        caddyConfigurationPath = $caddyConfigurationPath
        requiredTools = @('bash', 'caddy', 'curl', 'docker', 'pwsh', 'sha256sum', 'ss', 'sudo', 'tar')
    }
    network = [ordered]@{
        publicTlsPort = 443
        publicCertificateBootstrapPort = 80
        restrictedAdministrativePort = $SshPort
        forbiddenPublicBackendPort = 8080
        backendPort = 8080
        caddyUpstream = '127.0.0.1:8080'
        backendKnownProxyIp = '127.0.0.1'
        publicBackendPortMustFailClosed = $true
    }
    executionContract = [ordered]@{
        releaseVerifier = $verifierBinding
        deploymentPlanner = $deploymentPlannerBinding
        liveRequestPlanner = $livePlannerBinding
        environmentTemplate = $environmentTemplateBinding
        plannerRunsOnLiveHost = $true
        plannerRequireDeployable = $true
        environmentNeverLeavesLiveHost = $true
        deployScriptName = 'deploy-exact-candidate.sh'
        publicVerifierName = 'verify-public-https.sh'
        exactImageIdRequired = $true
        publicTlsVerificationRequired = $true
        certificateBypassAllowed = $false
    }
    secretBoundary = [ordered]@{
        secretValuesPresent = $false
        secretEnvironmentBundled = $false
        secretEnvironmentTransferredByRequest = $false
        remoteEnvironmentPath = $remoteEnvironmentPath
        requiredRemoteMode = '0600'
        requiredRemoteOwner = 'root'
    }
    publication = [ordered]@{
        physicalBringHere = [string]$manifest.publishAuthorization.physicalBringHere
        publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
        requestAuthorizesPublication = $false
        authorizationChanged = $false
    }
}

$requestText = $request | ConvertTo-Json -Depth 8
Require ($requestText.Length -gt 0 -and $requestText.Length -le 64KB) 'Live deployment request is empty or exceeds 64 KiB.'
[IO.File]::WriteAllText($outputFullPath, $requestText, [Text.UTF8Encoding]::new($false))

Write-Host
Write-Host '[OK] Exact closed-alpha candidate is bound to one fail-closed live-host deployment request.'
Write-Host "  Release: $version ($commitSha)"
Write-Host "  API / SSH host: $apiHost"
Write-Host "  Expected public IPv4: $canonicalPublicIpv4"
Write-Host "  SSH host key: $SshHostKeySha256"
Write-Host "  Backend image ID: $backendImageId"
Write-Host "  Remote environment: $remoteEnvironmentPath (secret values not read or copied)"
Write-Host '  Public backend port 8080: forbidden'
Write-Host '  Publication authorized by this request: no'
