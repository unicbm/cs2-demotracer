# Agent Guidance

This is the public repository for **CS2 DemoTracer**: convert CS2 `.dem`
files into compressed `.dtr` route replays, then play them back through bots on
a local CS2 server.

Keep this repo public, portable, and focused. Do not mix in private server
setup, local demo datasets, team roster tooling, unrelated bot AI experiments,
or legacy CS:GO paths.

## Project Boundaries

- The public CLI is `cs2-demotracer`; the public server command prefix is
  `dtr_`.
- The replay extension is `.dtr`. The binary magic is `CSDTRREC`; current
  writer format is `.dtr` v7. Current manifest ABI is 17,
  BotController native ABI is 16, and DemoTracer companion API is 6. Do not
  change magic, ABI, API, or format layout without an explicit version decision
  and matching docs.
- The maintained packaged converter target is Windows x64. Linux may work from
  source, but do not claim or publish Linux binaries unless they are built and
  verified.
- Public docs and examples must use placeholders such as `<demo.dem>`,
  `<output-dir>`, and `<manifest.json>`.
- Never commit local paths, Steam install paths, usernames, private repo names,
  `.dem` files, generated `.dtr` output, logs, `tmp/`, `target/`, `bin/`, or
  `obj/` artifacts.

## Repository Layout

- `converter/`: Rust CLI, demo parsing, round analysis, `.dtr` writing,
  manifest/pool generation, validation, and the wizard.
- `desktop/`: Tauri/React single-demo GUI and its thin Rust command bridge to
  the converter core.
- `runtime/BotController/`: Metamod runtime hooks, replay buffers, movement and
  input injection, weapon/buy control, and native C ABI exports.
- `css/DemoTracer/`: CounterStrikeSharp plugin, `dtr_` commands, manifest
  loading, bot assignment, replay sequencing, BotHider identity handoff, loadout
  alignment, projectile alignment, cosmetic/sticker alignment, crosshair
  alignment, optional scoreboard alignment, and user-facing status.
- `css/DemoTracerApi/`: CounterStrikeSharp API contract exposed to companion
  plugins.
- `docs/`: user-facing usage, command, and localized supplemental docs. The
  `.dtr` format contract lives in the root `README.md`.
- `third_party/`: vendored source and attribution. Keep vendor changes minimal.

## Converter Rules

- Support CS2 demos only.
- Debug converter builds are allowed, but they are extremely slow. Use a
  release build for normal conversion, runtime packaging, and performance
  judgments; do not treat debug-mode conversion speed as representative.
- Round selection is not a cheap partial parse. `--rounds`, single-round
  inspection, and wizard round choices filter analysis/export after demoparser
  parses the whole demo, so even one selected round still requires a full-match
  parse. Explain this in CLI/docs/workflows that expose round filters.
- Avoid redundant workflow-level full-demo parsing. Reuse `ParsedDemo` across
  analysis/export steps when the code path already has it. Parser-internal
  channel splitting is allowed only as a deliberate performance optimization
  when supplemental columns use strict row-key alignment, output ordering stays
  deterministic, and any parse or alignment mismatch falls back to the normal
  parser path.
- Default conversion should prefer recommended rounds and avoid suspicious
  tail/garbage rounds.
- Export one `.dtr` per player per round under
  `output/<demo-id>/roundNN/t|ct/`, where `<demo-id>` is content-hashed.
- Preserve replay state losslessly. Do not add interpolation, quantization, or
  precision-reducing compression unless the format is explicitly versioned.
- `.dtr` v7 is the current writer format. Projectile metadata was
  introduced in v4, v5 added `play_start_tick_index`/bounded freeze-time replay
  context, v6 added the high-fidelity metadata JSON blob, and v7 adds the
  section container plus optional command-frame/movement-extra sections. Older
  v3-v6 files remain readable; v3 files do not contain projectile events and
  v3-v5 files do not contain high-fidelity metadata JSON.
- Cosmetic/econ metadata is default-off and must stay explicit opt-in.
  `--export-cosmetics` requires the GSLT acknowledgement and export disclaimer
  flags; sticker export additionally requires `--export-stickers`. Do not add
  random cosmetics, generated fallback inventory, or server profile/database
  state.
- The converter should write `.dtr`, `manifest.json`, pool manifests, and
  user-facing logs. Do not add CSV/Parquet/raw dumps unless explicitly asked.

## Runtime And CSS Rules

- Keep manifest ABI, C# reader expectations, and native runtime ABI in sync.
- Never assign replay control to real human players. Valid targets are strict
  CS2 bots or slots known to be bot-managed by the BotHider/shared-state path.
- Default replay fidelity settings are identity `steam`, weapon/loadout alignment
  on, projectile alignment on, crosshair alignment off, left-hand desired writes
  on, partial replay on, and handoff `death_contact_c4 slot`. Cosmetic, sticker,
  and scoreboard alignment are default-off.
- Runtime default preferences may be loaded from server-local
  `demotracer.config.json` next to `DemoTracer.dll`. Keep the committed
  `demotracer.config.example.json` sanitized; do not commit private server
  configs.
- `dtr_handoff death_contact_c4 slot` is the safe default for opening-route
  replay. Death/contact handoff follows the configured scope; C4 planted releases
  all active replay slots because it is a round-phase handoff.
- On stop, unload, finish, handoff, or failure, release replay state: stop
  replay, clear input injection, unlock weapon locks, clear pending alignments,
  and reset bot state that would bias later rounds.
- Movement replay should flow through runtime movement/input hooks. Avoid
  teleport-as-primary-playback.
- Keep commands concise, stable, and under the `dtr_` prefix. Do not add public
  commands for team rosters, branding, bot profiles, or unrelated AI behavior.
- Weapon/loadout and projectile alignment are part of replay fidelity. Keep
  them defensive: avoid unstable entity deletion/replacement during live replay.
- Cosmetic and sticker alignment may only consume demo-backed manifest evidence
  and may only apply to safe replay bots. Never apply cosmetics to human
  players. Bot-only inventory mutation is not a Valve/GSLT policy exemption if
  humans can observe, control, possess, inspect, or otherwise use those bots.
- Crosshair alignment is safe to keep default-on because it only applies a
  stable demo crosshair code to the human viewer while they watch a replay bot
  in-eye, then restores the viewer's original crosshair.
- Scoreboard alignment is best-effort, default-off local presentation sync.
  Keep it one-shot and conservative; do not force damage, ADR, blind time,
  utility damage, or other fields unless the runtime interface is proven and
  documented.

## Documentation And Releases

- README title/subtitle should stay: **CS2 DemoTracer** and “Trace CS2 demos
  into bot-executable route replays.”
- Keep English README and `docs/README.zh-Hans.md` aligned at a high level.
- Keep the root README `.dtr Format Contract` aligned with the current `.dtr`
  writer/reader.
- Keep the README GSLT/cosmetic safety callout visible whenever cosmetic or
  sticker export/runtime behavior changes.
- Release sample packs must be sanitized: no raw `.dem`, no local paths in
  manifests, and no trace/debug CSVs.
- Release notes should be factual and conservative. Do not claim Linux packages
  or non-smoke projectile fixes unless they were built and verified.
- For version bumps, update `converter/Cargo.toml`, `converter/Cargo.lock`,
  `desktop/package.json`, `desktop/package-lock.json`, the desktop Tauri Cargo
  manifest/lock (`desktop/src-tauri/Cargo.toml` and
  `desktop/src-tauri/Cargo.lock`), the DemoTracer CSS module version, and all
  three packaging scripts together.
  Current release assets are the separate Windows x64 CLI and GUI converter
  zips, the Windows x64 playback bundle zip, and `SHA256SUMS.txt`.

## Validation

Run the narrowest relevant checks after changes:

```powershell
cd converter
cargo test
```

For CSS changes:

```powershell
dotnet build css\DemoTracer\DemoTracer.csproj -c Release
```

For converter release builds:

```powershell
cd converter
cargo build --release
```

For desktop GUI changes:

```powershell
cd desktop
npm.cmd ci
npm.cmd run check
npm.cmd run tauri:build -- --target x86_64-pc-windows-msvc -- --locked
```

For runtime C++ changes, build with the local CS2 Metamod/SDK toolchain if it is
configured. If that toolchain is unavailable, say so in the final response.

Before committing or publishing:

```powershell
git status -sb
git diff --check
```

Also scan changed public docs/source for local absolute paths.
