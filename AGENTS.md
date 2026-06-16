# Agent Guidance

This repository is the public project for **CS2 DemoTracer**: tracing CS2 demos
into bot-executable route replays. Keep it independent from private server
setups, local datasets, unrelated bot AI plugins, and legacy CS:GO codebases.

## Project Scope

- The core product is CS2 `.dem` -> compressed `.dtr` conversion plus local CS2
  bot playback.
- The public command prefix is `dtr_`. Do not add new public `cs2bm_` commands.
- The `.dtr` file extension is the current public replay format. The binary
  magic is still `CSDTRREC` for format continuity; do not change it without an
  explicit format-version decision.
- The converter should write `.dtr`, `manifest.json`, pool manifests, and
  user-facing logs. Do not add CSV, Parquet, raw-position dumps, or other
  intermediate export formats unless explicitly requested.
- Keep README and docs focused on converting demos and playing route replays in
  local CS2. Avoid turning public docs into troubleshooting notes for unrelated
  plugins or local experiments.

## Repository Layout

- `converter/`: Rust CLI and prompt-style wizard converter, package and CLI
  name `cs2-demotracer`.
- `css/`: CounterStrikeSharp control plugin, assembly/project name
  `DemoTracer`, and user-facing `dtr_` commands.
- `runtime/BotController/`: CS2 Metamod runtime based on
  XBribo/CS2-Bot-Controller.
- `docs/`: user-facing supplemental docs and `.dtr` format notes.
- `third_party/`: vendored third-party source and attribution.

Keep module boundaries clear:

- Rust converter owns demo parsing, round quality analysis, `.dtr` writing,
  the interactive wizard, manifest generation, and pool generation.
- Metamod runtime owns CS2 hooks, replay buffers, movement injection, input
  injection, weapon locking, and C ABI exports.
- CounterStrikeSharp plugin owns commands, manifest loading, bot-slot
  assignment, replay sequencing, BotHider identity handoff, and user-facing
  server messages.
- Team rosters, bot AI setup, team logos, and bot-profile databases are outside
  this repository's public scope. Do not add built-in team/roster commands.

## Public Hygiene

- Never commit local machine paths, Steam install paths, demo dataset paths,
  usernames, private repo names, or server-specific deployment paths.
- Use placeholders such as `<demo.dem>`, `<output-dir>`, and `<manifest.json>`
  in docs and examples.
- Do not commit `.dem`, `.dtr`, `output/`, `tmp/`, build outputs, `target/`,
  `bin/`, `obj/`, generated logs, or local deployment packages unless the user
  explicitly requests release packaging.
- This repo is public GPL-3.0. Preserve third-party license notices and
  attribution in `NOTICE.md` and vendored folders.
- Avoid rewriting public Git history unless the user explicitly asks for
  history cleanup.

## Converter Rules

- Support CS2 demos only. Do not mix in Source1/CS:GO parser paths.
- Keep round analysis visible and configurable: recommended/suspicious rounds,
  player counts, duration, and problem text matter for real HLTV demos.
- Default conversion should prefer recommended rounds and avoid suspicious
  tail/garbage rounds.
- Per-player export is one `.dtr` per player per round under
  `output/<demo-id>/roundNN/t|ct/`, where `<demo-id>` is content-hashed.
- Do not silently include dead-player tail data. The current model exports
  alive rows inside the selected round window.
- Preserve exact replay state. Do not use interpolation, quantization, or
  precision-reducing compression when the requirement is lossless replay data.
- If reducing file size later, prefer explicit format-versioned changes such as
  delta encoding, keyframes, compression, or dictionaries. Do not break existing
  reader/runtime compatibility without a version bump and clear migration plan.

## Runtime And CSS Rules

- The manifest ABI value, C# wrapper expectation, and native runtime ABI must
  stay synchronized.
- Never assign replay control to real human players. Safe candidates are strict
  CS2 bots or slots known to be bot-managed by the local BotHider/shared-state
  path.
- `dtr_handoff death_or_contact slot` is the intended safe default: replay
  controls opening movement, then releases only the contacted/dead replay slot
  after contact/death. Use `all` only for explicit experiments where one trigger
  should release every replaying bot.
- On stop, unload, finish, handoff, or failure, release replay state: stop
  replay, clear input injection, unlock weapon locks, clear pending weapon
  alignment, and reset bot brain state that would bias native AI.
- Weapon alignment is intentionally soft. Do not delete or replace conflicting
  primary/secondary weapons during live replay; that has caused unstable
  entities and crashes. Prefer round-start inventory preset work for stronger
  alignment.
- Avoid teleport-as-primary-playback. Movement replay should flow through the
  runtime movement hooks, with snapshots used for state seeding/correction.
- Keep server commands concise and stable. If adding commands, make them useful
  for local testing and status diagnosis, and use the `dtr_` prefix.
- Do not add commands that manage team rosters, team branding, or bot AI
  profiles. Those belong in external bot/server tooling.

## Documentation

- `README.md` should present the project as **CS2 DemoTracer** with the subtitle
  “Trace CS2 demos into bot-executable route replays.”
- Keep English README and `docs/README.zh-Hans.md` aligned at a high level.
- Do not mention private local paths or private repositories in docs.
- Do not add detailed discussion of external aim plugins, headshot behavior, or
  unrelated bot AI modules to the main README.
- Credits should remain factual and concise: XBribo/CS2-Bot-Controller,
  LaihoE/demoparser, csgowiki/minidemo-encoder inspiration, Metamod:Source, and
  CounterStrikeSharp.

## Validation

Run the narrowest relevant checks after changes:

```powershell
cd converter
cargo test
```

For CSS plugin changes, build the CounterStrikeSharp project with an available
.NET SDK:

```powershell
dotnet build css\DemoTracer.csproj -c Release
```

For runtime C++ changes, build with the local CS2 Metamod/SDK toolchain if
configured. If the native toolchain is unavailable, state that explicitly in the
final response.

Before publishing, also check:

```powershell
git status -sb
git diff --check
```

Also scan README/docs/source changes for accidental local absolute paths before
publishing.

`git diff --check` may report trailing whitespace inside vendored third-party
source. Do not reformat vendored source solely to satisfy whitespace checks.

## Third-Party Source

- Treat `third_party/demoparser` as vendored source. Keep local changes minimal
  and document why they were necessary.
- Do not reformat or mechanically rewrite vendored source unless the user
  explicitly asks for a vendor refresh or patch.
- If updating vendored projects, preserve upstream license files and update
  attribution notes.
