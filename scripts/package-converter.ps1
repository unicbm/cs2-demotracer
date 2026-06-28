param(
    [string]$Version = "0.3.1",
    [string]$OutputRoot = "dist",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$packageName = "cs2-demotracer-v$Version-windows-x64"
$stageRoot = Join-Path $outputRootPath $packageName
$zipPath = Join-Path $outputRootPath "$packageName.zip"
$converterRoot = Join-Path $repoRoot "converter"
$releaseRoot = Join-Path $converterRoot "target\release"

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

if (-not $SkipBuild) {
    Invoke-Checked "cargo" @("build", "--manifest-path", (Join-Path $converterRoot "Cargo.toml"), "--release", "--locked", "--bin", "cs2-demotracer")
    Invoke-Checked "cargo" @("build", "--manifest-path", (Join-Path $converterRoot "Cargo.toml"), "--release", "--locked", "--features", "gui", "--bin", "cs2-demotracer-gui")
}

$cliExe = Join-Path $releaseRoot "cs2-demotracer.exe"
$guiExe = Join-Path $releaseRoot "cs2-demotracer-gui.exe"
Require-Path $cliExe "converter CLI executable"
Require-Path $guiExe "converter GUI executable"

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

Copy-RequiredFile $cliExe (Join-Path $stageRoot "cs2-demotracer.exe")
Copy-RequiredFile $guiExe (Join-Path $stageRoot "cs2-demotracer-gui.exe")
Copy-RequiredFile (Join-Path $repoRoot "README.md") (Join-Path $stageRoot "README.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\README.zh-Hans.md") (Join-Path $stageRoot "docs\README.zh-Hans.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\USAGE.md") (Join-Path $stageRoot "docs\USAGE.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\USAGE.zh-Hans.md") (Join-Path $stageRoot "docs\USAGE.zh-Hans.md")
Copy-RequiredFile (Join-Path $repoRoot "LICENSE") (Join-Path $stageRoot "LICENSE")

$gitCommit = "unknown"
try {
    $gitCommit = (git -C $repoRoot rev-parse --short=12 HEAD).Trim()
} catch {
}

$versionText = @"
CS2 DemoTracer Converter
version: v$Version
git_commit: $gitCommit
platform: windows-x64
cli: cs2-demotracer.exe
gui: cs2-demotracer-gui.exe
dtr_writer: 7
manifest_abi: 17

Use cs2-demotracer.exe for CLI, batch, pool, and Demo2Nade workflows.
Use cs2-demotracer-gui.exe for the single-demo Windows GUI workbench.
"@
Set-Content -LiteralPath (Join-Path $stageRoot "VERSION.txt") -Value $versionText -Encoding UTF8

$readme = @'
# CS2 DemoTracer Converter v__VERSION__

This Windows x64 package contains both converter entry points:

- `cs2-demotracer.exe`: CLI for inspect, convert, validate, wizard, pool, and Demo2Nade workflows.
- `cs2-demotracer-gui.exe`: native Rust GUI for single-demo conversion.

The GUI does not replace the CLI. Batch pool conversion and Demo2Nade remain
CLI workflows in this release.

## Quick Start

Open the GUI:

```powershell
.\cs2-demotracer-gui.exe
```

Or run the CLI:

```powershell
.\cs2-demotracer.exe inspect --demo "<demo.dem>"
.\cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>"
.\cs2-demotracer.exe validate --input "<output-dir>"
```

Generated replay output is consumed by the CS2 DemoTracer server bundle.
'@
$readme = $readme.Replace("__VERSION__", $Version)
Set-Content -LiteralPath (Join-Path $stageRoot "PACKAGE.md") -Value $readme -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumPath = Join-Path $outputRootPath "$packageName.sha256.txt"
Set-Content -LiteralPath $sumPath -Value "$hash  $packageName.zip" -Encoding ASCII

Write-Host "Wrote $zipPath"
Write-Host "SHA256 $hash"
