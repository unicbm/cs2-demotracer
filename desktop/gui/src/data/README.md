# Professional player identity data

This repository tracks the generated
`cs2-pro-steamid-lib.v1.jsonl` snapshot so clean checkouts build
reproducibly. Its embedded source revision and CC0/CC BY-SA provenance must be
preserved whenever the snapshot is refreshed.

`professional-players.v2.json` is a generated, offline catalog. It has two
deliberately separate evidence layers:

1. A professional-profile record from `XBribo/CS2-Bot-Hider` is admitted only
   when its SteamID64 is confirmed by the local demo-derived identity census.
   Unambiguous upstream ID mistakes are replaced by the demo truth.
2. A SteamID64 can also be admitted directly when the same ten normalized
   player handles form an exact roster match between a demo series and an HLTV
   all-maps scoreboard. This strict match attaches a unique HLTV Player ID and
   fills upstream coverage gaps without a name-only guess. A manually reviewed
   enrichment file may then add the registered real name and nationality for
   that already-linked Player ID.

Current team, age, prize money, rankings, achievements, player photos, and
market price estimates are intentionally excluded. They are time-sensitive or
belong to a different data/license boundary and are not identity evidence.

The generated catalog embeds the source revision, checksum, license identifier,
verification timestamp, and aggregate evidence counts. It contains no local
paths, raw demos, crosshair codes, or cosmetic data.

Generate it with:

```powershell
python desktop/gui/scripts/generate-professional-player-catalog.py `
  --comparison <comparison.csv> `
  --summary <audit-summary.json> `
  --enrichment src/data/professional-player-enrichment.v1.json `
  --demo-memberships <player-demo-memberships.csv> `
  --hltv-scoreboards <hltv-scoreboards.csv> `
  --hltv-verified-at <YYYY-MM-DD> `
  --output src/data/professional-players.v2.json
```

The upstream Bot Hider catalog is licensed AGPL-3.0-only. The derived subset
retains that upstream attribution and must remain separately auditable in the
generated catalog. The rest of the project remains governed by the repository
license and the per-component notices recorded in source control.
