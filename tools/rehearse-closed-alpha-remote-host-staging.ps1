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

function ConvertTo-ShellSingleQuoted([string]$Value) {
    $singleQuote = [string][char]39
    $doubleQuote = [string][char]34
    $escapedQuote = $singleQuote + $doubleQuote + $singleQuote + $doubleQuote + $singleQuote
    return $singleQuote + $Value.Replace($singleQuote, $escapedQuote) + $singleQuote
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText(
        $Path,
        $Content.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Invoke-RequiredNative(
    [string]$Tool,
    [string[]]$Arguments,
    [string]$Context,
    [switch]$Capture) {
    $output = @(& $Tool @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $text = ($output -join [Environment]::NewLine).Trim()
        throw "$Context failed with exit code $LASTEXITCODE. $text"
    }

    if ($Capture.IsPresent) {
        return ($output -join [Environment]::NewLine).Trim()
    }

    foreach ($line in $output) {
        Write-Host $line
    }
}

foreach ($tool in @('docker', 'ssh', 'scp', 'ssh-keygen', 'ssh-keyscan', 'tar')) {
    Require-Tool $tool
}

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
$releaseVersion = [string]$manifest.version
$releaseCommit = [string]$manifest.commitSha
$imageId = [string]$manifest.deployment.backendImageId
$imageTag = [string]$manifest.deployment.backendImageTag
Require ($releaseVersion -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($releaseCommit -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($imageId -match '^sha256:[0-9a-f]{64}$') 'Backend image ID is malformed.'
Require (-not [string]::IsNullOrWhiteSpace($imageTag)) 'Backend image tag is missing.'
Require (-not [bool]$manifest.publishAuthorization.publishAllowed) 'Remote staging rehearsal cannot use a publish-authorized candidate.'
Require ([string]$manifest.publishAuthorization.physicalBringHere -ceq 'deferred') 'Remote staging rehearsal expects deferred physical Bring Here evidence.'

$backendArtifacts = @($manifest.artifacts | Where-Object {
    ([string]$_.path).StartsWith('backend/', [StringComparison]::Ordinal) -and
    ([string]$_.path).EndsWith('.tar', [StringComparison]::OrdinalIgnoreCase)
})
Require ($backendArtifacts.Count -eq 1) 'Release bundle must contain exactly one backend TAR.'
$backendRelativePath = [string]$backendArtifacts[0].path
$backendTarSha256 = [string]$backendArtifacts[0].sha256
Require ($backendRelativePath -match '^backend/[A-Za-z0-9._-]+\.tar$') 'Backend TAR path is not staging-safe.'
Require ($backendTarSha256 -match '^[0-9A-F]{64}$') 'Backend TAR SHA-256 is malformed.'

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("steward-remote-staging-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$hostBuildRoot = Join-Path $workRoot 'host-image'
[IO.Directory]::CreateDirectory($hostBuildRoot) | Out-Null
$containerName = "steward-remote-staging-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
$hostImageTag = "steward-remote-staging-host:$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
$privateKeyPath = Join-Path $workRoot 'id_ed25519'
$publicKeyPath = "$privateKeyPath.pub"
$knownHostsPath = Join-Path $workRoot 'known_hosts'
$archivePath = Join-Path $workRoot 'candidate.tar'
$remoteScriptPath = Join-Path $workRoot 'remote-stage.sh'
$returnedEvidencePath = Join-Path $workRoot 'remote-staging-evidence.json'
$remoteArchive = '/home/steward/candidate.tar'
$remoteScript = '/home/steward/remote-stage.sh'
$remoteEvidence = '/home/steward/remote-staging-evidence.json'
$remoteReleaseDirectory = "/srv/steward/releases/$releaseCommit"
$remoteUser = 'steward'
$backendImageLoaded = $false

$dockerfile = @'
FROM mcr.microsoft.com/powershell:7.5-ubuntu-24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        docker.io \
        openssh-client \
        openssh-server \
        tar \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --create-home --shell /bin/bash steward \
    && passwd -d steward \
    && mkdir -p /run/sshd /home/steward/.ssh /srv/steward/releases \
    && chown -R steward:steward /home/steward /srv/steward
COPY entrypoint.sh /entrypoint.sh
RUN chmod 0755 /entrypoint.sh
EXPOSE 22
ENTRYPOINT ["/entrypoint.sh"]
'@
$entrypoint = @'
#!/usr/bin/env bash
set -euo pipefail

ssh-keygen -A
socket_gid="$(stat -c '%g' /var/run/docker.sock)"
if ! getent group "${socket_gid}" >/dev/null; then
  groupadd --gid "${socket_gid}" steward-docker
fi
socket_group="$(getent group "${socket_gid}" | cut -d: -f1)"
usermod --append --groups "${socket_group}" steward
install -d -m 0700 -o steward -g steward /home/steward/.ssh
install -m 0600 -o steward -g steward /bootstrap/authorized_key /home/steward/.ssh/authorized_keys
cat >/etc/ssh/sshd_config.d/steward-ci.conf <<'CONFIG'
PasswordAuthentication no
KbdInteractiveAuthentication no
ChallengeResponseAuthentication no
PermitRootLogin no
PubkeyAuthentication yes
AllowUsers steward
UsePAM no
CONFIG
exec /usr/sbin/sshd -D -e
'@
Write-Utf8NoBom -Path (Join-Path $hostBuildRoot 'Dockerfile') -Content $dockerfile
Write-Utf8NoBom -Path (Join-Path $hostBuildRoot 'entrypoint.sh') -Content $entrypoint

$remoteStageTemplate = @'
#!/usr/bin/env bash
set -euo pipefail

archive_path="${1:?archive path is required}"
archive_sha256="${2:?archive SHA-256 is required}"
release_directory="${3:?release directory is required}"
backend_relative_path="${4:?backend TAR path is required}"
backend_sha256="${5:?backend TAR SHA-256 is required}"
image_tag="${6:?image tag is required}"
image_id="${7:?image ID is required}"
release_commit="${8:?release commit is required}"
release_version="${9:?release version is required}"
evidence_path="${10:?evidence path is required}"
ssh_host_key_sha256="${11:?SSH host-key SHA-256 is required}"

actual_archive_sha256="$(sha256sum "${archive_path}" | awk '{print tolower($1)}')"
[[ "${actual_archive_sha256}" == "${archive_sha256}" ]] || {
  echo 'candidate archive SHA-256 mismatch after SSH transfer' >&2
  exit 1
}

if docker image inspect "${image_id}" >/dev/null 2>&1; then
  echo 'backend image unexpectedly existed before remote staging' >&2
  exit 1
fi

rm -rf "${release_directory}"
mkdir -p "${release_directory}"
tar -xf "${archive_path}" -C "${release_directory}"

if find "${release_directory}" -type d -name .git -print -quit | grep -q .; then
  echo 'repository metadata was transferred to the remote release directory' >&2
  exit 1
fi
if find "${release_directory}" -type f \( -name 'backend.env' -o -name '*.secret' -o -name 'id_ed25519*' \) -print -quit | grep -q .; then
  echo 'a secret environment or SSH key was transferred with the release candidate' >&2
  exit 1
fi

pwsh -NoLogo -NoProfile -File "${release_directory}/verify-closed-alpha-release.ps1" \
  -BundleDirectory "${release_directory}"

backend_tar="${release_directory}/${backend_relative_path}"
[[ -f "${backend_tar}" ]] || { echo 'backend TAR is missing after extraction' >&2; exit 1; }
actual_backend_sha256="$(sha256sum "${backend_tar}" | awk '{print toupper($1)}')"
[[ "${actual_backend_sha256}" == "${backend_sha256}" ]] || {
  echo 'backend TAR SHA-256 mismatch on remote host' >&2
  exit 1
}

docker load --input "${backend_tar}" >/dev/null
loaded_id="$(docker image inspect "${image_tag}" --format '{{.Id}}')"
[[ "${loaded_id}" == "${image_id}" ]] || { echo 'loaded backend image ID mismatch' >&2; exit 1; }
loaded_user="$(docker image inspect "${image_id}" --format '{{.Config.User}}')"
loaded_platform="$(docker image inspect "${image_id}" --format '{{.Os}}/{{.Architecture}}')"
loaded_revision="$(docker image inspect "${image_id}" --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}')"
loaded_version="$(docker image inspect "${image_id}" --format '{{ index .Config.Labels "org.opencontainers.image.version" }}')"
[[ "${loaded_user}" == 'app' ]] || { echo 'loaded image runtime user mismatch' >&2; exit 1; }
[[ "${loaded_platform}" == 'linux/amd64' ]] || { echo 'loaded image platform mismatch' >&2; exit 1; }
[[ "${loaded_revision}" == "${release_commit}" ]] || { echo 'loaded image revision label mismatch' >&2; exit 1; }
[[ "${loaded_version}" == "${release_version}" ]] || { echo 'loaded image version label mismatch' >&2; exit 1; }
[[ -z "$(docker ps --all --quiet --filter "ancestor=${image_id}")" ]] || {
  echo 'remote staging unexpectedly started a backend container' >&2
  exit 1
}

export STEWARD_RELEASE_DIRECTORY="${release_directory}"
export STEWARD_ARCHIVE_SHA256="${archive_sha256}"
export STEWARD_SSH_HOST_KEY_SHA256="${ssh_host_key_sha256}"
export STEWARD_IMAGE_ID="${image_id}"
export STEWARD_IMAGE_TAG="${image_tag}"
export STEWARD_IMAGE_USER="${loaded_user}"
export STEWARD_IMAGE_PLATFORM="${loaded_platform}"
export STEWARD_IMAGE_REVISION="${loaded_revision}"
export STEWARD_IMAGE_VERSION="${loaded_version}"
export STEWARD_EVIDENCE_PATH="${evidence_path}"

pwsh -NoLogo -NoProfile -Command '
$manifestPath = Join-Path $env:STEWARD_RELEASE_DIRECTORY "release-manifest.json"
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$evidence = [ordered]@{
    documentType = "steward.closed-alpha-remote-host-staging"
    schemaVersion = 1
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    hostIsolation = "disposable-ssh-container-with-docker-socket"
    transport = "ssh-ed25519"
    remoteUser = "steward"
    sshHostKeySha256 = $env:STEWARD_SSH_HOST_KEY_SHA256
    candidateArchiveSha256 = $env:STEWARD_ARCHIVE_SHA256.ToUpperInvariant()
    releaseVersion = [string]$manifest.version
    releaseCommitSha = [string]$manifest.commitSha
    remoteReleaseDirectory = $env:STEWARD_RELEASE_DIRECTORY
    bundleVerified = $true
    repositoryMetadataTransferred = $false
    secretEnvironmentTransferred = $false
    backendImagePreexisting = $false
    backendImageLoaded = $true
    backendImageId = $env:STEWARD_IMAGE_ID
    backendImageTag = $env:STEWARD_IMAGE_TAG
    backendRuntimeUser = $env:STEWARD_IMAGE_USER
    backendPlatform = $env:STEWARD_IMAGE_PLATFORM
    backendRevisionLabel = $env:STEWARD_IMAGE_REVISION
    backendVersionLabel = $env:STEWARD_IMAGE_VERSION
    backendContainerStarted = $false
    publishAllowed = [bool]$manifest.publishAuthorization.publishAllowed
    physicalBringHere = [string]$manifest.publishAuthorization.physicalBringHere
}
[IO.File]::WriteAllText(
    $env:STEWARD_EVIDENCE_PATH,
    ($evidence | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))
'
'@
Write-Utf8NoBom -Path $remoteScriptPath -Content $remoteStageTemplate

try {
    Invoke-RequiredNative -Tool 'ssh-keygen' -Arguments @(
        '-q',
        '-t', 'ed25519',
        '-N', '',
        '-f', $privateKeyPath
    ) -Context 'SSH client-key generation'

    Invoke-RequiredNative -Tool 'tar' -Arguments @(
        '--sort=name',
        '--mtime=UTC 1970-01-01',
        '--owner=0',
        '--group=0',
        '--numeric-owner',
        '-C', $bundleRoot,
        '-cf', $archivePath,
        '.'
    ) -Context 'Deterministic candidate archive creation'
    $archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

    Invoke-Docker -Arguments @(
        'build',
        '--tag', $hostImageTag,
        $hostBuildRoot
    )

    Invoke-Docker -Arguments @(
        'run',
        '--detach',
        '--name', $containerName,
        '--publish', '127.0.0.1::22',
        '--mount', 'type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock',
        '--mount', "type=bind,source=$publicKeyPath,target=/bootstrap/authorized_key,readonly",
        $hostImageTag
    )

    $portText = Invoke-Docker -Arguments @('port', $containerName, '22/tcp') -Capture
    $portMatch = [Text.RegularExpressions.Regex]::Match($portText, '127\.0\.0\.1:(?<port>[0-9]{1,5})')
    Require $portMatch.Success "Could not resolve the disposable SSH host port from '$portText'."
    $sshPort = [int]$portMatch.Groups['port'].Value
    Require ($sshPort -ge 1 -and $sshPort -le 65535) 'Disposable SSH host port is outside the valid range.'

    $hostKeyLine = $null
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        $scanOutput = @(& ssh-keyscan -p $sshPort -t ed25519 127.0.0.1 2>$null)
        if ($LASTEXITCODE -eq 0 -and $scanOutput.Count -gt 0) {
            $hostKeyLine = ($scanOutput -join "`n").Trim()
            break
        }
        Start-Sleep -Seconds 1
    }
    if ([string]::IsNullOrWhiteSpace($hostKeyLine)) {
        $logs = @(& docker logs $containerName 2>&1) -join [Environment]::NewLine
        throw "Disposable SSH host did not become reachable. $logs"
    }
    Write-Utf8NoBom -Path $knownHostsPath -Content ($hostKeyLine + "`n")

    $fingerprintText = Invoke-RequiredNative -Tool 'ssh-keygen' -Arguments @(
        '-E', 'sha256',
        '-lf', $knownHostsPath
    ) -Context 'SSH host-key fingerprint calculation' -Capture
    $fingerprintMatch = [Text.RegularExpressions.Regex]::Match($fingerprintText, 'SHA256:[A-Za-z0-9+/=]+')
    Require $fingerprintMatch.Success 'Disposable SSH host key fingerprint is malformed.'
    $sshHostKeySha256 = $fingerprintMatch.Value

    $sshOptions = @(
        '-i', $privateKeyPath,
        '-p', $sshPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-o', "UserKnownHostsFile=$knownHostsPath",
        '-o', 'StrictHostKeyChecking=yes',
        '-o', 'BatchMode=yes',
        '-o', 'PasswordAuthentication=no',
        '-o', 'KbdInteractiveAuthentication=no',
        '-o', 'ConnectTimeout=10'
    )
    $scpOptions = @(
        '-i', $privateKeyPath,
        '-P', $sshPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-o', "UserKnownHostsFile=$knownHostsPath",
        '-o', 'StrictHostKeyChecking=yes',
        '-o', 'BatchMode=yes',
        '-o', 'PasswordAuthentication=no',
        '-o', 'KbdInteractiveAuthentication=no',
        '-o', 'ConnectTimeout=10'
    )
    $remoteTarget = "$remoteUser@127.0.0.1"

    Invoke-RequiredNative -Tool 'scp' -Arguments ($scpOptions + @(
        $archivePath,
        "${remoteTarget}:$remoteArchive"
    )) -Context 'Candidate archive SSH transfer'
    Invoke-RequiredNative -Tool 'scp' -Arguments ($scpOptions + @(
        $remoteScriptPath,
        "${remoteTarget}:$remoteScript"
    )) -Context 'Remote staging script SSH transfer'

    $remoteCommand = 'bash ' + (ConvertTo-ShellSingleQuoted $remoteScript) + ' ' + (@(
        $remoteArchive,
        $archiveSha256,
        $remoteReleaseDirectory,
        $backendRelativePath,
        $backendTarSha256,
        $imageTag,
        $imageId,
        $releaseCommit,
        $releaseVersion,
        $remoteEvidence,
        $sshHostKeySha256
    ) | ForEach-Object { ConvertTo-ShellSingleQuoted ([string]$_) } | Join-String -Separator ' ')
    Invoke-RequiredNative -Tool 'ssh' -Arguments ($sshOptions + @(
        $remoteTarget,
        $remoteCommand
    )) -Context 'Remote candidate staging'
    $backendImageLoaded = $true

    Invoke-RequiredNative -Tool 'scp' -Arguments ($scpOptions + @(
        "${remoteTarget}:$remoteEvidence",
        $returnedEvidencePath
    )) -Context 'Remote staging evidence retrieval'

    Require ([IO.File]::Exists($returnedEvidencePath)) 'Remote staging evidence was not returned.'
    $evidenceText = [IO.File]::ReadAllText($returnedEvidencePath)
    Require ($evidenceText.Length -gt 0 -and $evidenceText.Length -le 1MB) 'Remote staging evidence is empty or exceeds 1 MiB.'
    $evidence = $evidenceText | ConvertFrom-Json
    $expectedProperties = @(
        'backendContainerStarted',
        'backendImageId',
        'backendImageLoaded',
        'backendImagePreexisting',
        'backendImageTag',
        'backendPlatform',
        'backendRevisionLabel',
        'backendRuntimeUser',
        'backendVersionLabel',
        'bundleVerified',
        'candidateArchiveSha256',
        'completedAtUtc',
        'documentType',
        'hostIsolation',
        'physicalBringHere',
        'publishAllowed',
        'releaseCommitSha',
        'releaseVersion',
        'remoteReleaseDirectory',
        'remoteUser',
        'repositoryMetadataTransferred',
        'schemaVersion',
        'secretEnvironmentTransferred',
        'sshHostKeySha256',
        'transport'
    ) | Sort-Object
    $actualProperties = @($evidence.PSObject.Properties.Name | Sort-Object)
    Require ($actualProperties.Count -eq $expectedProperties.Count -and
        @(Compare-Object $actualProperties $expectedProperties).Count -eq 0) 'Remote staging evidence contains missing or unexpected properties.'
    Require ([string]$evidence.documentType -ceq 'steward.closed-alpha-remote-host-staging') 'Remote staging evidence has the wrong document type.'
    Require ([int]$evidence.schemaVersion -eq 1) 'Remote staging evidence has an unsupported schema version.'
    Require ([string]$evidence.hostIsolation -ceq 'disposable-ssh-container-with-docker-socket') 'Remote staging evidence has an unexpected host-isolation model.'
    Require ([string]$evidence.transport -ceq 'ssh-ed25519') 'Remote staging evidence has an unexpected transport.'
    Require ([string]$evidence.remoteUser -ceq $remoteUser) 'Remote staging evidence names the wrong user.'
    Require ([string]$evidence.sshHostKeySha256 -ceq $sshHostKeySha256) 'Remote staging evidence SSH host key does not match the pinned host key.'
    Require ([string]$evidence.candidateArchiveSha256 -ceq $archiveSha256.ToUpperInvariant()) 'Remote staging evidence archive hash does not match the transferred archive.'
    Require ([string]$evidence.releaseCommitSha -ceq $releaseCommit) 'Remote staging evidence commit does not match the candidate.'
    Require ([string]$evidence.releaseVersion -ceq $releaseVersion) 'Remote staging evidence version does not match the candidate.'
    Require ([string]$evidence.remoteReleaseDirectory -ceq $remoteReleaseDirectory) 'Remote staging evidence release directory is incorrect.'
    Require ([bool]$evidence.bundleVerified) 'Remote host did not verify the release bundle.'
    Require (-not [bool]$evidence.repositoryMetadataTransferred) 'Repository metadata was transferred to the remote host.'
    Require (-not [bool]$evidence.secretEnvironmentTransferred) 'A secret environment was transferred during staging.'
    Require (-not [bool]$evidence.backendImagePreexisting) 'Backend image unexpectedly preexisted on the disposable host.'
    Require ([bool]$evidence.backendImageLoaded) 'Backend image was not loaded on the remote host.'
    Require ([string]$evidence.backendImageId -ceq $imageId) 'Remote staging evidence image ID does not match the candidate.'
    Require ([string]$evidence.backendImageTag -ceq $imageTag) 'Remote staging evidence image tag does not match the candidate.'
    Require ([string]$evidence.backendRuntimeUser -ceq 'app') 'Remote staging evidence has the wrong runtime user.'
    Require ([string]$evidence.backendPlatform -ceq 'linux/amd64') 'Remote staging evidence has the wrong platform.'
    Require ([string]$evidence.backendRevisionLabel -ceq $releaseCommit) 'Remote staging evidence has the wrong revision label.'
    Require ([string]$evidence.backendVersionLabel -ceq $releaseVersion) 'Remote staging evidence has the wrong version label.'
    Require (-not [bool]$evidence.backendContainerStarted) 'Remote staging unexpectedly started the backend.'
    Require (-not [bool]$evidence.publishAllowed) 'Remote staging changed publish authorization.'
    Require ([string]$evidence.physicalBringHere -ceq 'deferred') 'Remote staging changed physical Bring Here status.'

    [IO.File]::WriteAllText(
        $evidenceFullPath,
        $evidenceText,
        [Text.UTF8Encoding]::new($false))

    Write-Host
    Write-Host '[OK] Exact closed-alpha candidate staged and loaded through a pinned SSH host boundary.'
    Write-Host "  Release: $releaseVersion ($releaseCommit)"
    Write-Host "  SSH host key: $sshHostKeySha256"
    Write-Host "  Candidate archive SHA-256: $($archiveSha256.ToUpperInvariant())"
    Write-Host "  Backend image ID: $imageId"
    Write-Host "  Remote release directory: $remoteReleaseDirectory"
    Write-Host '  Secret environment transferred: no'
    Write-Host '  Backend container started: no'
    Write-Host '  Publish authorization changed: no'
}
finally {
    Remove-ContainerIfPresent $containerName
    if ($backendImageLoaded) {
        & docker image rm --force $imageId *> $null
    }
    & docker image rm --force $hostImageTag *> $null
    if ([IO.Directory]::Exists($workRoot)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}