import { useMemo, useState } from "react";
import type { TextDictionary } from "../i18n";
import {
  AlertIcon,
  ArrowIcon,
  CheckIcon,
  FolderIcon,
  RefreshIcon,
  ReplayIcon,
  SearchIcon,
  TraceMark,
} from "../icons";
import type { Language } from "../types";
import "./batch-workspace.css";

export const BATCH_SELECTION_LIMIT = 24;

export type BatchConcurrency = "auto" | 1 | 2 | 3 | 4;
export type BatchCandidateStatus = "ready" | "imported" | "duplicate" | "unsupported";
export type BatchRunState = "idle" | "running" | "stopping" | "interrupted" | "complete";
export type BatchJobPhase =
  | "queued"
  | "decompressing"
  | "parsing"
  | "analyzing"
  | "selecting"
  | "converting"
  | "validating"
  | "completed"
  | "failed"
  | "skipped";

export interface BatchScanCandidate {
  id: string;
  path: string;
  fileName: string;
  sizeBytes: number | string;
  compressed?: boolean;
  modifiedAtMs?: number | null;
  status: BatchCandidateStatus;
  reason?: string | null;
  estimatedSeconds?: number | null;
}

export interface BatchJobItem {
  id: string;
  candidateId: string;
  path: string;
  fileName: string;
  phase: BatchJobPhase;
  /** Normalized 0..1 progress. Omit when the active phase is indeterminate. */
  progress?: number | null;
  stage?: string | null;
  elapsedSeconds?: number | null;
  etaSeconds?: number | null;
  error?: string | null;
  outputPath?: string | null;
}

export interface BatchEtaState {
  status: "waiting" | "calibrating" | "ready";
  sampleFileName?: string | null;
  sampleSeconds?: number | null;
  remainingSeconds?: number | null;
  confidence?: "low" | "medium" | "high";
}

export interface BatchRunSummary {
  total: number;
  completed: number;
  failed: number;
  skipped: number;
}

export interface BatchWorkspaceProps {
  words: TextDictionary;
  language: Language;
  folderPath: string;
  scanning: boolean;
  scanError?: string | null;
  candidates: readonly BatchScanCandidate[];
  selectedCandidateIds: readonly string[];
  concurrency: BatchConcurrency;
  runState: BatchRunState;
  canResume: boolean;
  jobs: readonly BatchJobItem[];
  eta?: BatchEtaState | null;
  summary: BatchRunSummary;
  soundNotifications: boolean;
  exportCosmetics: boolean;
  exportStickers: boolean;
  exportCharms: boolean;
  cosmeticOptionsLocked: boolean;
  retryCosmeticSettings?: {
    cosmetics: boolean;
    stickers: boolean;
    charms: boolean;
  } | null;
  onChooseFolder: () => void;
  onScan: () => void;
  onSelectionChange: (candidateIds: string[]) => void;
  onConcurrencyChange: (value: BatchConcurrency) => void;
  onSoundNotificationsChange: (enabled: boolean) => void;
  onRequestCosmetics: () => void;
  onCosmeticOptionsChange: (patch: {
    exportCosmetics?: boolean;
    exportStickers?: boolean;
    exportCharms?: boolean;
  }) => void;
  onStart: (candidateIds: string[]) => void;
  onResume: () => void;
  onStop: () => void;
  onRetryJob?: (jobId: string) => void;
  onOpenArchive?: (job: BatchJobItem) => void;
}

interface BatchCopy {
  eyebrow: string;
  title: string;
  subtitle: string;
  limit: string;
  sourceFolder: string;
  noFolder: string;
  chooseFolder: string;
  scan: string;
  scanning: string;
  rescan: string;
  scanHelp: string;
  candidates: string;
  search: string;
  searchPlaceholder: string;
  selected: string;
  selectVisible: string;
  clear: string;
  selectionLimit: string;
  noCandidates: string;
  noCandidatesBody: string;
  noMatches: string;
  candidateStatus: Record<BatchCandidateStatus, string>;
  estimatedItem: string;
  estimatedCompressedItem: string;
  compressedSize: string;
  currentQueueSetup: string;
  nextQueueSetup: string;
  queueOptions: string;
  concurrency: string;
  concurrencyHelp: string;
  concurrencyAuto: string;
  concurrencyValue: string;
  evidenceExport: string;
  cosmeticsBatchHelp: string;
  sound: string;
  soundHelp: string;
  queueMonitor: string;
  waitingForJobs: string;
  waitingForJobsBody: string;
  completed: string;
  failed: string;
  skipped: string;
  processed: string;
  etaTitle: string;
  etaWaiting: string;
  etaWaitingHelp: string;
  etaCalibrating: string;
  etaCalibratingHelp: string;
  etaReady: string;
  etaReadyHelp: string;
  confidence: Record<NonNullable<BatchEtaState["confidence"]>, string>;
  calibratedFrom: string;
  phase: Record<BatchJobPhase, string>;
  elapsed: string;
  remaining: string;
  retry: string;
  retryOriginalSettings: string;
  cosmeticsNotSaved: string;
  openArchive: string;
  start: string;
  resume: string;
  stopAfterCurrent: string;
  stopRequested: string;
  stopPolicy: string;
  completedAreKept: string;
  runComplete: string;
}

const COPY: Record<Language, BatchCopy> = {
  zh: {
    eyebrow: "本地任务队列",
    title: "批量扫描与入库",
    subtitle: "扫描一个 Demo 文件夹，以有限并发完成解压、解析、转换和验证。",
    limit: "单批最多 24 个",
    sourceFolder: "Demo 来源文件夹",
    noFolder: "尚未选择文件夹",
    chooseFolder: "选择文件夹",
    scan: "扫描 Demo",
    scanning: "正在扫描…",
    rescan: "重新扫描",
    scanHelp: "识别 .dem 和 .dem.zst；只读取候选，不会移动、修改或上传原文件。",
    candidates: "扫描候选",
    search: "搜索候选 Demo",
    searchPlaceholder: "按文件名筛选",
    selected: "已选 {count} / 24",
    selectVisible: "选择当前可见",
    clear: "清除",
    selectionLimit: "本批已达到 24 个上限。请先处理这一批，再加入更多 Demo。",
    noCandidates: "没有可入库的 Demo",
    noCandidatesBody: "选择包含 .dem 或 .dem.zst 的文件夹并开始扫描。",
    noMatches: "没有符合当前搜索的候选。",
    candidateStatus: {
      ready: "可入库",
      imported: "已在库中",
      duplicate: "重复内容",
      unsupported: "无法处理",
    },
    estimatedItem: "预计 {time}",
    estimatedCompressedItem: "初始预计 {time}",
    compressedSize: "压缩包 {size}",
    currentQueueSetup: "当前任务设置",
    nextQueueSetup: "下一批设置",
    queueOptions: "并发与档案证据",
    concurrency: "并发解析",
    concurrencyHelp: "Auto 根据本机 CPU 选择有限并发；手动模式最多 4 个。",
    concurrencyAuto: "Auto（推荐）",
    concurrencyValue: "{count} 个并发",
    evidenceExport: "档案证据",
    cosmeticsBatchHelp: "开启后，本批每个 Demo 都会解析并保留可归属给选手的饰品证据；首次风险确认后会沿用设置中的选择。",
    sound: "完成与错误提示音",
    soundHelp: "整批完成或需要处理错误时播放一次；不会为每个成功项目连续响铃。",
    queueMonitor: "处理进度",
    waitingForJobs: "队列尚未开始",
    waitingForJobsBody: "选择候选并启动后，每个 Demo 的阶段、进度与错误会显示在这里。",
    completed: "已入库",
    failed: "失败",
    skipped: "跳过",
    processed: "已处理 {done} / {total}",
    etaTitle: "预计剩余解压/解析时间",
    etaWaiting: "等待第一个样本",
    etaWaitingHelp: "首个 Demo 完成后，才会建立这台电脑的解析速度基线。",
    etaCalibrating: "正在校准本机速度",
    etaCalibratingHelp: "当前估算尚不稳定；.dem.zst 的压缩包大小只用于初始粗略估计。",
    etaReady: "约 {time}",
    etaReadyHelp: "只估算尚未完成的解压与解析；压缩率会影响 .dem.zst 的误差，归档写入与验证耗时不在其中。",
    confidence: { low: "低置信度", medium: "中等置信度", high: "较稳定" },
    calibratedFrom: "样本：{name} · {time}",
    phase: {
      queued: "等待中",
      decompressing: "解压 Demo",
      parsing: "解析 Demo",
      analyzing: "分析比赛",
      selecting: "准备转换",
      converting: "写入档案",
      validating: "验证输出",
      completed: "已入库",
      failed: "失败",
      skipped: "已跳过",
    },
    elapsed: "已用 {time}",
    remaining: "剩余约 {time}",
    retry: "重试",
    retryOriginalSettings: "重试沿用原批次：{details}",
    cosmeticsNotSaved: "不保存饰品证据",
    openArchive: "打开档案",
    start: "开始入库 {count} 个 Demo",
    resume: "恢复未完成队列",
    stopAfterCurrent: "当前在跑的项目完成后停止",
    stopRequested: "已请求停止",
    stopPolicy: "不会强行中断已经开始解析或写入的项目；这些在跑项目完成并验证后，不再派发新任务。",
    completedAreKept: "已经完成并验证的档案会一直保留，不会因停止、失败或重启而静默丢弃。",
    runComplete: "本批任务已结束",
  },
  en: {
    eyebrow: "Local job queue",
    title: "Batch scan and import",
    subtitle: "Scan a demo folder, then decompress, parse, convert, and validate with bounded concurrency.",
    limit: "Up to 24 per batch",
    sourceFolder: "Demo source folder",
    noFolder: "No folder selected",
    chooseFolder: "Choose folder",
    scan: "Scan for demos",
    scanning: "Scanning…",
    rescan: "Scan again",
    scanHelp: "Finds .dem and .dem.zst candidates only; source files are never moved, modified, or uploaded.",
    candidates: "Scan candidates",
    search: "Search demo candidates",
    searchPlaceholder: "Filter by file name",
    selected: "{count} / 24 selected",
    selectVisible: "Select visible",
    clear: "Clear",
    selectionLimit: "This batch has reached the 24-demo limit. Process it before adding more demos.",
    noCandidates: "No importable demos",
    noCandidatesBody: "Choose a folder containing .dem or .dem.zst files and start a scan.",
    noMatches: "No candidates match the current search.",
    candidateStatus: {
      ready: "Ready",
      imported: "Already imported",
      duplicate: "Duplicate content",
      unsupported: "Unsupported",
    },
    estimatedItem: "Est. {time}",
    estimatedCompressedItem: "Initial est. {time}",
    compressedSize: "Compressed {size}",
    currentQueueSetup: "Current job settings",
    nextQueueSetup: "Next batch settings",
    queueOptions: "Concurrency and archive evidence",
    concurrency: "Concurrent parses",
    concurrencyHelp: "Auto chooses bounded concurrency from the local CPU; manual mode is capped at four.",
    concurrencyAuto: "Auto (recommended)",
    concurrencyValue: "{count} concurrent",
    evidenceExport: "Archive evidence",
    cosmeticsBatchHelp: "When enabled, every demo in this batch is parsed for player-attributable cosmetic evidence. The setting is reused after the first risk confirmation.",
    sound: "Completion and error sounds",
    soundHelp: "Plays once when the batch finishes or needs attention, not after every successful item.",
    queueMonitor: "Job progress",
    waitingForJobs: "The queue has not started",
    waitingForJobsBody: "After you select candidates and start, each demo’s phase, progress, and errors appear here.",
    completed: "Imported",
    failed: "Failed",
    skipped: "Skipped",
    processed: "{done} / {total} processed",
    etaTitle: "Estimated decompress/parse time remaining",
    etaWaiting: "Waiting for the first sample",
    etaWaitingHelp: "A local parse-speed baseline is available only after the first demo completes.",
    etaCalibrating: "Calibrating this computer",
    etaCalibratingHelp: "The estimate is not stable yet; a .dem.zst archive size is only an initial rough signal.",
    etaReady: "About {time}",
    etaReadyHelp: "This estimates unfinished decompression and parsing only. Compression ratio adds uncertainty for .dem.zst; archive writing and validation are excluded.",
    confidence: { low: "Low confidence", medium: "Medium confidence", high: "More stable" },
    calibratedFrom: "Sample: {name} · {time}",
    phase: {
      queued: "Queued",
      decompressing: "Decompressing demo",
      parsing: "Parsing demo",
      analyzing: "Analyzing match",
      selecting: "Preparing conversion",
      converting: "Writing archive",
      validating: "Validating output",
      completed: "Imported",
      failed: "Failed",
      skipped: "Skipped",
    },
    elapsed: "Elapsed {time}",
    remaining: "About {time} left",
    retry: "Retry",
    retryOriginalSettings: "Retry uses the original batch: {details}",
    cosmeticsNotSaved: "No cosmetic evidence",
    openArchive: "Open archive",
    start: "Import {count} demos",
    resume: "Resume unfinished queue",
    stopAfterCurrent: "Stop after active items",
    stopRequested: "Stop requested",
    stopPolicy: "Active parses and writes are not force-terminated. Once those items finish and validate, no new job is dispatched.",
    completedAreKept: "Completed and validated archives remain available; stopping, failures, or a restart never silently discards them.",
    runComplete: "This batch has finished",
  },
};

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

function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return "—";
  const total = Math.max(0, Math.round(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const remainder = total % 60;
  if (hours > 0) return `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
  return `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function clampProgress(value: number | null | undefined): number | null {
  if (value === null || value === undefined || !Number.isFinite(value)) return null;
  return Math.min(1, Math.max(0, value));
}

function isCandidateSelectable(candidate: BatchScanCandidate): boolean {
  return candidate.status === "ready";
}

function isJobActive(phase: BatchJobPhase): boolean {
  return ["decompressing", "parsing", "analyzing", "selecting", "converting", "validating"].includes(phase);
}

export function BatchWorkspace({
  words,
  language,
  folderPath,
  scanning,
  scanError,
  candidates,
  selectedCandidateIds,
  concurrency,
  runState,
  canResume,
  jobs,
  eta,
  summary,
  soundNotifications,
  exportCosmetics,
  exportStickers,
  exportCharms,
  cosmeticOptionsLocked,
  retryCosmeticSettings,
  onChooseFolder,
  onScan,
  onSelectionChange,
  onConcurrencyChange,
  onSoundNotificationsChange,
  onRequestCosmetics,
  onCosmeticOptionsChange,
  onStart,
  onResume,
  onStop,
  onRetryJob,
  onOpenArchive,
}: BatchWorkspaceProps) {
  const copy = COPY[language];
  const [query, setQuery] = useState("");
  const selected = useMemo(() => new Set(selectedCandidateIds.slice(0, BATCH_SELECTION_LIMIT)), [selectedCandidateIds]);
  const locale = language === "zh" ? "zh-CN" : "en-US";
  const filteredCandidates = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase(locale);
    if (!needle) return candidates;
    return candidates.filter((candidate) => `${candidate.fileName} ${candidate.path}`.toLocaleLowerCase(locale).includes(needle));
  }, [candidates, locale, query]);
  const working = runState === "running" || runState === "stopping";
  const atLimit = selected.size >= BATCH_SELECTION_LIMIT;
  const selectableVisible = filteredCandidates.filter(isCandidateSelectable);
  const processed = Math.min(summary.total, summary.completed + summary.failed + summary.skipped);
  const overallProgress = summary.total > 0 ? Math.min(1, processed / summary.total) : 0;
  const retryEvidenceDetails = retryCosmeticSettings?.cosmetics
    ? [
      words.exportCosmetics,
      retryCosmeticSettings.stickers ? words.exportStickers : null,
      retryCosmeticSettings.charms ? words.exportCharms : null,
    ].filter((detail): detail is string => Boolean(detail)).join(" · ")
    : copy.cosmeticsNotSaved;

  function toggleCandidate(candidate: BatchScanCandidate) {
    if (!isCandidateSelectable(candidate) || working) return;
    const next = new Set(selected);
    if (next.has(candidate.id)) next.delete(candidate.id);
    else if (next.size < BATCH_SELECTION_LIMIT) next.add(candidate.id);
    onSelectionChange([...next]);
  }

  function selectVisible() {
    if (working) return;
    const next = new Set(selected);
    for (const candidate of selectableVisible) {
      if (next.size >= BATCH_SELECTION_LIMIT) break;
      next.add(candidate.id);
    }
    onSelectionChange([...next]);
  }

  const etaStatus = eta?.status ?? "waiting";
  const etaHeadline = etaStatus === "ready" && eta?.remainingSeconds !== null && eta?.remainingSeconds !== undefined
    ? copy.etaReady.replace("{time}", formatDuration(eta.remainingSeconds))
    : etaStatus === "calibrating" ? copy.etaCalibrating : copy.etaWaiting;
  const etaHelp = etaStatus === "ready" ? copy.etaReadyHelp : etaStatus === "calibrating" ? copy.etaCalibratingHelp : copy.etaWaitingHelp;

  return (
    <section className="batch-workspace" aria-labelledby="batch-workspace-title">
      <header className="batch-heading">
        <div className="batch-heading-mark" aria-hidden="true"><TraceMark size={30} /></div>
        <div>
          <span>{copy.eyebrow}</span>
          <h1 id="batch-workspace-title">{copy.title}</h1>
          <p>{copy.subtitle}</p>
        </div>
        <strong className="batch-limit-badge">{copy.limit}</strong>
      </header>

      <section className="batch-source-panel" aria-labelledby="batch-source-title">
        <div className="batch-source-copy">
          <span id="batch-source-title">{copy.sourceFolder}</span>
          <code title={folderPath}>{folderPath || copy.noFolder}</code>
          <small>{copy.scanHelp}</small>
        </div>
        <div className="batch-source-actions">
          <button className="secondary-button" type="button" onClick={onChooseFolder} disabled={working || scanning}>
            <FolderIcon size={15} />{copy.chooseFolder}
          </button>
          <button className="primary-button" type="button" onClick={onScan} disabled={!folderPath || working || scanning}>
            <RefreshIcon size={15} />{scanning ? copy.scanning : candidates.length > 0 ? copy.rescan : copy.scan}
          </button>
        </div>
      </section>

      {scanError ? <div className="batch-scan-error" role="alert"><AlertIcon size={16} /><span>{scanError}</span></div> : null}

      <div className="batch-layout">
        <section className="batch-candidate-pane" aria-labelledby="batch-candidate-title">
          <header className="batch-pane-header">
            <div>
              <span>{copy.candidates}</span>
              <strong id="batch-candidate-title">{copy.selected.replace("{count}", String(selected.size))}</strong>
            </div>
            <div>
              <button className="text-button" type="button" onClick={selectVisible} disabled={working || selectableVisible.length === 0 || atLimit}>{copy.selectVisible}</button>
              <button className="text-button" type="button" onClick={() => onSelectionChange([])} disabled={working || selected.size === 0}>{copy.clear}</button>
            </div>
          </header>

          <label className="batch-search">
            <SearchIcon size={15} />
            <span className="sr-only">{copy.search}</span>
            <input type="search" value={query} onChange={(event) => setQuery(event.target.value)} placeholder={copy.searchPlaceholder} />
          </label>

          {atLimit ? <p className="batch-limit-note" role="status"><AlertIcon size={14} />{copy.selectionLimit}</p> : null}

          <div className="batch-candidate-list">
            {filteredCandidates.length > 0 ? filteredCandidates.map((candidate) => {
              const selectable = isCandidateSelectable(candidate);
              const checked = selected.has(candidate.id);
              const selectionBlocked = !checked && atLimit;
              const disabled = working || !selectable || selectionBlocked;
              const reason = candidate.reason || (selectionBlocked ? copy.selectionLimit : copy.candidateStatus[candidate.status]);
              return (
                <label className={`batch-candidate is-${candidate.status}${checked ? " is-selected" : ""}`} key={candidate.id} title={reason}>
                  <input type="checkbox" checked={checked} disabled={disabled} onChange={() => toggleCandidate(candidate)} />
                  <span className="batch-candidate-check" aria-hidden="true">{checked ? <CheckIcon size={11} /> : null}</span>
                  <span className="batch-candidate-copy">
                    <strong title={candidate.path}>{candidate.fileName}</strong>
                    <small>{candidate.compressed
                      ? copy.compressedSize.replace("{size}", formatBytes(candidate.sizeBytes))
                      : formatBytes(candidate.sizeBytes)}<i>·</i>{copy.candidateStatus[candidate.status]}</small>
                  </span>
                  {candidate.estimatedSeconds !== null && candidate.estimatedSeconds !== undefined && etaStatus === "ready" ? (
                    <em>{(candidate.compressed ? copy.estimatedCompressedItem : copy.estimatedItem).replace("{time}", formatDuration(candidate.estimatedSeconds))}</em>
                  ) : null}
                </label>
              );
            }) : (
              <div className="batch-candidate-empty">
                <FolderIcon size={22} />
                <strong>{candidates.length > 0 ? copy.noMatches : copy.noCandidates}</strong>
                {candidates.length === 0 ? <p>{copy.noCandidatesBody}</p> : null}
              </div>
            )}
          </div>

          <section className="batch-queue-settings" aria-labelledby="batch-settings-title">
            <header><span>{cosmeticOptionsLocked ? copy.currentQueueSetup : copy.nextQueueSetup}</span><strong id="batch-settings-title">{copy.queueOptions}</strong></header>
            <strong className="batch-setting-label">{copy.concurrency}</strong>
            <div className="batch-concurrency-options" role="radiogroup" aria-label={copy.concurrency}>
              {(["auto", 1, 2, 3, 4] as const).map((value) => (
                <button
                  className={concurrency === value ? "is-active" : ""}
                  type="button"
                  role="radio"
                  aria-checked={concurrency === value}
                  disabled={working || cosmeticOptionsLocked}
                  key={value}
                  onClick={() => onConcurrencyChange(value)}
                >
                  {value === "auto" ? "Auto" : value}
                </button>
              ))}
            </div>
            <p>{concurrency === "auto" ? copy.concurrencyAuto : copy.concurrencyValue.replace("{count}", String(concurrency))} · {copy.concurrencyHelp}</p>

            <fieldset className="batch-cosmetic-settings" disabled={working || cosmeticOptionsLocked}>
              <legend>{copy.evidenceExport}</legend>
              <label className="batch-setting-checkbox">
                <input
                  type="checkbox"
                  checked={exportCosmetics}
                  onChange={(event) => {
                    if (event.target.checked) onRequestCosmetics();
                    else onCosmeticOptionsChange({ exportCosmetics: false });
                  }}
                />
                <span>
                  <strong>{words.exportCosmetics}</strong>
                  <small>{copy.cosmeticsBatchHelp}</small>
                </span>
              </label>
              {exportCosmetics ? (
                <div className="batch-cosmetic-detail-options">
                  <label><input type="checkbox" checked={exportStickers} onChange={(event) => onCosmeticOptionsChange({ exportStickers: event.target.checked })} />{words.exportStickers}</label>
                  <label><input type="checkbox" checked={exportCharms} onChange={(event) => onCosmeticOptionsChange({ exportCharms: event.target.checked })} />{words.exportCharms}</label>
                </div>
              ) : null}
            </fieldset>

            <label className="batch-sound-toggle">
              <input type="checkbox" checked={soundNotifications} onChange={(event) => onSoundNotificationsChange(event.target.checked)} />
              <span aria-hidden="true"><i /></span>
              <div><strong>{copy.sound}</strong><small>{copy.soundHelp}</small></div>
            </label>
          </section>
        </section>

        <section className="batch-monitor-pane" aria-labelledby="batch-monitor-title">
          <header className="batch-monitor-header">
            <div>
              <span>{copy.queueMonitor}</span>
              <strong id="batch-monitor-title">{summary.total > 0 ? copy.processed.replace("{done}", String(processed)).replace("{total}", String(summary.total)) : copy.waitingForJobs}</strong>
            </div>
            <div className="batch-summary-stats" aria-live="polite">
              <span className="is-complete"><b>{summary.completed}</b>{copy.completed}</span>
              <span className="is-failed"><b>{summary.failed}</b>{copy.failed}</span>
              <span className="is-skipped"><b>{summary.skipped}</b>{copy.skipped}</span>
            </div>
          </header>

          {summary.total > 0 ? (
            <div className="batch-overall-progress" aria-label={copy.processed.replace("{done}", String(processed)).replace("{total}", String(summary.total))}>
              <span style={{ width: `${overallProgress * 100}%` }} />
            </div>
          ) : null}

          <section className={`batch-eta-card is-${etaStatus}`} aria-labelledby="batch-eta-title" aria-live="polite">
            <div className="batch-eta-dial" aria-hidden="true"><i /><i /><i /></div>
            <div>
              <span id="batch-eta-title">{copy.etaTitle}</span>
              <strong>{etaHeadline}</strong>
              <p>{etaHelp}</p>
              {eta?.sampleFileName ? (
                <small>{copy.calibratedFrom.replace("{name}", eta.sampleFileName).replace("{time}", formatDuration(eta.sampleSeconds))}</small>
              ) : null}
            </div>
            {eta?.confidence ? <em>{copy.confidence[eta.confidence]}</em> : null}
          </section>

          <div className="batch-job-list">
            {jobs.length > 0 ? jobs.map((job) => {
              const progress = clampProgress(job.progress);
              const active = isJobActive(job.phase);
              return (
                <article className={`batch-job is-${job.phase}`} key={job.id}>
                  <span className="batch-job-state" aria-hidden="true">
                    {job.phase === "completed" ? <CheckIcon size={13} /> : job.phase === "failed" ? <AlertIcon size={13} /> : <i />}
                  </span>
                  <div className="batch-job-main">
                    <header>
                      <strong title={job.path}>{job.fileName}</strong>
                      <span>{copy.phase[job.phase]}</span>
                    </header>
                    <div className={`batch-job-progress${progress === null && active ? " is-indeterminate" : ""}`} aria-hidden="true">
                      <span style={progress !== null ? { width: `${progress * 100}%` } : undefined} />
                    </div>
                    <div className="batch-job-meta">
                      <span>{job.stage || copy.phase[job.phase]}</span>
                      {job.elapsedSeconds !== null && job.elapsedSeconds !== undefined ? <small>{copy.elapsed.replace("{time}", formatDuration(job.elapsedSeconds))}</small> : null}
                      {job.etaSeconds !== null && job.etaSeconds !== undefined && active ? <small>{copy.remaining.replace("{time}", formatDuration(job.etaSeconds))}</small> : null}
                    </div>
                    {job.error ? <p className="batch-job-error" role="alert">{job.error}</p> : null}
                  </div>
                  <div className="batch-job-actions">
                    {job.phase === "failed" && retryCosmeticSettings && !working ? (
                      <small className="batch-job-retry-settings">
                        {copy.retryOriginalSettings.replace("{details}", retryEvidenceDetails)}
                      </small>
                    ) : null}
                    {job.phase === "failed" && onRetryJob && !working ? <button className="quiet-button" type="button" onClick={() => onRetryJob(job.id)}><ReplayIcon size={13} />{copy.retry}</button> : null}
                    {job.phase === "completed" && onOpenArchive && !working ? <button className="quiet-button" type="button" onClick={() => onOpenArchive(job)}>{copy.openArchive}<ArrowIcon size={13} /></button> : null}
                  </div>
                </article>
              );
            }) : (
              <div className="batch-jobs-empty">
                <TraceMark size={34} />
                <strong>{copy.waitingForJobs}</strong>
                <p>{copy.waitingForJobsBody}</p>
              </div>
            )}
          </div>

          <footer className={`batch-run-bar is-${runState}`}>
            <div>
              <strong>{runState === "complete" ? copy.runComplete : runState === "stopping" ? copy.stopRequested : copy.completedAreKept}</strong>
              <p>{runState === "running" || runState === "stopping" ? copy.stopPolicy : copy.completedAreKept}</p>
            </div>
            <div>
              {working ? (
                <button className="stop-after-button" type="button" onClick={onStop} disabled={runState === "stopping"}>
                  {runState === "stopping" ? copy.stopRequested : copy.stopAfterCurrent}
                </button>
              ) : canResume ? (
                <button className="secondary-button" type="button" onClick={onResume}><ReplayIcon size={15} />{copy.resume}</button>
              ) : (
                <button className="primary-button" type="button" disabled={selected.size === 0 || scanning} onClick={() => onStart([...selected])}>
                  {copy.start.replace("{count}", String(selected.size))}<ArrowIcon size={15} />
                </button>
              )}
            </div>
          </footer>
        </section>
      </div>

      <span className="sr-only" role="status" aria-live="polite">{words.localOnlyShort}</span>
    </section>
  );
}
