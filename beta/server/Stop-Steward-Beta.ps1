[CmdletBinding()]
param(
    [switch]$RemoveContainers,
    [switch]$RemoveData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Required command 'docker' was not found."
    exit 1
}

Push-Location $PSScriptRoot
try {
    if ($RemoveData.IsPresent) {
        & docker compose --env-file .env down --volumes
    }
    elseif ($RemoveContainers.IsPresent) {
        & docker compose --env-file .env down
    }
    else {
        & docker compose --env-file .env stop
    }
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
