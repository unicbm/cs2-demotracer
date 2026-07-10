# CS2 DemoTracer

Trace CS2 demos into bot-executable route replays.

**Language:** English | [简体中文](docs/README.zh-Hans.md)

> [!CAUTION]
> **July 2026 CS2 update (1.41.6.9):** Server playback requires
> CounterStrikeSharp v1.0.371 or newer. Ray-Trace users need v1.0.16 or newer.
> CS2-Bot-Hider users need a build containing the July 2026 Windows client
> identity-offset fix; tagged v0.2.5 predates it. DemoTracer's Windows core
> replay path has been locally verified; the converter and `.dtr` format are
> unaffected.

CS2 DemoTracer converts CS2 `.dem` files into compact `.dtr` replay files, then
plays those routes back through bots on a local CS2 server. The normal converter
path is a packaged Windows x64 CLI/GUI; Python, Node.js, Conda, and game-server
plugins are not required for conversion.

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
- Exports optional Demo2Nade grenade clips for local utility libraries.
- Can align loadout, projectiles, crosshair, scoreboard presentation, and
  demo-backed cosmetics when those modes are explicitly enabled.

This is local replay tooling for research, content creation, and plugin
development. It is not intended for matchmaking or cheating.

## Dependencies

Conversion only needs the release converter package.

In-game playback needs a local Windows x64 CS2 server with:

- [Metamod:Source](https://www.sourcemm.net/)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- The DemoTracer server bundle, which includes the `BotController` Metamod
  runtime, `DemoTracer` CounterStrikeSharp plugin, `DemoTracerApi.dll`, and
  sanitized example config.

Optional integrations:

- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider), for BotHider-managed
  replay slots plus demo display-name, SteamID64, and avatar identity alignment.
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace), or another provider
  exposing `raytrace:craytraceinterface`, for stricter line-of-sight filtering
  in handoff 360 threat detection. DemoTracer works without it and reports the
  status through `dtr_doctor`.

The server bundle does not include Metamod:Source, CounterStrikeSharp,
CS2-Bot-Hider, or a RayTrace provider. Full dependency notes are in
[`docs/DEPENDENCIES.md`](docs/DEPENDENCIES.md).

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
- Server commands: [`docs/COMMANDS.md`](docs/COMMANDS.md)
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
- Current DemoTracer companion API: 5
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
- Server commands: [`docs/COMMANDS.md`](docs/COMMANDS.md)
- Dependencies: [`docs/DEPENDENCIES.md`](docs/DEPENDENCIES.md)
- File format: [`docs/FORMAT.md`](docs/FORMAT.md)
- Limitations: [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md)

## Repository Layout

- `converter/`: Rust CLI, GUI, local Rust API, pool conversion, and Demo2Nade.
- `runtime/BotController/`: CS2 Metamod runtime used by the server bundle.
- `css/DemoTracer/`: CounterStrikeSharp playback plugin.
- `css/DemoTracerApi/`: companion-plugin API contract.
- `docs/`: maintained usage, reference, format, and dependency docs.
- `examples/`: small Python and Node.js CLI integration examples.
- `third_party/`: vendored third-party source and license files.

## Acknowledgements

CS2 DemoTracer builds on several excellent open-source projects.

- [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)
  provides the AGPL-3.0 BotController foundation for replay hooks, recording,
  input injection, and weapon locking.
- [XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) provides the
  integration path used for managed bot detection, display-name alignment, and
  SteamID64 alignment.
- [LaihoE/demoparser](https://github.com/LaihoE/demoparser) provides the Rust
  CS2 demo parser used by the converter. The vendored source is preserved under
  `third_party/demoparser`.
- [csgowiki/minidemo-encoder](https://github.com/csgowiki/minidemo-encoder)
  provided historical `.dem -> replay file` workflow inspiration. No Go source
  from that project is copied.
- [Metamod:Source](https://github.com/alliedmodders/metamod-source) and
  [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) provide
  the runtime/plugin stack.

## License

CS2 DemoTracer is AGPL-3.0-only. See [`LICENSE`](LICENSE) for the full license
text. Vendored third-party components keep their upstream license files under
`third_party/`.
