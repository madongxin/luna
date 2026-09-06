#Requires -Version 5.1
param(
    [string]$HostName = "124.222.244.169",
    [int]$Port = 8083,
    [string]$ClientPath = ""
)
$ErrorActionPreference = "Stop"
if (-not $env:GAMEMESH_E2E_GATEWAY) {
    Write-Host "Session-replaced E2E NOT RUN. Set GAMEMESH_E2E_GATEWAY=1 and provide a live Gateway."
    exit 2
}
Write-Host "Session-replaced scenario requires a second process to Login the same player_id with kick_other_device=true, then assert the first client stops auto-reconnect. Drive it with -gamemeshAutoScenario session-replaced once a live cluster is available."
exit 2
