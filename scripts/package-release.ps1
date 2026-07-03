param(
    [string]$Version = "0.3.6",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$DotnetPath = "",
    [string]$RuntimePackage = "runtime\BotController\build\package",
    [switch]$ReuseLatestRuntimePackage,
    [switch]$SkipConverterBuild,
    [switch]$BuildRuntime,
    [switch]$SkipCssBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$serverPackageName = "cs2-demotracer-server-v$Version-windows-x64"

function Test-RuntimePackageRoot([string]$Path) {
    return (Test-Path -LiteralPath (Join-Path $Path "addons\BotController\bin\win64\BotController.dll")) `
        -and (Test-Path -LiteralPath (Join-Path $Path "addons\BotController\gamedata.json")) `
        -and (Test-Path -LiteralPath (Join-Path $Path "addons\metamod\BotController.vdf"))
}

function Resolve-RuntimePackageArgument() {
    $configuredRoot = if ([System.IO.Path]::IsPathRooted($RuntimePackage)) {
        $RuntimePackage
    } else {
        Join-Path $repoRoot $RuntimePackage
    }

    if (Test-RuntimePackageRoot $configuredRoot) {
        return $RuntimePackage
    }

    if (-not $ReuseLatestRuntimePackage) {
        return $RuntimePackage
    }

    $candidate = Get-ChildItem -LiteralPath $outputRootPath -Directory -Filter "cs2-demotracer-server-v*-windows-x64" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne $serverPackageName -and (Test-RuntimePackageRoot $_.FullName) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($candidate) {
        Write-Host "Using runtime package $($candidate.FullName)"
        return $candidate.FullName
    }

    throw "BotController runtime package not found. Pass -RuntimePackage, run with -BuildRuntime after configuring native tools, or keep a previous server package under $outputRootPath."
}

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
    RuntimePackage = (Resolve-RuntimePackageArgument)
}
if ($BuildRuntime) {
    $serverArgs.BuildRuntime = $true
}
if ($SkipCssBuild) {
    $serverArgs.SkipCssBuild = $true
}
if ($IncludeSymbols) {
    $serverArgs.IncludeSymbols = $true
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
