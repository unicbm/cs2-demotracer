# Usage

This document covers converter workflows, the GUI, pool conversion, and local
API usage. For CS2 server console commands, see
[`COMMANDS.md`](COMMANDS.md).

## 1. Convert A Demo To Round Replays

Inspect the demo first:

```powershell
cs2-demotracer.exe inspect --demo <demo.dem>
```

FACEIT-style `<demo.dem.zst>` inputs are accepted directly by the same commands.
Zstandard decompression runs inside DemoTracer, and identity is calculated from
the decompressed demo content.

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

When replacing an existing GUI output, the new DTR, avatar, voice, and manifest
files are written and validated in a sibling staging directory first. Only a
complete pack is promoted; conversion, voice, validation, or promotion failures
keep the previous output, and an interrupted swap is recovered on the next run.

The GUI home screen uses one main replay library and can index additional
archive folders at the same time. Its Windows first-run default is
`Documents\CS2 DemoTracer\Library`; changing the main export library is explicit
and an existing selection is never moved automatically. New GUI output uses
`<library>\<map>\<readable-name>--<hash12>\`. CLI `--output` behavior is
unchanged. Scanning reads compact metadata from `demo-info.json` when available
and falls back conservatively to `manifest.json`; it does not parse the source
demo again or decompress every `.dtr`. Opening one entry then uses the strict
manifest reader and validates its referenced replay files. Newly converted
demos write the desktop sidecar from the same parsed `ParsedDemo`, including
round-end-derived score, stable team identities across
side swaps, K/D/A, header/server platform evidence, and full `CDemoFileInfo`
playback duration. It also stores the original absolute `.dem` or `.dem.zst` path for local
reuse in `demo-info.json` and a small independent `demo-source.json`; the
portable manifest remains sanitized. A score without an explicit match-end panel is labeled as
complete only through the last observed round. Legacy manifest scoreboard
snapshots are not shown as final scores. Metadata repair first verifies and
reuses remembered source paths. Only unresolved archives ask for a relocated
demo or a search folder; successful matches update only the local metadata
sidecars. **Choose rounds again** resolves the same source pointer
before returning to conversion. Replay payloads are never rewritten. The source file mtime is shown only as an
approximate demo-file time because a reliable absolute match timestamp is not
present in the demo. **Organize old archives** strictly validates scattered
archive folders, skips duplicate full demo hashes, and copies accepted archives
into the map-grouped main library without moving or deleting their sources.

The GUI Settings workspace keeps output/archive roots separate from raw `.dem`/`.dem.zst`
library roots, remembers safe export and playback defaults, and provides a
local environment inspection. CS2 discovery runs only after the user clicks
the detection action; a manually entered CS2 or `game/csgo` path is always
supported. Inspection is read-only: it inventories Metamod,
CounterStrikeSharp, DemoTracer, local CSS plugins, the installed bundle receipt,
and known vendor conflicts without loading scanned DLLs or rewriting the game
directory. Saved raw-demo roots are searched during library metadata repair
before the GUI asks for another directory. When a local server is running, a
short-lived `demotracer-runtime.v1.json` heartbeat lets the same page verify the
loaded BotController ABI/capabilities, BotHider provider, cosmetic alignment
switches, loaded CounterStrikeSharp host version, and CSS plugin directory names. Stale evidence is shown as not
running, never as active.

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

For every exported weapon, knife, and glove cosmetic, the manifest includes an
`inspect.command` that can be pasted into the CS2 console. It also includes an
`inspect.steam_url` that can launch the preview through Steam when the encoded
payload fits the Steam protocol's 300-character limit. Long sticker/charm
combinations retain the command and omit only the URL. These synthetic preview
payloads need no Steam inventory, market listing, GC lookup, or third-party API.

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

The playback bundle includes `BotController`, the DemoTracer-maintained
`BotHider`, `DemoTracer`, `DemoTracerBotHider`, their API assemblies,
`demotracer-econ-index.v1.json`, and the sanitized example config. It does not
include Metamod:Source or CounterStrikeSharp. All bundled CounterStrikeSharp
plugins target .NET 10. Remove separately installed public BotHider CSS plugins
before installing the playback bundle.

Then use the server commands in [`COMMANDS.md`](COMMANDS.md).

Round manifests should use the high-level `dtr_go seq|round|pool` commands.
`dtr_run_manifest` and `dtr_run_pool` are compatibility aliases for old scripts,
not the preferred quick-start path.
The desktop GUI result view remembers playback switches locally and generates a
compact `dtr_preset 0x...; dtr_go ...` command for the current manifest.

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

Crosshair alignment is on by default. DemoTracer leases stable demo-observed
`crosshair_code` metadata for the safe replay bot. The bundled BotHider is the
only writer and publishes it through the controller's server-replicated
crosshair field. Handoff, replay finish, sequence completion, later server
rounds, and match end release playback control without changing the most recent
successful DTR presentation batch. A later successful batch replaces it
atomically; explicit slot unload/kick, disconnect, map change, slot reuse, or
plugin unload restores the current persona base. The path is fully
server-published and neither changes human client configuration nor injects
client-side code. Use `dtr_align crosshair off` to disable it.
