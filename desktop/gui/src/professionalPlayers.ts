/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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
