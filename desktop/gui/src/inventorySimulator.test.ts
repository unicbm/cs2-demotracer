/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  inventorySimulatorItemForCosmetic,
  type InventorySimulatorCatalogResolvers,
} from "./inventorySimulator.ts";
import type { CosmeticEvidence } from "./types.ts";

const resolvers: InventorySimulatorCatalogResolvers = {
  item: () => ({ id: 307, stickerSchemaCount: 5 }),
  stickerId: (id) => id + 10_000,
  keychainId: (id) => id + 20_000,
};

describe("Inventory Simulator cosmetic handoff", () => {
  it("maps supported demo evidence into one simulator item", () => {
    const cosmetic: CosmeticEvidence = {
      kind: "weapon",
      itemDefIndex: 7,
      paintKit: 926,
      seed: 42,
      wear: 0.1234567,
      quality: 9,
      stattrakCounter: 321,
      customName: "千古风流今在此，万里功名莫放休",
      originalOwnerSteamId: "76561198000000000",
      itemId: "123456789",
      stickers: [{
        slot: 2,
        stickerId: 225,
        wear: 0.019,
        offsetX: 0.123456,
        offsetY: -0.654321,
        scale: 1.25,
        rotation: 12.4,
      }],
      charms: [{
        slot: 0,
        charmId: 77,
        offsetX: 0.123456,
        offsetY: -0.123456,
        offsetZ: 0.000099,
        seed: 1234,
      }],
    };

    assert.deepEqual(inventorySimulatorItemForCosmetic(cosmetic, resolvers), {
      id: 307,
      seed: 42,
      wear: 0.123456,
      nameTag: "千古风流今在此，万里功名莫放休",
      statTrak: 0,
      stickers: {
        0: {
          id: 10225,
          schema: 2,
          wear: 0.01,
          x: 0.1234,
          y: -0.6543,
          rotation: 12.5,
        },
      },
      keychains: {
        0: {
          id: 20077,
          seed: 1234,
          x: 0.123,
          y: -0.123,
          z: 0,
        },
      },
    });
  });

  it("does not publish a partial item when catalog evidence is unresolved", () => {
    assert.equal(inventorySimulatorItemForCosmetic({
      kind: "weapon",
      stickers: [{ slot: 0, stickerId: 999, wear: 0, offsetX: 0, offsetY: 0 }],
    }, { ...resolvers, stickerId: () => null }), null);
  });

  it("normalizes demo sticker slots to the catalog item's physical schemas", () => {
    const cosmetic: CosmeticEvidence = {
      kind: "weapon",
      stickers: Array.from({ length: 5 }, (_, slot) => ({
        slot,
        stickerId: slot + 1,
        wear: 0,
        offsetX: 0,
        offsetY: 0,
      })),
    };

    const item = inventorySimulatorItemForCosmetic(cosmetic, {
      ...resolvers,
      item: () => ({ id: 79, stickerSchemaCount: 4 }),
    });

    assert.deepEqual(
      Object.values(item?.stickers ?? {}).map(({ schema }) => schema),
      [0, 1, 2, 3, 0],
    );
  });
});
