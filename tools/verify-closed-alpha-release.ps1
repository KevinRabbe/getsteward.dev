[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [switch]$RequirePublishAuthorized
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

function Require-SafeRelativePath([string]$Path) {
    Require (-not [string]::IsNullOrWhiteSpace($Path)) 'Artifact path is required.'
    Require ($Path.Length -le 260) "Artifact path '$Path' is too long."
    Require (-not [IO.Path]::IsPathRooted($Path)) "Artifact path '$Path' must be relative."
    Require (-not $Path.Contains('\', [StringComparison]::Ordinal)) "Artifact path '$Path' must use forward slashes."
    Require (-not $Path.StartsWith('/', [StringComparison]::Ordinal)) "Artifact path '$Path' cannot start with '/'."
    Require ($Path -notmatch '[\x00-\x1F\x7F:]') "Artifact path '$Path' contains a control character or colon."
    $segments = @($Path.Split('/'))
    Require ($segments.Count -gt 0) "Artifact path '$Path' has no segments."
    foreach ($segment in $segments) {
        Require (-not [string]::IsNullOrEmpty($segment)) "Artifact path '$Path' contains an empty segment."
        Require ($segment -cne '.' -and $segment -cne '..') "Artifact path '$Path' contains a traversal segment."
    }
}

function Get-ArtifactByPath([object[]]$Artifacts, [string]$Path) {
    $matches = @($Artifacts | Where-Object { [string]$_.path -ceq $Path })
    Require ($matches.Count -eq 1) "Evidence path '$Path' is not represented exactly once in the release artifact list."
    return $matches[0]
}

function Require-EvidenceCheckpoint([object]$Checkpoint, [string]$Context, [object[]]$Artifacts) {
    Require-ExactProperties $Checkpoint @('path', 'sha256') $Context
    $path = [string]$Checkpoint.path
    Require-SafeRelativePath $path
    Require ($path.StartsWith('physical-evidence/', [StringComparison]::Ordinal)) "$Context path must be under physical-evidence/."
    $sha256 = [string]$Checkpoint.sha256
    Require ($sha256 -match '^[0-9A-F]{64}$') "$Context SHA-256 is malformed."
    $artifact = Get-ArtifactByPath $Artifacts $path
    Require ([string]$artifact.sha256 -ceq $sha256) "$Context SHA-256 disagrees with the release artifact list."
}

$root = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($root)) "Closed-alpha bundle directory does not exist: $root"

$manifestPath = Join-Path $root 'release-manifest.json'
Require ([IO.File]::Exists($manifestPath)) "Closed-alpha release manifest is missing: $manifestPath"

$manifestText = [IO.File]::ReadAllText($manifestPath)
Require ($manifestText.Length -le 4MB) 'Closed-alpha release manifest exceeds the 4 MiB verification bound.'
$manifest = $manifestText | ConvertFrom-Json

Require-ExactProperties $manifest @(
    'artifacts',
    'builtAtUtc',
    'channel',
    'commitSha',
    'deployment',
    'documentType',
    'publishAuthorization',
    'schemaVersion',
    'version'
) 'Release manifest'

Require ([string]$manifest.documentType -ceq 'steward.closed-alpha-release-candidate') 'Unexpected release-manifest document type.'
Require ([int]$manifest.schemaVersion -eq 1) 'Unsupported release-manifest schema version.'
Require ([string]$manifest.channel -ceq 'closed-alpha') 'Release channel must be closed-alpha.'

$version = [string]$manifest.version
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is missing or malformed.'
$commitSha = [string]$manifest.commitSha
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA must be one lowercase 40-character Git SHA.'

$builtAt = [DateTimeOffset]::MinValue
Require ([DateTimeOffset]::TryParse([string]$manifest.builtAtUtc, [ref]$builtAt)) 'Release builtAtUtc is malformed.'
Require ($builtAt -ne [DateTimeOffset]::MinValue) 'Release builtAtUtc cannot be the default timestamp.'

Require-ExactProperties $manifest.publishAuthorization @(
    'evidencePath',
    'evidenceSha256',
    'physicalBringHere',
    'publishAllowed',
    'reason'
) 'Publish authorization'

$physicalStatus = [string]$manifest.publishAuthorization.physicalBringHere
Require ($physicalStatus -in @('deferred', 'passed')) 'Physical Bring Here status must be deferred or passed.'
$publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
Require ($publishAllowed -eq ($physicalStatus -ceq 'passed')) 'Publish authorization disagrees with the physical Bring Here status.'
$reason = [string]$manifest.publishAuthorization.reason
Require (-not [string]::IsNullOrWhiteSpace($reason) -and $reason.Length -le 500) 'Publish authorization reason is missing or too long.'
Require ($reason -notmatch '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') 'Publish authorization reason contains a forbidden control character.'
$physicalEvidencePathValue = $manifest.publishAuthorization.evidencePath
$physicalEvidenceShaValue = $manifest.publishAuthorization.evidenceSha256
if ($physicalStatus -ceq 'deferred') {
    Require ($null -eq $physicalEvidencePathValue -and $null -eq $physicalEvidenceShaValue) 'Deferred publish authorization cannot carry physical evidence fields.'
}
else {
    $physicalEvidencePath = [string]$physicalEvidencePathValue
    $physicalEvidenceSha = [string]$physicalEvidenceShaValue
    Require-SafeRelativePath $physicalEvidencePath
    Require ($physicalEvidencePath.StartsWith('physical-evidence/', [StringComparison]::Ordinal) -and
        $physicalEvidencePath.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) 'Passed physical evidence summary must be one JSON file under physical-evidence/.'
    Require ($physicalEvidenceSha -match '^[0-9A-F]{64}$') 'Passed physical evidence summary SHA-256 is malformed.'
}

Require-ExactProperties $manifest.deployment @(
    'apiBaseUrl',
    'authenticationMode',
    'backendImageId',
    'backendImageTag',
    'backendRuntimeUser',
    'objectStorageProtocol',
    'postgresMajorVersion',
    'targetPlatform'
) 'Deployment contract'

$apiUri = $null
Require ([Uri]::TryCreate([string]$manifest.deployment.apiBaseUrl, [UriKind]::Absolute, [ref]$apiUri)) 'Deployment API base URL is malformed.'
Require ([string]::Equals($apiUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) 'Deployment API base URL must use HTTPS.'
Require ([string]::IsNullOrEmpty($apiUri.UserInfo)) 'Deployment API base URL cannot contain credentials.'
Require ([string]::IsNullOrEmpty($apiUri.Query) -and [string]::IsNullOrEmpty($apiUri.Fragment)) 'Deployment API base URL cannot contain query or fragment data.'
Require ($apiUri.AbsoluteUri.EndsWith('/', [StringComparison]::Ordinal)) 'Deployment API base URL must end with a slash.'
Require ([string]$manifest.deployment.authenticationMode -ceq 'friends-build') 'Closed-alpha authentication mode must be friends-build.'
Require ([string]$manifest.deployment.backendRuntimeUser -ceq 'app') 'Backend image must run as the non-root app user.'
Require ([string]$manifest.deployment.targetPlatform -ceq 'linux/amd64') 'Backend target platform must be linux/amd64.'
Require ([int]$manifest.deployment.postgresMajorVersion -eq 17) 'Closed-alpha PostgreSQL major version must be 17.'
Require ([string]$manifest.deployment.objectStorageProtocol -ceq 's3-compatible') 'Closed-alpha object storage contract must be s3-compatible.'
Require ([string]$manifest.deployment.backendImageId -match '^sha256:[0-9a-f]{64}$') 'Backend image ID is malformed.'
Require (-not [string]::IsNullOrWhiteSpace([string]$manifest.deployment.backendImageTag)) 'Backend image tag is required.'

$artifacts = @($manifest.artifacts)
Require ($artifacts.Count -gt 0 -and $artifacts.Count -le 10_000) 'Release artifact list is empty or exceeds the 10,000-file bound.'
$seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($artifact in $artifacts) {
    Require-ExactProperties $artifact @('byteSize', 'path', 'sha256') 'Artifact entry'
    $relativePath = [string]$artifact.path
    Require-SafeRelativePath $relativePath
    Require ($seenPaths.Add($relativePath)) "Artifact path '$relativePath' is duplicated."

    $expectedSize = [int64]$artifact.byteSize
    Require ($expectedSize -ge 0) "Artifact '$relativePath' has a negative byte size."
    $expectedHash = [string]$artifact.sha256
    Require ($expectedHash -match '^[0-9A-F]{64}$') "Artifact '$relativePath' has a malformed SHA-256."

    $fullPath = [IO.Path]::GetFullPath((Join-Path $root $relativePath))
    Require ($fullPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) "Artifact '$relativePath' escapes the bundle root."
    Require ([IO.File]::Exists($fullPath)) "Artifact '$relativePath' is missing."
    $file = Get-Item -LiteralPath $fullPath
    Require ($null -eq $file.LinkType) "Artifact '$relativePath' cannot be a symbolic link."
    Require ($file.Length -eq $expectedSize) "Artifact '$relativePath' does not match its declared byte size."
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    Require ([string]::Equals($actualHash, $expectedHash, [StringComparison]::Ordinal)) "Artifact '$relativePath' does not match its declared SHA-256."
}

$actualRelativePaths = @(Get-ChildItem -LiteralPath $root -Recurse -File |
    ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') } |
    Sort-Object)
$expectedRelativePaths = @($seenPaths | Sort-Object) + @('release-manifest.json')
$expectedRelativePaths = @($expectedRelativePaths | Sort-Object)
Require ($actualRelativePaths.Count -eq $expectedRelativePaths.Count) 'Bundle contains unmanifested files or omits manifested files.'
Require (@(Compare-Object $actualRelativePaths $expectedRelativePaths).Count -eq 0) 'Bundle file set does not exactly match release-manifest.json.'

$desktopZip = @($artifacts | Where-Object { ([string]$_.path).StartsWith('desktop/', [StringComparison]::Ordinal) -and ([string]$_.path).EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) })
$backendTar = @($artifacts | Where-Object { ([string]$_.path).StartsWith('backend/', [StringComparison]::Ordinal) -and ([string]$_.path).EndsWith('.tar', [StringComparison]::OrdinalIgnoreCase) })
Require ($desktopZip.Count -eq 1) 'Bundle must contain exactly one Desktop ZIP.'
Require ($backendTar.Count -eq 1) 'Bundle must contain exactly one backend image TAR.'
Require ($seenPaths.Contains('backend/deployment.env.example')) 'Bundle is missing backend/deployment.env.example.'
Require ($seenPaths.Contains('CLOSED-ALPHA-OPERATOR-RUNBOOK.txt')) 'Bundle is missing the operator runbook.'
Require ($seenPaths.Contains('verify-closed-alpha-release.ps1')) 'Bundle is missing its verifier.'
Require ($seenPaths.Contains('RELEASE-STATUS.txt')) 'Bundle is missing RELEASE-STATUS.txt.'

if ($physicalStatus -ceq 'passed') {
    $physicalEvidencePath = [string]$physicalEvidencePathValue
    $physicalEvidenceSha = [string]$physicalEvidenceShaValue
    $physicalEvidenceArtifact = Get-ArtifactByPath $artifacts $physicalEvidencePath
    Require ([string]$physicalEvidenceArtifact.sha256 -ceq $physicalEvidenceSha) 'Physical evidence summary SHA-256 disagrees with the release artifact list.'
    $physicalEvidenceFullPath = Join-Path $root $physicalEvidencePath
    $physicalEvidenceText = [IO.File]::ReadAllText($physicalEvidenceFullPath)
    Require ($physicalEvidenceText.Length -le 1MB) 'Physical evidence summary exceeds the 1 MiB bound.'
    $physicalEvidence = $physicalEvidenceText | ConvertFrom-Json
    Require-ExactProperties $physicalEvidence @(
        'completedAtUtc',
        'documentType',
        'qualifiedKitCommitSha',
        'schemaVersion',
        'sourceAfter',
        'sourceBefore',
        'targetAfter'
    ) 'Physical evidence summary'
    Require ([string]$physicalEvidence.documentType -ceq 'steward.bring-here-physical-pass') 'Physical evidence summary has the wrong document type.'
    Require ([int]$physicalEvidence.schemaVersion -eq 1) 'Physical evidence summary has an unsupported schema version.'
    Require ([string]$physicalEvidence.qualifiedKitCommitSha -match '^[0-9a-f]{40}$') 'Physical evidence summary has a malformed qualified kit commit SHA.'
    $completedAt = [DateTimeOffset]::MinValue
    Require ([DateTimeOffset]::TryParse([string]$physicalEvidence.completedAtUtc, [ref]$completedAt) -and
        $completedAt -ne [DateTimeOffset]::MinValue) 'Physical evidence summary completedAtUtc is malformed.'
    Require-EvidenceCheckpoint $physicalEvidence.sourceBefore 'Physical source-before evidence' $artifacts
    Require-EvidenceCheckpoint $physicalEvidence.targetAfter 'Physical target-after evidence' $artifacts
    Require-EvidenceCheckpoint $physicalEvidence.sourceAfter 'Physical source-after evidence' $artifacts
}

if ($RequirePublishAuthorized.IsPresent -and -not $publishAllowed) {
    throw "Release candidate is structurally valid but publishing is blocked: $reason"
}

Write-Host
Write-Host '[OK] Safe World closed-alpha release candidate is byte-exact and structurally valid.'
Write-Host "  Version: $version"
Write-Host "  Commit: $commitSha"
Write-Host "  API: $($apiUri.AbsoluteUri)"
Write-Host "  Artifacts: $($artifacts.Count)"
if ($publishAllowed) {
    Write-Host '  Publish authorization: ALLOWED'
}
else {
    Write-Host '  Publish authorization: DEFERRED'
    Write-Host "  Reason: $reason"
}
