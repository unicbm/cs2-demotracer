# Dependencies

This document separates required dependencies, bundled components, and optional
runtime integrations.

## Converter

Normal conversion uses the packaged Windows x64 converter zip:

- `cs2-demotracer.exe`: CLI for inspect, convert, validate, pool conversion,
  and wizard workflows.
- `cs2-demotracer-gui.exe`: native single-demo GUI workbench.

Python, Node.js, Conda, virtualenvs, and CS2 server plugins are not required for
normal conversion. Rust is only required when building from source.

## In-Game Playback

Playback needs a local Windows x64 CS2 server with:

- [Metamod:Source](https://www.sourcemm.net/)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) v1.0.371
  or newer for the July 2026 CS2 1.41.6.9 update
- The DemoTracer server bundle

The server bundle includes:

- `BotController`: the DemoTracer Metamod runtime
- `DemoTracer`: the CounterStrikeSharp playback plugin
- `DemoTracerApi.dll`: companion-plugin API contract
- `demotracer-econ-index.v1.json`
- `demotracer.config.example.json`

The server bundle does not include Metamod:Source, CounterStrikeSharp,
CS2-Bot-Hider, or a RayTrace provider.

## Optional Integrations

### CS2-Bot-Hider

[CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) is optional.

For the July 2026 CS2 update, use a build containing the Windows client
identity-offset fix. Tagged v0.2.5 predates that fix.

DemoTracer does not vendor BotHider and does not compile against a BotHider DLL.
At runtime it probes BotHider-managed bot slots through BotHider's shared slot
state, then uses BotHider console commands for identity presentation:

- `bh_setname` for demo display names
- `bh_setsid` for demo or synthetic DTR SteamID64 values

Without BotHider, movement replay, weapon/loadout alignment, projectile
alignment, handoff, and cosmetic alignment still work on safe replay bots.
Identity features that need visible bot name or SteamID64 changes are skipped.

Avatar override behavior is split:

- BotHider supplies the visible SteamID64/name presentation path.
- DemoTracer's native runtime writes CS2 `ServerAvatarOverrides`.
- `dtr_replay_identity avatar` uses synthetic DTR SteamID64 keys to avoid real
  SteamID64 avatar collisions.

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
runtime. The CounterStrikeSharp plugin currently targets `net8.0` and talks to
that runtime through a C ABI / P/Invoke layer.

The source project deliberately keeps its compile-time CounterStrikeSharp API
reference separate from the required server runtime release so it can continue
to target `net8.0`. Changing that target is a compatibility migration, not a
release-package dependency bump.

Upstream `XBribo/CS2-Bot-Controller` also has a newer C# shared capability path
whose projects may target newer .NET versions. That upstream API is not bundled
or required by the current DemoTracer server bundle. Migrating to it should be a
deliberate compatibility change, not an incidental package update.

Current release compatibility:

- `.dtr` writer: v7
- `.dtr` reader: v3 through v7
- Manifest ABI: 17
- BotController native ABI: 16
- DemoTracer companion API: 6

Companion API 6 is intentionally narrow: bot ownership/busy-state queries,
demo-backed cosmetic state, and HUD crosshair override control. It does not
expose standalone replay-library or utility-clip orchestration.

## Source Builds

Source builds use the repo-local project files:

- Converter: Rust/Cargo under `converter/`
- CounterStrikeSharp plugin: `.NET SDK` compatible with `net8.0`
- Native BotController runtime: local CS2 Metamod/SDK/CMake toolchain

Release packaging should reuse an already staged BotController runtime unless
native runtime source changed and the native toolchain is intentionally
configured.
