import { useEffect, useRef } from "react";
import { ArrowIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { Language } from "../types";
import {
  PlayerDossier,
  playerSelectionKey,
  type PlayerSelection,
  type RosterPlayer,
} from "./PlayerRoster";
import type { CopyTarget } from "./TaskViews";
import { currentSteamAlias, SteamAvatar, type SteamProfileMap } from "./SteamProfile";
import "./archive-workspace.css";
import "./player-analysis.css";

export interface PlayerAnalysisTeam {
  id: string;
  name: string;
  players: RosterPlayer[];
  startSideLabel?: string;
}

interface PlayerAnalysisWorkspaceProps {
  words: TextDictionary;
  language: Language;
  teams: PlayerAnalysisTeam[];
  steamProfiles: SteamProfileMap;
  selectedPlayer: PlayerSelection;
  copiedTarget: CopyTarget | null;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onBack: () => void;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
}

function playerKda(player: RosterPlayer): string | null {
  const values = [player.kills, player.deaths, player.assists];
  return values.some((value) => value !== null && value !== undefined)
    ? values.map((value) => value ?? "—").join(" / ")
    : null;
}

export function PlayerAnalysisWorkspace({
  words,
  language,
  teams,
  steamProfiles,
  selectedPlayer,
  copiedTarget,
  onSelectPlayer,
  onBack,
  onCopy,
  onOpenExternal,
}: PlayerAnalysisWorkspaceProps) {
  const entries = teams.flatMap((team) => team.players.map((player, playerIndex) => {
    const selection = { teamId: team.id, playerIndex };
    return { team, player, selection, key: playerSelectionKey(selection) };
  }));
  const selectedKey = playerSelectionKey(selectedPlayer);
  const selectedEntry = entries.find((entry) => entry.key === selectedKey);
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    headingRef.current?.focus();
  }, [selectedEntry?.key, selectedKey]);

  if (!selectedEntry) {
    return (
      <section className="player-analysis-workspace" aria-labelledby="player-analysis-title">
        <header className="player-analysis-toolbar">
          <button className="player-analysis-back" type="button" onClick={onBack}>
            <ArrowIcon size={16} />
            <span>{words.backToMatch}</span>
          </button>
          <div>
            <span>{words.playerAnalysis}</span>
            <strong>{words.playerAnalysisUnavailable}</strong>
          </div>
        </header>
        <div className="player-analysis-scroll">
          <article className="player-analysis-main player-analysis-empty">
            <header className="player-analysis-heading">
              <h1 id="player-analysis-title" ref={headingRef} tabIndex={-1}>{words.playerAnalysis}</h1>
              <p>{words.playerAnalysisUnavailable}</p>
            </header>
          </article>
        </div>
      </section>
    );
  }

  const { player, team } = selectedEntry;
  const steamProfile = steamProfiles.get(player.steamId);
  const steamAlias = currentSteamAlias(steamProfile, player.name);

  return (
    <section className="player-analysis-workspace" aria-labelledby="player-analysis-title">
      <header className="player-analysis-toolbar">
        <button className="player-analysis-back" type="button" onClick={onBack}>
          <ArrowIcon size={16} />
          <span>{words.backToMatch}</span>
        </button>
        <div>
          <span>{words.playerAnalysis}</span>
          <strong title={player.name}>{player.name}</strong>
        </div>
      </header>

      <div className="player-analysis-scroll">
        <div className="player-analysis-layout">
          <aside className="player-analysis-index" aria-labelledby="player-analysis-index-title">
            <header>
              <h2 id="player-analysis-index-title">{words.choosePlayer}</h2>
              <select
                className="player-analysis-select"
                aria-label={words.choosePlayer}
                value={selectedEntry.key}
                onChange={(event) => {
                  const next = entries.find((entry) => entry.key === event.target.value);
                  if (next) onSelectPlayer(next.selection);
                }}
              >
                {teams.map((team) => (
                  <optgroup label={team.name} key={team.id}>
                    {team.players.map((optionPlayer, playerIndex) => {
                      const option = { teamId: team.id, playerIndex };
                      const optionKey = playerSelectionKey(option);
                      return <option value={optionKey} key={optionKey}>{optionPlayer.name}</option>;
                    })}
                  </optgroup>
                ))}
              </select>
            </header>
            <nav aria-label={words.choosePlayer}>
              {teams.map((team) => (
                <section className="player-analysis-team" aria-labelledby={`player-analysis-team-${team.id}`} key={team.id}>
                  <h3 id={`player-analysis-team-${team.id}`}>{team.name}</h3>
                  <div className="player-analysis-team-list">
                    {team.players.map((teamPlayer, playerIndex) => {
                      const selection = { teamId: team.id, playerIndex };
                      const entryKey = playerSelectionKey(selection);
                      const selected = entryKey === selectedEntry.key;
                      const kda = playerKda(teamPlayer);
                      return (
                        <button
                          className={selected ? "is-selected" : ""}
                          type="button"
                          aria-current={selected ? "page" : undefined}
                          onClick={() => onSelectPlayer(selection)}
                          key={entryKey}
                        >
                          <span className="player-analysis-index-identity">
                            <SteamAvatar profile={steamProfiles.get(teamPlayer.steamId)} fallbackName={teamPlayer.name} size="compact" />
                            <strong title={teamPlayer.name}>{teamPlayer.name}</strong>
                          </span>
                          {kda ? <span aria-label={`${words.kda} ${kda}`}>{kda}</span> : null}
                        </button>
                      );
                    })}
                  </div>
                </section>
              ))}
            </nav>
          </aside>

          <article className="player-analysis-main" aria-labelledby="player-analysis-title">
            <header className="player-analysis-heading">
              <SteamAvatar profile={steamProfile} fallbackName={player.name} size="large" />
              <div>
                <span>{team.name}{team.startSideLabel ? ` · ${team.startSideLabel}` : ""}</span>
                <h1 id="player-analysis-title" ref={headingRef} tabIndex={-1}>{player.name}</h1>
                {steamAlias ? <p>Steam · {steamAlias}</p> : null}
              </div>
            </header>

            <div className="player-analysis-dossier">
              <PlayerDossier
                key={selectedEntry.key}
                playerKey={selectedEntry.key}
                player={player}
                language={language}
                words={words}
                copiedTarget={copiedTarget}
                onCopy={onCopy}
                onOpenExternal={onOpenExternal}
              />
            </div>
          </article>
        </div>
      </div>
    </section>
  );
}
