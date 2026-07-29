param(
    [string]$BootstrapVersion = "1.0.0",
    [string]$UpdateVersion = "1.0.1",
    [string]$Bucket = "cs2-demotracer-releases",
    [string]$ReleaseBaseUrl = "https://releases.detr.site",
    [string]$TestRoot = "dist\gui-update-test-v1.0.0-to-v1.0.1",
    [switch]$AllowUnsignedInstaller,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testRootPath = if ([System.IO.Path]::IsPathRooted($TestRoot)) {
    [System.IO.Path]::GetFullPath($TestRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $TestRoot))
}
$releaseBase = $ReleaseBaseUrl.TrimEnd('/')
$latestPath = Join-Path $testRootPath "latest.json"
if (-not (Test-Path -LiteralPath $latestPath -PathType Leaf)) {
    throw "Required GUI update test asset is missing: latest.json"
}
$latest = Get-Content -LiteralPath $latestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$updateUri = $null
if (-not [System.Uri]::TryCreate($latest.platforms.'windows-x86_64'.url, [System.UriKind]::Absolute, [ref]$updateUri)) {
    throw "Test latest.json contains an invalid updater URL."
}
$updateName = [System.Uri]::UnescapeDataString([System.IO.Path]::GetFileName($updateUri.AbsolutePath))
$bootstrapCandidates = @(Get-ChildItem -LiteralPath $testRootPath -File -Filter "cs2-demotracer-gui-update-test-v$BootstrapVersion-*-windows-x64.exe")
if ($bootstrapCandidates.Count -ne 1) {
    throw "Expected exactly one content-addressed v$BootstrapVersion bootstrap installer; found $($bootstrapCandidates.Count)."
}
$bootstrapName = $bootstrapCandidates[0].Name
$required = @(
    $bootstrapName,
    "$bootstrapName.sig",
    $updateName,
    "$updateName.sig",
    "latest.json",
    "SHA256SUMS.txt"
)

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

if (-not (Test-Path -LiteralPath $testRootPath -PathType Container)) {
    throw "GUI update test directory not found: $testRootPath"
}
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $testRootPath $name) -PathType Leaf)) {
        throw "Required GUI update test asset is missing: $name"
    }
}

foreach ($installerName in @($bootstrapName, $updateName)) {
    $installerPath = Join-Path $testRootPath $installerName
    $authenticode = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($authenticode.Status -ne "Valid" -and -not $AllowUnsignedInstaller -and -not $DryRun) {
        throw "Refusing to publish test installer without Authenticode unless -AllowUnsignedInstaller is supplied: $installerName ($($authenticode.Status))"
    }
    if ($authenticode.Status -ne "Valid") {
        Write-Warning "Publishing explicitly allowed unsigned test installer: $installerName. Windows SmartScreen may warn."
    }

    $signature = (Get-Content -LiteralPath "$installerPath.sig" -Raw -Encoding UTF8).Trim()
    if ([string]::IsNullOrWhiteSpace($signature)) {
        throw "Tauri updater signature is empty: $installerName.sig"
    }
}

$expectedSums = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $testRootPath "SHA256SUMS.txt") -Encoding ASCII) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/]+)$') {
        throw "Invalid SHA256SUMS.txt line: $line"
    }
    $expectedSums[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
foreach ($name in $required | Where-Object { $_ -ne "SHA256SUMS.txt" }) {
    if (-not $expectedSums.ContainsKey($name)) {
        throw "SHA256SUMS.txt omits test asset: $name"
    }
    $actual = (Get-FileHash -LiteralPath (Join-Path $testRootPath $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expectedSums[$name]) {
        throw "SHA-256 mismatch for test asset: $name"
    }
}

$expectedUpdateUrl = "$releaseBase/test/gui-updater/v$UpdateVersion/$updateName"
if ($latest.version -ne $UpdateVersion -or
    $latest.platforms.'windows-x86_64'.url -ne $expectedUpdateUrl -or
    $updateName -notmatch "^cs2-demotracer-gui-update-test-v$([regex]::Escape($UpdateVersion))-[0-9a-f]{12}-windows-x64\.exe$") {
    throw "Test latest.json does not advertise the expected v$UpdateVersion immutable URL."
}

# Authenticate and verify the exact R2 target before any object mutation.
Invoke-Checked "npx.cmd" @("--yes", "wrangler@latest", "whoami")
Invoke-Checked "npx.cmd" @("--yes", "wrangler@latest", "r2", "bucket", "info", $Bucket)

foreach ($name in @($bootstrapName, "$bootstrapName.sig", $updateName, "$updateName.sig")) {
    $path = Join-Path $testRootPath $name
    $contentType = if ($name.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
        "application/vnd.microsoft.portable-executable"
    } else {
        "text/plain; charset=utf-8"
    }
    $version = if ($name.StartsWith("cs2-demotracer-gui-update-test-v$BootstrapVersion", [System.StringComparison]::OrdinalIgnoreCase)) {
        $BootstrapVersion
    } else {
        $UpdateVersion
    }
    $arguments = @(
        "--yes", "wrangler@latest", "r2", "object", "put",
        "$Bucket/test/gui-updater/v$version/$name",
        "--file=$path",
        "--content-type=$contentType",
        "--cache-control=public, max-age=31536000, immutable",
        "--remote",
        "--force"
    )
    if ($name.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
        $arguments += "--content-disposition=attachment; filename=`"$name`""
    }
    Invoke-Checked "npx.cmd" $arguments
}

# Publish the mutable channel pointer only after every immutable object exists.
Invoke-Checked "npx.cmd" @(
    "--yes", "wrangler@latest", "r2", "object", "put",
    "$Bucket/channels/test/latest.json",
    "--file=$(Join-Path $testRootPath 'latest.json')",
    "--content-type=application/json; charset=utf-8",
    "--cache-control=no-store",
    "--remote",
    "--force"
)

if (-not $DryRun) {
    $cacheBuster = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $remoteLatest = Invoke-RestMethod -Uri "$releaseBase/channels/test/latest.json?verify=$cacheBuster"
    if ($remoteLatest.version -ne $UpdateVersion -or
        $remoteLatest.platforms.'windows-x86_64'.url -ne $expectedUpdateUrl) {
        throw "R2 verification returned a stale or unexpected GUI update test manifest."
    }
    $head = Invoke-WebRequest -Method Head -Uri $expectedUpdateUrl
    if ($head.StatusCode -ne 200) {
        throw "R2 updater asset verification returned HTTP $($head.StatusCode)."
    }
    Write-Host "Published and verified isolated GUI update test v$BootstrapVersion -> v$UpdateVersion."
    Write-Host "Stable channel was not modified."
}
