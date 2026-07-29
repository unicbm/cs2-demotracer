param(
    [string]$Version = "1.0.0",
    [string]$Bucket = "cs2-demotracer-releases",
    [string]$ReleaseBaseUrl = "https://releases.detr.site",
    [string]$ReleaseRoot = "",
    [switch]$AllowUnsignedInstaller,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $repoRoot "dist\release-v$Version"
} elseif (-not [System.IO.Path]::IsPathRooted($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $repoRoot $ReleaseRoot
}
$ReleaseRoot = [System.IO.Path]::GetFullPath($ReleaseRoot)
$releaseBase = $ReleaseBaseUrl.TrimEnd('/')

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    Write-Host "> $Command $($Arguments -join ' ')"
    if ($DryRun) {
        return
    }
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

function Content-TypeFor([string]$Name) {
    if ($Name.EndsWith(".json", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "application/json; charset=utf-8"
    }
    if ($Name.EndsWith(".txt", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Name.EndsWith(".sig", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "text/plain; charset=utf-8"
    }
    if ($Name.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "application/zip"
    }
    return "application/vnd.microsoft.portable-executable"
}

if (-not (Test-Path -LiteralPath $ReleaseRoot -PathType Container)) {
    throw "Release directory not found: $ReleaseRoot"
}
$required = @(
    "cs2-demotracer-setup-v$Version-windows-x64.exe",
    "cs2-demotracer-setup-v$Version-windows-x64.exe.sig",
    "cs2-demotracer-playback-v$Version-windows-x64.zip",
    "cs2-demotracer-playback-v$Version-windows-x64.zip.sig",
    "latest.json",
    "playback.json",
    "SHA256SUMS.txt"
)
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $ReleaseRoot $name) -PathType Leaf)) {
        throw "Required release asset is missing: $name"
    }
}

$installerPath = Join-Path $ReleaseRoot "cs2-demotracer-setup-v$Version-windows-x64.exe"
$authenticode = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($authenticode.Status -ne "Valid") {
    if (-not $DryRun -and -not $AllowUnsignedInstaller) {
        throw "Refusing to publish an installer without a valid Authenticode signature: $($authenticode.Status)"
    }
    Write-Warning "Publishing an explicitly allowed unsigned installer: $($authenticode.Status). Windows SmartScreen may warn users."
}

$expectedSums = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $ReleaseRoot "SHA256SUMS.txt") -Encoding ASCII) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/]+)$') {
        throw "Invalid SHA256SUMS.txt line: $line"
    }
    $expectedSums[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
foreach ($name in $required | Where-Object { $_ -ne "SHA256SUMS.txt" }) {
    if (-not $expectedSums.ContainsKey($name)) {
        throw "SHA256SUMS.txt omits release asset: $name"
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $ReleaseRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedSums[$name]) {
        throw "SHA-256 mismatch for release asset: $name"
    }
}

$latest = Get-Content -LiteralPath (Join-Path $ReleaseRoot "latest.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$playback = Get-Content -LiteralPath (Join-Path $ReleaseRoot "playback.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ($latest.version -ne $Version -or $playback.version -ne $Version) {
    throw "Release manifest version does not match v$Version."
}
if (-not $latest.platforms.'windows-x86_64'.url.StartsWith("$releaseBase/releases/v$Version/", [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $playback.url.StartsWith("$releaseBase/releases/v$Version/", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release manifests do not point at the immutable v$Version R2 prefix."
}

Invoke-Checked "npx.cmd" @("--yes", "wrangler@latest", "whoami")
Invoke-Checked "npx.cmd" @("--yes", "wrangler@latest", "r2", "bucket", "info", $Bucket)

foreach ($name in $required) {
    $path = Join-Path $ReleaseRoot $name
    $contentType = Content-TypeFor $name
    $arguments = @(
        "--yes", "wrangler@latest", "r2", "object", "put",
        "$Bucket/releases/v$Version/$name",
        "--file=$path",
        "--content-type=$contentType",
        "--cache-control=public, max-age=31536000, immutable",
        "--remote",
        "--force"
    )
    if ($name.EndsWith(".exe") -or $name.EndsWith(".zip")) {
        $arguments += "--content-disposition=attachment; filename=`"$name`""
    }
    Invoke-Checked "npx.cmd" $arguments
}

$downloadAliases = [ordered]@{
    "CS2-DemoTracer-Setup.exe" = "cs2-demotracer-setup-v$Version-windows-x64.exe"
    "CS2-DemoTracer-Setup.exe.sig" = "cs2-demotracer-setup-v$Version-windows-x64.exe.sig"
    "CS2-DemoTracer-Playback.zip" = "cs2-demotracer-playback-v$Version-windows-x64.zip"
    "CS2-DemoTracer-Playback.zip.sig" = "cs2-demotracer-playback-v$Version-windows-x64.zip.sig"
}
foreach ($alias in $downloadAliases.GetEnumerator()) {
    $path = Join-Path $ReleaseRoot $alias.Value
    $aliasArguments = @(
        "--yes", "wrangler@latest", "r2", "object", "put",
        "$Bucket/downloads/$($alias.Key)",
        "--file=$path",
        "--content-type=$(Content-TypeFor $alias.Value)",
        "--cache-control=public, max-age=300, must-revalidate",
        "--remote",
        "--force"
    )
    if (-not $alias.Key.EndsWith(".sig", [System.StringComparison]::OrdinalIgnoreCase)) {
        $aliasArguments += "--content-disposition=attachment; filename=`"$($alias.Key)`""
    }
    Invoke-Checked "npx.cmd" $aliasArguments
}

foreach ($channelManifest in @("playback.json", "latest.json")) {
    $path = Join-Path $ReleaseRoot $channelManifest
    Invoke-Checked "npx.cmd" @(
        "--yes", "wrangler@latest", "r2", "object", "put",
        "$Bucket/channels/stable/$channelManifest",
        "--file=$path",
        "--content-type=application/json; charset=utf-8",
        "--cache-control=public, max-age=300, must-revalidate",
        "--remote",
        "--force"
    )
}

if (-not $DryRun) {
    $remoteLatest = Invoke-RestMethod -Uri "$releaseBase/channels/stable/latest.json"
    $remotePlayback = Invoke-RestMethod -Uri "$releaseBase/channels/stable/playback.json"
    if ($remoteLatest.version -ne $Version -or $remotePlayback.version -ne $Version) {
        throw "R2 verification returned a stale release manifest."
    }
    Write-Host "Published and verified DemoTracer v$Version at $releaseBase"
}
