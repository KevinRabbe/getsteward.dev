[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [uint32]$SteamAppId,
    [string]$Version = '0.1.0-beta.1',
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$MakensisPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($SteamAppId -eq 0) { throw 'SteamAppId must be positive.' }
if ($SteamAppId -eq 480) { throw 'AppID 480 is development-only and cannot build a SafeWorld public beta.' }
if ([string]::IsNullOrWhiteSpace($Version) -or $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    throw 'Version must use letters, digits, dots, and hyphens.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src/SharedWorlds.Desktop/SharedWorlds.Desktop.csproj'
$installerScript = Join-Path $repoRoot 'installer/windows/SafeWorldInstaller.nsi'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$product = Join-Path $output 'product'

if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) {
    throw "OutputDirectory must be a fresh path: $output"
}
[IO.Directory]::CreateDirectory($product) | Out-Null

$publishArguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $product,
    '--nologo',
    '--verbosity', 'minimal',
    '-p:SafeWorldPublicAssemblyName=SafeWorld.Desktop',
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "SafeWorld publish failed with exit code $LASTEXITCODE." }

foreach ($required in @('SafeWorld.Desktop.exe', 'SafeWorld.Desktop.dll', 'steam_api64.dll', 'SharedWorlds.GameAdapters.Factorio.dll')) {
    if (-not [IO.File]::Exists((Join-Path $product $required))) {
        throw "SafeWorld product is missing $required."
    }
}
if ([IO.File]::Exists((Join-Path $product 'steam_appid.txt'))) {
    throw 'Public beta product must not contain steam_appid.txt.'
}

$steamConfiguration = [ordered]@{ schemaVersion = 1; steamAppId = $SteamAppId }
[IO.File]::WriteAllText(
    (Join-Path $product 'safeworld-steam.json'),
    ($steamConfiguration | ConvertTo-Json -Compress),
    [Text.UTF8Encoding]::new($false))
if ([IO.File]::Exists((Join-Path $product 'steward-steam.json'))) {
    throw 'Public beta product must not contain the legacy Steam configuration filename.'
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
$productFiles = @(Get-ChildItem -LiteralPath $product -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($product, $_.FullName).Replace('\\', '/')
        byteSize = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$manifest = [ordered]@{
    documentType = 'safeworld.public-beta-product'
    schemaVersion = 1
    sourceCommitSha = $commit
    version = $Version
    runtime = 'win-x64'
    steamAppId = $SteamAppId
    executable = 'SafeWorld.Desktop.exe'
    steamConfiguration = 'safeworld-steam.json'
    files = $productFiles
}
[IO.File]::WriteAllText(
    (Join-Path $output 'safeworld-public-beta.json'),
    ($manifest | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

# Generate the uninstaller from the exact product file set instead of recursively deleting the
# install directory. This preserves any unrelated file a user may have placed beside SafeWorld.
$uninstallIncludePath = Join-Path $output 'SafeWorld.UninstallFiles.nsh'
$uninstallLines = [Collections.Generic.List[string]]::new()
foreach ($entry in $productFiles) {
    $relativePath = ([string]$entry.path).Replace('/', '\')
    $uninstallLines.Add(('  Delete "$INSTDIR\{0}"' -f $relativePath))
}
$directories = @(Get-ChildItem -LiteralPath $product -Recurse -Directory |
    Sort-Object { $_.FullName.Length } -Descending)
foreach ($directory in $directories) {
    $relativePath = [IO.Path]::GetRelativePath($product, $directory.FullName)
    $uninstallLines.Add(('  RMDir "$INSTDIR\{0}"' -f $relativePath))
}
[IO.File]::WriteAllLines(
    $uninstallIncludePath,
    $uninstallLines,
    [Text.UTF8Encoding]::new($false))

if ([string]::IsNullOrWhiteSpace($MakensisPath)) {
    $candidate = Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'
    if ([IO.File]::Exists($candidate)) { $MakensisPath = $candidate }
}
if ([string]::IsNullOrWhiteSpace($MakensisPath) -or -not [IO.File]::Exists($MakensisPath)) {
    throw 'NSIS makensis.exe was not found.'
}

$installerPath = Join-Path $output "SafeWorld-Setup-$Version.exe"
& $MakensisPath `
    '/V3' `
    "/DPRODUCT_ROOT=$product" `
    "/DOUTPUT_FILE=$installerPath" `
    "/DPRODUCT_VERSION=$Version" `
    "/DUNINSTALL_FILES=$uninstallIncludePath" `
    $installerScript
if ($LASTEXITCODE -ne 0) { throw "SafeWorld installer build failed with exit code $LASTEXITCODE." }
if (-not [IO.File]::Exists($installerPath)) { throw 'SafeWorld installer was not produced.' }

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    (Join-Path $output "SafeWorld-Setup-$Version.sha256"),
    "$installerHash  $([IO.Path]::GetFileName($installerPath))`n",
    [Text.UTF8Encoding]::new($false))

Write-Host '[OK] SafeWorld installer built.'
Write-Host "Source: $commit"
Write-Host "Installer: $installerPath"
Write-Host "SHA-256: $installerHash"
