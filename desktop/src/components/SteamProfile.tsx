import { invoke } from "@tauri-apps/api/core";
import { useEffect, useMemo, useState } from "react";
import type { SteamProfile } from "../types";
import "./steam-profile.css";

export type SteamProfileMap = ReadonlyMap<string, SteamProfile>;

export function useSteamProfiles(steamIds: string[]): SteamProfileMap {
  const requestKey = useMemo(() => [...new Set(steamIds.filter((steamId) => /^[1-9]\d{16}$/.test(steamId)))].sort().join(","), [steamIds]);
  const [profiles, setProfiles] = useState<SteamProfileMap>(() => new Map());

  useEffect(() => {
    const requested = requestKey ? requestKey.split(",") : [];
    if (requested.length === 0) {
      setProfiles(new Map());
      return undefined;
    }

    let active = true;
    void invoke<SteamProfile[]>("load_steam_profiles", { steamIds: requested })
      .then((next) => {
        if (active) setProfiles(new Map(next.map((profile) => [profile.steamId, profile])));
      })
      .catch(() => {
        if (active) setProfiles(new Map());
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

export function SteamAvatar({
  profile,
  fallbackName,
  size = "normal",
}: {
  profile?: SteamProfile;
  fallbackName: string;
  size?: "compact" | "normal" | "large";
}) {
  const [failed, setFailed] = useState(false);
  const initial = Array.from(fallbackName.trim())[0]?.toLocaleUpperCase() || "?";

  useEffect(() => setFailed(false), [profile?.avatarUrl]);

  return (
    <span className={`steam-avatar is-${size}`} title={profile?.personaName} aria-hidden="true">
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
  className = "",
}: {
  profile?: SteamProfile;
  demoName: string;
  steamId: string;
  className?: string;
}) {
  const alias = currentSteamAlias(profile, demoName);
  return (
    <span className={`steam-player-identity ${className}`.trim()}>
      <SteamAvatar profile={profile} fallbackName={demoName} />
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
