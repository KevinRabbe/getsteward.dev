[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Domain,
    [Parameter(Mandatory = $true)]
    [string]$OwnerDisplayName,
    [Parameter(Mandatory = $true)]
    [string]$FriendDisplayName,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'output')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

function Require-Command([string]$Name) {
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Fail "Required command '$Name' was not found."
    }
}

function New-Secret([int]$ByteCount = 32) {
    $bytes = [byte[]]::new($ByteCount)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    try {
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    }
}

function New-Friend([string]$DisplayName, [int]$Index) {
    $name = $DisplayName.Trim()
    if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 64 -or $name -notmatch '^[A-Za-z0-9 ._-]+$') {
        Fail "Tester display name '$DisplayName' must be 1-64 characters using letters, digits, spaces, dots, underscores, or hyphens."
    }
    $credential = 'st_friend_' + (New-Secret 32)
    $credentialBytes = [Text.Encoding]::UTF8.GetBytes($credential)
    try {
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($credentialBytes))
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($credentialBytes)
    }
    return [pscustomobject]@{
        Index = $Index
        Id = 'friend-' + [Guid]::NewGuid().ToString('N')
        DisplayName = $name
        Credential = $credential
        Hash = $digest
    }
}

function EnvValue([string]$Value) {
    return '"' + $Value.Replace('\\', '\\\\').Replace('"', '\\"') + '"'
}

function Docker([string[]]$Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        Fail "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Require-Command 'docker'
Docker @('compose', 'version')

$domainValue = $Domain.Trim().ToLowerInvariant().TrimEnd('.')
if ($domainValue.Length -gt 253 -or
    $domainValue -notmatch '^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$') {
    Fail 'Domain must be a public DNS hostname such as beta.example.com.'
}

$serverRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$packageRoot = [IO.Path]::GetFullPath((Join-Path $serverRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$envPath = Join-Path $serverRoot '.env'
$composePath = Join-Path $serverRoot 'docker-compose.yml'
$caddyPath = Join-Path $serverRoot 'Caddyfile'
$backendDirectory = Join-Path $serverRoot 'backend'
$backendMetadataPath = Join-Path $backendDirectory 'backend-metadata.json'
$backendTarFiles = @(Get-ChildItem -LiteralPath $backendDirectory -File -Filter '*.tar' -ErrorAction SilentlyContinue)
$genericClient = Join-Path $packageRoot 'client'

foreach ($path in @($composePath, $caddyPath, $backendMetadataPath)) {
    if (-not [IO.File]::Exists($path)) { Fail "Closed-beta server package is incomplete: $path" }
}
if ($backendTarFiles.Count -ne 1) { Fail 'Closed-beta server package must contain exactly one backend image TAR.' }
if (-not [IO.Directory]::Exists((Join-Path $genericClient 'app')) -or
    -not [IO.File]::Exists((Join-Path $genericClient 'Start-Steward.ps1'))) {
    Fail 'Closed-beta client package is missing from the beta kit.'
}
if ([IO.File]::Exists($envPath)) {
    Fail "This server package is already configured: $envPath. Use a fresh extracted beta kit instead of overwriting secrets."
}
if ([IO.Directory]::Exists($outputRoot) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count -gt 0) {
    Fail "Output directory is not empty: $outputRoot"
}

$backendMetadata = Get-Content -LiteralPath $backendMetadataPath -Raw | ConvertFrom-Json
$backendImage = [string]$backendMetadata.imageTag
if ([string]::IsNullOrWhiteSpace($backendImage) -or $backendImage -notmatch '^steward-backend:[a-z0-9][a-z0-9._-]{0,63}$') {
    Fail 'Backend image metadata is invalid.'
}

Write-Host 'Steward Closed Beta setup'
Write-Host "  Public API: https://$domainValue/"
Write-Host "  Backend: $backendImage"
Write-Host
Write-Host 'Pulling standard private-stack dependencies...'
$dependencyImages = @('postgres:17', 'minio/minio:latest', 'minio/mc:latest', 'caddy:2')
foreach ($image in $dependencyImages) { Docker @('pull', $image) }

Write-Host 'Loading exact Steward backend image...'
Docker @('load', '--input', $backendTarFiles[0].FullName)
$loadedImageId = (& docker image inspect $backendImage --format '{{.Id}}').Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $loadedImageId -notmatch '^sha256:[0-9a-f]{64}$') {
    Fail 'Loaded Steward backend image could not be inspected.'
}

$friend0 = New-Friend $OwnerDisplayName 0
$friend1 = New-Friend $FriendDisplayName 1
$postgresPassword = New-Secret 32
$minioAccessKey = 'stewardbeta'
$minioSecretKey = New-Secret 32

$envLines = @(
    'STEWARD_BETA_DOMAIN=' + (EnvValue $domainValue),
    'STEWARD_BACKEND_IMAGE=' + (EnvValue $backendImage),
    'POSTGRES_IMAGE=' + (EnvValue 'postgres:17'),
    'MINIO_IMAGE=' + (EnvValue 'minio/minio:latest'),
    'MINIO_MC_IMAGE=' + (EnvValue 'minio/mc:latest'),
    'CADDY_IMAGE=' + (EnvValue 'caddy:2'),
    'STEWARD_POSTGRES_PASSWORD=' + (EnvValue $postgresPassword),
    'STEWARD_MINIO_ACCESS_KEY=' + (EnvValue $minioAccessKey),
    'STEWARD_MINIO_SECRET_KEY=' + (EnvValue $minioSecretKey),
    'STEWARD_FRIEND_0_ID=' + (EnvValue $friend0.Id),
    'STEWARD_FRIEND_0_NAME=' + (EnvValue $friend0.DisplayName),
    'STEWARD_FRIEND_0_HASH=' + (EnvValue $friend0.Hash),
    'STEWARD_FRIEND_1_ID=' + (EnvValue $friend1.Id),
    'STEWARD_FRIEND_1_NAME=' + (EnvValue $friend1.DisplayName),
    'STEWARD_FRIEND_1_HASH=' + (EnvValue $friend1.Hash)
)
[IO.File]::WriteAllLines($envPath, $envLines, [Text.UTF8Encoding]::new($false))
if ($IsLinux -or $IsMacOS) { & chmod 600 $envPath }

$runtimeImages = [ordered]@{}
foreach ($image in $dependencyImages + @($backendImage)) {
    $id = (& docker image inspect $image --format '{{.Id}}').Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0 -or $id -notmatch '^sha256:[0-9a-f]{64}$') { Fail "Could not lock image '$image'." }
    $runtimeImages[$image] = $id
}
$lock = [ordered]@{
    documentType = 'steward.closed-beta-runtime-lock'
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    domain = $domainValue
    images = $runtimeImages
}
[IO.File]::WriteAllText(
    (Join-Path $serverRoot 'runtime-lock.json'),
    ($lock | ConvertTo-Json -Depth 4),
    [Text.UTF8Encoding]::new($false))

Write-Host 'Starting Steward backend stack...'
Push-Location $serverRoot
try {
    Docker @('compose', '--env-file', '.env', 'config', '--quiet')
    Docker @('compose', '--env-file', '.env', 'up', '-d')
}
finally {
    Pop-Location
}

$readyUri = "https://$domainValue/health/ready"
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
$ready = $false
Write-Host "Waiting for public readiness: $readyUri"
do {
    try {
        $response = Invoke-WebRequest -Uri $readyUri -Method Get -TimeoutSec 10
        if ($response.StatusCode -eq 200) { $ready = $true; break }
    }
    catch {
        Start-Sleep -Seconds 5
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

if (-not $ready) {
    Write-Host
    Write-Host 'Steward containers are running, but public HTTPS is not ready.'
    Write-Host 'Confirm the DNS A record points to this server and ports 80/443 reach this host.'
    Write-Host "Then test: $readyUri"
    Fail 'Public Steward beta endpoint did not become ready within five minutes.'
}

[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$configuredClient = Join-Path $outputRoot 'Steward-ClosedBeta-Client'
Copy-Item -LiteralPath $genericClient -Destination $configuredClient -Recurse
$config = [ordered]@{
    schemaVersion = 1
    apiBaseUrl = "https://$domainValue/"
    authMode = 'friends-build'
}
[IO.File]::WriteAllText(
    (Join-Path $configuredClient 'steward-beta.json'),
    ($config | ConvertTo-Json -Depth 3),
    [Text.UTF8Encoding]::new($false))

$clientZip = Join-Path $outputRoot 'Steward-ClosedBeta-Client.zip'
Compress-Archive -LiteralPath $configuredClient -DestinationPath $clientZip -CompressionLevel Optimal
$clientHash = (Get-FileHash -LiteralPath $clientZip -Algorithm SHA256).Hash.ToUpperInvariant()
[IO.File]::WriteAllText(
    "$clientZip.sha256",
    "$clientHash  $([IO.Path]::GetFileName($clientZip))`n",
    [Text.UTF8Encoding]::new($false))

$credentialDirectory = Join-Path $outputRoot 'credentials'
[IO.Directory]::CreateDirectory($credentialDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $credentialDirectory 'owner.txt'), $friend0.Credential, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $credentialDirectory 'friend.txt'), $friend1.Credential, [Text.UTF8Encoding]::new($false))
if ($IsLinux -or $IsMacOS) { & chmod 700 $credentialDirectory; & chmod 600 (Join-Path $credentialDirectory '*.txt') }

Write-Host
Write-Host '[OK] Steward Closed Beta is live.'
Write-Host "  API: https://$domainValue/"
Write-Host "  Client ZIP: $clientZip"
Write-Host "  Client SHA-256: $clientHash"
Write-Host "  Your credential: $(Join-Path $credentialDirectory 'owner.txt')"
Write-Host "  Friend credential: $(Join-Path $credentialDirectory 'friend.txt')"
Write-Host
Write-Host 'Send your friend ONLY the configured client ZIP and their dedicated credential.'
