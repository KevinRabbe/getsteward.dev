[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [Parameter(Mandatory = $true)]
    [string]$SshPrivateKeyPath,
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-ArtifactByPath([object[]]$Artifacts,[string]$Path) {
    $matches = @($Artifacts | Where-Object { [string]$_.path -ceq $Path })
    Require ($matches.Count -eq 1) "Release artifact '$Path' must appear exactly once."
    return $matches[0]
}

function Require-ManifestBoundFile([string]$BundleRoot,[object[]]$Artifacts,[string]$RelativePath) {
    $path = [IO.Path]::GetFullPath((Join-Path $BundleRoot $RelativePath))
    $bundlePrefix = $BundleRoot.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Require ($path.StartsWith($bundlePrefix,[StringComparison]::OrdinalIgnoreCase)) "Deployment tool escapes the release bundle: $RelativePath"
    Require ([IO.File]::Exists($path)) "Deployment tool is missing from the release bundle: $RelativePath"
    $item = Get-Item -LiteralPath $path
    Require ($null -eq $item.LinkType) "Deployment tool cannot be a symbolic link: $RelativePath"
    Require ($item.Length -gt 0 -and $item.Length -le 2MB) "Deployment tool is empty or exceeds 2 MiB: $RelativePath"
    $artifact = Get-ArtifactByPath $Artifacts $RelativePath
    Require ($item.Length -eq [int64]$artifact.byteSize) "Deployment tool byte size does not match the release manifest: $RelativePath"
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    Require ($hash -ceq [string]$artifact.sha256) "Deployment tool SHA-256 does not match the release manifest: $RelativePath"
    return [ordered]@{ Path=$path; RelativePath=$RelativePath; Sha256=$hash; ByteSize=$item.Length }
}

function Get-EvidenceBinding([string]$Path,[string]$ExpectedDocumentType) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    Require ([IO.File]::Exists($fullPath)) "Expected stage evidence was not written: $fullPath"
    $item = Get-Item -LiteralPath $fullPath
    Require ($null -eq $item.LinkType) "Stage evidence cannot be a symbolic link: $fullPath"
    Require ($item.Length -gt 0 -and $item.Length -le 1MB) "Stage evidence is empty or exceeds 1 MiB: $fullPath"
    $text = [IO.File]::ReadAllText($fullPath)
    $json = $text | ConvertFrom-Json
    Require ([string]$json.documentType -ceq $ExpectedDocumentType) "Stage evidence has the wrong document type: $ExpectedDocumentType"
    Require ([int]$json.schemaVersion -eq 1) "Stage evidence has an unsupported schema version: $ExpectedDocumentType"
    return [ordered]@{
        file = [IO.Path]::GetFileName($fullPath)
        documentType = $ExpectedDocumentType
        byteSize = $item.Length
        sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$bundleItem = Get-Item -LiteralPath $bundleRoot
Require ($null -eq $bundleItem.LinkType) 'Closed-alpha bundle directory cannot be a symbolic link.'

$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
$manifestItem = Get-Item -LiteralPath $manifestPath
Require ($null -eq $manifestItem.LinkType -and $manifestItem.Length -gt 0 -and $manifestItem.Length -le 4MB) 'Release manifest is invalid or exceeds 4 MiB.'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
Require ([string]$manifest.documentType -ceq 'steward.closed-alpha-release-candidate' -and [int]$manifest.schemaVersion -eq 1) 'Release manifest identity is invalid.'
Require ([string]$manifest.channel -ceq 'closed-alpha') 'Live-host execution requires the closed-alpha channel.'
Require (-not [bool]$manifest.publishAuthorization.publishAllowed) 'Live-host execution refuses a publish-authorized candidate.'
Require ([string]$manifest.publishAuthorization.physicalBringHere -ceq 'deferred') 'Live-host execution requires physical Bring Here to remain deferred.'
Require ([string]$manifest.version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ([string]$manifest.commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
$artifacts = @($manifest.artifacts)
Require ($artifacts.Count -gt 0 -and $artifacts.Count -le 10000) 'Release artifact count is outside the supported bound.'

$toolNames = @(
    'verify-closed-alpha-release.ps1',
    'preflight-closed-alpha-live-host.ps1',
    'stage-closed-alpha-live-deployment-plan.ps1',
    'deploy-closed-alpha-live-backend.ps1',
    'activate-closed-alpha-live-ingress.ps1',
    'verify-closed-alpha-external-ingress.ps1',
    'execute-closed-alpha-live-host.ps1'
)
$tools = @{}
foreach ($toolName in $toolNames) {
    $tools[$toolName] = Require-ManifestBoundFile -BundleRoot $bundleRoot -Artifacts $artifacts -RelativePath $toolName
}

$selfPath = [IO.Path]::GetFullPath($PSCommandPath)
Require ([string]::Equals($selfPath,[string]$tools['execute-closed-alpha-live-host.ps1'].Path,[StringComparison]::OrdinalIgnoreCase)) 'Run live-host execution from inside the verified release bundle.'

$requestFullPath = [IO.Path]::GetFullPath($RequestPath)
Require ([IO.File]::Exists($requestFullPath)) "Live deployment request does not exist: $requestFullPath"
$requestItem = Get-Item -LiteralPath $requestFullPath
Require ($null -eq $requestItem.LinkType -and $requestItem.Length -gt 0 -and $requestItem.Length -le 1MB) 'Live deployment request is invalid or exceeds 1 MiB.'
$requestSha256 = (Get-FileHash -LiteralPath $requestFullPath -Algorithm SHA256).Hash.ToUpperInvariant()

$keyFullPath = [IO.Path]::GetFullPath($SshPrivateKeyPath)
Require ([IO.File]::Exists($keyFullPath)) "SSH private key does not exist: $keyFullPath"
$keyItem = Get-Item -LiteralPath $keyFullPath
Require ($null -eq $keyItem.LinkType) 'SSH private key cannot be a symbolic link.'
Require ($keyItem.Length -gt 0 -and $keyItem.Length -le 64KB) 'SSH private key is empty or exceeds 64 KiB.'

$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
$bundlePrefix = $bundleRoot.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Require (-not [string]::Equals($evidenceRoot,$bundleRoot,[StringComparison]::OrdinalIgnoreCase) -and -not $evidenceRoot.StartsWith($bundlePrefix,[StringComparison]::OrdinalIgnoreCase)) 'Execution evidence must be written outside the immutable release bundle.'
$evidencePrefix = $evidenceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Require (-not $keyFullPath.StartsWith($evidencePrefix,[StringComparison]::OrdinalIgnoreCase)) 'SSH private key cannot live inside the execution evidence directory.'

if ([IO.Directory]::Exists($evidenceRoot)) {
    $evidenceRootItem = Get-Item -LiteralPath $evidenceRoot
    Require ($null -eq $evidenceRootItem.LinkType) 'Execution evidence directory cannot be a symbolic link.'
    Require (@(Get-ChildItem -LiteralPath $evidenceRoot -Force).Count -eq 0) 'Execution evidence directory must be empty to prevent cross-run evidence mixing.'
}
else {
    [IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
}

& ([string]$tools['verify-closed-alpha-release.ps1'].Path) -BundleDirectory $bundleRoot

$preflightEvidence = Join-Path $evidenceRoot '01-live-host-preflight.json'
$stagingEvidence = Join-Path $evidenceRoot '02-live-plan-staging.json'
$backendEvidence = Join-Path $evidenceRoot '03-live-backend-deployment.json'
$ingressEvidence = Join-Path $evidenceRoot '04-live-ingress-activation.json'
$handoffEvidence = Join-Path $evidenceRoot 'deployment-handoff.json'

Write-Host '[1/4] Read-only live-host preflight'
& ([string]$tools['preflight-closed-alpha-live-host.ps1'].Path) `
    -BundleDirectory $bundleRoot `
    -RequestPath $requestFullPath `
    -SshPrivateKeyPath $keyFullPath `
    -EvidencePath $preflightEvidence
$preflightBinding = Get-EvidenceBinding $preflightEvidence 'steward.closed-alpha-live-host-preflight'

Write-Host '[2/4] Exact candidate and deployment-plan staging'
& ([string]$tools['stage-closed-alpha-live-deployment-plan.ps1'].Path) `
    -BundleDirectory $bundleRoot `
    -RequestPath $requestFullPath `
    -PreflightEvidencePath $preflightEvidence `
    -SshPrivateKeyPath $keyFullPath `
    -EvidencePath $stagingEvidence
$stagingBinding = Get-EvidenceBinding $stagingEvidence 'steward.closed-alpha-live-plan-staging'

Write-Host '[3/4] Exact backend deployment'
& ([string]$tools['deploy-closed-alpha-live-backend.ps1'].Path) `
    -BundleDirectory $bundleRoot `
    -RequestPath $requestFullPath `
    -StagingEvidencePath $stagingEvidence `
    -SshPrivateKeyPath $keyFullPath `
    -EvidencePath $backendEvidence
$backendBinding = Get-EvidenceBinding $backendEvidence 'steward.closed-alpha-live-backend-deployment'

Write-Host '[4/4] Managed Caddy ingress activation'
& ([string]$tools['activate-closed-alpha-live-ingress.ps1'].Path) `
    -BundleDirectory $bundleRoot `
    -RequestPath $requestFullPath `
    -BackendDeploymentEvidencePath $backendEvidence `
    -SshPrivateKeyPath $keyFullPath `
    -EvidencePath $ingressEvidence
$ingressBinding = Get-EvidenceBinding $ingressEvidence 'steward.closed-alpha-live-ingress-activation'

$toolBindings = @($toolNames | ForEach-Object {
    $binding = $tools[$_]
    [ordered]@{ path=$binding.RelativePath; byteSize=$binding.ByteSize; sha256=$binding.Sha256 }
})
$stageEvidence = @($preflightBinding,$stagingBinding,$backendBinding,$ingressBinding)
$handoff = [ordered]@{
    documentType = 'steward.closed-alpha-live-host-deployment-handoff'
    schemaVersion = 1
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    releaseVersion = [string]$manifest.version
    releaseCommitSha = [string]$manifest.commitSha
    requestSha256 = $requestSha256
    tools = $toolBindings
    stageEvidence = $stageEvidence
    deploymentStageCount = 4
    allDeploymentStagesSucceeded = $true
    partialStageEvidencePreservedOnFailure = $true
    automaticCrossStageRollback = $false
    sshCredentialPathRecorded = $false
    sshCredentialCopied = $false
    protectedEnvironmentCopiedToOperator = $false
    externalAcceptancePerformed = $false
    externalAcceptanceRequiresSeparateObserver = $true
    externalObserverMustReceiveSshCredential = $false
    publishAllowed = $false
    physicalBringHere = 'deferred'
    publicationAuthorizationChanged = $false
}
$handoffText = $handoff | ConvertTo-Json -Depth 8
Require ($handoffText.Length -gt 0 -and $handoffText.Length -le 128KB) 'Live-host deployment-handoff evidence is empty or exceeds 128 KiB.'
[IO.File]::WriteAllText($handoffEvidence,$handoffText,[Text.UTF8Encoding]::new($false))

Write-Host
Write-Host '[OK] Exact closed-alpha live-host deployment stages completed.'
Write-Host "  Release: $([string]$manifest.version) ($([string]$manifest.commitSha))"
Write-Host "  Request SHA-256: $requestSha256"
Write-Host "  Evidence directory: $evidenceRoot"
Write-Host '  Deployment stages: 4/4 succeeded'
Write-Host '  SSH credential copied/recorded: no'
Write-Host '  External acceptance performed here: no'
Write-Host '  External acceptance requires a separate credential-free observer'
Write-Host '  Cross-stage atomic rollback claimed: no'
Write-Host '  Publication authorization changed: no'
Write-Host '  Physical Bring Here: deferred'
