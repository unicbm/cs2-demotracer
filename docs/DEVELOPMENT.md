# Development

## Architecture

| Path | Responsibility |
| --- | --- |
| `desktop/gui/` | Supported Tauri/React application and thin Rust command bridge |
| `desktop/converter/` | Rust parsing, analysis, `.dtr` writing, manifests, and validation |
| `server/plugins/DemoTracer/` | CounterStrikeSharp orchestration and `dtr_` commands |
| `server/plugins/DemoTracerApi/` | Companion-plugin API |
| `server/runtime/BotController/` | Native replay buffers, movement/input injection, weapon control, and C ABI |
| `server/runtime/BotHider/` | Native and managed bot identity/presentation provider |
| `shared/contracts/` | Versioned desktop/server release contracts |
| `shared/econ/` | Cross-runtime projection generated from the pinned `@ianlucas/cs2-lib` package |
| `tooling/` | Validation, packaging, signing, and publishing automation |

The Rust converter crate is the conversion truth source. The desktop backend
calls it directly; there is no supported converter CLI. Future automation
should use a separately versioned API instead of recreating a second UI.

## Build and Test

Requirements:

- Rust stable with the Windows MSVC target
- Node.js 22 and pnpm 11.9
- .NET 10 SDK
- Tauri's Windows prerequisites and Microsoft Edge WebView2
- Local CS2 Metamod/SDK toolchain only when rebuilding native runtimes

The full professional identity dataset is maintained in the separate public
[`unicbm/CS2-pro-steamid-lib`](https://github.com/unicbm/CS2-pro-steamid-lib)
repository and is not tracked here. Before desktop checks or builds, check out
the revision pinned by `desktop/gui/pro-steamid-catalog-source.json` and generate
the local ignored snapshot:

```powershell
node desktop\gui\scripts\import-pro-steamid-catalog.mjs <cs2-pro-steamid-lib>
```

The importer is offline: it reads that checkout's committed cache and never
contacts Liquipedia. It refuses dirty or unpinned source worktrees. CI performs
the same pinned checkout and generation step.

Run the narrowest affected checks first:

```powershell
cd tooling\cs2-lib-data
npm.cmd ci --ignore-scripts
npm.cmd run check

cd ..\..\desktop\converter
cargo test --locked

cd ..\gui
pnpm install --frozen-lockfile
pnpm run check
pnpm test
cargo test --manifest-path src-tauri\Cargo.toml --locked

cd ..\..
.\tooling\scripts\test-css.ps1
.\tooling\scripts\check-release-contract.ps1
```

Refresh `shared/econ/cs2-lib-econ-index.v1.json` only by updating the exact
`@ianlucas/cs2-lib` dependency and lockfile under `tooling/cs2-lib-data`, then
running `npm.cmd run generate` there. Do not add or patch item IDs in the
generated JSON.

Build the supported desktop target:

```powershell
cd desktop\gui
pnpm run tauri:build --target x86_64-pc-windows-msvc -- --locked
```

Debug Rust conversion is intentionally slow. Use release builds for performance
measurements.

## Converter Invariants

- CS2 demos only.
- The complete demo is parsed before round selection.
- Reuse one `ParsedDemo` across analysis and export; do not add redundant
  workflow-level parses.
- Preserve stored evidence bit-exactly. Format changes require an explicit
  version decision.
- Cosmetic/econ export stays explicit opt-in.
- Output contains `.dtr`, manifests, optional `.dtv` voice sidecars, and local
  GUI metadata—not CSV, Parquet, or raw debug dumps.

## Runtime Invariants

- Keep manifest ABI, C# readers, native ABI, and packaging contracts in sync.
- Never assign replay control to a human player.
- Release locks, injection state, pending alignments, and replay ownership on
  stop, unload, finish, handoff, or failure.
- Movement replay uses native movement/input hooks; teleport is not the primary
  playback path.
- Ordinary weapon, attachment, and scoreboard alignment remain default-off and
  demo-backed. BotRandomizer coordination claims Agent, Knife, Gloves, and
  ordinary weapon fields only when the selected preset has positive demo
  evidence. Missing cosmetic evidence must not trigger entity reconstruction.

## Packaging

The public release contains exactly two Windows x64 assets:

- `DemoTracer-GUI-vVERSION-windows-x64.exe`: NSIS desktop installer.
- `DemoTracer-CSS-vVERSION-windows-x64.zip`: matched CS2 plugin bundle.

The desktop app has no remote updater. Users download new installers and CSS
bundles from this repository's GitHub Releases. Local CSS installation still
validates the bundle receipt and every recorded file hash before changing CS2,
and preserves one rollback.

```powershell
.\tooling\scripts\package-release.ps1 `
  -Version <version> `
  -CertificateThumbprint <code-signing-certificate-thumbprint>
```

The release contract check verifies package versions and ABI/API gates before
packaging. `package-release.ps1` rebuilds the NSIS installer and CSS bundle, then
creates a clean `dist/release-v<version>` directory containing only those two
files.

An Authenticode code-signing certificate can reduce Windows reputation
warnings. Pass its SHA-1 certificate-store thumbprint when available. Without a
certificate, pass `-AllowUnsignedInstaller`; the script labels the result as
unsigned and Windows SmartScreen may warn. Never commit or upload a certificate
private key or PFX file.

Before publishing:

```powershell
git status -sb
git diff --check
```

Do not publish raw demos, generated replay archives, logs, local paths, private
server configuration, or build output.
