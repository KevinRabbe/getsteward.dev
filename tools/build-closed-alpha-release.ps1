[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [string]$Version = '2.0.0-alpha.2',
    [string]$OutputDirectory,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw $Message
}

function Require-Tool([string]$Name) {
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Fail "Required tool '$Name' is not available."
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopBuilder = Join-Path $repoRoot 'tools/v2-build-friends.ps1'
$verifierSource = Join-Path $repoRoot 'tools/verify-closed-alpha-release.ps1'
$runbookSource = Join-Path $repoRoot 'tools/closed-alpha-release/CLOSED-ALPHA-OPERATOR-RUNBOOK.txt'
$backendDockerfile = Join-Path $repoRoot 'src/SharedWorlds.Backend.Api/Dockerfile'
foreach ($requiredPath in @($desktopBuilder, $verifierSource, $runbookSource, $backendDockerfile)) {
    if (-not [IO.File]::Exists($requiredPath)) {
        Fail "Closed-alpha release input is missing: $requiredPath"
    }
}

Require-Tool 'git'
Require-Tool 'dotnet'
Require-Tool 'docker'

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version.Length -gt 64 -or
    $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    Fail 'Version must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
}

$apiUri = $null
if (-not [Uri]::TryCreate($ApiBaseUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    -not [string]::Equals($apiUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::IsNullOrEmpty($apiUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($apiUri.Query) -or
    -not [string]::IsNullOrEmpty($apiUri.Fragment)) {
    Fail 'ApiBaseUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
}
$normalizedApiBaseUrl = $apiUri.AbsoluteUri
if (-not $normalizedApiBaseUrl.EndsWith('/', [StringComparison]::Ordinal)) {
    $normalizedApiBaseUrl += '/'
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    Fail 'Could not resolve one exact lowercase Git commit SHA for the release candidate.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/closed-alpha-$Version"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output)) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
[IO.Directory]::CreateDirectory($output) | Out-Null

$desktopDirectory = Join-Path $output 'desktop'
$backendDirectory = Join-Path $output 'backend'
[IO.Directory]::CreateDirectory($desktopDirectory) | Out-Null
[IO.Directory]::CreateDirectory($backendDirectory) | Out-Null

Write-Host 'Building Safe World closed-alpha release candidate'
Write-Host "  Version: $Version"
Write-Host "  Commit: $commit"
Write-Host "  API: $normalizedApiBaseUrl"
Write-Host "  Output: $output"
Write-Host '  Physical Bring Here gate: deferred'
Write-Host

& $desktopBuilder `
    -ApiBaseUrl $normalizedApiBaseUrl `
    -Version $Version `
    -Configuration $Configuration `
    -OutputDirectory $desktopDirectory
if ($LASTEXITCODE -ne 0) {
    Fail "Friends Build package failed with exit code $LASTEXITCODE."
}

$desktopZips = @(Get-ChildItem -LiteralPath $desktopDirectory -File -Filter '*.zip')
$desktopChecksums = @(Get-ChildItem -LiteralPath $desktopDirectory -File -Filter '*.zip.sha256')
if ($desktopZips.Count -ne 1 -or $desktopChecksums.Count -ne 1) {
    Fail 'Closed-alpha Desktop output must contain exactly one ZIP and one ZIP checksum.'
}

$tagVersion = $Version.ToLowerInvariant()
if ($tagVersion -notmatch '^[a-z0-9][a-z0-9._-]{0,63}$') {
    Fail 'Version cannot be normalized into one safe backend image tag.'
}
$backendImageTag = "steward-backend:$tagVersion"
$buildArguments = @(
    'build',
    '--platform', 'linux/amd64',
    '--file', $backendDockerfile,
    '--tag', $backendImageTag,
    '--label', "org.opencontainers.image.revision=$commit",
    '--label', "org.opencontainers.image.version=$Version",
    $repoRoot
)
& docker @buildArguments
if ($LASTEXITCODE -ne 0) {
    Fail "Backend container build failed with exit code $LASTEXITCODE."
}

$backendImageId = (& docker image inspect $backendImageTag --format '{{.Id}}').Trim().ToLowerInvariant()
$backendRuntimeUser = (& docker image inspect $backendImageTag --format '{{.Config.User}}').Trim()
if ($LASTEXITCODE -ne 0 -or $backendImageId -notmatch '^sha256:[0-9a-f]{64}$') {
    Fail 'Built backend image did not expose one valid immutable image ID.'
}
if (-not [string]::Equals($backendRuntimeUser, 'app', [StringComparison]::Ordinal)) {
    Fail "Built backend image must run as non-root user 'app', not '$backendRuntimeUser'."
}

$backendTarName = "steward-backend-$Version-linux-amd64.tar"
$backendTarPath = Join-Path $backendDirectory $backendTarName
& docker save --output $backendTarPath $backendImageTag
if ($LASTEXITCODE -ne 0 -or
    -not [IO.File]::Exists($backendTarPath) -or
    (Get-Item -LiteralPath $backendTarPath).Length -le 0) {
    Fail 'Backend image TAR was not created.'
}

$deploymentExample = @"
# Safe World closed-alpha deployment example.
# Never put real secrets back into the distributed release bundle.
STEWARD_RELEASE_VERSION=$Version
STEWARD_RELEASE_COMMIT=$commit
STEWARD_BACKEND_IMAGE=$backendImageTag
PORT=8080
ConnectionStrings__Steward=<required-postgresql-17-connection-string>
ObjectStorage__ServiceUrl=<required-s3-compatible-service-url>
ObjectStorage__AuthenticationRegion=<required-region>
ObjectStorage__BucketName=<required-private-bucket>
ObjectStorage__AccessKeyId=<required-secret>
ObjectStorage__SecretAccessKey=<required-secret>
ObjectStorage__ForcePathStyle=true
FriendsBuild__Enabled=true
# Add identities produced by tools/v2-provision-friend.ps1 through the secret/configuration platform.
"@
[IO.File]::WriteAllText(
    (Join-Path $backendDirectory 'deployment.env.example'),
    $deploymentExample,
    [Text.UTF8Encoding]::new($false))

Copy-Item -LiteralPath $verifierSource -Destination (Join-Path $output 'verify-closed-alpha-release.ps1') -Force
Copy-Item -LiteralPath $runbookSource -Destination (Join-Path $output 'CLOSED-ALPHA-OPERATOR-RUNBOOK.txt') -Force

$statusText = @"
SAFE WORLD CLOSED-ALPHA RELEASE STATUS
======================================
Version: $Version
Commit: $commit
API base URL: $normalizedApiBaseUrl

Candidate packaging: READY FOR VERIFICATION
Public/closed-alpha publish authorization: DEFERRED

Reason:
The qualified physical PC A -> PC B -> PC A Bring Here acceptance has intentionally
been left for later. Automated PostgreSQL + MinIO source-to-target evidence is green,
but this candidate must not be represented as physically qualified or publish-approved.

Allowed now:
- deterministic candidate generation;
- byte verification;
- backend deployment rehearsal;
- backup and rollback rehearsal;
- private operator smoke testing;
- distribution-process preparation.

Blocked until a later candidate records physicalBringHere=passed:
- broad tester distribution;
- claiming the physical Bring Here milestone;
- treating -RequirePublishAuthorized as optional.
"@
[IO.File]::WriteAllText(
    (Join-Path $output 'RELEASE-STATUS.txt'),
    $statusText,
    [Text.UTF8Encoding]::new($false))

$artifactEntries = @(Get-ChildItem -LiteralPath $output -Recurse -File |
    Where-Object { -not [string]::Equals($_.Name, 'release-manifest.json', [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/')
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    })
if ($artifactEntries.Count -eq 0) {
    Fail 'Closed-alpha release candidate contains no artifacts.'
}

$manifest = [ordered]@{
    documentType = 'steward.closed-alpha-release-candidate'
    schemaVersion = 1
    channel = 'closed-alpha'
    version = $Version
    commitSha = $commit
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    deployment = [ordered]@{
        apiBaseUrl = $normalizedApiBaseUrl
        authenticationMode = 'friends-build'
        backendImageTag = $backendImageTag
        backendImageId = $backendImageId
        backendRuntimeUser = $backendRuntimeUser
        targetPlatform = 'linux/amd64'
        postgresMajorVersion = 17
        objectStorageProtocol = 's3-compatible'
    }
    publishAuthorization = [ordered]@{
        physicalBringHere = 'deferred'
        publishAllowed = $false
        reason = 'The real PC A -> PC B -> PC A Bring Here acceptance is deferred and has not been claimed.'
    }
    artifacts = $artifactEntries
}
$manifestPath = Join-Path $output 'release-manifest.json'
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

$packagedVerifier = Join-Path $output 'verify-closed-alpha-release.ps1'
& $packagedVerifier -BundleDirectory $output

Write-Host
Write-Host '[OK] Safe World closed-alpha release candidate bundle is complete.'
Write-Host "  Desktop ZIP: $($desktopZips[0].FullName)"
Write-Host "  Backend image: $backendImageTag"
Write-Host "  Backend image ID: $backendImageId"
Write-Host "  Backend TAR: $backendTarPath"
Write-Host "  Manifest: $manifestPath"
Write-Host '  Publish authorization: DEFERRED'
