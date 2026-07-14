# Usage

This document covers converter workflows, the GUI, pool conversion, and local
API usage. For CS2 server console commands, see
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

Round filters such as `--rounds` select analysis/export output only. Demoparser
still parses the full match, so selecting one round does not make parsing a
cheap partial operation.

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
When replay identity is explicitly set to `avatar`, DemoTracer applies valid
matching PNG avatar overrides to BotHider-managed replay bots using the real
demo SteamID64 and enables `sv_reliableavatardata` in the native runtime. This
preserves native Steam profile-card metadata. If the matching PNG is absent or
invalid, it falls back to the Steam avatar. The default `steam` mode does not
write overrides.

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

## 3. Low-Level Rust API

The converter crate exposes lossless `.dtr` IO through `cs2_demotracer::dtr`:

```rust
use cs2_demotracer::dtr::{read_rec_file, write_rec_file};

let rec = read_rec_file("clip.dtr")?;
write_rec_file("copy.dtr", &rec)?;
```

The Rust API is intended for local tools and git dependency use. The crate is
not currently published to crates.io.

## 4. Play In CS2

Make sure your local CS2 server has loaded:

- Metamod:Source
- CounterStrikeSharp
- the DemoTracer Metamod runtime plugin: `BotController`
- the DemoTracer CounterStrikeSharp plugin: `DemoTracer`

The server bundle includes `BotController`, the DemoTracer-maintained
`BotHider`, `DemoTracer`, `DemoTracerBotHider`, their API assemblies,
`demotracer-econ-index.v1.json`, and the sanitized example config. It does not
include Metamod:Source or CounterStrikeSharp. All bundled CounterStrikeSharp
plugins target .NET 10. Remove separately installed public BotHider CSS plugins
before installing the bundle.

Then use the server commands in [`COMMANDS.md`](COMMANDS.md).

Round manifests should use the high-level `dtr_go seq|round|pool` commands.
`dtr_run_manifest` and `dtr_run_pool` are compatibility aliases for old scripts,
not the preferred quick-start path.

Cosmetic alignment is optional and off by default. It has no effect unless the
round manifest was exported with `--export-cosmetics` plus the two risk
acknowledgement flags. When evidence exists, DemoTracer applies only
demo-observed weapon paint, knife, glove metadata, agent model evidence, and
stable weapon/knife custom names to safe replay bots. By default it does not
randomize cosmetics, read profile databases, or apply non-demo agents. When
`cosmetics.agent` evidence is present and `dtr_cosmetics agents` is enabled,
it can change the matching safe replay bot slot to the demo-backed agent model.
It can apply demo-observed StatTrak item quality (`quality=9`) for exported weapon
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

Crosshair alignment is off by default. If explicitly enabled with
`dtr_align crosshair on`, DemoTracer leases stable demo-observed
`crosshair_code` metadata for the safe replay bot. The bundled BotHider is the
only writer and publishes it through the controller's server-replicated
crosshair field. Handoff releases playback control without changing the loaded
replay's presentation. Exact lease release restores the current persona base
on unload/replacement, disconnect, map change, or reload. The path is fully
server-published and neither changes human client configuration nor injects
client-side code.
