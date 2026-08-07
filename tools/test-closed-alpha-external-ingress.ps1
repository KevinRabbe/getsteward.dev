[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }

function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Require-Tool([string]$Name) { Require ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue)) "Required external-test tool '$Name' is not available." }
function Write-Utf8([string]$Path,[string]$Content) {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText($Path,$Content.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
}
function Invoke-Native([string]$Tool,[string[]]$Arguments,[string]$Context,[switch]$Capture) {
    $output = @(& $Tool @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "$Context failed with exit code $LASTEXITCODE. $(($output -join [Environment]::NewLine).Trim())" }
    if ($Capture.IsPresent) { return ($output -join [Environment]::NewLine).Trim() }
    foreach ($line in $output) { Write-Host $line }
}
function Expect-Failure([scriptblock]$Action,[string]$Context,[string]$ExpectedMessage) {
    $failed = $false
    try { & $Action } catch {
        $failed = $true
        $message = [string]$_.Exception.Message
        Require ($message.Contains($ExpectedMessage,[StringComparison]::Ordinal)) "${Context} failed for the wrong reason: $message"
        Write-Host "[EXPECTED] ${Context}: $message"
    }
    Require $failed "Expected failure did not occur: $Context"
}
function Invoke-Observer([string[]]$PrefixArguments,[string]$ToolPath,[string]$Bundle,[string]$Request,[string]$Ingress,[string]$Evidence) {
    $arguments = @($PrefixArguments + @(
        'pwsh','-NoLogo','-NoProfile','-File',$ToolPath,
        '-BundleDirectory',$Bundle,
        '-RequestPath',$Request,
        '-IngressActivationEvidencePath',$Ingress,
        '-EvidencePath',$Evidence
    ))
    $output = @(& sudo @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "External observer command failed with exit code $LASTEXITCODE. $(($output -join [Environment]::NewLine).Trim())"
    }
    foreach ($line in $output) { Write-Host $line }
}

foreach ($tool in @('caddy','curl','ip','python3','sudo','update-ca-certificates')) { Require-Tool $tool }

$suffix = [Guid]::NewGuid().ToString('N').Substring(0,10)
$work = Join-Path ([IO.Path]::GetTempPath()) "steward-external-ingress-test-$suffix"
[IO.Directory]::CreateDirectory($work) | Out-Null
$bundle = Join-Path $work 'bundle'
$backendDirectory = Join-Path $bundle 'backend'
$desktopDirectory = Join-Path $bundle 'desktop'
[IO.Directory]::CreateDirectory($backendDirectory) | Out-Null
[IO.Directory]::CreateDirectory($desktopDirectory) | Out-Null

$namespace = "steward-ext-$suffix"
$vethHost = "seh$suffix"
$vethObserver = "seo$suffix"
$hostIpv4 = '93.184.216.34'
$observerIpv4 = '93.184.216.33'
$apiHost = 'alpha-external.getsteward.dev'
$apiBaseUrl = "https://$apiHost/"
$releaseVersion = '2.0.0-external-ingress'
$releaseCommit = '5555555555555555555555555555555555555555'
$imageId = 'sha256:' + ('5' * 64)
$imageTag = 'steward-backend:external-ingress-test'
$requestPath = Join-Path $work 'live-request.json'
$ingressPath = Join-Path $work 'ingress-activation.json'
$staleIngressPath = Join-Path $work 'stale-ingress-activation.json'
$acceptancePath = Join-Path $work 'external-acceptance.json'
$untrustedEvidence = Join-Path $work 'untrusted-evidence.json'
$openPortEvidence = Join-Path $work 'open-port-evidence.json'
$hostsBackup = Join-Path $work 'hosts.backup'
$caddyConfig = Join-Path $work 'Caddyfile'
$caddyData = Join-Path $work 'caddy-data'
$caddyConfigHome = Join-Path $work 'caddy-config'
$caddyLog = Join-Path $work 'caddy.log'
$caddyPidFile = Join-Path $work 'caddy.pid'
$backendScript = Join-Path $work 'backend.py'
$externalScript = Join-Path $work 'external-8080.py'
$caInstallPath = '/usr/local/share/ca-certificates/steward-external-ingress-test.crt'
$backendProcess = $null
$externalProcess = $null
$caddyPid = 0
$namespaceCreated = $false
$hostsModified = $false
$caInstalled = $false

try {
    foreach ($name in @(
        'verify-closed-alpha-release.ps1',
        'prepare-closed-alpha-deployment.ps1',
        'prepare-closed-alpha-live-deployment.ps1',
        'preflight-closed-alpha-live-host.ps1',
        'stage-closed-alpha-live-deployment-plan.ps1',
        'deploy-closed-alpha-live-backend.ps1',
        'activate-closed-alpha-live-ingress.ps1',
        'verify-closed-alpha-external-ingress.ps1'
    )) { Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $bundle $name) }

    Write-Utf8 (Join-Path $desktopDirectory 'friends.zip') 'synthetic desktop bytes'
    Write-Utf8 (Join-Path $backendDirectory 'deployment.env.example') 'PORT=8080'
    Write-Utf8 (Join-Path $backendDirectory 'steward-backend.tar') 'synthetic backend tar bytes'
    Write-Utf8 (Join-Path $bundle 'CLOSED-ALPHA-OPERATOR-RUNBOOK.txt') 'synthetic operator runbook'
    Write-Utf8 (Join-Path $bundle 'RELEASE-STATUS.txt') 'synthetic release status'

    $artifactEntries = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/')
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    })
    $manifest = [ordered]@{
        documentType = 'steward.closed-alpha-release-candidate'
        schemaVersion = 1
        channel = 'closed-alpha'
        version = $releaseVersion
        commitSha = $releaseCommit
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        deployment = [ordered]@{
            apiBaseUrl = $apiBaseUrl
            authenticationMode = 'friends-build'
            backendImageId = $imageId
            backendImageTag = $imageTag
            backendRuntimeUser = 'app'
            objectStorageProtocol = 's3-compatible'
            postgresMajorVersion = 17
            targetPlatform = 'linux/amd64'
        }
        publishAuthorization = [ordered]@{
            evidencePath = $null
            evidenceSha256 = $null
            physicalBringHere = 'deferred'
            publishAllowed = $false
            reason = 'Synthetic external ingress rehearsal; physical Bring Here remains deferred.'
        }
        artifacts = $artifactEntries
    }
    Write-Utf8 (Join-Path $bundle 'release-manifest.json') ($manifest | ConvertTo-Json -Depth 8)
    & (Join-Path $bundle 'verify-closed-alpha-release.ps1') -BundleDirectory $bundle

    $fingerprintBytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($fingerprintBytes)
    $hostFingerprint = 'SHA256:' + [Convert]::ToBase64String($fingerprintBytes).TrimEnd('=')
    & (Join-Path $bundle 'prepare-closed-alpha-live-deployment.ps1') `
        -BundleDirectory $bundle `
        -ExpectedPublicIpv4 $hostIpv4 `
        -SshHostKeySha256 $hostFingerprint `
        -OutputPath $requestPath

    $requestSha = (Get-FileHash -LiteralPath $requestPath -Algorithm SHA256).Hash.ToUpperInvariant()

    Write-Utf8 $caddyConfig @"
{
    admin off
    auto_https disable_redirects
    skip_install_trust
}
$apiHost {
    tls internal
    reverse_proxy 127.0.0.1:8080
}
"@
    $caddyHash = (Get-FileHash -LiteralPath $caddyConfig -Algorithm SHA256).Hash.ToUpperInvariant()
    $ingress = [ordered]@{
        documentType = 'steward.closed-alpha-live-ingress-activation'
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        backendDeploymentCompletedAtUtc = [DateTimeOffset]::UtcNow.AddSeconds(-10).ToString('O')
        backendDeploymentAgeSecondsAtStart = 10
        requestSha256 = $requestSha
        backendDeploymentEvidenceSha256 = ('A' * 64)
        releaseVersion = $releaseVersion
        releaseCommitSha = $releaseCommit
        apiBaseUrl = $apiBaseUrl
        apiHost = $apiHost
        resolvedPublicIpv4 = $hostIpv4
        sshUser = 'steward'
        sshPort = 22
        sshHostKeySha256 = $hostFingerprint
        backendDeploymentFresh = $true
        dnsReverified = $true
        sshHostKeyReverified = $true
        remoteReleaseDirectory = "/srv/steward/releases/$releaseCommit"
        remoteDeploymentPlanDirectory = "/srv/steward/deployment-plans/$releaseCommit"
        remoteEnvironmentPath = '/etc/steward/backend.env'
        remoteBundleReverified = $true
        deploymentPlanReverified = $true
        deploymentPlanSha256 = ('B' * 64)
        backendImageId = $imageId
        backendRuntimeUser = 'app'
        backendContainerRunning = $true
        backendLocalLive = $true
        backendLocalReady = $true
        backendPort8080Listening = $true
        protectedEnvironmentMetadataPreserved = $true
        protectedEnvironmentTransferred = $false
        caddyBaselineState = 'sha256:' + ('C' * 64)
        caddyActivatedState = "sha256:$caddyHash"
        caddyGeneratedSha256 = $caddyHash
        caddyConfigInstalled = $true
        caddyValidated = $true
        caddyReloaded = $true
        caddyServiceActive = $true
        publicHttpsVerified = $false
        externalPort8080ClosedVerified = $false
        publishAllowed = $false
        physicalBringHere = 'deferred'
        publicationAuthorizationChanged = $false
    }
    Write-Utf8 $ingressPath ($ingress | ConvertTo-Json -Depth 7)

    $staleIngress = [IO.File]::ReadAllText($ingressPath) | ConvertFrom-Json
    $staleIngress.completedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-20).ToString('O')
    Write-Utf8 $staleIngressPath ($staleIngress | ConvertTo-Json -Depth 7)
    Expect-Failure {
        & (Join-Path $bundle 'verify-closed-alpha-external-ingress.ps1') `
            -BundleDirectory $bundle `
            -RequestPath $requestPath `
            -IngressActivationEvidencePath $staleIngressPath `
            -EvidencePath (Join-Path $work 'stale-evidence.json')
    } 'stale ingress activation evidence is rejected' 'Live ingress activation evidence is stale; reactivate the exact ingress before external acceptance.'

    Copy-Item '/etc/hosts' $hostsBackup
    $hostLine = Join-Path $work 'hosts.line'
    Write-Utf8 $hostLine ("$hostIpv4 $apiHost`n")
    Invoke-Native sudo @('sh','-c',"cat '$hostLine' >> /etc/hosts") 'Install external-observer hosts binding'
    $hostsModified = $true

    Invoke-Native sudo @('ip','netns','add',$namespace) 'Create external observer network namespace'
    $namespaceCreated = $true
    Invoke-Native sudo @('ip','link','add',$vethHost,'type','veth','peer','name',$vethObserver) 'Create observer veth pair'
    Invoke-Native sudo @('ip','link','set',$vethObserver,'netns',$namespace) 'Move observer veth into namespace'
    Invoke-Native sudo @('ip','address','add',"$hostIpv4/30",'dev',$vethHost) 'Assign host-side public IPv4'
    Invoke-Native sudo @('ip','link','set',$vethHost,'up') 'Enable host-side veth'
    Invoke-Native sudo @('ip','netns','exec',$namespace,'ip','address','add',"$observerIpv4/30",'dev',$vethObserver) 'Assign observer-side IPv4'
    Invoke-Native sudo @('ip','netns','exec',$namespace,'ip','link','set','lo','up') 'Enable observer loopback'
    Invoke-Native sudo @('ip','netns','exec',$namespace,'ip','link','set',$vethObserver,'up') 'Enable observer veth'

    Write-Utf8 $backendScript @'
from http.server import BaseHTTPRequestHandler, HTTPServer
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path in ('/health/live', '/health/ready'):
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"status":"ok"}')
        else:
            self.send_response(404)
            self.end_headers()
    def log_message(self, format, *args):
        return
HTTPServer(('127.0.0.1', 8080), Handler).serve_forever()
'@
    $backendProcess = Start-Process python3 -ArgumentList @($backendScript) -PassThru -RedirectStandardOutput (Join-Path $work 'backend.out') -RedirectStandardError (Join-Path $work 'backend.err')
    $backendReady = $false
    for ($attempt=1;$attempt -le 30;$attempt++) {
        & curl --silent --fail --max-time 1 http://127.0.0.1:8080/health/ready *> $null
        if ($LASTEXITCODE -eq 0) { $backendReady = $true; break }
        Start-Sleep -Milliseconds 250
    }
    Require $backendReady 'Loopback-only backend fixture did not become ready.'

    [IO.Directory]::CreateDirectory($caddyData) | Out-Null
    [IO.Directory]::CreateDirectory($caddyConfigHome) | Out-Null
    $startCaddy = "XDG_DATA_HOME='$caddyData' XDG_CONFIG_HOME='$caddyConfigHome' caddy run --config '$caddyConfig' --adapter caddyfile > '$caddyLog' 2>&1 & echo `$! > '$caddyPidFile'"
    Invoke-Native sudo @('sh','-c',$startCaddy) 'Start synthetic HTTPS ingress'
    $caddyPidText = ''
    for ($attempt=1;$attempt -le 20;$attempt++) {
        if ([IO.File]::Exists($caddyPidFile)) {
            $caddyPidText = [IO.File]::ReadAllText($caddyPidFile).Trim()
            if ([int]::TryParse($caddyPidText,[ref]$caddyPid) -and $caddyPid -gt 1) { break }
        }
        Start-Sleep -Milliseconds 250
    }
    Require ($caddyPid -gt 1) 'Synthetic Caddy process did not expose a PID.'

    $rootCertificate = Join-Path $caddyData 'caddy/pki/authorities/local/root.crt'
    $rootReady = $false
    for ($attempt=1;$attempt -le 60;$attempt++) {
        if ([IO.File]::Exists($rootCertificate)) { $rootReady = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $rootReady) {
        $log = if ([IO.File]::Exists($caddyLog)) { [IO.File]::ReadAllText($caddyLog) } else { '' }
        throw "Synthetic Caddy internal root certificate was not created. $log"
    }

    $observerTool = Join-Path $bundle 'verify-closed-alpha-external-ingress.ps1'
    $observerPrefix = @('ip','netns','exec',$namespace)
    Expect-Failure {
        Invoke-Observer @($observerPrefix + @('env',"CURL_CA_BUNDLE=$rootCertificate","SSL_CERT_FILE=$rootCertificate",'HTTPS_PROXY=http://127.0.0.1:9')) $observerTool $bundle $requestPath $ingressPath $untrustedEvidence
    } 'custom-CA/proxy environment cannot bypass system trust' 'Public HTTPS liveness verification failed'

    Invoke-Native sudo @('install','-m','0644','-o','root','-g','root',$rootCertificate,$caInstallPath) 'Install synthetic CA into system trust store'
    $caInstalled = $true
    Invoke-Native sudo @('update-ca-certificates') 'Refresh system trust store'

    Write-Utf8 $externalScript @'
from http.server import BaseHTTPRequestHandler, HTTPServer
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
    def log_message(self, format, *args):
        return
HTTPServer(('93.184.216.34', 8080), Handler).serve_forever()
'@
    $externalProcess = Start-Process python3 -ArgumentList @($externalScript) -PassThru -RedirectStandardOutput (Join-Path $work 'external.out') -RedirectStandardError (Join-Path $work 'external.err')
    Start-Sleep -Milliseconds 500
    Require (-not $externalProcess.HasExited) 'Externally reachable 8080 fixture failed to start.'
    Expect-Failure {
        Invoke-Observer $observerPrefix $observerTool $bundle $requestPath $ingressPath $openPortEvidence
    } 'externally reachable backend port is rejected' 'Backend port 8080 is externally reachable; external acceptance fails closed.'
    Stop-Process -Id $externalProcess.Id -Force -ErrorAction SilentlyContinue
    $externalProcess.WaitForExit(5000) | Out-Null
    $externalProcess = $null

    Invoke-Observer $observerPrefix $observerTool $bundle $requestPath $ingressPath $acceptancePath
    Require ([IO.File]::Exists($acceptancePath)) 'External ingress verifier did not write evidence.'
    $acceptanceText = [IO.File]::ReadAllText($acceptancePath)
    Require ($acceptanceText.Length -gt 0 -and $acceptanceText.Length -le 64KB) 'External acceptance evidence is empty or too large.'
    $acceptance = $acceptanceText | ConvertFrom-Json
    Require ([string]$acceptance.documentType -ceq 'steward.closed-alpha-external-ingress-acceptance' -and [int]$acceptance.schemaVersion -eq 1) 'External acceptance evidence identity is invalid.'
    Require ([string]$acceptance.releaseVersion -ceq $releaseVersion -and [string]$acceptance.releaseCommitSha -ceq $releaseCommit) 'External acceptance lost exact release identity.'
    Require ([string]$acceptance.apiBaseUrl -ceq $apiBaseUrl -and [string]$acceptance.expectedPublicIpv4 -ceq $hostIpv4 -and [string]$acceptance.resolvedPublicIpv4 -ceq $hostIpv4) 'External acceptance lost exact public endpoint identity.'
    Require ([bool]$acceptance.dnsVerified -and [bool]$acceptance.directHttpsToBoundIpv4) 'External acceptance did not bind DNS and HTTPS to the same IPv4.'
    Require (-not [bool]$acceptance.observerUsedSsh -and -not [bool]$acceptance.hostMutationPerformed -and -not [bool]$acceptance.protectedEnvironmentAccessed) 'External observer crossed the read-only boundary.'
    Require ([string]$acceptance.certificateValidationMode -ceq 'system-trust-default-no-bypass' -and [int]$acceptance.tlsVerifyResult -eq 0) 'External acceptance did not use normal system-trust validation.'
    Require ([bool]$acceptance.curlConfigurationFileDisabled -and [bool]$acceptance.proxyEnvironmentIgnored -and [bool]$acceptance.customCaEnvironmentIgnored) 'External acceptance left curl trust/routing overrides enabled.'
    Require ([int]$acceptance.publicHttpsLiveStatusCode -eq 200 -and [int]$acceptance.publicHttpsReadyStatusCode -eq 200 -and [bool]$acceptance.publicHttpsVerified) 'External public HTTPS health did not pass.'
    Require ([int]$acceptance.externalPort8080ProbeAttempts -eq 2 -and [string]$acceptance.externalPort8080ProbeOutcome -in @('connection-refused','timeout','mixed-refused-timeout') -and [bool]$acceptance.externalPort8080ClosedVerified) 'External port-8080 fail-closed evidence is invalid.'
    Require (-not [bool]$acceptance.publishAllowed -and [string]$acceptance.physicalBringHere -ceq 'deferred' -and -not [bool]$acceptance.publicationAuthorizationChanged) 'External acceptance changed publication state.'

    Write-Host '[OK] Separate-network external HTTPS trust and fail-closed backend-port acceptance passed.'
}
finally {
    if ($externalProcess -and -not $externalProcess.HasExited) { Stop-Process -Id $externalProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($backendProcess -and -not $backendProcess.HasExited) { Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($caddyPid -gt 1) { & sudo kill $caddyPid *> $null }
    if ($caInstalled) {
        & sudo rm -f $caInstallPath *> $null
        & sudo update-ca-certificates *> $null
    }
    if ($namespaceCreated) { & sudo ip netns del $namespace *> $null }
    if ($hostsModified -and [IO.File]::Exists($hostsBackup)) { & sudo cp $hostsBackup /etc/hosts *> $null }
    if ([IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
