[CmdletBinding()]
param(
    [string]$PythonExe = "python",
    [string]$DataRoot = "data",
    [string]$ReplicaRoot = "",
    [string]$LogRoot = "data/logs"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $projectRoot
$logDirectory = Join-Path $projectRoot $LogRoot
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $logDirectory "monitor-$timestamp.log"
$arguments = @(".\odp.py", "monitor", "--data-root", $DataRoot)
if ($ReplicaRoot) {
    $arguments += @("--replica-root", $ReplicaRoot)
}

$exitCode = 0
try {
    & $PythonExe @arguments 2>&1 | Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        "ALERT: Open Data Platform monitor returned exit code $exitCode" | Tee-Object -FilePath $logPath -Append
    }
} catch {
    "ALERT: monitor invocation failed: $($_.Exception.Message)" | Tee-Object -FilePath $logPath -Append
    $exitCode = 1
}
Write-Output "Log: $logPath"
exit $exitCode
