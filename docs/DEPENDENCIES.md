# Dependencies

This document separates required dependencies, bundled components, and optional
runtime integrations.

## Converter

Normal conversion uses one of the separate packaged Windows x64 downloads:

- `cs2-demotracer-cli-v<version>-windows-x64.zip`: `cs2-demotracer.exe` for
  inspect, convert, validate, pool conversion, and wizard workflows.
- `cs2-demotracer-gui-v<version>-windows-x64.zip`:
  `cs2-demotracer-gui.exe`, the Tauri single-demo desktop workbench.

Python, Node.js, Conda, virtualenvs, and CS2 server plugins are not required for
normal conversion with the packaged downloads. The GUI uses the Microsoft Edge
WebView2 Runtime normally included with current Windows 10 and Windows 11
installations; install a current WebView2 Runtime if it is absent. Rust is
required to build the converter core or CLI from source; building the desktop
GUI also requires Node.js/npm and the Windows Tauri build prerequisites.

The converter's self-contained CS2 inspect-preview encoder is a Rust port of
the payload layout and checksum behavior documented by
[`ianlucas/cs2-lib-inspect`](https://github.com/ianlucas/cs2-lib-inspect), used
under its MIT license. The converter does not ship the npm package or require
Node.js, a Steam/GC lookup, or a third-party inspect API at runtime.

## In-Game Playback

Playback needs a local Windows x64 CS2 server with:

- [Metamod:Source](https://www.sourcemm.net/)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) v1.0.371
  or newer for the July 2026 CS2 1.41.6.9 update
- The DemoTracer playback bundle

The playback bundle includes:

- `BotController`: the DemoTracer Metamod runtime
- `BotHider`: the DemoTracer-maintained fake-client/identity Metamod runtime
- `DemoTracer`: the CounterStrikeSharp playback plugin
- `DemoTracerBotHider`: the sole bot presentation publisher
- `DemoTracerApi.dll`: companion-plugin API contract
- `DemoTracerBotHiderApi.dll`: versioned presentation-lease contract
- `demotracer-econ-index.v1.json`
- `demotracer.config.example.json`

All bundled CounterStrikeSharp projects target .NET 10 and compile against
CounterStrikeSharp.API 1.0.371. The playback bundle does not include
Metamod:Source, CounterStrikeSharp itself, or a RayTrace provider.

## Bundled BotHider Boundary

DemoTracer maintains its BotHider source under `runtime/BotHider`, based on
[XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider). Upstream
changes are imported selectively rather than merged mechanically.

The shared-memory mapping is private transport between the bundled native and
C# BotHider halves. DemoTracer consumes only the versioned
`demotracer:bot-hider:v1` capability. Temporary name, SteamID64, scoreboard
flair, and server-replicated crosshair values are one all-or-none presentation
lease with slot incarnation checks, exact release tokens, heartbeat expiry,
and provider/map epochs. DemoTracer retains the most recent successful DTR
presentation batch independently of active playback and replay-buffer cleanup,
so handoff, sequence completion, later rounds, and match end cannot expose the
underlying persona. A successful round replacement uses one batch replace
instead of a release/reacquire gap; a failed partial load keeps the prior batch.

BotHider continuously reconciles the native client identity and the
CounterStrikeSharp controller fields. Exact SteamID batches are validated for
duplicates and live-slot conflicts; the provider fails the batch rather than
substituting another `bot_info` identity.

Ordinary BotHider persona flair remains server-local evidence in
`bot_info.json`. Missing values remain empty; the runtime does not infer or
randomize fallback medals.

Do not co-install a separate public BotHider CounterStrikeSharp plugin. Multiple
presentation publishers can overwrite each other and are unsupported.

Avatar behavior remains split: the bundled BotHider publishes the leased
SteamID64/name, while BotController writes `ServerAvatarOverrides` for validated
manifest PNGs. Missing or invalid PNG evidence falls back to the Steam avatar.

## Optional Integrations

### RayTrace API Provider

[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace), or another RayTrace
provider, is optional. DemoTracer looks for a CounterStrikeSharp capability
named `raytrace:craytraceinterface` and `RayTraceApi` types at runtime. There is
no compile-time reference.

For the July 2026 CS2 update, use Ray-Trace v1.0.16 or newer.

When available, RayTrace is used for stricter line-of-sight filtering in
handoff 360 threat detection. Without it, DemoTracer keeps working and falls
back to a conservative "do not block handoff on missing raytrace" path. Use
`dtr_doctor` to see the current RayTrace status.

## BotController Boundary

This repository bundles its own DemoTracer-compatible BotController native
runtime. The CounterStrikeSharp plugins target `net10.0` and DemoTracer talks to
that runtime through a C ABI / P/Invoke layer.

Upstream `XBribo/CS2-Bot-Controller` also has a newer C# shared capability path
whose projects may target newer .NET versions. That upstream API is not bundled
or required by the current DemoTracer playback bundle. Migrating to it should be a
deliberate compatibility change, not an incidental package update.

Current release compatibility:

- `.dtr` writer: v7
- `.dtr` reader: v3 through v7
- Manifest ABI: 17
- BotController native ABI: 16
- DemoTracer companion API: 6

Companion API 6 is intentionally narrow: bot ownership/busy-state queries,
demo-backed cosmetic state, and server-published bot crosshair override control. It does not
expose standalone replay-library or utility-clip orchestration.

## Source Builds

Source builds use the repo-local project files:

- Converter core and CLI: Rust/Cargo under `converter/`
- Desktop GUI: Tauri/React under `desktop/`; use `npm.cmd ci`,
  `npm.cmd run check`, and `npm.cmd run tauri:build -- --target
  x86_64-pc-windows-msvc -- --locked`
- CounterStrikeSharp plugins: .NET 10 SDK
- Native BotController and BotHider runtimes: local CS2 Metamod/SDK/CMake toolchain

Release packaging should reuse staged BotController and BotHider runtimes unless
native source changed and the native toolchain is intentionally configured.
