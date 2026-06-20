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
  writer format is `.dtr` v5. Do not change magic, ABI, or format layout
  without an explicit version decision and matching docs.
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
- `runtime/BotController/`: Metamod runtime hooks, replay buffers, movement and
  input injection, weapon/buy control, and native C ABI exports.
- `css/DemoTracer/`: CounterStrikeSharp plugin, `dtr_` commands, manifest
  loading, bot assignment, replay sequencing, BotHider identity handoff, loadout
  alignment, projectile alignment, and user-facing status.
- `css/DemoTracerApi/`: CounterStrikeSharp API contract exposed to companion
  plugins.
- `docs/`: user-facing usage, command, and localized supplemental docs. The
  `.dtr` format contract lives in the root `README.md`.
- `third_party/`: vendored source and attribution. Keep vendor changes minimal.

## Converter Rules

- Support CS2 demos only.
- Default conversion should prefer recommended rounds and avoid suspicious
  tail/garbage rounds.
- Export one `.dtr` per player per round under
  `output/<demo-id>/roundNN/t|ct/`, where `<demo-id>` is content-hashed.
- Preserve replay state losslessly. Do not add interpolation, quantization, or
  precision-reducing compression unless the format is explicitly versioned.
- `.dtr` v5 is the current writer format. Projectile metadata was introduced in
  v4 for smoke alignment; older v3 files remain readable but do not contain
  projectile events.
- The converter should write `.dtr`, `manifest.json`, pool manifests, and
  user-facing logs. Do not add CSV/Parquet/raw dumps unless explicitly asked.

## Runtime And CSS Rules

- Keep manifest ABI, C# reader expectations, and native runtime ABI in sync.
- Never assign replay control to real human players. Valid targets are strict
  CS2 bots or slots known to be bot-managed by the BotHider/shared-state path.
- `dtr_handoff death_or_contact slot` is the safe default for opening-route
  replay.
- On stop, unload, finish, handoff, or failure, release replay state: stop
  replay, clear input injection, unlock weapon locks, clear pending alignments,
  and reset bot state that would bias later rounds.
- Movement replay should flow through runtime movement/input hooks. Avoid
  teleport-as-primary-playback.
- Keep commands concise, stable, and under the `dtr_` prefix. Do not add public
  commands for team rosters, branding, bot profiles, or unrelated AI behavior.
- Weapon/loadout and projectile alignment are part of replay fidelity. Keep
  them defensive: avoid unstable entity deletion/replacement during live replay.

## Documentation And Releases

- README title/subtitle should stay: **CS2 DemoTracer** and “Trace CS2 demos
  into bot-executable route replays.”
- Keep English README and `docs/README.zh-Hans.md` aligned at a high level.
- Keep the root README `.dtr Format Contract` aligned with the current `.dtr`
  writer/reader.
- Release sample packs must be sanitized: no raw `.dem`, no local paths in
  manifests, and no trace/debug CSVs.
- Release notes should be factual and conservative. Do not claim Linux packages
  or non-smoke projectile fixes unless they were built and verified.

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

For runtime C++ changes, build with the local CS2 Metamod/SDK toolchain if it is
configured. If that toolchain is unavailable, say so in the final response.

Before committing or publishing:

```powershell
git status -sb
git diff --check
```

Also scan changed public docs/source for local absolute paths.
