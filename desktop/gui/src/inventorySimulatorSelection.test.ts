/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import {
  addInventorySimulatorSelections,
  INVENTORY_SIMULATOR_BATCH_LIMIT,
  inventorySimulatorSelectionKey,
  type InventorySimulatorItem,
  toggleInventorySimulatorSelection,
} from "./inventorySimulator.ts";

function item(id: number): InventorySimulatorItem {
  return { id };
}

test("inventory selection persists entries from multiple demo players", () => {
  const first = {
    key: inventorySimulatorSelectionKey("76561198000000001", "weapon-0"),
    item: item(7),
  };
  const second = {
    key: inventorySimulatorSelectionKey("76561198000000002", "weapon-0"),
    item: item(9),
  };
  const selected = addInventorySimulatorSelections(new Map(), [first, second]);

  assert.equal(selected.size, 2);
  assert.deepEqual([...selected.values()], [item(7), item(9)]);
  assert.equal(toggleInventorySimulatorSelection(selected, first).has(second.key), true);
});

test("inventory selection enforces the simulator batch limit across players", () => {
  const entries = Array.from({ length: INVENTORY_SIMULATOR_BATCH_LIMIT + 5 }, (_, index) => ({
    key: inventorySimulatorSelectionKey(String(index), "weapon-0"),
    item: item(index + 1),
  }));

  assert.equal(addInventorySimulatorSelections(new Map(), entries).size, INVENTORY_SIMULATOR_BATCH_LIMIT);
});
