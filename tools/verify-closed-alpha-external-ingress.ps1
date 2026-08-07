[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [Parameter(Mandatory = $true)]
    [string]$IngressActivationEvidencePath,
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MaximumIngressActivationAge = [TimeSpan]::FromMinutes(15)
$MaximumClockSkew = [TimeSpan]::FromMinutes(2)
$ExternalPortProbeAttempts = 2
$ExternalPortProbeTimeout = [TimeSpan]::FromSeconds(3)

function Require([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Require-ExactProperties([object]$Value,[string[]]$Expected,[string]$Context) {
    Require ($null -ne $Value) "$Context is null."
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    Require ($actual.Count -eq $expectedSorted.Count) "$Context has an unexpected property count."
    Require (@(Compare-Object $actual $expectedSorted).Count -eq 0) "$Context contains missing or unexpected properties."
}

function Get-ArtifactByPath([object[]]$Artifacts,[string]$Path) {
    $matches = @($Artifacts | Where-Object { [string]$_.path -ceq $Path })
    Require ($matches.Count -eq 1) "Release artifact '$Path' must appear exactly once."
    return $matches[0]
}

function Require-BindingMatchesArtifact([object]$Binding,[object[]]$Artifacts,[string]$ExpectedPath,[string]$Context) {
    Require-ExactProperties $Binding @('byteSize','path','sha256') $Context
    Require ([string]$Binding.path -ceq $ExpectedPath) "$Context has the wrong path."
    $artifact = Get-ArtifactByPath $Artifacts $ExpectedPath
    Require ([int64]$Binding.byteSize -eq [int64]$artifact.byteSize) "$Context byte size does not match the release manifest."
    Require ([string]$Binding.sha256 -ceq [string]$artifact.sha256) "$Context SHA-256 does not match the release manifest."
}

function Read-BoundedJson([string]$Path,[int64]$MaximumBytes,[string]$Context) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    Require ([IO.File]::Exists($fullPath)) "$Context does not exist: $fullPath"
    $item = Get-Item -LiteralPath $fullPath
    Require ($null -eq $item.LinkType) "$Context cannot be a symbolic link."
    Require ($item.Length -gt 0 -and $item.Length -le $MaximumBytes) "$Context is empty or exceeds its byte bound."
    $text = [IO.File]::ReadAllText($fullPath)
    return [ordered]@{ FullPath=$fullPath; Text=$text; Json=($text | ConvertFrom-Json) }
}

function Test-PublicIpv4([Net.IPAddress]$Address) {
    if ($Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $bytes = $Address.GetAddressBytes()
    if ($bytes.Length -ne 4) { return $false }
    $a=[int]$bytes[0]; $b=[int]$bytes[1]; $c=[int]$bytes[2]
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

function Invoke-ExactProcess([string]$FilePath,[string[]]$Arguments,[string]$Context) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        Require ($process.Start()) "$Context could not start '$FilePath'."
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            $details = @($stderr.Trim(),$stdout.Trim()) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            throw "$Context failed with exit code $($process.ExitCode). $(($details -join [Environment]::NewLine).Trim())"
        }
        return [ordered]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-CurlHealth(
    [string]$CurlPath,
    [string]$ApiHost,
    [string]$ExpectedIpv4,
    [Uri]$Uri,
    [string]$Context,
    [string]$OutputPath) {
    $arguments = @(
        '-q',
        '--silent','--show-error','--fail',
        '--connect-timeout','5','--max-time','15',
        '--proto','=https','--tlsv1.2',
        '--noproxy','*',
        '--resolve',"${ApiHost}:443:${ExpectedIpv4}",
        '--output',$OutputPath,
        '--write-out','%{http_code}\t%{remote_ip}\t%{ssl_verify_result}\t%{scheme}',
        $Uri.AbsoluteUri
    )
    $result = Invoke-ExactProcess $CurlPath $arguments $Context
    Require ([string]::IsNullOrWhiteSpace([string]$result.StdErr)) "$Context emitted unexpected stderr on a successful transfer: $([string]$result.StdErr)."
    $text = ([string]$result.StdOut).Trim()
    $match = [Text.RegularExpressions.Regex]::Match($text,'^(?<status>[0-9]{3})\t(?<remote>[^\t\r\n]+)\t(?<verify>-?[0-9]+)\t(?<scheme>[A-Za-z]+)$')
    Require $match.Success "$Context returned malformed curl evidence: '$text'."
    Require ($match.Groups['status'].Value -ceq '200') "$Context returned HTTP $($match.Groups['status'].Value), expected 200."
    Require ($match.Groups['remote'].Value -ceq $ExpectedIpv4) "$Context connected to '$($match.Groups['remote'].Value)', expected '$ExpectedIpv4'."
    Require ($match.Groups['verify'].Value -ceq '0') "$Context did not pass certificate verification. curl ssl_verify_result=$($match.Groups['verify'].Value)."
    Require ([string]::Equals($match.Groups['scheme'].Value,'https',[StringComparison]::OrdinalIgnoreCase)) "$Context did not use HTTPS."
    $outputItem = Get-Item -LiteralPath $OutputPath
    Require ($null -eq $outputItem.LinkType -and $outputItem.Length -le 1MB) "$Context response body is unsafe or exceeds 1 MiB."
    return [ordered]@{
        statusCode = 200
        remoteIpv4 = $ExpectedIpv4
        sslVerifyResult = 0
        scheme = 'https'
    }
}

function Invoke-ExternalPortProbe([Net.IPAddress]$Address,[int]$Port,[TimeSpan]$Timeout) {
    $client = [Net.Sockets.TcpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    $cts = [Threading.CancellationTokenSource]::new($Timeout)
    try {
        $client.ConnectAsync($Address,$Port,$cts.Token).GetAwaiter().GetResult()
        return 'connected'
    }
    catch [Net.Sockets.SocketException] {
        switch ($_.Exception.SocketErrorCode) {
            ([Net.Sockets.SocketError]::ConnectionRefused) { return 'connection-refused' }
            ([Net.Sockets.SocketError]::TimedOut) { return 'timeout' }
            default { return "socket-error:$($_.Exception.SocketErrorCode)" }
        }
    }
    catch [OperationCanceledException] {
        return 'timeout'
    }
    finally {
        $cts.Dispose()
        $client.Dispose()
    }
}

$curlCommand = Get-Command curl -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
Require ($null -ne $curlCommand) 'Required native curl executable is not available.'
$curlPath = [string]$curlCommand.Source

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'
& $verifierPath -BundleDirectory $bundleRoot

$selfPath = [IO.Path]::GetFullPath($PSCommandPath)
$bundledSelfPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'verify-closed-alpha-external-ingress.ps1'))
Require ([string]::Equals($selfPath,$bundledSelfPath,[StringComparison]::OrdinalIgnoreCase)) 'Run external ingress verification from inside the verified release bundle.'

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$artifacts = @($manifest.artifacts)
$selfArtifact = Get-ArtifactByPath $artifacts 'verify-closed-alpha-external-ingress.ps1'
$selfItem = Get-Item -LiteralPath $selfPath
Require ($null -eq $selfItem.LinkType) 'External ingress verifier cannot be a symbolic link.'
Require ($selfItem.Length -eq [int64]$selfArtifact.byteSize) 'External ingress verifier byte size does not match the release manifest.'
Require ((Get-FileHash -LiteralPath $selfPath -Algorithm SHA256).Hash -ceq [string]$selfArtifact.sha256) 'External ingress verifier SHA-256 does not match the release manifest.'

$version = [string]$manifest.version
$commitSha = [string]$manifest.commitSha
$apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
$imageId = [string]$manifest.deployment.backendImageId
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($imageId -match '^sha256:[0-9a-f]{64}$') 'Release backend image ID is malformed.'
Require ([string]$manifest.deployment.targetPlatform -ceq 'linux/amd64') 'External ingress verification expects the linux/amd64 release topology.'
Require ([string]$manifest.deployment.backendRuntimeUser -ceq 'app') 'External ingress verification requires runtime user app.'
Require (-not [bool]$manifest.publishAuthorization.publishAllowed) 'External ingress verification cannot use a publish-authorized candidate.'
Require ([string]$manifest.publishAuthorization.physicalBringHere -ceq 'deferred') 'External ingress verification expects physical Bring Here to remain deferred.'

$requestFile = Read-BoundedJson $RequestPath 1MB 'Live deployment request'
$request = $requestFile.Json
Require-ExactProperties $request @('createdAtUtc','documentType','executionContract','host','network','publication','release','schemaVersion','secretBoundary') 'Live deployment request'
Require ([string]$request.documentType -ceq 'steward.closed-alpha-live-deployment-request' -and [int]$request.schemaVersion -eq 1) 'Live deployment request identity is invalid.'
Require-ExactProperties $request.release @('apiBaseUrl','backendImageId','backendImageTag','backendTar','commitSha','version') 'Live deployment request release'
Require-ExactProperties $request.host @('apiHost','caddyConfigurationPath','expectedPublicIpv4','remoteDeploymentPlanDirectory','remoteEnvironmentPath','remoteReleaseDirectory','requiredTools','sshHost','sshHostKeySha256','sshPort','sshUser','targetPlatform','topology') 'Live deployment request host'
Require-ExactProperties $request.network @('backendKnownProxyIp','backendPort','caddyUpstream','forbiddenPublicBackendPort','publicBackendPortMustFailClosed','publicCertificateBootstrapPort','publicTlsPort','restrictedAdministrativePort') 'Live deployment request network'
Require-ExactProperties $request.publication @('authorizationChanged','physicalBringHere','publishAllowed','requestAuthorizesPublication') 'Live deployment request publication'
Require-ExactProperties $request.secretBoundary @('remoteEnvironmentPath','requiredRemoteMode','requiredRemoteOwner','secretEnvironmentBundled','secretEnvironmentTransferredByRequest','secretValuesPresent') 'Live deployment request secret boundary'
Require-ExactProperties $request.executionContract @('certificateBypassAllowed','deployScriptName','deploymentPlanner','environmentNeverLeavesLiveHost','environmentTemplate','exactImageIdRequired','liveHostPreflight','liveRequestPlanner','plannerRequireDeployable','plannerRunsOnLiveHost','publicTlsVerificationRequired','publicVerifierName','releaseVerifier') 'Live deployment request execution contract'
Require ([string]$request.release.version -ceq $version -and [string]$request.release.commitSha -ceq $commitSha) 'Request release identity does not match the candidate.'
Require ([string]$request.release.apiBaseUrl -ceq $apiBaseUrl -and [string]$request.release.backendImageId -ceq $imageId -and [string]$request.release.backendImageTag -ceq [string]$manifest.deployment.backendImageTag) 'Request deployment identity does not match the candidate.'
Require-BindingMatchesArtifact $request.executionContract.releaseVerifier $artifacts 'verify-closed-alpha-release.ps1' 'Request release verifier binding'
Require-BindingMatchesArtifact $request.executionContract.deploymentPlanner $artifacts 'prepare-closed-alpha-deployment.ps1' 'Request deployment planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveRequestPlanner $artifacts 'prepare-closed-alpha-live-deployment.ps1' 'Request live request planner binding'
Require-BindingMatchesArtifact $request.executionContract.liveHostPreflight $artifacts 'preflight-closed-alpha-live-host.ps1' 'Request live-host preflight binding'
Require-BindingMatchesArtifact $request.executionContract.environmentTemplate $artifacts 'backend/deployment.env.example' 'Request environment template binding'
Require ([string]$request.host.topology -ceq 'single-linux-amd64-host' -and [string]$request.host.targetPlatform -ceq 'linux/amd64') 'Unsupported live-host topology.'
Require ([int]$request.network.publicTlsPort -eq 443 -and [int]$request.network.backendPort -eq 8080 -and [int]$request.network.forbiddenPublicBackendPort -eq 8080) 'Request public/backend port contract changed.'
Require ([bool]$request.network.publicBackendPortMustFailClosed) 'Request no longer requires backend port 8080 to fail closed externally.'
Require ([string]$request.network.caddyUpstream -ceq '127.0.0.1:8080' -and [string]$request.network.backendKnownProxyIp -ceq '127.0.0.1') 'Request one-proxy contract changed.'
Require (-not [bool]$request.secretBoundary.secretValuesPresent -and -not [bool]$request.secretBoundary.secretEnvironmentBundled -and -not [bool]$request.secretBoundary.secretEnvironmentTransferredByRequest) 'Request crossed the protected environment boundary.'
Require ([string]$request.secretBoundary.remoteEnvironmentPath -ceq '/etc/steward/backend.env' -and [string]$request.secretBoundary.requiredRemoteOwner -ceq 'root' -and [string]$request.secretBoundary.requiredRemoteMode -ceq '0600') 'Request protected environment contract changed.'
Require (-not [bool]$request.publication.publishAllowed -and [string]$request.publication.physicalBringHere -ceq 'deferred' -and -not [bool]$request.publication.authorizationChanged -and -not [bool]$request.publication.requestAuthorizesPublication) 'Request publication state changed.'
Require (-not [bool]$request.executionContract.certificateBypassAllowed -and [bool]$request.executionContract.publicTlsVerificationRequired) 'Request external TLS verification contract changed.'

$apiUri = $null
Require ([Uri]::TryCreate($apiBaseUrl,[UriKind]::Absolute,[ref]$apiUri)) 'Candidate API base URL is malformed.'
Require ([string]$apiUri.Scheme -ceq 'https' -and $apiUri.IsDefaultPort) 'External ingress verification requires the default HTTPS port.'
Require ([string]::IsNullOrWhiteSpace($apiUri.UserInfo) -and [string]::IsNullOrWhiteSpace($apiUri.Query) -and [string]::IsNullOrWhiteSpace($apiUri.Fragment)) 'External ingress verification does not accept API URL user-info, query, or fragment.'
Require ($apiUri.AbsolutePath -ceq '/') 'External ingress verification requires an API base URL rooted at /. '
$apiHost = $apiUri.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
Require ([string]$request.host.apiHost -ceq $apiHost) 'Request API host does not match the candidate API host.'

$requestSha256 = (Get-FileHash -LiteralPath $requestFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()
$ingressFile = Read-BoundedJson $IngressActivationEvidencePath 1MB 'Live ingress activation evidence'
$ingress = $ingressFile.Json
Require-ExactProperties $ingress @(
    'apiBaseUrl','apiHost','backendContainerRunning','backendDeploymentAgeSecondsAtStart','backendDeploymentCompletedAtUtc',
    'backendDeploymentEvidenceSha256','backendDeploymentFresh','backendImageId','backendLocalLive','backendLocalReady',
    'backendPort8080Listening','backendRuntimeUser','caddyActivatedState','caddyBaselineState','caddyConfigInstalled',
    'caddyGeneratedSha256','caddyReloaded','caddyServiceActive','caddyValidated','completedAtUtc','deploymentPlanReverified',
    'deploymentPlanSha256','dnsReverified','documentType','externalPort8080ClosedVerified','physicalBringHere',
    'protectedEnvironmentMetadataPreserved','protectedEnvironmentTransferred','publicationAuthorizationChanged','publicHttpsVerified',
    'publishAllowed','releaseCommitSha','releaseVersion','remoteBundleReverified','remoteDeploymentPlanDirectory',
    'remoteEnvironmentPath','remoteReleaseDirectory','requestSha256','resolvedPublicIpv4','schemaVersion','sshHostKeyReverified',
    'sshHostKeySha256','sshPort','sshUser'
) 'Live ingress activation evidence'
Require ([string]$ingress.documentType -ceq 'steward.closed-alpha-live-ingress-activation' -and [int]$ingress.schemaVersion -eq 1) 'Live ingress activation evidence identity is invalid.'
Require ([string]$ingress.requestSha256 -ceq $requestSha256) 'Ingress activation evidence does not bind the exact live deployment request.'
Require ([string]$ingress.releaseVersion -ceq $version -and [string]$ingress.releaseCommitSha -ceq $commitSha) 'Ingress activation evidence does not bind the exact release.'
Require ([string]$ingress.apiBaseUrl -ceq $apiBaseUrl -and [string]$ingress.apiHost -ceq $apiHost) 'Ingress activation API identity changed.'
Require ([string]$ingress.resolvedPublicIpv4 -ceq [string]$request.host.expectedPublicIpv4) 'Ingress activation DNS address does not match the request.'
Require ([string]$ingress.sshUser -ceq [string]$request.host.sshUser -and [int]$ingress.sshPort -eq [int]$request.host.sshPort -and [string]$ingress.sshHostKeySha256 -ceq [string]$request.host.sshHostKeySha256) 'Ingress activation SSH identity does not match the request.'
Require ([string]$ingress.remoteReleaseDirectory -ceq [string]$request.host.remoteReleaseDirectory -and [string]$ingress.remoteDeploymentPlanDirectory -ceq [string]$request.host.remoteDeploymentPlanDirectory -and [string]$ingress.remoteEnvironmentPath -ceq [string]$request.host.remoteEnvironmentPath) 'Ingress activation remote paths changed.'
Require ([bool]$ingress.backendDeploymentFresh -and [bool]$ingress.dnsReverified -and [bool]$ingress.sshHostKeyReverified) 'Ingress activation evidence did not prove fresh host identity.'
Require ([bool]$ingress.remoteBundleReverified -and [bool]$ingress.deploymentPlanReverified -and [string]$ingress.deploymentPlanSha256 -match '^[0-9A-F]{64}$') 'Ingress activation evidence did not preserve exact staged bytes.'
Require ([string]$ingress.backendImageId -ceq $imageId -and [string]$ingress.backendRuntimeUser -ceq 'app' -and [bool]$ingress.backendContainerRunning) 'Ingress activation evidence lost exact backend runtime identity.'
Require ([bool]$ingress.backendLocalLive -and [bool]$ingress.backendLocalReady -and [bool]$ingress.backendPort8080Listening) 'Ingress activation evidence did not preserve local backend readiness.'
Require ([bool]$ingress.caddyConfigInstalled -and [bool]$ingress.caddyValidated -and [bool]$ingress.caddyReloaded -and [bool]$ingress.caddyServiceActive) 'Ingress activation evidence did not prove managed Caddy activation.'
Require ([string]$ingress.caddyGeneratedSha256 -match '^[0-9A-F]{64}$' -and [string]$ingress.caddyActivatedState -ceq "sha256:$($ingress.caddyGeneratedSha256)") 'Ingress activation Caddy binding is inconsistent.'
Require ([bool]$ingress.protectedEnvironmentMetadataPreserved -and -not [bool]$ingress.protectedEnvironmentTransferred) 'Ingress activation evidence crossed the protected environment boundary.'
Require (-not [bool]$ingress.publicHttpsVerified -and -not [bool]$ingress.externalPort8080ClosedVerified) 'Ingress activation evidence already claims external acceptance.'
Require (-not [bool]$ingress.publishAllowed -and [string]$ingress.physicalBringHere -ceq 'deferred' -and -not [bool]$ingress.publicationAuthorizationChanged) 'Ingress activation evidence changed publication state.'

$ingressCompletedAt = [DateTimeOffset]::MinValue
Require ([DateTimeOffset]::TryParse([string]$ingress.completedAtUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$ingressCompletedAt)) 'Ingress activation completedAtUtc is malformed.'
$now = [DateTimeOffset]::UtcNow
$ingressAge = $now - $ingressCompletedAt.ToUniversalTime()
Require ($ingressAge -ge $MaximumClockSkew.Negate()) 'Ingress activation evidence timestamp is too far in the future.'
Require ($ingressAge -le $MaximumIngressActivationAge) 'Live ingress activation evidence is stale; reactivate the exact ingress before external acceptance.'
$ingressAgeSeconds = [Math]::Max(0,[int][Math]::Floor($ingressAge.TotalSeconds))
$ingressEvidenceSha256 = (Get-FileHash -LiteralPath $ingressFile.FullPath -Algorithm SHA256).Hash.ToUpperInvariant()

$expectedAddress = $null
Require ([Net.IPAddress]::TryParse([string]$request.host.expectedPublicIpv4,[ref]$expectedAddress)) 'Request expected public IPv4 is malformed.'
Require (Test-PublicIpv4 $expectedAddress) 'Request expected IPv4 is not globally routable.'
$resolved = @([Net.Dns]::GetHostAddresses($apiHost) | Sort-Object -Property IPAddressToString -Unique)
Require ($resolved.Count -eq 1 -and $resolved[0].AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) 'External observer requires the API hostname to resolve to exactly one IPv4 address.'
Require ([string]$resolved[0].ToString() -ceq [string]$expectedAddress.ToString()) 'External observer DNS does not match the bound public IPv4.'

$environmentNames = @(
    'CURL_CA_BUNDLE','CURL_HOME','SSL_CERT_FILE','SSL_CERT_DIR',
    'HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY',
    'http_proxy','https_proxy','all_proxy','no_proxy'
)
$environmentBackup = @{}
foreach ($name in $environmentNames) {
    $environmentBackup[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name,$null)
}

$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = [IO.Path]::GetDirectoryName($evidenceFullPath)
Require (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) 'EvidencePath must include a parent directory.'
[IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-external-ingress-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

try {
    $liveResult = Invoke-CurlHealth $curlPath $apiHost ([string]$expectedAddress) ([Uri]::new($apiUri,'health/live')) 'Public HTTPS liveness verification' (Join-Path $workRoot 'live.body')
    $readyResult = Invoke-CurlHealth $curlPath $apiHost ([string]$expectedAddress) ([Uri]::new($apiUri,'health/ready')) 'Public HTTPS readiness verification' (Join-Path $workRoot 'ready.body')

    $portOutcomes = @()
    for ($attempt=1;$attempt -le $ExternalPortProbeAttempts;$attempt++) {
        $outcome = Invoke-ExternalPortProbe $expectedAddress 8080 $ExternalPortProbeTimeout
        Require ($outcome -cne 'connected') 'Backend port 8080 is externally reachable; external acceptance fails closed.'
        Require ($outcome -in @('connection-refused','timeout')) "Backend port 8080 probe was indeterminate: $outcome"
        $portOutcomes += $outcome
    }
    $distinctOutcomes = @($portOutcomes | Sort-Object -Unique)
    $portOutcome = if ($distinctOutcomes.Count -eq 1) { [string]$distinctOutcomes[0] } else { 'mixed-refused-timeout' }

    $evidence = [ordered]@{
        documentType = 'steward.closed-alpha-external-ingress-acceptance'
        schemaVersion = 1
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        ingressActivationCompletedAtUtc = $ingressCompletedAt.ToUniversalTime().ToString('O')
        ingressActivationAgeSecondsAtStart = $ingressAgeSeconds
        requestSha256 = $requestSha256
        ingressActivationEvidenceSha256 = $ingressEvidenceSha256
        releaseVersion = $version
        releaseCommitSha = $commitSha
        apiBaseUrl = $apiBaseUrl
        apiHost = $apiHost
        expectedPublicIpv4 = [string]$expectedAddress
        resolvedPublicIpv4 = [string]$resolved[0].ToString()
        dnsVerified = $true
        observerUsedSsh = $false
        hostMutationPerformed = $false
        protectedEnvironmentAccessed = $false
        directHttpsToBoundIpv4 = $true
        certificateValidationMode = 'system-trust-default-no-bypass'
        curlConfigurationFileDisabled = $true
        proxyEnvironmentIgnored = $true
        customCaEnvironmentIgnored = $true
        publicHttpsLiveStatusCode = [int]$liveResult.statusCode
        publicHttpsReadyStatusCode = [int]$readyResult.statusCode
        publicHttpsRemoteIpv4 = [string]$liveResult.remoteIpv4
        tlsVerifyResult = [int]$liveResult.sslVerifyResult
        publicHttpsVerified = $true
        externalPort8080ProbeAttempts = $ExternalPortProbeAttempts
        externalPort8080ProbeOutcome = $portOutcome
        externalPort8080ClosedVerified = $true
        publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
        physicalBringHere = [string]$manifest.publishAuthorization.physicalBringHere
        publicationAuthorizationChanged = $false
    }
    $evidenceText = $evidence | ConvertTo-Json -Depth 5
    Require ($evidenceText.Length -gt 0 -and $evidenceText.Length -le 64KB) 'External ingress acceptance evidence is empty or exceeds 64 KiB.'
    [IO.File]::WriteAllText($evidenceFullPath,$evidenceText,[Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] External ingress acceptance passed without SSH or host mutation.'
    Write-Host "  Release: $version ($commitSha)"
    Write-Host "  HTTPS: $apiBaseUrl"
    Write-Host "  Bound IPv4: $expectedAddress"
    Write-Host '  Certificate validation: system trust, no bypass'
    Write-Host '  Public liveness/readiness: HTTP 200'
    Write-Host "  External port 8080: $portOutcome"
    Write-Host '  Publication authorization changed: no'
}
finally {
    foreach ($name in $environmentNames) {
        $originalValue = $environmentBackup[$name]
        if ($null -eq $originalValue) {
            [Environment]::SetEnvironmentVariable($name,$null)
        }
        else {
            [Environment]::SetEnvironmentVariable($name,[string]$originalValue)
        }
    }
    if ([IO.Directory]::Exists($workRoot)) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
