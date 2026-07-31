# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$AllowUnsignedInstaller,
    [string]$DotnetPath = "",
    [string]$RuntimePackage = "server\runtime\BotController\build\package",
    [string]$BotHiderRuntimePackage = "server\runtime\BotHider\build\package",
    [switch]$SkipGuiBuild,
    [switch]$SkipCssBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRootPath = Join-Path $repoRoot $OutputRoot
$publishRootPath = Join-Path $outputRootPath "release-v$Version"
$guiName = "DemoTracer-GUI-v$Version-windows-x64.exe"
$cssName = "DemoTracer-CSS-v$Version-windows-x64.zip"

& (Join-Path $PSScriptRoot "assert-clean-worktree.ps1") -RepoRoot $repoRoot
& (Join-Path $PSScriptRoot "check-release-contract.ps1") -Version $Version

$guiArgs = @{
    Version = $Version
    OutputRoot = $OutputRoot
    CertificateThumbprint = $CertificateThumbprint
    TimestampUrl = $TimestampUrl
}
if ($AllowUnsignedInstaller) {
    $guiArgs.AllowUnsignedInstaller = $true
}
if ($SkipGuiBuild) {
    $guiArgs.SkipBuild = $true
}
& (Join-Path $PSScriptRoot "package-converter.ps1") @guiArgs

$cssArgs = @{
    Version = $Version
    Configuration = $Configuration
    OutputRoot = $OutputRoot
    DotnetPath = $DotnetPath
    RuntimePackage = $RuntimePackage
    BotHiderRuntimePackage = $BotHiderRuntimePackage
}
if ($SkipCssBuild) {
    $cssArgs.SkipCssBuild = $true
}
if ($IncludeSymbols) {
    $cssArgs.IncludeSymbols = $true
}
& (Join-Path $PSScriptRoot "package-server.ps1") @cssArgs

$assetNames = @($guiName, $cssName)
foreach ($assetName in $assetNames) {
    $assetPath = Join-Path $outputRootPath $assetName
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "release asset not found: $assetPath"
    }
}

if (Test-Path -LiteralPath $publishRootPath) {
    Remove-Item -LiteralPath $publishRootPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishRootPath | Out-Null
foreach ($assetName in $assetNames) {
    Copy-Item -LiteralPath (Join-Path $outputRootPath $assetName) -Destination $publishRootPath -Force
}

$publishedNames = @(Get-ChildItem -LiteralPath $publishRootPath -File | Select-Object -ExpandProperty Name | Sort-Object)
$expectedPublishedNames = @($assetNames | Sort-Object)
if (Compare-Object -ReferenceObject $expectedPublishedNames -DifferenceObject $publishedNames) {
    throw "Release directory contains an unexpected asset set: $publishRootPath"
}

Write-Host "Clean release assets: $publishRootPath"
