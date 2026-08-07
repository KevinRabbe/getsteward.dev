[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BundleDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Require([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
$root=[IO.Path]::GetFullPath($BundleDirectory);Require([IO.Directory]::Exists($root)) 'Closed-alpha bundle directory is missing.';$manifestPath=Join-Path $root 'release-manifest.json';$verifier=Join-Path $root 'verify-closed-alpha-release.ps1';Require([IO.File]::Exists($manifestPath) -and [IO.File]::Exists($verifier)) 'Closed-alpha bundle is missing manifest/verifier.'
& $verifier -BundleDirectory $root
$manifestText=[IO.File]::ReadAllText($manifestPath);$manifest=$manifestText|ConvertFrom-Json;Require([string]$manifest.documentType -ceq 'steward.closed-alpha-release-candidate' -and [int]$manifest.schemaVersion -eq 1 -and [string]$manifest.channel -ceq 'closed-alpha') 'Release manifest identity is invalid.';Require(-not [bool]$manifest.publishAuthorization.publishAllowed -and [string]$manifest.publishAuthorization.physicalBringHere -ceq 'deferred' -and $null -eq $manifest.publishAuthorization.evidencePath -and $null -eq $manifest.publishAuthorization.evidenceSha256) 'Publication-boundary packaging requires a deferred candidate.'
$identityBefore=[ordered]@{documentType=[string]$manifest.documentType;schemaVersion=[int]$manifest.schemaVersion;channel=[string]$manifest.channel;version=[string]$manifest.version;commitSha=[string]$manifest.commitSha;builtAtUtc=[string]$manifest.builtAtUtc;deployment=$manifest.deployment;publishAuthorization=$manifest.publishAuthorization}|ConvertTo-Json -Compress -Depth 8
$promotionSource=Join-Path $PSScriptRoot 'promote-closed-alpha-publication.ps1';Require([IO.File]::Exists($promotionSource)) 'Qualified publication promotion tool is missing.';$ps=Get-Item -LiteralPath $promotionSource;Require($null -eq $ps.LinkType -and $ps.Length -gt 0 -and $ps.Length -le 2MB) 'Qualified publication promotion tool is invalid.'
$promotionDestination=Join-Path $root 'promote-closed-alpha-publication.ps1';$readmeDestination=Join-Path $root 'PUBLICATION-AUTHORIZATION-README.txt';Require(-not [IO.File]::Exists($promotionDestination) -and -not [IO.File]::Exists($readmeDestination)) 'Publication boundary already appears to be packaged.';Copy-Item -LiteralPath $promotionSource -Destination $promotionDestination
$readme=@'
SAFE WORLD CLOSED-ALPHA PUBLICATION AUTHORIZATION
================================================

The downloaded candidate is deliberately DEFERRED and is not publish-authorized.

Publication requires BOTH independent evidence families:

1. Physical PC A -> PC B -> PC A Bring Here
   - pc-a source-before JSON
   - pc-b target-after JSON
   - pc-a source-after JSON
   - PC A source-before screenshot (PNG)
   - PC B Available/Bring-here-before screenshot (PNG)
   - PC B target-after/local screenshot (PNG)
   - PC A source-after screenshot (PNG)

   The three probe JSON files must use the exact release commit, the exact same
   byte-verified acceptance package and World/revision/payload identity, distinct
   source/target machine + installation identities, synchronized publication
   journals, and the source-after file must cryptographically link both earlier
   JSON files.

   Screenshot CONTENT remains an operator-reviewed requirement because the
   read-only probe cannot prove what the PC B UI displayed before the click. The
   promotion tool verifies PNG bytes and hash-binds them; it does not claim to
   machine-interpret screenshot content.

2. Exact live-host execution chain
   - live-host-execution-chain.json produced only after the qualified four-stage
     deployment sequence, separate external HTTPS/8080 acceptance, and no-SSH
     evidence finalization.

   External observer credential separation remains operator-required and is not
   overclaimed as a filesystem-wide machine attestation.

TERMINAL PROMOTION
------------------

Run promotion from the verified deferred candidate and write to a NEW directory:

pwsh ./promote-closed-alpha-publication.ps1 \
  -BundleDirectory . \
  -SourceBeforeEvidencePath <pc-a-source-before.json> \
  -TargetAfterEvidencePath <pc-b-target-after.json> \
  -SourceAfterEvidencePath <pc-a-source-after.json> \
  -SourceBeforeScreenshotPath <pc-a-source-before.png> \
  -TargetAvailableScreenshotPath <pc-b-available-before.png> \
  -TargetAfterScreenshotPath <pc-b-target-after.png> \
  -SourceAfterScreenshotPath <pc-a-source-after.png> \
  -LiveHostExecutionChainPath <live-host-execution-chain.json> \
  -OutputDirectory <new-promoted-candidate-directory>

The tool never promotes in place. The promoted candidate contains a copy of the
original deferred release manifest and the strict verifier proves that no original
artifact changed except RELEASE-STATUS.txt. Publication evidence is added only
under publication-evidence/ and is jointly summarized by one manifest-bound
publication-authorization.json.

After promotion, independently run:

pwsh ./verify-closed-alpha-release.ps1 \
  -BundleDirectory <new-promoted-candidate-directory> \
  -RequirePublishAuthorized

Do not distribute broadly unless that strict command succeeds on the exact
promoted directory. CI synthetic promotion tests are qualification fixtures only;
they are never uploaded as release candidates and are not real authorization.
'@
[IO.File]::WriteAllText($readmeDestination,$readme.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
$artifacts=@(Get-ChildItem -LiteralPath $root -Recurse -File|Where-Object{[IO.Path]::GetFullPath($_.FullName) -ne [IO.Path]::GetFullPath($manifestPath)}|Sort-Object FullName|ForEach-Object{Require($null -eq $_.LinkType) "Release artifact cannot be a symbolic link: $($_.FullName)";[ordered]@{path=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/');byteSize=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()}});$manifest.artifacts=$artifacts
$identityAfter=[ordered]@{documentType=[string]$manifest.documentType;schemaVersion=[int]$manifest.schemaVersion;channel=[string]$manifest.channel;version=[string]$manifest.version;commitSha=[string]$manifest.commitSha;builtAtUtc=[string]$manifest.builtAtUtc;deployment=$manifest.deployment;publishAuthorization=$manifest.publishAuthorization}|ConvertTo-Json -Compress -Depth 8;Require([string]::Equals($identityBefore,$identityAfter,[StringComparison]::Ordinal)) 'Publication-boundary packaging changed immutable release identity or authorization state.'
[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));& $verifier -BundleDirectory $root
$final=[IO.File]::ReadAllText($manifestPath)|ConvertFrom-Json;$entry=@($final.artifacts|Where-Object{[string]$_.path -ceq 'promote-closed-alpha-publication.ps1'});Require($entry.Count -eq 1 -and [string]$entry[0].sha256 -ceq (Get-FileHash -LiteralPath $promotionDestination -Algorithm SHA256).Hash.ToUpperInvariant()) 'Final candidate does not byte-bind the publication promotion tool.';Require(@($final.artifacts|Where-Object{[string]$_.path -ceq 'PUBLICATION-AUTHORIZATION-README.txt'}).Count -eq 1) 'Final candidate does not manifest the publication authorization instructions.'
Write-Host;Write-Host '[OK] Deferred candidate contains its terminal publication-authorization boundary.';Write-Host "  Promotion tool SHA-256: $([string]$entry[0].sha256)";Write-Host "  Artifacts: $(@($final.artifacts).Count)";Write-Host '  Publish authorization changed: no';Write-Host '  Physical Bring Here: deferred'
