import type { Language } from "./types";

export interface ProfessionalPlayerIdentity {
  steamId: string;
  handle: string;
  realName: string;
  team: string;
  country: string;
  role: string;
  sourceUrl: string;
  verifiedAt: string;
}

const PLAYERS: Record<string, Omit<ProfessionalPlayerIdentity, "steamId" | "role"> & { role: Record<Language, string> }> = {
  "76561198386265483": {
    handle: "donk",
    realName: "Danil Kryshkovets",
    team: "Team Spirit",
    country: "Russia",
    role: { zh: "步枪手", en: "Rifler" },
    sourceUrl: "https://prosettings.net/players/donk/",
    verifiedAt: "2026-07-22",
  },
};

export function resolveProfessionalPlayer(steamId: string, language: Language): ProfessionalPlayerIdentity | null {
  const player = PLAYERS[steamId];
  return player ? { ...player, steamId, role: player.role[language] } : null;
}
