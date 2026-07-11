param(
    [string]$Version = "0.5.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$RuntimePackage = "runtime\BotController\build\package",
    [string]$RuntimeBuild = "runtime\BotController\build",
    [string]$DotnetPath = "",
    [switch]$BuildRuntime,
    [switch]$SkipCssBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$packageName = "cs2-demotracer-server-v$Version-windows-x64"
$stageRoot = Join-Path $outputRootPath $packageName
$zipPath = Join-Path $outputRootPath "$packageName.zip"
$runtimeRoot = if ([System.IO.Path]::IsPathRooted($RuntimePackage)) {
    $RuntimePackage
} else {
    Join-Path $repoRoot $RuntimePackage
}
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

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

function Test-DotnetHasSdk([string]$Command) {
    try {
        $sdks = & $Command --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $false
        }
        return $null -ne ($sdks | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    } catch {
        return $false
    }
}

function Resolve-DotnetPath([string]$PreferredPath) {
    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        if (Test-DotnetHasSdk $PreferredPath) {
            return $PreferredPath
        }
        throw "dotnet SDK not found at preferred path: $PreferredPath"
    }

    $candidates = @()
    $candidates += (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
    if ($env:DOTNET_ROOT_X64) {
        $candidates += (Join-Path $env:DOTNET_ROOT_X64 "dotnet.exe")
    }
    if ($env:DOTNET_ROOT) {
        $candidates += (Join-Path $env:DOTNET_ROOT "dotnet.exe")
    }
    $candidates += "C:\Program Files\dotnet\dotnet.exe"
    $candidates += "C:\Program Files (x86)\dotnet\dotnet.exe"

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-DotnetHasSdk $candidate) {
            return $candidate
        }
    }

    $command = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command -and (Test-DotnetHasSdk $command.Source)) {
        return $command.Source
    }
    throw "dotnet SDK not found. Install a .NET SDK or pass -DotnetPath to a dotnet.exe with SDKs installed."
}

if ($BuildRuntime) {
    Invoke-Checked "cmake" @("--build", (Join-Path $repoRoot $RuntimeBuild), "--config", $Configuration, "--target", "BotController")
}

if (-not $SkipCssBuild) {
    $resolvedDotnetPath = Resolve-DotnetPath $DotnetPath
    Invoke-Checked $resolvedDotnetPath @("build", (Join-Path $repoRoot "css\DemoTracer\DemoTracer.csproj"), "-c", $Configuration)
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
Copy-RequiredFile (Join-Path $cssOut "demotracer-econ-index.v1.json") (Join-Path $pluginOut "demotracer-econ-index.v1.json")
Copy-RequiredFile (Join-Path $repoRoot "css\DemoTracer\demotracer.config.example.json") (Join-Path $pluginOut "demotracer.config.example.json")
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.deps.json") (Join-Path $pluginOut "DemoTracerApi.deps.json")
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.dll") (Join-Path $pluginOut "DemoTracerApi.dll")
if ($IncludeSymbols) {
    Copy-RequiredFile (Join-Path $cssOut "DemoTracer.pdb") (Join-Path $pluginOut "DemoTracer.pdb")
    Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.pdb") (Join-Path $pluginOut "DemoTracerApi.pdb")
}

New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot "docs") | Out-Null
Copy-RequiredFile (Join-Path $repoRoot "docs\COMMANDS.md") (Join-Path $stageRoot "docs\COMMANDS.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\COMMANDS.zh-Hans.md") (Join-Path $stageRoot "docs\COMMANDS.zh-Hans.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\VOICE.md") (Join-Path $stageRoot "docs\VOICE.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\VOICE.zh-Hans.md") (Join-Path $stageRoot "docs\VOICE.zh-Hans.md")
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
bundled_botcontroller_abi: 16
bundled_botcontroller_abi_minor: 29
expected_demotracer_native_abi: 16
dtr_reader: 3..7
demotracer_api: 5

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
2. Make sure the server already has Metamod:Source and CounterStrikeSharp
   v1.0.371 or newer installed. They are prerequisites and are not included in
   this bundle.
3. Copy this package's `addons` directory into the server `game/csgo` directory
   so it merges with the existing `addons` directory.
4. Start the server.
5. In the server console, run:

```text
dtr_runtime
bc_status
```

Expected ABI check:

```text
expected_abi=16 runtime_abi=16 abi_minor=29
```

For v0.5.0, require `runtime_abi=16` and `abi_minor=29` or newer. If the minor
version is missing or lower, replace the complete server bundle, including
`addons/BotController/bin/win64/BotController.dll`,
`addons/BotController/gamedata.json`, and `addons/metamod/BotController.vdf`.

## Voice Replay

Voice playback uses demo-backed `.dtv` sidecars exported by the converter with
`--export-voice`. Keep `voice/roundXX.dtv` next to the matching manifest output
and enable `dtr_voice_auto on` before `dtr_go seq` or `dtr_go round`. See
`docs/VOICE.md` and `docs/VOICE.zh-Hans.md`.

## Chat Replay

Text chat is stored in `manifest.json` as `rounds[].chat_messages` and replays
through CS2's native `say` / `say_team` path when `dtr_chat_auto on` is enabled
(default). For spectator testing of native player text chat, set
`sv_full_alltalk 1`; `sv_allchat 1` by itself is not enough.

## Contents

- `addons/BotController/bin/win64/BotController.dll`
- `addons/BotController/gamedata.json`
- `addons/metamod/BotController.vdf`
- `addons/counterstrikesharp/plugins/DemoTracer/`
  - `demotracer.config.example.json` sanitized local runtime defaults
  - `demotracer-econ-index.v1.json` compact econ validation index
- `docs/COMMANDS.md`
- `docs/COMMANDS.zh-Hans.md`
- `docs/VOICE.md`
- `docs/VOICE.zh-Hans.md`

## Compatibility

- Required BotController native ABI: 16
- Bundled BotController native ABI: 16
- Bundled BotController native ABI minor: 29
- Supported `.dtr` reader versions: 3..7
- DemoTracer companion API: 6
- Maintained runtime platform: Windows x64

## Dependencies

Required external server prerequisites:

- Metamod:Source
- CounterStrikeSharp v1.0.371 or newer for CS2 1.41.6.9

Included in this bundle:

- `BotController` Metamod runtime
- `DemoTracer` CounterStrikeSharp plugin
- `DemoTracerApi.dll`
- `demotracer-econ-index.v1.json`
- `demotracer.config.example.json`

Optional:

- CS2-Bot-Hider, only for BotHider-managed replay slots and identity alignment
  features such as demo display names, SteamID64 alignment, and demo avatar
  override alignment. For the July 2026 update, use a build containing the
  Windows client identity-offset fix; tagged v0.2.5 predates it.
- Ray-Trace v1.0.16 or newer, only for stricter line-of-sight filtering in
  handoff 360 threat detection.
- Demo-backed agent model evidence can change the matching safe replay bot slot
  to the demo agent model when `dtr_cosmetics agents` is enabled.
'@
$readme = $readme.Replace("__VERSION__", $Version)
Set-Content -LiteralPath (Join-Path $stageRoot "README.md") -Value $readme -Encoding UTF8

if (-not $IncludeSymbols) {
    $pdbFiles = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -Filter "*.pdb" -File -ErrorAction SilentlyContinue)
    if ($pdbFiles.Count -gt 0) {
        $pdbList = ($pdbFiles | ForEach-Object { $_.FullName }) -join "`n"
        throw "default server bundle must not contain PDB files:`n$pdbList"
    }
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumPath = Join-Path $outputRootPath "$packageName.sha256.txt"
Set-Content -LiteralPath $sumPath -Value "$hash  $packageName.zip" -Encoding ASCII

Write-Host "Wrote $zipPath"
Write-Host "SHA256 $hash"
