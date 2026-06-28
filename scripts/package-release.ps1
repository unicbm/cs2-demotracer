param(
    [string]$Version = "0.3.2",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$DotnetPath = "",
    [switch]$SkipConverterBuild,
    [switch]$BuildRuntime,
    [switch]$SkipCssBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot

$converterArgs = @{
    Version = $Version
    OutputRoot = $OutputRoot
}
if ($SkipConverterBuild) {
    $converterArgs.SkipBuild = $true
}
& (Join-Path $PSScriptRoot "package-converter.ps1") @converterArgs

$serverArgs = @{
    Version = $Version
    Configuration = $Configuration
    OutputRoot = $OutputRoot
    DotnetPath = $DotnetPath
}
if ($BuildRuntime) {
    $serverArgs.BuildRuntime = $true
}
if ($SkipCssBuild) {
    $serverArgs.SkipCssBuild = $true
}
& (Join-Path $PSScriptRoot "package-server.ps1") @serverArgs

$assetNames = @(
    "cs2-demotracer-v$Version-windows-x64.zip",
    "cs2-demotracer-server-v$Version-windows-x64.zip"
)

$lines = foreach ($assetName in $assetNames) {
    $assetPath = Join-Path $outputRootPath $assetName
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "release asset not found: $assetPath"
    }
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $assetName"
}

$sumsPath = Join-Path $outputRootPath "SHA256SUMS.txt"
Set-Content -LiteralPath $sumsPath -Value $lines -Encoding ASCII

Write-Host "Wrote $sumsPath"
