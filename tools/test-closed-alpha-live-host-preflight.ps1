[CmdletBinding()]
param(
    [switch]$RunPlanStaging
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
    Require ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue)) "Required integration-test tool '$Name' is not available."
}

function Invoke-Native([string]$Tool, [string[]]$Arguments, [string]$Context, [switch]$Capture) {
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

function Expect-Failure([scriptblock]$Action, [string]$Context, [string]$ExpectedMessage) {
    $failed = $false
    try {
        & $Action
    }
    catch {
        $failed = $true
        $message = [string]$_.Exception.Message
        Require ($message.Contains($ExpectedMessage, [StringComparison]::Ordinal)) "${Context} failed for the wrong reason: $message"
        Write-Host "[EXPECTED] ${Context}: $message"
    }
    Require $failed "Expected failure did not occur: $Context"
}

function Write-Utf8([string]$Path, [string]$Content) {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Write-SyntheticCandidate([string]$Bundle, [string]$ApiBaseUrl) {
    [IO.Directory]::CreateDirectory((Join-Path $Bundle 'desktop')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $Bundle 'backend')) | Out-Null

    foreach ($name in @(
        'verify-closed-alpha-release.ps1',
        'prepare-closed-alpha-deployment.ps1',
        'prepare-closed-alpha-live-deployment.ps1',
        'preflight-closed-alpha-live-host.ps1',
        'stage-closed-alpha-live-deployment-plan.ps1'
    )) {
        Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $Bundle $name)
    }

    Write-Utf8 (Join-Path $Bundle 'desktop/friends.zip') 'synthetic desktop bytes'
    Write-Utf8 (Join-Path $Bundle 'backend/steward-backend.tar') 'synthetic backend image bytes'
    Write-Utf8 (Join-Path $Bundle 'backend/deployment.env.example') 'PORT=8080'
    Write-Utf8 (Join-Path $Bundle 'CLOSED-ALPHA-OPERATOR-RUNBOOK.txt') 'synthetic operator runbook'
    Write-Utf8 (Join-Path $Bundle 'RELEASE-STATUS.txt') 'synthetic release status'

    $artifactEntries = @(Get-ChildItem -LiteralPath $Bundle -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($Bundle, $_.FullName).Replace('\', '/')
                byteSize = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            }
        })

    $manifest = [ordered]@{
        documentType = 'steward.closed-alpha-release-candidate'
        schemaVersion = 1
        channel = 'closed-alpha'
        version = '2.0.0-live-host-preflight'
        commitSha = '2222222222222222222222222222222222222222'
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        deployment = [ordered]@{
            apiBaseUrl = $ApiBaseUrl
            authenticationMode = 'friends-build'
            backendImageId = 'sha256:' + ('b' * 64)
            backendImageTag = 'steward-backend:live-host-preflight'
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
            reason = 'Synthetic live-host rehearsal; physical Bring Here remains deferred.'
        }
        artifacts = $artifactEntries
    }
    Write-Utf8 (Join-Path $Bundle 'release-manifest.json') ($manifest | ConvertTo-Json -Depth 8)
}

foreach ($tool in @('docker', 'ip', 'ssh-keygen', 'ssh-keyscan', 'sudo')) {
    Require-Tool $tool
}
Require ([IO.File]::Exists('/usr/sbin/sshd')) 'OpenSSH server is not installed at /usr/sbin/sshd.'

$work = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-host-integration-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
$bundle = Join-Path $work 'bundle'
$clientKey = Join-Path $work 'client_ed25519'
$hostKey = Join-Path $work 'host_ed25519'
$sshdConfig = Join-Path $work 'sshd_config'
$sshdLog = Join-Path $work 'sshd.log'
$sshdPid = Join-Path $work 'sshd.pid'
$hostsBackup = Join-Path $work 'hosts.backup'
$requestPath = Join-Path $work 'live-deployment-request.json'
$preflightEvidencePath = Join-Path $work 'live-host-preflight.json'
$wrongRequestPath = Join-Path $work 'wrong-host-key-request.json'
$modeFailureEvidencePath = Join-Path $work 'mode-failure-evidence.json'
$stalePreflightPath = Join-Path $work 'stale-preflight.json'
$stagingEvidencePath = Join-Path $work 'live-plan-staging.json'
$apiHost = 'alpha-preflight.getsteward.dev'
$publicIpv4 = '93.184.216.34'
$sshPort = 32222
$releaseCommit = '2222222222222222222222222222222222222222'
$remoteReleaseDirectory = "/srv/steward/releases/$releaseCommit"
$remotePlanDirectory = "/srv/steward/deployment-plans/$releaseCommit"
$syntheticPostgresPassword = 'SyntheticPostgresPassword-1234567890'
$syntheticStorageAccessKey = 'SYNTHETICACCESSKEY12345'
$syntheticStorageSecretKey = 'SyntheticObjectStorageSecretKey-1234567890'
$createdCaddyStub = $false
$aliasAdded = $false
$hostModified = $false
$userCreated = $false
$sshdStarted = $false

try {
    Write-SyntheticCandidate -Bundle $bundle -ApiBaseUrl "https://$apiHost/"
    & (Join-Path $bundle 'verify-closed-alpha-release.ps1') -BundleDirectory $bundle

    Invoke-Native -Tool 'ssh-keygen' -Arguments @('-q', '-t', 'ed25519', '-N', '', '-f', $clientKey) -Context 'Client key generation'
    Invoke-Native -Tool 'ssh-keygen' -Arguments @('-q', '-t', 'ed25519', '-N', '', '-f', $hostKey) -Context 'Host key generation'
    $hostFingerprintText = Invoke-Native -Tool 'ssh-keygen' -Arguments @('-E', 'sha256', '-lf', "$hostKey.pub") -Context 'Host-key fingerprint calculation' -Capture
    $hostFingerprintMatch = [Text.RegularExpressions.Regex]::Match($hostFingerprintText, 'SHA256:[A-Za-z0-9+/]{43}')
    Require $hostFingerprintMatch.Success 'Synthetic SSH host-key fingerprint is malformed.'
    $hostFingerprint = $hostFingerprintMatch.Value

    Copy-Item -LiteralPath '/etc/hosts' -Destination $hostsBackup
    $hostLinePath = Join-Path $work 'hosts.line'
    Write-Utf8 $hostLinePath ("$publicIpv4 $apiHost`n")
    Invoke-Native -Tool 'sudo' -Arguments @('sh', '-c', "cat '$hostLinePath' >> /etc/hosts") -Context 'Temporary hosts entry installation'
    $hostModified = $true

    Invoke-Native -Tool 'sudo' -Arguments @('ip', 'address', 'add', "$publicIpv4/32", 'dev', 'lo') -Context 'Temporary public IPv4 loopback alias'
    $aliasAdded = $true

    & id steward *> $null
    if ($LASTEXITCODE -eq 0) {
        Invoke-Native -Tool 'sudo' -Arguments @('userdel', '--remove', 'steward') -Context 'Remove pre-existing disposable steward user'
    }
    Invoke-Native -Tool 'sudo' -Arguments @('useradd', '--create-home', '--shell', '/bin/bash', 'steward') -Context 'Create disposable steward user'
    $userCreated = $true
    Invoke-Native -Tool 'sudo' -Arguments @('passwd', '-d', 'steward') -Context 'Enable public-key-only steward account'
    Invoke-Native -Tool 'sudo' -Arguments @('usermod', '--append', '--groups', 'docker', 'steward') -Context 'Grant disposable steward user Docker socket group'

    $authorizedKeyPath = Join-Path $work 'authorized_keys'
    Copy-Item -LiteralPath "$clientKey.pub" -Destination $authorizedKeyPath
    Invoke-Native -Tool 'sudo' -Arguments @('install', '-d', '-m', '0700', '-o', 'steward', '-g', 'steward', '/home/steward/.ssh') -Context 'Create disposable authorized-keys directory'
    Invoke-Native -Tool 'sudo' -Arguments @('install', '-m', '0600', '-o', 'steward', '-g', 'steward', $authorizedKeyPath, '/home/steward/.ssh/authorized_keys') -Context 'Install disposable authorized key'

    $sudoersSource = Join-Path $work 'steward-sudoers'
    Write-Utf8 $sudoersSource "steward ALL=(ALL) NOPASSWD: ALL`n"
    Invoke-Native -Tool 'sudo' -Arguments @('install', '-m', '0440', '-o', 'root', '-g', 'root', $sudoersSource, '/etc/sudoers.d/steward-live-host-preflight') -Context 'Install disposable sudo contract'

    if ($null -eq (Get-Command caddy -ErrorAction SilentlyContinue)) {
        $caddySource = Join-Path $work 'caddy'
        Write-Utf8 $caddySource "#!/usr/bin/env bash`nexit 0`n"
        Invoke-Native -Tool 'sudo' -Arguments @('install', '-m', '0755', '-o', 'root', '-g', 'root', $caddySource, '/usr/local/bin/caddy') -Context 'Install tool-presence-only Caddy stub'
        $createdCaddyStub = $true
    }

    $environmentSource = Join-Path $work 'backend.env'
    $environmentText = @"
PORT=8080
ConnectionStrings__Steward=Host=db.alpha.getsteward.dev;Port=5432;Database=steward;Username=steward;Password=$syntheticPostgresPassword;SSL Mode=VerifyFull;Trust Server Certificate=false
ObjectStorage__ServiceUrl=https://s3.alpha.getsteward.dev/
ObjectStorage__AuthenticationRegion=fr-par
ObjectStorage__BucketName=steward-private
ObjectStorage__AccessKeyId=$syntheticStorageAccessKey
ObjectStorage__SecretAccessKey=$syntheticStorageSecretKey
ObjectStorage__ForcePathStyle=false
FriendsBuild__Enabled=true
FriendsBuild__Identities__0__Id=friend-alpha
FriendsBuild__Identities__0__DisplayName=Alpha Friend
FriendsBuild__Identities__0__CredentialSha256=$('c' * 64)
ReverseProxy__KnownProxyIp=127.0.0.1
Cleanup__IntervalMinutes=15
Cleanup__VerifiedCandidateRetentionDays=7
Cleanup__BatchSize=100
"@
    Write-Utf8 $environmentSource $environmentText
    Invoke-Native -Tool 'sudo' -Arguments @('install', '-d', '-m', '0755', '-o', 'root', '-g', 'root', '/etc/steward') -Context 'Create disposable Steward configuration directory'
    Invoke-Native -Tool 'sudo' -Arguments @('install', '-m', '0600', '-o', 'root', '-g', 'root', $environmentSource, '/etc/steward/backend.env') -Context 'Install protected environment fixture'

    Invoke-Native -Tool 'sudo' -Arguments @('rm', '-rf', $remoteReleaseDirectory, $remotePlanDirectory) -Context 'Clear exact target paths'
    & docker rm --force steward-backend *> $null
    $port8080 = @(& ss -ltnH 'sport = :8080' 2>$null)
    Require ($port8080.Count -eq 0) 'Port 8080 is unexpectedly occupied before the integration rehearsal.'

    Invoke-Native -Tool 'sudo' -Arguments @('install', '-d', '-m', '0755', '/run/sshd') -Context 'Create sshd runtime directory'
    $sshdText = @"
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
    Write-Utf8 $sshdConfig $sshdText
    Invoke-Native -Tool 'sudo' -Arguments @('/usr/sbin/sshd', '-f', $sshdConfig, '-E', $sshdLog) -Context 'Start disposable SSH server'
    $sshdStarted = $true

    $reachable = $false
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        $scan = @(& ssh-keyscan -4 -T 2 -p $sshPort -t ed25519 $apiHost 2>$null)
        if ($LASTEXITCODE -eq 0 -and $scan.Count -eq 1) {
            $reachable = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $reachable) {
        $log = if ([IO.File]::Exists($sshdLog)) { [IO.File]::ReadAllText($sshdLog) } else { '<no sshd log>' }
        throw "Disposable SSH server did not become reachable. $log"
    }

    & (Join-Path $bundle 'prepare-closed-alpha-live-deployment.ps1') `
        -BundleDirectory $bundle `
        -ExpectedPublicIpv4 $publicIpv4 `
        -SshHostKeySha256 $hostFingerprint `
        -SshPort $sshPort `
        -OutputPath $requestPath

    & (Join-Path $bundle 'preflight-closed-alpha-live-host.ps1') `
        -BundleDirectory $bundle `
        -RequestPath $requestPath `
        -SshPrivateKeyPath $clientKey `
        -EvidencePath $preflightEvidencePath

    Require ([IO.File]::Exists($preflightEvidencePath)) 'Live-host preflight did not write evidence.'
    $evidenceText = [IO.File]::ReadAllText($preflightEvidencePath)
    Require ($evidenceText.Length -gt 0 -and $evidenceText.Length -le 64KB) 'Live-host preflight evidence is empty or too large.'
    $evidence = $evidenceText | ConvertFrom-Json
    Require ([string]$evidence.documentType -ceq 'steward.closed-alpha-live-host-preflight') 'Live-host preflight evidence has the wrong document type.'
    Require ([int]$evidence.schemaVersion -eq 1) 'Live-host preflight evidence has the wrong schema version.'
    Require ([string]$evidence.releaseCommitSha -ceq $releaseCommit) 'Live-host preflight evidence lost release identity.'
    Require ([string]$evidence.apiHost -ceq $apiHost -and [string]$evidence.resolvedPublicIpv4 -ceq $publicIpv4) 'Live-host preflight evidence has the wrong DNS identity.'
    Require ([string]$evidence.sshHostKeySha256 -ceq $hostFingerprint) 'Live-host preflight evidence has the wrong SSH host key.'
    Require ([string]$evidence.remoteOs -ceq 'Linux' -and [string]$evidence.remoteArchitecture -ceq 'x86_64') 'Live-host preflight evidence has the wrong platform.'
    Require ([bool]$evidence.requiredToolsPresent -and [bool]$evidence.sudoNonInteractive -and [bool]$evidence.dockerServerAccessible) 'Live-host preflight did not prove remote tool/sudo/Docker readiness.'
    Require ([int]$evidence.remoteEnvironmentOwnerUid -eq 0 -and [string]$evidence.remoteEnvironmentMode -ceq '0600') 'Live-host preflight did not prove root/0600 environment metadata.'
    Require ([bool]$evidence.backendPort8080Clear -and [bool]$evidence.releaseDirectoryAbsent -and [bool]$evidence.deploymentPlanDirectoryAbsent) 'Live-host preflight did not prove a clean first-deployment target.'
    Require (-not [bool]$evidence.candidateTransferred -and -not [bool]$evidence.deploymentStarted -and -not [bool]$evidence.secretEnvironmentRead) 'Live-host preflight crossed its read-only boundary.'
    Require (-not [bool]$evidence.publishAllowed -and [string]$evidence.physicalBringHere -ceq 'deferred' -and -not [bool]$evidence.publicationAuthorizationChanged) 'Live-host preflight changed or overstated publication state.'

    $wrongRequest = [IO.File]::ReadAllText($requestPath) | ConvertFrom-Json
    $wrongFingerprint = 'SHA256:' + ([Convert]::ToBase64String([byte[]](1..32))).TrimEnd('=')
    Require ($wrongFingerprint -cne $hostFingerprint) 'Wrong-host-key fixture unexpectedly equals the live host key.'
    $wrongRequest.host.sshHostKeySha256 = $wrongFingerprint
    Write-Utf8 $wrongRequestPath ($wrongRequest | ConvertTo-Json -Depth 8)
    Expect-Failure {
        & (Join-Path $bundle 'preflight-closed-alpha-live-host.ps1') `
            -BundleDirectory $bundle -RequestPath $wrongRequestPath -SshPrivateKeyPath $clientKey `
            -EvidencePath (Join-Path $work 'wrong-host-key-evidence.json')
    } 'mismatched observed SSH host key is rejected' 'Observed SSH host key does not match the request-bound fingerprint.'

    Invoke-Native -Tool 'sudo' -Arguments @('chmod', '0644', '/etc/steward/backend.env') -Context 'Relax protected environment mode for refusal test'
    try {
        Expect-Failure {
            & (Join-Path $bundle 'preflight-closed-alpha-live-host.ps1') `
                -BundleDirectory $bundle -RequestPath $requestPath -SshPrivateKeyPath $clientKey `
                -EvidencePath $modeFailureEvidencePath
        } 'world-readable protected environment is rejected' 'protected backend environment mode is not 0600'
    }
    finally {
        Invoke-Native -Tool 'sudo' -Arguments @('chmod', '0600', '/etc/steward/backend.env') -Context 'Restore protected environment mode'
    }

    Write-Host '[OK] Live-host preflight integration rehearsal and refusal cases passed.'

    if ($RunPlanStaging.IsPresent) {
        $staleEvidence = [IO.File]::ReadAllText($preflightEvidencePath) | ConvertFrom-Json
        $staleEvidence.observedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-20).ToString('O')
        Write-Utf8 $stalePreflightPath ($staleEvidence | ConvertTo-Json -Depth 8)
        Expect-Failure {
            & (Join-Path $bundle 'stage-closed-alpha-live-deployment-plan.ps1') `
                -BundleDirectory $bundle -RequestPath $requestPath -PreflightEvidencePath $stalePreflightPath `
                -SshPrivateKeyPath $clientKey -EvidencePath (Join-Path $work 'stale-staging.json')
        } 'stale preflight evidence is rejected before transfer' 'Preflight evidence is stale; run the live-host preflight again before transfer.'

        Invoke-Native -Tool 'sudo' -Arguments @('mkdir', '-p', $remoteReleaseDirectory) -Context 'Introduce post-preflight target drift'
        try {
            Expect-Failure {
                & (Join-Path $bundle 'stage-closed-alpha-live-deployment-plan.ps1') `
                    -BundleDirectory $bundle -RequestPath $requestPath -PreflightEvidencePath $preflightEvidencePath `
                    -SshPrivateKeyPath $clientKey -EvidencePath (Join-Path $work 'drift-staging.json')
            } 'post-preflight target drift is rejected before transfer' 'Fresh live-host recheck failed before candidate transfer.'
        }
        finally {
            Invoke-Native -Tool 'sudo' -Arguments @('rm', '-rf', $remoteReleaseDirectory) -Context 'Remove post-preflight target drift fixture'
        }

        & (Join-Path $bundle 'stage-closed-alpha-live-deployment-plan.ps1') `
            -BundleDirectory $bundle `
            -RequestPath $requestPath `
            -PreflightEvidencePath $preflightEvidencePath `
            -SshPrivateKeyPath $clientKey `
            -EvidencePath $stagingEvidencePath

        Require ([IO.File]::Exists($stagingEvidencePath)) 'Live plan staging did not write evidence.'
        $stagingText = [IO.File]::ReadAllText($stagingEvidencePath)
        Require ($stagingText.Length -gt 0 -and $stagingText.Length -le 64KB) 'Live plan staging evidence is empty or too large.'
        $staging = $stagingText | ConvertFrom-Json
        Require ([string]$staging.documentType -ceq 'steward.closed-alpha-live-plan-staging' -and [int]$staging.schemaVersion -eq 1) 'Live plan staging evidence identity is invalid.'
        Require ([string]$staging.releaseCommitSha -ceq $releaseCommit -and [string]$staging.apiHost -ceq $apiHost -and [string]$staging.resolvedPublicIpv4 -ceq $publicIpv4) 'Live plan staging evidence lost the release/host identity.'
        Require ([bool]$staging.preflightFresh -and [bool]$staging.dnsReverified -and [bool]$staging.sshHostKeyReverified) 'Live plan staging did not revalidate fresh host identity.'
        Require ([bool]$staging.candidateTransferred -and [bool]$staging.remoteBundleVerified -and [bool]$staging.deploymentPlanGenerated) 'Live plan staging did not complete the intended mutation boundary.'
        Require ([bool]$staging.protectedEnvironmentReadOnHost -and -not [bool]$staging.protectedEnvironmentTransferred -and -not [bool]$staging.deploymentPlanSecretValuesCopied) 'Live plan staging crossed the protected-value boundary.'
        Require (-not [bool]$staging.backendImagePreexisting -and -not [bool]$staging.backendImageLoaded -and -not [bool]$staging.backendContainerStarted -and -not [bool]$staging.deploymentStarted) 'Live plan staging crossed the deployment boundary.'
        Require ([bool]$staging.backendPort8080Clear -and -not [bool]$staging.caddyModified) 'Live plan staging changed runtime/Caddy state.'
        Require (-not [bool]$staging.publishAllowed -and [string]$staging.physicalBringHere -ceq 'deferred' -and -not [bool]$staging.publicationAuthorizationChanged) 'Live plan staging changed publication state.'
        Require ([IO.Directory]::Exists($remoteReleaseDirectory) -and [IO.Directory]::Exists($remotePlanDirectory)) 'Live plan staging did not materialize the exact remote release and plan directories.'
        Require ([IO.File]::Exists((Join-Path $remotePlanDirectory 'deployment-plan.json'))) 'Remote deployment-plan.json is missing.'
        Require ([IO.File]::Exists((Join-Path $remotePlanDirectory 'Caddyfile'))) 'Generated Caddyfile is missing.'
        Require ([IO.File]::Exists((Join-Path $remotePlanDirectory 'deploy-exact-candidate.sh'))) 'Generated deploy script is missing.'
        Require ([IO.File]::Exists((Join-Path $remotePlanDirectory 'verify-public-https.sh'))) 'Generated public verifier is missing.'

        $generatedText = @(
            [IO.File]::ReadAllText((Join-Path $remotePlanDirectory 'deployment-plan.json')),
            [IO.File]::ReadAllText((Join-Path $remotePlanDirectory 'Caddyfile')),
            [IO.File]::ReadAllText((Join-Path $remotePlanDirectory 'deploy-exact-candidate.sh')),
            [IO.File]::ReadAllText((Join-Path $remotePlanDirectory 'verify-public-https.sh'))
        ) -join "`n"
        foreach ($protectedValue in @($syntheticPostgresPassword, $syntheticStorageAccessKey, $syntheticStorageSecretKey)) {
            Require (-not $generatedText.Contains($protectedValue, [StringComparison]::Ordinal)) 'Generated deployment-plan files copied a protected synthetic value.'
        }

        & docker image inspect ('sha256:' + ('b' * 64)) *> $null
        Require ($LASTEXITCODE -ne 0) 'Live plan staging unexpectedly loaded the synthetic backend image.'
        & docker container inspect steward-backend *> $null
        Require ($LASTEXITCODE -ne 0) 'Live plan staging unexpectedly created steward-backend.'
        Require (@(& ss -ltnH 'sport = :8080' 2>$null).Count -eq 0) 'Live plan staging unexpectedly occupied port 8080.'
        Require (-not [IO.File]::Exists('/etc/caddy/Caddyfile')) 'Live plan staging unexpectedly materialized the live Caddyfile.'
        $envMode = (Invoke-Native -Tool 'sudo' -Arguments @('stat', '-Lc', '%a', '/etc/steward/backend.env') -Context 'Verify protected environment mode after staging' -Capture).Trim()
        $envUid = (Invoke-Native -Tool 'sudo' -Arguments @('stat', '-Lc', '%u', '/etc/steward/backend.env') -Context 'Verify protected environment owner after staging' -Capture).Trim()
        Require ($envMode -ceq '600' -and $envUid -ceq '0') 'Live plan staging changed protected environment metadata.'

        Write-Host '[OK] Fresh preflight gated exact candidate transfer and live deployment-plan generation without deployment.'
    }
}
finally {
    & sudo rm -rf $remoteReleaseDirectory $remotePlanDirectory *> $null
    if ($sshdStarted -and [IO.File]::Exists($sshdPid)) {
        $pidText = [IO.File]::ReadAllText($sshdPid).Trim()
        $pidValue = 0
        if ([int]::TryParse($pidText, [ref]$pidValue) -and $pidValue -gt 1) {
            & sudo kill $pidValue *> $null
        }
    }
    & sudo rm -f /etc/sudoers.d/steward-live-host-preflight *> $null
    & sudo rm -f /etc/steward/backend.env *> $null
    if ($createdCaddyStub) {
        & sudo rm -f /usr/local/bin/caddy *> $null
    }
    if ($userCreated) {
        & sudo userdel --remove steward *> $null
    }
    if ($aliasAdded) {
        & sudo ip address del "$publicIpv4/32" dev lo *> $null
    }
    if ($hostModified -and [IO.File]::Exists($hostsBackup)) {
        & sudo cp $hostsBackup /etc/hosts *> $null
    }
    if ([IO.Directory]::Exists($work)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
