/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

interface OpeningSidePlayer {
  steamId: string;
}

interface OpeningSideRound {
  round: number;
  tSteamIds?: readonly string[];
  ctSteamIds?: readonly string[];
}

export function rosterOpeningSide(
  players: readonly OpeningSidePlayer[],
  rounds: readonly OpeningSideRound[],
): "t" | "ct" | null {
  const steamIds = new Set(players.map((player) => player.steamId).filter(Boolean));
  if (steamIds.size === 0) return null;

  const orderedRounds = [...rounds].sort((left, right) => left.round - right.round);
  for (const round of orderedRounds) {
    const tMatches = (round.tSteamIds ?? []).filter((steamId) => steamIds.has(steamId)).length;
    const ctMatches = (round.ctSteamIds ?? []).filter((steamId) => steamIds.has(steamId)).length;
    if (tMatches > ctMatches) return "t";
    if (ctMatches > tMatches) return "ct";
  }
  return null;
}
