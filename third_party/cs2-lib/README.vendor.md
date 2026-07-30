# cs2-lib attribution

`desktop/gui/src/data/cs2-cosmetic-catalog.v1.json` is generated from the item
catalog and English/Simplified Chinese translations maintained by
[`ianlucas/cs2-lib`](https://github.com/ianlucas/cs2-lib).
`shared/econ/cs2-lib-econ-index.v1.json` is a smaller cross-runtime projection
of that project's exported item catalog.

The runtime econ projection is generated directly from the exact
`@ianlucas/cs2-lib` 8.4.0 npm dependency and its lockfile integrity. The GUI
catalog's existing source-checkout generator remains pinned to upstream commit
`e8057c583e89d6b7a37f27e1cb7ebdbe94dd6238` until that presentation-only
snapshot is refreshed.

The generated file retains only identifiers, localized display names,
rarity colors, viewer identifiers, and content-hashed CDN image paths needed
by the desktop evidence viewer. It includes weapon, agent, sticker, charm, and
music-kit lookup entries. No
upstream TypeScript source or image file is bundled. Preview images are loaded
at runtime from `https://cdn.cstrike.app` and are catalog illustrations, not an
exact render of the recorded seed, wear, stickers, or charm placement.

The generator is `desktop/gui/scripts/generate-cosmetic-catalog.mjs`. The retained
upstream MIT license applies to cs2-lib; it does not relicense Counter-Strike 2
artwork or other third-party material referenced by the catalog.

The cross-runtime ID projection generator is
`tooling/cs2-lib-data/generate-econ-index.mjs`. Its output is derived data, not
an independently maintained DemoTracer item registry.
