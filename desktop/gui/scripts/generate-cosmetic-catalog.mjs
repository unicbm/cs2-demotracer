/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { execFileSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const PINNED_COMMIT = "e8057c583e89d6b7a37f27e1cb7ebdbe94dd6238";
const REPOSITORY = "https://github.com/ianlucas/cs2-lib";
const CDN_BASE_URL = "https://cdn.cstrike.app";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const desktopDirectory = resolve(scriptDirectory, "..");
const sourceArgument = process.argv[2];
if (!sourceArgument) {
  throw new Error("Usage: node desktop/gui/scripts/generate-cosmetic-catalog.mjs <cs2-lib-checkout>");
}
const sourceRoot = resolve(sourceArgument);
const outputPath = join(
  desktopDirectory,
  "src",
  "data",
  "cs2-cosmetic-catalog.v1.json",
);

function readJson(path) {
  return JSON.parse(readFileSync(path, "utf8"));
}

function readGeneratedTranslation(path, exportName) {
  const source = readFileSync(path, "utf8");
  const marker = `export const ${exportName}: CS2ItemTranslationMap = `;
  const start = source.indexOf(marker);
  const end = source.lastIndexOf(";");
  if (start < 0 || end < start) {
    throw new Error(`Unexpected generated translation format: ${path}`);
  }
  return JSON.parse(source.slice(start + marker.length, end));
}

function numericKeyCompare(left, right) {
  const leftParts = left.split(":").map(Number);
  const rightParts = right.split(":").map(Number);
  for (let index = 0; index < Math.max(leftParts.length, rightParts.length); index += 1) {
    const difference = (leftParts[index] ?? 0) - (rightParts[index] ?? 0);
    if (difference !== 0) return difference;
  }
  return 0;
}

function buildMap(entries) {
  const output = new Map();
  for (const [key, value] of entries) {
    if (output.has(key)) throw new Error(`Duplicate cosmetic catalog key: ${key}`);
    output.set(key, value);
  }
  return Object.fromEntries([...output].sort(([left], [right]) => numericKeyCompare(left, right)));
}

const actualCommit = execFileSync("git", ["-C", sourceRoot, "rev-parse", "HEAD"], {
  encoding: "utf8",
}).trim();
if (actualCommit !== PINNED_COMMIT) {
  throw new Error(`Expected cs2-lib ${PINNED_COMMIT}, found ${actualCommit}`);
}

const commitDate = execFileSync(
  "git",
  ["-C", sourceRoot, "show", "-s", "--format=%cI", "HEAD"],
  { encoding: "utf8" },
).trim();
const items = readJson(join(sourceRoot, "scripts", "data", "items.json"));
const english = readJson(join(sourceRoot, "scripts", "data", "english.json"));
const schinese = readGeneratedTranslation(
  join(sourceRoot, "src", "translations", "schinese.ts"),
  "schinese",
);
const itemsById = new Map(items.map((item) => [item.id, item]));

function compactEntry(item) {
  const englishName = english[item.id]?.name;
  const chineseName = schinese[item.id]?.name;
  if (!item.image || !englishName || !chineseName) {
    throw new Error(`Incomplete cosmetic catalog entry for cs2-lib item ${item.id}`);
  }
  return [item.image, englishName, chineseName, item.id, item.rarity ?? "#b0b4ba"];
}

const mainTypes = new Set(["weapon", "melee", "glove"]);
const inventorySimulatorTypes = new Set([
  ...mainTypes,
  "agent",
  "sticker",
  "keychain",
  "musickit",
]);

function stickerSchemaCount(item) {
  const parent = itemsById.get(item.baseId) ?? item;
  const count = (item.legacy ? parent.legacyStickerSchemaCount : undefined)
    ?? parent.stickerSchemaCount
    ?? 5;
  if (!Number.isSafeInteger(count) || count < 1) {
    throw new Error(`Invalid sticker schema count for cs2-lib item ${item.id}`);
  }
  return count;
}

const catalog = {
  schemaVersion: 1,
  source: {
    repository: REPOSITORY,
    commit: PINNED_COMMIT,
    commitDate,
    cdnBaseUrl: CDN_BASE_URL,
  },
  inventorySimulator: {
    freeItemIds: items
      .filter((item) => inventorySimulatorTypes.has(item.type) && item.free === true)
      .map((item) => item.id)
      .sort((left, right) => left - right),
    stickerSchemaCountByItemId: buildMap(
      items
        .filter((item) => mainTypes.has(item.type))
        .map((item) => [String(item.id), stickerSchemaCount(item)]),
    ),
  },
  items: buildMap(
    items
      .filter((item) => mainTypes.has(item.type))
      .map((item) => [`${item.def}:${item.index ?? 0}`, compactEntry(item)]),
  ),
  agents: buildMap(
    items
      .filter((item) => item.type === "agent")
      .map((item) => [`${item.def}`, compactEntry(item)]),
  ),
  stickers: buildMap(
    items
      .filter((item) => item.type === "sticker")
      .map((item) => [`${item.index}`, compactEntry(item)]),
  ),
  charms: buildMap(
    items
      .filter((item) => item.type === "keychain")
      .map((item) => {
        const wrappedSticker = item.stickerId === undefined ? undefined : itemsById.get(item.stickerId);
        if (wrappedSticker !== undefined && wrappedSticker.type !== "sticker") {
          throw new Error(`Keychain ${item.id} wraps non-sticker item ${item.stickerId}`);
        }
        return [`${item.index}:${wrappedSticker?.index ?? 0}`, compactEntry(item)];
      }),
  ),
  musicKits: buildMap(
    items
      .filter((item) => item.type === "musickit")
      .map((item) => [`${item.index}`, compactEntry(item)]),
  ),
};

mkdirSync(dirname(outputPath), { recursive: true });
writeFileSync(outputPath, `${JSON.stringify(catalog)}\n`, "utf8");

console.log(
  `Wrote ${outputPath} (${Object.keys(catalog.items).length} items, ` +
    `${Object.keys(catalog.agents).length} agents, ${Object.keys(catalog.stickers).length} stickers, ` +
    `${Object.keys(catalog.charms).length} charms, ${Object.keys(catalog.musicKits).length} music kits).`,
);
