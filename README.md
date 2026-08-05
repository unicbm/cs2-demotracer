# DemoTracer

Trace CS2 demos into bot-executable route replays.

**CS2 DemoTracer** is an open-source Windows desktop application and matched
server playback stack. It converts Counter-Strike 2 demo files into compact
.dtr replays, then reproduces movement, view angles, command state, weapons,
projectiles, optional voice, and selected presentation evidence through bots on
a local CS2 server.

[Documentation](docs/README.md) · [Development](docs/DEVELOPMENT.md) ·
[Latest release](https://github.com/unicbm/demotracer/releases/latest)

![CI](https://github.com/unicbm/demotracer/actions/workflows/ci.yml/badge.svg)
![License](https://img.shields.io/badge/license-AGPL--3.0--only-blue)

## Demo

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

- Provides a bilingual Tauri and React workflow for opening demos, inspecting
  rounds, selecting players, converting replays, and maintaining a local
  replay library.
- Writes deterministic .dtr v8 files and ABI 17 manifests through the Rust
  converter linked directly into the desktop application. There is no separate
  converter CLI in the supported 1.x product.
- Replays movement, subtick input, view angles, weapons, projectiles, optional
  demo voice, and demo-backed presentation evidence through the matched
  CounterStrikeSharp and Metamod stack.
- Installs, verifies, repairs, and rolls back local CSS bundles while preserving
  server-local configuration.
- Keeps cosmetic, sticker, charm, agent, and scoreboard alignment explicit,
  demo-backed, and default-off where appropriate.

DemoTracer is local replay tooling for research, content creation, analysis,
and plugin development. It is not intended for matchmaking or cheating.

## Requirements

- Windows 10 or Windows 11 x64.
- Microsoft Edge WebView2 for the desktop application.
- For playback: a local Windows x64 CS2 server with
  [Metamod:Source](https://www.sourcemm.net/) and
  [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp).

The desktop installer does not require Python, Node.js, Rust, or a local build
toolchain. See [Development](docs/DEVELOPMENT.md#dependencies-and-provenance).

## Downloads

Use the artifacts attached to the
[latest official release](https://github.com/unicbm/demotracer/releases/latest):

- `demotracer-gui-vVERSION.exe`: NSIS desktop installer.
- `demotracer-css-vVERSION.zip`: matched CS2 playback plugins.

Only artifacts attached by unicbm to this repository's GitHub Releases are
official DemoTracer builds. See
[Trademark and Official Build Policy](TRADEMARKS.md).

## Source Build

The maintained source target is Windows x64. Install Rust stable, Node.js 22,
pnpm 11.9, .NET 10, and the Tauri Windows prerequisites, then run:

    cd desktop\converter
    cargo test --locked

    cd ..\gui
    pnpm install --frozen-lockfile
    pnpm run check
    pnpm test
    cargo test --manifest-path src-tauri\Cargo.toml --locked

    cd ..\..
    .\tooling\scripts\test-css.ps1
    .\tooling\scripts\check-release-contract.ps1

Native BotController and BotHider builds additionally require the local CS2
Metamod and SDK toolchain. Detailed setup is documented in
[Development](docs/DEVELOPMENT.md).

## Compatibility Contract

The release truth source is
[shared/contracts/playback-contract.v1.json](shared/contracts/playback-contract.v1.json).

| Contract | Supported value |
| --- | --- |
| .dtr writer | v8 |
| .dtr reader | v3-v8 |
| Manifest ABI | 17 |
| BotController ABI | 16, minor 33+ |
| BotHider API | 1 |
| DemoTracer companion API | 7 |

See [.dtr Format Contract](docs/FORMAT.md) for the binary layout and limits.

## Repository Layout

- desktop/gui: Tauri and React application plus the Rust desktop backend.
- desktop/converter: Rust parsing, analysis, synthesis, .dtr writing, manifests,
  and validation.
- server/plugins: CounterStrikeSharp playback orchestration and companion API.
- server/runtime: maintained BotController and BotHider native runtimes.
- shared: versioned compatibility contracts and generated runtime metadata.
- third_party: vendored dependencies, provenance, and license notices.
- tooling: validation, packaging, and release automation.

## Credits and License

DemoTracer builds on
[CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller),
[CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider),
[demoparser](https://github.com/LaihoE/demoparser),
[minidemo-encoder](https://github.com/csgowiki/minidemo-encoder),
Metamod:Source, and CounterStrikeSharp. `minidemo-encoder` provided an early
foundation for reconstructing continuous movement from discrete demo
trajectories.

First-party source is licensed under **AGPL-3.0-only**. Vendored components and
datasets retain their recorded licenses and attribution. The code license does
not grant rights to misrepresent modified builds as official releases.

The Insight interface skin and selected menu and demo-presentation design cues
reference [CS2 Insight Agent](https://github.com/DrEAmSs59/CS2-insight-agent)
under direct, paid, project-specific authorization from DrEAmSs59. The original
reference material remains the property of that project and is not granted to
third parties under DemoTracer's AGPL-3.0-only license. See the maintained
[source and authorization notice](docs/CS2_INSIGHT_GUI_REFERENCE.md).
