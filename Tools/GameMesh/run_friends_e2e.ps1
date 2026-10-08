#Requires -Version 5.1
# Two-client friend flow. Exit 2 is BLOCKED (no live gateway or no built client), not a pass.
param(
    [string]$HostName = "124.222.244.169",
    [int]$Port = 8083,
    [string]$ClientPath = "",
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $ClientPath) {
    $ClientPath = Join-Path $Root "Builds\GameMeshClient\GameMeshClient.exe"
}

$procs = @()
function Stop-Tracked {
    foreach ($p in $procs) {
        if ($p -and -not $p.HasExited) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

if (-not $env:GAMEMESH_E2E_GATEWAY) {
    Write-Host "BLOCKED: GAMEMESH_E2E_GATEWAY is not set."
    Write-Host "Friend dual-client E2E needs a live Gateway. Set GAMEMESH_E2E_GATEWAY=1, build the integration client, then rerun."
    Write-Host "Exit code 2 means BLOCKED, not a test failure."
    exit 2
}
if (-not (Test-Path $ClientPath)) {
    Write-Host "BLOCKED: missing $ClientPath"
    exit 2
}

$hashFile = Join-Path $Root "maps\1001.grid.json.sha256"
if (-not (Test-Path $hashFile)) {
    Write-Host "BLOCKED: missing $hashFile"
    exit 2
}
$mapHash = (Get-Content -Raw $hashFile).Trim()
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $Root "Logs\e2e-friends-$stamp"
$coord = Join-Path $work "coord"
$aDir = Join-Path $work "a"
$bDir = Join-Path $work "b"
New-Item -ItemType Directory -Force -Path $coord, $aDir, $bDir | Out-Null

function Start-Client([string]$role, [string]$device, [string]$name, [string]$dataPath, [string]$resultDir) {
    New-Item -ItemType Directory -Force -Path $dataPath, $resultDir | Out-Null
    $args = @(
        "-gamemeshHost", $HostName,
        "-gamemeshPort", "$Port",
        "-gamemeshDevice", $device,
        "-gamemeshName", $name,
        "-gamemeshPassword", "e2e-local",
        "-gamemeshMapHash", $mapHash,
        "-gamemeshMapVersion", "1",
        "-gamemeshAutoScenario", "friends",
        "-gamemeshRole", $role,
        "-gamemeshCoordDir", $coord,
        "-gamemeshResultDir", $resultDir,
        "-dataPath", $dataPath,
        "-logFile", (Join-Path $resultDir "player.log")
    )
    return Start-Process -FilePath $ClientPath -ArgumentList $args -PassThru
}

function Assert-Event($events, [string]$name) {
    $hit = @($events | Where-Object { $_.event -eq $name })
    if ($hit.Count -lt 1) { throw "missing structured event $name" }
}

try {
    $a = Start-Client "a" "e2e-fa-$stamp" "Alice" (Join-Path $aDir "data") $aDir
    $b = Start-Client "b" "e2e-fb-$stamp" "Bob" (Join-Path $bDir "data") $bDir
    $procs = @($a, $b)
    $aResult = Join-Path $aDir "result.json"
    $bResult = Join-Path $bDir "result.json"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($a.HasExited -and $b.HasExited) { break }
        if ((Test-Path $aResult) -and (Test-Path $bResult)) { break }
        Start-Sleep -Seconds 1
    }
    Stop-Tracked
    if (-not (Test-Path $aResult) -or -not (Test-Path $bResult)) {
        throw "missing result.json"
    }
    $aj = Get-Content -Raw $aResult | ConvertFrom-Json
    $bj = Get-Content -Raw $bResult | ConvertFrom-Json
    if ($aj.result -ne "PASS" -or $bj.result -ne "PASS") {
        throw "client result not PASS A=$($aj.result) B=$($bj.result) errA=$($aj.error) errB=$($bj.error)"
    }
    function Read-Events([string]$dir) {
        $path = Join-Path $dir "events.jsonl"
        if (-not (Test-Path $path)) { return @() }
        return @(Get-Content $path | ForEach-Object {
            $line = $_.Trim()
            if ($line) { try { $line | ConvertFrom-Json } catch { $null } }
        } | Where-Object { $_ -ne $null })
    }
    $ae = Read-Events $aDir
    $be = Read-Events $bDir
    Assert-Event $ae "friend_applied"
    Assert-Event $ae "friend_added_seen"
    Assert-Event $ae "friend_removed_seen"
    Assert-Event $ae "friend_blocked"
    Assert-Event $be "friend_request_seen"
    Assert-Event $be "friend_accepted"
    Assert-Event $be "friend_deleted"
    Assert-Event $be "friend_reapply_hidden"
    $blob = (Get-Content -Raw (Join-Path $bDir "events.jsonl"))
    if ($blob -match "拉黑了你") { throw "block leak in B events" }
    Write-Host "PASS friends e2e"
}
finally {
    Stop-Tracked
}
