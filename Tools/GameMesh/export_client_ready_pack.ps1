#Requires -Version 5.1
param(
    [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $OutDir) { $OutDir = Join-Path $Root "Builds\luna-pack" }

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir "protocol") | Out-Null
$commit = (git -C $Root rev-parse HEAD).Trim()
$short = $commit.Substring(0, 8)
$bundle = Join-Path $OutDir "luna-$short.bundle"
git -C $Root bundle create $bundle --all
if ($LASTEXITCODE -ne 0) { throw "git bundle failed" }

Copy-Item (Join-Path $Root "Assets\GameMesh\Protocol\Schema\game.proto") (Join-Path $OutDir "protocol\game.proto") -Force
Copy-Item (Join-Path $Root "Assets\GameMesh\Protocol\protocol_manifest.json") (Join-Path $OutDir "protocol\protocol_manifest.json") -Force
Copy-Item (Join-Path $Root "maps\1001.grid.json.sha256") (Join-Path $OutDir "protocol\1001.grid.json.sha256") -Force

$manifest = Get-Content -Raw (Join-Path $Root "Assets\GameMesh\Protocol\protocol_manifest.json") | ConvertFrom-Json
$exe = Join-Path $Root "Builds\GameMeshClient\GameMeshClient.exe"
$binary = if (Test-Path $exe) { $exe } else { "" }
if ($binary) {
    $clientDir = Join-Path $OutDir "GameMeshClient"
    if (Test-Path (Join-Path $Root "Builds\GameMeshClient")) {
        Copy-Item (Join-Path $Root "Builds\GameMeshClient") $clientDir -Recurse -Force
    }
}

$ready = @"
luna_commit=$commit
luna_repo=$Root
schema_sha256=$($manifest.schema_sha256)
source_commit=$($manifest.source_commit)
map_sha256=$((Get-Content -Raw (Join-Path $Root "maps\1001.grid.json.sha256")).Trim())
git_bundle=$bundle
client_binary=$binary
luna_protocol_contract=PASS_IF_LUNA_REPO_SET

# Clone without GitHub:
#   git clone "$bundle" luna
#   cd luna
#   export LUNA_REPO="`$PWD"

# Server repo gates (run inside webserver, not this repo):
#   export LUNA_REPO="$Root"
#   ./scripts/check_luna_protocol_contract.sh
#   ./scripts/client_ready_gate.sh
#   ./scripts/stable_gate.sh --full
"@
Set-Content -Path (Join-Path $OutDir "LUNA_READY.txt") -Value $ready -Encoding utf8
Write-Host $ready
Write-Host "pack=$OutDir"
if (-not $binary) {
    Write-Host "WARN: GameMeshClient.exe missing. Run Tools/GameMesh/build_integration_client.ps1"
    exit 2
}
exit 0
