# CS2 Pro SteamID Lib attribution

The authoritative collected player records are maintained in
[`unicbm/CS2-pro-steamid-lib`](https://github.com/unicbm/CS2-pro-steamid-lib).
DemoTracer intentionally does not track a duplicate of that full JSON dataset.
Release and CI builds generate an ignored deterministic projection locally and
bundle it for offline professional Counter-Strike identity recognition; neither
the importer nor the packaged app queries Liquipedia at runtime.

The source repository and commit are pinned in
`desktop/gui/pro-steamid-catalog-source.json`.

The projection retains each mapping source URL and evidence class. Liquipedia
records also retain the MediaWiki revision ID, revision timestamp, and
retrieval date required for attribution. The import script validates the
one-to-one SteamID/player invariant before replacing the snapshot:

```powershell
node desktop/gui/scripts/import-pro-steamid-catalog.mjs <cs2-pro-steamid-lib>
```

The upstream project code is MIT-licensed. Maintainer-authored factual data is
offered under CC0 1.0; records derived from Liquipedia are attributed to
Liquipedia contributors under CC BY-SA 3.0. See `DATA_LICENSE.md` in this
directory for the upstream data-license notice.
