import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";
import vm from "node:vm";

interface DedupeResult {
  items: Array<Record<string, unknown>>;
  skipped: number;
}

interface DedupeApi {
  selectNewItems: (items: Array<Record<string, unknown>>, inventory: string | null) => DedupeResult;
}

const source = readFileSync(
  new URL("../src-tauri/src/inventory_simulator_dedupe.js", import.meta.url),
  "utf8",
);
const dedupe = vm.runInNewContext(source) as DedupeApi;

function inventory(...items: Array<Record<string, unknown>>): string {
  return JSON.stringify({
    items: Object.fromEntries(items.map((item, index) => [index, item])),
    version: 1,
  });
}

describe("Inventory Simulator duplicate filtering", () => {
  it("ignores name tags and server-only timestamps", () => {
    const candidate = {
      id: 307,
      nameTag: "kyousuke",
      seed: 42,
      wear: 0.123456,
      stickers: { 0: { id: 10_225, schema: 2, wear: 0, rotation: 0, x: 0, y: 0 } },
    };
    const result = dedupe.selectNewItems([candidate], inventory({
      ...candidate,
      nameTag: "old custom name",
      updatedAt: 123,
      equippedT: true,
      stickers: { 0: { id: 10_225, schema: 2 } },
    }));
    assert.equal(result.items.length, 0);
    assert.equal(result.skipped, 1);
  });

  it("deduplicates the selected batch itself", () => {
    const first = { id: 307, seed: 42, nameTag: "kyousuke" };
    const second = { id: 307, seed: 42, nameTag: "another name" };
    const result = dedupe.selectNewItems([first, second], null);
    assert.equal(result.items.length, 1);
    assert.equal(result.items[0], first);
    assert.equal(result.skipped, 1);
  });

  it("keeps items whose cosmetic attributes differ", () => {
    const existing = { id: 307, seed: 42, wear: 0.12 };
    const result = dedupe.selectNewItems([
      { ...existing, wear: 0.13 },
      { ...existing, seed: 43 },
    ], inventory(existing));
    assert.equal(result.items.length, 2);
    assert.equal(result.skipped, 0);
  });

  it("finds matching items inside storage units", () => {
    const stored = { id: 307, seed: 42 };
    const result = dedupe.selectNewItems([stored], inventory({
      id: 12,
      nameTag: "storage",
      storage: { 0: stored },
    }));
    assert.equal(result.items.length, 0);
    assert.equal(result.skipped, 1);
  });
});
