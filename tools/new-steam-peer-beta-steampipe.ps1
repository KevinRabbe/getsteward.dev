[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [uint32]$SteamAppId,
    [Parameter(Mandatory = $true)]
    [uint32]$WindowsDepotId,
    [Parameter(Mandatory = $true)]
    [string]$ProductDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$BuildOutputDirectory,
    [string]$BuildDescription = 'Steward peer closed beta RC'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

function Assert-SafeVdfValue([string]$Value, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.IndexOfAny([char[]]@('"', "`r", "`n", "`t")) -ge 0) {
        Fail "$Name contains characters that are unsafe for a generated SteamPipe VDF value."
    }
}

function Get-FullPath([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        Fail "$Name is required."
    }

    try {
        return [IO.Path]::GetFullPath($Path)
    }
    catch {
        Fail "$Name is not a valid filesystem path: $Path"
    }
}

function Test-IsInside([string]$Candidate, [string]$Parent) {
    $normalizedParent = $Parent.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Candidate.StartsWith($normalizedParent, [StringComparison]::OrdinalIgnoreCase)
}

function Get-ManifestRelativePath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or [IO.Path]::IsPathRooted($Value)) {
        Fail "Acceptance manifest contains an invalid package path: '$Value'."
    }

    $normalized = $Value.Replace('/', [IO.Path]::DirectorySeparatorChar)
    foreach ($segment in $normalized.Split([IO.Path]::DirectorySeparatorChar)) {
        if ([string]::Equals($segment, '..', [StringComparison]::Ordinal)) {
            Fail "Acceptance manifest package path escapes product root: '$Value'."
        }
    }

    return $normalized
}

if ($SteamAppId -eq 0) {
    Fail 'SteamAppId must be a positive UInt32.'
}
if ($WindowsDepotId -eq 0) {
    Fail 'WindowsDepotId must be a positive UInt32.'
}
if ($BuildDescription.Length -gt 128) {
    Fail 'BuildDescription must be 1-128 characters.'
}
Assert-SafeVdfValue $BuildDescription 'BuildDescription'

$product = Get-FullPath $ProductDirectory 'ProductDirectory'
if (-not [IO.Directory]::Exists($product)) {
    Fail "ProductDirectory does not exist: $product"
}

$output = Get-FullPath $OutputDirectory 'OutputDirectory'
if (Test-IsInside $output $product -or [string]::Equals($output, $product, [StringComparison]::OrdinalIgnoreCase)) {
    Fail 'OutputDirectory must be outside the immutable product directory.'
}

$buildOutput = if ([string]::IsNullOrWhiteSpace($BuildOutputDirectory)) {
    Join-Path $output 'build-output'
}
else {
    Get-FullPath $BuildOutputDirectory 'BuildOutputDirectory'
}
if (Test-IsInside $buildOutput $product -or [string]::Equals($buildOutput, $product, [StringComparison]::OrdinalIgnoreCase)) {
    Fail 'BuildOutputDirectory must be outside the immutable product directory.'
}

Assert-SafeVdfValue $product 'ProductDirectory'
Assert-SafeVdfValue $buildOutput 'BuildOutputDirectory'

$manifestPath = Join-Path $product 'acceptance-build.json'
$steamConfigPath = Join-Path $product 'steward-steam.json'
$desktopExecutable = Join-Path $product 'SharedWorlds.Desktop.exe'
foreach ($required in @($manifestPath, $steamConfigPath, $desktopExecutable)) {
    if (-not [IO.File]::Exists($required)) {
        Fail "Required normal peer product file is missing: $required"
    }
}

foreach ($legacyFile in @('steward-steam-release.json', 'steward-friends-build.json')) {
    $legacyPath = Join-Path $product $legacyFile
    if ([IO.File]::Exists($legacyPath)) {
        Fail "Normal Steam peer product unexpectedly contains migration/legacy routing file: $legacyFile"
    }
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    Fail "Could not parse acceptance-build.json: $($_.Exception.Message)"
}
if ($manifest.documentType -ne 'steward.e4-desktop-acceptance-build' -or [int]$manifest.schemaVersion -ne 2) {
    Fail 'acceptance-build.json is not the expected Steward E4 schema-2 product manifest.'
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.commitSha)) {
    Fail 'acceptance-build.json does not contain a product commit SHA.'
}

try {
    $steamConfig = Get-Content -LiteralPath $steamConfigPath -Raw | ConvertFrom-Json
}
catch {
    Fail "Could not parse steward-steam.json: $($_.Exception.Message)"
}
$configProperties = @($steamConfig.PSObject.Properties.Name | Sort-Object)
$expectedProperties = @('schemaVersion', 'steamAppId' | Sort-Object)
if ($configProperties.Count -ne 2 -or
    $configProperties[0] -ne $expectedProperties[0] -or
    $configProperties[1] -ne $expectedProperties[1]) {
    Fail 'steward-steam.json must contain exactly schemaVersion and steamAppId.'
}
if ([int]$steamConfig.schemaVersion -ne 1 -or [uint32]$steamConfig.steamAppId -ne $SteamAppId) {
    Fail "steward-steam.json AppID does not match requested SteamAppId $SteamAppId."
}

$manifestFiles = @($manifest.files)
if ($manifestFiles.Count -eq 0) {
    Fail 'acceptance-build.json contains no package files.'
}

$expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifestFiles) {
    $relative = Get-ManifestRelativePath ([string]$entry.path)
    $relativeSlash = $relative.Replace([IO.Path]::DirectorySeparatorChar, '/')
    if (-not $expectedPaths.Add($relativeSlash)) {
        Fail "acceptance-build.json contains duplicate package path: $relativeSlash"
    }

    $fullPath = [IO.Path]::GetFullPath((Join-Path $product $relative))
    if (-not (Test-IsInside $fullPath $product)) {
        Fail "Acceptance manifest package path escaped product root: $relativeSlash"
    }
    if (-not [IO.File]::Exists($fullPath)) {
        Fail "Manifested product file is missing: $relativeSlash"
    }

    $file = Get-Item -LiteralPath $fullPath
    if ([long]$entry.byteSize -ne $file.Length) {
        Fail "Manifested product file size mismatch: $relativeSlash"
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "Manifested product file SHA-256 mismatch: $relativeSlash"
    }
}

$actualProductFiles = @(Get-ChildItem -LiteralPath $product -Recurse -File |
    Where-Object { -not [string]::Equals($_.FullName, $manifestPath, [StringComparison]::OrdinalIgnoreCase) })
if ($actualProductFiles.Count -ne $expectedPaths.Count) {
    Fail "Product contains unmanifested or missing files. Manifest: $($expectedPaths.Count); actual: $($actualProductFiles.Count)."
}
foreach ($file in $actualProductFiles) {
    $relative = [IO.Path]::GetRelativePath($product, $file.FullName).Replace('\', '/')
    if (-not $expectedPaths.Contains($relative)) {
        Fail "Product contains unmanifested file: $relative"
    }
}

if ([IO.Directory]::Exists($output)) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.Directory]::CreateDirectory($buildOutput) | Out-Null

function New-AppBuildVdf([bool]$Preview) {
    $previewLine = if ($Preview) { '    "Preview" "1"' } else { $null }
    $lines = @(
        '"AppBuild"',
        '{',
        "    `"AppID`" `"$SteamAppId`"",
        "    `"Desc`" `"$BuildDescription`"",
        "    `"ContentRoot`" `"$product`"",
        "    `"BuildOutput`" `"$buildOutput`""
    )
    if ($null -ne $previewLine) {
        $lines += $previewLine
    }
    $lines += @(
        '    "Depots"',
        '    {',
        "        `"$WindowsDepotId`"",
        '        {',
        '            "FileMapping"',
        '            {',
        '                "LocalPath" "*"',
        '                "DepotPath" "."',
        '                "Recursive" "1"',
        '            }',
        '        }',
        '    }',
        '}'
    )
    return ($lines -join [Environment]::NewLine) + [Environment]::NewLine
}

$previewPath = Join-Path $output "app_build_${SteamAppId}_preview.vdf"
$uploadPath = Join-Path $output "app_build_${SteamAppId}_upload.vdf"
[IO.File]::WriteAllText($previewPath, (New-AppBuildVdf $true), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($uploadPath, (New-AppBuildVdf $false), [Text.UTF8Encoding]::new($false))

$evidence = [ordered]@{
    documentType = 'steward.steampipe-rc-input'
    schemaVersion = 1
    steamAppId = $SteamAppId
    windowsDepotId = $WindowsDepotId
    productCommitSha = [string]$manifest.commitSha
    productManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    productDirectory = $product
    buildOutputDirectory = $buildOutput
    buildDescription = $BuildDescription
    previewVdf = [IO.Path]::GetFileName($previewPath)
    previewVdfSha256 = (Get-FileHash -LiteralPath $previewPath -Algorithm SHA256).Hash
    uploadVdf = [IO.Path]::GetFileName($uploadPath)
    uploadVdfSha256 = (Get-FileHash -LiteralPath $uploadPath -Algorithm SHA256).Hash
}
$evidencePath = Join-Path $output 'steampipe-rc-input.json'
$evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8

Write-Host '[OK] SteamPipe RC inputs generated from a byte-verified normal Steward peer product.'
Write-Host "  Product commit: $($manifest.commitSha)"
Write-Host "  Steam AppID: $SteamAppId"
Write-Host "  Windows DepotID: $WindowsDepotId"
Write-Host "  Preview VDF: $previewPath"
Write-Host "  Upload VDF: $uploadPath"
Write-Host "  Evidence: $evidencePath"
Write-Host
Write-Host 'Run the preview VDF first. Upload only after its SteamPipe file manifest is correct.'
Write-Host 'This tool never accepts credentials, product keys, branch passwords, or SetLive configuration.'
