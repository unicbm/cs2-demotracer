import catalogSource from "./data/professional-players.v2.json?raw";
import registrySource from "./data/cs2-pro-steamid-lib.v1.jsonl?raw";
import {
  mergeProfessionalPlayerIdentities,
  parseProfessionalPlayerCatalog,
  resolveProfessionalPlayerFromCatalog,
  type ProfessionalPlayerIdentity,
} from "./professionalPlayersCatalog";
import { parseProSteamIdCatalogJsonl, resolveProSteamIdFromCatalog } from "./proSteamIdCatalog";
const catalog = parseProfessionalPlayerCatalog(JSON.parse(catalogSource));
const registryCatalog = parseProSteamIdCatalogJsonl(registrySource);

export function resolveProfessionalPlayer(steamId: string): ProfessionalPlayerIdentity | null {
  return mergeProfessionalPlayerIdentities(
    resolveProfessionalPlayerFromCatalog(catalog, steamId),
    resolveProSteamIdFromCatalog(registryCatalog, steamId),
    registryCatalog,
  );
}
