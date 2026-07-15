param(
    [string]$Version = "0.7.1",
    [string]$OutputRoot = "dist",
    [ValidateSet("All", "Cli", "Gui")]
    [string]$Package = "All",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$converterRoot = Join-Path $repoRoot "converter"
$converterReleaseRoot = Join-Path $converterRoot "target\release"
$desktopRoot = Join-Path $repoRoot "desktop"
$desktopReleaseRoot = Join-Path $desktopRoot "src-tauri\target\x86_64-pc-windows-msvc\release"

function Require-Path([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
}

function Copy-RequiredFile([string]$Source, [string]$Destination) {
    Require-Path $Source "required file"
    $destinationDir = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

function Copy-ConverterDocs([string]$StageRoot) {
    Copy-RequiredFile (Join-Path $repoRoot "README.md") (Join-Path $StageRoot "README.md")
    Copy-RequiredFile (Join-Path $repoRoot "docs\README.zh-Hans.md") (Join-Path $StageRoot "docs\README.zh-Hans.md")
    Copy-RequiredFile (Join-Path $repoRoot "docs\USAGE.md") (Join-Path $StageRoot "docs\USAGE.md")
    Copy-RequiredFile (Join-Path $repoRoot "docs\USAGE.zh-Hans.md") (Join-Path $StageRoot "docs\USAGE.zh-Hans.md")
    Copy-RequiredFile (Join-Path $repoRoot "docs\VOICE.md") (Join-Path $StageRoot "docs\VOICE.md")
    Copy-RequiredFile (Join-Path $repoRoot "docs\VOICE.zh-Hans.md") (Join-Path $StageRoot "docs\VOICE.zh-Hans.md")
    Copy-RequiredFile (Join-Path $repoRoot "LICENSE") (Join-Path $StageRoot "LICENSE")
}

function New-ConverterPackage([ValidateSet("cli", "gui")][string]$Kind) {
    $packageName = "cs2-demotracer-$Kind-v$Version-windows-x64"
    $stageRoot = Join-Path $outputRootPath $packageName
    $zipPath = Join-Path $outputRootPath "$packageName.zip"
    $executableName = if ($Kind -eq "cli") { "cs2-demotracer.exe" } else { "cs2-demotracer-gui.exe" }
    $displayName = if ($Kind -eq "cli") { "CLI" } else { "GUI" }
    $executableRoot = if ($Kind -eq "cli") { $converterReleaseRoot } else { $desktopReleaseRoot }
    $executablePath = Join-Path $executableRoot $executableName
    Require-Path $executablePath "converter $displayName executable"

    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

    Copy-RequiredFile $executablePath (Join-Path $stageRoot $executableName)
    Copy-ConverterDocs $stageRoot

    $gitCommit = "unknown"
    try {
        $gitCommit = (git -C $repoRoot rev-parse --short=12 HEAD).Trim()
    } catch {
    }

    $versionText = @"
CS2 DemoTracer Converter $displayName
version: v$Version
git_commit: $gitCommit
platform: windows-x64
package: $Kind
entrypoint: $executableName
dtr_writer: 7
manifest_abi: 17
"@
    Set-Content -LiteralPath (Join-Path $stageRoot "VERSION.txt") -Value $versionText -Encoding UTF8

    if ($Kind -eq "cli") {
        $packageReadme = @'
# CS2 DemoTracer CLI v__VERSION__

This Windows x64 download contains the command-line converter only. It is the
smaller package for inspect, convert, validate, wizard, batch, and pool workflows.
Download the separate GUI package if you prefer a desktop single-demo workbench.

## Quick Start

```powershell
.\cs2-demotracer.exe inspect --demo "<demo.dem>"
.\cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>"
.\cs2-demotracer.exe validate --input "<output-dir>"
```

To export demo-backed in-game voice sidecars for automatic playback, add
`--export-voice`. See `docs/VOICE.md` and `docs/VOICE.zh-Hans.md`.

Generated replay output is consumed by the separate CS2 DemoTracer Playback
Bundle installed on a local Windows x64 CS2 server.
'@
    } else {
        $packageReadme = @'
# CS2 DemoTracer GUI v__VERSION__

This Windows x64 download contains the Tauri single-demo desktop workbench only.
Download the separate CLI package for inspect, validate, wizard, batch, and pool
workflows.

## Quick Start

```powershell
.\cs2-demotracer-gui.exe
```

In the GUI, analyze a demo, choose rounds and export options, then convert it to
`.dtr` replay output. See `docs/USAGE.md` and `docs/USAGE.zh-Hans.md`.

The GUI requires the Microsoft Edge WebView2 Runtime, normally present on
current Windows 10 and Windows 11 installations. Install a current WebView2
Runtime if it is absent. Node.js is needed only when building the GUI from
source; it is not required to run this packaged executable.

Generated replay output is consumed by the separate CS2 DemoTracer Playback
Bundle installed on a local Windows x64 CS2 server.
'@
    }
    $packageReadme = $packageReadme.Replace("__VERSION__", $Version)
    Set-Content -LiteralPath (Join-Path $stageRoot "PACKAGE.md") -Value $packageReadme -Encoding UTF8

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -Force

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumPath = Join-Path $outputRootPath "$packageName.sha256.txt"
    Set-Content -LiteralPath $sumPath -Value "$hash  $packageName.zip" -Encoding ASCII

    Write-Host "Wrote $zipPath"
    Write-Host "SHA256 $hash"
}

$buildCli = $Package -eq "All" -or $Package -eq "Cli"
$buildGui = $Package -eq "All" -or $Package -eq "Gui"

if (-not $SkipBuild) {
    if ($buildCli) {
        Invoke-Checked "cargo" @(
            "build", "--manifest-path", (Join-Path $converterRoot "Cargo.toml"),
            "--release", "--locked", "--no-default-features", "--features", "cli,demoparser",
            "--bin", "cs2-demotracer")
    }
    if ($buildGui) {
        Push-Location $desktopRoot
        try {
            Invoke-Checked "npm.cmd" @("ci")
            Invoke-Checked "npm.cmd" @(
                "run", "tauri:build", "--",
                "--target", "x86_64-pc-windows-msvc", "--", "--locked")
        } finally {
            Pop-Location
        }
    }
}

if ($buildCli) {
    New-ConverterPackage "cli"
}
if ($buildGui) {
    New-ConverterPackage "gui"
}
