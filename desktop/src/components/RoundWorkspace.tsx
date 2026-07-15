import { useMemo, useState } from "react";
import { ArrowIcon, FolderIcon, SlidersIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { AnalysisResult, RoundInfo } from "../types";
import { RoundTable, type RoundTableLabels } from "./RoundTable";

interface RoundWorkspaceProps {
  words: TextDictionary;
  analysis: AnalysisResult;
  selectedRounds: Set<number>;
  allowSuspicious: boolean;
  outputDir: string;
  outputRoot: string;
  onToggleRound: (round: RoundInfo) => void;
  onRestoreRecommended: () => void;
  onClearSelection: () => void;
  onAllowSuspiciousChange: (checked: boolean) => void;
  onChooseOutput: () => void;
  onOpenSettings: (trigger: HTMLButtonElement) => void;
  onConvert: () => void;
  formatNumber: (value: number) => string;
}

function compactPath(path: string, limit = 72): string {
  if (path.length <= limit) return path;
  const keep = Math.floor((limit - 1) / 2);
  return `${path.slice(0, keep)}…${path.slice(-keep)}`;
}

export function RoundWorkspace({
  words,
  analysis,
  selectedRounds,
  allowSuspicious,
  outputDir,
  outputRoot,
  onToggleRound,
  onRestoreRecommended,
  onClearSelection,
  onAllowSuspiciousChange,
  onChooseOutput,
  onOpenSettings,
  onConvert,
  formatNumber,
}: RoundWorkspaceProps) {
  const [expandedRound, setExpandedRound] = useState<number | null>(null);
  const recommendedCount = useMemo(() => analysis.rounds.filter((round) => round.status === "recommended").length, [analysis.rounds]);
  const suspiciousCount = analysis.rounds.length - recommendedCount;
  const labels: RoundTableLabels = {
    caption: words.rounds,
    select: words.selectColumn,
    round: words.roundColumn,
    status: words.statusColumn,
    duration: words.durationColumn,
    teams: words.teamsColumn,
    validRows: words.validRowsColumn,
    problems: words.issuesColumn,
    recommended: words.recommended,
    suspicious: words.suspicious,
    noProblems: words.noIssues,
    suspiciousLocked: words.suspiciousLocked,
    showDetails: words.showIssues,
    hideDetails: words.hideIssues,
  };
  const summary = words.roundSummary
    .replace("{total}", formatNumber(analysis.rounds.length))
    .replace("{recommended}", formatNumber(recommendedCount))
    .replace("{suspicious}", formatNumber(suspiciousCount));
  const canConvert = selectedRounds.size > 0 && Boolean(outputDir);

  return (
    <section className="round-workspace" aria-label={words.rounds}>
      <div className="round-toolbar">
        <strong className="round-summary">{summary}</strong>
        <div className="round-batch-actions">
          <button className="text-button" type="button" onClick={onRestoreRecommended}>{words.restoreRecommended}</button>
          <button className="text-button" type="button" onClick={onClearSelection}>{words.clearSelection}</button>
        </div>
        {suspiciousCount > 0 ? (
          <label className="allow-suspicious-control">
            <input type="checkbox" checked={allowSuspicious} onChange={(event) => onAllowSuspiciousChange(event.target.checked)} />
            <span className="wide-label">{words.allowSuspicious}</span>
            <span className="compact-label">{words.allowSuspiciousShort}</span>
          </label>
        ) : <span className="toolbar-spacer" />}
      </div>

      <RoundTable
        labels={labels}
        rounds={analysis.rounds}
        selectedRounds={selectedRounds}
        allowSuspicious={allowSuspicious}
        expandedRound={expandedRound}
        onToggle={onToggleRound}
        onToggleDetails={(round) => setExpandedRound((current) => current === round ? null : round)}
        formatNumber={formatNumber}
      />

      <footer className="export-status-bar">
        <div className="selection-status" aria-live="polite">
          <strong>{selectedRounds.size > 0 ? words.selectedCount.replace("{count}", String(selectedRounds.size)) : words.selectAtLeastOne}</strong>
          <div className="output-status">
            <span>{words.outputParent}</span>
            <code title={outputDir}>{outputDir ? compactPath(outputDir) : words.notSelected}</code>
            {outputRoot ? <small title={outputRoot}>{words.outputTarget}: {compactPath(outputRoot)}</small> : null}
          </div>
        </div>
        <div className="export-actions">
          <button className="secondary-button output-button" type="button" onClick={onChooseOutput}>
            <FolderIcon size={15} />
            {outputDir ? words.changeOutput : words.chooseOutput}
          </button>
          <button className="secondary-button status-settings-button" type="button" onClick={(event) => onOpenSettings(event.currentTarget)}>
            <SlidersIcon size={15} />
            {words.exportSettings}
          </button>
          <button className="primary-button convert-button" type="button" disabled={!canConvert} onClick={onConvert}>
            {words.convertCount.replace("{count}", String(selectedRounds.size))}
            <ArrowIcon size={16} />
          </button>
        </div>
      </footer>
    </section>
  );
}
