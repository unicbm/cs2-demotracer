# CS2 DemoTracer

Trace CS2 demos into bot-executable route replays.

**Language:** English | [简体中文](docs/README.zh-Hans.md)

Convert CS2 match demos into route replay files, then play those rounds back through bots on a local CS2 server.

If this project helps you, please consider giving it a star. It makes the project easier for other CS2 tool/plugin developers to find.

## Demo

First-person spectator view stays synchronized while bots replay converted CS2 demo movement, view angles, firing, and weapon state.

<p align="center">
  <img src="docs/media/first-person-replay-nuke.gif" alt="First-person CS2 bot replay on Nuke">
</p>
<p align="center">
  <img src="docs/media/first-person-replay-route.gif" alt="First-person CS2 bot replay through an indoor route">
</p>

## What It Does

CS2 DemoTracer takes a `.dem` file, analyzes its rounds, and exports compressed `.dtr` route replay files for each player.

In a local CS2 server, the runtime and CounterStrikeSharp plugin can then make bots replay the demo player's movement, view angles, jumping, crouching, firing, and basic weapon switching.

This is still an MVP, but the full demo -> replay -> in-game bot playback loop is already working.

Plugin/runtime authors who only need to inspect replay fields can read the binary layout in [`docs/FORMAT.md`](docs/FORMAT.md).

## Who This Is For

- People who want to replay pro match movement inside a local CS2 server.
- People who want a fast CLI pipeline for `.dem` -> `.dtr` conversion.
- Developers building CS2 route replay, bot playback, or demo analysis tooling.

## Requirements

- Windows x64 for the packaged converter.
- Rust only if building the converter from source.
- A local CS2 server with Metamod and CounterStrikeSharp if you want in-game playback.

The converter is a standalone CLI executable. The playback plugins are only needed when loading generated `.dtr` files in CS2.

If you only want to test plugin playback, use the pre-converted Mirage sample pack: [`samples/cs2-demotracer-sample-spirit-vs-falcons-m2-mirage-full.zip`](samples/cs2-demotracer-sample-spirit-vs-falcons-m2-mirage-full.zip). Unzip it and run playback from the included `manifest.json`.

## Convert One Demo

Open PowerShell:

```powershell
cs2-demotracer.exe inspect --demo "<demo.dem>"
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>"
cs2-demotracer.exe validate --input "<output-dir>"
```

Common conversion options:

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --rounds 0,1,5-8
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --side t
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --full-round
```

`inspect` prints the map, tick rate, row count, and recommended/suspicious round table. `convert` exports recommended rounds by default. Use `--include-suspicious` only when you intentionally want suspicious rounds. By default, exported replays stop before the C4 plant begins; use `--full-round` for full-round export.

The output looks like this:

```text
output/<demo-id>/manifest.json
output/<demo-id>/round00/t/<player>.dtr
output/<demo-id>/round00/ct/<player>.dtr
output/<demo-id>/round01/...
```

`<demo-id>` is `<demo-stem>-<hash12>`, where `hash12` is derived from the demo file contents. This prevents repeated event/map names from overwriting each other.

`manifest.json` is the easiest file to use for playback.

An interactive prompt is also available:

```powershell
cs2-demotracer.exe wizard
```

## Batch Convert A Map Pool

If you have many demos, you can build a replay pool and let the plugin choose a similar round by economy:

```powershell
cs2-demotracer.exe convert-pool --demo-dir "<demo-root>" --output "<output-dir>\mirage_pool" --map de_mirage --recursive
```

This writes:

```text
<output-dir>/mirage_pool/pool_manifest.json
<output-dir>/mirage_pool/replays/<demo-id>/manifest.json
<output-dir>/mirage_pool/replays/<demo-id>/roundNN/...
```

`convert-pool` filters by map, converts each matching demo, and records economy metadata for round selection.

## Play In CS2

Make sure your local CS2 server has loaded:

- the Metamod runtime plugin: `BotController`
- the CounterStrikeSharp plugin: `DemoTracer`

In the server console:

```text
css_plugins reload DemoTracer
dtr_weapon_align 1
dtr_run_manifest "<output-dir>\<demo-id>\manifest.json" 0
```

The last number is the starting round. Use `0` to start from round 0.

To start from a specific round:

```text
dtr_run_manifest "<output-dir>\<demo-id>\manifest.json" 12
```

For a Mirage pool:

```text
dtr_run_pool "<output-dir>\mirage_pool\pool_manifest.json" 0
```

Round 0 and round 12 only match pistol-round candidates from demo round 0 or 12. Other rounds are matched by each side's current equipment value.

Useful checks:

```text
bc_status
dtr_status 0
dtr_bots
```

Stop playback:

```text
dtr_stop_all
```

## Round Quality

The converter marks rounds as recommended or suspicious.

Suspicious rounds usually mean:

- fewer than 10 available players
- wrong T/CT player counts
- abnormally short round window
- broken reconnect data
- post-match garbage rounds at the end of the demo

For normal use, export the recommended rounds only.

## Current Limitations

- Windows x64 local CS2 is the primary target.
- The server should run the same map and have enough bots.
- `.dtr` uses a lossless compressed BotController-compatible replay format. Full offline subtick/usercmd reconstruction is future work.
- Some weapon/loadout details are still limited by CS2 slot behavior, especially default pistols.
- This is for local servers, research, content creation, and plugin development. It is not intended for matchmaking or cheating.

## Advanced CLI

```powershell
cd cs2-demotracer\converter
cargo test
cargo run --release -- inspect --demo <demo.dem>
cargo run --release -- convert --demo <demo.dem> --output <output-dir>
cargo run --release -- convert-pool --demo-dir <demo-root> --output <output-dir> --map de_mirage --recursive
cargo run --release -- validate --input <output-dir>
cargo run --release -- wizard
```

Repository layout:

- `converter/`: Rust CLI and prompt-style wizard converter.
- `runtime/BotController/`: CS2 Metamod runtime.
- `css/`: CounterStrikeSharp control plugin.
- `docs/`: extra docs.
- `third_party/`: vendored third-party source and license notes.

## Credits

Thanks to:

- [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller): CS2 bot hooks, replay, recording, input injection, and weapon-locking ideas. This project uses the BotController runtime architecture.
- [LaihoE/demoparser](https://github.com/LaihoE/demoparser): Rust CS2 demo parser used by the converter.
- [csgowiki/minidemo-encoder](https://github.com/csgowiki/minidemo-encoder): inspiration for the historical CS:GO demo-to-replay tooling workflow.
- The Metamod:Source and CounterStrikeSharp communities.

This project is licensed under GPL-3.0. See `NOTICE.md` and the vendored source folders for third-party license details.
