[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }

function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Require-Tool([string]$Name) { Require ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue)) "Required ingress-test tool '$Name' is not available." }
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

foreach ($tool in @('caddy','curl','docker','ip','ssh-keygen','ssh-keyscan','sudo','systemctl')) { Require-Tool $tool }
Require ([IO.File]::Exists('/usr/sbin/sshd')) 'OpenSSH server is not installed at /usr/sbin/sshd.'

$work = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-ingress-test-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
$bundle = Join-Path $work 'bundle'
$backendDirectory = Join-Path $bundle 'backend'
$desktopDirectory = Join-Path $bundle 'desktop'
[IO.Directory]::CreateDirectory($backendDirectory) | Out-Null
[IO.Directory]::CreateDirectory($desktopDirectory) | Out-Null

$clientKey = Join-Path $work 'client_ed25519'
$hostKey = Join-Path $work 'host_ed25519'
$sshdConfig = Join-Path $work 'sshd_config'
$sshdLog = Join-Path $work 'sshd.log'
$sshdPid = Join-Path $work 'sshd.pid'
$hostsBackup = Join-Path $work 'hosts.backup'
$caddyOriginalBackup = Join-Path $work 'Caddyfile.original'
$baselineSource = Join-Path $work 'Caddyfile.baseline'
$driftSource = Join-Path $work 'Caddyfile.drift'
$requestPath = Join-Path $work 'live-request.json'
$preflightPath = Join-Path $work 'preflight.json'
$stagingPath = Join-Path $work 'staging.json'
$deploymentPath = Join-Path $work 'backend-deployment.json'
$activationPath = Join-Path $work 'ingress-activation.json'
$staleBackendPath = Join-Path $work 'stale-backend-deployment.json'

$apiHost = 'alpha-ingress.getsteward.dev'
$publicIpv4 = '93.184.216.34'
$sshPort = 32224
$releaseCommit = '4444444444444444444444444444444444444444'
$releaseVersion = '2.0.0-live-ingress'
$imageTag = 'steward-backend:live-ingress-test'
$remoteReleaseDirectory = "/srv/steward/releases/$releaseCommit"
$remotePlanDirectory = "/srv/steward/deployment-plans/$releaseCommit"
$imageId = $null
$aliasAdded = $false
$hostModified = $false
$userCreated = $false
$sshdStarted = $false
$caddyOriginalExists = [IO.File]::Exists('/etc/caddy/Caddyfile')
$caddyServiceWasActive = $false

& systemctl is-active --quiet caddy *> $null
$caddyServiceWasActive = ($LASTEXITCODE -eq 0)
if ($caddyOriginalExists) { Copy-Item '/etc/caddy/Caddyfile' $caddyOriginalBackup }

try {
    $imageBuild = Join-Path $work 'health-image'
    [IO.Directory]::CreateDirectory($imageBuild) | Out-Null
    Write-Utf8 (Join-Path $imageBuild 'server.py') @'
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
HTTPServer(('0.0.0.0', 8080), Handler).serve_forever()
'@
    Write-Utf8 (Join-Path $imageBuild 'Dockerfile') @'
FROM python:3.12-alpine
RUN addgroup -S app && adduser -S -G app app
WORKDIR /app
COPY server.py /app/server.py
USER app
CMD ["python", "/app/server.py"]
'@
    Invoke-Native docker @('build','--platform','linux/amd64','--tag',$imageTag,$imageBuild) 'Build disposable non-root health image'
    $imageId = (Invoke-Native docker @('image','inspect',$imageTag,'--format','{{.Id}}') 'Inspect disposable health image' -Capture).Trim()
    Require ($imageId -match '^sha256:[0-9a-f]{64}$') 'Disposable health image ID is malformed.'
    Require ((Invoke-Native docker @('image','inspect',$imageId,'--format','{{.Config.User}}') 'Inspect disposable health image user' -Capture).Trim() -ceq 'app') 'Disposable health image runtime user is not app.'
    $backendTar = Join-Path $backendDirectory 'steward-backend.tar'
    Invoke-Native docker @('save','--output',$backendTar,$imageTag) 'Save disposable health image'
    Invoke-Native docker @('image','rm','--force',$imageTag) 'Remove preloaded disposable health image tag'
    & docker image inspect $imageId *> $null
    Require ($LASTEXITCODE -ne 0) 'Disposable health image still exists before preflight.'

    foreach ($name in @(
        'verify-closed-alpha-release.ps1',
        'prepare-closed-alpha-deployment.ps1',
        'prepare-closed-alpha-live-deployment.ps1',
        'preflight-closed-alpha-live-host.ps1',
        'stage-closed-alpha-live-deployment-plan.ps1',
        'deploy-closed-alpha-live-backend.ps1',
        'activate-closed-alpha-live-ingress.ps1'
    )) { Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $bundle $name) }
    Write-Utf8 (Join-Path $desktopDirectory 'friends.zip') 'synthetic desktop bytes'
    Write-Utf8 (Join-Path $backendDirectory 'deployment.env.example') 'PORT=8080'
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
            apiBaseUrl = "https://$apiHost/"
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
            reason = 'Synthetic ingress activation rehearsal; physical Bring Here remains deferred.'
        }
        artifacts = $artifactEntries
    }
    Write-Utf8 (Join-Path $bundle 'release-manifest.json') ($manifest | ConvertTo-Json -Depth 8)
    & (Join-Path $bundle 'verify-closed-alpha-release.ps1') -BundleDirectory $bundle

    Invoke-Native ssh-keygen @('-q','-t','ed25519','-N','','-f',$clientKey) 'Client key generation'
    Invoke-Native ssh-keygen @('-q','-t','ed25519','-N','','-f',$hostKey) 'Host key generation'
    $hostFingerprintText = Invoke-Native ssh-keygen @('-E','sha256','-lf',"$hostKey.pub") 'Host-key fingerprint calculation' -Capture
    $hostFingerprintMatch = [Text.RegularExpressions.Regex]::Match($hostFingerprintText,'SHA256:[A-Za-z0-9+/]{43}')
    Require $hostFingerprintMatch.Success 'Synthetic SSH host-key fingerprint is malformed.'
    $hostFingerprint = $hostFingerprintMatch.Value

    Copy-Item '/etc/hosts' $hostsBackup
    $hostLine = Join-Path $work 'hosts.line'
    Write-Utf8 $hostLine ("$publicIpv4 $apiHost`n")
    Invoke-Native sudo @('sh','-c',"cat '$hostLine' >> /etc/hosts") 'Install temporary hosts entry'
    $hostModified = $true
    Invoke-Native sudo @('ip','address','add',"$publicIpv4/32",'dev','lo') 'Install temporary public IPv4 loopback alias'
    $aliasAdded = $true

    & id steward *> $null
    if ($LASTEXITCODE -eq 0) { Invoke-Native sudo @('userdel','--remove','steward') 'Remove pre-existing disposable steward user' }
    Invoke-Native sudo @('useradd','--create-home','--shell','/bin/bash','steward') 'Create disposable steward user'
    $userCreated = $true
    Invoke-Native sudo @('passwd','-d','steward') 'Enable public-key-only steward account'
    Invoke-Native sudo @('usermod','--append','--groups','docker','steward') 'Grant disposable steward user Docker access'
    $authorizedKeyPath = Join-Path $work 'authorized_keys'
    Copy-Item "$clientKey.pub" $authorizedKeyPath
    Invoke-Native sudo @('install','-d','-m','0700','-o','steward','-g','steward','/home/steward/.ssh') 'Create authorized-keys directory'
    Invoke-Native sudo @('install','-m','0600','-o','steward','-g','steward',$authorizedKeyPath,'/home/steward/.ssh/authorized_keys') 'Install authorized key'
    $sudoersSource = Join-Path $work 'sudoers'
    Write-Utf8 $sudoersSource "steward ALL=(ALL) NOPASSWD: ALL`n"
    Invoke-Native sudo @('install','-m','0440','-o','root','-g','root',$sudoersSource,'/etc/sudoers.d/steward-live-ingress-test') 'Install disposable sudo contract'

    Write-Utf8 $baselineSource ":19081 {`n    respond `"steward-baseline`"`n}`n"
    Invoke-Native sudo @('install','-d','-m','0755','-o','root','-g','root','/etc/caddy') 'Create Caddy configuration directory'
    Invoke-Native sudo @('install','-m','0644','-o','root','-g','root',$baselineSource,'/etc/caddy/Caddyfile') 'Install exact Caddy baseline'
    Invoke-Native sudo @('caddy','validate','--config','/etc/caddy/Caddyfile','--adapter','caddyfile') 'Validate exact Caddy baseline'
    Invoke-Native sudo @('systemctl','restart','caddy') 'Start managed Caddy baseline'
    & systemctl is-active --quiet caddy
    Require ($LASTEXITCODE -eq 0) 'Managed caddy.service is not active with the baseline configuration.'
    $baselineHash = (Invoke-Native sudo @('sha256sum','/etc/caddy/Caddyfile') 'Hash exact Caddy baseline' -Capture).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)[0].ToUpperInvariant()
    Require ($baselineHash -match '^[0-9A-F]{64}$') 'Caddy baseline SHA-256 is malformed.'

    $environmentSource = Join-Path $work 'backend.env'
    Write-Utf8 $environmentSource @"
PORT=8080
ConnectionStrings__Steward=Host=db.alpha.getsteward.dev;Port=5432;Database=steward;Username=steward;Password=SyntheticPostgresPassword-Ingress;SSL Mode=VerifyFull;Trust Server Certificate=false
ObjectStorage__ServiceUrl=https://s3.alpha.getsteward.dev/
ObjectStorage__AuthenticationRegion=fr-par
ObjectStorage__BucketName=steward-private
ObjectStorage__AccessKeyId=SYNTHETICINGRESSACCESSKEY
ObjectStorage__SecretAccessKey=SyntheticIngressStorageSecretKey
ObjectStorage__ForcePathStyle=false
FriendsBuild__Enabled=true
FriendsBuild__Identities__0__Id=friend-ingress
FriendsBuild__Identities__0__DisplayName=Ingress Friend
FriendsBuild__Identities__0__CredentialSha256=$('e' * 64)
ReverseProxy__KnownProxyIp=127.0.0.1
Cleanup__IntervalMinutes=15
Cleanup__VerifiedCandidateRetentionDays=7
Cleanup__BatchSize=100
"@
    Invoke-Native sudo @('install','-d','-m','0755','-o','root','-g','root','/etc/steward') 'Create Steward configuration directory'
    Invoke-Native sudo @('install','-m','0600','-o','root','-g','root',$environmentSource,'/etc/steward/backend.env') 'Install protected environment fixture'
    Invoke-Native sudo @('rm','-rf',$remoteReleaseDirectory,$remotePlanDirectory) 'Clear exact target paths'
    & docker rm --force steward-backend *> $null
    Require (@(& ss -ltnH 'sport = :8080' 2>$null).Count -eq 0) 'Port 8080 is occupied before ingress activation rehearsal.'

    Invoke-Native sudo @('install','-d','-m','0755','/run/sshd') 'Create sshd runtime directory'
    Write-Utf8 $sshdConfig @"
Port $sshPort
ListenAddress $publicIpv4
HostKey $hostKey
PidFile $sshdPid
AuthorizedKeysFile .ssh/authorized_keys
PasswordAuthentication no
KbdInteractiveAuthentication no
ChallengeResponseAuthentication no
PermitRootLogin no
PubkeyAuthentication yes
AllowUsers steward
UsePAM no
StrictModes yes
Subsystem sftp internal-sftp
LogLevel VERBOSE
"@
    Invoke-Native sudo @('/usr/sbin/sshd','-f',$sshdConfig,'-E',$sshdLog) 'Start disposable SSH server'
    $sshdStarted = $true
    $reachable = $false
    for ($attempt=1;$attempt -le 30;$attempt++) {
        $scan = @(& ssh-keyscan -4 -T 2 -p $sshPort -t ed25519 $apiHost 2>$null)
        if ($LASTEXITCODE -eq 0 -and $scan.Count -eq 1) { $reachable = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Require $reachable 'Disposable SSH server did not become reachable.'

    & (Join-Path $bundle 'prepare-closed-alpha-live-deployment.ps1') -BundleDirectory $bundle -ExpectedPublicIpv4 $publicIpv4 -SshHostKeySha256 $hostFingerprint -SshPort $sshPort -OutputPath $requestPath
    & (Join-Path $bundle 'preflight-closed-alpha-live-host.ps1') -BundleDirectory $bundle -RequestPath $requestPath -SshPrivateKeyPath $clientKey -EvidencePath $preflightPath
    & (Join-Path $bundle 'stage-closed-alpha-live-deployment-plan.ps1') -BundleDirectory $bundle -RequestPath $requestPath -PreflightEvidencePath $preflightPath -SshPrivateKeyPath $clientKey -EvidencePath $stagingPath
    & (Join-Path $bundle 'deploy-closed-alpha-live-backend.ps1') -BundleDirectory $bundle -RequestPath $requestPath -StagingEvidencePath $stagingPath -SshPrivateKeyPath $clientKey -EvidencePath $deploymentPath

    $deployment = [IO.File]::ReadAllText($deploymentPath) | ConvertFrom-Json
    Require ([string]$deployment.documentType -ceq 'steward.closed-alpha-live-backend-deployment') 'Backend deployment evidence identity is invalid.'
    Require ([string]$deployment.caddyStateAfter -ceq "sha256:$baselineHash") 'Backend deployment did not preserve the exact Caddy baseline.'
    Require ([bool]$deployment.backendContainerRunning -and [bool]$deployment.backendLocalReady) 'Backend is not ready before ingress activation tests.'

    $staleBackend = [IO.File]::ReadAllText($deploymentPath) | ConvertFrom-Json
    $staleBackend.completedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-20).ToString('O')
    Write-Utf8 $staleBackendPath ($staleBackend | ConvertTo-Json -Depth 8)
    Expect-Failure {
        & (Join-Path $bundle 'activate-closed-alpha-live-ingress.ps1') -BundleDirectory $bundle -RequestPath $requestPath -BackendDeploymentEvidencePath $staleBackendPath -SshPrivateKeyPath $clientKey -EvidencePath (Join-Path $work 'stale-activation.json')
    } 'stale backend evidence is rejected before ingress activation' 'Live backend deployment evidence is stale; redeploy the exact backend before ingress activation.'

    Write-Utf8 $driftSource ":19082 {`n    respond `"steward-drift`"`n}`n"
    Invoke-Native sudo @('install','-m','0644','-o','root','-g','root',$driftSource,'/etc/caddy/Caddyfile') 'Introduce post-backend Caddy drift'
    Invoke-Native sudo @('systemctl','reload','caddy') 'Reload post-backend Caddy drift fixture'
    try {
        Expect-Failure {
            & (Join-Path $bundle 'activate-closed-alpha-live-ingress.ps1') -BundleDirectory $bundle -RequestPath $requestPath -BackendDeploymentEvidencePath $deploymentPath -SshPrivateKeyPath $clientKey -EvidencePath (Join-Path $work 'drift-activation.json')
        } 'post-backend Caddy drift is rejected before ingress activation' 'Caddy state changed after backend deployment'
    }
    finally {
        Invoke-Native sudo @('install','-m','0644','-o','root','-g','root',$baselineSource,'/etc/caddy/Caddyfile') 'Restore exact Caddy baseline after drift refusal'
        Invoke-Native sudo @('systemctl','reload','caddy') 'Reload exact Caddy baseline after drift refusal'
    }

    & (Join-Path $bundle 'activate-closed-alpha-live-ingress.ps1') -BundleDirectory $bundle -RequestPath $requestPath -BackendDeploymentEvidencePath $deploymentPath -SshPrivateKeyPath $clientKey -EvidencePath $activationPath
    Require ([IO.File]::Exists($activationPath)) 'Ingress activator did not write evidence.'
    $activationText = [IO.File]::ReadAllText($activationPath)
    Require ($activationText.Length -gt 0 -and $activationText.Length -le 64KB) 'Ingress activation evidence is empty or too large.'
    $activation = $activationText | ConvertFrom-Json
    Require ([string]$activation.documentType -ceq 'steward.closed-alpha-live-ingress-activation' -and [int]$activation.schemaVersion -eq 1) 'Ingress activation evidence identity is invalid.'
    Require ([string]$activation.releaseCommitSha -ceq $releaseCommit -and [string]$activation.backendImageId -ceq $imageId) 'Ingress activation lost exact release/backend identity.'
    Require ([bool]$activation.backendDeploymentFresh -and [bool]$activation.dnsReverified -and [bool]$activation.sshHostKeyReverified) 'Ingress activation did not revalidate the fresh host boundary.'
    Require ([bool]$activation.remoteBundleReverified -and [bool]$activation.deploymentPlanReverified) 'Ingress activation did not reverify staged bytes.'
    Require ([bool]$activation.backendContainerRunning -and [bool]$activation.backendLocalLive -and [bool]$activation.backendLocalReady -and [bool]$activation.backendPort8080Listening) 'Ingress activation did not preserve the exact local backend.'
    Require ([string]$activation.caddyBaselineState -ceq "sha256:$baselineHash") 'Ingress activation did not bind the exact Caddy baseline.'
    Require ([bool]$activation.caddyConfigInstalled -and [bool]$activation.caddyValidated -and [bool]$activation.caddyReloaded -and [bool]$activation.caddyServiceActive) 'Ingress activation did not prove managed Caddy activation.'
    Require ([string]$activation.caddyGeneratedSha256 -match '^[0-9A-F]{64}$' -and [string]$activation.caddyActivatedState -ceq "sha256:$($activation.caddyGeneratedSha256)") 'Ingress activation Caddy hash evidence is inconsistent.'
    Require ([bool]$activation.protectedEnvironmentMetadataPreserved -and -not [bool]$activation.protectedEnvironmentTransferred) 'Ingress activation crossed the protected environment boundary.'
    Require (-not [bool]$activation.publicHttpsVerified -and -not [bool]$activation.externalPort8080ClosedVerified) 'Ingress activation overstated external acceptance.'
    Require (-not [bool]$activation.publishAllowed -and [string]$activation.physicalBringHere -ceq 'deferred' -and -not [bool]$activation.publicationAuthorizationChanged) 'Ingress activation changed publication state.'

    $installedHash = (Invoke-Native sudo @('sha256sum','/etc/caddy/Caddyfile') 'Hash activated Caddy configuration' -Capture).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)[0].ToUpperInvariant()
    Require ($installedHash -ceq [string]$activation.caddyGeneratedSha256) 'Installed Caddy configuration does not match activation evidence.'
    & systemctl is-active --quiet caddy
    Require ($LASTEXITCODE -eq 0) 'Managed caddy.service is not active after ingress activation.'
    Invoke-Native curl @('--fail','--silent','--show-error','--max-time','5','http://127.0.0.1:8080/health/ready') 'Verify backend readiness after Caddy activation'

    Write-Host '[OK] Managed Caddy activation, rollback boundary, and refusal cases passed without public-HTTPS claims.'
}
finally {
    & docker rm --force steward-backend *> $null
    if (-not [string]::IsNullOrWhiteSpace($imageId)) {
        & docker image rm --force $imageTag *> $null
        & docker image rm --force $imageId *> $null
    }
    & sudo rm -rf $remoteReleaseDirectory $remotePlanDirectory *> $null
    & sudo rm -f /etc/sudoers.d/steward-live-ingress-test *> $null
    & sudo rm -f /etc/steward/backend.env *> $null

    if ($caddyOriginalExists -and [IO.File]::Exists($caddyOriginalBackup)) {
        & sudo install -m 0644 -o root -g root $caddyOriginalBackup /etc/caddy/Caddyfile *> $null
        if ($caddyServiceWasActive) { & sudo systemctl restart caddy *> $null } else { & sudo systemctl stop caddy *> $null }
    }
    else {
        & sudo rm -f /etc/caddy/Caddyfile *> $null
        if (-not $caddyServiceWasActive) { & sudo systemctl stop caddy *> $null }
    }

    if ($sshdStarted -and [IO.File]::Exists($sshdPid)) {
        $pidText = [IO.File]::ReadAllText($sshdPid).Trim(); $pidValue = 0
        if ([int]::TryParse($pidText,[ref]$pidValue) -and $pidValue -gt 1) { & sudo kill $pidValue *> $null }
    }
    if ($userCreated) { & sudo userdel --remove steward *> $null }
    if ($aliasAdded) { & sudo ip address del "$publicIpv4/32" dev lo *> $null }
    if ($hostModified -and [IO.File]::Exists($hostsBackup)) { & sudo cp $hostsBackup /etc/hosts *> $null }
    if ([IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
