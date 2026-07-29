param(
    [string]$BootstrapVersion = "1.0.0",
    [string]$UpdateVersion = "1.0.1",
    [string]$OutputRoot = "dist\gui-update-test-v1.0.0-to-v1.0.1",
    [string]$ReleaseBaseUrl = "https://releases.detr.site",
    [string]$UpdaterPublicKeyPath = "tooling\release\updater-public-key.txt",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$ReleaseNotes = "Improved the update experience and fixed a few issues.",
    [string]$ReleaseNotesZh = "优化了更新体验，并修复了一些问题。",
    [switch]$AllowUnsignedInstaller,
    [switch]$SkipDependencyInstall
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$desktopRoot = Join-Path $repoRoot "desktop\gui"
$distRoot = Join-Path $repoRoot "dist"
$outputRootPath = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
$bundleRoot = Join-Path $desktopRoot "src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis"
$publicKeyPath = if ([System.IO.Path]::IsPathRooted($UpdaterPublicKeyPath)) {
    $UpdaterPublicKeyPath
} else {
    Join-Path $repoRoot $UpdaterPublicKeyPath
}

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

function Assert-SafeChildPath([string]$Path, [string]$Parent, [string]$Label) {
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must stay below $resolvedParent"
    }
}

$releaseBase = $ReleaseBaseUrl.TrimEnd('/')
$releaseUri = $null
if (-not [System.Uri]::TryCreate($releaseBase, [System.UriKind]::Absolute, [ref]$releaseUri) -or
    $releaseUri.Scheme -ne "https" -or
    [string]::IsNullOrWhiteSpace($releaseUri.Host)) {
    throw "ReleaseBaseUrl must be an absolute HTTPS URL."
}

$bootstrapSemVer = $null
$updateSemVer = $null
if (-not [System.Version]::TryParse($BootstrapVersion, [ref]$bootstrapSemVer) -or
    -not [System.Version]::TryParse($UpdateVersion, [ref]$updateSemVer)) {
    throw "BootstrapVersion and UpdateVersion must be numeric versions."
}
if ($updateSemVer -le $bootstrapSemVer) {
    throw "UpdateVersion must be newer than BootstrapVersion."
}

Assert-SafeChildPath $outputRootPath $distRoot "OutputRoot"
Assert-SafeChildPath $bundleRoot $desktopRoot "Tauri bundle directory"
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
    throw "Authenticode certificate thumbprint is required unless -AllowUnsignedInstaller is explicitly supplied."
}
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
    $CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "CertificateThumbprint must be a 40-character hexadecimal certificate thumbprint."
}

if (Test-Path -LiteralPath $outputRootPath) {
    Remove-Item -LiteralPath $outputRootPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null

if (-not $SkipDependencyInstall) {
    Push-Location $desktopRoot
    try {
        Invoke-Checked "pnpm.cmd" @("install", "--frozen-lockfile")
    } finally {
        Pop-Location
    }
}

function Build-TestInstaller([string]$Version) {
    $windowsBundle = [ordered]@{}
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $windowsBundle.certificateThumbprint = $CertificateThumbprint.ToUpperInvariant()
        $windowsBundle.digestAlgorithm = "sha256"
        $windowsBundle.timestampUrl = $TimestampUrl
    }

    $testConfig = [ordered]@{
        version = $Version
        bundle = [ordered]@{
            windows = $windowsBundle
        }
        plugins = [ordered]@{
            updater = [ordered]@{
                pubkey = $updaterPublicKey
                endpoints = @("$releaseBase/channels/test/latest.json")
                windows = [ordered]@{
                    installMode = "passive"
                }
            }
            demotracerRelease = [ordered]@{
                playbackManifestUrl = "$releaseBase/channels/stable/playback.json"
            }
        }
    }

    $configPath = Join-Path $outputRootPath "tauri.gui-update-test.v$Version.json"
    $testConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8

    if (Test-Path -LiteralPath $bundleRoot) {
        Remove-Item -LiteralPath $bundleRoot -Recurse -Force
    }

    Push-Location $desktopRoot
    try {
        Invoke-Checked "pnpm.cmd" @(
            "run", "tauri:build",
            "--target", "x86_64-pc-windows-msvc",
            "--config", $configPath,
            "--", "--locked")
    } finally {
        Pop-Location
    }

    Require-Path $bundleRoot "NSIS bundle directory"
    $builtInstallers = @(Get-ChildItem -LiteralPath $bundleRoot -Filter "*-setup.exe" -File)
    if ($builtInstallers.Count -ne 1) {
        throw "Expected exactly one NSIS installer under $bundleRoot; found $($builtInstallers.Count)."
    }
    $builtInstaller = $builtInstallers[0]

    Push-Location $desktopRoot
    try {
        $signerArgs = @("exec", "tauri", "signer", "sign")
        if ([string]::IsNullOrEmpty($env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD)) {
            $signerArgs += "--password="
        }
        $signerArgs += $builtInstaller.FullName
        Invoke-Checked "pnpm.cmd" $signerArgs
    } finally {
        Pop-Location
    }

    $builtSignature = "$($builtInstaller.FullName).sig"
    Require-Path $builtSignature "Tauri updater signature"
    $contentHash = (Get-FileHash -LiteralPath $builtInstaller.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $assetName = "cs2-demotracer-gui-update-test-v$Version-$($contentHash.Substring(0, 12))-windows-x64.exe"
    $assetPath = Join-Path $outputRootPath $assetName
    Copy-Item -LiteralPath $builtInstaller.FullName -Destination $assetPath -Force
    Copy-Item -LiteralPath $builtSignature -Destination "$assetPath.sig" -Force

    $authenticode = Get-AuthenticodeSignature -LiteralPath $assetPath
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and $authenticode.Status -ne "Valid") {
        throw "NSIS installer Authenticode verification failed for v$Version`: $($authenticode.Status)"
    }
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -and $authenticode.Status -ne "NotSigned") {
        Write-Warning "Unsigned test installer v$Version returned Authenticode status $($authenticode.Status)."
    }

    $productVersion = (Get-Item -LiteralPath $assetPath).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion) -or -not $productVersion.StartsWith($Version, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Built installer product version '$productVersion' does not match v$Version."
    }

    Write-Host "Built GUI update test installer v${Version}: $assetPath"
    return [pscustomobject]@{
        Version = $Version
        Name = $assetName
        Path = $assetPath
        SignaturePath = "$assetPath.sig"
        ProductVersion = $productVersion
        Authenticode = $authenticode.Status.ToString()
        Sha256 = $contentHash
    }
}

$bootstrap = Build-TestInstaller $BootstrapVersion
$update = Build-TestInstaller $UpdateVersion
$publishedAt = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)
$signature = (Get-Content -LiteralPath $update.SignaturePath -Raw -Encoding UTF8).Trim()
if ([string]::IsNullOrWhiteSpace($signature)) {
    throw "Updater signature for v$UpdateVersion is empty."
}
$localizedReleaseNotes = [ordered]@{
    zh = $ReleaseNotesZh
    en = $ReleaseNotes
} | ConvertTo-Json -Compress

$latest = [ordered]@{
    version = $UpdateVersion
    notes = $localizedReleaseNotes
    pub_date = $publishedAt
    platforms = [ordered]@{
        "windows-x86_64" = [ordered]@{
            signature = $signature
            url = "$releaseBase/test/gui-updater/v$UpdateVersion/$($update.Name)"
        }
    }
}
$latestPath = Join-Path $outputRootPath "latest.json"
$latest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $latestPath -Encoding UTF8

$assetNames = @(
    $bootstrap.Name,
    "$($bootstrap.Name).sig",
    $update.Name,
    "$($update.Name).sig",
    "latest.json"
)
$sumLines = foreach ($assetName in $assetNames) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $outputRootPath $assetName) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $assetName"
}
Set-Content -LiteralPath (Join-Path $outputRootPath "SHA256SUMS.txt") -Value $sumLines -Encoding ASCII

$instructions = @(
    "CS2 DemoTracer GUI updater end-to-end test",
    "",
    "1. Run $($bootstrap.Name).",
    "2. Launch DemoTracer v$BootstrapVersion.",
    "3. Wait for the update dialog, or open Settings > Install & update and click Check desktop update.",
    "4. Confirm that the dialog shows current v$BootstrapVersion, latest v$UpdateVersion, and release notes.",
    "5. Click Download and install. The app should install the signed GUI update and relaunch.",
    "6. Confirm Settings shows desktop application v$UpdateVersion.",
    "",
    "This test channel updates only the desktop GUI. Playback components remain manual and use the stable playback channel."
)
Set-Content -LiteralPath (Join-Path $outputRootPath "TEST-INSTRUCTIONS.txt") -Value $instructions -Encoding UTF8

Write-Host "GUI update test package is ready: $outputRootPath"
Write-Host "Bootstrap: $($bootstrap.Name) ($($bootstrap.ProductVersion), Authenticode $($bootstrap.Authenticode))"
Write-Host "Update: $($update.Name) ($($update.ProductVersion), Authenticode $($update.Authenticode))"
Write-Host "Channel manifest: $latestPath"
