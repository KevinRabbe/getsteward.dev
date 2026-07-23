[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = "OpenDataPlatform-GLEIF",
    [ValidateSet("Daily", "Weekly")]
    [string]$Frequency = "Daily",
    [string]$StartTime = "02:00",
    [string]$PythonExe = "python",
    [string]$DataRoot = "data",
    [string]$ReplicaRoot = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$wrapper = Join-Path $projectRoot "scripts\run_gleif_pipeline.ps1"
$wrapperArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$wrapper`" -PythonExe `"$PythonExe`" -DataRoot `"$DataRoot`""
if ($ReplicaRoot) {
    $wrapperArguments += " -ReplicaRoot `"$ReplicaRoot`""
}

$action = New-ScheduledTaskAction -Execute "PowerShell.exe" -Argument $wrapperArguments -WorkingDirectory $projectRoot
$parsedTime = [datetime]::ParseExact($StartTime, "HH:mm", $null)
if ($Frequency -eq "Daily") {
    $trigger = New-ScheduledTaskTrigger -Daily -At $parsedTime
} else {
    $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At $parsedTime
}
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType InteractiveToken -RunLevel Limited

if ($PSCmdlet.ShouldProcess($TaskName, "Register Open Data Platform scheduled task")) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Description "Run the verified Open Data Platform GLEIF pipeline" -Force | Out-Null
    Write-Output "Registered scheduled task: $TaskName"
} else {
    Write-Output "WhatIf: would register scheduled task: $TaskName"
}
