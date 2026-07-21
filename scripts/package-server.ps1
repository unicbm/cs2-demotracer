param(
    [string]$Version = "0.8.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$RuntimePackage = "runtime\BotController\build\package",
    [string]$RuntimeBuild = "runtime\BotController\build",
    [string]$BotHiderRuntimePackage = "runtime\BotHider\build\package",
    [string]$BotHiderRuntimeBuild = "runtime\BotHider\build",
    [string]$DotnetPath = "",
    [switch]$BuildRuntime,
    [switch]$BuildBotHiderRuntime,
    [switch]$SkipCssBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$packageName = "cs2-demotracer-playback-v$Version-windows-x64"
$stageRoot = Join-Path $outputRootPath $packageName
$zipPath = Join-Path $outputRootPath "$packageName.zip"
$runtimeRoot = if ([System.IO.Path]::IsPathRooted($RuntimePackage)) {
    $RuntimePackage
} else {
    Join-Path $repoRoot $RuntimePackage
}
$botHiderRuntimeRoot = if ([System.IO.Path]::IsPathRooted($BotHiderRuntimePackage)) {
    $BotHiderRuntimePackage
} else {
    Join-Path $repoRoot $BotHiderRuntimePackage
}
$cssOut = Join-Path $repoRoot "css\DemoTracer\bin\$Configuration\net10.0"
$apiOut = Join-Path $repoRoot "css\DemoTracerApi\bin\$Configuration\net10.0"
$botHiderCssOut = Join-Path $repoRoot "runtime\BotHider\csharp\BotHiderImpl\bin\$Configuration\net10.0"
$botHiderApiOut = Join-Path $repoRoot "runtime\BotHider\csharp\BotHiderApi\bin\$Configuration\net10.0"
$playbackContractPath = Join-Path $repoRoot "compatibility\playback-contract.v1.json"

function Require-Path([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
}

function Test-SameFullPath([string]$Left, [string]$Right) {
    $leftPath = [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/')
    $rightPath = [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/')
    return [System.StringComparer]::OrdinalIgnoreCase.Equals($leftPath, $rightPath)
}

function Assert-ExternalRuntimeReceipt(
    [string]$Root,
    [string[]]$RequiredPaths,
    [string]$Component,
    [string]$Label,
    [object]$ExpectedContract
) {
    $receiptPath = Join-Path $Root "addons\demotracer-install.v1.json"
    Require-Path $receiptPath "$Label source receipt"
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.schema_version -ne 1 -or
        $receipt.product -ne $ExpectedContract.product -or
        $receipt.platform -ne $ExpectedContract.platform -or
        $receipt.compatibility.bot_controller.abi_major -ne $ExpectedContract.bot_controller.abi_major -or
        $receipt.compatibility.bot_controller.min_abi_minor -lt $ExpectedContract.bot_controller.min_abi_minor -or
        $receipt.compatibility.bot_controller.required_capabilities_hex -ne $ExpectedContract.bot_controller.required_capabilities_hex -or
        $receipt.compatibility.bot_hider.api -ne $ExpectedContract.bot_hider.api -or
        $receipt.compatibility.demotracer.companion_api -ne $ExpectedContract.demotracer.companion_api) {
        throw "$Label source receipt does not match the current DemoTracer playback contract: $receiptPath"
    }

    $entries = @($receipt.files | Where-Object { $_.component -eq $Component })
    if ($entries.Count -eq 0) {
        throw "$Label source receipt does not contain component $Component"
    }
    foreach ($requiredPath in $RequiredPaths) {
        $receiptRequiredPath = $requiredPath.Replace('\', '/')
        if (-not ($entries | Where-Object {
            [System.StringComparer]::OrdinalIgnoreCase.Equals([string]$_.path, $receiptRequiredPath)
        } | Select-Object -First 1)) {
            throw "$Label source receipt does not contain required file $receiptRequiredPath"
        }
    }

    $seenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        $relativePath = ([string]$entry.path).Replace('\', '/')
        $segments = @($relativePath.Split('/'))
        $unsafeSegment = $segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains(':')
        } | Select-Object -First 1
        if ($segments.Count -lt 2 -or
            $segments[0] -ne 'addons' -or
            $null -ne $unsafeSegment) {
            throw "$Label source receipt contains an unsafe path: $relativePath"
        }
        if (-not $seenPaths.Add($relativePath)) {
            throw "$Label source receipt contains a duplicate path: $relativePath"
        }
        $filePath = Join-Path $Root $relativePath.Replace('/', '\')
        Require-Path $filePath "$Label receipt file"
        $actual = Get-Item -LiteralPath $filePath
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actual.Length -ne [long]$entry.size -or
            -not [System.StringComparer]::OrdinalIgnoreCase.Equals($actualHash, [string]$entry.sha256)) {
            throw "$Label source component no longer matches its DemoTracer receipt: $filePath"
        }
    }
}

function Assert-BinaryContainsExport([string]$Path, [string]$ExportName) {
    Require-Path $Path "native runtime DLL"
    $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Path))
    if ($ascii.IndexOf($ExportName + [char]0, [System.StringComparison]::Ordinal) -lt 0) {
        throw "BotController runtime is older than the packaged ABI metadata (missing export $ExportName): $Path. Rebuild it with -BuildRuntime or pass a current runtime package."
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
        return $null -ne ($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)
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
    throw "dotnet 10 SDK not found. Install .NET 10 SDK or pass -DotnetPath to a dotnet.exe that provides it."
}

if ($BuildRuntime) {
    Invoke-Checked "cmake" @("--build", (Join-Path $repoRoot $RuntimeBuild), "--config", $Configuration, "--target", "BotController")
}

if ($BuildBotHiderRuntime) {
    Invoke-Checked "cmake" @("--build", (Join-Path $repoRoot $BotHiderRuntimeBuild), "--config", $Configuration, "--target", "BotHider")
}

if (-not $SkipCssBuild) {
    $resolvedDotnetPath = Resolve-DotnetPath $DotnetPath
    Invoke-Checked $resolvedDotnetPath @("build", (Join-Path $repoRoot "css\DemoTracer\DemoTracer.csproj"), "-c", $Configuration, "-m:1")
    Invoke-Checked $resolvedDotnetPath @("build", (Join-Path $repoRoot "runtime\BotHider\csharp\BotHiderImpl\BotHiderImpl.csproj"), "-c", $Configuration, "-m:1")
}

Require-Path $playbackContractPath "playback compatibility contract"
$playbackContract = Get-Content -LiteralPath $playbackContractPath -Raw -Encoding UTF8 | ConvertFrom-Json
$defaultRuntimeRoot = Join-Path $repoRoot "runtime\BotController\build\package"
$defaultBotHiderRuntimeRoot = Join-Path $repoRoot "runtime\BotHider\build\package"
if (-not (Test-SameFullPath $runtimeRoot $defaultRuntimeRoot)) {
    Assert-ExternalRuntimeReceipt `
        -Root $runtimeRoot `
        -RequiredPaths @(
            "addons\BotController\bin\win64\BotController.dll",
            "addons\BotController\gamedata.json",
            "addons\metamod\BotController.vdf"
        ) `
        -Component "bot_controller" `
        -Label "BotController" `
        -ExpectedContract $playbackContract
}
if (-not (Test-SameFullPath $botHiderRuntimeRoot $defaultBotHiderRuntimeRoot)) {
    Assert-ExternalRuntimeReceipt `
        -Root $botHiderRuntimeRoot `
        -RequiredPaths @(
            "addons\BotHider\bin\win64\BotHider.dll",
            "addons\BotHider\gamedata.json",
            "addons\BotHider\map_whitelist.json",
            "addons\BotHider\bot_info.example.json",
            "addons\metamod\BotHider.vdf"
        ) `
        -Component "bot_hider_native" `
        -Label "BotHider" `
        -ExpectedContract $playbackContract
}

$runtimeDll = Join-Path $runtimeRoot "addons\BotController\bin\win64\BotController.dll"
Require-Path $runtimeDll "BotController runtime DLL"
Assert-BinaryContainsExport $runtimeDll "BotController_GetAbiInfo"
Assert-BinaryContainsExport $runtimeDll "BotController_GetCapabilities"
Assert-BinaryContainsExport $runtimeDll "BotController_GetBuildId"
Assert-BinaryContainsExport $runtimeDll "BotController_ReleaseReplayBuffer"
Require-Path (Join-Path $runtimeRoot "addons\BotController\gamedata.json") "BotController gamedata"
Require-Path (Join-Path $runtimeRoot "addons\metamod\BotController.vdf") "BotController Metamod VDF"
Require-Path (Join-Path $botHiderRuntimeRoot "addons\BotHider\bin\win64\BotHider.dll") "DemoTracer BotHider runtime DLL"
Require-Path (Join-Path $botHiderRuntimeRoot "addons\BotHider\gamedata.json") "DemoTracer BotHider gamedata"
Require-Path (Join-Path $botHiderRuntimeRoot "addons\metamod\BotHider.vdf") "DemoTracer BotHider Metamod VDF"
Require-Path (Join-Path $cssOut "DemoTracer.dll") "DemoTracer CSS plugin"
Require-Path (Join-Path $apiOut "DemoTracerApi.dll") "DemoTracer API assembly"
Require-Path (Join-Path $botHiderCssOut "DemoTracerBotHider.dll") "DemoTracer BotHider CSS plugin"
Require-Path (Join-Path $botHiderApiOut "DemoTracerBotHiderApi.dll") "DemoTracer BotHider API assembly"

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$addonsOut = Join-Path $stageRoot "addons"
New-Item -ItemType Directory -Force -Path $addonsOut | Out-Null
$botControllerOut = Join-Path $addonsOut "BotController"
Copy-RequiredFile (Join-Path $runtimeRoot "addons\BotController\bin\win64\BotController.dll") `
    (Join-Path $botControllerOut "bin\win64\BotController.dll")
Copy-RequiredFile (Join-Path $runtimeRoot "addons\BotController\gamedata.json") `
    (Join-Path $botControllerOut "gamedata.json")
Copy-RequiredFile (Join-Path $runtimeRoot "addons\metamod\BotController.vdf") `
    (Join-Path $addonsOut "metamod\BotController.vdf")
$botHiderOut = Join-Path $addonsOut "BotHider"
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\BotHider\bin\win64\BotHider.dll") `
    (Join-Path $botHiderOut "bin\win64\BotHider.dll")
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\BotHider\gamedata.json") `
    (Join-Path $botHiderOut "gamedata.json")
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\BotHider\map_whitelist.json") `
    (Join-Path $botHiderOut "map_whitelist.json")
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\BotHider\bot_info.example.json") `
    (Join-Path $botHiderOut "bot_info.example.json")
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\metamod\BotHider.vdf") `
    (Join-Path $addonsOut "metamod\BotHider.vdf")

$pluginOut = Join-Path $stageRoot "addons\counterstrikesharp\plugins\DemoTracer"
Copy-RequiredFile (Join-Path $cssOut "DemoTracer.deps.json") (Join-Path $pluginOut "DemoTracer.deps.json")
Copy-RequiredFile (Join-Path $cssOut "DemoTracer.dll") (Join-Path $pluginOut "DemoTracer.dll")
Copy-RequiredFile (Join-Path $cssOut "demotracer-econ-index.v1.json") (Join-Path $pluginOut "demotracer-econ-index.v1.json")
Copy-RequiredFile (Join-Path $repoRoot "css\DemoTracer\demotracer.config.example.json") (Join-Path $pluginOut "demotracer.config.example.json")
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.deps.json") (Join-Path $pluginOut "DemoTracerApi.deps.json")
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.dll") (Join-Path $pluginOut "DemoTracerApi.dll")

$botHiderPluginOut = Join-Path $stageRoot "addons\counterstrikesharp\plugins\DemoTracerBotHider"
Copy-RequiredFile (Join-Path $botHiderCssOut "DemoTracerBotHider.deps.json") (Join-Path $botHiderPluginOut "DemoTracerBotHider.deps.json")
Copy-RequiredFile (Join-Path $botHiderCssOut "DemoTracerBotHider.dll") (Join-Path $botHiderPluginOut "DemoTracerBotHider.dll")
$botHiderSharedOut = Join-Path $stageRoot "addons\counterstrikesharp\shared\DemoTracerBotHiderApi"
Copy-RequiredFile (Join-Path $botHiderApiOut "DemoTracerBotHiderApi.dll") (Join-Path $botHiderSharedOut "DemoTracerBotHiderApi.dll")
$harmonySource = Join-Path $botHiderCssOut "shared\0Harmony\0Harmony.dll"
Copy-RequiredFile $harmonySource (Join-Path $stageRoot "addons\counterstrikesharp\shared\0Harmony\0Harmony.dll")
if ($IncludeSymbols) {
    Copy-RequiredFile (Join-Path $cssOut "DemoTracer.pdb") (Join-Path $pluginOut "DemoTracer.pdb")
    Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.pdb") (Join-Path $pluginOut "DemoTracerApi.pdb")
    Copy-RequiredFile (Join-Path $botHiderCssOut "DemoTracerBotHider.pdb") (Join-Path $botHiderPluginOut "DemoTracerBotHider.pdb")
    Copy-RequiredFile (Join-Path $botHiderApiOut "DemoTracerBotHiderApi.pdb") (Join-Path $botHiderSharedOut "DemoTracerBotHiderApi.pdb")
}

New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot "docs") | Out-Null
Copy-RequiredFile (Join-Path $repoRoot "docs\COMMANDS.md") (Join-Path $stageRoot "docs\COMMANDS.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\COMMANDS.zh-Hans.md") (Join-Path $stageRoot "docs\COMMANDS.zh-Hans.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\VOICE.md") (Join-Path $stageRoot "docs\VOICE.md")
Copy-RequiredFile (Join-Path $repoRoot "docs\VOICE.zh-Hans.md") (Join-Path $stageRoot "docs\VOICE.zh-Hans.md")
Copy-RequiredFile (Join-Path $repoRoot "runtime\BotHider\UPSTREAM.md") (Join-Path $stageRoot "docs\BOTHIDER-UPSTREAM.md")
Copy-RequiredFile (Join-Path $repoRoot "LICENSE") (Join-Path $stageRoot "LICENSE")

$gitCommit = "unknown"
try {
    $gitCommit = (git -C $repoRoot rev-parse --short=12 HEAD).Trim()
} catch {
}

$receiptFiles = @(
    Get-ChildItem -LiteralPath $addonsOut -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($stageRoot, $_.FullName).Replace('\', '/')
            $component = if ($relativePath -like "addons/BotController/*" -or $relativePath -eq "addons/metamod/BotController.vdf") {
                "bot_controller"
            } elseif ($relativePath -like "addons/BotHider/*" -or $relativePath -eq "addons/metamod/BotHider.vdf") {
                "bot_hider_native"
            } elseif ($relativePath -like "addons/counterstrikesharp/plugins/DemoTracerBotHider/*" -or
                      $relativePath -like "addons/counterstrikesharp/shared/DemoTracerBotHiderApi/*") {
                "bot_hider_managed"
            } elseif ($relativePath -like "addons/counterstrikesharp/plugins/DemoTracer/*") {
                "demotracer"
            } else {
                "shared_dependency"
            }
            [ordered]@{
                path = $relativePath
                component = $component
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$installReceipt = [ordered]@{
    schema_version = 1
    product = "CS2 DemoTracer Playback Bundle"
    bundle_version = $Version
    git_commit = $gitCommit
    platform = "windows-x64"
    compatibility = $playbackContract
    files = $receiptFiles
}
$installReceiptPath = Join-Path $addonsOut "demotracer-install.v1.json"
$installReceipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $installReceiptPath -Encoding UTF8

$versionText = @"
CS2 DemoTracer Playback Bundle
version: v$Version
git_commit: $gitCommit
platform: windows-x64
bundled_botcontroller_abi: 16
bundled_botcontroller_abi_minor: 31
expected_demotracer_native_abi: 16
dtr_reader: $($playbackContract.dtr_reader.min)..$($playbackContract.dtr_reader.max)
demotracer_api: 6
demotracer_bothider_api: 1
counterstrikesharp_target: net10.0

Install target:
Copy this package's addons directory into your CS2 server game/csgo directory.
"@
Set-Content -LiteralPath (Join-Path $stageRoot "VERSION.txt") -Value $versionText -Encoding UTF8

$readme = @'
# CS2 DemoTracer Playback Bundle v__VERSION__

This is the complete Windows x64 local playback package. Install it into the CS2
server used for replay; it is not a hosted or cloud service. The bundle includes
the Metamod BotController and BotHider runtimes plus their CounterStrikeSharp
plugins as one matching runtime set.

## Install

1. Stop the CS2 server.
2. Make sure the server already has Metamod:Source and CounterStrikeSharp
   v1.0.371 or newer installed. They are prerequisites and are not included in
   this bundle.
3. Remove separately installed public `BotHiderImpl` CounterStrikeSharp plugin
   directories. The bundled `DemoTracerBotHider` is the only supported
   presentation writer and must not run beside another BotHider CSS plugin.
4. Do not merge BotController, BotHider, `BotControllerImpl`, or `BotHiderImpl`
   from a full CS2-Bot-Improver package. Those builds use overlapping paths but
   are not the same vendor contract. For post-handoff AI, keep this bundle's
   native set and add only a compatible behavior-only integration.
5. Copy this package's `addons` directory into the server `game/csgo` directory
   so it merges with the existing `addons` directory.
6. Start the server.
7. In the server console, run:

```text
dtr_runtime
bc_status
bh_status
```

Expected ABI check:

```text
expected_abi=16 runtime_abi=16 abi_minor=31
```

For v__VERSION__, require `runtime_abi=16` and `abi_minor=31` or newer. If the minor
version is missing or lower, replace the complete playback bundle, including
`addons/BotController/bin/win64/BotController.dll`,
`addons/BotController/gamedata.json`, and `addons/metamod/BotController.vdf`.

## Desktop Playback Presets

The desktop GUI generates a compact command such as:

```text
dtr_preset 0x15; dtr_go seq "<manifest.json>" 0
```

`dtr_preset status` prints the effective v1 mask and bit assignments. Presets
from the current desktop GUI require a playback bundle that includes this
command.

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
- `addons/BotHider/bin/win64/BotHider.dll`
- `addons/BotHider/gamedata.json`
- `addons/BotHider/bot_info.example.json` (does not overwrite local `bot_info.json`)
- `addons/metamod/BotHider.vdf`
- `addons/demotracer-install.v1.json` (component contract and file hashes for desktop diagnostics)
- `addons/counterstrikesharp/plugins/DemoTracerBotHider/`
- `addons/counterstrikesharp/shared/DemoTracerBotHiderApi/`
- `addons/counterstrikesharp/plugins/DemoTracer/`
  - `demotracer.config.example.json` sanitized local runtime defaults
  - `demotracer-econ-index.v1.json` compact econ validation index
  - `demotracer-runtime.v1.json` is created at runtime as a short-lived local
    health heartbeat; it is not prepackaged
- `docs/COMMANDS.md`
- `docs/COMMANDS.zh-Hans.md`
- `docs/VOICE.md`
- `docs/VOICE.zh-Hans.md`
- `docs/BOTHIDER-UPSTREAM.md`

## Compatibility

- Required BotController native ABI: 16
- Bundled BotController native ABI: 16
- Bundled BotController native ABI minor: 31
- Supported `.dtr` reader versions: __DTR_READER_MIN__..__DTR_READER_MAX__
- DemoTracer companion API: 6
- DemoTracer BotHider API: 1
- CounterStrikeSharp plugin target: .NET 10
- Maintained runtime platform: Windows x64

## Dependencies

Required external server prerequisites:

- Metamod:Source
- CounterStrikeSharp v1.0.371 or newer for CS2 1.41.6.9

Included in this bundle:

- `BotController` Metamod runtime
- `BotHider` Metamod runtime maintained by DemoTracer
- `DemoTracer` CounterStrikeSharp plugin
- `DemoTracerBotHider` CounterStrikeSharp plugin
- `DemoTracerBotHiderApi.dll`
- `DemoTracerApi.dll`
- `demotracer-econ-index.v1.json`
- `demotracer.config.example.json`

Do not install a second public CS2-Bot-Hider CSS plugin beside the bundled
`DemoTracerBotHider`. Identity and crosshair presentation use exclusive,
versioned leases and require one publisher.

Do not infer BotController/BotHider compatibility from directory names or VDF
targets. Known CS2-Bot-Improver v1.4.2 native packages use BotController ABI 14;
DemoTracer requires ABI 16/minor 31. The desktop environment inspection reads
`addons/demotracer-install.v1.json` and exact component hashes to detect a mixed
or replaced vendor set.

Optional:

- Ray-Trace v1.0.16 or newer, only for stricter line-of-sight filtering in
  handoff 360 threat detection.
- Demo-backed agent model evidence can change the matching safe replay bot slot
  to the demo agent model when `dtr_cosmetics agents` is enabled.
'@
$readme = $readme.Replace("__VERSION__", $Version)
$readme = $readme.Replace("__DTR_READER_MIN__", [string]$playbackContract.dtr_reader.min)
$readme = $readme.Replace("__DTR_READER_MAX__", [string]$playbackContract.dtr_reader.max)
Set-Content -LiteralPath (Join-Path $stageRoot "README.md") -Value $readme -Encoding UTF8

if (-not $IncludeSymbols) {
    $pdbFiles = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -Filter "*.pdb" -File -ErrorAction SilentlyContinue)
    if ($pdbFiles.Count -gt 0) {
        $pdbList = ($pdbFiles | ForEach-Object { $_.FullName }) -join "`n"
        throw "default playback bundle must not contain PDB files:`n$pdbList"
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
