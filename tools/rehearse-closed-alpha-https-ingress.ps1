[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Require-Tool([string]$Name) {
    Require ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue)) "Required tool '$Name' is not available."
}

function Invoke-Docker([string[]]$Arguments, [switch]$Capture) {
    $output = @(& docker @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $text = ($output -join [Environment]::NewLine).Trim()
        throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE. $text"
    }

    if ($Capture.IsPresent) {
        return ($output -join [Environment]::NewLine).Trim()
    }

    foreach ($line in $output) {
        Write-Host $line
    }
}

function Remove-ContainerIfPresent([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        return
    }

    & docker container inspect $Name *> $null
    if ($LASTEXITCODE -eq 0) {
        & docker rm --force $Name *> $null
    }
}

function Remove-VolumeIfPresent([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        return
    }

    & docker volume inspect $Name *> $null
    if ($LASTEXITCODE -eq 0) {
        & docker volume rm --force $Name *> $null
    }
}

function Wait-PostgreSql(
    [string]$ContainerName,
    [string]$DatabaseName,
    [int]$Port) {
    for ($attempt = 1; $attempt -le 90; $attempt++) {
        & docker exec $ContainerName pg_isready `
            --username postgres `
            --dbname $DatabaseName `
            --port $Port *> $null
        if ($LASTEXITCODE -eq 0) {
            return
        }

        Start-Sleep -Seconds 1
    }

    $logs = @(& docker logs $ContainerName 2>&1) -join [Environment]::NewLine
    throw "PostgreSQL did not become ready. $logs"
}

function Wait-HttpOk([Uri]$Uri, [string]$Context, [string]$ContainerName) {
    for ($attempt = 1; $attempt -le 90; $attempt++) {
        try {
            $response = Invoke-WebRequest `
                -Uri $Uri `
                -Method Get `
                -TimeoutSec 2 `
                -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            # The service may still be starting.
        }

        Start-Sleep -Seconds 1
    }

    $logs = @(& docker logs $ContainerName 2>&1) -join [Environment]::NewLine
    throw "$Context did not become ready at '$Uri'. $logs"
}

function Wait-CaddyAuthority(
    [string]$ContainerName,
    [string]$DestinationPath) {
    for ($attempt = 1; $attempt -le 90; $attempt++) {
        & docker cp `
            "${ContainerName}:/data/caddy/pki/authorities/local/root.crt" `
            $DestinationPath *> $null
        if ($LASTEXITCODE -eq 0 -and [IO.File]::Exists($DestinationPath)) {
            return
        }

        Start-Sleep -Seconds 1
    }

    $logs = @(& docker logs $ContainerName 2>&1) -join [Environment]::NewLine
    throw "Caddy did not create its local root authority certificate. $logs"
}

function Invoke-TrustedJsonRequest(
    [string]$Method,
    [Uri]$Uri,
    [string]$CaCertificatePath,
    [object]$Body,
    [hashtable]$Headers = @{}) {
    $requestId = [Guid]::NewGuid().ToString('N')
    $responsePath = Join-Path ([IO.Path]::GetTempPath()) "steward-https-response-$requestId.json"
    $bodyPath = Join-Path ([IO.Path]::GetTempPath()) "steward-https-body-$requestId.json"

    try {
        $arguments = [Collections.Generic.List[string]]::new()
        foreach ($argument in @(
            '--silent',
            '--show-error',
            '--cacert', $CaCertificatePath,
            '--request', $Method,
            '--output', $responsePath,
            '--write-out', '%{http_code}')) {
            $arguments.Add($argument)
        }

        foreach ($name in $Headers.Keys) {
            $arguments.Add('--header')
            $arguments.Add("${name}: $($Headers[$name])")
        }

        if ($null -ne $Body) {
            [IO.File]::WriteAllText(
                $bodyPath,
                ($Body | ConvertTo-Json -Compress -Depth 8),
                [Text.UTF8Encoding]::new($false))
            $arguments.Add('--header')
            $arguments.Add('Content-Type: application/json')
            $arguments.Add('--data-binary')
            $arguments.Add("@$bodyPath")
        }

        $arguments.Add($Uri.AbsoluteUri)
        $statusText = @(& curl @arguments 2>&1) -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) {
            throw "Trusted HTTPS request to '$Uri' failed with curl exit code $LASTEXITCODE. $statusText"
        }

        $statusCode = 0
        Require ([int]::TryParse($statusText.Trim(), [ref]$statusCode)) "Trusted HTTPS request to '$Uri' returned a malformed status code."
        $json = $null
        if ([IO.File]::Exists($responsePath)) {
            $responseText = [IO.File]::ReadAllText($responsePath)
            if (-not [string]::IsNullOrWhiteSpace($responseText)) {
                $json = $responseText | ConvertFrom-Json
            }
        }

        return [pscustomobject]@{
            StatusCode = $statusCode
            Json = $json
        }
    }
    finally {
        Remove-Item -LiteralPath $responsePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $bodyPath -Force -ErrorAction SilentlyContinue
    }
}

Require-Tool 'docker'
Require-Tool 'curl'

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = [IO.Path]::GetDirectoryName($evidenceFullPath)
Require (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) 'EvidencePath must include a parent directory.'
[IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null

$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
& $verifierPath -BundleDirectory $bundleRoot

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$backendArtifacts = @($manifest.artifacts | Where-Object {
    ([string]$_.path).StartsWith('backend/', [StringComparison]::Ordinal) -and
    ([string]$_.path).EndsWith('.tar', [StringComparison]::OrdinalIgnoreCase)
})
Require ($backendArtifacts.Count -eq 1) 'The release bundle must contain exactly one backend image TAR.'
$backendRelativePath = [string]$backendArtifacts[0].path
$backendTarPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot $backendRelativePath))
Require ($backendTarPath.StartsWith($bundleRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'The backend image TAR escapes the bundle root.'
Require ([IO.File]::Exists($backendTarPath)) 'The backend image TAR is missing.'

$imageId = [string]$manifest.deployment.backendImageId
$imageTag = [string]$manifest.deployment.backendImageTag
$releaseCommit = [string]$manifest.commitSha
$releaseVersion = [string]$manifest.version
Require ($imageId -match '^sha256:[0-9a-f]{64}$') 'The manifest backend image ID is malformed.'
Require ([string]$manifest.deployment.authenticationMode -ceq 'friends-build') 'The HTTPS ingress rehearsal requires Friends Build authentication.'
Require ([int]$manifest.deployment.postgresMajorVersion -eq 17) 'The HTTPS ingress rehearsal requires PostgreSQL 17.'
Require ([string]$manifest.deployment.objectStorageProtocol -ceq 's3-compatible') 'The HTTPS ingress rehearsal requires S3-compatible storage.'

Invoke-Docker -Arguments @('load', '--input', $backendTarPath)
$loadedImageId = Invoke-Docker -Arguments @('image', 'inspect', $imageTag, '--format', '{{.Id}}') -Capture
$loadedRuntimeUser = Invoke-Docker -Arguments @('image', 'inspect', $imageTag, '--format', '{{.Config.User}}') -Capture
$loadedPlatform = Invoke-Docker -Arguments @('image', 'inspect', $imageTag, '--format', '{{.Os}}/{{.Architecture}}') -Capture
$loadedRevision = Invoke-Docker -Arguments @('image', 'inspect', $imageTag, '--format', '{{ index .Config.Labels "org.opencontainers.image.revision" }}') -Capture
$loadedVersion = Invoke-Docker -Arguments @('image', 'inspect', $imageTag, '--format', '{{ index .Config.Labels "org.opencontainers.image.version" }}') -Capture
Require ([string]::Equals($loadedImageId, $imageId, [StringComparison]::Ordinal)) 'The loaded image ID does not match release-manifest.json.'
Require ([string]::Equals($loadedRuntimeUser, 'app', [StringComparison]::Ordinal)) 'The loaded image does not use runtime user app.'
Require ([string]::Equals($loadedPlatform, 'linux/amd64', [StringComparison]::Ordinal)) 'The loaded image is not linux/amd64.'
Require ([string]::Equals($loadedRevision, $releaseCommit, [StringComparison]::Ordinal)) 'The loaded image revision label does not match the release commit.'
Require ([string]::Equals($loadedVersion, $releaseVersion, [StringComparison]::Ordinal)) 'The loaded image version label does not match the release version.'

$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$postgresName = "steward-https-postgres-$suffix"
$minioName = "steward-https-minio-$suffix"
$backendName = "steward-https-backend-$suffix"
$caddyName = "steward-https-caddy-$suffix"
$caddyDataVolume = "steward-https-caddy-data-$suffix"
$caddyConfigVolume = "steward-https-caddy-config-$suffix"
$databaseName = 'steward_closed_alpha_https_rehearsal'
$bucketName = 'steward-closed-alpha-https-rehearsal'
$postgreSqlPort = 15433
$minioPort = 19001
$backendPort = 18081
$httpsPort = 18443
$connectionString = "Host=127.0.0.1;Port=$postgreSqlPort;Database=$databaseName;Username=postgres;Password=postgres;Pooling=false"
$objectStorageServiceUrl = [Uri]"http://127.0.0.1:$minioPort/"
$httpsBaseUri = [Uri]"https://localhost:$httpsPort/"
$caddyFilePath = Join-Path ([IO.Path]::GetTempPath()) "steward-caddy-$suffix.Caddyfile"
$caCertificatePath = Join-Path ([IO.Path]::GetTempPath()) "steward-caddy-root-$suffix.crt"
$secretBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($secretBytes)
$encodedSecret = [Convert]::ToBase64String($secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$credential = "st_friend_$encodedSecret"
$credentialHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($credential)))

try {
    Invoke-Docker -Arguments @(
        'run',
        '--detach',
        '--name', $postgresName,
        '--network', 'host',
        '--env', 'POSTGRES_USER=postgres',
        '--env', 'POSTGRES_PASSWORD=postgres',
        '--env', "POSTGRES_DB=$databaseName",
        'postgres:17-alpine',
        '-c', "port=$postgreSqlPort"
    )
    Wait-PostgreSql $postgresName $databaseName $postgreSqlPort
    $postgresVersionNumberText = Invoke-Docker -Arguments @(
        'exec',
        $postgresName,
        'psql',
        '--username', 'postgres',
        '--dbname', $databaseName,
        '--port', $postgreSqlPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--no-psqlrc',
        '--tuples-only',
        '--no-align',
        '--set', 'ON_ERROR_STOP=1',
        '--command', "SELECT current_setting('server_version_num');"
    ) -Capture
    $postgresVersionNumber = [int]$postgresVersionNumberText.Trim()
    Require ($postgresVersionNumber -ge 170000 -and $postgresVersionNumber -lt 180000) "Disposable database is PostgreSQL version number $postgresVersionNumber, expected major version 17."
    $postgresImageId = Invoke-Docker -Arguments @('container', 'inspect', $postgresName, '--format', '{{.Image}}') -Capture

    Invoke-Docker -Arguments @(
        'run',
        '--detach',
        '--name', $minioName,
        '--network', 'host',
        '--env', 'MINIO_ROOT_USER=minioadmin',
        '--env', 'MINIO_ROOT_PASSWORD=minioadmin',
        'quay.io/minio/minio:latest',
        'server', '/data', '--address', ":$minioPort"
    )
    Wait-HttpOk ([Uri]::new($objectStorageServiceUrl, 'minio/health/ready')) 'MinIO readiness' $minioName
    $minioImageId = Invoke-Docker -Arguments @('container', 'inspect', $minioName, '--format', '{{.Image}}') -Capture

    Invoke-Docker -Arguments @(
        'run',
        '--rm',
        '--network', 'host',
        '--entrypoint', '/bin/sh',
        'quay.io/minio/mc:latest',
        '-c',
        "mc alias set local $($objectStorageServiceUrl.AbsoluteUri) minioadmin minioadmin >/dev/null && mc mb --ignore-existing local/$bucketName >/dev/null"
    )

    Invoke-Docker -Arguments @(
        'run',
        '--detach',
        '--name', $backendName,
        '--network', 'host',
        '--env', "PORT=$backendPort",
        '--env', "ConnectionStrings__Steward=$connectionString",
        '--env', "ObjectStorage__ServiceUrl=$($objectStorageServiceUrl.AbsoluteUri)",
        '--env', 'ObjectStorage__AuthenticationRegion=us-east-1',
        '--env', "ObjectStorage__BucketName=$bucketName",
        '--env', 'ObjectStorage__AccessKeyId=minioadmin',
        '--env', 'ObjectStorage__SecretAccessKey=minioadmin',
        '--env', 'ObjectStorage__ForcePathStyle=true',
        '--env', 'FriendsBuild__Enabled=true',
        '--env', 'FriendsBuild__Identities__0__Id=closed-alpha-https-rehearsal',
        '--env', 'FriendsBuild__Identities__0__DisplayName=Closed Alpha HTTPS Rehearsal',
        '--env', "FriendsBuild__Identities__0__CredentialSha256=$credentialHash",
        '--env', 'ReverseProxy__KnownProxyIp=127.0.0.1',
        $imageId
    )
    $backendBaseUri = [Uri]"http://127.0.0.1:$backendPort/"
    Wait-HttpOk ([Uri]::new($backendBaseUri, 'health/live')) 'Backend direct liveness' $backendName
    Wait-HttpOk ([Uri]::new($backendBaseUri, 'health/ready')) 'Backend direct readiness' $backendName
    $runningImageId = Invoke-Docker -Arguments @('container', 'inspect', $backendName, '--format', '{{.Image}}') -Capture
    $runningUser = Invoke-Docker -Arguments @('container', 'inspect', $backendName, '--format', '{{.Config.User}}') -Capture
    Require ([string]::Equals($runningImageId, $imageId, [StringComparison]::Ordinal)) 'HTTPS rehearsal did not run the exact release image ID.'
    Require ([string]::Equals($runningUser, 'app', [StringComparison]::Ordinal)) 'HTTPS rehearsal backend does not run as app.'

    $caddyFile = @"
{
    auto_https disable_redirects
}

https://localhost:$httpsPort {
    tls internal
    reverse_proxy 127.0.0.1:$backendPort
}
"@
    [IO.File]::WriteAllText($caddyFilePath, $caddyFile, [Text.UTF8Encoding]::new($false))
    Invoke-Docker -Arguments @('volume', 'create', $caddyDataVolume)
    Invoke-Docker -Arguments @('volume', 'create', $caddyConfigVolume)
    Invoke-Docker -Arguments @(
        'run',
        '--detach',
        '--name', $caddyName,
        '--network', 'host',
        '--volume', "${caddyFilePath}:/etc/caddy/Caddyfile:ro",
        '--volume', "${caddyDataVolume}:/data",
        '--volume', "${caddyConfigVolume}:/config",
        'caddy:2-alpine',
        'caddy', 'run', '--config', '/etc/caddy/Caddyfile', '--adapter', 'caddyfile'
    )
    Wait-CaddyAuthority $caddyName $caCertificatePath

    & curl `
        --silent `
        --show-error `
        --output /dev/null `
        ([Uri]::new($httpsBaseUri, 'health/live').AbsoluteUri) 2> $null
    $untrustedCertificateRejected = $LASTEXITCODE -ne 0
    Require $untrustedCertificateRejected 'HTTPS ingress unexpectedly succeeded without trusting the disposable Caddy authority.'

    $httpsLiveness = Invoke-TrustedJsonRequest `
        -Method Get `
        -Uri ([Uri]::new($httpsBaseUri, 'health/live')) `
        -CaCertificatePath $caCertificatePath `
        -Body $null
    Require ($httpsLiveness.StatusCode -eq 200) "Trusted HTTPS liveness returned HTTP $($httpsLiveness.StatusCode)."

    $httpsReadiness = Invoke-TrustedJsonRequest `
        -Method Get `
        -Uri ([Uri]::new($httpsBaseUri, 'health/ready')) `
        -CaCertificatePath $caCertificatePath `
        -Body $null
    Require ($httpsReadiness.StatusCode -eq 200) "Trusted HTTPS readiness returned HTTP $($httpsReadiness.StatusCode)."

    $httpsAuth = Invoke-TrustedJsonRequest `
        -Method Post `
        -Uri ([Uri]::new($httpsBaseUri, 'api/v1/auth/friends/session')) `
        -CaCertificatePath $caCertificatePath `
        -Body ([ordered]@{
            credential = $credential
            installationId = 'closed-alpha-https-ingress'
        })
    Require ($httpsAuth.StatusCode -eq 200) "HTTPS Friends Build authentication returned HTTP $($httpsAuth.StatusCode)."
    Require ([string]$httpsAuth.Json.code -ceq 'Authenticated') 'HTTPS Friends Build authentication returned the wrong code.'
    $accessToken = [string]$httpsAuth.Json.data.tokens.accessToken
    Require (-not [string]::IsNullOrWhiteSpace($accessToken)) 'HTTPS authentication returned no access token.'

    $httpsRoster = Invoke-TrustedJsonRequest `
        -Method Get `
        -Uri ([Uri]::new($httpsBaseUri, 'api/v1/auth/friends/identities')) `
        -CaCertificatePath $caCertificatePath `
        -Body $null `
        -Headers @{ Authorization = "Bearer $accessToken" }
    Require ($httpsRoster.StatusCode -eq 200) "HTTPS authenticated roster returned HTTP $($httpsRoster.StatusCode)."
    Require ([string]$httpsRoster.Json.code -ceq 'FriendsBuildIdentitiesFound') 'HTTPS authenticated roster returned the wrong code.'

    $caddyImageId = Invoke-Docker -Arguments @('container', 'inspect', $caddyName, '--format', '{{.Image}}') -Capture
    Require ($caddyImageId -match '^sha256:[0-9a-f]{64}$') 'Disposable Caddy image ID is malformed.'
    $caCertificateSha256 = (Get-FileHash -LiteralPath $caCertificatePath -Algorithm SHA256).Hash

    $evidence = [ordered]@{
        documentType = 'steward.closed-alpha-https-ingress-rehearsal'
        schemaVersion = 1
        release = [ordered]@{
            version = $releaseVersion
            commitSha = $releaseCommit
            backendImageTag = $imageTag
            backendImageId = $imageId
            backendTarPath = $backendRelativePath
        }
        infrastructure = [ordered]@{
            networkMode = 'host-loopback'
            postgresImage = 'postgres:17-alpine'
            postgresImageId = $postgresImageId
            postgresVersionNumber = $postgresVersionNumber
            objectStorageImage = 'quay.io/minio/minio:latest'
            objectStorageImageId = $minioImageId
            objectStorageTransport = 'http-loopback-only'
            caddyImage = 'caddy:2-alpine'
            caddyImageId = $caddyImageId
        }
        backend = [ordered]@{
            runtimeUser = $runningUser
            exactImageLoaded = $true
            exactImageRunById = $true
            directLiveness = 'passed'
            directReadiness = 'passed'
            knownProxyIp = '127.0.0.1'
        }
        httpsIngress = [ordered]@{
            apiBaseUrl = $httpsBaseUri.AbsoluteUri
            tlsMode = 'caddy-internal-disposable-ca'
            caCertificateSha256 = $caCertificateSha256
            untrustedCertificateRejected = $untrustedCertificateRejected
            trustedLiveness = 'passed'
            trustedReadiness = 'passed'
            friendsAuthentication = 'passed'
            authenticatedRoster = 'passed'
        }
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText(
        $evidenceFullPath,
        ($evidence | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Closed-alpha HTTPS ingress rehearsal passed with the exact bundled backend image.'
    Write-Host "  Release: $releaseVersion ($releaseCommit)"
    Write-Host "  Backend image ID: $imageId"
    Write-Host "  HTTPS API: $($httpsBaseUri.AbsoluteUri)"
    Write-Host "  Disposable CA SHA-256: $caCertificateSha256"
    Write-Host "  Evidence: $evidenceFullPath"
}
finally {
    Remove-ContainerIfPresent $caddyName
    Remove-ContainerIfPresent $backendName
    Remove-ContainerIfPresent $minioName
    Remove-ContainerIfPresent $postgresName
    Remove-VolumeIfPresent $caddyConfigVolume
    Remove-VolumeIfPresent $caddyDataVolume
    Remove-Item -LiteralPath $caddyFilePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $caCertificatePath -Force -ErrorAction SilentlyContinue

    [Array]::Clear($secretBytes, 0, $secretBytes.Length)
    $credential = $null
    $accessToken = $null
}
