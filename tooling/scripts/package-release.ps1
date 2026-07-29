param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$ReleaseBaseUrl = "https://releases.detr.site",
    [string]$UpdaterPublicKeyPath = "tooling\release\updater-public-key.txt",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$ReleaseNotes = "",
    [string]$ReleaseNotesZh = "",
    [switch]$AllowUnsignedInstaller,
    [string]$DotnetPath = "",
    [string]$RuntimePackage = "server\runtime\BotController\build\package",
    [switch]$ReuseLatestRuntimePackage,
    [switch]$SkipConverterBuild,
    [switch]$BuildRuntime,
    [switch]$BuildBotHiderRuntime,
    [switch]$SkipCssBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRootPath = Join-Path $repoRoot $OutputRoot
$publishRootPath = Join-Path $outputRootPath "release-v$Version"
$playbackPackageName = "cs2-demotracer-playback-v$Version-windows-x64"
$installerName = "cs2-demotracer-setup-v$Version-windows-x64.exe"
$releaseBase = $ReleaseBaseUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = "Improvements and bug fixes."
}
if ([string]::IsNullOrWhiteSpace($ReleaseNotesZh)) {
    $ReleaseNotesZh = "功能改进与问题修复。"
}

& (Join-Path $PSScriptRoot "assert-clean-worktree.ps1") -RepoRoot $repoRoot
& (Join-Path $PSScriptRoot "check-release-contract.ps1") -Version $Version

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

    $candidate = @(
        Get-ChildItem -LiteralPath $outputRootPath -Directory -Filter "cs2-demotracer-playback-v*-windows-x64" -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $outputRootPath -Directory -Filter "cs2-demotracer-server-v*-windows-x64" -ErrorAction SilentlyContinue
    ) |
        Where-Object { $_.Name -ne $playbackPackageName -and (Test-RuntimePackageRoot $_.FullName) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($candidate) {
        Write-Host "Using runtime package $($candidate.FullName)"
        return $candidate.FullName
    }

    throw "BotController runtime package not found. Pass -RuntimePackage, run with -BuildRuntime after configuring native tools, or keep a previous playback bundle under $outputRootPath."
}

$converterArgs = @{
    Version = $Version
    OutputRoot = $OutputRoot
    ReleaseBaseUrl = $releaseBase
    UpdaterPublicKeyPath = $UpdaterPublicKeyPath
    CertificateThumbprint = $CertificateThumbprint
    TimestampUrl = $TimestampUrl
}
if ($AllowUnsignedInstaller) {
    $converterArgs.AllowUnsignedInstaller = $true
}
if ($SkipConverterBuild) {
    $converterArgs.SkipBuild = $true
}
& (Join-Path $PSScriptRoot "package-converter.ps1") @converterArgs

$playbackArgs = @{
    Version = $Version
    Configuration = $Configuration
    OutputRoot = $OutputRoot
    DotnetPath = $DotnetPath
    RuntimePackage = (Resolve-RuntimePackageArgument)
}
if ($BuildRuntime) {
    $playbackArgs.BuildRuntime = $true
}
if ($BuildBotHiderRuntime) {
    $playbackArgs.BuildBotHiderRuntime = $true
}
if ($SkipCssBuild) {
    $playbackArgs.SkipCssBuild = $true
}
if ($IncludeSymbols) {
    $playbackArgs.IncludeSymbols = $true
}
& (Join-Path $PSScriptRoot "package-server.ps1") @playbackArgs

$playbackZip = Join-Path $outputRootPath "$playbackPackageName.zip"
if (-not (Test-Path -LiteralPath $playbackZip)) {
    throw "playback package not found: $playbackZip"
}
Push-Location (Join-Path $repoRoot "desktop\gui")
try {
    $signerArgs = @("exec", "tauri", "signer", "sign")
    if ([string]::IsNullOrEmpty($env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD)) {
        $signerArgs += "--password="
    }
    $signerArgs += $playbackZip
    & pnpm.cmd @signerArgs
    if ($LASTEXITCODE -ne 0) { throw "Tauri signer failed for $playbackZip" }
} finally {
    Pop-Location
}
$playbackSignaturePath = "$playbackZip.sig"
if (-not (Test-Path -LiteralPath $playbackSignaturePath)) {
    throw "playback signature not found: $playbackSignaturePath"
}

$installerPath = Join-Path $outputRootPath $installerName
$installerSignaturePath = "$installerPath.sig"
if (-not (Test-Path -LiteralPath $installerPath) -or -not (Test-Path -LiteralPath $installerSignaturePath)) {
    throw "NSIS installer or updater signature is missing under $outputRootPath"
}
$publishedAt = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)
$immutableBase = "$releaseBase/releases/v$Version"
$installerSignature = (Get-Content -LiteralPath $installerSignaturePath -Raw -Encoding UTF8).Trim()
$playbackSignature = (Get-Content -LiteralPath $playbackSignaturePath -Raw -Encoding UTF8).Trim()
$playbackHash = (Get-FileHash -LiteralPath $playbackZip -Algorithm SHA256).Hash.ToLowerInvariant()
$playbackSize = (Get-Item -LiteralPath $playbackZip).Length
$playbackContract = Get-Content -LiteralPath (Join-Path $repoRoot "shared\contracts\playback-contract.v1.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$localizedReleaseNotes = [ordered]@{
    zh = $ReleaseNotesZh
    en = $ReleaseNotes
} | ConvertTo-Json -Compress

$latestManifest = [ordered]@{
    version = $Version
    notes = $localizedReleaseNotes
    pub_date = $publishedAt
    platforms = [ordered]@{
        "windows-x86_64" = [ordered]@{
            signature = $installerSignature
            url = "$immutableBase/$installerName"
        }
    }
}
$latestManifestPath = Join-Path $outputRootPath "latest.json"
$latestManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $latestManifestPath -Encoding UTF8

$playbackManifest = [ordered]@{
    schemaVersion = 1
    product = "CS2 DemoTracer Playback Bundle"
    version = $Version
    pubDate = $publishedAt
    notes = $localizedReleaseNotes
    platform = "windows-x64"
    url = "$immutableBase/$playbackPackageName.zip"
    signature = $playbackSignature
    sha256 = $playbackHash
    size = $playbackSize
    compatibility = $playbackContract
}
$playbackManifestPath = Join-Path $outputRootPath "playback.json"
$playbackManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $playbackManifestPath -Encoding UTF8

$assetNames = @(
    $installerName,
    "$installerName.sig",
    "$playbackPackageName.zip",
    "$playbackPackageName.zip.sig",
    "latest.json",
    "playback.json"
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

if (Test-Path -LiteralPath $publishRootPath) {
    Remove-Item -LiteralPath $publishRootPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishRootPath | Out-Null
foreach ($assetName in $assetNames) {
    Copy-Item -LiteralPath (Join-Path $outputRootPath $assetName) -Destination $publishRootPath -Force
}
Copy-Item -LiteralPath $sumsPath -Destination $publishRootPath -Force

$publishedNames = @(Get-ChildItem -LiteralPath $publishRootPath -File | Select-Object -ExpandProperty Name | Sort-Object)
$expectedPublishedNames = @($assetNames + "SHA256SUMS.txt" | Sort-Object)
if (Compare-Object -ReferenceObject $expectedPublishedNames -DifferenceObject $publishedNames) {
    throw "Clean release directory contains an unexpected asset set: $publishRootPath"
}

Write-Host "Clean release assets: $publishRootPath"
