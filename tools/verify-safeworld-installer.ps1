[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installer = [IO.Path]::GetFullPath($InstallerPath)
if (-not [IO.File]::Exists($installer)) {
    throw "SafeWorld installer not found: $installer"
}

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs/SafeWorld'
$uninstaller = Join-Path $installRoot 'Uninstall SafeWorld.exe'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft/Windows/Start Menu/Programs/SafeWorld/SafeWorld.lnk'
$uninstallRegistryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\SafeWorld'

$installProcess = Start-Process -FilePath $installer -ArgumentList '/S' -Wait -PassThru
if ($installProcess.ExitCode -ne 0) {
    throw "SafeWorld installer exited with code $($installProcess.ExitCode)."
}

foreach ($required in @(
    (Join-Path $installRoot 'SafeWorld.Desktop.exe'),
    $uninstaller,
    $startMenuShortcut)) {
    if (-not [IO.File]::Exists($required)) {
        throw "Installed SafeWorld output is missing: $required"
    }
}
if ([IO.File]::Exists((Join-Path $installRoot 'SharedWorlds.Desktop.exe'))) {
    throw 'Installed product exposes the engineering executable name.'
}

using namespace Microsoft.Win32
$uninstallKey = [Registry]::CurrentUser.OpenSubKey($uninstallRegistryPath, writable: false)
if ($null -eq $uninstallKey) {
    throw 'SafeWorld is not registered for uninstall in the current user profile.'
}
try {
    if ([string]$uninstallKey.GetValue('DisplayName') -ne 'SafeWorld') {
        throw 'SafeWorld uninstall DisplayName is invalid.'
    }
    if ([string]$uninstallKey.GetValue('InstallLocation') -ne $installRoot) {
        throw 'SafeWorld uninstall InstallLocation is invalid.'
    }
}
finally {
    $uninstallKey.Dispose()
}

# SafeWorld World data is deliberately outside the application directory. A sentinel here proves
# that uninstall removes application bytes and Windows integration without deleting persisted Worlds.
$worldDataRoot = Join-Path $env:LOCALAPPDATA 'SharedWorlds'
[IO.Directory]::CreateDirectory($worldDataRoot) | Out-Null
$worldSentinel = Join-Path $worldDataRoot 'installer-preserves-world-data.txt'
[IO.File]::WriteAllText($worldSentinel, 'preserve')

$uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList '/S' -Wait -PassThru
if ($uninstallProcess.ExitCode -ne 0) {
    throw "SafeWorld uninstaller exited with code $($uninstallProcess.ExitCode)."
}

if ([IO.File]::Exists((Join-Path $installRoot 'SafeWorld.Desktop.exe'))) {
    throw 'SafeWorld executable remains after uninstall.'
}
if ([IO.File]::Exists($startMenuShortcut)) {
    throw 'SafeWorld Start Menu shortcut remains after uninstall.'
}
$remainingUninstallKey = [Registry]::CurrentUser.OpenSubKey($uninstallRegistryPath, writable: false)
if ($null -ne $remainingUninstallKey) {
    $remainingUninstallKey.Dispose()
    throw 'SafeWorld uninstall registration remains after uninstall.'
}
if (-not [IO.File]::Exists($worldSentinel)) {
    throw 'SafeWorld uninstall removed external World data.'
}

Write-Host '[OK] SafeWorld installer lifecycle verified.'
Write-Host "  Install root: $installRoot"
Write-Host '  Installed executable, Start Menu shortcut, and uninstall registration verified.'
Write-Host '  Uninstall removed application integration and preserved external World data.'
