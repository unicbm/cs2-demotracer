(() => {
  const isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  const sortedEntries = (record) => Object.entries(record).sort(([left], [right]) => Number(left) - Number(right));
  const numeric = (value) => typeof value === "number" && Number.isFinite(value) ? value : undefined;

  function normalizedStickers(record) {
    if (!isRecord(record)) return undefined;
    const stickers = sortedEntries(record).map(([key, value]) => {
      if (!isRecord(value)) return null;
      const sticker = { id: numeric(value.id), schema: numeric(value.schema) ?? Number(key) };
      for (const field of ["wear", "rotation", "x", "y"]) {
        const number = numeric(value[field]);
        // cs2-lib canonicalizes zero-valued optional sticker attributes away.
        if (number) sticker[field] = number;
      }
      return sticker;
    });
    return stickers.length > 0 && stickers.every(Boolean) ? stickers : undefined;
  }

  function normalizedKeychains(record) {
    if (!isRecord(record)) return undefined;
    const keychains = sortedEntries(record).map(([key, value]) => {
      if (!isRecord(value)) return null;
      const keychain = { slot: Number(key), id: numeric(value.id) };
      for (const field of ["seed", "x", "y", "z"]) {
        const number = numeric(value[field]);
        if (number !== undefined) keychain[field] = number;
      }
      return keychain;
    });
    return keychains.length > 0 && keychains.every(Boolean) ? keychains : undefined;
  }

  function normalizedPatches(record) {
    if (!isRecord(record)) return undefined;
    const patches = sortedEntries(record).map(([key, value]) => [Number(key), numeric(value)]);
    return patches.length > 0 ? patches : undefined;
  }

  function fingerprint(item) {
    if (!isRecord(item) || numeric(item.id) === undefined) return null;
    const normalized = { id: item.id };
    for (const field of ["seed", "statTrak", "wear"]) {
      const number = numeric(item[field]);
      if (number !== undefined) normalized[field] = number;
    }
    if (typeof item.nameTag === "string" && item.nameTag.length > 0) normalized.nameTag = item.nameTag;
    const stickers = normalizedStickers(item.stickers);
    const keychains = normalizedKeychains(item.keychains);
    const patches = normalizedPatches(item.patches);
    if (stickers !== undefined) normalized.stickers = stickers;
    if (keychains !== undefined) normalized.keychains = keychains;
    if (patches !== undefined) normalized.patches = patches;
    // Intentionally ignore only inventory-specific ownership and presentation fields.
    return JSON.stringify(normalized);
  }

  function parseInventoryItems(rawInventory) {
    if (rawInventory === null) return [];
    if (typeof rawInventory !== "string") throw new Error("inventory-payload-invalid");
    const parsed = JSON.parse(rawInventory);
    if (!isRecord(parsed) || !isRecord(parsed.items)) throw new Error("inventory-payload-invalid");
    const items = [];
    const visit = (item) => {
      if (!isRecord(item)) throw new Error("inventory-item-invalid");
      items.push(item);
      if (item.storage !== undefined) {
        if (!isRecord(item.storage)) throw new Error("inventory-storage-invalid");
        for (const stored of Object.values(item.storage)) visit(stored);
      }
    };
    for (const item of Object.values(parsed.items)) visit(item);
    return items;
  }

  function selectNewItems(candidates, rawInventory) {
    if (!Array.isArray(candidates)) throw new Error("inventory-candidates-invalid");
    const existing = new Set(parseInventoryItems(rawInventory).map(fingerprint).filter(Boolean));
    const queued = new Set();
    const items = [];
    for (const item of candidates) {
      const key = fingerprint(item);
      if (key === null) throw new Error("inventory-candidate-invalid");
      if (existing.has(key) || queued.has(key)) continue;
      queued.add(key);
      items.push(item);
    }
    return { items, skipped: candidates.length - items.length };
  }

  return Object.freeze({ fingerprint, parseInventoryItems, selectNewItems });
})()
