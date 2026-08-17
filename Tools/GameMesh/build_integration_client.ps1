#Requires -Version 5.1
param(
    [string]$UnityPath = $env:UNITY_PATH,
    [string]$OutputDir = ""
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $UnityPath) { $UnityPath = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe" }
if (-not (Test-Path $UnityPath)) { throw "Unity not found. Pass -UnityPath or set UNITY_PATH" }
if (-not $OutputDir) { $OutputDir = Join-Path $Root "Builds\GameMeshClient" }
New-Item -ItemType Directory -Force -Path $OutputDir, (Join-Path $Root "Logs") | Out-Null
$log = Join-Path $Root "Logs\build_integration_client.log"
$p = Start-Process -FilePath $UnityPath -ArgumentList @(
    "-batchmode", "-nographics", "-projectPath", $Root,
    "-logFile", $log, "-quit",
    "-executeMethod", "GameMesh.Editor.IntegrationBuild.BuildWindows"
) -PassThru -Wait
$code = $p.ExitCode
Write-Host "Build exit=$code log=$log"
exit $code
