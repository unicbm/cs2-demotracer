# cs2-lib attribution

`desktop/src/data/cs2-cosmetic-catalog.v1.json` is generated from the item
catalog and English/Simplified Chinese translations maintained by
[`ianlucas/cs2-lib`](https://github.com/ianlucas/cs2-lib).

Pinned upstream commit:
`e8057c583e89d6b7a37f27e1cb7ebdbe94dd6238`.

The generated file retains only identifiers, localized display names, and
content-hashed CDN image paths needed by the desktop evidence viewer. No
upstream TypeScript source or image file is bundled. Preview images are loaded
at runtime from `https://cdn.cstrike.app` and are catalog illustrations, not an
exact render of the recorded seed, wear, stickers, or charm placement.

The generator is `desktop/scripts/generate-cosmetic-catalog.mjs`. The retained
upstream MIT license applies to cs2-lib; it does not relicense Counter-Strike 2
artwork or other third-party material referenced by the catalog.
