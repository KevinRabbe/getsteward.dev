[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Get-StrictProperties([object]$Value) {
    Require ($null -ne $Value) 'Expected a JSON object but found null.'
    return @($Value.PSObject.Properties.Name | Sort-Object)
}

function Require-ExactProperties([object]$Value, [string[]]$Expected, [string]$Context) {
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
foreach ($source in @($plannerSource, $livePlannerSource, $liveHostPreflightSource)) {
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
    'artifacts', 'builtAtUtc', 'channel', 'commitSha', 'deployment', 'documentType',
    'publishAuthorization', 'schemaVersion', 'version'
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
Copy-Item -LiteralPath $plannerSource -Destination $plannerDestination -Force
Copy-Item -LiteralPath $livePlannerSource -Destination $livePlannerDestination -Force
Copy-Item -LiteralPath $liveHostPreflightSource -Destination $liveHostPreflightDestination -Force

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
[IO.File]::WriteAllText(
    $environmentExamplePath,
    $environmentExample.Replace("`r`n", "`n"),
    [Text.UTF8Encoding]::new($false))

$deploymentReadmePath = Join-Path $bundleRoot 'DEPLOYMENT-PLAN-README.txt'
$deploymentReadme = @'
SAFE WORLD CLOSED-ALPHA DEPLOYMENT PLAN
=======================================

This candidate contains three byte-manifested deployment-boundary tools:

  prepare-closed-alpha-deployment.ps1
  prepare-closed-alpha-live-deployment.ps1
  preflight-closed-alpha-live-host.ps1

They do not copy protected backend-environment values into release, request, or
preflight evidence.

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

   The first acceptance topology uses the release API hostname as the SSH host.
   The request binds one globally routable IPv4, one canonical pinned SSH host
   key, fixed remote paths, linux/amd64, Caddy -> 127.0.0.1:8080, and the rule
   that backend port 8080 must not be public.

3. Put the completed backend environment on the live host only at:

   /etc/steward/backend.env

   It must be a regular non-symlink file, root-owned, mode 0600. The first release
   requires PostgreSQL 17 with SSL Mode=VerifyFull and Trust Server Certificate=false,
   private HTTPS S3-compatible storage, and the configured Friends Build identities.

4. Before transferring the candidate, run the byte-manifested read-only preflight:

   pwsh ./preflight-closed-alpha-live-host.ps1 \
     -BundleDirectory . \
     -RequestPath ../live-deployment-request.json \
     -SshPrivateKeyPath <operator-private-key> \
     -EvidencePath ../live-host-preflight.json

   It requires exactly one DNS address equal to the request-bound IPv4, observes
   and compares the Ed25519 SSH host key before trusted SSH, uses strict known-host
   checking, proves Linux/x86_64, required tools, non-interactive sudo, Docker
   server access, root/0600 environment metadata without reading its contents,
   empty release/plan target paths, no existing steward-backend container, and
   no listener on backend port 8080. It transfers no candidate and starts nothing.

5. Only after that preflight passes, stage the exact candidate on the host and run
   the bundled deployment planner there beside the protected environment:

   pwsh ./prepare-closed-alpha-deployment.ps1 \
     -BundleDirectory . \
     -EnvironmentFile /etc/steward/backend.env \
     -OutputDirectory /srv/steward/deployment-plans/<release-commit> \
     -HostEnvironmentPath /etc/steward/backend.env \
     -RequireDeployable

6. Install the generated Caddyfile as /etc/caddy/Caddyfile. Keep Backend.Api on
   port 8080 behind 127.0.0.1 and keep port 8080 non-public in cloud and host policy.

7. Run on the Linux host:

   bash /srv/steward/deployment-plans/<release-commit>/deploy-exact-candidate.sh \
     /srv/steward/releases/<release-commit>

8. After DNS and public TLS are active, verify without certificate bypass:

   bash /srv/steward/deployment-plans/<release-commit>/verify-public-https.sh

The live request and live-host preflight do not authorize publication. Physical
PC A -> PC B -> PC A Bring Here remains controlled only by release-manifest.json
and the strict release verifier.
'@
[IO.File]::WriteAllText(
    $deploymentReadmePath,
    $deploymentReadme.Replace("`r`n", "`n"),
    [Text.UTF8Encoding]::new($false))

$artifactEntries = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
    Where-Object {
        -not [string]::Equals(
            [IO.Path]::GetFullPath($_.FullName),
            [IO.Path]::GetFullPath($manifestPath),
            [StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object FullName |
    ForEach-Object {
        Require ($null -eq $_.LinkType) "Release artifact '$($_.FullName)' cannot be a symbolic link."
        [ordered]@{
            path = [IO.Path]::GetRelativePath($bundleRoot, $_.FullName).Replace('\', '/')
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
Require ([string]::Equals($immutableIdentityBefore, $immutableIdentityAfter, [StringComparison]::Ordinal)) 'Deployment-plan finalization changed immutable release identity or publish authorization.'

[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

& $verifierPath -BundleDirectory $bundleRoot

$finalManifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$plannerEntries = @($finalManifest.artifacts | Where-Object { [string]$_.path -ceq 'prepare-closed-alpha-deployment.ps1' })
$livePlannerEntries = @($finalManifest.artifacts | Where-Object { [string]$_.path -ceq 'prepare-closed-alpha-live-deployment.ps1' })
$preflightEntries = @($finalManifest.artifacts | Where-Object { [string]$_.path -ceq 'preflight-closed-alpha-live-host.ps1' })
Require ($plannerEntries.Count -eq 1) 'Finalized release manifest does not contain exactly one deployment planner.'
Require ($livePlannerEntries.Count -eq 1) 'Finalized release manifest does not contain exactly one live deployment request planner.'
Require ($preflightEntries.Count -eq 1) 'Finalized release manifest does not contain exactly one live-host preflight tool.'
$expectedPlannerHash = (Get-FileHash -LiteralPath $plannerDestination -Algorithm SHA256).Hash
$expectedLivePlannerHash = (Get-FileHash -LiteralPath $livePlannerDestination -Algorithm SHA256).Hash
$expectedPreflightHash = (Get-FileHash -LiteralPath $liveHostPreflightDestination -Algorithm SHA256).Hash
Require ([string]$plannerEntries[0].sha256 -ceq $expectedPlannerHash) 'Finalized release manifest does not bind the deployment planner bytes.'
Require ([string]$livePlannerEntries[0].sha256 -ceq $expectedLivePlannerHash) 'Finalized release manifest does not bind the live deployment request planner bytes.'
Require ([string]$preflightEntries[0].sha256 -ceq $expectedPreflightHash) 'Finalized release manifest does not bind the live-host preflight bytes.'

Write-Host
Write-Host '[OK] Closed-alpha candidate now contains its byte-exact deployment-boundary tools.'
Write-Host "  Bundle: $bundleRoot"
Write-Host "  Deployment planner SHA-256: $expectedPlannerHash"
Write-Host "  Live request planner SHA-256: $expectedLivePlannerHash"
Write-Host "  Live-host preflight SHA-256: $expectedPreflightHash"
Write-Host "  Artifacts: $($finalManifest.artifacts.Count)"
Write-Host '  Secret values copied: no'
Write-Host '  Publish authorization changed: no'
