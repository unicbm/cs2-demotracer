import { AlertIcon, ArrowIcon, CheckIcon, ChevronIcon, CopyIcon, FolderIcon, RefreshIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConversionSummary, Language, ManifestArchive, ManifestArchiveRound, PlayerSummary } from "../types";
import { displayMap, MapArtwork, mapArtworkStyle } from "./MapArtwork";
import { PlaybackCommandBuilder, type PlaybackPresetOptions } from "./PlaybackCommandBuilder";
import { RosterTeam } from "./PlayerRoster";
import type { CommandMode, CopyTarget } from "./TaskViews";
import "./archive-workspace.css";

interface ArchiveWorkspaceProps {
  words: TextDictionary;
  language: Language;
  archive: ManifestArchive;
  busy: boolean;
  selectedRound: number;
  commandMode: CommandMode;
  playbackPreset: PlaybackPresetOptions;
  copiedTarget: CopyTarget | null;
  onSelectRound: (round: number) => void;
  onCommandModeChange: (mode: CommandMode) => void;
  onPlaybackPresetChange: (patch: Partial<PlaybackPresetOptions>) => void;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
  onOpenFolder: () => void;
  onReconvert: () => void;
  onChooseManifest: () => void;
  onClose: () => void;
}

function fileName(path: string): string {
  const parts = path.split(/[\\/]/);
  return parts.at(-1) || path;
}

function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return "—";
  const totalSeconds = Math.max(0, Math.round(seconds));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainder = totalSeconds % 60;
  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
  }
  return `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function formatBytes(value: number | string): string {
  const bytes = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return String(value);
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let amount = bytes / 1024;
  let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) {
    amount /= 1024;
    unit += 1;
  }
  return `${amount >= 10 ? amount.toFixed(1) : amount.toFixed(2)} ${units[unit]}`;
}

function formatTickRate(tickRate: number): string {
  return Number.isInteger(tickRate) ? String(tickRate) : tickRate.toFixed(2);
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

function cleanTeamName(value: string | null | undefined): string | null {
  const name = value?.trim();
  if (!name) return null;
  const normalized = name.toLowerCase().replace(/[\s_-]+/g, "");
  return ["t", "ct", "terrorist", "terrorists", "counterterrorist", "counterterrorists"].includes(normalized)
    ? null
    : name;
}

function teamNameFromPlayers(archive: ManifestArchive, identity: "a" | "b"): string | null {
  const counts = new Map<string, { name: string; count: number }>();
  for (const player of archive.players) {
    if (player.matchTeam?.toLowerCase() !== identity) continue;
    const name = cleanTeamName(player.teamName);
    if (!name) continue;
    const key = name.toLocaleLowerCase();
    const current = counts.get(key);
    counts.set(key, { name, count: (current?.count ?? 0) + 1 });
  }
  return [...counts.values()].sort((left, right) => right.count - left.count)[0]?.name ?? null;
}

function sameIdentityName(left: string, right: string): boolean {
  return left.localeCompare(right, undefined, { sensitivity: "base" }) === 0;
}

function playerMatchIdentity(
  player: PlayerSummary,
  teamAName: string,
  teamBName: string,
): "a" | "b" | null {
  const explicit = player.matchTeam?.trim().toLowerCase();
  if (explicit === "a" || explicit === "b") return explicit;

  const teamName = cleanTeamName(player.teamName);
  if (!teamName) return null;
  if (sameIdentityName(teamName, teamAName)) return "a";
  if (sameIdentityName(teamName, teamBName)) return "b";
  return null;
}

function roundStableScore(
  round: ManifestArchiveRound,
  teamAName: string,
  teamBName: string,
): [number, number] | null {
  const scoreboard = round.scoreboard;
  const first = scoreboard?.tTeamName?.trim();
  const second = scoreboard?.ctTeamName?.trim();
  if (!scoreboard || !first || !second) return null;
  const same = (left: string, right: string) => left.localeCompare(right, undefined, { sensitivity: "base" }) === 0;
  if (same(first, teamAName) && same(second, teamBName)) return [scoreboard.tScore, scoreboard.ctScore];
  if (same(first, teamBName) && same(second, teamAName)) return [scoreboard.ctScore, scoreboard.tScore];
  return null;
}

function adaptArchiveResult(
  archive: ManifestArchive,
  selected: ManifestArchiveRound,
  playableRounds: ManifestArchiveRound[],
  commandRounds: ManifestArchiveRound[],
): ConversionSummary {
  return {
    root: archive.root,
    manifestPath: archive.manifestPath,
    filesWritten: archive.playableFiles,
    validatedFiles: archive.playableFiles,
    outputBytes: archive.outputBytes,
    roundsExported: playableRounds.length,
    firstExportedRound: selected.round,
    rounds: playableRounds.map((round) => ({ round: round.round, files: round.files })),
    players: archive.players,
    voice: {
      requested: archive.voice.sidecars > 0 ? true : archive.voice.requested,
      sidecars: commandRounds.filter((round) => archive.voice.rounds.includes(round.round)).length,
    },
    cosmetics: {
      requested: archive.cosmetics.files > 0 ? true : archive.cosmetics.requested,
      stickerRequested: archive.cosmetics.stickerFiles > 0 ? true : archive.cosmetics.stickerRequested,
      charmRequested: archive.cosmetics.charmFiles > 0 ? true : archive.cosmetics.charmRequested,
      files: commandRounds.reduce((sum, round) => sum + round.cosmeticFiles, 0),
      stickerFiles: commandRounds.reduce((sum, round) => sum + round.stickerFiles, 0),
      charmFiles: commandRounds.reduce((sum, round) => sum + round.charmFiles, 0),
      preset: archive.cosmetics.preset,
    },
    commands: selected.commands,
  };
}

function ArchiveIssues({ archive, words }: { archive: ManifestArchive; words: TextDictionary }) {
  if (archive.issues.length === 0) return null;
  return (
    <details className="archive-issues">
      <summary>
        <span><AlertIcon size={15} />{words.archiveIssues}</span>
        <strong>{archive.issues.length}</strong>
        <ChevronIcon size={15} />
      </summary>
      <ul>
        {archive.issues.map((issue, index) => (
          <li className={`is-${issue.severity}`} key={`${issue.code}-${issue.round ?? "all"}-${index}`}>
            {issue.round !== undefined && issue.round !== null ? <b>Round {issue.round}</b> : null}
            <span>{issue.message}</span>
          </li>
        ))}
      </ul>
    </details>
  );
}

export function ArchiveWorkspace({
  words,
  language,
  archive,
  busy,
  selectedRound,
  commandMode,
  playbackPreset,
  copiedTarget,
  onSelectRound,
  onCommandModeChange,
  onPlaybackPresetChange,
  onCopy,
  onOpenExternal,
  onOpenFolder,
  onReconvert,
  onChooseManifest,
  onClose,
}: ArchiveWorkspaceProps) {
  const playableRounds = archive.rounds.filter((round) => round.available);
  const selected = playableRounds.find((round) => round.round === selectedRound) ?? playableRounds[0];
  const selectedIndex = selected
    ? playableRounds.findIndex((round) => round.round === selected.round)
    : -1;
  const sequenceDisabled = Boolean(selected && selected.sequenceLength === 0);
  const effectiveCommandMode: CommandMode = playableRounds.length <= 1 || sequenceDisabled ? "round" : commandMode;
  const sequenceCount = selected?.sequenceLength ?? 0;
  const commandRounds = selected
    ? effectiveCommandMode === "sequence"
      ? playableRounds.slice(selectedIndex, selectedIndex + sequenceCount)
      : [selected]
    : [];
  const result = selected ? adaptArchiveResult(archive, selected, playableRounds, commandRounds) : null;
  const archiveTitle = archive.displayName || fileName(archive.demoPath) || archive.demoId;
  const teamAName = cleanTeamName(archive.score?.teamA.name) || teamNameFromPlayers(archive, "a") || words.teamA;
  const teamBName = cleanTeamName(archive.score?.teamB.name) || teamNameFromPlayers(archive, "b") || words.teamB;
  const expectedPlayers = Math.max(0, ...playableRounds.map((round) => round.files));
  const rosterPlayers = [...archive.players].sort((left, right) => left.name.localeCompare(right.name));
  const teamARoster = rosterPlayers.filter((player) => playerMatchIdentity(player, teamAName, teamBName) === "a");
  const teamBRoster = rosterPlayers.filter((player) => playerMatchIdentity(player, teamAName, teamBName) === "b");
  const unassignedRoster = rosterPlayers.filter((player) => playerMatchIdentity(player, teamAName, teamBName) === null);

  return (
    <section className="archive-workspace" aria-labelledby="archive-workspace-title" style={mapArtworkStyle(archive.map)}>
      <header className="archive-toolbar">
        <button className="archive-back-button" type="button" onClick={onClose}>
          <ArrowIcon size={15} />{words.backToLibrary}
        </button>
        <div className="archive-toolbar-title">
          <span>{words.preparePlayback}</span>
          <h1 id="archive-workspace-title" title={archive.sourcePath || archive.demoPath} tabIndex={-1}>{archiveTitle}</h1>
        </div>
        <div className="archive-toolbar-actions">
          <button className="quiet-button" type="button" onClick={onReconvert} disabled={busy} title={words.reconvertArchiveHelp}>
            <RefreshIcon size={15} />{busy ? words.readingSourceDemo : words.reconvertArchive}
          </button>
          <button className="quiet-button archive-open-folder" type="button" onClick={onOpenFolder} disabled={busy}>
            <FolderIcon size={15} />{words.openFolder}
          </button>
        </div>
      </header>

      <section className="archive-match-hero">
        <div className="archive-map-panel">
          <MapArtwork map={archive.map} loading="eager" />
          <div><span>{words.map}</span><strong>{displayMap(archive.map)}</strong></div>
        </div>
        <div className="archive-match-summary">
          <div className={`archive-scoreboard is-${archive.score?.status || "unknown"}`}>
            <strong title={teamAName}>{teamAName}</strong>
            <div aria-label={archive.score ? `${teamAName} ${archive.score.teamA.score} : ${archive.score.teamB.score} ${teamBName}` : words.scoreUnavailable}>
              <span className="archive-score-numbers">
                {archive.score ? <><b>{archive.score.teamA.score}</b><i>:</i><b>{archive.score.teamB.score}</b></> : <em>— : —</em>}
              </span>
              {archive.score?.status === "completed" ? <small>{words.scoreAtDemoEnd}</small> : null}
            </div>
            <strong title={teamBName}>{teamBName}</strong>
          </div>
          <dl className="archive-match-facts">
            <div><dt>{words.demoSource}</dt><dd>{archive.demoSource ? platformName(archive.demoSource.name) : "—"}</dd></div>
            <div><dt>{words.demoFileTime}</dt><dd>{formatDate(archive.sourceModifiedAtMs)}</dd></div>
            <div><dt>{words.demoDuration}</dt><dd>{formatDuration(archive.durationSeconds)}</dd></div>
            <div><dt>{words.playableRounds}</dt><dd>{playableRounds.length}</dd></div>
          </dl>
        </div>
      </section>

      {rosterPlayers.length > 0 ? (
        <details className="archive-roster" open>
          <summary>
            <span>
              <strong>{words.matchRoster}</strong>
              <small>{words.matchRosterHelp}</small>
            </span>
            <b>{words.rosterPlayerCount.replace("{count}", String(rosterPlayers.length))}</b>
            <ChevronIcon size={15} />
          </summary>
          <div className="archive-roster-grid">
            <RosterTeam name={teamAName} players={teamARoster} language={language} words={words} countLabel={words.rosterPlayerCount} copiedTarget={copiedTarget} onCopy={onCopy} onOpenExternal={onOpenExternal} />
            <RosterTeam name={teamBName} players={teamBRoster} language={language} words={words} countLabel={words.rosterPlayerCount} className="is-team-b" copiedTarget={copiedTarget} onCopy={onCopy} onOpenExternal={onOpenExternal} />
            {unassignedRoster.length > 0 ? (
              <RosterTeam name={words.unassignedPlayers} players={unassignedRoster} language={language} words={words} countLabel={words.rosterPlayerCount} className="is-unassigned" copiedTarget={copiedTarget} onCopy={onCopy} onOpenExternal={onOpenExternal} />
            ) : null}
          </div>
        </details>
      ) : null}

      <div className="archive-split-view">
        <section className="archive-round-pane" aria-labelledby="archive-round-list-title">
          <header className="archive-pane-heading">
            <div>
              <span>{words.roundSelectionStep}</span>
              <h2 id="archive-round-list-title">{words.choosePlaybackStart}</h2>
              <p>{words.archiveRoundHint}</p>
            </div>
            <strong>{playableRounds.length}</strong>
          </header>

          <div className="archive-round-list" aria-label={words.choosePlaybackStart}>
            {archive.rounds.map((round) => {
              const active = selected?.round === round.round;
              const stableScore = roundStableScore(round, teamAName, teamBName);
              const playableIndex = playableRounds.findIndex((item) => item.round === round.round);
              const continuation = effectiveCommandMode === "sequence"
                && round.available
                && selectedIndex >= 0
                && playableIndex > selectedIndex
                && playableIndex < selectedIndex + sequenceCount;
              const incomplete = round.available && expectedPlayers > 0 && round.files < expectedPlayers;
              const state = active
                ? words.playbackStart
                : continuation
                  ? words.inPlaybackRange
                  : round.available
                    ? words.selectRoundAction
                    : words.unavailable;

              return (
                <button
                  className={[
                    "archive-round-option",
                    active ? "is-start" : "",
                    continuation ? "is-continuation" : "",
                    round.available ? "" : "is-unavailable",
                  ].filter(Boolean).join(" ")}
                  type="button"
                  aria-pressed={active}
                  disabled={!round.available}
                  key={round.round}
                  onClick={() => onSelectRound(round.round)}
                >
                  <span className="archive-round-number">R{round.round}</span>
                  <span className="archive-round-score" title={stableScore ? `${teamAName} / ${teamBName}` : undefined}>
                    {stableScore ? <><b>{stableScore[0]}</b><i>:</i><b>{stableScore[1]}</b></> : "— : —"}
                  </span>
                  <span className="archive-round-meta">
                    <b>{formatDuration(round.durationSeconds)}</b>
                    {round.pistolRound ? <small>{words.pistolRound}</small> : null}
                    {incomplete ? <small className="is-warning">{words.partialRoutes.replace("{count}", String(round.files)).replace("{total}", String(expectedPlayers))}</small> : null}
                  </span>
                  <strong className="archive-round-state">
                    {active ? <CheckIcon size={13} /> : null}{state}
                  </strong>
                </button>
              );
            })}
            {archive.rounds.length === 0 ? (
              <div className="archive-empty-state">
                <AlertIcon size={18} />
                <span>{words.noPlayableRounds}</span>
              </div>
            ) : null}
          </div>
        </section>

        <aside className="archive-playback-pane" aria-label={words.playDemoCommand}>
          {result && selected ? (
            <>
              <div className="archive-playback-context" role="status" aria-live="polite">
                <span>{words.currentPlayback}</span>
                <strong>{words.startFromRound.replace("{round}", String(selected.round))}</strong>
                <p>
                  {effectiveCommandMode === "sequence"
                    ? words.sequenceIncludes
                      .replace("{round}", String(selected.round))
                      .replace("{count}", String(sequenceCount))
                    : sequenceDisabled
                      ? words.sequenceUnavailable
                      : words.singleRoundSelected.replace("{round}", String(selected.round))}
                </p>
              </div>

              <PlaybackCommandBuilder
                words={words}
                result={result}
                options={playbackPreset}
                commandMode={effectiveCommandMode}
                sequenceDisabled={sequenceDisabled}
                copied={copiedTarget === "playback"}
                onOptionsChange={onPlaybackPresetChange}
                onCommandModeChange={onCommandModeChange}
                onCopy={(command) => onCopy(command, "playback")}
              />

              <ArchiveIssues archive={archive} words={words} />

              <details className="archive-file-info">
                <summary>{words.technicalDetails}<ChevronIcon size={14} /></summary>
                <div className="archive-file-info-content">
                  <div className="archive-manifest-row">
                    <span>{words.manifest}</span>
                    <code title={archive.manifestPath}>{archive.manifestPath}</code>
                    <button className="icon-button" type="button" aria-label={words.copyPath} title={words.copyPath} onClick={() => onCopy(archive.manifestPath, "manifest")}>
                      {copiedTarget === "manifest" ? <CheckIcon size={15} /> : <CopyIcon size={15} />}
                    </button>
                  </div>
                  <dl>
                    <div><dt>{words.source}</dt><dd title={archive.sourcePath || archive.demoPath}>{archive.sourcePath ? fileName(archive.sourcePath) : "—"}</dd></div>
                    <div><dt>{words.manifestFormat}</dt><dd>DTR v{archive.formatVersion}</dd></div>
                    <div><dt>{words.manifestAbi}</dt><dd>{archive.abi}</dd></div>
                    <div><dt>{words.tickRate}</dt><dd>{formatTickRate(archive.tickRate)}</dd></div>
                    <div><dt>{words.traceSize}</dt><dd>{formatBytes(archive.outputBytes)}</dd></div>
                  </dl>
                </div>
              </details>
            </>
          ) : (
            <div className="archive-no-playable">
              <AlertIcon size={20} />
              <strong>{words.noPlayableRounds}</strong>
              <button className="secondary-button" type="button" onClick={onChooseManifest}>
                {words.openAnotherArchive}
              </button>
              <ArchiveIssues archive={archive} words={words} />
            </div>
          )}
        </aside>
      </div>
    </section>
  );
}
