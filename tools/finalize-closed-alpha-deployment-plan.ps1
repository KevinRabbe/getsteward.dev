[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-StrictProperties([object]$Value) {
    Require ($null -ne $Value) 'Expected a JSON object but found null.'
    return @($Value.PSObject.Properties.Name | Sort-Object)
}

function Require-ExactProperties([object]$Value,[string[]]$Expected,[string]$Context) {
    $actual = @(Get-StrictProperties $Value)
    $expectedSorted = @($Expected | Sort-Object)
    Require ($actual.Count -eq $expectedSorted.Count) "$Context has an unexpected property count."
    Require (@(Compare-Object $actual $expectedSorted).Count -eq 0) "$Context contains missing or unexpected properties."
}

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'

$plannerSource = Join-Path $PSScriptRoot 'prepare-closed-alpha-deployment.ps1'
$livePlannerSource = Join-Path $PSScriptRoot 'prepare-closed-alpha-live-deployment.ps1'
$liveHostPreflightSource = Join-Path $PSScriptRoot 'preflight-closed-alpha-live-host.ps1'
$livePlanStagerSource = Join-Path $PSScriptRoot 'stage-closed-alpha-live-deployment-plan.ps1'
$liveBackendDeploymentSource = Join-Path $PSScriptRoot 'deploy-closed-alpha-live-backend.ps1'
$liveIngressActivationSource = Join-Path $PSScriptRoot 'activate-closed-alpha-live-ingress.ps1'
$externalIngressVerifierSource = Join-Path $PSScriptRoot 'verify-closed-alpha-external-ingress.ps1'
foreach ($source in @(
    $plannerSource,$livePlannerSource,$liveHostPreflightSource,$livePlanStagerSource,
    $liveBackendDeploymentSource,$liveIngressActivationSource,$externalIngressVerifierSource)) {
    Require ([IO.File]::Exists($source)) "Qualified deployment tool is missing: $source"
    $sourceItem = Get-Item -LiteralPath $source
    Require ($null -eq $sourceItem.LinkType) "Qualified deployment tool cannot be a symbolic link: $source"
    Require ($sourceItem.Length -gt 0 -and $sourceItem.Length -le 2MB) "Qualified deployment tool is empty or exceeds the 2 MiB bound: $source"
}

& $verifierPath -BundleDirectory $bundleRoot

$manifestText = [IO.File]::ReadAllText($manifestPath)
Require ($manifestText.Length -le 4MB) 'Closed-alpha release manifest exceeds the 4 MiB finalization bound.'
$manifest = $manifestText | ConvertFrom-Json
Require-ExactProperties $manifest @(
    'artifacts','builtAtUtc','channel','commitSha','deployment','documentType',
    'publishAuthorization','schemaVersion','version'
) 'Release manifest'
Require ([string]$manifest.documentType -ceq 'steward.closed-alpha-release-candidate') 'Unexpected release-manifest document type.'
Require ([int]$manifest.schemaVersion -eq 1) 'Unsupported release-manifest schema version.'
Require ([string]$manifest.channel -ceq 'closed-alpha') 'Release channel must be closed-alpha.'

$immutableIdentityBefore = [ordered]@{
    documentType = [string]$manifest.documentType
    schemaVersion = [int]$manifest.schemaVersion
    channel = [string]$manifest.channel
    version = [string]$manifest.version
    commitSha = [string]$manifest.commitSha
    builtAtUtc = [string]$manifest.builtAtUtc
    deployment = $manifest.deployment
    publishAuthorization = $manifest.publishAuthorization
} | ConvertTo-Json -Compress -Depth 8

$plannerDestination = Join-Path $bundleRoot 'prepare-closed-alpha-deployment.ps1'
$livePlannerDestination = Join-Path $bundleRoot 'prepare-closed-alpha-live-deployment.ps1'
$liveHostPreflightDestination = Join-Path $bundleRoot 'preflight-closed-alpha-live-host.ps1'
$livePlanStagerDestination = Join-Path $bundleRoot 'stage-closed-alpha-live-deployment-plan.ps1'
$liveBackendDeploymentDestination = Join-Path $bundleRoot 'deploy-closed-alpha-live-backend.ps1'
$liveIngressActivationDestination = Join-Path $bundleRoot 'activate-closed-alpha-live-ingress.ps1'
$externalIngressVerifierDestination = Join-Path $bundleRoot 'verify-closed-alpha-external-ingress.ps1'
Copy-Item -LiteralPath $plannerSource -Destination $plannerDestination -Force
Copy-Item -LiteralPath $livePlannerSource -Destination $livePlannerDestination -Force
Copy-Item -LiteralPath $liveHostPreflightSource -Destination $liveHostPreflightDestination -Force
Copy-Item -LiteralPath $livePlanStagerSource -Destination $livePlanStagerDestination -Force
Copy-Item -LiteralPath $liveBackendDeploymentSource -Destination $liveBackendDeploymentDestination -Force
Copy-Item -LiteralPath $liveIngressActivationSource -Destination $liveIngressActivationDestination -Force
Copy-Item -LiteralPath $externalIngressVerifierSource -Destination $externalIngressVerifierDestination -Force

$backendDirectory = Join-Path $bundleRoot 'backend'
Require ([IO.Directory]::Exists($backendDirectory)) 'The release bundle is missing its backend directory.'
$environmentExamplePath = Join-Path $backendDirectory 'deployment.env.example'
$environmentExample = @'
# Safe World closed-alpha backend environment template.
# Copy this OUTSIDE the release bundle, replace every <required-...> value,
# protect the resulting file as a secret, and never add it back to the bundle.
PORT=8080
ConnectionStrings__Steward=<required-unquoted-postgresql-17-connection-string-with-SSL-Mode-VerifyFull>
ObjectStorage__ServiceUrl=<required-https-s3-compatible-service-url>
ObjectStorage__AuthenticationRegion=<required-region>
ObjectStorage__BucketName=<required-private-bucket>
ObjectStorage__AccessKeyId=<required-secret>
ObjectStorage__SecretAccessKey=<required-secret>
ObjectStorage__ForcePathStyle=false
FriendsBuild__Enabled=true
FriendsBuild__Identities__0__Id=<required-opaque-friend-id>
FriendsBuild__Identities__0__DisplayName=<required-friend-display-name>
FriendsBuild__Identities__0__CredentialSha256=<required-64-hex-bootstrap-credential-digest>
ReverseProxy__KnownProxyIp=127.0.0.1
Cleanup__IntervalMinutes=15
Cleanup__VerifiedCandidateRetentionDays=7
Cleanup__BatchSize=100
'@
[IO.File]::WriteAllText($environmentExamplePath,$environmentExample.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))

$deploymentReadmePath = Join-Path $bundleRoot 'DEPLOYMENT-PLAN-README.txt'
$deploymentReadme = @'
SAFE WORLD CLOSED-ALPHA DEPLOYMENT PLAN
=======================================

This candidate contains seven byte-manifested deployment-boundary tools:

  prepare-closed-alpha-deployment.ps1
  prepare-closed-alpha-live-deployment.ps1
  preflight-closed-alpha-live-host.ps1
  stage-closed-alpha-live-deployment-plan.ps1
  deploy-closed-alpha-live-backend.ps1
  activate-closed-alpha-live-ingress.ps1
  verify-closed-alpha-external-ingress.ps1

Protected backend-environment values remain on the live host. Request and
preflight evidence contain no protected values; live plan staging allows only the
bundled deployment planner to read the protected file on that host.

Operator sequence
-----------------

1. Verify this candidate:

   pwsh ./verify-closed-alpha-release.ps1 -BundleDirectory .

2. Bind the exact candidate to the intended first live host without contacting it:

   pwsh ./prepare-closed-alpha-live-deployment.ps1 \
     -BundleDirectory . \
     -ExpectedPublicIpv4 <public-ipv4> \
     -SshHostKeySha256 SHA256:<pinned-ed25519-host-key-fingerprint> \
     -OutputPath ../live-deployment-request.json

3. Put the completed backend environment on the live host only at
   /etc/steward/backend.env. It must be a regular non-symlink file, root-owned,
   mode 0600. Provision Caddy as a managed service with a valid root:root mode
   0644 baseline /etc/caddy/Caddyfile before preflight; that baseline is the
   fail-closed rollback target for later ingress activation.

4. Run the byte-manifested read-only host preflight:

   pwsh ./preflight-closed-alpha-live-host.ps1 \
     -BundleDirectory . \
     -RequestPath ../live-deployment-request.json \
     -SshPrivateKeyPath <operator-private-key> \
     -EvidencePath ../live-host-preflight.json

5. Within 15 minutes, stage the exact candidate and generate the deployment plan:

   pwsh ./stage-closed-alpha-live-deployment-plan.ps1 \
     -BundleDirectory . \
     -RequestPath ../live-deployment-request.json \
     -PreflightEvidencePath ../live-host-preflight.json \
     -SshPrivateKeyPath <operator-private-key> \
     -EvidencePath ../live-plan-staging.json

6. Within 15 minutes, deploy only the exact backend runtime:

   pwsh ./deploy-closed-alpha-live-backend.ps1 \
     -BundleDirectory . \
     -RequestPath ../live-deployment-request.json \
     -StagingEvidencePath ../live-plan-staging.json \
     -SshPrivateKeyPath <operator-private-key> \
     -EvidencePath ../live-backend-deployment.json

7. Within 15 minutes of backend deployment, activate the exact generated Caddyfile:

   pwsh ./activate-closed-alpha-live-ingress.ps1 \
     -BundleDirectory . \
     -RequestPath ../live-deployment-request.json \
     -BackendDeploymentEvidencePath ../live-backend-deployment.json \
     -SshPrivateKeyPath <operator-private-key> \
     -EvidencePath ../live-ingress-activation.json

   This atomically activates the byte-bound generated Caddyfile while preserving
   the exact backend. It still does not claim public certificate trust or that
   backend port 8080 is externally unreachable.

8. Within 15 minutes, from a separate external observer with NO SSH credentials,
   run the byte-manifested read-only acceptance verifier:

   pwsh ./verify-closed-alpha-external-ingress.ps1 \
     -BundleDirectory . \
     -RequestPath ../live-deployment-request.json \
     -IngressActivationEvidencePath ../live-ingress-activation.json \
     -EvidencePath ../external-ingress-acceptance.json

   The verifier re-resolves the bound hostname, disables curl configuration-file,
   proxy and custom-CA environment influence, connects directly to the bound IPv4
   while retaining hostname/SNI, requires normal system-trust certificate
   validation and HTTP 200 from /health/live and /health/ready, then separately
   proves TCP port 8080 is connection-refused or timed out from the same observer.
   It does not use SSH, inspect protected environment values, or mutate the host.

None of these preparation, staging, backend-deployment, ingress-activation or
external-acceptance steps authorizes publication. Physical PC A -> PC B -> PC A
Bring Here remains controlled only by release-manifest.json and the strict release
verifier.
'@
[IO.File]::WriteAllText($deploymentReadmePath,$deploymentReadme.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))

$artifactEntries = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
    Where-Object {
        -not [string]::Equals([IO.Path]::GetFullPath($_.FullName),[IO.Path]::GetFullPath($manifestPath),[StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object FullName |
    ForEach-Object {
        Require ($null -eq $_.LinkType) "Release artifact '$($_.FullName)' cannot be a symbolic link."
        [ordered]@{
            path = [IO.Path]::GetRelativePath($bundleRoot,$_.FullName).Replace('\','/')
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    })
Require ($artifactEntries.Count -gt 0 -and $artifactEntries.Count -le 10000) 'Finalized release artifact count is outside the supported bound.'
$manifest.artifacts = $artifactEntries

$immutableIdentityAfter = [ordered]@{
    documentType = [string]$manifest.documentType
    schemaVersion = [int]$manifest.schemaVersion
    channel = [string]$manifest.channel
    version = [string]$manifest.version
    commitSha = [string]$manifest.commitSha
    builtAtUtc = [string]$manifest.builtAtUtc
    deployment = $manifest.deployment
    publishAuthorization = $manifest.publishAuthorization
} | ConvertTo-Json -Compress -Depth 8
Require ([string]::Equals($immutableIdentityBefore,$immutableIdentityAfter,[StringComparison]::Ordinal)) 'Deployment-plan finalization changed immutable release identity or publish authorization.'

[IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
& $verifierPath -BundleDirectory $bundleRoot

$finalManifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$toolBindings = @(
    @{ Path='prepare-closed-alpha-deployment.ps1'; Destination=$plannerDestination; Label='deployment planner' },
    @{ Path='prepare-closed-alpha-live-deployment.ps1'; Destination=$livePlannerDestination; Label='live deployment request planner' },
    @{ Path='preflight-closed-alpha-live-host.ps1'; Destination=$liveHostPreflightDestination; Label='live-host preflight' },
    @{ Path='stage-closed-alpha-live-deployment-plan.ps1'; Destination=$livePlanStagerDestination; Label='live plan stager' },
    @{ Path='deploy-closed-alpha-live-backend.ps1'; Destination=$liveBackendDeploymentDestination; Label='live backend executor' },
    @{ Path='activate-closed-alpha-live-ingress.ps1'; Destination=$liveIngressActivationDestination; Label='live ingress activator' },
    @{ Path='verify-closed-alpha-external-ingress.ps1'; Destination=$externalIngressVerifierDestination; Label='external ingress acceptance verifier' }
)
$toolHashes = @{}
foreach ($binding in $toolBindings) {
    $entries = @($finalManifest.artifacts | Where-Object { [string]$_.path -ceq [string]$binding.Path })
    Require ($entries.Count -eq 1) "Finalized release manifest does not contain exactly one $($binding.Label)."
    $expectedHash = (Get-FileHash -LiteralPath $binding.Destination -Algorithm SHA256).Hash
    Require ([string]$entries[0].sha256 -ceq $expectedHash) "Finalized release manifest does not bind the $($binding.Label) bytes."
    $toolHashes[$binding.Path] = $expectedHash
}

Write-Host
Write-Host '[OK] Closed-alpha candidate now contains its byte-exact deployment-boundary tools.'
Write-Host "  Bundle: $bundleRoot"
Write-Host "  Deployment planner SHA-256: $($toolHashes['prepare-closed-alpha-deployment.ps1'])"
Write-Host "  Live request planner SHA-256: $($toolHashes['prepare-closed-alpha-live-deployment.ps1'])"
Write-Host "  Live-host preflight SHA-256: $($toolHashes['preflight-closed-alpha-live-host.ps1'])"
Write-Host "  Live plan stager SHA-256: $($toolHashes['stage-closed-alpha-live-deployment-plan.ps1'])"
Write-Host "  Live backend executor SHA-256: $($toolHashes['deploy-closed-alpha-live-backend.ps1'])"
Write-Host "  Live ingress activator SHA-256: $($toolHashes['activate-closed-alpha-live-ingress.ps1'])"
Write-Host "  External ingress verifier SHA-256: $($toolHashes['verify-closed-alpha-external-ingress.ps1'])"
Write-Host "  Artifacts: $($finalManifest.artifacts.Count)"
Write-Host '  Protected values copied into candidate: no'
Write-Host '  Publish authorization changed: no'
