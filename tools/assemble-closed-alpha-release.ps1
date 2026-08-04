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
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw $Message
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        Fail $Message
    }
}

function Require-Tool([string]$Name) {
    Require ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue)) "Required tool '$Name' is not available."
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

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$verifierSource = Join-Path $repoRoot 'tools/verify-closed-alpha-release.ps1'
$runbookSource = Join-Path $repoRoot 'tools/closed-alpha-release/CLOSED-ALPHA-OPERATOR-RUNBOOK.txt'
foreach ($requiredPath in @($verifierSource, $runbookSource)) {
    Require ([IO.File]::Exists($requiredPath)) "Closed-alpha assembly input is missing: $requiredPath"
}
Require-Tool 'docker'

Require (-not [string]::IsNullOrWhiteSpace($Version) -and
    $Version.Length -le 64 -and
    $Version -match '^[0-9A-Za-z][0-9A-Za-z.-]*$') 'Version must be 1-64 filename-safe characters using letters, digits, dots, or hyphens.'
$normalizedCommit = $CommitSha.Trim().ToLowerInvariant()
Require ($normalizedCommit -match '^[0-9a-f]{40}$') 'CommitSha must be one lowercase-compatible 40-character Git SHA.'

$apiUri = $null
Require ([Uri]::TryCreate($ApiBaseUrl, [UriKind]::Absolute, [ref]$apiUri) -and
    [string]::Equals($apiUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase) -and
    [string]::IsNullOrEmpty($apiUri.UserInfo) -and
    [string]::IsNullOrEmpty($apiUri.Query) -and
    [string]::IsNullOrEmpty($apiUri.Fragment)) 'ApiBaseUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
$normalizedApiBaseUrl = $apiUri.AbsoluteUri
if (-not $normalizedApiBaseUrl.EndsWith('/', [StringComparison]::Ordinal)) {
    $normalizedApiBaseUrl += '/'
}

$desktopInput = [IO.Path]::GetFullPath($DesktopInputDirectory)
Require ([IO.Directory]::Exists($desktopInput)) "Desktop input directory does not exist: $desktopInput"
$desktopZips = @(Get-ChildItem -LiteralPath $desktopInput -File -Filter '*.zip')
$desktopChecksums = @(Get-ChildItem -LiteralPath $desktopInput -File -Filter '*.zip.sha256')
Require ($desktopZips.Count -eq 1 -and $desktopChecksums.Count -eq 1) 'Desktop input must contain exactly one ZIP and one ZIP checksum.'
$desktopZip = $desktopZips[0]
$desktopChecksum = $desktopChecksums[0]
$expectedDesktopZipName = "Steward-$Version-win-x64.zip"
Require ([string]::Equals($desktopZip.Name, $expectedDesktopZipName, [StringComparison]::Ordinal)) "Desktop ZIP name '$($desktopZip.Name)' does not match version '$Version'."
Require ([string]::Equals($desktopChecksum.Name, "$expectedDesktopZipName.sha256", [StringComparison]::Ordinal)) 'Desktop ZIP checksum filename does not match the ZIP filename.'

$checksumText = [IO.File]::ReadAllText($desktopChecksum.FullName).Trim()
$checksumMatch = [Text.RegularExpressions.Regex]::Match(
    $checksumText,
    '^([0-9A-Fa-f]{64})\s{2}([^\r\n]+)$',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
Require ($checksumMatch.Success -and
    [string]::Equals($checksumMatch.Groups[2].Value, $desktopZip.Name, [StringComparison]::Ordinal)) 'Desktop checksum file has an invalid format or filename.'
$desktopHash = (Get-FileHash -LiteralPath $desktopZip.FullName -Algorithm SHA256).Hash
Require ([string]::Equals($desktopHash, $checksumMatch.Groups[1].Value, [StringComparison]::OrdinalIgnoreCase)) 'Desktop ZIP does not match its SHA-256 checksum file.'

$desktopInspectionRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-closed-alpha-desktop-inspect-" + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $desktopZip.FullName -DestinationPath $desktopInspectionRoot
    $acceptanceManifests = @(Get-ChildItem -LiteralPath $desktopInspectionRoot -Recurse -File -Filter 'acceptance-build.json')
    Require ($acceptanceManifests.Count -eq 1) 'Desktop ZIP must contain exactly one acceptance-build.json.'
    $packageRoot = $acceptanceManifests[0].Directory.FullName
    $desktopManifestText = [IO.File]::ReadAllText($acceptanceManifests[0].FullName)
    Require ($desktopManifestText.Length -le 16MB) 'Desktop acceptance manifest exceeds the 16 MiB inspection bound.'
    $desktopManifest = $desktopManifestText | ConvertFrom-Json
    Require-ExactProperties $desktopManifest @(
        'builtAtUtc',
        'commitSha',
        'configuration',
        'documentType',
        'executable',
        'files',
        'runtime',
        'schemaVersion',
        'selfContained',
        'steamNativeRuntime'
    ) 'Desktop acceptance manifest'
    Require ([string]$desktopManifest.documentType -ceq 'steward.e4-desktop-acceptance-build') 'Desktop acceptance manifest has the wrong document type.'
    Require ([int]$desktopManifest.schemaVersion -eq 2) 'Desktop acceptance manifest has an unsupported schema version.'
    Require ([string]$desktopManifest.commitSha -ceq $normalizedCommit) 'Desktop ZIP was not built from the release commit.'
    Require ([string]$desktopManifest.runtime -ceq 'win-x64') 'Desktop ZIP runtime must be win-x64.'
    Require ([string]$desktopManifest.configuration -ceq 'Release') 'Desktop ZIP configuration must be Release.'
    Require ([bool]$desktopManifest.selfContained) 'Closed-alpha Desktop ZIP must be self-contained.'
    Require ([string]$desktopManifest.executable -ceq 'SharedWorlds.Desktop.exe') 'Desktop acceptance manifest names the wrong executable.'
    Require ([string]$desktopManifest.steamNativeRuntime -ceq 'steam_api64.dll') 'Desktop acceptance manifest names the wrong Steam runtime.'

    $desktopEntries = @($desktopManifest.files)
    Require ($desktopEntries.Count -gt 0 -and $desktopEntries.Count -le 10_000) 'Desktop acceptance manifest is empty or exceeds the file bound.'
    $seenDesktopPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $desktopEntries) {
        Require-ExactProperties $entry @('byteSize', 'path', 'sha256') 'Desktop package entry'
        $relativePath = [string]$entry.path
        Require (-not [string]::IsNullOrWhiteSpace($relativePath) -and
            -not [IO.Path]::IsPathRooted($relativePath) -and
            -not $relativePath.Contains('\', [StringComparison]::Ordinal) -and
            $relativePath -notmatch '(^|/)\.\.?(/|$)') "Desktop package path '$relativePath' is unsafe."
        Require ($seenDesktopPaths.Add($relativePath)) "Desktop package path '$relativePath' is duplicated."
        $fullPath = [IO.Path]::GetFullPath((Join-Path $packageRoot $relativePath))
        Require ($fullPath.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) "Desktop package path '$relativePath' escapes its root."
        Require ([IO.File]::Exists($fullPath)) "Desktop package file '$relativePath' is missing."
        $file = Get-Item -LiteralPath $fullPath
        Require ($file.Length -eq [int64]$entry.byteSize) "Desktop package file '$relativePath' has the wrong byte size."
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        Require ([string]::Equals($actualHash, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) "Desktop package file '$relativePath' has the wrong SHA-256."
    }

    $actualDesktopPaths = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
        Where-Object { -not [string]::Equals($_.FullName, $acceptanceManifests[0].FullName, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/') } |
        Sort-Object)
    $expectedDesktopPaths = @($seenDesktopPaths | Sort-Object)
    Require ($actualDesktopPaths.Count -eq $expectedDesktopPaths.Count -and
        @(Compare-Object $actualDesktopPaths $expectedDesktopPaths).Count -eq 0) 'Desktop ZIP file set does not match its acceptance manifest.'

    $friendsConfigurationPath = Join-Path $packageRoot 'steward-friends-build.json'
    Require ([IO.File]::Exists($friendsConfigurationPath)) 'Desktop ZIP is missing steward-friends-build.json.'
    $friendsConfiguration = [IO.File]::ReadAllText($friendsConfigurationPath) | ConvertFrom-Json
    Require-ExactProperties $friendsConfiguration @('apiBaseUrl', 'schemaVersion') 'Friends Build configuration'
    Require ([int]$friendsConfiguration.schemaVersion -eq 1) 'Friends Build configuration has an unsupported schema version.'
    Require ([string]$friendsConfiguration.apiBaseUrl -ceq $normalizedApiBaseUrl) 'Desktop ZIP API coordinate does not match the release manifest coordinate.'

    $managedDesktopPath = Join-Path $packageRoot 'SharedWorlds.Desktop.dll'
    Require ([IO.File]::Exists($managedDesktopPath)) 'Desktop ZIP is missing SharedWorlds.Desktop.dll.'
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($managedDesktopPath).ProductVersion
    Require ([string]::Equals($productVersion, $Version, [StringComparison]::Ordinal)) "Desktop binary version '$productVersion' does not match release version '$Version'."
}
finally {
    if ([IO.Directory]::Exists($desktopInspectionRoot)) {
        Remove-Item -LiteralPath $desktopInspectionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$backendTar = [IO.Path]::GetFullPath($BackendTarPath)
Require ([IO.File]::Exists($backendTar) -and (Get-Item -LiteralPath $backendTar).Length -gt 0) "Backend image TAR is missing or empty: $backendTar"
Require (-not [string]::IsNullOrWhiteSpace($BackendImageTag) -and $BackendImageTag.Length -le 200) 'BackendImageTag is missing or too long.'

& docker load --input $backendTar
Require ($LASTEXITCODE -eq 0) 'Backend image TAR could not be loaded.'
$backendImageId = (& docker image inspect $BackendImageTag --format '{{.Id}}').Trim().ToLowerInvariant()
$backendRuntimeUser = (& docker image inspect $BackendImageTag --format '{{.Config.User}}').Trim()
$backendPlatform = (& docker image inspect $BackendImageTag --format '{{.Os}}/{{.Architecture}}').Trim().ToLowerInvariant()
$backendRevision = (& docker image inspect $BackendImageTag --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}').Trim().ToLowerInvariant()
$backendVersion = (& docker image inspect $BackendImageTag --format '{{ index .Config.Labels "org.opencontainers.image.version" }}').Trim()
Require ($LASTEXITCODE -eq 0) "Loaded backend image '$BackendImageTag' could not be inspected."
Require ($backendImageId -match '^sha256:[0-9a-f]{64}$') 'Loaded backend image ID is malformed.'
Require ([string]::Equals($backendRuntimeUser, 'app', [StringComparison]::Ordinal)) "Loaded backend image must run as non-root user 'app'."
Require ([string]::Equals($backendPlatform, 'linux/amd64', [StringComparison]::Ordinal)) "Loaded backend image platform '$backendPlatform' is not linux/amd64."
Require ([string]::Equals($backendRevision, $normalizedCommit, [StringComparison]::Ordinal)) 'Loaded backend image revision label does not match the release commit.'
Require ([string]::Equals($backendVersion, $Version, [StringComparison]::Ordinal)) 'Loaded backend image version label does not match the release version.'

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
Require ($artifactEntries.Count -gt 0) 'Closed-alpha release candidate contains no artifacts.'

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
        backendImageId = $backendImageId
        backendRuntimeUser = $backendRuntimeUser
        targetPlatform = $backendPlatform
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
Write-Host "  Backend image: $BackendImageTag"
Write-Host "  Backend image ID: $backendImageId"
Write-Host "  Backend TAR: $(Join-Path $backendOutput $backendTarName)"
Write-Host "  Manifest: $manifestPath"
Write-Host '  Publish authorization: DEFERRED'
