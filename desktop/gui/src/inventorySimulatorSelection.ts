/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useState } from "react";
import {
  addInventorySimulatorSelections,
  INVENTORY_SIMULATOR_BATCH_LIMIT,
  toggleInventorySimulatorSelection,
  type InventorySimulatorItem,
  type InventorySimulatorSelectionEntry,
} from "./inventorySimulator";
import type { Language } from "./types";

export interface InventorySimulatorSelectionController {
  items: ReadonlyMap<string, InventorySimulatorItem>;
  busy: boolean;
  full: boolean;
  toggle: (entry: InventorySimulatorSelectionEntry) => void;
  select: (entries: readonly InventorySimulatorSelectionEntry[]) => void;
  clear: () => void;
  sync: (language: Language) => Promise<void>;
}

interface StoredInventorySimulatorSelection {
  scopeKey: string;
  items: Map<string, InventorySimulatorItem>;
  busy: boolean;
}

const EMPTY_SELECTION = new Map<string, InventorySimulatorItem>();

export function useInventorySimulatorSelection(
  scopeKey: string,
  onSync: (items: InventorySimulatorItem[], language: Language) => Promise<void>,
): InventorySimulatorSelectionController {
  const [stored, setStored] = useState<StoredInventorySimulatorSelection>({
    scopeKey,
    items: new Map(),
    busy: false,
  });
  const active = stored.scopeKey === scopeKey ? stored : null;
  const items = active?.items ?? EMPTY_SELECTION;
  const busy = active?.busy ?? false;

  return {
    items,
    busy,
    full: items.size >= INVENTORY_SIMULATOR_BATCH_LIMIT,
    toggle(entry) {
      setStored((current) => ({
        scopeKey,
        items: toggleInventorySimulatorSelection(
          current.scopeKey === scopeKey ? current.items : EMPTY_SELECTION,
          entry,
        ),
        busy: false,
      }));
    },
    select(entries) {
      setStored((current) => ({
        scopeKey,
        items: addInventorySimulatorSelections(
          current.scopeKey === scopeKey ? current.items : EMPTY_SELECTION,
          entries,
        ),
        busy: false,
      }));
    },
    clear() {
      setStored({ scopeKey, items: new Map(), busy: false });
    },
    async sync(language) {
      const selectedItems = [...items.values()];
      if (selectedItems.length === 0 || busy) return;
      setStored((current) => (
        current.scopeKey === scopeKey ? { ...current, busy: true } : current
      ));
      try {
        await onSync(selectedItems, language);
        setStored((current) => (
          current.scopeKey === scopeKey
            ? { scopeKey, items: new Map(), busy: false }
            : current
        ));
      } finally {
        setStored((current) => (
          current.scopeKey === scopeKey ? { ...current, busy: false } : current
        ));
      }
    },
  };
}
