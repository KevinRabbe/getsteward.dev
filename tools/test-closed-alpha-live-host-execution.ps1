[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Utf8([string]$Path,[string]$Content) {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText($Path,$Content.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
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
function Write-Manifest([string]$Bundle,[bool]$PublishAllowed) {
    $artifacts = @(Get-ChildItem -LiteralPath $Bundle -File | Sort-Object Name | ForEach-Object {
        [ordered]@{
            path = $_.Name
            byteSize = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    })
    $manifest = [ordered]@{
        documentType = 'steward.closed-alpha-release-candidate'
        schemaVersion = 1
        channel = 'closed-alpha'
        version = '2.0.0-live-chain-test'
        commitSha = ('7' * 40)
        publishAuthorization = [ordered]@{
            publishAllowed = $PublishAllowed
            physicalBringHere = if ($PublishAllowed) { 'complete' } else { 'deferred' }
        }
        artifacts = $artifacts
    }
    Write-Utf8 (Join-Path $Bundle 'release-manifest.json') ($manifest | ConvertTo-Json -Depth 6)
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('steward-live-chain-test-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $work 'bundle'
$request = Join-Path $work 'request.json'
$key = Join-Path $work 'id_ed25519'
$trace = Join-Path $work 'trace.txt'
[IO.Directory]::CreateDirectory($bundle) | Out-Null

try {
    Copy-Item (Join-Path $PSScriptRoot 'execute-closed-alpha-live-host.ps1') (Join-Path $bundle 'execute-closed-alpha-live-host.ps1')

    Write-Utf8 (Join-Path $bundle 'verify-closed-alpha-release.ps1') @'
[CmdletBinding()] param([Parameter(Mandatory=$true)][string]$BundleDirectory)
if (-not [IO.File]::Exists((Join-Path ([IO.Path]::GetFullPath($BundleDirectory)) 'release-manifest.json'))) { throw 'manifest missing' }
'@

    Write-Utf8 (Join-Path $bundle 'preflight-closed-alpha-live-host.ps1') @'
[CmdletBinding()] param([string]$BundleDirectory,[string]$RequestPath,[string]$SshPrivateKeyPath,[string]$EvidencePath)
Add-Content -LiteralPath $env:STEWARD_EXECUTION_TEST_TRACE -Value 'preflight'
if ($env:STEWARD_EXECUTION_TEST_FAIL_STAGE -ceq 'preflight') { throw 'synthetic preflight failure' }
@{documentType='steward.closed-alpha-live-host-preflight';schemaVersion=1} | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
'@

    Write-Utf8 (Join-Path $bundle 'stage-closed-alpha-live-deployment-plan.ps1') @'
[CmdletBinding()] param([string]$BundleDirectory,[string]$RequestPath,[string]$PreflightEvidencePath,[string]$SshPrivateKeyPath,[string]$EvidencePath)
if (-not [IO.File]::Exists($PreflightEvidencePath)) { throw 'preflight evidence missing' }
Add-Content -LiteralPath $env:STEWARD_EXECUTION_TEST_TRACE -Value 'stage'
if ($env:STEWARD_EXECUTION_TEST_FAIL_STAGE -ceq 'stage') { throw 'synthetic stage failure' }
@{documentType='steward.closed-alpha-live-plan-staging';schemaVersion=1} | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
'@

    Write-Utf8 (Join-Path $bundle 'deploy-closed-alpha-live-backend.ps1') @'
[CmdletBinding()] param([string]$BundleDirectory,[string]$RequestPath,[string]$StagingEvidencePath,[string]$SshPrivateKeyPath,[string]$EvidencePath)
if (-not [IO.File]::Exists($StagingEvidencePath)) { throw 'staging evidence missing' }
Add-Content -LiteralPath $env:STEWARD_EXECUTION_TEST_TRACE -Value 'backend'
if ($env:STEWARD_EXECUTION_TEST_FAIL_STAGE -ceq 'backend') { throw 'synthetic backend failure' }
@{documentType='steward.closed-alpha-live-backend-deployment';schemaVersion=1} | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
'@

    Write-Utf8 (Join-Path $bundle 'activate-closed-alpha-live-ingress.ps1') @'
[CmdletBinding()] param([string]$BundleDirectory,[string]$RequestPath,[string]$BackendDeploymentEvidencePath,[string]$SshPrivateKeyPath,[string]$EvidencePath)
if (-not [IO.File]::Exists($BackendDeploymentEvidencePath)) { throw 'backend evidence missing' }
Add-Content -LiteralPath $env:STEWARD_EXECUTION_TEST_TRACE -Value 'ingress'
if ($env:STEWARD_EXECUTION_TEST_FAIL_STAGE -ceq 'ingress') { throw 'synthetic ingress failure' }
@{documentType='steward.closed-alpha-live-ingress-activation';schemaVersion=1} | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
'@

    Write-Utf8 (Join-Path $bundle 'verify-closed-alpha-external-ingress.ps1') @'
[CmdletBinding()] param([string]$BundleDirectory,[string]$RequestPath,[string]$IngressActivationEvidencePath,[string]$EvidencePath)
if (-not [IO.File]::Exists($IngressActivationEvidencePath)) { throw 'ingress evidence missing' }
Add-Content -LiteralPath $env:STEWARD_EXECUTION_TEST_TRACE -Value 'external'
if ($env:STEWARD_EXECUTION_TEST_FAIL_STAGE -ceq 'external') { throw 'synthetic external failure' }
@{documentType='steward.closed-alpha-external-ingress-acceptance';schemaVersion=1} | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
'@

    Write-Utf8 $request '{"synthetic":true}'
    Write-Utf8 $key 'synthetic-private-key-material-never-copy-this'
    Write-Manifest $bundle $false

    $env:STEWARD_EXECUTION_TEST_TRACE = $trace
    $env:STEWARD_EXECUTION_TEST_FAIL_STAGE = ''
    $successEvidence = Join-Path $work 'success-evidence'
    & (Join-Path $bundle 'execute-closed-alpha-live-host.ps1') `
        -BundleDirectory $bundle `
        -RequestPath $request `
        -SshPrivateKeyPath $key `
        -EvidenceDirectory $successEvidence

    $traceLines = @(Get-Content -LiteralPath $trace)
    Require (@(Compare-Object $traceLines @('preflight','stage','backend','ingress','external') -SyncWindow 0).Count -eq 0) 'Successful execution did not preserve exact five-stage order.'
    $chainPath = Join-Path $successEvidence 'execution-chain.json'
    Require ([IO.File]::Exists($chainPath)) 'Successful execution did not write final chain evidence.'
    $chainText = [IO.File]::ReadAllText($chainPath)
    $chain = $chainText | ConvertFrom-Json
    Require ([string]$chain.documentType -ceq 'steward.closed-alpha-live-host-execution-chain' -and [int]$chain.schemaVersion -eq 1) 'Execution-chain evidence identity is invalid.'
    Require ([int]$chain.stageCount -eq 5 -and [bool]$chain.allStagesSucceeded) 'Execution-chain evidence did not record five successful stages.'
    Require (@($chain.stageEvidence).Count -eq 5 -and @($chain.tools).Count -eq 7) 'Execution-chain evidence has the wrong binding count.'
    Require (-not [bool]$chain.automaticCrossStageRollback -and [bool]$chain.partialStageEvidencePreservedOnFailure) 'Execution-chain failure semantics are misstated.'
    Require (-not [bool]$chain.sshCredentialPathRecorded -and -not [bool]$chain.sshCredentialCopied -and -not [bool]$chain.externalAcceptanceUsedSsh) 'Execution-chain evidence crossed the SSH credential boundary.'
    Require (-not [bool]$chain.publishAllowed -and [string]$chain.physicalBringHere -ceq 'deferred' -and -not [bool]$chain.publicationAuthorizationChanged) 'Execution-chain evidence changed publication state.'
    Require (-not $chainText.Contains([IO.Path]::GetFullPath($key),[StringComparison]::OrdinalIgnoreCase)) 'Execution-chain evidence leaked the SSH private-key path.'
    Require (-not $chainText.Contains('synthetic-private-key-material-never-copy-this',[StringComparison]::Ordinal)) 'Execution-chain evidence leaked SSH private-key contents.'

    Remove-Item -LiteralPath $trace -Force
    $env:STEWARD_EXECUTION_TEST_FAIL_STAGE = 'backend'
    $failedEvidence = Join-Path $work 'failed-evidence'
    Expect-Failure {
        & (Join-Path $bundle 'execute-closed-alpha-live-host.ps1') `
            -BundleDirectory $bundle `
            -RequestPath $request `
            -SshPrivateKeyPath $key `
            -EvidenceDirectory $failedEvidence
    } 'mid-chain failure stops later stages' 'synthetic backend failure'
    $failedTrace = @(Get-Content -LiteralPath $trace)
    Require (@(Compare-Object $failedTrace @('preflight','stage','backend') -SyncWindow 0).Count -eq 0) 'Failure path executed a stage after the failing backend stage.'
    Require ([IO.File]::Exists((Join-Path $failedEvidence '01-live-host-preflight.json'))) 'Failure path did not preserve preflight evidence.'
    Require ([IO.File]::Exists((Join-Path $failedEvidence '02-live-plan-staging.json'))) 'Failure path did not preserve staging evidence.'
    Require (-not [IO.File]::Exists((Join-Path $failedEvidence '03-live-backend-deployment.json'))) 'Failing backend stage wrote success evidence unexpectedly.'
    Require (-not [IO.File]::Exists((Join-Path $failedEvidence '04-live-ingress-activation.json'))) 'Ingress stage ran after backend failure.'
    Require (-not [IO.File]::Exists((Join-Path $failedEvidence '05-external-ingress-acceptance.json'))) 'External stage ran after backend failure.'
    Require (-not [IO.File]::Exists((Join-Path $failedEvidence 'execution-chain.json'))) 'Failure path wrote a final success chain.'

    $nonEmptyEvidence = Join-Path $work 'nonempty-evidence'
    [IO.Directory]::CreateDirectory($nonEmptyEvidence) | Out-Null
    Write-Utf8 (Join-Path $nonEmptyEvidence 'old.json') '{}'
    Expect-Failure {
        & (Join-Path $bundle 'execute-closed-alpha-live-host.ps1') `
            -BundleDirectory $bundle `
            -RequestPath $request `
            -SshPrivateKeyPath $key `
            -EvidenceDirectory $nonEmptyEvidence
    } 'cross-run evidence mixing is rejected' 'Execution evidence directory must be empty'

    Write-Manifest $bundle $true
    $publishEvidence = Join-Path $work 'publish-evidence'
    Expect-Failure {
        & (Join-Path $bundle 'execute-closed-alpha-live-host.ps1') `
            -BundleDirectory $bundle `
            -RequestPath $request `
            -SshPrivateKeyPath $key `
            -EvidenceDirectory $publishEvidence
    } 'publish-authorized candidate is rejected' 'Live-host execution refuses a publish-authorized candidate.'

    Write-Manifest $bundle $false
    Add-Content -LiteralPath (Join-Path $bundle 'verify-closed-alpha-external-ingress.ps1') -Value '# tamper'
    $tamperEvidence = Join-Path $work 'tamper-evidence'
    Expect-Failure {
        & (Join-Path $bundle 'execute-closed-alpha-live-host.ps1') `
            -BundleDirectory $bundle `
            -RequestPath $request `
            -SshPrivateKeyPath $key `
            -EvidenceDirectory $tamperEvidence
    } 'manifest-bound child-tool tampering is rejected' 'Deployment tool byte size does not match the release manifest'

    Write-Host '[OK] Live-host execution composition, fail-fast, and evidence-boundary tests passed.'
}
finally {
    Remove-Item Env:STEWARD_EXECUTION_TEST_TRACE -ErrorAction SilentlyContinue
    Remove-Item Env:STEWARD_EXECUTION_TEST_FAIL_STAGE -ErrorAction SilentlyContinue
    if ([IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
