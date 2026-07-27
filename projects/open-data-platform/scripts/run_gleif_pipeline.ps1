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
$logPath = Join-Path $logDirectory "run-gleif-$timestamp.log"

$commandArguments = @(".\odp.py", "run-gleif", "--data-root", $DataRoot)
if ($ReplicaRoot) {
    $commandArguments += @("--replica-root", $ReplicaRoot)
}

$exitCode = 0
try {
    "Started $(Get-Date -Format o)" | Tee-Object -FilePath $logPath
    "Working directory: $projectRoot" | Tee-Object -FilePath $logPath -Append
    "Command: $PythonExe $($commandArguments -join ' ')" | Tee-Object -FilePath $logPath -Append
    & $PythonExe @commandArguments 2>&1 | Tee-Object -FilePath $logPath -Append
    $exitCode = $LASTEXITCODE
    "Finished $(Get-Date -Format o) with exit code $exitCode" | Tee-Object -FilePath $logPath -Append
}
catch {
    "Failed $(Get-Date -Format o): $($_.Exception.Message)" | Tee-Object -FilePath $logPath -Append
    $exitCode = 1
}

Write-Output "Log: $logPath"
exit $exitCode
