import { invoke } from "@tauri-apps/api/core";
import { useEffect, useMemo, useState, type CSSProperties } from "react";
import type { SteamProfile } from "../types";
import "./steam-profile.css";

export type SteamProfileMap = ReadonlyMap<string, SteamProfile>;

const memoryProfileCache = new Map<string, SteamProfile>();
const pendingProfileRequests = new Map<string, Promise<SteamProfile[]>>();
const demoPlayerColors: Readonly<Record<string, string>> = {
  blue: "#62a8f5",
  green: "#69bd5b",
  yellow: "#e9c849",
  orange: "#e58a3b",
  purple: "#b878df",
};

export function demoPlayerColorValue(value: string | null | undefined): string | undefined {
  return value ? demoPlayerColors[value.trim().toLowerCase()] : undefined;
}

function cachedProfileMap(steamIds: string[]): Map<string, SteamProfile> {
  return new Map(steamIds.flatMap((steamId) => {
    const profile = memoryProfileCache.get(steamId);
    return profile ? [[steamId, profile] as const] : [];
  }));
}

function requestProfiles(steamIds: string[]): Promise<SteamProfile[]> {
  const requestKey = steamIds.join(",");
  const pending = pendingProfileRequests.get(requestKey);
  if (pending) return pending;
  const request = invoke<SteamProfile[]>("load_steam_profiles", { steamIds })
    .then((profiles) => {
      profiles.forEach((profile) => memoryProfileCache.set(profile.steamId, profile));
      return profiles;
    })
    .finally(() => pendingProfileRequests.delete(requestKey));
  pendingProfileRequests.set(requestKey, request);
  return request;
}

export function useSteamProfiles(steamIds: string[]): SteamProfileMap {
  const requestKey = useMemo(() => [...new Set(steamIds.filter((steamId) => /^[1-9]\d{16}$/.test(steamId)))].sort().join(","), [steamIds]);
  const [profiles, setProfiles] = useState<SteamProfileMap>(() => cachedProfileMap(requestKey ? requestKey.split(",") : []));

  useEffect(() => {
    const requested = requestKey ? requestKey.split(",") : [];
    if (requested.length === 0) {
      setProfiles(new Map());
      return undefined;
    }

    const cached = cachedProfileMap(requested);
    setProfiles(cached);
    const missing = requested.filter((steamId) => !cached.has(steamId));
    if (missing.length === 0) return undefined;

    let active = true;
    void requestProfiles(missing)
      .then(() => {
        if (active) setProfiles(cachedProfileMap(requested));
      })
      .catch(() => {
        if (active) setProfiles(cachedProfileMap(requested));
      });
    return () => {
      active = false;
    };
  }, [requestKey]);

  return profiles;
}

export function currentSteamAlias(profile: SteamProfile | undefined, demoName: string): string | null {
  const alias = profile?.personaName.trim();
  if (!alias || alias.localeCompare(demoName.trim(), undefined, { sensitivity: "base" }) === 0) return null;
  return alias;
}

export function teamRepresentative<T extends { name: string; steamId: string }>(teamName: string, players: T[]): T | undefined {
  const normalizedTeam = teamName.trim().toLocaleLowerCase().replace(/^team[\s_-]*/, "").replace(/[\s_-]+/g, "");
  return players.find((player) => player.name.toLocaleLowerCase().replace(/[\s_-]+/g, "") === normalizedTeam)
    ?? [...players].sort((left, right) => left.name.localeCompare(right.name))[0];
}

export function SteamAvatar({
  profile,
  fallbackName,
  playerColor,
  size = "normal",
}: {
  profile?: SteamProfile;
  fallbackName: string;
  playerColor?: string | null;
  size?: "compact" | "normal" | "hero" | "large";
}) {
  const [failed, setFailed] = useState(false);
  const initial = Array.from(fallbackName.trim())[0]?.toLocaleUpperCase() || "?";
  const accent = demoPlayerColorValue(playerColor);
  const avatarStyle = accent
    ? ({ "--steam-avatar-accent": accent } as CSSProperties)
    : undefined;

  useEffect(() => setFailed(false), [profile?.avatarUrl]);

  return (
    <span className={`steam-avatar is-${size}${accent ? " has-player-color" : ""}`} style={avatarStyle} title={profile?.personaName} aria-hidden="true">
      <span>{initial}</span>
      {profile && !failed ? (
        <img
          src={profile.avatarUrl}
          alt=""
          loading="lazy"
          draggable={false}
          referrerPolicy="no-referrer"
          onError={() => setFailed(true)}
        />
      ) : null}
    </span>
  );
}

export function SteamPlayerIdentity({
  profile,
  demoName,
  steamId,
  playerColor,
  className = "",
}: {
  profile?: SteamProfile;
  demoName: string;
  steamId: string;
  playerColor?: string | null;
  className?: string;
}) {
  const alias = currentSteamAlias(profile, demoName);
  return (
    <span className={`steam-player-identity ${className}`.trim()}>
      <SteamAvatar profile={profile} fallbackName={demoName} playerColor={playerColor} />
      <span className="steam-player-labels">
        <span className="steam-player-name-row">
          <strong title={demoName}>{demoName}</strong>
          {alias ? <small title={alias}>Steam · {alias}</small> : null}
        </span>
        <code title={`SteamID ${steamId}`}>{steamId}</code>
      </span>
    </span>
  );
}
