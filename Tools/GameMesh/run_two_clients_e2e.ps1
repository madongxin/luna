#Requires -Version 5.1
param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 8081,
    [string]$ClientPath = "",
    [int]$TimeoutSec = 90,
    [string]$Scenario = "presence-move-logout"
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
    Write-Host "Real dual-client E2E NOT RUN. Set GAMEMESH_E2E_GATEWAY=1 and provide a live Gateway."
    exit 2
}
if (-not (Test-Path $ClientPath)) {
    Write-Host "Real dual-client E2E NOT RUN. missing $ClientPath"
    exit 2
}

$hashFile = Join-Path $Root "maps\1001.grid.json.sha256"
if (-not (Test-Path $hashFile)) {
    Write-Host "Real dual-client E2E NOT RUN. missing $hashFile"
    exit 2
}
$mapHash = (Get-Content -Raw $hashFile).Trim()

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $Root "Logs\e2e-$stamp"
$coord = Join-Path $work "coord"
$aDir = Join-Path $work "a"
$bDir = Join-Path $work "b"
New-Item -ItemType Directory -Force -Path $coord, $aDir, $bDir | Out-Null
$commit = (git -C $Root rev-parse HEAD)

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
        "-gamemeshAutoScenario", $Scenario,
        "-gamemeshRole", $role,
        "-gamemeshCoordDir", $coord,
        "-gamemeshResultDir", $resultDir,
        "-dataPath", $dataPath,
        "-logFile", (Join-Path $resultDir "player.log")
    )
    $p = Start-Process -FilePath $ClientPath -ArgumentList $args -PassThru
    return $p
}

function Get-JsonlEvents([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    return Get-Content $path | ForEach-Object {
        $line = $_.Trim()
        if ($line) {
            try { $line | ConvertFrom-Json } catch { $null }
        }
    } | Where-Object { $_ -ne $null }
}

function Assert-Event($events, [string]$name) {
    $hit = @($events | Where-Object { $_.event -eq $name })
    if ($hit.Count -lt 1) { throw "missing structured event $name" }
    return $hit[0]
}

try {
    $env:GAMEMESH_CLIENT_COMMIT = $commit
    $a = Start-Client "a" "e2e-a-$stamp" "Alice" (Join-Path $aDir "data") $aDir
    $b = Start-Client "b" "e2e-b-$stamp" "Bob" (Join-Path $bDir "data") $bDir
    $procs = @($a, $b)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($a.HasExited -and $b.HasExited) { break }
        Start-Sleep -Seconds 1
    }

    if (-not $a.HasExited -or -not $b.HasExited) {
        throw "clients did not exit by themselves"
    }
    if ($a.ExitCode -ne 0 -or $b.ExitCode -ne 0) {
        throw "nonzero client exit A=$($a.ExitCode) B=$($b.ExitCode)"
    }

    $aResult = Join-Path $aDir "result.json"
    $bResult = Join-Path $bDir "result.json"
    if (-not (Test-Path $aResult) -or -not (Test-Path $bResult)) {
        throw "missing result.json A=$(Test-Path $aResult) B=$(Test-Path $bResult)"
    }
    $aj = Get-Content -Raw $aResult | ConvertFrom-Json
    $bj = Get-Content -Raw $bResult | ConvertFrom-Json
    if ($aj.result -ne "PASS" -or $bj.result -ne "PASS") {
        throw "client result not PASS A=$($aj.result) B=$($bj.result) errA=$($aj.error) errB=$($bj.error)"
    }
    if (-not $aj.hello_ok -or -not $bj.hello_ok -or -not $aj.login_ok -or -not $bj.login_ok) {
        throw "hello/login not ok"
    }
    $aMap = [uint64]$aj.player_id_before_logout
    $bMap = [uint64]$bj.player_id_before_logout
    if ($aMap -eq 0 -or $bMap -eq 0) { throw "player_id_before_logout missing" }
    $aInst = [uint64]$aj.map_instance_id_before_logout
    $bInst = [uint64]$bj.map_instance_id_before_logout
    if ($aInst -eq 0 -or $aInst -ne $bInst) {
        throw "map_instance mismatch A=$aInst B=$bInst"
    }
    if (-not $aj.peer_seen -or -not $bj.peer_seen) { throw "mutual visibility failed" }
    if (-not $aj.peer_move_seen -or -not $bj.peer_move_seen) { throw "bidirectional move failed" }
    if (-not $aj.logout_rsp_ok -or -not $bj.logout_rsp_ok) { throw "logout_rsp_ok missing" }
    if (-not $bj.peer_leave_seen) { throw "B did not see AOI Leave after A logout" }

    $aEvents = Get-JsonlEvents (Join-Path $aDir "events.jsonl")
    $bEvents = Get-JsonlEvents (Join-Path $bDir "events.jsonl")
    $aSeen = Assert-Event $aEvents "aoi_peer_seen"
    $bSeen = Assert-Event $bEvents "aoi_peer_seen"
    if ([uint64]$aSeen.peer_id -eq 0 -or [uint64]$bSeen.peer_id -eq 0) { throw "aoi_peer_seen missing peer_id" }
    $aMoved = Assert-Event $aEvents "aoi_peer_moved"
    $bMoved = Assert-Event $bEvents "aoi_peer_moved"
    if ([uint64]$aMoved.new_state_seq -le [uint64]$aMoved.old_state_seq -and [uint64]$aMoved.old_state_seq -ne 0) {
        throw "A did not observe increasing state_seq"
    }
    if ([uint64]$bMoved.new_state_seq -le [uint64]$bMoved.old_state_seq -and [uint64]$bMoved.old_state_seq -ne 0) {
        throw "B did not observe increasing state_seq"
    }
    Assert-Event $bEvents "aoi_peer_left" | Out-Null
    $aLogout = Assert-Event $aEvents "logout"
    $bLogout = Assert-Event $bEvents "logout"
    if (-not $aLogout.ok -or -not $bLogout.ok) { throw "structured logout ok=false" }

    $secretHits = Select-String -Path $aResult, $bResult, (Join-Path $aDir "events.jsonl"), (Join-Path $bDir "events.jsonl") -Pattern "e2e-local" -ErrorAction SilentlyContinue
    if ($secretHits) { throw "secret leaked into result/events" }

    $meta = @{
        client_commit = $commit
        schema_sha256 = (Get-Content -Raw (Join-Path $Root "Assets\GameMesh\Protocol\protocol_manifest.json") | ConvertFrom-Json).schema_sha256
        server_commit = (Get-Content -Raw (Join-Path $Root "Assets\GameMesh\Protocol\protocol_manifest.json") | ConvertFrom-Json).source_commit
        a_result = $aResult
        b_result = $bResult
        scenario = $Scenario
    }
    $playerLogs = @(
        (Join-Path $aDir "data\Player.log"),
        (Join-Path $bDir "data\Player.log")
    ) | Where-Object { Test-Path $_ }
    foreach ($log in $playerLogs) {
        Copy-Item $log (Join-Path $work ([IO.Path]::GetFileName([IO.Path]::GetDirectoryName($log)) + "-Player.log")) -ErrorAction SilentlyContinue
    }
    $meta | ConvertTo-Json | Set-Content (Join-Path $work "meta.json")
    Write-Host "E2E PASS work=$work"
    exit 0
}
catch {
    Write-Host "E2E FAIL: $($_.Exception.Message)"
    exit 1
}
finally {
    Stop-Tracked
}
