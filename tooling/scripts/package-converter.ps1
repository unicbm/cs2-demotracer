param(
    [string]$Version = "1.0.0",
    [string]$OutputRoot = "dist",
    [string]$ReleaseBaseUrl = "https://releases.detr.site",
    [string]$UpdaterPublicKeyPath = "tooling\release\updater-public-key.txt",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$AllowUnsignedInstaller,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRootPath = Join-Path $repoRoot $OutputRoot
$desktopRoot = Join-Path $repoRoot "desktop\gui"
$bundleRoot = Join-Path $desktopRoot "src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis"
$packageName = "cs2-demotracer-setup-v$Version-windows-x64"
$installerPath = Join-Path $outputRootPath "$packageName.exe"
$signaturePath = "$installerPath.sig"
$publicKeyPath = if ([System.IO.Path]::IsPathRooted($UpdaterPublicKeyPath)) {
    $UpdaterPublicKeyPath
} else {
    Join-Path $repoRoot $UpdaterPublicKeyPath
}

& (Join-Path $PSScriptRoot "assert-clean-worktree.ps1") -RepoRoot $repoRoot
& (Join-Path $PSScriptRoot "check-release-contract.ps1") -Version $Version

function Require-Path([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

$releaseBase = $ReleaseBaseUrl.TrimEnd('/')
$releaseUri = $null
if (-not [System.Uri]::TryCreate($releaseBase, [System.UriKind]::Absolute, [ref]$releaseUri) -or
    $releaseUri.Scheme -ne "https" -or
    [string]::IsNullOrWhiteSpace($releaseUri.Host)) {
    throw "ReleaseBaseUrl must be an absolute HTTPS URL."
}

Require-Path $publicKeyPath "Tauri updater public key"
$updaterPublicKey = (Get-Content -LiteralPath $publicKeyPath -Raw -Encoding UTF8).Trim()
if ([string]::IsNullOrWhiteSpace($updaterPublicKey)) {
    throw "Tauri updater public key is empty: $publicKeyPath"
}

if ([string]::IsNullOrWhiteSpace($env:TAURI_SIGNING_PRIVATE_KEY) -and
    [string]::IsNullOrWhiteSpace($env:TAURI_SIGNING_PRIVATE_KEY_PATH)) {
    $defaultPrivateKey = Join-Path $env:USERPROFILE ".tauri\cs2-demotracer.key"
    if (Test-Path -LiteralPath $defaultPrivateKey) {
        $env:TAURI_SIGNING_PRIVATE_KEY_PATH = $defaultPrivateKey
    } else {
        throw "Tauri updater private key is unavailable. Set TAURI_SIGNING_PRIVATE_KEY_PATH or TAURI_SIGNING_PRIVATE_KEY."
    }
}

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -and -not $AllowUnsignedInstaller) {
    throw "Authenticode certificate thumbprint is required by default. Pass -CertificateThumbprint or explicitly use -AllowUnsignedInstaller."
}
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
    $CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "CertificateThumbprint must be a 40-character hexadecimal certificate thumbprint."
}

$windowsBundle = [ordered]@{}
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $windowsBundle.certificateThumbprint = $CertificateThumbprint.ToUpperInvariant()
    $windowsBundle.digestAlgorithm = "sha256"
    $windowsBundle.timestampUrl = $TimestampUrl
}

$releaseConfig = [ordered]@{
    bundle = [ordered]@{
        windows = $windowsBundle
    }
    plugins = [ordered]@{
        updater = [ordered]@{
            pubkey = $updaterPublicKey
            endpoints = @("$releaseBase/channels/stable/latest.json")
            windows = [ordered]@{
                installMode = "passive"
            }
        }
        demotracerRelease = [ordered]@{
            playbackManifestUrl = "$releaseBase/channels/stable/playback.json"
        }
    }
}

$buildConfigRoot = Join-Path $outputRootPath ".release-build"
$buildConfigPath = Join-Path $buildConfigRoot "tauri.release.v$Version.json"
New-Item -ItemType Directory -Force -Path $buildConfigRoot | Out-Null
$releaseConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $buildConfigPath -Encoding UTF8

if (-not $SkipBuild) {
    if (Test-Path -LiteralPath $bundleRoot) {
        $resolvedBundleRoot = [System.IO.Path]::GetFullPath($bundleRoot)
        $resolvedDesktopRoot = [System.IO.Path]::GetFullPath($desktopRoot)
        if (-not $resolvedBundleRoot.StartsWith($resolvedDesktopRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean bundle output outside desktop: $resolvedBundleRoot"
        }
        Remove-Item -LiteralPath $resolvedBundleRoot -Recurse -Force
    }
    Push-Location $desktopRoot
    try {
        Invoke-Checked "pnpm.cmd" @("install", "--frozen-lockfile")
        Invoke-Checked "pnpm.cmd" @(
            "run", "tauri:build",
            "--target", "x86_64-pc-windows-msvc",
            "--config", $buildConfigPath,
            "--", "--locked")
    } finally {
        Pop-Location
    }
}

Require-Path $bundleRoot "NSIS bundle directory"
$builtInstallers = @(Get-ChildItem -LiteralPath $bundleRoot -Filter "*-setup.exe" -File)
if ($builtInstallers.Count -ne 1) {
    throw "Expected exactly one NSIS installer under $bundleRoot; found $($builtInstallers.Count)."
}
$builtInstaller = $builtInstallers[0]
$builtSignature = "$($builtInstaller.FullName).sig"
Push-Location $desktopRoot
try {
    $signerArgs = @("exec", "tauri", "signer", "sign")
    if ([string]::IsNullOrEmpty($env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD)) {
        # The Tauri signer prompts when no password variable exists, even for an
        # intentionally unencrypted key. An explicit empty value keeps release
        # packaging non-interactive without putting a real password on argv.
        $signerArgs += "--password="
    }
    $signerArgs += $builtInstaller.FullName
    Invoke-Checked "pnpm.cmd" $signerArgs
} finally {
    Pop-Location
}
Require-Path $builtSignature "Tauri updater signature"

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null
Copy-Item -LiteralPath $builtInstaller.FullName -Destination $installerPath -Force
Copy-Item -LiteralPath $builtSignature -Destination $signaturePath -Force

$authenticode = Get-AuthenticodeSignature -LiteralPath $installerPath
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ($authenticode.Status -ne "Valid") {
        throw "NSIS installer Authenticode verification failed: $($authenticode.Status) $($authenticode.StatusMessage)"
    }
} elseif ($authenticode.Status -ne "NotSigned") {
    Write-Warning "Unsigned test installer returned Authenticode status $($authenticode.Status)."
}

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumPath = Join-Path $outputRootPath "$packageName.sha256.txt"
Set-Content -LiteralPath $sumPath -Value "$hash  $packageName.exe" -Encoding ASCII

Write-Host "Wrote $installerPath"
Write-Host "Wrote $signaturePath"
Write-Host "SHA256 $hash"
if ($authenticode.Status -eq "Valid") {
    Write-Host "Authenticode Valid: $($authenticode.SignerCertificate.Subject)"
} else {
    Write-Warning "Created an unsigned NSIS installer. Windows SmartScreen may warn; publishing also requires an explicit unsigned override."
}
