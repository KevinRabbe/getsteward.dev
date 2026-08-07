[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }

function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Require-Tool([string]$Name) { Require ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue)) "Required integration-test tool '$Name' is not available." }
function Write-Utf8([string]$Path, [string]$Content) {
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

foreach ($tool in @('docker','ip','ssh-keygen','ssh-keyscan','sudo')) { Require-Tool $tool }
Require ([IO.File]::Exists('/usr/sbin/sshd')) 'OpenSSH server is not installed at /usr/sbin/sshd.'

$work = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-backend-test-" + [Guid]::NewGuid().ToString('N'))
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
$requestPath = Join-Path $work 'live-request.json'
$preflightPath = Join-Path $work 'preflight.json'
$stagingPath = Join-Path $work 'staging.json'
$deploymentPath = Join-Path $work 'backend-deployment.json'
$staleStagingPath = Join-Path $work 'stale-staging.json'
$apiHost = 'alpha-backend.getsteward.dev'
$publicIpv4 = '93.184.216.34'
$sshPort = 32223
$releaseCommit = '3333333333333333333333333333333333333333'
$releaseVersion = '2.0.0-live-backend'
$imageTag = 'steward-backend:live-backend-test'
$remoteReleaseDirectory = "/srv/steward/releases/$releaseCommit"
$remotePlanDirectory = "/srv/steward/deployment-plans/$releaseCommit"
$createdCaddyStub = $false
$aliasAdded = $false
$hostModified = $false
$userCreated = $false
$sshdStarted = $false
$imageId = $null

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
        'deploy-closed-alpha-live-backend.ps1'
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
            reason = 'Synthetic backend executor rehearsal; physical Bring Here remains deferred.'
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
    Invoke-Native sudo @('install','-m','0440','-o','root','-g','root',$sudoersSource,'/etc/sudoers.d/steward-live-backend-test') 'Install disposable sudo contract'

    if ($null -eq (Get-Command caddy -ErrorAction SilentlyContinue)) {
        $caddyStub = Join-Path $work 'caddy'
        Write-Utf8 $caddyStub "#!/usr/bin/env bash`nexit 0`n"
        Invoke-Native sudo @('install','-m','0755','-o','root','-g','root',$caddyStub,'/usr/local/bin/caddy') 'Install Caddy presence stub'
        $createdCaddyStub = $true
    }

    $environmentSource = Join-Path $work 'backend.env'
    Write-Utf8 $environmentSource @"
PORT=8080
ConnectionStrings__Steward=Host=db.alpha.getsteward.dev;Port=5432;Database=steward;Username=steward;Password=SyntheticPostgresPassword-Backend;SSL Mode=VerifyFull;Trust Server Certificate=false
ObjectStorage__ServiceUrl=https://s3.alpha.getsteward.dev/
ObjectStorage__AuthenticationRegion=fr-par
ObjectStorage__BucketName=steward-private
ObjectStorage__AccessKeyId=SYNTHETICBACKENDACCESSKEY
ObjectStorage__SecretAccessKey=SyntheticBackendStorageSecretKey
ObjectStorage__ForcePathStyle=false
FriendsBuild__Enabled=true
FriendsBuild__Identities__0__Id=friend-backend
FriendsBuild__Identities__0__DisplayName=Backend Friend
FriendsBuild__Identities__0__CredentialSha256=$('d' * 64)
ReverseProxy__KnownProxyIp=127.0.0.1
Cleanup__IntervalMinutes=15
Cleanup__VerifiedCandidateRetentionDays=7
Cleanup__BatchSize=100
"@
    Invoke-Native sudo @('install','-d','-m','0755','-o','root','-g','root','/etc/steward') 'Create disposable Steward configuration directory'
    Invoke-Native sudo @('install','-m','0600','-o','root','-g','root',$environmentSource,'/etc/steward/backend.env') 'Install protected environment fixture'
    Invoke-Native sudo @('rm','-rf',$remoteReleaseDirectory,$remotePlanDirectory) 'Clear exact target paths'
    & docker rm --force steward-backend *> $null
    Require (@(& ss -ltnH 'sport = :8080' 2>$null).Count -eq 0) 'Port 8080 is occupied before backend executor rehearsal.'

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

    $stale = [IO.File]::ReadAllText($stagingPath) | ConvertFrom-Json
    $stale.completedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-20).ToString('O')
    Write-Utf8 $staleStagingPath ($stale | ConvertTo-Json -Depth 8)
    Expect-Failure {
        & (Join-Path $bundle 'deploy-closed-alpha-live-backend.ps1') -BundleDirectory $bundle -RequestPath $requestPath -StagingEvidencePath $staleStagingPath -SshPrivateKeyPath $clientKey -EvidencePath (Join-Path $work 'stale-deploy.json')
    } 'stale staging evidence is rejected before backend deployment' 'Live plan staging evidence is stale; restage the exact plan before backend deployment.'

    Invoke-Native sudo @('install','-d','-m','0755','-o','root','-g','root','/etc/caddy') 'Create disposable Caddy directory'
    $caddyDrift = Join-Path $work 'Caddyfile.drift'
    Write-Utf8 $caddyDrift "# drift fixture`n"
    Invoke-Native sudo @('install','-m','0644','-o','root','-g','root',$caddyDrift,'/etc/caddy/Caddyfile') 'Introduce post-staging Caddy drift'
    try {
        Expect-Failure {
            & (Join-Path $bundle 'deploy-closed-alpha-live-backend.ps1') -BundleDirectory $bundle -RequestPath $requestPath -StagingEvidencePath $stagingPath -SshPrivateKeyPath $clientKey -EvidencePath (Join-Path $work 'drift-deploy.json')
        } 'post-staging Caddy drift is rejected before backend deployment' 'Caddy state changed after live plan staging'
    }
    finally { Invoke-Native sudo @('rm','-f','/etc/caddy/Caddyfile') 'Remove post-staging Caddy drift fixture' }

    & (Join-Path $bundle 'deploy-closed-alpha-live-backend.ps1') -BundleDirectory $bundle -RequestPath $requestPath -StagingEvidencePath $stagingPath -SshPrivateKeyPath $clientKey -EvidencePath $deploymentPath
    Require ([IO.File]::Exists($deploymentPath)) 'Live backend executor did not write evidence.'
    $deploymentText = [IO.File]::ReadAllText($deploymentPath)
    Require ($deploymentText.Length -gt 0 -and $deploymentText.Length -le 64KB) 'Live backend deployment evidence is empty or too large.'
    $deployment = $deploymentText | ConvertFrom-Json
    Require ([string]$deployment.documentType -ceq 'steward.closed-alpha-live-backend-deployment' -and [int]$deployment.schemaVersion -eq 1) 'Live backend deployment evidence identity is invalid.'
    Require ([string]$deployment.releaseCommitSha -ceq $releaseCommit -and [string]$deployment.backendImageId -ceq $imageId) 'Live backend deployment evidence lost exact release/image identity.'
    Require ([bool]$deployment.stagingFresh -and [bool]$deployment.dnsReverified -and [bool]$deployment.sshHostKeyReverified) 'Live backend deployment did not revalidate staged host identity.'
    Require ([bool]$deployment.remoteBundleReverified -and [bool]$deployment.deploymentPlanReverified) 'Live backend deployment did not reverify staged bytes.'
    Require ([bool]$deployment.backendImageLoaded -and [bool]$deployment.backendContainerStarted -and [bool]$deployment.backendContainerRunning) 'Live backend deployment did not cross the intended backend mutation boundary.'
    Require ([bool]$deployment.backendLocalLive -and [bool]$deployment.backendLocalReady -and [bool]$deployment.backendPort8080Listening) 'Live backend deployment did not prove local runtime readiness.'
    Require ([bool]$deployment.protectedEnvironmentMetadataPreserved -and -not [bool]$deployment.protectedEnvironmentTransferred) 'Live backend deployment crossed the protected environment boundary.'
    Require (-not [bool]$deployment.caddyModified -and -not [bool]$deployment.publicHttpsVerified -and -not [bool]$deployment.externalPort8080ClosedVerified) 'Live backend deployment overstated or crossed the public-ingress boundary.'
    Require (-not [bool]$deployment.publishAllowed -and [string]$deployment.physicalBringHere -ceq 'deferred' -and -not [bool]$deployment.publicationAuthorizationChanged) 'Live backend deployment changed publication state.'

    Require ((Invoke-Native docker @('container','inspect','steward-backend','--format','{{.State.Running}}') 'Verify deployed backend running' -Capture).Trim() -ceq 'true') 'Disposable backend is not running after executor success.'
    Require ((Invoke-Native docker @('container','inspect','steward-backend','--format','{{.Image}}') 'Verify deployed backend image' -Capture).Trim() -ceq $imageId) 'Disposable backend is running the wrong image.'
    Invoke-Native curl @('--fail','--silent','--show-error','--max-time','5','http://127.0.0.1:8080/health/ready') 'Verify disposable backend readiness'
    Require (-not [IO.File]::Exists('/etc/caddy/Caddyfile')) 'Backend executor unexpectedly created the live Caddyfile.'

    Write-Host '[OK] Staged exact-image backend deployment executor and refusal cases passed.'
}
finally {
    & docker rm --force steward-backend *> $null
    if (-not [string]::IsNullOrWhiteSpace($imageId)) {
        & docker image rm --force $imageTag *> $null
        & docker image rm --force $imageId *> $null
    }
    & sudo rm -rf $remoteReleaseDirectory $remotePlanDirectory *> $null
    & sudo rm -f /etc/caddy/Caddyfile *> $null
    & sudo rm -f /etc/sudoers.d/steward-live-backend-test *> $null
    & sudo rm -f /etc/steward/backend.env *> $null
    if ($sshdStarted -and [IO.File]::Exists($sshdPid)) {
        $pidText = [IO.File]::ReadAllText($sshdPid).Trim(); $pidValue = 0
        if ([int]::TryParse($pidText,[ref]$pidValue) -and $pidValue -gt 1) { & sudo kill $pidValue *> $null }
    }
    if ($createdCaddyStub) { & sudo rm -f /usr/local/bin/caddy *> $null }
    if ($userCreated) { & sudo userdel --remove steward *> $null }
    if ($aliasAdded) { & sudo ip address del "$publicIpv4/32" dev lo *> $null }
    if ($hostModified -and [IO.File]::Exists($hostsBackup)) { & sudo cp $hostsBackup /etc/hosts *> $null }
    if ([IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
