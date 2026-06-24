param(
    [string]$Version = "0.2.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$RuntimePackage = "runtime\BotController\build\package",
    [switch]$SkipCssBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$packageName = "cs2-demotracer-server-v$Version-windows-x64"
$stageRoot = Join-Path $outputRootPath $packageName
$zipPath = Join-Path $outputRootPath "$packageName.zip"
$runtimeRoot = Join-Path $repoRoot $RuntimePackage
$cssOut = Join-Path $repoRoot "css\DemoTracer\bin\$Configuration\net8.0"
$apiOut = Join-Path $repoRoot "css\DemoTracerApi\bin\$Configuration\net8.0"

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

if (-not $SkipCssBuild) {
    dotnet build (Join-Path $repoRoot "css\DemoTracer\DemoTracer.csproj") -c $Configuration
}

Require-Path (Join-Path $runtimeRoot "addons\BotController\bin\win64\BotController.dll") "BotController runtime DLL"
Require-Path (Join-Path $runtimeRoot "addons\BotController\gamedata.json") "BotController gamedata"
Require-Path (Join-Path $runtimeRoot "addons\metamod\BotController.vdf") "BotController Metamod VDF"
Require-Path (Join-Path $cssOut "DemoTracer.dll") "DemoTracer CSS plugin"
Require-Path (Join-Path $apiOut "DemoTracerApi.dll") "DemoTracer API assembly"

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$addonsOut = Join-Path $stageRoot "addons"
New-Item -ItemType Directory -Force -Path $addonsOut | Out-Null
Copy-Item -LiteralPath (Join-Path $runtimeRoot "addons\BotController") `
    -Destination (Join-Path $addonsOut "BotController") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $runtimeRoot "addons\metamod") `
    -Destination (Join-Path $addonsOut "metamod") -Recurse -Force

$pluginOut = Join-Path $stageRoot "addons\counterstrikesharp\plugins\DemoTracer"
Copy-RequiredFile (Join-Path $cssOut "DemoTracer.deps.json") (Join-Path $pluginOut "DemoTracer.deps.json")
Copy-RequiredFile (Join-Path $cssOut "DemoTracer.dll") (Join-Path $pluginOut "DemoTracer.dll")
Copy-RequiredFile (Join-Path $cssOut "DemoTracer.pdb") (Join-Path $pluginOut "DemoTracer.pdb")
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.deps.json") (Join-Path $pluginOut "DemoTracerApi.deps.json")
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.dll") (Join-Path $pluginOut "DemoTracerApi.dll")
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.pdb") (Join-Path $pluginOut "DemoTracerApi.pdb")

New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot "docs") | Out-Null
Copy-RequiredFile (Join-Path $repoRoot "docs\COMMANDS.md") (Join-Path $stageRoot "docs\COMMANDS.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\COMMANDS.zh-Hans.md") (Join-Path $stageRoot "docs\COMMANDS.zh-Hans.md")
Copy-RequiredFile (Join-Path $repoRoot "LICENSE") (Join-Path $stageRoot "LICENSE")

$gitCommit = "unknown"
try {
    $gitCommit = (git -C $repoRoot rev-parse --short=12 HEAD).Trim()
} catch {
}

$versionText = @"
CS2 DemoTracer Server Bundle
version: v$Version
git_commit: $gitCommit
platform: windows-x64
bundled_botcontroller_abi: 15
expected_demotracer_native_abi: 15
dtr_reader: 3..6
demotracer_api: 2

Install target:
Copy this package's addons directory into your CS2 server game/csgo directory.
"@
Set-Content -LiteralPath (Join-Path $stageRoot "VERSION.txt") -Value $versionText -Encoding UTF8

$readme = @'
# CS2 DemoTracer Server Bundle v__VERSION__

This is the complete Windows x64 server playback package. It includes the
Metamod BotController runtime and the CounterStrikeSharp DemoTracer plugin as a
matching ABI set.

## Install

1. Stop the CS2 server.
2. Copy this package's `addons` directory into the server `game/csgo` directory
   so it merges with the existing `addons` directory.
3. Start the server.
4. In the server console, run:

```text
dtr_runtime
bc_status
```

Expected ABI check:

```text
expected_abi=15 runtime_abi=15
```

If `runtime_abi` is lower than `15`, the server is still loading an old
`BotController.dll`. Replace `addons/BotController/bin/win64/BotController.dll`,
`addons/BotController/gamedata.json`, and `addons/metamod/BotController.vdf`
from this package.

## Contents

- `addons/BotController/bin/win64/BotController.dll`
- `addons/BotController/gamedata.json`
- `addons/metamod/BotController.vdf`
- `addons/counterstrikesharp/plugins/DemoTracer/`
- `docs/COMMANDS.md`
- `docs/COMMANDS.zh-Hans.md`

## Compatibility

- Required BotController native ABI: 15
- Bundled BotController native ABI: 15
- Supported `.dtr` reader versions: 3..6
- Maintained runtime platform: Windows x64
'@
$readme = $readme.Replace("__VERSION__", $Version)
Set-Content -LiteralPath (Join-Path $stageRoot "README.md") -Value $readme -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumPath = Join-Path $outputRootPath "$packageName.sha256.txt"
Set-Content -LiteralPath $sumPath -Value "$hash  $packageName.zip" -Encoding ASCII

Write-Host "Wrote $zipPath"
Write-Host "SHA256 $hash"
