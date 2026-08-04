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

function Invoke-PostgreSqlScalar(
    [string]$ContainerName,
    [string]$DatabaseName,
    [int]$Port,
    [string]$Sql) {
    $value = Invoke-Docker -Arguments @(
        'exec',
        $ContainerName,
        'psql',
        '--username', 'postgres',
        '--dbname', $DatabaseName,
        '--port', $Port.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--no-psqlrc',
        '--tuples-only',
        '--no-align',
        '--set', 'ON_ERROR_STOP=1',
        '--command', $Sql
    ) -Capture
    return $value.Trim()
}

function Invoke-JsonRequest(
    [string]$Method,
    [Uri]$Uri,
    [object]$Body,
    [hashtable]$Headers = @{}) {
    $parameters = @{
        Uri = $Uri
        Method = $Method
        Headers = $Headers
        TimeoutSec = 10
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Compress -Depth 8
    }

    $response = Invoke-WebRequest @parameters
    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
        $json = $response.Content | ConvertFrom-Json
    }

    return [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Json = $json
    }
}

function Start-Backend(
    [string]$ContainerName,
    [string]$ImageId,
    [string]$ConnectionString,
    [Uri]$ObjectStorageServiceUrl,
    [string]$BucketName,
    [string]$CredentialHash,
    [int]$Port) {
    Remove-ContainerIfPresent $ContainerName

    Invoke-Docker -Arguments @(
        'run',
        '--detach',
        '--name', $ContainerName,
        '--network', 'host',
        '--env', "PORT=$Port",
        '--env', "ConnectionStrings__Steward=$ConnectionString",
        '--env', "ObjectStorage__ServiceUrl=$($ObjectStorageServiceUrl.AbsoluteUri)",
        '--env', 'ObjectStorage__AuthenticationRegion=us-east-1',
        '--env', "ObjectStorage__BucketName=$BucketName",
        '--env', 'ObjectStorage__AccessKeyId=minioadmin',
        '--env', 'ObjectStorage__SecretAccessKey=minioadmin',
        '--env', 'ObjectStorage__ForcePathStyle=true',
        '--env', 'FriendsBuild__Enabled=true',
        '--env', 'FriendsBuild__Identities__0__Id=closed-alpha-rehearsal',
        '--env', 'FriendsBuild__Identities__0__DisplayName=Closed Alpha Rehearsal',
        '--env', "FriendsBuild__Identities__0__CredentialSha256=$CredentialHash",
        $ImageId
    )

    $baseUri = [Uri]"http://127.0.0.1:$Port/"
    Wait-HttpOk ([Uri]::new($baseUri, 'health/live')) 'Backend liveness' $ContainerName
    Wait-HttpOk ([Uri]::new($baseUri, 'health/ready')) 'Backend readiness' $ContainerName

    $runtimeUser = Invoke-Docker -Arguments @(
        'container',
        'inspect',
        $ContainerName,
        '--format', '{{.Config.User}}'
    ) -Capture
    Require ([string]::Equals($runtimeUser, 'app', [StringComparison]::Ordinal)) "Backend container runs as '$runtimeUser', expected 'app'."

    return $baseUri
}

Require-Tool 'docker'

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
Require (-not [string]::IsNullOrWhiteSpace($imageTag)) 'The manifest backend image tag is missing.'
Require ([int]$manifest.deployment.postgresMajorVersion -eq 17) 'The deployment rehearsal requires PostgreSQL 17.'
Require ([string]$manifest.deployment.objectStorageProtocol -ceq 's3-compatible') 'The deployment rehearsal requires S3-compatible storage.'

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
$postgresName = "steward-alpha-postgres-$suffix"
$minioName = "steward-alpha-minio-$suffix"
$backendName = "steward-alpha-backend-$suffix"
$databaseName = 'steward_closed_alpha_rehearsal'
$bucketName = 'steward-closed-alpha-rehearsal'
$postgreSqlPort = 15432
$minioPort = 19000
$backendPort = 18080
$connectionString = "Host=127.0.0.1;Port=$postgreSqlPort;Database=$databaseName;Username=postgres;Password=postgres;Pooling=false"
$objectStorageServiceUrl = [Uri]"http://127.0.0.1:$minioPort/"
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
    $postgresVersionNumber = [int](Invoke-PostgreSqlScalar `
        -ContainerName $postgresName `
        -DatabaseName $databaseName `
        -Port $postgreSqlPort `
        -Sql "SELECT current_setting('server_version_num');")
    Require ($postgresVersionNumber -ge 170000 -and $postgresVersionNumber -lt 180000) "Disposable database is PostgreSQL version number $postgresVersionNumber, expected major version 17."
    $postgresImageId = Invoke-Docker -Arguments @(
        'container',
        'inspect',
        $postgresName,
        '--format', '{{.Image}}'
    ) -Capture
    Require ($postgresImageId -match '^sha256:[0-9a-f]{64}$') 'Disposable PostgreSQL image ID is malformed.'

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
    $minioImageId = Invoke-Docker -Arguments @(
        'container',
        'inspect',
        $minioName,
        '--format', '{{.Image}}'
    ) -Capture
    Require ($minioImageId -match '^sha256:[0-9a-f]{64}$') 'Disposable MinIO image ID is malformed.'

    Invoke-Docker -Arguments @(
        'run',
        '--rm',
        '--network', 'host',
        '--entrypoint', '/bin/sh',
        'quay.io/minio/mc:latest',
        '-c',
        "mc alias set local $($objectStorageServiceUrl.AbsoluteUri) minioadmin minioadmin >/dev/null && mc mb --ignore-existing local/$bucketName >/dev/null"
    )

    $initialBaseUri = Start-Backend `
        -ContainerName $backendName `
        -ImageId $imageId `
        -ConnectionString $connectionString `
        -ObjectStorageServiceUrl $objectStorageServiceUrl `
        -BucketName $bucketName `
        -CredentialHash $credentialHash `
        -Port $backendPort

    $schemaReady = Invoke-PostgreSqlScalar `
        -ContainerName $postgresName `
        -DatabaseName $databaseName `
        -Port $postgreSqlPort `
        -Sql "SELECT CASE WHEN to_regclass('public.steward_shared_worlds') IS NOT NULL AND to_regclass('public.steward_auth_sessions') IS NOT NULL THEN 1 ELSE 0 END;"
    Require ($schemaReady -ceq '1') 'Backend startup did not initialize the required PostgreSQL schema.'
    $stewardTableCount = [int](Invoke-PostgreSqlScalar `
        -ContainerName $postgresName `
        -DatabaseName $databaseName `
        -Port $postgreSqlPort `
        -Sql "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname = 'public' AND tablename LIKE 'steward_%';")
    Require ($stewardTableCount -gt 0) 'Backend startup initialized no Steward PostgreSQL tables.'

    $initialAuth = Invoke-JsonRequest `
        -Method Post `
        -Uri ([Uri]::new($initialBaseUri, 'api/v1/auth/friends/session')) `
        -Body ([ordered]@{
            credential = $credential
            installationId = 'closed-alpha-before-backup'
        })
    Require ($initialAuth.StatusCode -eq 200) "Initial Friends Build authentication returned HTTP $($initialAuth.StatusCode)."
    Require ([string]$initialAuth.Json.code -ceq 'Authenticated') 'Initial Friends Build authentication returned the wrong code.'
    $initialAccessToken = [string]$initialAuth.Json.data.tokens.accessToken
    Require (-not [string]::IsNullOrWhiteSpace($initialAccessToken)) 'Initial authentication returned no access token.'

    $initialRoster = Invoke-JsonRequest `
        -Method Get `
        -Uri ([Uri]::new($initialBaseUri, 'api/v1/auth/friends/identities')) `
        -Body $null `
        -Headers @{ Authorization = "Bearer $initialAccessToken" }
    Require ($initialRoster.StatusCode -eq 200) 'The initial authenticated roster request failed.'
    Require ([string]$initialRoster.Json.code -ceq 'FriendsBuildIdentitiesFound') 'The initial authenticated roster request returned the wrong code.'

    $sessionsBeforeBackup = [int](Invoke-PostgreSqlScalar `
        -ContainerName $postgresName `
        -DatabaseName $databaseName `
        -Port $postgreSqlPort `
        -Sql 'SELECT count(*) FROM steward_auth_sessions;')
    Require ($sessionsBeforeBackup -eq 1) "Expected one session before backup, found $sessionsBeforeBackup."

    Invoke-Docker -Arguments @(
        'exec',
        $postgresName,
        'pg_dump',
        '--username', 'postgres',
        '--dbname', $databaseName,
        '--port', $postgreSqlPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--format', 'custom',
        '--file', '/tmp/pre-rollback.dump'
    )
    $backupSha256 = Invoke-Docker -Arguments @(
        'exec',
        $postgresName,
        'sha256sum',
        '/tmp/pre-rollback.dump'
    ) -Capture
    $backupHashMatch = [Text.RegularExpressions.Regex]::Match($backupSha256, '^(?<hash>[0-9a-f]{64})\s')
    Require $backupHashMatch.Success 'The PostgreSQL rollback backup SHA-256 could not be read.'
    $backupHash = $backupHashMatch.Groups['hash'].Value.ToUpperInvariant()

    $postBackupAuth = Invoke-JsonRequest `
        -Method Post `
        -Uri ([Uri]::new($initialBaseUri, 'api/v1/auth/friends/session')) `
        -Body ([ordered]@{
            credential = $credential
            installationId = 'closed-alpha-after-backup'
        })
    Require ($postBackupAuth.StatusCode -eq 200) 'The post-backup authentication mutation failed.'
    $postBackupAccessToken = [string]$postBackupAuth.Json.data.tokens.accessToken
    Require (-not [string]::IsNullOrWhiteSpace($postBackupAccessToken)) 'The post-backup mutation returned no access token.'
    $sessionsAfterMutation = [int](Invoke-PostgreSqlScalar `
        -ContainerName $postgresName `
        -DatabaseName $databaseName `
        -Port $postgreSqlPort `
        -Sql 'SELECT count(*) FROM steward_auth_sessions;')
    Require ($sessionsAfterMutation -eq 2) "Expected two sessions after mutation, found $sessionsAfterMutation."

    Remove-ContainerIfPresent $backendName
    Invoke-Docker -Arguments @(
        'exec',
        $postgresName,
        'dropdb',
        '--username', 'postgres',
        '--port', $postgreSqlPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--if-exists',
        '--force',
        $databaseName
    )
    Invoke-Docker -Arguments @(
        'exec',
        $postgresName,
        'createdb',
        '--username', 'postgres',
        '--port', $postgreSqlPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        $databaseName
    )
    Invoke-Docker -Arguments @(
        'exec',
        $postgresName,
        'pg_restore',
        '--username', 'postgres',
        '--dbname', $databaseName,
        '--port', $postgreSqlPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--exit-on-error',
        '/tmp/pre-rollback.dump'
    )

    $rollbackBaseUri = Start-Backend `
        -ContainerName $backendName `
        -ImageId $imageId `
        -ConnectionString $connectionString `
        -ObjectStorageServiceUrl $objectStorageServiceUrl `
        -BucketName $bucketName `
        -CredentialHash $credentialHash `
        -Port $backendPort

    $sessionsAfterRollback = [int](Invoke-PostgreSqlScalar `
        -ContainerName $postgresName `
        -DatabaseName $databaseName `
        -Port $postgreSqlPort `
        -Sql 'SELECT count(*) FROM steward_auth_sessions;')
    Require ($sessionsAfterRollback -eq 1) "Rollback restored $sessionsAfterRollback sessions instead of one."

    $restoredRoster = Invoke-JsonRequest `
        -Method Get `
        -Uri ([Uri]::new($rollbackBaseUri, 'api/v1/auth/friends/identities')) `
        -Body $null `
        -Headers @{ Authorization = "Bearer $initialAccessToken" }
    Require ($restoredRoster.StatusCode -eq 200) 'The pre-backup session did not survive database rollback.'
    Require ([string]$restoredRoster.Json.code -ceq 'FriendsBuildIdentitiesFound') 'The restored pre-backup session returned the wrong code.'

    $rolledBackRoster = Invoke-JsonRequest `
        -Method Get `
        -Uri ([Uri]::new($rollbackBaseUri, 'api/v1/auth/friends/identities')) `
        -Body $null `
        -Headers @{ Authorization = "Bearer $postBackupAccessToken" }
    Require ($rolledBackRoster.StatusCode -eq 401) "The post-backup session remained valid after rollback (HTTP $($rolledBackRoster.StatusCode))."

    $newAuthAfterRollback = Invoke-JsonRequest `
        -Method Post `
        -Uri ([Uri]::new($rollbackBaseUri, 'api/v1/auth/friends/session')) `
        -Body ([ordered]@{
            credential = $credential
            installationId = 'closed-alpha-after-rollback'
        })
    Require ($newAuthAfterRollback.StatusCode -eq 200) 'Authentication did not recover after rollback.'
    Require ([string]$newAuthAfterRollback.Json.code -ceq 'Authenticated') 'Post-rollback authentication returned the wrong code.'

    $runningImageId = Invoke-Docker -Arguments @(
        'container',
        'inspect',
        $backendName,
        '--format', '{{.Image}}'
    ) -Capture
    Require ([string]::Equals($runningImageId, $imageId, [StringComparison]::Ordinal)) 'Rollback did not reuse the exact release image ID.'

    $evidence = [ordered]@{
        documentType = 'steward.closed-alpha-deployment-rehearsal'
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
            objectStorageProtocol = 's3-compatible'
            objectStorageTransport = 'http-loopback-only'
        }
        startup = [ordered]@{
            liveness = 'passed'
            readiness = 'passed'
            runtimeUser = 'app'
            exactImageLoaded = $true
            exactImageRunById = $true
        }
        schemaInitialization = [ordered]@{
            requiredTablesPresent = $true
            stewardTableCount = $stewardTableCount
        }
        authentication = [ordered]@{
            initialAuthentication = 'passed'
            authenticatedRoster = 'passed'
            authenticationAfterRollback = 'passed'
        }
        rollback = [ordered]@{
            backupSha256 = $backupHash
            sessionsBeforeBackup = $sessionsBeforeBackup
            sessionsAfterMutation = $sessionsAfterMutation
            sessionsAfterRollback = $sessionsAfterRollback
            preBackupSessionRestored = $true
            postBackupSessionRejected = $true
            exactImageReused = $true
        }
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText(
        $evidenceFullPath,
        ($evidence | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Closed-alpha deployment rehearsal passed without rebuilding the backend image.'
    Write-Host "  Release: $releaseVersion ($releaseCommit)"
    Write-Host "  Backend image ID: $imageId"
    Write-Host "  PostgreSQL backup SHA-256: $backupHash"
    Write-Host "  Evidence: $evidenceFullPath"
}
finally {
    Remove-ContainerIfPresent $backendName
    Remove-ContainerIfPresent $minioName
    Remove-ContainerIfPresent $postgresName

    [Array]::Clear($secretBytes, 0, $secretBytes.Length)
    $credential = $null
}
