# Usage

This document covers converter workflows, the GUI, pool conversion, Demo2Nade,
and local API usage. For CS2 server console commands, see
[`COMMANDS.md`](COMMANDS.md).

## 1. Convert A Demo To Round Replays

Inspect the demo first:

```powershell
cs2-demotracer.exe inspect --demo <demo.dem>
```

Convert recommended rounds:

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir>
```

Convert recommended rounds and export demo-backed in-game voice sidecars:

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir> --export-voice
```

Validate the output:

```powershell
cs2-demotracer.exe validate --input <output-dir>
```

By default, round replay export cuts before the C4 plant starts. Use
`--full-round` when you intentionally want full-round output.
Round replay exports keep at most 10 seconds of same-round freeze-time context
by default so pre-freeze grenade holds can release correctly after
`round_freeze_end`. Tune this with `--freeze-preroll-seconds`.
`--full-round` controls exported replay coverage only. The CSS plugin still
starts playback from `round_start` / freeze time and relies on normal CS2
simulation to create later round state.

Useful options:

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir> --export-voice
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir> --rounds 0,1,2,5-8
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir> --side t
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir> --include-suspicious
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir> --freeze-preroll-seconds 10
```

`--export-voice` writes `voice/roundXX.dtv` files for automatic runtime voice
playback. It only works when the demo contains recorded in-game voice data.
Community, FACEIT, and 5E demos are more likely to include it than demos whose
platform stripped voice data. See [`VOICE.md`](VOICE.md) for the full workflow.

In the GUI, `Export voice if present` is enabled by default. The GUI parses
voice metadata during Analyze, writes `.dtv` sidecars during Convert when voice
is present, and adds `dtr_voice_auto on` to copied console commands when voice
sidecars were exported.

Cosmetic/econ metadata is not exported by default, so normal manifests contain
no `cosmetics` blocks. To intentionally export demo-observed weapon paint,
knife, glove, agent model metadata, and stable weapon/knife custom names, you
must pass all three flags:

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <output-dir> --export-cosmetics --acknowledge-cosmetic-gslt-risk --accept-cosmetic-export-disclaimer
```

To include stable weapon sticker slot/id/wear/offset metadata, also pass
`--export-stickers`. To include stable weapon charm/keychain slot-0 id, offset,
and optional seed/highlight/sticker metadata, also pass `--export-charms`.
`convert-pool` accepts the same flags and otherwise keeps every replay manifest
cosmetic-free.

In the GUI, cosmetic export is one high-risk main option. Sticker and charm
export are saved sub-options under the cosmetic details menu because partial
cosmetic export does not make runtime cosmetic alignment a GSLT-safe operation.

The most important files are:

```text
manifest.json
avatars/<sha256>.<ext>
round00/t/*.dtr
round00/ct/*.dtr
voice/round00.dtv
```

The actual output root is `<demo-stem>-<hash12>`, where `hash12` is derived from
the demo contents to avoid overwriting unrelated demos with similar names.
`voice/` is written only when `--export-voice` is set and the demo contains
usable voice frames.
`avatars/` is written only when the demo contains server-provided avatar
override images; the manifest records which SteamID64 each image came from.
When replay identity is explicitly set to `avatar`, DemoTracer applies matching
PNG avatar overrides to BotHider-managed replay bots using synthetic DTR
SteamID64 keys and enables `sv_reliableavatardata` in the native runtime. The
default `steam` identity mode does not write avatar overrides.

## 2. Batch Convert A Map Pool

Build a Mirage replay pool:

```powershell
cs2-demotracer.exe convert-pool --demo-dir <demo-root> --output <output-dir>\mirage_pool --map de_mirage --recursive
```

The pool contains:

```text
pool_manifest.json
replays/<demo-id>/manifest.json
replays/<demo-id>/roundNN/...
```

`convert-pool` filters by map and records economy metadata so the server plugin
can choose a similar round during local playback.

## 3. Demo2Nade Grenade Clips

`convert-nades` scans one demo for grenade projectiles and exports a short `.dtr`
clip around each throw. The clip contains the thrower's minimal movement and view
context, plus projectile initial position/velocity metadata.

Default context:

- `--pre-roll 1.0`: seconds before release.
- `--post-roll 0.5`: seconds after release.
- `--opening-seconds 20.0`: throws before this live-round threshold are marked
  as `opening` unless they are post-plant.

Basic export:

```powershell
cs2-demotracer.exe convert-nades --demo <demo.dem> --output <output-dir>\nades
```

Filtered export:

```powershell
cs2-demotracer.exe convert-nades --demo <demo.dem> --output <output-dir>\nades --side ct --rounds 3,4,8-12 --pre-roll 0.8 --post-roll 0.35 --opening-seconds 18
```

Output layout:

```text
<output-dir>/nades/<demo-id>/nade_manifest.json
<output-dir>/nades/<demo-id>/nade_manifest.json.br
<output-dir>/nades/<demo-id>/nade_conversion.log
<output-dir>/nades/<demo-id>/nades/<side>/<phase>/<kind>/<clip-id>.dtr
```

`side` is `t` or `ct`. `phase` is:

- `opening`: early live-round utility before the configured opening threshold.
- `combat`: live-round utility before C4 is planted.
- `retake`: utility after C4 is planted.

Supported grenade kinds are smoke, flash, HE, molotov, incendiary, and decoy.
Molotov and incendiary share the manifest kind `molotov`; `weapon_def_index`
distinguishes weapon `46` from `48`.

Clips are absolute-map `.dtr` files. They are intentionally not relative
lineups; the manifest stores start origin, yaw, projectile initial vectors,
detonation position, player, round, side, phase, and source context.

## 4. Build A Demo2Nade Library

`convert-nades-library` converts many demos into one map-indexed local utility
library:

```powershell
cs2-demotracer.exe convert-nades-library --demo-dir <demo-root> --output <output-dir>\nade_library --recursive --jobs 8
```

Output layout:

```text
<output-dir>/nade_library/demos/<demo-id>/nade_manifest.json
<output-dir>/nade_library/maps/<map>/nade_manifest.json
<output-dir>/nade_library/maps/<map>/nade_manifest.json.br
<output-dir>/nade_library/nade_library.json
<output-dir>/nade_library/nade_library.json.br
```

Useful options:

```powershell
cs2-demotracer.exe convert-nades-library --demo-dir <demo-root> --output <library-dir> --recursive --jobs 1
cs2-demotracer.exe convert-nades-library --demo-dir <demo-root> --output <library-dir> --recursive --max-demos 20
cs2-demotracer.exe convert-nades-library --demo-dir <demo-root> --output <library-dir> --aggregate-only
cs2-demotracer.exe convert-nades-library --demo-dir <demo-root> --output <library-dir> --reuse-root <old-library>\demos
cs2-demotracer.exe convert-nades-library --demo-dir <demo-root> --output <library-dir> --no-dedupe
```

Default dedupe collapses near-identical clips in the map-level manifest only.
The source per-demo clips remain on disk. Tuning options:

- `--dedupe-origin-units 48`
- `--dedupe-yaw-degrees 8`
- `--dedupe-velocity-units 120`

Use `--aggregate-only` after changing dedupe parameters or after copying
existing per-demo exports into `demos/`.

For Python and Node.js users, see [`examples/`](../examples/). Those scripts
invoke the CLI and inspect `manifest.json`; they are integration examples, not
stable language bindings.

## 5. Rust API

The converter crate exposes a local Rust API for tools that do not want to shell
out to the CLI.

Single-demo nade clip export:

```rust
use cs2_demotracer::prelude::*;

let mut request = NadeClipExportRequest::new("match.dem", "out/nades");
request.side = Side::Both;
request.context = NadeContextOptions {
    pre_roll_seconds: 1.0,
    post_roll_seconds: 0.5,
    opening_seconds: 20.0,
};

let report = export_nade_clips_from_demo_path(&request)?;
println!("clips={} skipped={}", report.clips_written, report.skipped);
```

Export from an already parsed demo:

```rust
use cs2_demotracer::prelude::*;

let request = NadeClipExportRequest::for_parsed("out/nades");
let report = export_nade_clips_from_parsed(&parsed_demo, &request)?;
```

Build a library quietly:

```rust
use cs2_demotracer::prelude::*;

let mut request = NadeLibraryExportRequest::new("demos", "out/nade_library");
request.recursive = true;
request.jobs = 8;
request.dedupe = NadeDedupeOptions::default();

let report = build_nade_library(&request)?;
println!("maps={} clips={}", report.maps_written, report.clips);
```

Build with structured progress:

```rust
let report = build_nade_library_with_progress(&request, |event| {
    println!("{event:?}");
})?;
```

Read manifests from either `.json` or `.json.br`:

```rust
let demo_manifest = read_nade_manifest("out/nades/<demo-id>/nade_manifest.json.br")?;
let map_manifest = read_nade_map_manifest("out/nade_library/maps/de_mirage/nade_manifest.json.br")?;
let library = read_nade_library_manifest("out/nade_library/nade_library.json.br")?;
```

Low-level `.dtr` IO is available through `cs2_demotracer::dtr`:

```rust
use cs2_demotracer::dtr::{read_rec_file, write_rec_file};

let rec = read_rec_file("clip.dtr")?;
write_rec_file("copy.dtr", &rec)?;
```

The Rust API is intended for local tools and git dependency use. The crate is
not currently published to crates.io.

## 6. Play In CS2

Make sure your local CS2 server has loaded:

- Metamod:Source
- CounterStrikeSharp
- the DemoTracer Metamod runtime plugin: `BotController`
- the DemoTracer CounterStrikeSharp plugin: `DemoTracer`

The server bundle includes `BotController`, `DemoTracer`, `DemoTracerApi.dll`,
`skins_en.json`, and the sanitized example config. It does not include
Metamod:Source, CounterStrikeSharp, or CS2-Bot-Hider. BotHider is optional and
only needed for BotHider-managed replay slots plus identity features such as
demo display names, SteamID64 alignment, and demo avatar override alignment.

Then use the server commands in [`COMMANDS.md`](COMMANDS.md).

Round manifests should use the high-level `dtr_go seq|round|pool` commands.
`dtr_run_manifest` and `dtr_run_pool` are compatibility aliases for old scripts,
not the preferred quick-start path. Nade manifests use `dtr_list_nades` and
`dtr_run_nade`.

Cosmetic alignment is optional and off by default. It has no effect unless the
round manifest was exported with `--export-cosmetics` plus the two risk
acknowledgement flags. When evidence exists, DemoTracer applies only
demo-observed weapon paint, knife, glove metadata, agent model evidence, and
stable weapon/knife custom names to safe replay bots. By default it does not
randomize cosmetics, read profile databases, or apply non-demo agents. It can
apply demo-observed StatTrak item quality (`quality=9`) for exported weapon
cosmetics. When a demo StatTrak counter is not exposed, runtime writes a display
counter of `0` so CS2 can select the StatTrak counter model; this does not
invent a demo kill count. Weapon stickers require the extra `--export-stickers`
converter flag and `dtr_cosmetics stickers on` at runtime. Weapon
charms/keychains require the extra `--export-charms` converter flag and
`dtr_cosmetics charms on` at runtime.

This feature is intended for local/private replay validation. A local listen
server may not have the same GSLT exposure as a dedicated server, but bot-only
cosmetic mutation is not a policy exemption if humans can observe, control, or
use those bot items. Dedicated, community, or public servers should treat this
as cosmetic/inventory simulation risk under Valve server guidelines and enable
it only at the operator's own risk.

Crosshair alignment is on by default. DemoTracer temporarily applies stable
demo-observed `crosshair_code` metadata to a human viewer while they are
watching a safe replay bot in-eye, then restores the viewer's original
crosshair when they leave that replay POV.
