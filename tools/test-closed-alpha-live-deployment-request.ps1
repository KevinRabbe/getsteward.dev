[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Expect-Failure([scriptblock]$Action, [string]$Context) {
    $failed = $false
    try {
        & $Action
    }
    catch {
        $failed = $true
        Write-Host "[EXPECTED] ${Context}: $($_.Exception.Message)"
    }
    Require $failed "Expected failure did not occur: $Context"
}

function Write-Utf8([string]$Path, [string]$Content) {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText(
        $Path,
        $Content.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false))
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("steward-live-request-test-" + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $work 'bundle'
[IO.Directory]::CreateDirectory((Join-Path $bundle 'desktop')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $bundle 'backend')) | Out-Null

try {
    Copy-Item (Join-Path $PSScriptRoot 'verify-closed-alpha-release.ps1') (Join-Path $bundle 'verify-closed-alpha-release.ps1')
    Copy-Item (Join-Path $PSScriptRoot 'prepare-closed-alpha-deployment.ps1') (Join-Path $bundle 'prepare-closed-alpha-deployment.ps1')
    Copy-Item (Join-Path $PSScriptRoot 'prepare-closed-alpha-live-deployment.ps1') (Join-Path $bundle 'prepare-closed-alpha-live-deployment.ps1')

    Write-Utf8 (Join-Path $bundle 'desktop/friends.zip') 'synthetic desktop bytes'
    Write-Utf8 (Join-Path $bundle 'backend/steward-backend.tar') 'synthetic backend image bytes'
    Write-Utf8 (Join-Path $bundle 'backend/deployment.env.example') 'PORT=8080'
    Write-Utf8 (Join-Path $bundle 'CLOSED-ALPHA-OPERATOR-RUNBOOK.txt') 'synthetic operator runbook'
    Write-Utf8 (Join-Path $bundle 'RELEASE-STATUS.txt') 'synthetic release status'

    $artifactEntries = @(Get-ChildItem -LiteralPath $bundle -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($bundle, $_.FullName).Replace('\', '/')
                byteSize = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            }
        })

    $imageId = 'sha256:' + ((('a') * 64) -join '')
    $manifest = [ordered]@{
        documentType = 'steward.closed-alpha-release-candidate'
        schemaVersion = 1
        channel = 'closed-alpha'
        version = '2.0.0-live-contract'
        commitSha = '1111111111111111111111111111111111111111'
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        deployment = [ordered]@{
            apiBaseUrl = 'https://alpha.getsteward.dev/'
            authenticationMode = 'friends-build'
            backendImageId = $imageId
            backendImageTag = 'steward-backend:live-contract'
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
            reason = 'Synthetic contract candidate; physical Bring Here remains deferred.'
        }
        artifacts = $artifactEntries
    }
    $manifestPath = Join-Path $bundle 'release-manifest.json'
    Write-Utf8 $manifestPath ($manifest | ConvertTo-Json -Depth 8)

    $bundledPlanner = Join-Path $bundle 'prepare-closed-alpha-live-deployment.ps1'
    $requestPath = Join-Path $work 'live-deployment-request.json'
    $fingerprint = 'SHA256:' + ([Convert]::ToBase64String([byte[]]::new(32)).TrimEnd('='))

    & $bundledPlanner `
        -BundleDirectory $bundle `
        -ExpectedPublicIpv4 '1.1.1.1' `
        -SshHostKeySha256 $fingerprint `
        -OutputPath $requestPath

    Require ([IO.File]::Exists($requestPath)) 'Live deployment request was not written.'
    $requestText = [IO.File]::ReadAllText($requestPath)
    Require ($requestText.Length -gt 0 -and $requestText.Length -le 64KB) 'Live deployment request is empty or too large.'
    $request = $requestText | ConvertFrom-Json
    Require ([string]$request.documentType -ceq 'steward.closed-alpha-live-deployment-request') 'Live deployment request has the wrong document type.'
    Require ([int]$request.schemaVersion -eq 1) 'Live deployment request has the wrong schema version.'
    Require ([string]$request.release.commitSha -ceq [string]$manifest.commitSha) 'Live deployment request lost the release commit.'
    Require ([string]$request.release.apiBaseUrl -ceq 'https://alpha.getsteward.dev/') 'Live deployment request changed the API coordinate.'
    Require ([string]$request.host.apiHost -ceq 'alpha.getsteward.dev') 'Live deployment request changed the API host.'
    Require ([string]$request.host.sshHost -ceq 'alpha.getsteward.dev') 'First live topology must use the API hostname as the SSH host.'
    Require ([string]$request.host.expectedPublicIpv4 -ceq '1.1.1.1') 'Live deployment request changed the expected public IPv4.'
    Require ([string]$request.host.remoteEnvironmentPath -ceq '/etc/steward/backend.env') 'Live deployment request changed the protected environment path.'
    Require ([string]$request.host.remoteReleaseDirectory -ceq '/srv/steward/releases/1111111111111111111111111111111111111111') 'Live deployment request changed the release directory.'
    Require ([int]$request.network.publicTlsPort -eq 443) 'Live deployment request changed the TLS port.'
    Require ([int]$request.network.forbiddenPublicBackendPort -eq 8080) 'Live deployment request did not forbid public backend port 8080.'
    Require ([string]$request.network.caddyUpstream -ceq '127.0.0.1:8080') 'Live deployment request changed the one-proxy upstream.'
    Require ([string]$request.network.backendKnownProxyIp -ceq '127.0.0.1') 'Live deployment request widened proxy trust.'
    Require ([bool]$request.executionContract.plannerRunsOnLiveHost) 'Deployment planner must run on the live host beside its protected environment.'
    Require ([bool]$request.executionContract.environmentNeverLeavesLiveHost) 'Protected environment must remain on the live host.'
    Require (-not [bool]$request.secretBoundary.secretValuesPresent) 'Live deployment request cannot contain protected values.'
    Require ([string]$request.secretBoundary.requiredRemoteMode -ceq '0600') 'Live protected environment mode must remain 0600.'
    Require ([string]$request.secretBoundary.requiredRemoteOwner -ceq 'root') 'Live protected environment owner must remain root.'
    Require (-not [bool]$request.publication.requestAuthorizesPublication) 'Live deployment request cannot authorize publication.'
    Require (-not [bool]$request.publication.authorizationChanged) 'Live deployment request cannot change publication state.'

    $originalManifestText = [IO.File]::ReadAllText($manifestPath)

    Expect-Failure {
        & $bundledPlanner -BundleDirectory $bundle -ExpectedPublicIpv4 '10.0.0.8' -SshHostKeySha256 $fingerprint -OutputPath (Join-Path $work 'private-ip.json')
    } 'private IPv4 is rejected'

    Expect-Failure {
        & $bundledPlanner -BundleDirectory $bundle -ExpectedPublicIpv4 '192.0.2.8' -SshHostKeySha256 $fingerprint -OutputPath (Join-Path $work 'documentation-ip.json')
    } 'documentation IPv4 is rejected'

    Expect-Failure {
        & $bundledPlanner -BundleDirectory $bundle -ExpectedPublicIpv4 '1.1.1.1' -SshHostKeySha256 'SHA256:not-a-real-fingerprint' -OutputPath (Join-Path $work 'bad-host-key.json')
    } 'malformed SSH host fingerprint is rejected'

    Expect-Failure {
        & $bundledPlanner -BundleDirectory $bundle -ExpectedPublicIpv4 '1.1.1.1' -SshHostKeySha256 $fingerprint -OutputPath (Join-Path $bundle 'forbidden-request.json')
    } 'request cannot mutate immutable bundle'

    $reservedManifest = $originalManifestText | ConvertFrom-Json
    $reservedManifest.deployment.apiBaseUrl = 'https://closed-alpha.example.invalid/'
    Write-Utf8 $manifestPath ($reservedManifest | ConvertTo-Json -Depth 8)
    Expect-Failure {
        & $bundledPlanner -BundleDirectory $bundle -ExpectedPublicIpv4 '1.1.1.1' -SshHostKeySha256 $fingerprint -OutputPath (Join-Path $work 'reserved-host.json')
    } 'reserved API hostname is rejected'
    Write-Utf8 $manifestPath $originalManifestText

    Expect-Failure {
        & (Join-Path $PSScriptRoot 'prepare-closed-alpha-live-deployment.ps1') -BundleDirectory $bundle -ExpectedPublicIpv4 '1.1.1.1' -SshHostKeySha256 $fingerprint -OutputPath (Join-Path $work 'repository-copy.json')
    } 'planner must execute from byte-manifested bundle'

    & (Join-Path $bundle 'verify-closed-alpha-release.ps1') -BundleDirectory $bundle
    Write-Host '[OK] Live deployment request contract and refusal cases passed.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
