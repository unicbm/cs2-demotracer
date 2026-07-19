import catalogSource from "./data/cs2-cosmetic-catalog.v1.json?raw";
import type { CosmeticEvidence, Language } from "./types";

type CatalogTuple = [imagePath: string, englishName: string, chineseName: string];

export interface CosmeticCatalogData {
  source: {
    cdnBaseUrl: string;
  };
  items: Record<string, CatalogTuple>;
  agents: Record<string, CatalogTuple>;
  stickers: Record<string, CatalogTuple>;
  charms: Record<string, CatalogTuple>;
}

export interface CosmeticCatalogEntry {
  name: string;
  imageUrl: string;
  fallbackImageUrl?: string;
}

const catalog = JSON.parse(catalogSource) as CosmeticCatalogData;
const cdnBaseUrl = catalog.source.cdnBaseUrl.replace(/\/$/, "");

function localizedEntry(
  tuple: CatalogTuple,
  language: Language,
): CosmeticCatalogEntry {
  return {
    name: language === "zh" && tuple[2] ? tuple[2] : tuple[1],
    imageUrl: `${cdnBaseUrl}/${tuple[0].replace(/^\//, "")}`,
  };
}

function withWearPreview(entry: CosmeticCatalogEntry, wear: number | null | undefined): CosmeticCatalogEntry {
  if (wear === null || wear === undefined) return entry;
  const grade = wear < 1 / 3 ? "light" : wear < 2 / 3 ? "medium" : "heavy";
  const variant = entry.imageUrl.replace(/\.webp$/, `_${grade}.webp`);
  return variant === entry.imageUrl
    ? entry
    : { ...entry, imageUrl: variant, fallbackImageUrl: entry.imageUrl };
}

export function resolveCosmeticCatalog(
  cosmetic: CosmeticEvidence,
  language: Language,
): CosmeticCatalogEntry | null {
  if (cosmetic.itemDefIndex === null || cosmetic.itemDefIndex === undefined) return null;
  if (cosmetic.kind === "agent") {
    const tuple = catalog.agents[String(cosmetic.itemDefIndex)];
    return tuple ? localizedEntry(tuple, language) : null;
  }

  const paintKit = cosmetic.paintKit ?? 0;
  const tuple = catalog.items[`${cosmetic.itemDefIndex}:${paintKit}`];
  return tuple ? withWearPreview(localizedEntry(tuple, language), cosmetic.wear) : null;
}

export function resolveStickerCatalog(
  stickerId: number,
  language: Language,
): CosmeticCatalogEntry | null {
  const tuple = catalog.stickers[String(stickerId)];
  return tuple ? localizedEntry(tuple, language) : null;
}

export function resolveCharmCatalog(
  charmId: number,
  stickerId: number | null | undefined,
  language: Language,
): CosmeticCatalogEntry | null {
  const tuple = stickerId === null || stickerId === undefined
    ? undefined
    : catalog.charms[`${charmId}:${stickerId}`];
  const fallback = tuple ?? catalog.charms[`${charmId}:0`];
  return fallback ? localizedEntry(fallback, language) : null;
}
