import { AlertIcon, CheckIcon, ChevronIcon, CloseIcon, CopyIcon, FolderIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConversionSummary, ManifestArchive, ManifestArchiveRound } from "../types";
import { PlaybackCommandBuilder, type PlaybackPresetOptions } from "./PlaybackCommandBuilder";
import type { CommandMode, CopyTarget } from "./TaskViews";
import "./archive-workspace.css";

interface ArchiveWorkspaceProps {
  words: TextDictionary;
  archive: ManifestArchive;
  selectedRound: number;
  commandMode: CommandMode;
  playbackPreset: PlaybackPresetOptions;
  copiedTarget: CopyTarget | null;
  onSelectRound: (round: number) => void;
  onCommandModeChange: (mode: CommandMode) => void;
  onPlaybackPresetChange: (patch: Partial<PlaybackPresetOptions>) => void;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenFolder: () => void;
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

function economyLabel(round: ManifestArchiveRound): string {
  const formatEconomy = (economy: ManifestArchiveRound["tEconomy"]): string => {
    if (!economy) return "—";
    const value = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 })
      .format(economy.roundStartEquipmentValue);
    return `${economy.class || "—"} · $${value}`;
  };
  const tEconomy = formatEconomy(round.tEconomy);
  const ctEconomy = formatEconomy(round.ctEconomy);
  return `T ${tEconomy} · CT ${ctEconomy}`;
}

function adaptArchiveResult(
  archive: ManifestArchive,
  selected: ManifestArchiveRound,
  playableRounds: ManifestArchiveRound[],
  voiceSidecars: number,
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
      requested: voiceSidecars > 0,
      sidecars: voiceSidecars,
    },
    cosmetics: archive.cosmetics,
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
  archive,
  selectedRound,
  commandMode,
  playbackPreset,
  copiedTarget,
  onSelectRound,
  onCommandModeChange,
  onPlaybackPresetChange,
  onCopy,
  onOpenFolder,
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
      ? playableRounds.slice(selectedIndex, selectedIndex + sequenceCount).map((round) => round.round)
      : [selected.round]
    : [];
  const voiceSidecars = archive.voice.rounds.filter((round) => commandRounds.includes(round)).length;
  const result = selected ? adaptArchiveResult(archive, selected, playableRounds, voiceSidecars) : null;
  const archiveTitle = fileName(archive.demoPath) || archive.demoId;

  return (
    <section className="archive-workspace" aria-labelledby="archive-workspace-title">
      <header className="archive-toolbar">
        <div className="archive-identity">
          <span>{words.archive}</span>
          <div className="archive-title-line">
            <h1 id="archive-workspace-title" title={archive.demoPath} tabIndex={-1}>{archiveTitle}</h1>
            <code title={archive.demoId}>{archive.demoId}</code>
          </div>
          <p>
            {words.archiveSummary
              .replace("{rounds}", String(playableRounds.length))
              .replace("{files}", String(archive.playableFiles))}
          </p>
        </div>

        <dl className="archive-metadata" aria-label={words.archive}>
          <div><dt>{words.map}</dt><dd>{archive.map || "—"}</dd></div>
          <div><dt>{words.tickRate}</dt><dd>{formatTickRate(archive.tickRate)}</dd></div>
          <div><dt>{words.manifestAbi}</dt><dd>{archive.abi}</dd></div>
          <div><dt>{words.manifestFormat}</dt><dd>v{archive.formatVersion}</dd></div>
        </dl>

        <div className="archive-toolbar-actions">
          <button className="secondary-button" type="button" onClick={onChooseManifest}>
            {words.changeManifest}
          </button>
          <button className="quiet-button" type="button" onClick={onOpenFolder}>
            <FolderIcon size={15} />
            {words.openFolder}
          </button>
          <button
            className="icon-button"
            type="button"
            aria-label={words.closeArchive}
            title={words.closeArchive}
            onClick={onClose}
          >
            <CloseIcon size={16} />
          </button>
        </div>
      </header>

      <div className="archive-manifest-strip">
        <span>{words.manifest}</span>
        <code title={archive.manifestPath}>{archive.manifestPath}</code>
        <button
          className="icon-button"
          type="button"
          aria-label={words.copyPath}
          title={words.copyPath}
          onClick={() => onCopy(archive.manifestPath, "manifest")}
        >
          {copiedTarget === "manifest" ? <CheckIcon size={15} /> : <CopyIcon size={15} />}
        </button>
      </div>

      <div className="archive-split-view">
        <section className="archive-round-pane" aria-labelledby="archive-round-list-title">
          <header className="archive-pane-heading">
            <div>
              <h2 id="archive-round-list-title">{words.playableRounds}</h2>
              <p>{words.archiveRoundHint}</p>
            </div>
            <strong>{playableRounds.length}</strong>
          </header>

          <div className="archive-round-table-scroll">
            <table className="archive-round-table">
              <caption className="sr-only">{words.playableRounds}</caption>
              <thead>
                <tr>
                  <th scope="col"><span className="sr-only">{words.selectColumn}</span></th>
                  <th scope="col">{words.sourceRound}</th>
                  <th scope="col">{words.durationColumn}</th>
                  <th scope="col">{words.archiveSides}</th>
                  <th scope="col">{words.economy}</th>
                  <th scope="col">{words.archiveFiles}</th>
                  <th scope="col">{words.statusColumn}</th>
                </tr>
              </thead>
              <tbody>
                {archive.rounds.map((round) => {
                  const active = selected?.round === round.round;
                  const playableIndex = playableRounds.findIndex((item) => item.round === round.round);
                  const continuation = effectiveCommandMode === "sequence"
                    && round.available
                    && selectedIndex >= 0
                    && playableIndex > selectedIndex
                    && playableIndex < selectedIndex + sequenceCount;
                  const rowClassName = [
                    "archive-round-row",
                    active ? "is-start" : "",
                    continuation ? "is-continuation" : "",
                    round.available ? "" : "is-unavailable",
                  ].filter(Boolean).join(" ");

                  return (
                    <tr
                      className={rowClassName}
                      key={round.round}
                      onClick={round.available ? () => onSelectRound(round.round) : undefined}
                    >
                      <td className="archive-round-select-cell">
                        <input
                          type="radio"
                          name="archive-source-round"
                          checked={active}
                          disabled={!round.available}
                          aria-label={words.selectArchiveRound.replace("{round}", String(round.round))}
                          onChange={() => onSelectRound(round.round)}
                        />
                      </td>
                      <th scope="row">
                        <span className="archive-round-number">{round.round}</span>
                        {round.pistolRound ? <small>{words.pistolRound}</small> : null}
                      </th>
                      <td>{formatDuration(round.durationSeconds)}</td>
                      <td className="archive-sides-cell">
                        <span>{round.tFiles}</span><i aria-hidden="true">/</i><span>{round.ctFiles}</span>
                      </td>
                      <td className="archive-economy-cell" title={economyLabel(round)}>{economyLabel(round)}</td>
                      <td>{round.files}</td>
                      <td>
                        <span className={`archive-availability ${round.available ? "is-available" : "is-missing"}`}>
                          {round.available ? words.available : words.unavailable}
                        </span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
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
                <div>
                  <span>{words.sourceRound}</span>
                  <strong>Round {selected.round}</strong>
                </div>
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

              <dl className="archive-output-summary">
                <div><dt>{words.availableFiles}</dt><dd>{archive.playableFiles}</dd></div>
                <div><dt>{words.traceSize}</dt><dd>{formatBytes(archive.outputBytes)}</dd></div>
                <div><dt>{words.players}</dt><dd>{archive.players.length}</dd></div>
                <div><dt>{words.voiceFiles}</dt><dd>{archive.voice.sidecars}</dd></div>
              </dl>

              <ArchiveIssues archive={archive} words={words} />
            </>
          ) : (
            <div className="archive-no-playable">
              <AlertIcon size={20} />
              <strong>{words.noPlayableRounds}</strong>
              <button className="secondary-button" type="button" onClick={onChooseManifest}>
                {words.changeManifest}
              </button>
              <ArchiveIssues archive={archive} words={words} />
            </div>
          )}
        </aside>
      </div>
    </section>
  );
}
