[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DisplayName,
    [ValidateRange(0, 63)]
    [int]$Index = 0,
    [string]$ExternalId,
    [string]$ConfigurationOutputPath,
    [string]$CredentialOutputPath,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

function Test-ContainsControlCharacter([string]$Value) {
    foreach ($character in $Value.ToCharArray()) {
        if ([char]::IsControl($character)) {
            return $true
        }
    }

    return $false
}

$displayNameValue = $DisplayName.Trim()
if ([string]::IsNullOrWhiteSpace($displayNameValue) -or
    $displayNameValue.Length -gt 128 -or
    (Test-ContainsControlCharacter $displayNameValue)) {
    Fail 'DisplayName must be non-empty, contain no control characters, and be at most 128 characters.'
}

if ([string]::IsNullOrWhiteSpace($ExternalId)) {
    $ExternalId = 'friend-' + [Guid]::NewGuid().ToString('N')
}
else {
    $ExternalId = $ExternalId.Trim()
}

if ([string]::IsNullOrWhiteSpace($ExternalId) -or
    $ExternalId.Length -gt 512 -or
    (Test-ContainsControlCharacter $ExternalId)) {
    Fail 'ExternalId must be non-empty, contain no control characters, and be at most 512 characters.'
}

$secretBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($secretBytes)
try {
    $encodedSecret = [Convert]::ToBase64String($secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $credential = 'st_friend_' + $encodedSecret

    $credentialBytes = [Text.Encoding]::UTF8.GetBytes($credential)
    try {
        $digestBytes = [Security.Cryptography.SHA256]::HashData($credentialBytes)
        try {
            $digest = [Convert]::ToHexString($digestBytes)
        }
        finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($digestBytes)
        }
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($credentialBytes)
    }
}
finally {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($secretBytes)
}

$configuration = [ordered]@{
    FriendsBuild__Enabled = 'true'
    ("FriendsBuild__Identities__{0}__Id" -f $Index) = $ExternalId
    ("FriendsBuild__Identities__{0}__DisplayName" -f $Index) = $displayNameValue
    ("FriendsBuild__Identities__{0}__CredentialSha256" -f $Index) = $digest
}

if (-not [string]::IsNullOrWhiteSpace($ConfigurationOutputPath)) {
    $configurationPath = [IO.Path]::GetFullPath($ConfigurationOutputPath)
    if ([IO.File]::Exists($configurationPath)) {
        Fail "Configuration output already exists: $configurationPath"
    }

    $configurationDirectory = [IO.Path]::GetDirectoryName($configurationPath)
    if (-not [string]::IsNullOrWhiteSpace($configurationDirectory)) {
        [IO.Directory]::CreateDirectory($configurationDirectory) | Out-Null
    }

    $configurationJson = $configuration | ConvertTo-Json
    [IO.File]::WriteAllText(
        $configurationPath,
        $configurationJson,
        [Text.UTF8Encoding]::new($false))
}

if (-not [string]::IsNullOrWhiteSpace($CredentialOutputPath)) {
    $credentialPath = [IO.Path]::GetFullPath($CredentialOutputPath)
    if ([IO.File]::Exists($credentialPath)) {
        Fail "Credential output already exists: $credentialPath"
    }

    $credentialDirectory = [IO.Path]::GetDirectoryName($credentialPath)
    if (-not [string]::IsNullOrWhiteSpace($credentialDirectory)) {
        [IO.Directory]::CreateDirectory($credentialDirectory) | Out-Null
    }

    [IO.File]::WriteAllText(
        $credentialPath,
        $credential,
        [Text.UTF8Encoding]::new($false))
}

if (-not $Quiet.IsPresent) {
    Write-Host
    Write-Host "Friend: $displayNameValue"
    Write-Host "Stable ID: $ExternalId"
    Write-Host
    Write-Host 'PRIVATE BOOTSTRAP CREDENTIAL — send this only to that friend:'
    Write-Host $credential
    Write-Host
    Write-Host 'BACKEND CONFIGURATION — stores only the credential digest:'
    foreach ($entry in $configuration.GetEnumerator()) {
        Write-Host "$($entry.Key)=$($entry.Value)"
    }

    if (-not [string]::IsNullOrWhiteSpace($ConfigurationOutputPath)) {
        Write-Host
        Write-Host "Configuration JSON: $([IO.Path]::GetFullPath($ConfigurationOutputPath))"
    }
    if (-not [string]::IsNullOrWhiteSpace($CredentialOutputPath)) {
        Write-Host "Credential file: $([IO.Path]::GetFullPath($CredentialOutputPath))"
    }
}
