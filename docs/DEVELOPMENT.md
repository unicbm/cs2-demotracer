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
  demo-backed. For a BotHider-authenticated professional replay slot,
  BotRandomizer coordination always reserves Agent, Knife, and Gloves; missing
  evidence restores the native team model, default team knife, and no gloves.

## Packaging

Windows releases have two signing layers:

- A Tauri updater key signs both the NSIS updater artifact and playback ZIP.
  Store the private key outside the repository, set
  `TAURI_SIGNING_PRIVATE_KEY_PATH`, and keep the matching public key in
  `tooling/release/updater-public-key.txt`.
- An Authenticode code-signing certificate can sign the NSIS executable and
  reduce Windows reputation warnings. Pass its SHA-1 certificate-store
  thumbprint to the packaging script when available. Authenticode is recommended
  but not required for updater integrity: an unsigned build requires the
  explicit `-AllowUnsignedInstaller` override at both package and publish time.

```powershell
$env:TAURI_SIGNING_PRIVATE_KEY_PATH = "<private-key-path>"
.\tooling\scripts\package-release.ps1 `
  -Version <version> `
  -CertificateThumbprint <code-signing-certificate-thumbprint>
.\tooling\scripts\publish-r2.ps1 -Version <version>
```

The release contract check verifies package versions and ABI/API gates before
packaging. `package-release.ps1` produces a Windows x64 NSIS installer, its
Tauri updater signature, the playback ZIP and signature, both stable-channel
manifests, and `SHA256SUMS.txt` under `dist/release-v<version>`.

`publish-r2.ps1` uploads versioned assets below
`https://releases.detr.site/releases/v<version>/`, refreshes the stable download
aliases, and publishes `playback.json` and `latest.json` last. This ordering
prevents a channel manifest from advertising incomplete assets. Immutable
versioned objects use long-lived cache headers; stable aliases and channel
manifests use short revalidation windows.

Release notes are short user-facing sentences. Pass English with
`-ReleaseNotes` and Simplified Chinese with `-ReleaseNotesZh`; the desktop app
selects the active UI language while retaining compatibility with older
plain-text manifests.

Without an Authenticode certificate, pass `-AllowUnsignedInstaller`. The script
labels that result as unsigned and warns that Windows SmartScreen may appear.
The Tauri updater signature remains mandatory and is independent of
Authenticode. Never commit or upload the updater private key, its password, a
certificate private key, or a PFX file.

To verify the complete in-app GUI update path without touching stable, run
`package-gui-update-test.ps1 -AllowUnsignedInstaller`, then
`publish-gui-update-test.ps1 -AllowUnsignedInstaller`. This produces a local
v1.0.0 bootstrap installer, a signed v1.0.1 updater artifact, and publishes only
`test/gui-updater/...` plus `channels/test/latest.json`. The test build checks
that isolated channel, presents the same explicit update dialog as stable, and
never updates playback components automatically.

Before publishing:

```powershell
git status -sb
git diff --check
```

Do not publish raw demos, generated replay archives, logs, local paths, private
server configuration, or build output.
