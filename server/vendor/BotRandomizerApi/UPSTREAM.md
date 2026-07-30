# BotRandomizer API provenance

- Upstream repository: `https://github.com/unicbm/CS2-Bot-Randomizer`
- Upstream commit: `81d7b9e31eea917bcfd0dd21a691dfccfee7c7ea`
- Upstream paths: `BotRandomizerApi/BotRandomizerApi.csproj` and
  `BotRandomizerApi/IBotRandomizerApi.cs`
- Local status: verbatim API v1 contract snapshot; no source modifications
- License: AGPL-3.0-only, the same license distributed in this repository's
  root `LICENSE` file

The snapshot keeps DemoTracer source builds self-contained. Runtime deployment
still uses the single canonical `BotRandomizerApi.dll` installed under
`addons/counterstrikesharp/shared/BotRandomizerApi/`; the playback bundle does
not package a second copy.
