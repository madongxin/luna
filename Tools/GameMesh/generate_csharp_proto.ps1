#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) {
    $Root = Split-Path -Parent $PSScriptRoot
}
Set-Location $Root

$Version = "25.3"
$Cache = Join-Path $PSScriptRoot "cache"
$ProtocZip = Join-Path $Cache "protoc-$Version-win64.zip"
$ProtocDir = Join-Path $Cache "protoc-$Version"
$Protoc = if ($env:PROTOC_PATH) { $env:PROTOC_PATH } else { Join-Path $ProtocDir "bin\protoc.exe" }

if (-not (Test-Path $Protoc)) {
    New-Item -ItemType Directory -Force -Path $Cache | Out-Null
    if (-not (Test-Path $ProtocZip)) {
        $url = "https://github.com/protocolbuffers/protobuf/releases/download/v$Version/protoc-$Version-win64.zip"
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $ProtocZip
    }
    Expand-Archive -Force $ProtocZip $ProtocDir
    $Protoc = Join-Path $ProtocDir "bin\protoc.exe"
}

$Schema = Join-Path $Root "Assets\GameMesh\Protocol\Schema\game.proto"
if (-not (Test-Path $Schema)) { throw "missing $Schema — run import_server_contract first" }

$genDir = Join-Path $Cache "gen"
New-Item -ItemType Directory -Force -Path $genDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false
$text = $utf8.GetString([IO.File]::ReadAllBytes($Schema))
if ($text -notmatch 'option csharp_namespace') {
    $text = $text.Replace("package game;", "package game;`noption csharp_namespace = `"GameMesh.Protocol`";")
}
[IO.File]::WriteAllText((Join-Path $genDir "game.proto"), $text, $utf8)

$outDir = Join-Path $Root "Assets\GameMesh\Protocol\Generated"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$desc = Join-Path $Root "Assets\GameMesh\Protocol\game.desc"
& $Protoc -I $genDir --csharp_out=$outDir --descriptor_set_out=$desc (Join-Path $genDir "game.proto")
if ($LASTEXITCODE -ne 0) { throw "protoc failed" }

$ver = & $Protoc --version
Write-Host "Generated C# with $ver -> $outDir"
