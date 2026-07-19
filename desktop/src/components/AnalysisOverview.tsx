import { ChevronIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { AnalysisPlayerSummary, AnalysisResult } from "../types";
import { displayMap, MapArtwork } from "./MapArtwork";
import { RosterTeam, type PlayerSelection } from "./PlayerRoster";
import "./analysis-overview.css";
import "./archive-workspace.css";

function cleanTeamName(value: string | null | undefined): string | null {
  const name = value?.trim();
  if (!name) return null;
  const normalized = name.toLowerCase().replace(/[\s_-]+/g, "");
  return ["t", "ct", "terrorist", "terrorists", "counterterrorist", "counterterrorists"].includes(normalized)
    ? null
    : name;
}

function teamNameFromPlayers(players: AnalysisPlayerSummary[], identity: "a" | "b"): string | null {
  const counts = new Map<string, { name: string; count: number }>();
  for (const player of players) {
    if (player.team.toLowerCase() !== identity) continue;
    const name = cleanTeamName(player.teamName);
    if (!name) continue;
    const key = name.toLocaleLowerCase();
    const current = counts.get(key);
    counts.set(key, { name, count: (current?.count ?? 0) + 1 });
  }
  return [...counts.values()].sort((left, right) => right.count - left.count)[0]?.name ?? null;
}

function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds)) return "—";
  const value = Math.max(0, Math.round(seconds));
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  const remainder = value % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
    : `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function formatDate(value: number | null | undefined): string {
  if (!value || !Number.isFinite(value)) return "—";
  return new Intl.DateTimeFormat(document.documentElement.lang || undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  }).format(new Date(value));
}

function platformName(value: string): string {
  return value.toLowerCase() === "faceit" ? "FACEIT" : value;
}

export function analysisRoster(analysis: AnalysisResult, words: TextDictionary) {
  const teamAName = cleanTeamName(analysis.score?.teamA.name)
    || teamNameFromPlayers(analysis.players, "a")
    || words.teamA;
  const teamBName = cleanTeamName(analysis.score?.teamB.name)
    || teamNameFromPlayers(analysis.players, "b")
    || words.teamB;
  const sortedPlayers = [...analysis.players].sort((left, right) => left.name.localeCompare(right.name));
  return {
    teamAName,
    teamBName,
    sortedPlayers,
    teamA: sortedPlayers.filter((player) => player.team.toLowerCase() === "a"),
    teamB: sortedPlayers.filter((player) => player.team.toLowerCase() === "b"),
    unassigned: sortedPlayers.filter((player) => !["a", "b"].includes(player.team.toLowerCase())),
  };
}

export function AnalysisOverview({
  analysis,
  words,
  onSelectPlayer,
  onOpenExternal,
}: {
  analysis: AnalysisResult;
  words: TextDictionary;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onOpenExternal: (url: string) => void;
}) {
  const { teamAName, teamBName, sortedPlayers, teamA, teamB, unassigned } = analysisRoster(analysis, words);

  return (
    <div className="analysis-overview">
      <section className="archive-match-hero">
        <div className="archive-map-panel">
          <MapArtwork map={analysis.map} loading="eager" />
          <div><span>{words.map}</span><strong>{displayMap(analysis.map)}</strong></div>
        </div>
        <div className="archive-match-summary">
          <div className={`archive-scoreboard is-${analysis.score?.status || "unknown"}`}>
            <strong title={teamAName}>{teamAName}</strong>
            <div aria-label={analysis.score ? `${teamAName} ${analysis.score.teamA.score} : ${analysis.score.teamB.score} ${teamBName}` : words.scoreUnavailable}>
              <span className="archive-score-numbers">
                {analysis.score
                  ? <><b>{analysis.score.teamA.score}</b><i>:</i><b>{analysis.score.teamB.score}</b></>
                  : <em>— : —</em>}
              </span>
              {analysis.score?.status === "completed" ? <small>{words.scoreAtDemoEnd}</small> : null}
            </div>
            <strong title={teamBName}>{teamBName}</strong>
          </div>
          <dl className="archive-match-facts analysis-match-facts">
            <div><dt>{words.demoSource}</dt><dd>{analysis.demoSource ? platformName(analysis.demoSource.name) : "—"}</dd></div>
            <div><dt>{words.demoServerName}</dt><dd title={analysis.serverName ?? undefined}>{analysis.serverName || "—"}</dd></div>
            <div><dt>{words.demoDuration}</dt><dd>{formatDuration(analysis.durationSeconds)}</dd></div>
            <div><dt>{words.playableRounds}</dt><dd>{analysis.rounds.length}</dd></div>
            <div><dt>{words.demoFileTime}</dt><dd>{formatDate(analysis.sourceModifiedAtMs)}</dd></div>
            <div><dt>{words.tickRate}</dt><dd>{Number.isInteger(analysis.tickRate) ? analysis.tickRate : analysis.tickRate.toFixed(2)}</dd></div>
          </dl>
        </div>
      </section>

      {sortedPlayers.length > 0 ? (
        <details className="archive-roster analysis-roster" open>
          <summary>
            <span><strong>{words.matchRoster}</strong><small>{words.matchRosterHelp}</small></span>
            <b>{words.rosterPlayerCount.replace("{count}", String(sortedPlayers.length))}</b>
            <ChevronIcon size={15} />
          </summary>
          <div className="archive-roster-grid">
            <RosterTeam teamId="a" name={teamAName} players={teamA} words={words} countLabel={words.rosterPlayerCount} onSelectPlayer={onSelectPlayer} onOpenExternal={onOpenExternal} />
            <RosterTeam teamId="b" name={teamBName} players={teamB} words={words} countLabel={words.rosterPlayerCount} className="is-team-b" onSelectPlayer={onSelectPlayer} onOpenExternal={onOpenExternal} />
            {unassigned.length > 0
              ? <RosterTeam teamId="unknown" name={words.unassignedPlayers} players={unassigned} words={words} countLabel={words.rosterPlayerCount} className="is-unassigned" onSelectPlayer={onSelectPlayer} onOpenExternal={onOpenExternal} />
              : null}
          </div>
        </details>
      ) : null}
    </div>
  );
}
