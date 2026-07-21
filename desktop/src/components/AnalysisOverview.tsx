import type { TextDictionary } from "../i18n";
import type { AnalysisPlayerSummary, AnalysisResult } from "../types";
import { displayMap, MapArtwork } from "./MapArtwork";
import { hasSteamProfile, steamProfileUrl, type PlayerSelection } from "./PlayerRoster";
import { SteamPlayerIdentity, type SteamProfileMap } from "./SteamProfile";
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

function playerMetrics(player: AnalysisPlayerSummary, matchRounds: number | null = null) {
  const kills = player.kills;
  const deaths = player.deaths;
  const assists = player.assists;
  const headshots = player.details?.headshotKills;
  const damage = player.details?.totalDamage;
  const rounds = player.details?.statsRounds ?? matchRounds;
  const validRounds = rounds !== null && rounds > 0 ? rounds : null;
  return {
    kills,
    deaths,
    assists,
    score: player.score,
    adr: damage !== null && damage !== undefined && validRounds !== null ? (damage / validRounds).toFixed(1) : null,
    kd: kills !== null && kills !== undefined && deaths !== null && deaths !== undefined && deaths > 0 ? (kills / deaths).toFixed(2) : null,
    kr: kills !== null && kills !== undefined && validRounds !== null ? (kills / validRounds).toFixed(2) : null,
    hs: kills !== null && kills !== undefined && kills > 0 && headshots !== null && headshots !== undefined && headshots <= kills
      ? `${(headshots / kills * 100).toFixed(1)}% · ${headshots}`
      : null,
    mvps: player.mvps,
  };
}

type PlayerMetrics = ReturnType<typeof playerMetrics>;

interface AnalysisMetricColumn {
  key: "kda" | "adr" | "kd" | "kr" | "hs" | "mvps" | "score";
  label: string;
  width: string;
  value: (metrics: PlayerMetrics) => string | null;
}

function metricValue(value: number | string | null | undefined): string | null {
  return value === null || value === undefined ? null : String(value);
}

function analysisMetricColumns(players: AnalysisPlayerSummary[], words: TextDictionary, matchRounds: number | null): AnalysisMetricColumn[] {
  const metrics = players.map((player) => playerMetrics(player, matchRounds));
  const stat = (value: number | null | undefined) => value === null || value === undefined ? "—" : String(value);
  const columns: AnalysisMetricColumn[] = [
    {
      key: "kda",
      label: words.kda,
      width: "minmax(78px, .9fr)",
      value: (values) => (values.kills === null || values.kills === undefined)
        && (values.deaths === null || values.deaths === undefined)
        && (values.assists === null || values.assists === undefined)
        ? null
        : `${stat(values.kills)} / ${stat(values.deaths)} / ${stat(values.assists)}`,
    },
    { key: "adr", label: words.adr, width: "minmax(45px, .55fr)", value: (values) => metricValue(values.adr) },
    { key: "kd", label: words.kd, width: "minmax(45px, .55fr)", value: (values) => metricValue(values.kd) },
    { key: "kr", label: "KPR", width: "minmax(45px, .55fr)", value: (values) => metricValue(values.kr) },
    { key: "hs", label: "HS%", width: "minmax(70px, .75fr)", value: (values) => metricValue(values.hs) },
    { key: "mvps", label: "MVP", width: "minmax(44px, .5fr)", value: (values) => metricValue(values.mvps) },
    { key: "score", label: words.scoreColumn, width: "minmax(48px, .55fr)", value: (values) => metricValue(values.score) },
  ];
  return columns.filter((column) => metrics.some((values) => column.value(values) !== null));
}

function AnalysisTeamRows({
  teamId,
  name,
  score,
  players,
  columns,
  matchRounds,
  startSideLabel,
  steamProfiles,
  words,
  onSelectPlayer,
  onOpenExternal,
}: {
  teamId: string;
  name: string;
  score?: number;
  players: AnalysisPlayerSummary[];
  columns: AnalysisMetricColumn[];
  matchRounds: number | null;
  startSideLabel?: string;
  steamProfiles: SteamProfileMap;
  words: TextDictionary;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onOpenExternal: (url: string) => void;
}) {
  const gridTemplateColumns = `minmax(190px, 1.8fr) ${columns.map((column) => column.width).join(" ")}`;
  return (
    <section className={`analysis-team-block is-team-${teamId}`} aria-label={name}>
      <header className="analysis-team-heading">
        <strong title={name}>{name}</strong>
        {startSideLabel ? <em>{startSideLabel}</em> : null}
        <span>{words.rosterPlayerCount.replace("{count}", String(players.length))}</span>
        {score !== undefined ? <b>{score}</b> : null}
      </header>
      <ul>
        {players.map((player, playerIndex) => {
          const metrics = playerMetrics(player, matchRounds);
          const selection = { teamId, playerIndex };
          const profileAvailable = hasSteamProfile(player.steamId);
          return (
            <li key={`${player.steamId}:${playerIndex}`}>
              <button
                className="analysis-player-stat-row"
                type="button"
                style={{ gridTemplateColumns }}
                title={profileAvailable ? words.rosterPlayerHint : words.playerAnalysis}
                onClick={() => onSelectPlayer(selection)}
                onContextMenu={(event) => {
                  if (!profileAvailable) return;
                  event.preventDefault();
                  onOpenExternal(steamProfileUrl(player.steamId));
                }}
              >
                <SteamPlayerIdentity
                  className="analysis-player-identity"
                  profile={steamProfiles.get(player.steamId)}
                  demoName={player.name}
                  steamId={player.steamId}
                />
                {columns.map((column) => (
                  <span className={`analysis-stat-value is-${column.key}`} key={column.key}>
                    {column.value(metrics) ?? "—"}
                  </span>
                ))}
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

export function AnalysisOverview({
  analysis,
  words,
  steamProfiles,
  onSelectPlayer,
  onOpenExternal,
}: {
  analysis: AnalysisResult;
  words: TextDictionary;
  steamProfiles: SteamProfileMap;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onOpenExternal: (url: string) => void;
}) {
  const { teamAName, teamBName, sortedPlayers, teamA, teamB, unassigned } = analysisRoster(analysis, words);
  const matchRounds = analysis.score
    ? analysis.score.teamA.score + analysis.score.teamB.score
    : null;
  const metricColumns = analysisMetricColumns(sortedPlayers, words, matchRounds);
  const metricGrid = `minmax(190px, 1.8fr) ${metricColumns.map((column) => column.width).join(" ")}`;
  const hasStartingSideEvidence = teamA.length > 0 && teamB.length > 0;

  return (
    <div className="analysis-overview">
      <section className="archive-match-hero">
        <div className="archive-map-panel">
          <MapArtwork map={analysis.map} loading="eager" />
          <div><span>{words.map}</span><strong>{displayMap(analysis.map)}</strong></div>
        </div>
        <div className="archive-match-summary">
          <div className={`archive-scoreboard is-${analysis.score?.status || "unknown"}`}>
            <div className="archive-score-team is-team-a">
              <strong title={teamAName}>{teamAName}</strong>
              {hasStartingSideEvidence ? <small>{words.startsAsT}</small> : null}
            </div>
            <div className="archive-scoreline" aria-label={analysis.score ? `${teamAName} ${analysis.score.teamA.score} : ${analysis.score.teamB.score} ${teamBName}` : words.scoreUnavailable}>
              <span className="archive-score-numbers">
                {analysis.score
                  ? <><b>{analysis.score.teamA.score}</b><i>:</i><b>{analysis.score.teamB.score}</b></>
                  : <em>— : —</em>}
              </span>
              {analysis.score?.status === "completed" ? <small>{words.scoreAtDemoEnd}</small> : null}
            </div>
            <div className="archive-score-team is-team-b">
              <strong title={teamBName}>{teamBName}</strong>
              {hasStartingSideEvidence ? <small>{words.startsAsCt}</small> : null}
            </div>
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
        <section className="analysis-scoreboard" aria-labelledby="analysis-scoreboard-title">
          <header><h2 id="analysis-scoreboard-title">{words.matchRoster}</h2><span>{words.rosterPlayerCount.replace("{count}", String(sortedPlayers.length))}</span></header>
          <div className="analysis-scoreboard-columns" style={{ gridTemplateColumns: metricGrid }}>
            <span>{words.playerColumn}</span>
            {metricColumns.map((column) => <span key={column.key}>{column.label}</span>)}
          </div>
          <div className="analysis-scoreboard-teams">
            <AnalysisTeamRows teamId="a" name={teamAName} score={analysis.score?.teamA.score} players={teamA} columns={metricColumns} matchRounds={matchRounds} startSideLabel={hasStartingSideEvidence ? words.startsAsT : undefined} steamProfiles={steamProfiles} words={words} onSelectPlayer={onSelectPlayer} onOpenExternal={onOpenExternal} />
            <AnalysisTeamRows teamId="b" name={teamBName} score={analysis.score?.teamB.score} players={teamB} columns={metricColumns} matchRounds={matchRounds} startSideLabel={hasStartingSideEvidence ? words.startsAsCt : undefined} steamProfiles={steamProfiles} words={words} onSelectPlayer={onSelectPlayer} onOpenExternal={onOpenExternal} />
            {unassigned.length > 0 ? <AnalysisTeamRows teamId="unknown" name={words.unassignedPlayers} players={unassigned} columns={metricColumns} matchRounds={matchRounds} steamProfiles={steamProfiles} words={words} onSelectPlayer={onSelectPlayer} onOpenExternal={onOpenExternal} /> : null}
          </div>
        </section>
      ) : null}
    </div>
  );
}
