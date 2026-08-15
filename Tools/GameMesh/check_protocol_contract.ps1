#Requires -Version 5.1
param(
    [string]$Source = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) {
    $Root = Split-Path -Parent $PSScriptRoot
}
Set-Location $Root

$protoDir = Join-Path $Root "Assets\GameMesh\Protocol"
$schema = Join-Path $protoDir "Schema\game.proto"
$generated = Join-Path $protoDir "Generated\Game.cs"
$manifestPath = Join-Path $protoDir "protocol_manifest.json"
$versions = Get-Content -Raw (Join-Path $PSScriptRoot "versions.json") | ConvertFrom-Json

if (-not (Test-Path $schema)) { throw "missing $schema" }
if (-not (Test-Path $generated)) { throw "missing $generated" }
if (-not (Test-Path $manifestPath)) { throw "missing $manifestPath" }

$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
$clientSha = (Get-FileHash -Algorithm SHA256 $schema).Hash.ToLower()
if ($manifest.schema_sha256.ToLower() -ne $clientSha) {
    throw "client schema hash drift manifest=$($manifest.schema_sha256) actual=$clientSha"
}
if ($manifest.frame_format -ne $versions.frame_format) { throw "frame_format drift" }
if ([int]$manifest.max_frame_bytes -ne [int]$versions.max_frame_bytes) { throw "max_frame_bytes drift" }
if ($manifest.csharp_namespace -ne $versions.csharp_namespace) { throw "csharp_namespace drift" }
if ($manifest.protoc_version -ne $versions.protoc) { throw "protoc version drift" }
if ($manifest.google_protobuf -ne $versions.google_protobuf) { throw "google_protobuf version drift" }

$cs = Get-Content -Raw $generated
if ($cs -notmatch "namespace GameMesh.Protocol") { throw "generated C# namespace drift" }
if ($cs -notmatch "source: game.proto") { throw "generated C# is not from game.proto" }

$required = @(
    "RegisterReq", "LoginReq", "LogoutReq", "ReconnectReq", "PushAckReq",
    "PlayerAttributes", "Vec3", "EntitySnapshot",
    "EnterMapReq", "LeaveMapReq", "MoveReq", "AoiDelta",
    "PlayerMailSendReq", "MailboxSummaryReq", "MailListReq", "MailGetReq",
    "MailboxChangedNotify", "ServerPushEnvelope"
)
$protoText = Get-Content -Raw $schema
$missing = @()
foreach ($t in $required) {
    if ($protoText -notmatch "message\s+$t\b") { $missing += $t }
    if ($cs -notmatch "class $t\b") { $missing += "$t(C#)" }
}
if ($cs -notmatch "EnterMap") { $missing += "oneof EnterMap" }
if ($cs -notmatch "GetSelfProfile") { $missing += "oneof GetSelfProfile" }
if ($cs -notmatch "PlayerMailSend") { $missing += "oneof PlayerMailSend" }
if ($cs -notmatch "MailboxChanged") { $missing += "oneof MailboxChanged" }
if ($missing.Count -gt 0) {
    throw ("required types missing: " + ($missing -join ", "))
}
if (@($manifest.required_types_missing).Count -gt 0) {
    throw ("manifest required_types_missing not empty: " + ($manifest.required_types_missing -join ", "))
}

if ($Source) {
    $srcCandidates = @(
        (Join-Path $Source "game.proto"),
        (Join-Path $Source "proto\game.proto"),
        (Join-Path $Source "Schema\game.proto")
    )
    $src = $srcCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $src) { throw "server game.proto not found under $Source" }
    $serverSha = (Get-FileHash -Algorithm SHA256 $src).Hash.ToLower()
    if ($serverSha -ne $clientSha) {
        throw "server/client schema hash mismatch server=$serverSha client=$clientSha"
    }
    Write-Host "server schema matches client: $serverSha"
}

Write-Host "protocol contract OK schema_sha256=$clientSha commit=$($manifest.source_commit)"
exit 0
