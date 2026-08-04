[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$CommitSha,
    [Parameter(Mandatory = $true)]
    [string]$DesktopInputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$BackendTarPath,
    [Parameter(Mandatory = $true)]
    [string]$BackendImageTag,
    [Parameter(Mandatory = $true)]
    [string]$BackendImageId,
    [string]$BackendRuntimeUser = 'app',
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw $Message
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$verifierSource = Join-Path $repoRoot 'tools/verify-closed-alpha-release.ps1'
$runbookSource = Join-Path $repoRoot 'tools/closed-alpha-release/CLOSED-ALPHA-OPERATOR-RUNBOOK.txt'
foreach ($requiredPath in @($verifierSource, $runbookSource)) {
    if (-not [IO.File]::Exists($requiredPath)) {
        Fail "Closed-alpha assembly input is missing: $requiredPath"
    }
}

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version.Length -gt 64 -or
    $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    Fail 'Version must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
}
$normalizedCommit = $CommitSha.Trim().ToLowerInvariant()
if ($normalizedCommit -notmatch '^[0-9a-f]{40}$') {
    Fail 'CommitSha must be one lowercase-compatible 40-character Git SHA.'
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

$desktopInput = [IO.Path]::GetFullPath($DesktopInputDirectory)
if (-not [IO.Directory]::Exists($desktopInput)) {
    Fail "Desktop input directory does not exist: $desktopInput"
}
$desktopZips = @(Get-ChildItem -LiteralPath $desktopInput -File -Filter '*.zip')
$desktopChecksums = @(Get-ChildItem -LiteralPath $desktopInput -File -Filter '*.zip.sha256')
if ($desktopZips.Count -ne 1 -or $desktopChecksums.Count -ne 1) {
    Fail 'Desktop input must contain exactly one ZIP and one ZIP checksum.'
}
$desktopZip = $desktopZips[0]
$desktopChecksum = $desktopChecksums[0]
$expectedChecksumName = "$($desktopZip.Name).sha256"
if (-not [string]::Equals($desktopChecksum.Name, $expectedChecksumName, [StringComparison]::Ordinal)) {
    Fail 'Desktop ZIP checksum filename does not match the ZIP filename.'
}
$checksumText = [IO.File]::ReadAllText($desktopChecksum.FullName).Trim()
$checksumMatch = [Text.RegularExpressions.Regex]::Match(
    $checksumText,
    '^([0-9A-Fa-f]{64})\s{2}([^\r\n]+)$',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $checksumMatch.Success -or
    -not [string]::Equals($checksumMatch.Groups[2].Value, $desktopZip.Name, [StringComparison]::Ordinal)) {
    Fail 'Desktop checksum file has an invalid format or filename.'
}
$desktopHash = (Get-FileHash -LiteralPath $desktopZip.FullName -Algorithm SHA256).Hash
if (-not [string]::Equals($desktopHash, $checksumMatch.Groups[1].Value, [StringComparison]::OrdinalIgnoreCase)) {
    Fail 'Desktop ZIP does not match its SHA-256 checksum file.'
}

$backendTar = [IO.Path]::GetFullPath($BackendTarPath)
if (-not [IO.File]::Exists($backendTar) -or (Get-Item -LiteralPath $backendTar).Length -le 0) {
    Fail "Backend image TAR is missing or empty: $backendTar"
}
$normalizedImageId = $BackendImageId.Trim().ToLowerInvariant()
if ($normalizedImageId -notmatch '^sha256:[0-9a-f]{64}$') {
    Fail 'BackendImageId must be one immutable sha256 image ID.'
}
if ([string]::IsNullOrWhiteSpace($BackendImageTag) -or $BackendImageTag.Length -gt 200) {
    Fail 'BackendImageTag is missing or too long.'
}
if (-not [string]::Equals($BackendRuntimeUser, 'app', [StringComparison]::Ordinal)) {
    Fail "BackendRuntimeUser must be 'app'."
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output)) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
$desktopOutput = Join-Path $output 'desktop'
$backendOutput = Join-Path $output 'backend'
[IO.Directory]::CreateDirectory($desktopOutput) | Out-Null
[IO.Directory]::CreateDirectory($backendOutput) | Out-Null

Copy-Item -LiteralPath $desktopZip.FullName -Destination (Join-Path $desktopOutput $desktopZip.Name) -Force
Copy-Item -LiteralPath $desktopChecksum.FullName -Destination (Join-Path $desktopOutput $desktopChecksum.Name) -Force
$backendTarName = "steward-backend-$Version-linux-amd64.tar"
Copy-Item -LiteralPath $backendTar -Destination (Join-Path $backendOutput $backendTarName) -Force
Copy-Item -LiteralPath $verifierSource -Destination (Join-Path $output 'verify-closed-alpha-release.ps1') -Force
Copy-Item -LiteralPath $runbookSource -Destination (Join-Path $output 'CLOSED-ALPHA-OPERATOR-RUNBOOK.txt') -Force

$deploymentExample = @"
# Safe World closed-alpha deployment example.
# Never put real secrets back into the distributed release bundle.
STEWARD_RELEASE_VERSION=$Version
STEWARD_RELEASE_COMMIT=$normalizedCommit
STEWARD_BACKEND_IMAGE=$BackendImageTag
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
    (Join-Path $backendOutput 'deployment.env.example'),
    $deploymentExample,
    [Text.UTF8Encoding]::new($false))

$statusText = @"
SAFE WORLD CLOSED-ALPHA RELEASE STATUS
======================================
Version: $Version
Commit: $normalizedCommit
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
    commitSha = $normalizedCommit
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    deployment = [ordered]@{
        apiBaseUrl = $normalizedApiBaseUrl
        authenticationMode = 'friends-build'
        backendImageTag = $BackendImageTag
        backendImageId = $normalizedImageId
        backendRuntimeUser = $BackendRuntimeUser
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
Write-Host '[OK] Safe World closed-alpha release candidate assembled.'
Write-Host "  Version: $Version"
Write-Host "  Commit: $normalizedCommit"
Write-Host "  Desktop ZIP: $(Join-Path $desktopOutput $desktopZip.Name)"
Write-Host "  Backend TAR: $(Join-Path $backendOutput $backendTarName)"
Write-Host "  Manifest: $manifestPath"
Write-Host '  Publish authorization: DEFERRED'
