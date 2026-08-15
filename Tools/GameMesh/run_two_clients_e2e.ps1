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

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $Root "Logs\e2e-$stamp"
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
        "-gamemeshAutoScenario", "two-client",
        "-gamemeshRole", $role,
        "-gamemeshCoordDir", $coord,
        "-gamemeshResultDir", $resultDir,
        "-dataPath", $dataPath
    )
    return Start-Process -FilePath $ClientPath -ArgumentList $args -PassThru
}

try {
    $a = Start-Client "a" "e2e-a-$stamp" "Alice" (Join-Path $aDir "data") $aDir
    $b = Start-Client "b" "e2e-b-$stamp" "Bob" (Join-Path $bDir "data") $bDir
    $procs = @($a, $b)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($a.HasExited -and $b.HasExited) { break }
        Start-Sleep -Seconds 1
    }

    $aResult = Join-Path $aDir "result.json"
    $bResult = Join-Path $bDir "result.json"
    if (-not (Test-Path $aResult) -or -not (Test-Path $bResult)) {
        throw "missing result.json A=$(Test-Path $aResult) B=$(Test-Path $bResult)"
    }
    $aj = Get-Content -Raw $aResult | ConvertFrom-Json
    $bj = Get-Content -Raw $bResult | ConvertFrom-Json
    if ($aj.result -ne "PASS" -or $bj.result -ne "PASS") {
        throw "client result not PASS A=$($aj.result) B=$($bj.result)"
    }
    if ([uint64]$aj.map_instance_id -eq 0 -or [uint64]$aj.map_instance_id -ne [uint64]$bj.map_instance_id) {
        throw "map_instance mismatch A=$($aj.map_instance_id) B=$($bj.map_instance_id)"
    }
    $aEvents = Get-Content (Join-Path $aDir "events.jsonl") -ErrorAction SilentlyContinue
    $bEvents = Get-Content (Join-Path $bDir "events.jsonl") -ErrorAction SilentlyContinue
    if (-not ($aEvents | Where-Object { $_ -match '"aoi_peer_seen"' })) { throw "A did not see B in AOI" }
    if (-not ($bEvents | Where-Object { $_ -match '"aoi_peer_seen"' })) { throw "B did not see A in AOI" }
    if (-not ($aEvents | Where-Object { $_ -match '"mail_sent"' })) { throw "A did not send mail" }
    if (-not ($bEvents | Where-Object { $_ -match '"mail_received"' })) { throw "B did not receive mail" }
    if (-not $a.HasExited -or -not $b.HasExited) {
        throw "clients did not exit by themselves"
    }
    if ($a.ExitCode -ne 0 -or $b.ExitCode -ne 0) {
        throw "nonzero client exit A=$($a.ExitCode) B=$($b.ExitCode)"
    }

    $meta = @{
        client_commit = (git -C $Root rev-parse HEAD)
        schema_sha256 = (Get-Content -Raw (Join-Path $Root "Assets\GameMesh\Protocol\protocol_manifest.json") | ConvertFrom-Json).schema_sha256
        server_commit = (Get-Content -Raw (Join-Path $Root "Assets\GameMesh\Protocol\protocol_manifest.json") | ConvertFrom-Json).source_commit
        a_result = $aResult
        b_result = $bResult
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
