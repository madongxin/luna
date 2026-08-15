#Requires -Version 5.1
param(
    [string]$UnityPath = $env:UNITY_PATH
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $Root "Assets"))) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $UnityPath) {
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe"
}
if (-not (Test-Path $UnityPath)) { throw "Unity not found. Pass -UnityPath or set UNITY_PATH" }
New-Item -ItemType Directory -Force -Path (Join-Path $Root "Logs"), (Join-Path $Root "TestResults") | Out-Null
$log = Join-Path $Root "Logs\playmode.log"
$xml = Join-Path $Root "TestResults\playmode.xml"
& $UnityPath -batchmode -nographics -projectPath $Root -runTests -testPlatform PlayMode -testFilter GameMesh -testResults $xml -logFile $log
$code = $LASTEXITCODE
Write-Host "PlayMode exit=$code log=$log"
exit $code
