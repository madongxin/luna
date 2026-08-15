#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$Source
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) {
    $Root = Split-Path -Parent $PSScriptRoot
}
Set-Location $Root

function Find-Proto([string]$base) {
    $candidates = @(
        (Join-Path $base "game.proto"),
        (Join-Path $base "proto\game.proto"),
        (Join-Path $base "Schema\game.proto"),
        (Join-Path $base "Assets\GameMesh\Protocol\Schema\game.proto")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return (Resolve-Path $c).Path }
    }
    throw "game.proto not found under $base. Pass a server repo root or an export directory."
}

$srcProto = Find-Proto $Source
$dstDir = Join-Path $Root "Assets\GameMesh\Protocol\Schema"
New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
$dstProto = Join-Path $dstDir "game.proto"
Copy-Item -Force $srcProto $dstProto

$sha = (Get-FileHash -Algorithm SHA256 $dstProto).Hash.ToLower()
$manifestSrc = @(
    (Join-Path $Source "protocol_manifest.json"),
    (Join-Path $Source "Assets\GameMesh\Protocol\protocol_manifest.json")
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$frame = "uint32_be_length_prefixed"
$maxBytes = 4194304
if ($manifestSrc) {
    $m = Get-Content -Raw $manifestSrc | ConvertFrom-Json
    if ($m.schema_sha256 -and $m.schema_sha256.ToLower() -ne $sha) {
        throw "protocol_manifest schema_sha256 $($m.schema_sha256) != computed $sha"
    }
    if ($m.frame_format -and $m.frame_format -ne $frame) {
        throw "unsupported frame_format: $($m.frame_format)"
    }
    if ($m.max_frame_bytes -and [int]$m.max_frame_bytes -ne $maxBytes) {
        throw "unsupported max_frame_bytes: $($m.max_frame_bytes)"
    }
}

$required = @(
    "RegisterReq", "LoginReq", "LogoutReq", "ReconnectReq", "PushAckReq",
    "PlayerAttributes", "Vec3", "EntitySnapshot",
    "EnterMapReq", "LeaveMapReq", "MoveReq", "AoiDelta",
    "PlayerMailSendReq", "MailboxSummaryReq", "MailListReq", "MailGetReq",
    "MailboxChangedNotify", "ServerPushEnvelope"
)
$protoText = [IO.File]::ReadAllText($dstProto)
$present = @()
$missing = @()
foreach ($t in $required) {
    if ($protoText -match "message\s+$t\b") { $present += $t } else { $missing += $t }
}

$manifest = [ordered]@{
    schema_file            = "Schema/game.proto"
    schema_sha256          = $sha
    generated_csharp       = "Generated/Game.cs"
    descriptor             = "game.desc"
    frame_format           = $frame
    max_frame_bytes        = $maxBytes
    csharp_namespace       = "GameMesh.Protocol"
    protoc_version         = "25.3"
    google_protobuf        = "3.25.3"
    source                 = $srcProto
    required_types_present = $present
    required_types_missing = $missing
}
$manifestPath = Join-Path $Root "Assets\GameMesh\Protocol\protocol_manifest.json"
($manifest | ConvertTo-Json -Depth 6) + "`n" | Set-Content -Encoding utf8 $manifestPath

Write-Host "Imported $srcProto"
Write-Host "schema_sha256=$sha"
if ($missing.Count -gt 0) {
    Write-Warning ("Missing required types: " + ($missing -join ", "))
}

& (Join-Path $PSScriptRoot "generate_csharp_proto.ps1")
exit 0
