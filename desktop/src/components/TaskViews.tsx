import { type RefObject } from "react";
import {
  AlertIcon,
  ArrowIcon,
  CheckIcon,
  ChevronIcon,
  CopyIcon,
  FolderIcon,
  ReplayIcon,
  TraceMark,
} from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConversionSummary, ProgressPhase, ProgressState } from "../types";

export type CopyTarget = "playback" | "phrase" | "output" | "manifest";
export type CommandMode = "sequence" | "round";

interface DemoPickerViewProps {
  words: TextDictionary;
  chooseButtonRef: RefObject<HTMLButtonElement | null>;
  onChoose: () => void;
  onOpenManifest: () => void;
}

export function DemoPickerView({ words, chooseButtonRef, onChoose, onOpenManifest }: DemoPickerViewProps) {
  return (
    <section className="demo-picker-view" aria-labelledby="demo-picker-title">
      <TraceMark size={34} />
      <h1 id="demo-picker-title">{words.chooseDemoTitle}</h1>
      <p>{words.chooseDemoBody}</p>
      <div className="picker-actions">
        <button ref={chooseButtonRef} className="primary-button" type="button" onClick={onChoose}>
          <FolderIcon size={17} />
          {words.chooseDemo}
        </button>
        <button className="secondary-button" type="button" onClick={onOpenManifest}>
          <ReplayIcon size={16} />
          {words.openManifest}
        </button>
      </div>
      <div className="picker-notes">
        <span>{words.localOnly}</span>
        <span>{words.fullParse}</span>
      </div>
    </section>
  );
}

interface OpeningArchiveViewProps {
  words: TextDictionary;
  manifestName: string;
}

export function OpeningArchiveView({ words, manifestName }: OpeningArchiveViewProps) {
  return (
    <section className="task-progress-view archive-opening-view" aria-labelledby="archive-opening-title">
      <div className="task-progress-copy">
        <h1 id="archive-opening-title">{words.openingManifestTitle}</h1>
        <strong>{manifestName}</strong>
        <p>{words.openingManifestBody}</p>
      </div>
      <div className="indeterminate-progress" aria-hidden="true"><span /></div>
      <div className="task-progress-meta" role="status" aria-live="polite">
        <span>{words.readingArchive}</span>
      </div>
    </section>
  );
}

function formatElapsed(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${String(minutes).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
}

interface AnalysisProgressViewProps {
  words: TextDictionary;
  sourceFileName: string;
  elapsedSeconds: number;
  progressPhase: ProgressPhase;
}

export function AnalysisProgressView({ words, sourceFileName, elapsedSeconds, progressPhase }: AnalysisProgressViewProps) {
  const decompressing = progressPhase === "decompressing";
  return (
    <section className="task-progress-view" aria-labelledby="analysis-progress-title">
      <div className="task-progress-copy">
        <h1 id="analysis-progress-title">{decompressing ? words.decompressingTitle : words.analyzingTitle}</h1>
        <strong>{sourceFileName}</strong>
        <p>{decompressing ? words.decompressingBody : words.fullParse}</p>
      </div>
      <div className="indeterminate-progress" aria-hidden="true"><span /></div>
      <div className="task-progress-meta" role="status" aria-live="polite">
        <span>{words.localOnly}</span>
        {elapsedSeconds >= 8 ? <span>{words.elapsed.replace("{time}", formatElapsed(elapsedSeconds))}</span> : null}
      </div>
      {elapsedSeconds >= 30 ? <p className="long-task-note">{words.analyzingLong}</p> : null}
    </section>
  );
}

interface AnalysisFailedViewProps {
  words: TextDictionary;
  error: string;
  retryButtonRef: RefObject<HTMLButtonElement | null>;
  onRetry: () => void;
  onChangeDemo: () => void;
}

export function AnalysisFailedView({ words, error, retryButtonRef, onRetry, onChangeDemo }: AnalysisFailedViewProps) {
  return (
    <section className="failure-view" aria-labelledby="analysis-failed-title">
      <span className="failure-symbol" aria-hidden="true"><AlertIcon size={22} /></span>
      <h1 id="analysis-failed-title" tabIndex={-1}>{words.analysisFailedTitle}</h1>
      <p>{error}</p>
      <div className="view-actions">
        <button ref={retryButtonRef} className="primary-button" type="button" onClick={onRetry}><ReplayIcon size={16} />{words.retryAnalysis}</button>
        <button className="secondary-button" type="button" onClick={onChangeDemo}>{words.changeDemo}</button>
      </div>
    </section>
  );
}

function progressPhaseIndex(phase: ProgressPhase): number {
  if (phase === "writing") return 1;
  if (phase === "artifacts") return 2;
  if (phase === "voice") return 3;
  if (phase === "validating" || phase === "complete") return 4;
  return 0;
}

interface ConversionProgressViewProps {
  words: TextDictionary;
  progress: ProgressState;
  outputRoot: string;
}

export function ConversionProgressView({ words, progress, outputRoot }: ConversionProgressViewProps) {
  const stages = [words.preparing, words.writingPlayers, words.writingArtifacts, words.exportingVoice, words.validating];
  const activeIndex = progressPhaseIndex(progress.phase);
  const determinate = progress.estimated > 0 && progress.unit !== null;
  const fraction = determinate ? Math.min(1, progress.written / progress.estimated) : 0;
  const currentLabel =
    progress.unit === "playerFiles"
      ? words.playerFilesProgress.replace("{written}", String(progress.written)).replace("{total}", String(progress.estimated))
      : progress.unit === "artifacts"
        ? words.artifactsProgress.replace("{written}", String(progress.written)).replace("{total}", String(progress.estimated))
        : stages[activeIndex];

  return (
    <section className="conversion-progress-view" aria-labelledby="conversion-title">
      <header>
        <h1 id="conversion-title">{words.conversionTitle}</h1>
        <p>{words.conversionBody}</p>
      </header>

      <div className="conversion-progress-main">
        <strong>{stages[activeIndex]}</strong>
        <span className="progress-count">{currentLabel}</span>
        <div className={`linear-progress${determinate ? " is-determinate" : " is-indeterminate"}`} aria-hidden="true">
          <span style={determinate ? { width: `${fraction * 100}%` } : undefined} />
        </div>
        {progress.currentRound !== undefined ? (
          <span className="round-progress-copy">
            {words.roundProgress
              .replace("{round}", String(progress.currentRound))
              .replace("{completed}", String(progress.completedRounds))
              .replace("{total}", String(progress.selectedRounds))}
          </span>
        ) : null}
        {progress.currentItem ? <span className="current-item">{progress.currentItem}</span> : null}
      </div>

      <ol className="stage-list" aria-label={words.workflowLabel}>
        {stages.map((stage, index) => (
          <li className={`${index < activeIndex ? "is-done" : ""}${index === activeIndex ? " is-active" : ""}`} key={stage}>
            <i aria-hidden="true">{index < activeIndex ? <CheckIcon size={12} /> : null}</i>
            <span>{stage}</span>
          </li>
        ))}
      </ol>

      <div className="output-root-readout">
        <span>{words.outputTarget}</span>
        <code title={outputRoot}>{outputRoot}</code>
      </div>

      {progress.log.length > 0 ? (
        <details className="activity-disclosure">
          <summary>{words.activityDetails}<ChevronIcon size={15} /></summary>
          <div className="activity-log">
            {progress.log.map((entry, index) => <p className={`log-${entry.level}`} key={`${entry.message}-${index}`}>{entry.message}</p>)}
          </div>
        </details>
      ) : null}

      <span className="sr-only" role="status" aria-live="polite">{progress.announcement}</span>
    </section>
  );
}

interface ValidationFailedViewProps {
  words: TextDictionary;
  error: string;
  outputRoot: string;
  onOpenFolder: () => void;
  onBack: () => void;
}

export function ValidationFailedView({ words, error, outputRoot, onOpenFolder, onBack }: ValidationFailedViewProps) {
  return (
    <section className="failure-view validation-failure" aria-labelledby="validation-failed-title">
      <span className="failure-symbol" aria-hidden="true"><AlertIcon size={22} /></span>
      <h1 id="validation-failed-title" tabIndex={-1}>{words.validationFailedTitle}</h1>
      <p>{words.validationFailedBody}</p>
      <code className="failure-detail">{error}</code>
      <div className="path-readout"><span>{words.outputTarget}</span><code>{outputRoot}</code></div>
      <div className="view-actions">
        <button className="secondary-button" type="button" onClick={onOpenFolder}><FolderIcon size={16} />{words.openFolder}</button>
        <button className="primary-button" type="button" onClick={onBack}>{words.backToRounds}</button>
      </div>
    </section>
  );
}

interface ResultViewProps {
  words: TextDictionary;
  result: ConversionSummary;
  warnings: string[];
  copiedTarget: CopyTarget | null;
  resultHeadingRef: RefObject<HTMLHeadingElement | null>;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenFolder: () => void;
  onBrowseManifest: () => void;
  onBack: () => void;
  onNewDemo: () => void;
  formatNumber: (value: number) => string;
  formatBytes: (value: number | string) => string;
}

export function ResultView({
  words,
  result,
  warnings,
  copiedTarget,
  resultHeadingRef,
  onCopy,
  onOpenFolder,
  onBrowseManifest,
  onBack,
  onNewDemo,
  formatNumber,
  formatBytes,
}: ResultViewProps) {
  const visibleWarnings = [...new Set(warnings)];
  const voiceState = result.voice.sidecars > 0
    ? words.voiceExportedCount.replace("{count}", formatNumber(result.voice.sidecars))
    : result.voice.requested === true
      ? words.voiceRequestedEmptyResult
      : result.voice.requested === false
        ? words.voiceNotRequested
        : words.voiceUnknown;
  const cosmeticState = result.cosmetics.files > 0
    ? words.cosmeticsExportedCount.replace("{count}", formatNumber(result.cosmetics.files))
    : result.cosmetics.requested === true
      ? words.cosmeticsRequestedEmptyResult
      : result.cosmetics.requested === false
        ? words.cosmeticsNotRequested
        : words.cosmeticsUnknown;

  return (
    <section className="result-view" aria-labelledby="result-title">
      <header className="result-heading">
        <span className="success-symbol" aria-hidden="true"><CheckIcon size={20} /></span>
        <div>
          <h1 id="result-title" ref={resultHeadingRef} tabIndex={-1}>{words.completeTitle}</h1>
          <p>{words.completeSummary.replace("{rounds}", formatNumber(result.rounds.length)).replace("{files}", formatNumber(result.filesWritten))}</p>
        </div>
      </header>

      {visibleWarnings.length > 0 ? (
        <div className="result-warning" role="status">
          <AlertIcon size={17} />
          <div>
            <strong>{words.resultWarningsTitle}</strong>
            {visibleWarnings.map((warning) => <p key={warning}>{warning}</p>)}
          </div>
        </div>
      ) : null}

      <section className="result-next-step" aria-labelledby="result-next-title">
        <div>
          <span>{words.nextStep}</span>
          <h2 id="result-next-title">{words.resultReadyTitle}</h2>
          <p>{words.resultReadyBody}</p>
        </div>
        <button className="primary-button" type="button" onClick={onBrowseManifest}>
          <ReplayIcon size={15} />{words.preparePlayback}<ArrowIcon size={15} />
        </button>
      </section>

      <div className="result-capabilities" aria-label={words.archiveContents}>
        <div>
          <span>{words.voiceCapability}</span>
          <strong>{voiceState}</strong>
        </div>
        <div>
          <span>{words.cosmeticsCapability}</span>
          <strong>{cosmeticState}</strong>
        </div>
      </div>

      <details className="result-details">
        <summary>{words.resultDetails}<ChevronIcon size={15} /></summary>
        <div className="result-paths">
          <div className="result-path-row">
            <div><span>{words.output}</span><code title={result.root}>{result.root}</code></div>
            <div className="path-actions">
              <button className="secondary-button" type="button" onClick={onOpenFolder}><FolderIcon size={15} />{words.openFolder}</button>
              <button className="icon-button" type="button" onClick={() => onCopy(result.root, "output")} aria-label={words.copyPath} title={words.copyPath}>{copiedTarget === "output" ? <CheckIcon size={15} /> : <CopyIcon size={15} />}</button>
            </div>
          </div>
          <div className="result-path-row">
            <div><span>{words.manifest}</span><code title={result.manifestPath}>{result.manifestPath}</code></div>
            <button className="icon-button" type="button" onClick={() => onCopy(result.manifestPath, "manifest")} aria-label={words.copyPath} title={words.copyPath}>{copiedTarget === "manifest" ? <CheckIcon size={15} /> : <CopyIcon size={15} />}</button>
          </div>
        </div>
        <div className="result-statline">
          <span><b>{formatNumber(result.validatedFiles)}</b> {words.validatedFiles}</span>
          <span><b>{formatBytes(result.outputBytes)}</b> {words.outputSize}</span>
        </div>
      </details>

      <footer className="result-footer">
        <button className="quiet-button" type="button" onClick={onNewDemo}>{words.processAnother}</button>
        <button className="secondary-button" type="button" onClick={onBack}><ReplayIcon size={15} />{words.backToRounds}</button>
      </footer>
    </section>
  );
}
