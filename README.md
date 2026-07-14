# CS2 DemoTracer

Trace CS2 demos into bot-executable route replays.

**Language:** English | [简体中文](docs/README.zh-Hans.md)

> [!CAUTION]
> **July 2026 CS2 update (1.41.6.9):** Server playback requires
> CounterStrikeSharp v1.0.371 or newer. Ray-Trace users need v1.0.16 or newer.
> The playback bundle now carries DemoTracer's maintained BotHider runtime with
> the required Windows identity offsets. DemoTracer's Windows core replay path
> has been locally verified. Demos using the newer delta user-command encoding
> require converter v0.5.0 or newer; the `.dtr` format is unchanged.

CS2 DemoTracer converts CS2 `.dem` files into compact `.dtr` replay files, then
plays those routes back through bots on a local CS2 server. The normal converter
path uses separate packaged Windows x64 CLI and GUI downloads; Python, Node.js,
Conda, and game-server plugins are not required for conversion. The desktop GUI
uses Tauri and requires the Microsoft Edge WebView2 Runtime, which is normally
present on current Windows 10 and Windows 11 installations.

## Demo

First-person spectator view stays synchronized while bots replay converted demo
movement, view angles, firing, weapon state, and projectile alignment.

<table>
  <tr>
    <td align="center" width="50%">
      <img src="docs/media/first-person-replay-nuke.gif" alt="First-person CS2 bot replay on Nuke" width="100%"><br>
      <sub>First-person route replay</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/media/first-person-replay-route.gif" alt="First-person CS2 bot replay through an indoor route" width="100%"><br>
      <sub>Indoor route replay</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="50%">
      <img src="docs/media/mirage-opening-replay.gif" alt="Mirage multi-bot opening replay" width="100%"><br>
      <sub>Mirage multi-bot opening</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/media/mirage-projectile-smokes.gif" alt="Projectile-aligned Mirage smoke replay" width="100%"><br>
      <sub>Projectile-aligned Mirage smokes</sub>
    </td>
  </tr>
</table>

## What It Does

- Converts CS2 match demos into `.dtr` route replay files, one player/side/round
  at a time.
- Optionally exports demo-backed in-game voice sidecars under `voice/roundXX.dtv`
  when the source demo contains usable voice data.
- Replays demo movement, view angles, crouch/jump state, firing, weapon switching,
  and selected high-fidelity metadata through local CS2 bots.
- Can align loadout, projectiles, crosshair, scoreboard presentation, and
  demo-backed cosmetics when those modes are explicitly enabled.

This is local replay tooling for research, content creation, and plugin
development. It is not intended for matchmaking or cheating.

## Requirements

- **Conversion:** either packaged Windows x64 converter download. No game-server
  plugins are required.
- **GUI runtime:** Microsoft Edge WebView2, normally included with current
  Windows 10 and Windows 11 installations.
- **Playback:** a local Windows x64 CS2 server with
  [Metamod:Source](https://www.sourcemm.net/),
  [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp), and the
  DemoTracer playback bundle.
- **Optional:** [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace), or a
  compatible provider, for stricter handoff line-of-sight filtering.

The playback bundle supplies DemoTracer's runtime and plugins, but not
Metamod:Source, CounterStrikeSharp, or a RayTrace provider. See
[`docs/DEPENDENCIES.md`](docs/DEPENDENCIES.md) for versions, bundled components,
optional integrations, and compatibility boundaries.

## Downloads

Choose only what you need from the
[latest GitHub release](https://github.com/unicbm/cs2-demotracer/releases/latest):

- `cs2-demotracer-cli-v<version>-windows-x64.zip`: the smallest converter
  download for CLI, wizard, batch, and pool workflows.
- `cs2-demotracer-gui-v<version>-windows-x64.zip`: the Tauri single-demo desktop
  converter.
- `cs2-demotracer-playback-v<version>-windows-x64.zip`: the server-side
  CounterStrikeSharp/Metamod plugins and runtimes for replaying `.dtr` files on
  a local Windows x64 CS2 server.

The playback bundle is not a hosted or cloud service. Install it only on the
local CS2 server where converted routes should be replayed. CLI and GUI users
who only convert demos do not need it.

## Quick Start

Convert and validate a demo:

```powershell
cs2-demotracer.exe inspect --demo "<demo.dem>"
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>"
cs2-demotracer.exe validate --input "<output-dir>"
```

To export demo-backed in-game voice for automatic replay, add `--export-voice`:

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --export-voice
```

This writes `voice/roundXX.dtv` sidecars next to the converted round replay
files. Not every demo contains voice; community, FACEIT, and 5E demos are more
likely to include it. See [`docs/VOICE.md`](docs/VOICE.md).

Play a converted manifest on a local server:

```text
css_plugins reload DemoTracer
dtr_config_status
dtr_voice_auto on
dtr_go seq "<output-dir>\<demo-id>\manifest.json" 0
```

`seq` starts a sequence from a source round. Use
`dtr_go round "<manifest.json>" 0` for a single source round.

More commands:

- Converter usage: [`docs/USAGE.md`](docs/USAGE.md)
- Voice export and replay: [`docs/VOICE.md`](docs/VOICE.md)
- Playback commands: [`docs/COMMANDS.md`](docs/COMMANDS.md)
- Examples: [`examples/`](examples/)

## Cosmetic Alignment and GSLT Safety

> [!IMPORTANT]
> Cosmetic, custom-name, sticker, charm, and agent model metadata are never exported by
> default. Normal `convert` output is the recommended safe path.
>
> Export that metadata only when you intentionally pass `--export-cosmetics`,
> `--acknowledge-cosmetic-gslt-risk`, and
> `--accept-cosmetic-export-disclaimer`; stickers also require
> `--export-stickers`, and charms also require `--export-charms`. Runtime
> cosmetic, agent, sticker, and charm alignment are also default-off and consume
> only demo evidence from the manifest.
> When `cosmetics.agent` evidence exists and agent alignment is enabled,
> DemoTracer changes that safe replay bot slot to the demo-backed agent model.
>
> This feature is for local/private replay fidelity. Bot-only inventory mutation
> is not a Valve policy exemption if humans can observe, control, possess,
> inspect, or otherwise use bots with simulated items. On dedicated, community,
> or public servers, treat cosmetic/inventory simulation as operator-risk under
> Valve's [Game Server Operation Guidelines](https://blog.counter-strike.net/server_guidelines/)
> and Steam [game server account](https://steamcommunity.com/dev/managegameservers)
> rules.

## `.dtr` Format Contract

`.dtr` is the native replay file consumed by DemoTracer's CounterStrikeSharp
loader and BotController runtime. Detailed binary layout is documented in
[`docs/FORMAT.md`](docs/FORMAT.md).

- Magic: `CSDTRREC`
- Current writer format: `.dtr` v7
- Runtime reader support: v3 through v7
- Current manifest ABI: 17
- Current BotController native ABI: 16
- Current DemoTracer companion API: 6
- Endianness: little-endian
- Current v7 layout: section container with required movement snapshot, tick
  metadata, and subtick sections; optional projectile, high-fidelity metadata,
  command-frame, and movement-extra sections.

The format is lossless for stored replay evidence: movement snapshots,
projectile events, high-fidelity metadata, subtick records, and command-frame
data retain their original `f32`, integer, or UTF-8 JSON values. Detailed binary
layout is documented in [`docs/FORMAT.md`](docs/FORMAT.md).

## Documentation

- Docs index: [`docs/README.md`](docs/README.md)
- Usage: [`docs/USAGE.md`](docs/USAGE.md)
- Playback commands: [`docs/COMMANDS.md`](docs/COMMANDS.md)
- Dependencies: [`docs/DEPENDENCIES.md`](docs/DEPENDENCIES.md)
- File format: [`docs/FORMAT.md`](docs/FORMAT.md)
- Limitations: [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md)

## Repository Layout

- `converter/`: Rust conversion core, CLI, local Rust API, and pool conversion.
- `desktop/`: Tauri/React single-demo desktop GUI.
- `runtime/BotController/`: CS2 Metamod runtime used by the playback bundle.
- `runtime/BotHider/`: DemoTracer-maintained BotHider native/CSS runtime and
  versioned presentation-lease API.
- `css/DemoTracer/`: CounterStrikeSharp playback plugin.
- `css/DemoTracerApi/`: companion-plugin API contract.
- `docs/`: maintained usage, reference, format, and dependency docs.
- `examples/`: small Python and Node.js CLI integration examples.
- `third_party/`: vendored third-party source and license files.

## Credits and License

CS2 DemoTracer builds on
[CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller),
[CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider),
[demoparser](https://github.com/LaihoE/demoparser), Metamod:Source, and
CounterStrikeSharp. [minidemo-encoder](https://github.com/csgowiki/minidemo-encoder)
provided historical workflow inspiration.

The project is AGPL-3.0-only; see [`LICENSE`](LICENSE). Vendored components keep
their upstream licenses and attribution under `third_party/` and
`runtime/BotHider/`.
