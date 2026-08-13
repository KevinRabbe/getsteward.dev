[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$FilePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($path in $FilePath) {
    $fullPath = [IO.Path]::GetFullPath($path)
    if (-not [IO.File]::Exists($fullPath)) {
        throw "SafeWorld signature target not found: $fullPath"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "SafeWorld candidate is not validly Authenticode-signed: '$fullPath' ($($signature.Status))."
    }
    if ($null -eq $signature.SignerCertificate -or
        [string]::IsNullOrWhiteSpace($signature.SignerCertificate.Subject)) {
        throw "SafeWorld candidate has no readable Authenticode signer identity: '$fullPath'."
    }

    Write-Host "[OK] SafeWorld signature verified: $([IO.Path]::GetFileName($fullPath))"
    Write-Host "  Signer: $($signature.SignerCertificate.Subject)"
}
