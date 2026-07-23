export interface ReplayRetentionOrders {
  a: string[];
  b: string[];
}

const STEAM_ID64_PATTERN = /^[1-9]\d{16}$/;

export function canPrioritizeReplayRoster(steamIds: readonly string[]): boolean {
  return steamIds.length > 1
    && steamIds.every((steamId) => STEAM_ID64_PATTERN.test(steamId))
    && new Set(steamIds).size === steamIds.length;
}

export function normalizeReplayRetentionOrder(
  steamIds: readonly string[],
  preferred: readonly string[] | null | undefined,
): string[] {
  const defaults = [...steamIds];
  if (!canPrioritizeReplayRoster(defaults) || !preferred || preferred.length !== defaults.length) {
    return defaults;
  }
  const expected = new Set(defaults);
  return new Set(preferred).size === expected.size && preferred.every((steamId) => expected.has(steamId))
    ? [...preferred]
    : defaults;
}

export function moveReplayRetentionPlayer(
  order: readonly string[],
  fromIndex: number,
  toIndex: number,
): string[] {
  if (fromIndex === toIndex
    || fromIndex < 0
    || toIndex < 0
    || fromIndex >= order.length
    || toIndex >= order.length) {
    return [...order];
  }
  const next = [...order];
  const [moved] = next.splice(fromIndex, 1);
  next.splice(toIndex, 0, moved);
  return next;
}

export function orderReplayRoster<T extends { steamId: string }>(
  players: readonly T[],
  preferred: readonly string[] | null | undefined,
): T[] {
  const order = normalizeReplayRetentionOrder(players.map((player) => player.steamId), preferred);
  const rank = new Map(order.map((steamId, index) => [steamId, index]));
  return [...players].sort((left, right) =>
    (rank.get(left.steamId) ?? Number.MAX_SAFE_INTEGER)
    - (rank.get(right.steamId) ?? Number.MAX_SAFE_INTEGER));
}

export function buildReplayRetentionCommand(orders: ReplayRetentionOrders): string | null {
  const first = canPrioritizeReplayRoster(orders.a) ? orders.a : [];
  const second = canPrioritizeReplayRoster(orders.b) ? orders.b : [];
  if (first.length === 0 && second.length === 0) return null;
  const combined = [...first, ...second];
  if (new Set(combined).size !== combined.length) return null;
  return `dtr_retain ${first.join(",") || "-"} ${second.join(",") || "-"}`;
}

export function replayRetentionStorageKey(archiveIdentity: string): string {
  return `demotracer:replay-retention:v1:${archiveIdentity.trim().toLocaleLowerCase()}`;
}
