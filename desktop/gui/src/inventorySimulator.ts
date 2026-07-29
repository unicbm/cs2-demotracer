import type { CosmeticEvidence } from "./types";

const ITEM_SEED_MAX = 1_000;
const KEYCHAIN_SEED_MAX = 100_000;
const KEYCHAIN_OFFSET_MAX = 100;
const MAX_STICKERS = 5;
const MAX_KEYCHAINS = 1;
export const INVENTORY_SIMULATOR_BATCH_LIMIT = 64;
const NAME_TAG_PATTERN = /^[A-Za-z0-9`!@#$%^&*\-+=(){}[\]\/|\\,.?:;'_，。；！\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\s]{0,20}$/u;

export interface InventorySimulatorSticker {
  id: number;
  rotation?: number;
  schema?: number;
  wear?: number;
  x?: number;
  y?: number;
}

export interface InventorySimulatorKeychain {
  id: number;
  seed?: number;
  x?: number;
  y?: number;
  z?: number;
}

export interface InventorySimulatorItem {
  id: number;
  keychains?: Record<string, InventorySimulatorKeychain>;
  nameTag?: string;
  seed?: number;
  statTrak?: 0;
  stickers?: Record<string, InventorySimulatorSticker>;
  wear?: number;
}

export interface InventorySimulatorSelectionEntry {
  key: string;
  item: InventorySimulatorItem;
}

export function inventorySimulatorSelectionKey(playerId: string, evidenceKey: string): string {
  return `${encodeURIComponent(playerId)}:${evidenceKey}`;
}

export function addInventorySimulatorSelections(
  current: ReadonlyMap<string, InventorySimulatorItem>,
  entries: readonly InventorySimulatorSelectionEntry[],
): Map<string, InventorySimulatorItem> {
  const next = new Map(current);
  for (const entry of entries) {
    if (!next.has(entry.key) && next.size >= INVENTORY_SIMULATOR_BATCH_LIMIT) break;
    next.set(entry.key, entry.item);
  }
  return next;
}

export function toggleInventorySimulatorSelection(
  current: ReadonlyMap<string, InventorySimulatorItem>,
  entry: InventorySimulatorSelectionEntry,
): Map<string, InventorySimulatorItem> {
  const next = new Map(current);
  if (next.has(entry.key)) {
    next.delete(entry.key);
  } else if (next.size < INVENTORY_SIMULATOR_BATCH_LIMIT) {
    next.set(entry.key, entry.item);
  }
  return next;
}

export interface InventorySimulatorCatalogResolvers {
  item: (cosmetic: CosmeticEvidence) => {
    id: number;
    stickerSchemaCount: number;
  } | null;
  stickerId: (stickerId: number) => number | null;
  keychainId: (keychainId: number, stickerId: number | null | undefined) => number | null;
}

export function inventorySimulatorItemWithNameTag(
  item: InventorySimulatorItem,
  nameTag: string,
): InventorySimulatorItem {
  const normalized = nameTag.trim();
  return normalized.length > 0 && NAME_TAG_PATTERN.test(normalized)
    ? { ...item, nameTag: normalized }
    : item;
}

function validCatalogId(value: number | null): value is number {
  return value !== null && Number.isSafeInteger(value) && value > 0;
}

function truncateTo(value: number, decimals: number): number {
  if (!Number.isFinite(value)) return value;
  const text = value.toString();
  if (text.includes("e")) return text.includes("e-") ? 0 : value;
  const decimal = text.indexOf(".");
  return decimal < 0 ? value : Number(text.slice(0, decimal + decimals + 1));
}

function normalizedRotation(value: number): number {
  const snapped = Math.round(value * 2) / 2;
  const wrapped = ((snapped + 180) % 360 + 360) % 360 - 180;
  return Object.is(wrapped, -0) ? 0 : wrapped;
}

export function inventorySimulatorItemForCosmetic(
  cosmetic: CosmeticEvidence,
  resolvers: InventorySimulatorCatalogResolvers,
): InventorySimulatorItem | null {
  const catalogItem = resolvers.item(cosmetic);
  if (!catalogItem
    || !validCatalogId(catalogItem.id)
    || !Number.isSafeInteger(catalogItem.stickerSchemaCount)
    || catalogItem.stickerSchemaCount < 1) return null;

  const item: InventorySimulatorItem = { id: catalogItem.id };
  if (cosmetic.seed !== null && cosmetic.seed !== undefined) {
    if (!Number.isSafeInteger(cosmetic.seed) || cosmetic.seed < 1 || cosmetic.seed > ITEM_SEED_MAX) return null;
    item.seed = cosmetic.seed;
  }
  if (cosmetic.wear !== null && cosmetic.wear !== undefined) {
    if (!Number.isFinite(cosmetic.wear) || cosmetic.wear < 0 || cosmetic.wear > 1) return null;
    item.wear = truncateTo(cosmetic.wear, 6);
  }
  if (cosmetic.customName) {
    if (!NAME_TAG_PATTERN.test(cosmetic.customName)) return null;
    item.nameTag = cosmetic.customName;
  }
  if (cosmetic.quality === 9 || (cosmetic.stattrakCounter !== null && cosmetic.stattrakCounter !== undefined)) {
    item.statTrak = 0;
  }

  const stickers = [...(cosmetic.stickers ?? [])].sort((left, right) => left.slot - right.slot);
  if (stickers.length > MAX_STICKERS || new Set(stickers.map(({ slot }) => slot)).size !== stickers.length) return null;
  if (stickers.length > 0) {
    const mapped: Record<string, InventorySimulatorSticker> = {};
    const usedSchemas = new Set<number>();
    for (const [index, sticker] of stickers.entries()) {
      const stickerId = resolvers.stickerId(sticker.stickerId);
      if (!validCatalogId(stickerId) || !Number.isSafeInteger(sticker.slot) || sticker.slot < 0) return null;
      if (!Number.isFinite(sticker.wear) || sticker.wear < 0 || sticker.wear > 1) return null;
      if (![sticker.offsetX, sticker.offsetY].every(Number.isFinite)) return null;
      if (sticker.rotation !== null && sticker.rotation !== undefined && !Number.isFinite(sticker.rotation)) return null;
      let schema = sticker.slot;
      if (schema >= catalogItem.stickerSchemaCount) {
        schema = Array.from(
          { length: catalogItem.stickerSchemaCount },
          (_, candidate) => candidate,
        ).find((candidate) => !usedSchemas.has(candidate)) ?? 0;
      }
      usedSchemas.add(schema);
      mapped[String(index)] = {
        id: stickerId,
        schema,
        wear: truncateTo(sticker.wear, 2),
        x: truncateTo(sticker.offsetX, 4),
        y: truncateTo(sticker.offsetY, 4),
        ...(sticker.rotation !== null && sticker.rotation !== undefined
          ? { rotation: normalizedRotation(sticker.rotation) }
          : {}),
      };
    }
    item.stickers = mapped;
  }

  const keychains = [...(cosmetic.charms ?? [])].sort((left, right) => left.slot - right.slot);
  if (keychains.length > MAX_KEYCHAINS || new Set(keychains.map(({ slot }) => slot)).size !== keychains.length) return null;
  if (keychains.length > 0) {
    const mapped: Record<string, InventorySimulatorKeychain> = {};
    for (const keychain of keychains) {
      const keychainId = resolvers.keychainId(keychain.charmId, keychain.stickerId);
      if (!validCatalogId(keychainId) || !Number.isSafeInteger(keychain.slot) || keychain.slot < 0) return null;
      if (![keychain.offsetX, keychain.offsetY, keychain.offsetZ].every(Number.isFinite)) return null;
      if ([keychain.offsetX, keychain.offsetY, keychain.offsetZ]
        .some((offset) => offset < -KEYCHAIN_OFFSET_MAX || offset > KEYCHAIN_OFFSET_MAX)) return null;
      if (keychain.seed !== null && keychain.seed !== undefined
        && (!Number.isSafeInteger(keychain.seed) || keychain.seed < 1 || keychain.seed > KEYCHAIN_SEED_MAX)) return null;
      mapped[String(keychain.slot)] = {
        id: keychainId,
        ...(keychain.seed !== null && keychain.seed !== undefined ? { seed: keychain.seed } : {}),
        x: truncateTo(keychain.offsetX, 3),
        y: truncateTo(keychain.offsetY, 3),
        z: truncateTo(keychain.offsetZ, 3),
      };
    }
    item.keychains = mapped;
  }

  return item;
}
