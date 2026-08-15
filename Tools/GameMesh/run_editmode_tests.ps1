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
$log = Join-Path $Root "Logs\editmode.log"
$xml = Join-Path $Root "TestResults\editmode.xml"
if (Test-Path $xml) { Remove-Item -Force $xml }
$p = Start-Process -FilePath $UnityPath -ArgumentList @(
    "-batchmode", "-nographics", "-projectPath", $Root,
    "-runTests", "-testPlatform", "EditMode", "-testFilter", "GameMesh",
    "-testResults", $xml, "-logFile", $log
) -PassThru -Wait
$code = $p.ExitCode
Write-Host "EditMode exit=$code log=$log"
if ($code -ne 0) { exit $code }
if (-not (Test-Path $xml)) {
    Write-Host "EditMode missing results XML: $xml"
    exit 1
}
if (Select-String -Path $xml -Pattern 'result="Failed"' -Quiet) {
    Write-Host "EditMode has failed tests. xml=$xml log=$log"
    exit 1
}
exit 0
