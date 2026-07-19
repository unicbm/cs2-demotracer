import catalogSource from "./data/cs2-cosmetic-catalog.v1.json?raw";
import type { CosmeticEvidence, Language } from "./types";

type CatalogTuple = [
  imagePath: string,
  englishName: string,
  chineseName: string,
  viewerId: number,
  rarity: string,
];

export interface CosmeticCatalogData {
  source: {
    cdnBaseUrl: string;
  };
  items: Record<string, CatalogTuple>;
  agents: Record<string, CatalogTuple>;
  stickers: Record<string, CatalogTuple>;
  charms: Record<string, CatalogTuple>;
  musicKits: Record<string, CatalogTuple>;
}

export interface CosmeticCatalogEntry {
  name: string;
  imageUrl: string;
  fallbackImageUrl?: string;
  viewerId: number;
  rarity: string;
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
    viewerId: tuple[3],
    rarity: tuple[4],
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

export function resolveMusicKitCatalog(
  musicKitId: number,
  language: Language,
): CosmeticCatalogEntry | null {
  const tuple = catalog.musicKits[String(musicKitId)];
  return tuple ? localizedEntry(tuple, language) : null;
}

interface ViewerSticker {
  id: number;
  rotation?: number;
  schema?: number;
  wear?: number;
  x?: number;
  y?: number;
}

interface ViewerItem {
  id: number;
  seed?: number;
  wear?: number;
  stickers?: Record<string, ViewerSticker>;
  statTrak?: number;
  nameTag?: string;
}

export function buildCosmeticViewerUrl(
  cosmetic: CosmeticEvidence,
  language: Language,
): string | null {
  if (cosmetic.kind !== "weapon" && cosmetic.kind !== "knife") return null;
  const entry = resolveCosmeticCatalog(cosmetic, language);
  if (!entry) return null;

  const item: ViewerItem = { id: entry.viewerId };
  if (cosmetic.seed !== null && cosmetic.seed !== undefined) item.seed = cosmetic.seed;
  if (cosmetic.wear !== null && cosmetic.wear !== undefined) item.wear = cosmetic.wear;
  if (cosmetic.stattrakCounter !== null && cosmetic.stattrakCounter !== undefined) item.statTrak = cosmetic.stattrakCounter;
  if (cosmetic.customName) item.nameTag = cosmetic.customName;

  const stickers = Object.fromEntries((cosmetic.stickers ?? []).flatMap((sticker, index) => {
    const stickerEntry = resolveStickerCatalog(sticker.stickerId, language);
    if (!stickerEntry) return [];
    return [[String(index), {
      id: stickerEntry.viewerId,
      schema: sticker.slot,
      wear: sticker.wear,
      x: sticker.offsetX,
      y: sticker.offsetY,
      ...(sticker.rotation !== null && sticker.rotation !== undefined ? { rotation: sticker.rotation } : {}),
    } satisfies ViewerSticker]];
  }));
  if (Object.keys(stickers).length > 0) item.stickers = stickers;

  const url = new URL("https://3d.cstrike.app/view");
  url.searchParams.set("halfRotation", "1");
  url.searchParams.set("item", JSON.stringify(item));
  return url.toString();
}
