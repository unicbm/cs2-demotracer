# CS2 DemoTracer

Trace CS2 demos into bot-executable route replays.

**Language:** English | [简体中文](docs/README.zh-Hans.md)

Convert CS2 match demos into route replay files, then play those rounds back through bots on a local CS2 server.

If this project helps you, please consider giving it a star. It makes the project easier for other CS2 tool/plugin developers to find.

## Demo

First-person spectator view stays synchronized while bots replay converted CS2 demo movement, view angles, firing, and weapon state.

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

CS2 DemoTracer takes a `.dem` file, analyzes its rounds, and exports compressed `.dtr` route replay files for each player.

In a local CS2 server, the runtime and CounterStrikeSharp plugin can then make bots replay the demo player's movement, view angles, jumping, crouching, firing, and basic weapon switching.

It can also export short grenade throw clips with minimal player context. This
Demo2Nade path turns demo throws into `.dtr` clips plus a typed manifest, so
other local tools can index, query, and replay real pro utility throws.

This is still an MVP, but the full demo -> replay -> in-game bot playback loop is already working.

Detailed converter usage is in [`docs/USAGE.md`](docs/USAGE.md). Server command
details are documented in [`docs/COMMANDS.md`](docs/COMMANDS.md).

## `.dtr` Format Contract

`.dtr` is the native replay file consumed by DemoTracer's CounterStrikeSharp
loader and BotController runtime. This section is the public binary contract;
format changes should update this section in the same commit.

All values are little-endian. v4 is the current writer format. The runtime
reader also accepts v3 files for backward compatibility, but v3 does not contain
projectile metadata.

The format is lossless: movement snapshots, projectile events, and subtick
records are written with their original `f32` and integer bit patterns. The body
removes duplicated adjacent tick snapshots and is then compressed with Brotli.

### Header

| Field | Type | Notes |
| --- | --- | --- |
| magic | 8 bytes | `CSDTRREC` |
| version | `u32` | `4` |
| tick_rate | `f32` | Demo tickrate estimate |
| round | `u32` | `total_rounds_played` window |
| side | `u8` | `2=T`, `3=CT`, `0=unknown` |
| flags | `u32` | Reserved |
| steam_id | `u64` | Player SteamID64 |
| tick_count | `u32` | Number of replay ticks |
| subtick_count | `u32` | Number of subtick moves |
| projectile_count | `u32` | Number of replay projectile events |
| map | `u16 len + utf8` | Map name |
| player_name | `u16 len + utf8` | Demo player name |
| codec | `u8` | `1 = Brotli` |
| body_uncompressed_len | `u64` | Expected decoded body byte length |
| body_compressed_len | `u64` | Compressed body byte length |

The next `body_compressed_len` bytes are a Brotli stream.

### Decoded Body

After decompression, the body layout is:

| Part | Count | Bytes Each |
| --- | ---: | ---: |
| `MovementSnapshotV3` | `0 if tick_count == 0, else tick_count + 1` | 92 |
| tick metadata | `tick_count` | 8 |
| `ProjectileEventV4` | `projectile_count` | 48 |
| `SubtickMoveV3` | `subtick_count` | 28 |

Tick metadata is:

| Field | Type |
| --- | --- |
| weapon_def_index | `i32` |
| num_subtick | `u32` |

Reconstruct replay ticks as:

- `tick[i].pre = snapshots[i]`
- `tick[i].post = snapshots[i + 1]`
- `tick[i].weapon_def_index = metadata[i].weapon_def_index`
- `tick[i].num_subtick = metadata[i].num_subtick`

The sum of all `num_subtick` values must equal header `subtick_count`.

### ProjectileEventV4

Projectile events store demo-derived projectile state for runtime alignment and
utility clip export. The converter emits grenade projectile events for smoke,
flash, HE, molotov/incendiary, and decoy throws when the demo has valid
projectile data. Older v3 files have no projectile event section.

| Field | Type | Notes |
| --- | --- | --- |
| tick_index | `u32` | |
| weapon_def_index | `i32` | |
| kind | `u8` | `0=unknown`, `1=smoke`, `2=flash`, `3=he`, `4=molotov/incendiary`, `5=decoy` |
| pad | 3 bytes | |
| initial_position | `f32[3]` | |
| initial_velocity | `f32[3]` | |
| detonation_position | `f32[3]` | |

### MovementSnapshotV3

This layout matches BotController ABI 11 (`92` bytes with `Pack=4`).

| Field | Type |
| --- | --- |
| origin | `f32[3]` |
| velocity | `f32[3]` |
| angles | `f32[3]` pitch/yaw/roll |
| entity_flags | `u32` |
| move_type | `u8` |
| pad | 3 bytes |
| buttons | `u64` |
| buttons1 | `u64` |
| buttons2 | `u64` |
| duck_amount | `f32` |
| duck_speed | `f32` |
| ladder_normal | `f32[3]` |
| ducked | `u8` |
| ducking | `u8` |
| desires_duck | `u8` |
| actual_move_type | `u8` |

### SubtickMoveV3

| Field | Type |
| --- | --- |
| when | `f32` |
| button | `u32` |
| pressed | `f32` |
| analog_forward | `f32` |
| analog_left | `f32` |
| pitch_delta | `f32` |
| yaw_delta | `f32` |

### Parser Checklist

1. Read and validate magic `CSDTRREC`.
2. Require `version == 4` for current writer output, or accept `version == 3`
   only if projectile metadata is optional.
3. Read `tick_count`, `subtick_count`, `projectile_count`, `map`, and
   `player_name`. For v3, treat `projectile_count` as `0` because the field is
   absent.
4. Require `codec == 1`.
5. Check `body_uncompressed_len == snapshot_count * 92 + tick_count * 8 +
   projectile_count * 48 + subtick_count * 28`, where `snapshot_count` is `0`
   for empty replays and `tick_count + 1` otherwise.
6. Read and Brotli-decompress exactly `body_compressed_len` bytes.
7. Rebuild ticks from the snapshot chain and metadata.
8. Sum all tick `num_subtick` values and verify it equals `subtick_count`.

## Who This Is For

- People who want to replay pro match movement inside a local CS2 server.
- People who want a fast CLI pipeline for `.dem` -> `.dtr` conversion.
- People building a local library of real grenade throws from CS2 demos.
- Developers building CS2 route replay, bot playback, or demo analysis tooling.

## Requirements

- Windows x64 for the primary packaged converter.
- Linux x64 release assets are experimental. The published Linux binary is built
  and smoke-tested on local WSL Ubuntu 22.04; native Linux distributions are not
  separately verified yet.
- Rust only if building the converter from source.
- A local CS2 server with Metamod and CounterStrikeSharp if you want in-game playback.

The converter is a standalone CLI executable. The playback plugins are only needed when loading generated `.dtr` files in CS2.

If you only want to test plugin playback, download the pre-converted Mirage sample pack from the release assets: [`cs2-demotracer-sample-spirit-vs-falcons-m2-mirage-full.zip`](https://github.com/unicbm/cs2-demotracer/releases/download/v0.1.1/cs2-demotracer-sample-spirit-vs-falcons-m2-mirage-full.zip). Unzip it and run playback from the included `manifest.json`.

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

## Export Grenade Clips With Demo2Nade

Demo2Nade exports each real demo grenade throw as a short `.dtr` clip with
player movement and view context around the release tick. Defaults are `1.0s`
before release and `0.5s` after release.

```powershell
cs2-demotracer.exe convert-nades --demo "<demo.dem>" --output "<output-dir>\nades"
cs2-demotracer.exe convert-nades --demo "<demo.dem>" --output "<output-dir>\nades" --side t --rounds 0,1,5-8 --pre-roll 1.0 --post-roll 0.5
```

This writes:

```text
<output-dir>/nades/<demo-id>/nade_manifest.json
<output-dir>/nades/<demo-id>/nade_manifest.json.br
<output-dir>/nades/<demo-id>/nades/<side>/<phase>/<kind>/<clip-id>.dtr
```

`phase` is `opening`, `combat`, or `retake`. The `opening` window defaults to
20 seconds after freeze time ends and can be changed with `--opening-seconds`.

To build a local map-indexed utility library from many demos:

```powershell
cs2-demotracer.exe convert-nades-library --demo-dir "<demo-root>" --output "<output-dir>\nade_library" --recursive --jobs 8
```

This writes per-demo clips under `demos/`, map manifests under `maps/<map>/`,
and a top-level `nade_library.json(.br)`. The library command deduplicates near
identical clips by default; pass `--no-dedupe` to keep every source throw.

Rust callers can use the local API directly:

```rust
use cs2_demotracer::prelude::*;

let mut request = NadeClipExportRequest::new("match.dem", "out/nades");
request.context = NadeContextOptions {
    pre_roll_seconds: 1.0,
    post_roll_seconds: 0.5,
    opening_seconds: 20.0,
};

let report = export_nade_clips_from_demo_path(&request)?;
println!("clips={}", report.clips_written);
```

See [`docs/USAGE.md`](docs/USAGE.md#demo2nade-grenade-clips) for the full CLI
and Rust API examples.

## Play In CS2

Make sure your local CS2 server has loaded:

- the Metamod runtime plugin: `BotController`
- the CounterStrikeSharp plugin: `DemoTracer`

In the server console:

```text
css_plugins reload DemoTracer
dtr_weapon_align 1
dtr_projectile_align 1
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
bc_replay_pov spectated
bc_perf 1
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

- Windows x64 local CS2 is the primary target. Linux x64 converter binaries are
  WSL Ubuntu build artifacts and may need extra validation on native Linux.
- The server should run the same map and have enough bots.
- `.dtr` uses a lossless compressed BotController-compatible replay format with demo-derived projectile metadata for smoke alignment. Full offline subtick/usercmd reconstruction is future work.
- Some weapon/loadout details are still limited by CS2 slot behavior, especially default pistols.
- CS2 demos can expose cosmetic/econ metadata, but DemoTracer intentionally does not extract or apply skins, knives, gloves, stickers, charms, or agents. Valve's [Game Server Operation Guidelines](https://blog.counter-strike.net/server_guidelines/) prohibit falsifying inventories or granting items players do not own, and Valve has previously disabled Game Server Login Tokens (GSLTs) for operators that offered those services. Third-party cosmetic overrides are outside this project and are at your own risk.
- This is for local servers, research, content creation, and plugin development. It is not intended for matchmaking or cheating.

## Advanced CLI

```powershell
cd cs2-demotracer\converter
cargo test
cargo run --release -- inspect --demo <demo.dem>
cargo run --release -- convert --demo <demo.dem> --output <output-dir>
cargo run --release -- convert-pool --demo-dir <demo-root> --output <output-dir> --map de_mirage --recursive
cargo run --release -- convert-nades --demo <demo.dem> --output <output-dir>
cargo run --release -- convert-nades-library --demo-dir <demo-root> --output <output-dir> --recursive
cargo run --release -- validate --input <output-dir>
cargo run --release -- wizard
```

Repository layout:

- `converter/`: Rust CLI, local Rust API, prompt-style wizard converter, and Demo2Nade export code.
- `runtime/BotController/`: CS2 Metamod runtime.
- `css/`: CounterStrikeSharp control plugin.
- `docs/`: extra docs.
- `third_party/`: vendored third-party source and upstream license files.

## Dependencies And Credits

The main upstream projects are:

- [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller), GPL-3.0: BotController runtime architecture, bot replay hooks, recording, input injection, and weapon locking.
- [XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider), GPL-3.0: optional BotHider integration for BotHider-managed bot detection, display-name alignment, and SteamID64 alignment.
- [LaihoE/demoparser](https://github.com/LaihoE/demoparser), MIT: Rust CS2 demo parser used by the converter. Vendored under `third_party/demoparser` with upstream license and README files preserved.
- [csgowiki/minidemo-encoder](https://github.com/csgowiki/minidemo-encoder), MIT: historical `.dem -> replay file` workflow inspiration. No Go source from this project is copied.
- [alliedmodders/metamod-source](https://github.com/alliedmodders/metamod-source): Metamod:Source plugin infrastructure used by the runtime.
- [roflmuffin/CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp): CounterStrikeSharp plugin API used by the C# control plugin.

CS2 DemoTracer's own code is licensed under GPL-3.0-only. Vendored third-party source keeps its upstream license files in `third_party/`.
