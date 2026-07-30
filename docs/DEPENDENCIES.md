# Dependencies and Compatibility

## Desktop Converter

The packaged Windows x64 GUI uses a per-user NSIS installer and requires
Microsoft Edge WebView2. The installer can bootstrap WebView2 when it is not
already present. It does not require Python, Node.js, Rust, Conda, or CS2 server
plugins.

Source builds require Rust, Node.js 22, pnpm 11.9, Tauri's Windows
prerequisites, and the Windows MSVC target. See
[DEVELOPMENT.md](DEVELOPMENT.md).

The desktop bundles or derives data from pinned, attributed components:

- `demoparser` for CS2 demo parsing.
- [Ian Lucas's `cs2-lib-inspect`](https://github.com/ianlucas/cs2-lib-inspect)
  behavior for local inspect-link encoding.
- [Ian Lucas's `cs2-lib`](https://github.com/ianlucas/cs2-lib) metadata for
  cosmetic names, localization, CDN image keys, and inspect-compatible item
  grammar.
- `csgo-sharecode` for crosshair decoding.
- `flag-icons` for bundled country flag assets in professional player profiles.
- `CS2 Pro SteamID Lib` for the offline, evidence-backed professional identity
  registry.

The authoritative collected records, including Liquipedia-derived metadata,
are maintained in our dedicated public `CS2-pro-steamid-lib` repository rather
than duplicated in DemoTracer's Git history. Maintainer and CI builds generate
an ignored snapshot from the pinned source revision and bundle it into the
desktop application, so roster recognition remains offline at runtime.
DemoTracer merges that broad registry with its smaller demo-verified catalog,
preserving stronger demo evidence when the sources overlap. The source pin,
refresh tooling, and CC0/CC BY-SA attribution are retained here. The importer
uses `i18n-iso-countries` at build time to normalize country names to ISO-style
two-letter flag codes.

Cosmetic images and the optional 3D viewer are loaded on demand from
`cdn.cstrike.app` and `3d.cstrike.app`; they are not bundled. See
[ONLINE_SERVICES.md](ONLINE_SERVICES.md).

User-confirmed Inventory Simulator batch sync runs in a dedicated Edge
WebView2 window on `inventory.cstrike.app`. It uses that official page's own
Steam session and same-origin sync routes; DemoTracer does not bundle an
Inventory Simulator client library or require an API key.

## Playback Server

DemoTracer's playback runtime is built on two foundational XBribo projects:

- [`XBribo/CS2-Bot-Controller`](https://github.com/XBribo/CS2-Bot-Controller)
  provided the engine-level bot control, movement-recording, replay, and lock
  architecture from which `server/runtime/BotController` is maintained.
- [`XBribo/CS2-Bot-Hider`](https://github.com/XBribo/CS2-Bot-Hider) provided
  the native and CounterStrikeSharp identity/presentation system from which
  `server/runtime/BotHider` is maintained.

These are maintained derivatives, not opaque binary dependencies. Their
authorship, licenses, and upstream boundaries are preserved in each runtime's
`README.md` and `UPSTREAM.md`. DemoTracer-specific ABI, safety, lease, packaging,
and lifecycle behavior is developed in this repository.

Required external components:

- Windows x64 CS2 server.
- [Metamod:Source](https://www.sourcemm.net/).
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
  1.0.371 or newer for CS2 1.41.6.9.
- The matching DemoTracer playback bundle.

The desktop app can detect Steam library installations and install, repair,
update, or roll back DemoTracer's signed playback files. Close CS2 before any
playback mutation. Automatic management does not install or replace Metamod or
CounterStrikeSharp, and it preserves the server-local
`demotracer.config.json`. A signed local bundle can be selected when the target
machine is offline.

The playback bundle contains:

- Native `BotController` and `BotHider` Metamod runtimes.
- `DemoTracer` and `DemoTracerBotHider` CounterStrikeSharp plugins.
- `DemoTracerApi.dll` and `DemoTracerBotHiderApi.dll` contracts.
- The econ index, example config, and installation receipt.

The C# projects target .NET 10. Metamod, CounterStrikeSharp itself, and RayTrace
are not bundled.

Demo-backed cosmetic alignment can coordinate with BotRandomizer 1.5 through
`BotRandomizerApi` v1. The API assembly and BotRandomizer provider are not part
of the DemoTracer playback bundle. Install the canonical API once under
`addons/counterstrikesharp/shared/BotRandomizerApi/` and install BotRandomizer
separately. Source builds use the verbatim v1 contract snapshot under
`server/vendor/BotRandomizerApi`; its provenance is recorded beside the source.

## Runtime Contracts

| Contract | Required value |
| --- | --- |
| `.dtr` writer | v8 |
| `.dtr` reader | v3–v8 |
| Manifest ABI | 17 |
| BotController native ABI | 16, minor 33+ |
| DemoTracer BotHider API | 1 |
| BotRandomizer cosmetic writer API | 1 |
| DemoTracer companion API | 6 |

`addons/demotracer-install.v1.json` records the bundle contract and file hashes.
The desktop uses it to detect missing or mixed installations. While loaded,
DemoTracer also writes a short-lived `demotracer-runtime.v1.json` heartbeat with
runtime versions and capability state; it contains no player or absolute-path
data.

## BotHider Boundary

The playback bundle's native and managed BotHider components form one versioned
identity provider. Do not install another public BotHider CounterStrikeSharp
plugin beside it. Multiple presentation writers are unsupported.

The provider leases demo names, SteamID64 values, scoreboard presentation, and
crosshair state only to validated replay bots. BotController separately applies
validated manifest avatar PNGs; missing evidence falls back to the Steam avatar.

## BotRandomizer Cosmetic Boundary

BotHider remains the authority that authenticates a replay SteamID to a live
bot slot. DemoTracer then acquires a field-granular BotRandomizer writer lease
using BotRandomizer's own slot incarnation. Every authenticated professional
replay slot always claims Agent, Knife, and Gloves as replay identity fields.
Missing normalized evidence means the native T/CT agent model, the default
team knife (CT 42 or T 59), or no gloves; it does not authorize Randomizer to
synthesize those fields. DemoTracer actively restores those defaults after a
late lease, including a freeze-period knife entity rebuild when needed.

Ordinary weapons remain positive-evidence, field-granular claims. A weapon not
present in the demo, and sticker or keychain families omitted from its evidence,
remain owned by BotRandomizer.

If the provider, identity authentication, heartbeat, or incarnation check is
unavailable, replay can continue but DemoTracer performs no cosmetic writes.
Weapon paint claims update their named paint attributes without clearing the
complete attribute list, preserving Randomizer-owned stickers and keychains.

## Optional RayTrace

[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) 1.0.16 or newer is
optional. DemoTracer discovers the `raytrace:craytraceinterface` capability at
runtime and uses it for stricter handoff line-of-sight filtering. Playback still
works without it.

## Conflicting Packages

Full CS2-Bot-Improver packages can install different binaries at the same
BotController and BotHider paths. Do not merge their `BotController`,
`BotHider`, `BotControllerImpl`, or `BotHiderImpl` files into DemoTracer's
bundle. Keep DemoTracer's complete runtime set and add only integrations that
explicitly support its ABI.
