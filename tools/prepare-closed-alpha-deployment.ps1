[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$EnvironmentFile,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$HostEnvironmentPath = '/etc/steward/backend.env',
    [switch]$RequireDeployable
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

function Require-SafeAbsoluteLinuxPath([string]$Path, [string]$Context) {
    Require (-not [string]::IsNullOrWhiteSpace($Path)) "$Context is required."
    Require ($Path.StartsWith('/', [StringComparison]::Ordinal)) "$Context must be an absolute Linux path."
    Require ($Path.Length -le 260) "$Context is too long."
    Require ($Path -notmatch '[\x00-\x1F\x7F]') "$Context contains a control character."
    Require ($Path -notmatch '(^|/)\.\.?(/|$)') "$Context contains a traversal segment."
}

function Read-StrictEnvironmentFile([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    Require ([IO.File]::Exists($fullPath)) "Deployment environment file does not exist: $fullPath"
    $item = Get-Item -LiteralPath $fullPath
    Require ($null -eq $item.LinkType) 'Deployment environment file cannot be a symbolic link.'
    Require ($item.Length -gt 0 -and $item.Length -le 1MB) 'Deployment environment file must be 1 byte to 1 MiB.'

    $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $text = [IO.File]::ReadAllText($fullPath)
    Require ($text -notmatch '[\x00]') 'Deployment environment file contains a null byte.'
    $lineNumber = 0
    foreach ($rawLine in [Text.RegularExpressions.Regex]::Split($text, '\r?\n')) {
        $lineNumber++
        $line = $rawLine.Trim()
        if ([string]::IsNullOrEmpty($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }

        Require (-not $line.StartsWith('export ', [StringComparison]::Ordinal)) "Environment line $lineNumber cannot use shell export syntax."
        $separator = $line.IndexOf('=')
        Require ($separator -gt 0) "Environment line $lineNumber must use KEY=VALUE syntax."
        $key = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        Require ($key -match '^[A-Za-z_][A-Za-z0-9_]*$') "Environment line $lineNumber has an invalid key."
        Require (-not $value.Contains("`r") -and -not $value.Contains("`n")) "Environment key '$key' contains a line break."
        Require ($entries.TryAdd($key, $value)) "Deployment environment key '$key' is duplicated."
    }

    Require ($entries.Count -gt 0 -and $entries.Count -le 512) 'Deployment environment key count is outside the supported bound.'
    return $entries
}

function Get-RequiredEnvironmentValue(
    [Collections.Generic.Dictionary[string, string]]$Entries,
    [string]$Key) {
    $value = $null
    Require ($Entries.TryGetValue($Key, [ref]$value)) "Required deployment environment key '$Key' is missing."
    Require (-not [string]::IsNullOrWhiteSpace($value)) "Deployment environment key '$Key' is empty."
    Require ($value.Length -le 8192) "Deployment environment key '$Key' exceeds the value bound."
    Require ($value -notmatch '^\s*<.*>\s*$') "Deployment environment key '$Key' still contains a placeholder."
    return $value
}

function Get-NormalizedConnectionValues([string]$ConnectionString) {
    $builder = [Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.ConnectionString = $ConnectionString
    }
    catch {
        throw 'ConnectionStrings__Steward is not a valid connection string.'
    }

    $values = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($keyObject in $builder.Keys) {
        $key = [string]$keyObject
        $normalized = ($key -replace '[\s_-]', '').ToLowerInvariant()
        Require (-not $values.ContainsKey($normalized)) "ConnectionStrings__Steward contains an ambiguous duplicate key '$key'."
        $values.Add($normalized, [string]$builder[$key])
    }
    return $values
}

function Get-RequiredConnectionValue(
    [Collections.Generic.Dictionary[string, string]]$Values,
    [string[]]$Aliases,
    [string]$Context) {
    foreach ($alias in $Aliases) {
        $value = $null
        if ($Values.TryGetValue($alias, [ref]$value)) {
            Require (-not [string]::IsNullOrWhiteSpace($value)) "$Context is empty."
            return $value
        }
    }
    throw "$Context is missing."
}

function Test-ReservedDnsHost([string]$HostName) {
    if ([string]::IsNullOrWhiteSpace($HostName)) {
        return $true
    }
    $normalized = $HostName.TrimEnd('.').ToLowerInvariant()
    if ($normalized -eq 'localhost' -or
        $normalized.EndsWith('.localhost', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('.invalid', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('.example', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('.test', [StringComparison]::Ordinal)) {
        return $true
    }

    $ipAddress = $null
    if ([Net.IPAddress]::TryParse($normalized, [ref]$ipAddress)) {
        return $true
    }
    return -not $normalized.Contains('.', [StringComparison]::Ordinal)
}

function ConvertTo-ShellSingleQuoted([string]$Value) {
    $singleQuote = [string][char]39
    $doubleQuote = [string][char]34
    $replacement = $singleQuote + $doubleQuote + $singleQuote + $doubleQuote + $singleQuote
    return $singleQuote + $Value.Replace($singleQuote, $replacement) + $singleQuote
}

$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
Require ([IO.Directory]::Exists($bundleRoot)) "Closed-alpha bundle directory does not exist: $bundleRoot"
$verifierPath = Join-Path $bundleRoot 'verify-closed-alpha-release.ps1'
$manifestPath = Join-Path $bundleRoot 'release-manifest.json'
Require ([IO.File]::Exists($verifierPath)) 'The release bundle is missing its verifier.'
Require ([IO.File]::Exists($manifestPath)) 'The release bundle is missing release-manifest.json.'
& $verifierPath -BundleDirectory $bundleRoot

$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$version = [string]$manifest.version
$commitSha = [string]$manifest.commitSha
$apiBaseUrl = [string]$manifest.deployment.apiBaseUrl
$backendImageId = [string]$manifest.deployment.backendImageId
$backendImageTag = [string]$manifest.deployment.backendImageTag
Require ($version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') 'Release version is malformed.'
Require ($commitSha -match '^[0-9a-f]{40}$') 'Release commit SHA is malformed.'
Require ($backendImageId -match '^sha256:[0-9a-f]{64}$') 'Release backend image ID is malformed.'
Require (-not [string]::IsNullOrWhiteSpace($backendImageTag)) 'Release backend image tag is missing.'
Require ([int]$manifest.deployment.postgresMajorVersion -eq 17) 'Deployment plan requires PostgreSQL 17.'
Require ([string]$manifest.deployment.objectStorageProtocol -ceq 's3-compatible') 'Deployment plan requires S3-compatible object storage.'

$apiUri = [Uri]$apiBaseUrl
Require ([string]::Equals($apiUri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase)) 'Release API base URL must use HTTPS.'
Require ([string]::IsNullOrEmpty($apiUri.UserInfo) -and [string]::IsNullOrEmpty($apiUri.Query) -and [string]::IsNullOrEmpty($apiUri.Fragment)) 'Release API base URL contains forbidden components.'
Require ($apiUri.AbsolutePath -ceq '/') 'Deployment-ready API base URL must use the origin root path.'
Require ($apiUri.IsDefaultPort) 'Deployment-ready API base URL must use the default HTTPS port.'
$apiHost = $apiUri.DnsSafeHost
$apiHostReserved = Test-ReservedDnsHost $apiHost

$backendArtifacts = @($manifest.artifacts | Where-Object {
    ([string]$_.path).StartsWith('backend/', [StringComparison]::Ordinal) -and
    ([string]$_.path).EndsWith('.tar', [StringComparison]::OrdinalIgnoreCase)
})
Require ($backendArtifacts.Count -eq 1) 'Release bundle must contain exactly one backend TAR.'
$backendArtifact = $backendArtifacts[0]
$backendTarRelativePath = [string]$backendArtifact.path
$backendTarSha256 = [string]$backendArtifact.sha256
$backendTarByteSize = [int64]$backendArtifact.byteSize
Require ($backendTarSha256 -match '^[0-9A-F]{64}$') 'Backend TAR SHA-256 is malformed.'
Require ($backendTarByteSize -gt 0) 'Backend TAR byte size must be positive.'

$entries = Read-StrictEnvironmentFile $EnvironmentFile
$allowedExactKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($key in @(
    'PORT',
    'ConnectionStrings__Steward',
    'ObjectStorage__ServiceUrl',
    'ObjectStorage__AuthenticationRegion',
    'ObjectStorage__BucketName',
    'ObjectStorage__AccessKeyId',
    'ObjectStorage__SecretAccessKey',
    'ObjectStorage__ForcePathStyle',
    'FriendsBuild__Enabled',
    'ReverseProxy__KnownProxyIp',
    'Cleanup__IntervalMinutes',
    'Cleanup__VerifiedCandidateRetentionDays',
    'Cleanup__BatchSize'
)) {
    [void]$allowedExactKeys.Add($key)
}

$identityFields = @{}
foreach ($entry in $entries.GetEnumerator()) {
    $key = $entry.Key
    Require (-not $key.StartsWith('Steam__', [StringComparison]::Ordinal)) 'Friends Build deployment cannot contain Steam configuration.'
    Require (-not [string]::Equals($key, 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', [StringComparison]::Ordinal)) 'Broad ASP.NET forwarded-header trust is forbidden.'
    if ($allowedExactKeys.Contains($key)) {
        continue
    }

    $identityMatch = [Text.RegularExpressions.Regex]::Match(
        $key,
        '^FriendsBuild__Identities__(?<index>[0-9]{1,2})__(?<field>Id|DisplayName|CredentialSha256)$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    Require $identityMatch.Success "Unsupported deployment environment key '$key'."
    $index = [int]$identityMatch.Groups['index'].Value
    Require ($index -ge 0 -and $index -lt 64) "Friends Build identity index $index is outside the supported bound."
    if (-not $identityFields.ContainsKey($index)) {
        $identityFields[$index] = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    }
    $field = $identityMatch.Groups['field'].Value
    Require ($identityFields[$index].TryAdd($field, $entry.Value)) "Friends Build identity $index field '$field' is duplicated."
}

Require ((Get-RequiredEnvironmentValue $entries 'PORT') -ceq '8080') 'PORT must be exactly 8080 for the one-host Caddy deployment.'
Require ((Get-RequiredEnvironmentValue $entries 'FriendsBuild__Enabled') -ceq 'true') 'FriendsBuild__Enabled must be exactly true.'
Require ((Get-RequiredEnvironmentValue $entries 'ReverseProxy__KnownProxyIp') -ceq '127.0.0.1') 'ReverseProxy__KnownProxyIp must be exactly 127.0.0.1.'

$connectionString = Get-RequiredEnvironmentValue $entries 'ConnectionStrings__Steward'
$connectionValues = Get-NormalizedConnectionValue $connectionString
$postgresHost = Get-RequiredConnectionValue $connectionValues @('host', 'server', 'datasource') 'PostgreSQHÜİ	Â‰ÜİÜ™\Ñ]X˜\ÙHHÙ]T™\]Z\™YÛÛ›™Xİ[Û•˜[YH	ÛÛ›™Xİ[Û•˜[Y\È
	Ù]X˜\ÙIË	Ú[š]X[Ø][ÙÉÊH	ÔÜİÜ™TÔS]X˜\ÙIÂ‰ÜİÜ™\Õ\Ù\›˜[YHHÙ]T™\]Z\™YÛÛ›™Xİ[Û•˜[YH	ÛÛ›™Xİ[Û•˜[Y\È
	İ\Ù\›˜[YIË	İ\Ù\šY	Ë	İ\Ù\‰ÊH	ÔÜİÜ™TÔS\Ù\›˜[YIÂ‰ÜİÜ™\Ô\ÜİÛÜ™HÙ]T™\]Z\™YÛÛ›™Xİ[Û•˜[YH	ÛÛ›™Xİ[Û•˜[Y\È
	Ü\ÜİÛÜ™	Ë	ÜÙ	ÊH	ÔÜİÜ™TÔR\ÜİÛÜ™	Â‰ÜİÜ™\ÔÜÛ[ÙHHÙ]T™\]Z\™YÛÛ›™Xİ[Û•˜[YH	ÛÛ›™Xİ[Û•˜[Y\È
	ÜÜÛ[ÙIÊH	ÔÜİÜ™TÔSÔÓ[ÙIÂ”™\]Z\™H
Üİš[™×N‘\]X[Ê	ÜİÜ™\ÔÜÛ[ÙK”™\XÙJ	È	Ë	ÉÊK	Õ™\šYQ[	ËÔİš[™ĞÛÛ\\š\ÛÛ—N“Ü™[˜[YÛ›Ü™PØ\ÙJJH	ÔÜİÜ™TÔSÔÓ[ÙH]\İ™H™\šYQ[‰Â”™\]Z\™H
	ÜİÜ™\ÒÜİ“[™İ[HMHX[™	ÜİÜ™\Ñ]X˜\ÙK“[™İ[HLX[™	ÜİÜ™\Õ\Ù\›˜[YK“[™İ[HL
H	ÔÜİÜ™TÔSÛÛ›™Xİ[ÛˆY[]H^ÙYYÈHİ\ÜY›İ[™‰Â”™\]Z\™H
	ÜİÜ™\Ô\ÜİÛÜ™“[™İYÙHMŠH	ÔÜİÜ™TÔS\ÜİÛÜ™]\İÛÛZ[ˆ]X\İMˆÚ\˜Xİ\œË‰Â‰\İÙ\™\Ù\YšXØ]HH	[šYˆ
	ÛÛ›™Xİ[Û•˜[Y\Ë•QÙ]˜[YJ	İ\İÙ\™\˜Ù\YšXØ]IËÜ™Y—I\İÙ\™\Ù\YšXØ]JJHÂˆ™\]Z\™H
Üİš[™×N‘\]X[Ê	\İÙ\™\Ù\YšXØ]K	Ù˜[ÙIËÔİš[™ĞÛÛ\\š\ÛÛ—N“Ü™[˜[YÛ›Ü™PØ\ÙJJH	Õ\İÙ\™\ˆÙ\YšXØ]H]\İ™H˜[ÙHÚ[ˆÛÛ™šYİ\™Y‰ÂŸB‚‰Øš™XİİÜ˜YÙU\šU^HÙ]T™\]Z\™Y[š\›Û›Y[˜[YH	[šY\È	ÓØš™XİİÜ˜YÙW×ÔÙ\šXÙU\›	Â‰Øš™XİİÜ˜YÙU\šHH	[”™\]Z\™H
Õ\šWN•PÜ™X]J	Øš™XİİÜ˜YÙU\šU^Õ\šRÚ[™NXœÛÛ]KÜ™Y—IØš™XİİÜ˜YÙU\šJJH	ÓØš™XİİÜ˜YÙW×ÔÙ\šXÙU\›\ÈX[›Ü›YY‰Â”™\]Z\™H
Üİš[™×N‘\]X[Ê	Øš™XİİÜ˜YÙU\šK”ØÚ[YK	ÚÉËÔİš[™ĞÛÛ\\š\ÛÛ—N“Ü™[˜[YÛ›Ü™PØ\ÙJJH	ÓØš™XİİÜ˜YÙW×ÔÙ\šXÙU\›]\İ\ÙHË‰Â”™\]Z\™H
Üİš[™×N’\Ó[Ü‘[\J	Øš™XİİÜ˜YÙU\šK•\Ù\’[™›ÊHX[™Üİš[™×N’\Ó[Ü‘[\J	Øš™XİİÜ˜YÙU\šK”]Y\JHX[™Üİš[™×N’\Ó[Ü‘[\J	Øš™XİİÜ˜YÙU\šK‘œ˜YÛY[
JH	ÓØš™XİİÜ˜YÙW×ÔÙ\šXÙU\›ÛÛZ[œÈ›Ü˜šY[ˆÛÛ\Û™[Ë‰Â‰Øš™XİİÜ˜YÙT™YÚ[ÛˆHÙ]T™\]Z\™Y[š\›Û›Y[˜[YH	[šY\È	ÓØš™XİİÜ˜YÙW×Ğ]][XØ][Û”™YÚ[Û‰Â‰Øš™XİİÜ˜YÙPXÚÙ]HÙ]T™\]Z\™Y[š\›Û›Y[˜[YH	[šY\È	ÓØš™XİİÜ˜YÙW×ĞXÚÙ]˜[YIÂ‰Øš™XİİÜ˜YÙPXØÙ\ÜÒÙ^HHÙ]T™\]Z\™Y[š\›Û›Y[˜[YH	[šY\È	ÓØš™XİİÜ˜YÙW×ĞXØÙ\ÜÒÙ^RY	Â‰Øš™XİİÜ˜YÙTÙXÜ™]Ù^HHÙ]T™\]Z\™Y[š\›Û›Y[˜[YH	[šY\È	ÓØš™XİİÜ˜YÙW×ÔÙXÜ™]XØÙ\ÜÒÙ^IÂ‰›Ü˜ÙT]İ[U^HÙ]T™\]Z\™Y[š\›Û›Y[˜[YH	[šY\È	ÓØš™XİİÜ˜YÙW×Ñ›Ü˜ÙT]İ[IÂ”™\]Z\™H
	›Ü˜ÙT]İ[U^Z[ˆ
	İYIË	Ù˜[ÙIÊJH	ÓØš™XİİÜ˜YÙW×Ñ›Ü˜ÙT]İ[H]\İ™H^XİHYHÜˆ˜[ÙK‰Â”™\]Z\™H
	Øš™XİİÜ˜YÙT™YÚ[Û‹“[™İ[HX[™	Øš™XİİÜ˜YÙPXÚÙ]“[™İ[HMJH	ÓØš™Xİ\İÜ˜YÙH™YÚ[ÛˆÜˆXÚÙ]^ÙYYÈHİ\ÜY›İ[™‰Â”™\]Z\™H
	Øš™XİİÜ˜YÙPXØÙ\ÜÒÙ^K“[™İYÙHLŠH	ÓØš™Xİ\İÜ˜YÙHXØÙ\ÜÈÙ^H\ÈÛÈÚÜ›ÜˆH™X[\Ş[Y[‰Â”™\]Z\™H
	Øš™XİİÜ˜YÙTÙXÜ™]Ù^K“[™İYÙH
H	ÓØš™Xİ\İÜ˜YÙHÙXÜ™]Ù^H\ÈÛÈÚÜ›ÜˆH™X[\Ş[Y[‰Â‚”™\]Z\™H
	Y[]QšY[ËÛİ[YÙHHX[™	Y[]QšY[ËÛİ[[H
H	Ğ]X\İÛ™H[™][ÜİœšY[™ÈZ[Y[]Y\È\™H™\]Z\™Y‰Â‰[™XÙ\ÈH
	Y[]QšY[Ë’Ù^\ÈÛÜSØš™Xİ
B™›Üˆ
	^XİY[™^HÈ	^XİY[™^[	[™XÙ\ËÛİ[È	^XİY[™^
ÊÊHÂˆ™\]Z\™H
	[™XÙ\ÖÉ^XİY[™^HY\H	^XİY[™^
H	ÑœšY[™ÈZ[Y[]H[™XÙ\È]\İ™HÛÛYİ[İ\Èœ›ÛH™\›Ë‰ÂŸB‰ÙY[’Y[]RYÈHĞÛÛXİ[ÛœË‘Ù[™\šXË’\ÚÙ]Üİš[™×WN›™]ÊÔİš[™ĞÛÛ\\™\—N“Ü™[˜[
B‰ÙY[’Y[]R\Ú\ÈHĞÛÛXİ[ÛœË‘Ù[™\šXË’\ÚÙ]Üİš[™×WN›™]ÊÔİš[™ĞÛÛ\\™\—N“Ü™[˜[YÛ›Ü™PØ\ÙJB™›Ü™XXÚ
	[™^[ˆ	[™XÙ\ÊHÂˆ	šY[ÈH	Y[]QšY[ÖÉ[™^Bˆ™\]Z\™H
	šY[ËÛİ[Y\HÊH‘œšY[™ÈZ[Y[]H	[™^]\İÛÛZ[ˆ^XİHY\Ü^S˜[YK[™Ü™Y[X[ÚLM‹ˆ‚ˆ›Ü™XXÚ
	šY[˜[YH[ˆ
	ÒY	Ë	Ñ\Ü^S˜[YIË	ĞÜ™Y[X[ÚLM‰ÊJHÂˆ™\]Z\™H
	šY[ËÛÛZ[œÒÙ^J	šY[˜[YJJH‘œšY[™ÈZ[Y[]H	[™^\ÈZ\ÜÚ[™È	šY[˜[YKˆ‚ˆBˆ	Y[]RYH	šY[ÖÉÒY	×Bˆ	\Ü^S˜[YHH	šY[ÖÉÑ\Ü^S˜[YI×Bˆ	Ü™Y[X[\ÚH	šY[ÖÉĞÜ™Y[X[ÚLM‰×Bˆ™\]Z\™H
[›İÜİš[™×N’\Ó[Ü•Ú]TÜXÙJ	Y[]RY
HX[™	Y[]RY“[™İ[HLX[™	Y[]RY[›İX]Ú	Ö×WQ—Ñ—IÊH‘œšY[™ÈZ[Y[]H	[™^\È[ˆ[˜[YYˆ‚ˆ™\]Z\™H
[›İÜİš[™×N’\Ó[Ü•Ú]TÜXÙJ	\Ü^S˜[YJHX[™	\Ü^S˜[YK“[™İ[HLX[™	\Ü^S˜[YH[›İX]Ú	Ö×WQ—Ñ—IÊH‘œšY[™ÈZ[Y[]H	[™^\È[ˆ[˜[Y\Ü^S˜[YKˆ‚ˆ™\]Z\™H
	Ü™Y[X[\Ú[X]Ú	×–ÌNPKQ˜KY—^ÍI	ÊH‘œšY[™ÈZ[Y[]H	[™^\ÈHX[›Ü›YYÜ™Y[X[ÒKLM‹ˆ‚ˆ™\]Z\™H
	ÙY[’Y[]RYËY
	Y[]RY
JH‘œšY[™ÈZ[Y[]HY	ÉY[]RY	È\È\XØ]Yˆ‚ˆ™\]Z\™H
	ÙY[’Y[]R\Ú\ËY
	Ü™Y[X[\Ú
JH‘œšY[™ÈZ[Y[]H	[™^™]\Ù\È[›İ\ˆÜ™Y[X[YÙ\İˆ‚ŸB‚™›Ü™XXÚ
	›İ[™YÙ^H[ˆ
ˆÈ˜[YHH	ĞÛX[\×Ò[\˜[Z[]\ÉÎÈZ[š[][HHNÈX^[][HHMKˆÈ˜[YHH	ĞÛX[\×Õ™\šYšYYØ[™Y]T™][[Û‘^\ÉÎÈZ[š[][HHNÈX^[][HHÍHKˆÈ˜[YHH	ĞÛX[\×Ğ˜]ÚÚ^™IÎÈZ[š[][HHNÈX^[][HHLBŠJHÂˆ	˜[YHH	[ˆYˆ
	[šY\Ë•QÙ]˜[YJ	›İ[™YÙ^K“˜[YKÜ™Y—I˜[YJJHÂˆ	\œÙYHˆ™\]Z\™H
Ú[N•T\œÙJ	˜[YKÑÛØ˜[^˜][Û‹“[X™\”İ[\×N“›Û™KÑÛØ˜[^˜][Û‹İ[\™R[™›×N’[˜\šX[İ[\™KÜ™Y—I\œÙY
JH‘\Ş[Y[[š\›Û›Y[Ù^H	É
	›İ[™YÙ^K“˜[YJIÈ]\İ™H[ˆ[YÙ\‹ˆ‚ˆ™\]Z\™H
	\œÙYYÙH	›İ[™YÙ^K“Z[š[][HX[™	\œÙY[H	›İ[™YÙ^K“X^[][JH‘\Ş[Y[[š\›Û›Y[Ù^H	É
	›İ[™YÙ^K“˜[YJIÈ\Èİ]ÚYH]Èİ\ÜY›İ[™ˆ‚ˆBŸB‚”™\]Z\™KTØY™PXœÛÛ]S[^]	Üİ[š\›Û›Y[]	ÒÜİ[š\›Û›Y[]	Â‰İ]]›ÛİHÒSË”]N‘Ù][]
	İ]]\™XİÜJBšYˆ
ÒSË‘\™XİÜWN‘^\İÊ	İ]]›Ûİ
JHÂˆ™[[İ™KR][HS]\˜[]	İ]]›ÛİT™Xİ\œÙHQ›Ü˜ÙBŸB–ÒSË‘\™XİÜWNÜ™X]Q\™XİÜJ	İ]]›Ûİ
Hİ]S[‚‰ØYQš[S˜[YHH	ĞØYYš[IÂ‰\ŞTØÜš\˜[YHH	Ù\ŞKY^XİXØ[™Y]KœÚ	Â‰™\šYTØÜš\˜[YHH	İ™\šYK\X›XËZËœÚ	Â‰ØYU^H‚‰\RÜİÂˆ™]™\œÙWÜ›ŞHLËŒŒŒNŸBˆÒSË‘š[WN•Üš]P[^

›Ú[‹T]	İ]]›Ûİ	ØYQš[S˜[YJK	ØYU^Õ^•U[˜ÛÙ[™×N›™]Ê	˜[ÙJJB‚‰][İY[XYÙRYHÛÛ™\ËTÚ[Ú[™ÛT][İY	˜XÚÙ[™[XYÙRY‰][İY[XYÙUYÈHÛÛ™\ËTÚ[Ú[™ÛT][İY	˜XÚÙ[™[XYÙUYÂ‰][İY\”™[]]™HHÛÛ™\ËTÚ[Ú[™ÛT][İY	˜XÚÙ[™\”™[]]™T]‰][İY\’\ÚHÛÛ™\ËTÚ[Ú[™ÛT][İY	˜XÚÙ[™\”ÚLM‹•ÓİÙ\’[˜\šX[

B‰][İY[š\›Û›Y[]HÛÛ™\ËTÚ[Ú[™ÛT][İY	Üİ[š\›Û›Y[]‰\ŞU^H‚ˆÈKİ\Ü‹Øš[‹Ù[ˆ˜\ÚœÙ]Y][È\Y˜Z[‚˜[™WÙ\H˜	ÌNİ\ØYÙNˆ\ŞKY^XİXØ[™Y]KœÚ[™KY\™XİÜOŸH‚™[—Ùš[OIÜ][İY[š\›Û›Y[]Bš[XYÙWÚYIÜ][İY[XYÙRYBš[XYÙWİYÏIÜ][İY[XYÙUYßB\—Ü™[]]™OIÜ][İY\”™[]]™_B\—ÜÚLMIÜ][İY\’\ÚB\—Ü]H˜	Ø[™WÙ\ŸKØ	İ\—Ü™[]]™_H‚‚–ÖÈYˆ˜	İ\—Ü]HˆWHÈXÚÈ˜˜XÚÙ[™Tˆ\ÈZ\ÜÚ[™Îˆ	İ\—Ü]Hˆ‰ŒÈ^]NÈB–ÖÈ\ˆ˜	Ù[—Ùš[_HˆWHÈXÚÈ˜˜XÚÙ[™[š\›Û›Y[\È›İ™XYX›Nˆ	Ù[—Ùš[_Hˆ‰ŒÈ^]NÈB˜XİX[Ú\ÚH˜	
ÚLMœİ[H˜	İ\—Ü]Hˆ]ÚÈ	ŞÜš[ÛİÙ\Š	J_IÊH‚–ÖÈ˜	ØXİX[Ú\ÚHˆOH˜	İ\—ÜÚLMŸHˆWHÈXÚÈ	Ø˜XÚÙ[™TˆÒKLMˆZ\ÛX]Ú	È‰ŒÈ^]NÈB‚™ØÚÙ\ˆØYKZ[œ]˜	İ\—Ü]Hˆ‹Ù]‹Û[›ØYYÚYH˜	
ØÚÙ\ˆ[XYÙH[œÜXİ˜	Ú[XYÙWİYßHˆKY›Ü›X]	ŞŞË’Y_IÊH‚–ÖÈ˜	ÛØYYÚYHˆOH˜	Ú[XYÙWÚYHˆWHÈXÚÈ	ÛØYY˜XÚÙ[™[XYÙHQZ\ÛX]Ú	È‰ŒÈ^]NÈB›ØYYİ\Ù\H˜	
ØÚÙ\ˆ[XYÙH[œÜXİ˜	Ú[XYÙWÚYHˆKY›Ü›X]	ŞŞËÛÛ™šYË•\Ù\Ÿ_IÊH‚–ÖÈ˜	ÛØYYİ\Ù\ŸHˆOH	Ø\	ÈWHÈXÚÈ	Ø˜XÚÙ[™[XYÙHÙ\È›İ\ÙH[[YH\Ù\ˆ\	È‰ŒÈ^]NÈB‚™ØÚÙ\ˆ›HKY›Ü˜ÙHİ]Ø\™X˜XÚÙ[™‹Ù]‹Û[‰ŒHYB™ØÚÙ\ˆ[ˆKY]XÚˆK[˜[YHİ]Ø\™X˜XÚÙ[™ˆK\™\İ\[›\ÜË\İÜYˆK[™]ÛÜšÈÜİˆKY[‹Yš[H˜	Ù[—Ùš[_Hˆˆ˜	Ú[XYÙWÚYHˆ‹Ù]‹Û[‚™›Üˆ][\[ˆ	
Ù\HHL
NÈÂˆYˆİ\›KY˜Z[K\Ú[[K\ÚİËY\œ›ÜˆK[X^][YHˆ‹ËÌLËŒŒŒNÚX[Ü™XYH‹Ù]‹Û[È[‚ˆ[›š[™×ÚYH˜	
ØÚÙ\ˆÛÛZ[™\ˆ[œÜXİİ]Ø\™X˜XÚÙ[™KY›Ü›X]	ŞŞË’[XYÙ__IÊH‚ˆÖÈ˜	Ü[›š[™×ÚYHˆOH˜	Ú[XYÙWÚYHˆWHÈXÚÈ	Ü[›š[™È˜XÚÙ[™[XYÙHQZ\ÛX]Ú	È‰ŒÈ^]NÈBˆXÚÈ	Ù^Xİ˜XÚÙ[™Ø[™Y]H\È™XYHÛˆLËŒŒŒN	Âˆ^]ˆšBˆÛY\B™Û™B‚™ØÚÙ\ˆÙÜÈİ]Ø\™X˜XÚÙ[™‰ŒˆYB™XÚÈ	Ø˜XÚÙ[™Y›İ™XÛÛYH™XYIÈ‰Œ‚™^]Bˆ–ÒSË‘š[WN•Üš]P[^

›Ú[‹T]	İ]]›Ûİ	\ŞTØÜš\˜[YJK	\ŞU^Õ^•U[˜ÛÙ[™×N›™]Ê	˜[ÙJJB‚‰][İY\P˜\ÙU\›HÛÛ™\ËTÚ[Ú[™ÛT][İY	\P˜\ÙU\›‰™\šYU^H‚ˆÈKİ\Ü‹Øš[‹Ù[ˆ˜\ÚœÙ]Y][È\Y˜Z[‚˜\WØ˜\ÙWİ\›IÜ][İY\P˜\ÙU\›B˜İ\›KY˜Z[K\Ú[[K\ÚİËY\œ›ÜˆK\›İÈ	ÏZÉÈK]İŒKŒˆK[X^][YHMH˜	Ø\WØ˜\ÙWİ\›ZX[Û]™Hˆ‹Ù]‹Û[˜İ\›KY˜Z[K\Ú[[K\ÚİËY\œ›ÜˆK\›İÈ	ÏZÉÈK]İŒKŒˆK[X^][YHMH˜	Ø\WØ˜\ÙWİ\›ZX[Ü™XYHˆ‹Ù]‹Û[™XÚÈœX›XÈÈX[\ÜÙY›Üˆ	Ø\WØ˜\ÙWİ\›H‚ˆ–ÒSË‘š[WN•Üš]P[^

›Ú[‹T]	İ]]›Ûİ	™\šYTØÜš\˜[YJK	™\šYU^Õ^•U[˜ÛÙ[™×N›™]Ê	˜[ÙJJB‚‰\ŞXX›HH[›İ	\RÜİ™\Ù\™Y‰\ŞXXš[]T™X\ÛÛˆHYˆ
	\ŞXX›JHÂˆ	ÕH™[X\ÙHTHÜİ\ÈH›Û‹\™\Ù\™Y”È˜[YH[™Hİ\YYÙXÜ™][š\›Û›Y[Ø]\ÙšY\ÈHİšXİ\Ş[Y[ÛÛ˜Xİ‰ÂŸB™[ÙHÂˆ	ÕH™[X\ÙHTHÜİ\È™\Ù\™YØØ[[ˆT]\˜[Üˆ›İH[H]X[YšYY”È˜[YKˆÙ[™\˜]HH™]ÈØ[™Y]HÚ]H™X[X›XÈÈTHT“‰ÂŸB‚‰Ù[™\˜]Yš[\ÈH

B™›Ü™XXÚ
	˜[YH[ˆ
	ØYQš[S˜[YK	\ŞTØÜš\˜[YK	™\šYTØÜš\˜[YJJHÂˆ	]H›Ú[‹T]	İ]]›Ûİ	˜[YBˆ	š[HHÙ]R][HS]\˜[]	]ˆ	Ù[™\˜]Yš[\È
ÏHÛÜ™\™YPÂˆ]H	˜[YBˆ]TÚ^™HH	š[K“[™İˆÚLMˆH
Ù]Qš[R\ÚS]\˜[]	]P[ÛÜš]HÒLMŠK’\Ú•Õ\\’[˜\šX[

BˆBŸB‚‰[ˆHÛÜ™\™YPÂˆØİ[Y[\HH	Üİ]Ø\™˜ÛÜÙYX[KY\Ş[Y[\[‰ÂˆØÚ[XU™\œÚ[ÛˆHBˆ™[X\ÙHHÛÜ™\™YPÂˆ™\œÚ[ÛˆH	™\œÚ[Û‚ˆÛÛ[Z]ÚHH	ÛÛ[Z]ÚBˆ\P˜\ÙU\›H	\P˜\ÙU\›ˆ˜XÚÙ[™[XYÙUYÈH	˜XÚÙ[™[XYÙUYÂˆ˜XÚÙ[™[XYÙRYH	˜XÚÙ[™[XYÙRYˆ˜XÚÙ[™\”]H	˜XÚÙ[™\”™[]]™T]ˆ˜XÚÙ[™\]TÚ^™HH	˜XÚÙ[™\]TÚ^™Bˆ˜XÚÙ[™\”ÚLMˆH	˜XÚÙ[™\”ÚLM‚ˆBˆÜİHÛÜ™\™YPÂˆ\RÜİH	\RÜİˆ˜XÚÙ[™ÜHˆÛ›İÛ”›ŞR\H	ÌLËŒŒŒIÂˆ[š\›Û›Y[š[T]H	Üİ[š\›Û›Y[]ˆØYQš[HH	ØYQš[S˜[YBˆBˆÛÛ™šYİ\˜][ÛˆHÛÜ™\™YPÂˆÜİÜ™\ÓXZ›Ü•™\œÚ[ÛˆHMÂˆÜİÜ™\ÕÓ[ÙHH	Õ™\šYQ[	ÂˆØš™XİİÜ˜YÙTÙ\šXÙTØÚ[YHH	ÚÉÂˆØš™XİİÜ˜YÙQ›Ü˜ÙT]İ[HHØ›ÛÛN”\œÙJ	›Ü˜ÙT]İ[U^
BˆœšY[™ĞZ[Y[]PÛİ[H	Y[]QšY[ËÛİ[ˆİX[PÛÛ™šYİ\˜][Û”™\Ù[H	˜[ÙBˆœ›ØY›ÜØ\™YXY\œÑ[˜X›YH	˜[ÙBˆÙXÜ™]˜[Y\ĞÛÜYYH	˜[ÙBˆBˆ\ŞXXš[]HHÛÜ™\™YPÂˆ\ŞXX›HH	\ŞXX›Bˆ™X\ÛÛˆH	\ŞXXš[]T™X\ÛÛ‚ˆBˆÙ[™\˜]Yš[\ÈH	Ù[™\˜]Yš[\ÂˆÙ[™\˜]Y]]ÈHÑ]U[YSÙ™œÙ]N•]Ó›İË•Ôİš[™Ê	ÓÉÊBŸB‰[”]H›Ú[‹T]	İ]]›Ûİ	Ù\Ş[Y[\[‹šœÛÛ‰Â–ÒSË‘š[WN•Üš]P[^
	[”]
	[ˆÛÛ™\ËRœÛÛˆQ\
KÕ^•U[˜ÛÙ[™×N›™]Ê	˜[ÙJJB‚•Üš]KRÜİ•Üš]KRÜİ	ÖÓÒ×HÛÜÙYX[H\Ş[Y[[ˆÙ[™\˜]YÚ]İ]ÛÜZ[™ÈÙXÜ™]˜[Y\Ë‰Â•Üš]KRÜİˆ™[X\ÙNˆ	™\œÚ[Ûˆ
	ÛÛ[Z]ÚJH‚•Üš]KRÜİˆTNˆ	\P˜\ÙU\›‚•Üš]KRÜİˆ˜XÚÙ[™[XYÙHQˆ	˜XÚÙ[™[XYÙRY‚•Üš]KRÜİˆœšY[™ÈZ[Y[]Y\Îˆ	
	Y[]QšY[ËÛİ[
H‚•Üš]KRÜİˆİ]]ˆ	İ]]›Ûİ‚šYˆ
	\ŞXX›JHÂˆÜš]KRÜİ	È\Ş[Y[™XY[™\ÜÎˆ‘PQH“ÔˆÔTUÔˆVPÕUSÓ‰ÂŸB™[ÙHÂˆÜš]KRÜİ	È\Ş[Y[™XY[™\ÜÎˆ“ĞÒÑQ–H‘TÑT•‘QTHÔÕ	ÂŸB‚šYˆ
	™\]Z\™Q\ŞXX›K’\Ô™\Ù[X[™[›İ	\ŞXX›JHÂˆ›İÈ‘\Ş[Y[[ˆ\ÈİXİ\˜[H˜[Y]›İ\ŞXX›Nˆ	\ŞXXš[]T™X\ÛÛˆ‚ŸB