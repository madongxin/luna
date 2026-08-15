#Requires -Version 5.1
param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 8081,
    [string]$ClientPath = "",
    [int]$TimeoutSec = 90
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $ClientPath) {
    $ClientPath = Join-Path $Root "Builds\GameMeshClient\GameMeshClient.exe"
}
if (-not $env:GAMEMESH_E2E_GATEWAY -or -not (Test-Path $ClientPath)) {
    Write-Host "Real dual-client E2E NOT RUN. Set GAMEMESH_E2E_GATEWAY=1, build the client, and provide a live Gateway."
    if (-not (Test-Path $ClientPath)) { Write-Host "missing $ClientPath" }
    exit 2
}

function Start-Client([string]$device, [string]$name, [string]$dataPath) {
    New-Item -ItemType Directory -Force -Path $dataPath | Out-Null
    $args = @(
        "-gamemeshHost", $HostName,
        "-gamemeshPort", "$Port",
        "-gamemeshDevice", $device,
        "-gamemeshName", $name,
        "-gamemeshPassword", "e2e-not-logged",
        "-gamemeshAutoScenario", "login-enter",
        "-dataPath", $dataPath
    )
    return Start-Process -FilePath $ClientPath -ArgumentList $args -PassThru
}

$a = Start-Client "e2e-a" "Alice" (Join-Path $Root "Logs\e2e-a")
$b = Start-Client "e2e-b" "Bob" (Join-Path $Root "Logs\e2e-b")
Start-Sleep -Seconds $TimeoutSec
if (-not $a.HasExited) { Stop-Process -Id $a.Id -Force }
if (-not $b.HasExited) { Stop-Process -Id $b.Id -Force }
Write-Host "E2E processes launched against ${HostName}:${Port}."
Write-Host "This script only proves two isolated dataPath processes started."
Write-Host "Full C3.4 assertions require a live Gateway and log markers; mark NOT RUN if Gateway is down."
exit 0
