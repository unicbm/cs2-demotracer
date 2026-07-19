import { Channel, invoke } from "@tauri-apps/api/core";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AppChrome } from "./components/AppChrome";
import { AppSidebar } from "./components/AppSidebar";
import { ArchiveWorkspace } from "./components/ArchiveWorkspace";
import {
  BATCH_SELECTION_LIMIT,
  BatchWorkspace,
  type BatchConcurrency,
  type BatchEtaState,
  type BatchJobItem,
  type BatchJobPhase,
  type BatchRunState,
  type BatchScanCandidate,
} from "./components/BatchWorkspace";
import { DialogPrimitive } from "./components/Dialog";
import { ExportInspector } from "./components/ExportInspector";
import { FaqWorkspace } from "./components/FaqWorkspace";
import { LibraryWorkspace, type LibrarySort } from "./components/LibraryWorkspace";
import type { PlaybackPresetOptions } from "./components/PlaybackCommandBuilder";
import { RoundWorkspace } from "./components/RoundWorkspace";
import { SettingsWorkspace } from "./components/SettingsWorkspace";
import {
  AnalysisFailedView,
  AnalysisProgressView,
  type CommandMode,
  ConversionProgressView,
  type CopyTarget,
  OpeningArchiveView,
  ResultView,
  ValidationFailedView,
} from "./components/TaskViews";
import { AlertIcon, ArrowIcon, CheckIcon, CloseIcon, CopyIcon, FolderIcon } from "./icons";
import { COSMETIC_PHRASE, TEXT } from "./i18n";
import {
  mergeLibraryScans,
  normalizeLibraryRoot,
  persistDemoSourceIndex,
  persistLibraryPreferences,
  rememberDemoSource,
  storedDemoSourceIndex,
  storedLibraryPreferences,
  uniqueLibraryRoots,
  withExportRoot,
} from "./library";
import type {
  AnalysisResult,
  BatchEvent,
  BatchItem,
  BatchItemPhase,
  BatchLedger,
  BatchList,
  Cs2InstallCandidate,
  CommandErrorDto,
  ConversionProgressEvent,
  ConversionSummary,
  ConverterSettings,
  DemoLibraryEntry,
  DemoLibraryScan,
  DemoFolderScan,
  EnvironmentDiagnosticReport,
  ImportArchivesResult,
  Language,
  LocalEnvironmentSettings,
  ManifestArchive,
  OutputPreflight,
  Phase,
  ProgressPhase,
  ProgressState,
  RefreshArchiveMetadataResult,
  RefreshLibraryMetadataResult,
  ResolveArchiveSourceResult,
  RoundInfo,
  SaveServerConfigResult,
  ServerConfigDocument,
  ServerConfigValidation,
  TaskEvent,
  TaskPhase,
  Theme,
  WorkspaceSection,
} from "./types";

const DEFAULT_SETTINGS: ConverterSettings = {
  side: "both",
  fullRound: false,
  freezePrerollSeconds: 10,
  subtickMode: "auto",
  maxRoundSeconds: 240,
  exportVoice: true,
  exportCosmetics: false,
  exportStickers: false,
  exportCharms: false,
  includeSuspicious: false,
};

const INITIAL_LIBRARY_PREFERENCES = storedLibraryPreferences();

const DEFAULT_LOCAL_ENVIRONMENT: LocalEnvironmentSettings = {
  cs2Path: "",
  demoRoots: [],
  soundNotifications: true,
};

const BATCH_PREFERENCES_STORAGE_KEY = "demotracer.batch-preferences.v1";
const ESTIMATED_ZSTD_EXPANSION = 4;

interface StoredBatchPreferences {
  folderPath: string;
  concurrency: BatchConcurrency;
}

interface BatchItemProgress {
  progress?: number | null;
  stage?: string | null;
  startedAtMs?: number;
  finishedAtMs?: number;
  written: number;
  estimated: number;
}

function storedBatchPreferences(): StoredBatchPreferences {
  try {
    const saved = JSON.parse(localStorage.getItem(BATCH_PREFERENCES_STORAGE_KEY) ?? "null") as Partial<StoredBatchPreferences> | null;
    const concurrency = saved?.concurrency;
    return {
      folderPath: typeof saved?.folderPath === "string" ? saved.folderPath : "",
      concurrency: concurrency === "auto" || concurrency === 1 || concurrency === 2 || concurrency === 3 || concurrency === 4
        ? concurrency
        : "auto",
    };
  } catch {
    return { folderPath: "", concurrency: "auto" };
  }
}

function batchJobPhase(phase: BatchItemPhase): BatchJobPhase {
  if (phase === "complete") return "completed";
  if (phase === "voice") return "converting";
  return phase;
}

function batchRunState(status: BatchLedger["status"] | undefined, invocationActive: boolean): BatchRunState {
  if (invocationActive) {
    if (status === "stopping") return "stopping";
    return "running";
  }
  if (status === "completed" || status === "completedWithErrors") return "complete";
  if (status === "paused") return "interrupted";
  if (status === "running" || status === "stopping" || status === "pending") return "interrupted";
  return "idle";
}

function nextBatchItemProgress(current: BatchItemProgress | undefined, task: TaskEvent): BatchItemProgress {
  const next: BatchItemProgress = current ?? { written: 0, estimated: 0, startedAtMs: Date.now() };
  if (task.kind === "phase") {
    return { ...next, progress: task.phase === "complete" ? 1 : next.progress };
  }
  if (task.kind === "log") {
    return task.level === "info" ? next : { ...next, stage: task.message };
  }

  const event = task.progress;
  switch (event.event) {
    case "analysisStarted":
      return { ...next, progress: 0.02 };
    case "analysisFinished":
      return { ...next, progress: 0.05, written: 0, estimated: Math.max(1, event.estimatedFiles) };
    case "roundStarted":
      return { ...next, stage: `Round ${event.round}` };
    case "roundSkipped":
      return { ...next, stage: `Round ${event.round}: ${event.reason}` };
    case "playerSkipped":
      return { ...next, stage: `${event.steamId}: ${event.reason}` };
    case "playerWritten": {
      const written = next.written + 1;
      return {
        ...next,
        written,
        progress: Math.min(0.88, 0.05 + 0.83 * (written / Math.max(1, next.estimated))),
        stage: event.playerName,
      };
    }
    case "artifactsWritingStarted":
      return { ...next, progress: 0.9, written: 0, estimated: Math.max(1, event.artifacts), stage: undefined };
    case "artifactWritten": {
      const written = next.written + 1;
      return {
        ...next,
        written,
        progress: Math.min(0.99, 0.9 + 0.09 * (written / Math.max(1, next.estimated))),
        stage: fileName(event.path),
      };
    }
    case "finished":
      return { ...next, progress: 1, stage: fileName(event.manifestPath), finishedAtMs: Date.now() };
    default:
      return next;
  }
}

const DEFAULT_PLAYBACK_PRESET: PlaybackPresetOptions = {
  weapons: true,
  cosmetics: false,
  steamIdentity: true,
  avatar: false,
  voice: true,
  playoff: false,
};

function emptyProgress(): ProgressState {
  return {
    phase: "preparing",
    message: "",
    written: 0,
    estimated: 0,
    unit: null,
    completedRounds: 0,
    selectedRounds: 0,
    log: [],
    warnings: [],
    announcement: "",
  };
}

function storedLanguage(): Language {
  const saved = localStorage.getItem("demotracer.language");
  if (saved === "zh" || saved === "en") return saved;
  return navigator.language.toLowerCase().startsWith("zh") ? "zh" : "en";
}

function storedTheme(): Theme {
  const saved = localStorage.getItem("demotracer.theme");
  return saved === "light" || saved === "dark" || saved === "system" ? saved : "system";
}

function storedSettings(): ConverterSettings {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.settings") ?? "null") as Partial<ConverterSettings> | null;
    if (!saved || typeof saved !== "object" || Array.isArray(saved)) return { ...DEFAULT_SETTINGS };
    return {
      ...DEFAULT_SETTINGS,
      side: saved.side === "both" || saved.side === "t" || saved.side === "ct" ? saved.side : DEFAULT_SETTINGS.side,
      fullRound: typeof saved.fullRound === "boolean" ? saved.fullRound : DEFAULT_SETTINGS.fullRound,
      freezePrerollSeconds: typeof saved.freezePrerollSeconds === "number"
        && Number.isFinite(saved.freezePrerollSeconds)
        && saved.freezePrerollSeconds >= 0
        && saved.freezePrerollSeconds <= 120
        ? saved.freezePrerollSeconds
        : DEFAULT_SETTINGS.freezePrerollSeconds,
      subtickMode: saved.subtickMode === "auto" || saved.subtickMode === "off"
        ? saved.subtickMode
        : DEFAULT_SETTINGS.subtickMode,
      maxRoundSeconds: typeof saved.maxRoundSeconds === "number"
        && Number.isFinite(saved.maxRoundSeconds)
        && saved.maxRoundSeconds >= 30
        && saved.maxRoundSeconds <= 1800
        ? saved.maxRoundSeconds
        : DEFAULT_SETTINGS.maxRoundSeconds,
      exportVoice: typeof saved.exportVoice === "boolean" ? saved.exportVoice : DEFAULT_SETTINGS.exportVoice,
      exportCosmetics: false,
      exportStickers: typeof saved.exportStickers === "boolean" ? saved.exportStickers : DEFAULT_SETTINGS.exportStickers,
      exportCharms: typeof saved.exportCharms === "boolean" ? saved.exportCharms : DEFAULT_SETTINGS.exportCharms,
      includeSuspicious: false,
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

function storedPlaybackPreset(): PlaybackPresetOptions {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.playback-preset.v1") ?? "null") as Partial<PlaybackPresetOptions> | null;
    if (!saved || typeof saved !== "object") return { ...DEFAULT_PLAYBACK_PRESET };
    const read = (key: keyof PlaybackPresetOptions) =>
      typeof saved[key] === "boolean" ? saved[key] : DEFAULT_PLAYBACK_PRESET[key];
    const cosmetics = read("cosmetics");
    const avatar = read("avatar");
    return {
      weapons: read("weapons") || cosmetics,
      cosmetics,
      steamIdentity: read("steamIdentity") || avatar,
      avatar,
      voice: read("voice"),
      playoff: read("playoff"),
    };
  } catch {
    return { ...DEFAULT_PLAYBACK_PRESET };
  }
}

function storedLocalEnvironment(): LocalEnvironmentSettings {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.local-environment.v1") ?? "null") as Partial<LocalEnvironmentSettings> | null;
    if (!saved || typeof saved !== "object") return { ...DEFAULT_LOCAL_ENVIRONMENT };
    return {
      cs2Path: typeof saved.cs2Path === "string" ? saved.cs2Path : "",
      demoRoots: Array.isArray(saved.demoRoots)
        ? uniqueLibraryRoots(saved.demoRoots.filter((root): root is string => typeof root === "string"))
        : [],
      soundNotifications: typeof saved.soundNotifications === "boolean" ? saved.soundNotifications : true,
    };
  } catch {
    return { ...DEFAULT_LOCAL_ENVIRONMENT };
  }
}

const ENVIRONMENT_REPORT_STORAGE_KEY = "demotracer.environment-report.v1";

interface StoredEnvironmentReport {
  cs2Path: string;
  report: EnvironmentDiagnosticReport;
}

function normalizedDiagnosticPath(path: string): string {
  return path.trim().replace(/\\/g, "/").replace(/\/+$/, "").toLocaleLowerCase();
}

function isEnvironmentDiagnosticReport(value: unknown): value is EnvironmentDiagnosticReport {
  if (!value || typeof value !== "object") return false;
  const report = value as Partial<EnvironmentDiagnosticReport>;
  return Number.isFinite(report.checkedAtMs)
    && typeof report.requestedPath === "string"
    && typeof report.cs2Root === "string"
    && typeof report.gameCsgoPath === "string"
    && ["pass", "warning", "error", "unverified"].includes(String(report.overall))
    && Array.isArray(report.checks)
    && Array.isArray(report.plugins)
    && Array.isArray(report.conflicts)
    && Boolean(report.receipt && typeof report.receipt === "object");
}

function cachedEnvironmentReport(report: EnvironmentDiagnosticReport): EnvironmentDiagnosticReport {
  const runtimeConflictRules = new Set(["known_cosmetic_writer", "cs2_bot_improver_bot_randomizer"]);
  const checks = report.checks
    .filter((check) => check.group !== "runtime")
    .map((check) => check.id === "counterStrikeSharp.runtime" && check.status === "pass"
      ? {
          ...check,
          status: "unverified" as const,
          summary: "CounterStrikeSharp was installed at the last inspection; its loaded host version requires a fresh inspection.",
          actual: "cached; runtime version unknown",
        }
      : check);
  const conflicts = report.conflicts.map((conflict) => runtimeConflictRules.has(conflict.ruleId)
    ? {
        ...conflict,
        confidence: "medium" as const,
        summary: `${conflict.title} was present at the last inspection. Inspect again to verify whether it is currently loaded or overlaps DemoTracer runtime behavior.`,
      }
    : conflict);

  return {
    ...report,
    cached: true,
    overall: "unverified",
    runtimeVerification: "unknown",
    checks,
    plugins: report.plugins.map((plugin) => ({ ...plugin, runtimeState: "unknown" })),
    conflicts,
  };
}

function storedEnvironmentReport(expectedCs2Path: string): EnvironmentDiagnosticReport | null {
  const expectedPath = normalizedDiagnosticPath(expectedCs2Path);
  if (!expectedPath) return null;
  try {
    const saved = JSON.parse(localStorage.getItem(ENVIRONMENT_REPORT_STORAGE_KEY) ?? "null") as Partial<StoredEnvironmentReport> | null;
    if (!saved || typeof saved !== "object" || typeof saved.cs2Path !== "string") return null;
    if (normalizedDiagnosticPath(saved.cs2Path) !== expectedPath || !isEnvironmentDiagnosticReport(saved.report)) return null;
    return cachedEnvironmentReport(saved.report);
  } catch {
    return null;
  }
}

function fileName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path;
}

function isDemoFilePath(path: string): boolean {
  const lowered = path.toLowerCase();
  return lowered.endsWith(".dem") || lowered.endsWith(".dem.zst");
}

function formatBytes(value: number | string): string {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const power = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** power).toFixed(power === 0 ? 0 : 1)} ${units[power]}`;
}

function parseCommandError(error: unknown): CommandErrorDto {
  if (error && typeof error === "object" && "code" in error && "message" in error) {
    const value = error as { code: unknown; message: unknown; path?: unknown };
    return {
      code: String(value.code),
      message: String(value.message),
      path: typeof value.path === "string" ? value.path : undefined,
    };
  }
  if (typeof error === "string") {
    try {
      return parseCommandError(JSON.parse(error));
    } catch {
      return { code: "unknown", message: error };
    }
  }
  if (error && typeof error === "object" && "message" in error) {
    return { code: "unknown", message: String(error.message) };
  }
  return { code: "unknown", message: String(error) };
}

function phaseFromBackend(phase: TaskPhase, current: ProgressPhase): ProgressPhase {
  if (phase === "decompressing") return "decompressing";
  if (phase === "parsing") return "parsing";
  if (phase === "analyzing") return "analyzing";
  if (phase === "voice") return "voice";
  if (phase === "validating") return "validating";
  if (phase === "complete") return "complete";
  return current;
}

function consentIsValid(phrase: string): boolean {
  return phrase.trim() === COSMETIC_PHRASE;
}

function useElapsed(active: boolean): number {
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    if (!active) {
      setSeconds(0);
      return;
    }
    const started = Date.now();
    const timer = window.setInterval(() => setSeconds(Math.floor((Date.now() - started) / 1000)), 1000);
    return () => window.clearInterval(timer);
  }, [active]);
  return seconds;
}

function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const update = () => setMatches(media.matches);
    update();
    media.addEventListener("change", update);
    return () => media.removeEventListener("change", update);
  }, [query]);
  return matches;
}

function App() {
  const [language, setLanguage] = useState<Language>(storedLanguage);
  const [theme, setTheme] = useState<Theme>(storedTheme);
  const [phase, setPhase] = useState<Phase>("idle");
  const [activeSection, setActiveSection] = useState<WorkspaceSection>("library");
  const [sourcePath, setSourcePath] = useState("");
  const [outputDir, setOutputDir] = useState(INITIAL_LIBRARY_PREFERENCES.exportRoot);
  const [libraryPreferences, setLibraryPreferences] = useState(INITIAL_LIBRARY_PREFERENCES);
  const [demoSourceIndex, setDemoSourceIndex] = useState(storedDemoSourceIndex);
  const [libraryScan, setLibraryScan] = useState<DemoLibraryScan | null>(null);
  const [libraryLoading, setLibraryLoading] = useState(false);
  const [repairingManifest, setRepairingManifest] = useState("");
  const [repairingLibrary, setRepairingLibrary] = useState(false);
  const [importingArchives, setImportingArchives] = useState(false);
  const [libraryNotice, setLibraryNotice] = useState("");
  const [libraryQuery, setLibraryQuery] = useState("");
  const [libraryMap, setLibraryMap] = useState("");
  const [librarySort, setLibrarySort] = useState<LibrarySort>("recent");
  const [batchFolderPath, setBatchFolderPath] = useState(() => storedBatchPreferences().folderPath);
  const [batchScanning, setBatchScanning] = useState(false);
  const [batchScanError, setBatchScanError] = useState("");
  const [batchScan, setBatchScan] = useState<DemoFolderScan | null>(null);
  const [batchSelectedIds, setBatchSelectedIds] = useState<string[]>([]);
  const [batchConcurrency, setBatchConcurrency] = useState<BatchConcurrency>(() => storedBatchPreferences().concurrency);
  const [batchLedger, setBatchLedger] = useState<BatchLedger | null>(null);
  const [batchProgressByItem, setBatchProgressByItem] = useState<Record<string, BatchItemProgress>>({});
  const [batchEtaSeconds, setBatchEtaSeconds] = useState<number | null>(null);
  const [batchInvocationActive, setBatchInvocationActive] = useState(false);
  const [batchStopPending, setBatchStopPending] = useState(false);
  const [batchStartingCandidates, setBatchStartingCandidates] = useState<BatchScanCandidate[]>([]);
  const [batchClock, setBatchClock] = useState(() => Date.now());
  const [outputRoot, setOutputRoot] = useState("");
  const [analysis, setAnalysis] = useState<AnalysisResult | null>(null);
  const [selectedRounds, setSelectedRounds] = useState<Set<number>>(new Set());
  const [settings, setSettings] = useState<ConverterSettings>(storedSettings);
  const [playbackPreset, setPlaybackPreset] = useState<PlaybackPresetOptions>(storedPlaybackPreset);
  const [localEnvironment, setLocalEnvironment] = useState<LocalEnvironmentSettings>(storedLocalEnvironment);
  const [installCandidates, setInstallCandidates] = useState<Cs2InstallCandidate[]>([]);
  const [installDetectionCompleted, setInstallDetectionCompleted] = useState(false);
  const [environmentReport, setEnvironmentReport] = useState<EnvironmentDiagnosticReport | null>(
    () => storedEnvironmentReport(storedLocalEnvironment().cs2Path),
  );
  const [detectingInstallations, setDetectingInstallations] = useState(false);
  const [inspectingEnvironment, setInspectingEnvironment] = useState(false);
  const [serverConfigDocument, setServerConfigDocument] = useState<ServerConfigDocument | null>(null);
  const [serverConfigDraft, setServerConfigDraft] = useState("");
  const [serverConfigValidation, setServerConfigValidation] = useState<ServerConfigValidation | null>(null);
  const [loadingServerConfig, setLoadingServerConfig] = useState(false);
  const [savingServerConfig, setSavingServerConfig] = useState(false);
  const [progress, setProgress] = useState<ProgressState>(emptyProgress);
  const [result, setResult] = useState<ConversionSummary | null>(null);
  const [archive, setArchive] = useState<ManifestArchive | null>(null);
  const [archivePath, setArchivePath] = useState("");
  const [selectedArchiveRound, setSelectedArchiveRound] = useState<number | null>(null);
  const [conversionWarnings, setConversionWarnings] = useState<string[]>([]);
  const [analysisError, setAnalysisError] = useState("");
  const [validationError, setValidationError] = useState("");
  const [globalError, setGlobalError] = useState<CommandErrorDto | null>(null);
  const [inspectorSheetOpen, setInspectorSheetOpen] = useState(false);
  const [overwriteConflict, setOverwriteConflict] = useState<OutputPreflight | null>(null);
  const [cosmeticOpen, setCosmeticOpen] = useState(false);
  const [closeOpen, setCloseOpen] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const [cosmeticPhrase, setCosmeticPhrase] = useState("");
  const [copiedTarget, setCopiedTarget] = useState<CopyTarget | null>(null);
  const [commandMode, setCommandMode] = useState<CommandMode>("sequence");
  const [liveMessage, setLiveMessage] = useState("");

  const taskTokenRef = useRef(0);
  const libraryScanTokenRef = useRef(0);
  const taskWarningsRef = useRef<string[]>([]);
  const isBusyRef = useRef(false);
  const analyzedMaxRoundSecondsRef = useRef(DEFAULT_SETTINGS.maxRoundSeconds);
  const environmentInspectionTokenRef = useRef(0);
  const batchIdRef = useRef("");
  const batchGenerationRef = useRef(0);
  const batchStopPendingRef = useRef(false);
  const batchCancelGenerationRef = useRef(-1);
  const taskSoundContextRef = useRef<AudioContext | null>(null);
  const soundNotificationsRef = useRef(localEnvironment.soundNotifications);
  const retryButtonRef = useRef<HTMLButtonElement | null>(null);
  const resultHeadingRef = useRef<HTMLHeadingElement | null>(null);
  const settingsTriggerRef = useRef<HTMLElement | null>(null);
  const cosmeticInputRef = useRef<HTMLInputElement | null>(null);
  const chooseOtherOutputRef = useRef<HTMLButtonElement | null>(null);
  const keepWorkingRef = useRef<HTMLButtonElement | null>(null);

  const words = TEXT[language];
  const libraryRoot = libraryPreferences.exportRoot;
  const libraryRoots = libraryPreferences.roots;
  const numberFormat = useMemo(() => new Intl.NumberFormat(language === "zh" ? "zh-CN" : "en-US"), [language]);
  const isRepairing = repairingLibrary || Boolean(repairingManifest);
  const isMaintainingLibrary = isRepairing || importingArchives;
  const isBusy = phase === "analyzing" || phase === "converting" || phase === "openingArchive" || isMaintainingLibrary || batchInvocationActive;
  isBusyRef.current = phase === "analyzing" || phase === "converting" || isMaintainingLibrary || batchInvocationActive;
  const inspectorDocked = useMediaQuery("(min-width: 1080px)");
  const inspectorVisible = inspectorDocked || inspectorSheetOpen;
  const elapsedSeconds = useElapsed(phase === "analyzing");
  const sourceFileName = analysis?.fileName || fileName(sourcePath);
  const themeTitle = theme === "system" ? words.systemTheme : theme === "light" ? words.lightTheme : words.darkTheme;
  soundNotificationsRef.current = localEnvironment.soundNotifications;
  const importedBatchSources = useMemo(() => new Set(
    (libraryScan?.entries ?? [])
      .map((entry) => entry.sourcePath)
      .filter((path): path is string => Boolean(path))
      .map(normalizedDiagnosticPath),
  ), [libraryScan]);
  const batchCandidates = useMemo<BatchScanCandidate[]>(() => {
    const secondsPerGib = batchLedger?.calibration?.secondsPerGib;
    return (batchScan?.candidates ?? []).map((candidate) => {
      const imported = importedBatchSources.has(normalizedDiagnosticPath(candidate.path));
      const size = Number(candidate.sizeBytes);
      const estimatedParseSize = candidate.compressed ? size * ESTIMATED_ZSTD_EXPANSION : size;
      return {
        id: normalizedDiagnosticPath(candidate.path),
        path: candidate.path,
        fileName: candidate.fileName,
        sizeBytes: candidate.sizeBytes,
        compressed: candidate.compressed,
        modifiedAtMs: candidate.modifiedAtMs,
        status: imported ? "imported" : "ready",
        reason: imported
          ? (language === "zh" ? "这个源 Demo 已经有本地档案。" : "This source demo already has a local archive.")
          : null,
        estimatedSeconds: secondsPerGib && Number.isFinite(estimatedParseSize)
          ? Math.max(1, secondsPerGib * estimatedParseSize / (1024 ** 3))
          : null,
      };
    });
  }, [batchLedger?.calibration?.secondsPerGib, batchScan, importedBatchSources, language]);
  const batchJobs = useMemo<BatchJobItem[]>(() => {
    if (!batchLedger) {
      return batchStartingCandidates.map((candidate) => ({
        id: candidate.id,
        candidateId: candidate.id,
        path: candidate.path,
        fileName: candidate.fileName,
        phase: "queued",
        progress: null,
      }));
    }
    return batchLedger.items.map((item: BatchItem) => {
      const transient = batchProgressByItem[item.itemId];
      const phase = item.status === "completed"
        ? "completed"
        : item.status === "failed"
          ? "failed"
          : batchJobPhase(item.phase);
      const finishedAt = transient?.finishedAtMs ?? (phase === "completed" || phase === "failed" ? batchLedger.updatedAtMs : undefined);
      const elapsed = transient?.startedAtMs
        ? Math.max(0, Math.floor(((finishedAt ?? batchClock) - transient.startedAtMs) / 1000))
        : null;
      return {
        id: item.itemId,
        candidateId: normalizedDiagnosticPath(item.sourcePath),
        path: item.sourcePath,
        fileName: item.fileName,
        phase,
        progress: phase === "completed" ? 1 : transient?.progress ?? null,
        stage: transient?.stage,
        elapsedSeconds: elapsed,
        etaSeconds: item.predictedParseMs && (phase === "queued" || phase === "decompressing" || phase === "parsing")
          ? Math.ceil(item.predictedParseMs / 1000)
          : null,
        error: item.error?.message ?? null,
        outputPath: item.manifestPath ?? null,
      };
    });
  }, [batchClock, batchLedger, batchProgressByItem, batchStartingCandidates]);
  const batchSummary = useMemo(() => {
    const items = batchLedger?.items;
    if (!items) return { total: batchStartingCandidates.length, completed: 0, failed: 0, skipped: 0 };
    return {
      total: items.length,
      completed: items.filter((item) => item.status === "completed").length,
      failed: items.filter((item) => item.status === "failed").length,
      skipped: 0,
    };
  }, [batchLedger, batchStartingCandidates.length]);
  const currentBatchRunState = batchStopPending && batchInvocationActive
    ? "stopping"
    : batchRunState(batchLedger?.status, batchInvocationActive);
  const canResumeBatch = !batchInvocationActive && Boolean(batchLedger?.items.some((item) =>
    item.status === "pending" || item.status === "running"));
  const batchEta = useMemo<BatchEtaState>(() => {
    const calibration = batchLedger?.calibration;
    if (!calibration) {
      return {
        status: batchInvocationActive ? "calibrating" : "waiting",
        sampleFileName: batchLedger?.items.find((item) => item.status === "running")?.fileName ?? null,
      };
    }
    const remainingMs = batchLedger.items
      .filter((item) => (item.status === "pending" || item.status === "running") && item.parseMs == null)
      .reduce((total, item) => total + (item.predictedParseMs ?? 0), 0);
    const effectiveWorkers = 1 + (Math.max(1, batchLedger.concurrency) - 1) * 0.65;
    const restoredEtaSeconds = Math.ceil(remainingMs / effectiveWorkers / 1000);
    return {
      status: "ready",
      sampleFileName: batchLedger?.items.find((item) => item.itemId === calibration.firstItemId)?.fileName ?? null,
      sampleSeconds: calibration.firstParseMs / 1000,
      remainingSeconds: batchEtaSeconds ?? restoredEtaSeconds,
      confidence: calibration.samples >= 3 ? "high" : calibration.samples >= 2 ? "medium" : "low",
    };
  }, [batchEtaSeconds, batchInvocationActive, batchLedger]);

  const primeTaskSound = useCallback((force = false) => {
    if (!force && !soundNotificationsRef.current) return;
    try {
      const context = taskSoundContextRef.current?.state === "closed"
        ? new AudioContext()
        : taskSoundContextRef.current ?? new AudioContext();
      taskSoundContextRef.current = context;
      if (context.state === "suspended") void context.resume().catch(() => undefined);
    } catch {
      // Sound is feedback only. An unavailable audio device must never block a task.
    }
  }, []);

  const playTaskSound = useCallback((kind: "success" | "failure" | "stopped") => {
    if (!soundNotificationsRef.current) return;
    try {
      const context = taskSoundContextRef.current?.state === "closed"
        ? new AudioContext()
        : taskSoundContextRef.current ?? new AudioContext();
      taskSoundContextRef.current = context;
      const schedule = () => {
        try {
          const now = context.currentTime;
          const notes = kind === "success"
            ? [{ frequency: 660, offset: 0 }, { frequency: 880, offset: 0.14 }]
            : kind === "stopped"
              ? [{ frequency: 520, offset: 0 }, { frequency: 390, offset: 0.16 }]
              : [{ frequency: 330, offset: 0 }, { frequency: 247, offset: 0.18 }];
          for (const note of notes) {
            const oscillator = context.createOscillator();
            const gain = context.createGain();
            const start = now + note.offset;
            oscillator.type = "sine";
            oscillator.frequency.setValueAtTime(note.frequency, start);
            gain.gain.setValueAtTime(0.0001, start);
            gain.gain.exponentialRampToValueAtTime(0.075, start + 0.018);
            gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.14);
            oscillator.connect(gain);
            gain.connect(context.destination);
            oscillator.start(start);
            oscillator.stop(start + 0.15);
          }
        } catch {
          // Notification audio is deliberately best effort.
        }
      };
      if (context.state === "suspended") void context.resume().then(schedule).catch(() => undefined);
      else schedule();
    } catch {
      // Notification audio is deliberately best effort.
    }
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.lang = language === "zh" ? "zh-CN" : "en";
    localStorage.setItem("demotracer.theme", theme);
    localStorage.setItem("demotracer.language", language);
    if ("__TAURI_INTERNALS__" in window) {
      void getCurrentWindow().setTheme(theme === "system" ? null : theme).catch(() => undefined);
    }
  }, [language, theme]);

  useEffect(() => () => {
    const context = taskSoundContextRef.current;
    taskSoundContextRef.current = null;
    if (context) {
      try {
        void context.close().catch(() => undefined);
      } catch {
        // Best-effort cleanup only.
      }
    }
  }, []);

  useEffect(() => {
    if (inspectorDocked) setInspectorSheetOpen(false);
  }, [inspectorDocked]);

  useEffect(() => {
    const persisted = { ...settings, exportCosmetics: false, includeSuspicious: false };
    localStorage.setItem("demotracer.settings", JSON.stringify(persisted));
  }, [settings]);

  useEffect(() => {
    localStorage.setItem("demotracer.playback-preset.v1", JSON.stringify(playbackPreset));
  }, [playbackPreset]);

  useEffect(() => {
    localStorage.setItem("demotracer.local-environment.v1", JSON.stringify(localEnvironment));
  }, [localEnvironment]);

  useEffect(() => {
    localStorage.setItem(BATCH_PREFERENCES_STORAGE_KEY, JSON.stringify({
      folderPath: batchFolderPath,
      concurrency: batchConcurrency,
    } satisfies StoredBatchPreferences));
  }, [batchConcurrency, batchFolderPath]);

  useEffect(() => {
    const ready = new Set(batchCandidates.filter((candidate) => candidate.status === "ready").map((candidate) => candidate.id));
    setBatchSelectedIds((current) => {
      const next = current.filter((id) => ready.has(id)).slice(0, BATCH_SELECTION_LIMIT);
      return next.length === current.length && next.every((id, index) => id === current[index]) ? current : next;
    });
  }, [batchCandidates]);

  useEffect(() => {
    if (!batchInvocationActive) return;
    setBatchClock(Date.now());
    const timer = window.setInterval(() => setBatchClock(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [batchInvocationActive]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    const generation = batchGenerationRef.current;
    void invoke<BatchList>("list_batch_imports").then(({ batches }) => {
      if (disposed || generation !== batchGenerationRef.current || batches.length === 0) return;
      const resumable = batches.find((ledger) =>
        ledger.status === "pending"
        || ledger.status === "paused"
        || ledger.status === "running"
        || ledger.status === "stopping"
        || ledger.items.some((item) => item.status === "failed"),
      );
      const latest = resumable ?? batches[0];
      batchIdRef.current = latest.batchId;
      setBatchLedger(latest);
      setBatchFolderPath((current) => current || latest.sourceRoot);
      setBatchConcurrency(latest.requestedConcurrency && latest.requestedConcurrency >= 1 && latest.requestedConcurrency <= 4
        ? latest.requestedConcurrency as 1 | 2 | 3 | 4
        : "auto");
    }).catch((reason) => {
      if (!disposed && generation === batchGenerationRef.current) {
        const error = parseCommandError(reason);
        setBatchScanError(language === "zh"
          ? `无法检查上次批量队列：${error.message}`
          : `Could not inspect the previous batch queue: ${error.message}`);
      }
    });
    return () => { disposed = true; };
  }, []);

  useEffect(() => {
    if (environmentReport?.cached) return;
    if (!environmentReport) {
      localStorage.removeItem(ENVIRONMENT_REPORT_STORAGE_KEY);
      return;
    }
    const saved: StoredEnvironmentReport = {
      cs2Path: environmentReport.cs2Root,
      report: environmentReport,
    };
    localStorage.setItem(ENVIRONMENT_REPORT_STORAGE_KEY, JSON.stringify(saved));
  }, [environmentReport]);

  useEffect(() => {
    persistLibraryPreferences(libraryPreferences);
  }, [libraryPreferences]);

  useEffect(() => {
    persistDemoSourceIndex(demoSourceIndex);
  }, [demoSourceIndex]);

  useEffect(() => {
    if (libraryRoot || !("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    void invoke<string>("default_library_dir").then((path) => {
      if (disposed || !path) return;
      const root = normalizeLibraryRoot(path);
      setLibraryScan(null);
      setLibraryPreferences({ exportRoot: root, roots: [root] });
      setOutputDir(root);
    }).catch((reason) => {
      if (!disposed) setGlobalError(parseCommandError(reason));
    });
    return () => { disposed = true; };
  }, [libraryRoot]);

  useEffect(() => {
    if (phase === "analysisFailed") retryButtonRef.current?.focus({ preventScroll: true });
    if (phase === "complete") resultHeadingRef.current?.focus({ preventScroll: true });
    if (phase === "archive") {
      window.requestAnimationFrame(() => {
        const firstRound = document.querySelector<HTMLButtonElement>('.archive-round-option[aria-pressed="true"]:not(:disabled)');
        const archiveHeading = document.querySelector<HTMLElement>("#archive-workspace-title");
        (firstRound ?? archiveHeading)?.focus({ preventScroll: true });
      });
    }
    if (phase === "selecting") {
      window.requestAnimationFrame(() => {
        const firstRound = document.querySelector<HTMLInputElement>('.round-data-table input[data-round-select="true"]:not(:disabled)');
        const suspiciousToggle = document.querySelector<HTMLInputElement>(".allow-suspicious-control input");
        (firstRound ?? suspiciousToggle)?.focus({ preventScroll: true });
      });
    }
  }, [phase]);

  const absorbEvent = useCallback((raw: TaskEvent, token: number) => {
    if (token !== taskTokenRef.current) return;

    if (raw.kind === "phase") {
      setProgress((current) => ({
        ...current,
        phase: phaseFromBackend(raw.phase, current.phase),
        unit: raw.phase === "voice" || raw.phase === "validating" ? null : current.unit,
        currentItem: raw.phase === "voice" || raw.phase === "validating" ? undefined : current.currentItem,
        announcement: raw.phase,
      }));
      return;
    }

    if (raw.kind === "log") {
      if (raw.level === "warning" && !taskWarningsRef.current.includes(raw.message) && taskWarningsRef.current.length < 6) {
        taskWarningsRef.current = [...taskWarningsRef.current, raw.message];
      }
      setProgress((current) => ({
        ...current,
        log: [...current.log.slice(-199), { level: raw.level, message: raw.message }],
        warnings: taskWarningsRef.current,
      }));
      return;
    }

    const event: ConversionProgressEvent = raw.progress;
    setProgress((current) => {
      switch (event.event) {
        case "analysisStarted":
          return { ...current, phase: "preparing", announcement: words.preparing };
        case "analysisFinished":
          return {
            ...current,
            phase: "writing",
            written: 0,
            estimated: event.estimatedFiles,
            unit: "playerFiles",
            selectedRounds: event.selectedRounds,
            announcement: words.writingPlayers,
          };
        case "roundStarted":
          return { ...current, phase: "writing", currentRound: event.round, currentItem: undefined };
        case "playerWritten":
          return { ...current, written: current.written + 1, currentItem: `${event.playerName} · ${event.side}` };
        case "roundFinished":
          return {
            ...current,
            completedRounds: current.completedRounds + 1,
            announcement: `Round ${event.round}`,
          };
        case "roundSkipped": {
          if (event.reason === "not selected") return current;
          const message = `Round ${event.round}: ${event.reason}`;
          const policySkip = event.reason.startsWith("suspicious (");
          if (!taskWarningsRef.current.includes(message) && taskWarningsRef.current.length < 6) taskWarningsRef.current = [...taskWarningsRef.current, message];
          return {
            ...current,
            completedRounds: current.completedRounds + (policySkip ? 0 : 1),
            log: [...current.log.slice(-199), { level: "warning", message }],
            warnings: taskWarningsRef.current,
            announcement: `Round ${event.round}`,
          };
        }
        case "playerSkipped": {
          const message = `Round ${event.round}: ${event.reason}`;
          if (!taskWarningsRef.current.includes(message) && taskWarningsRef.current.length < 6) taskWarningsRef.current = [...taskWarningsRef.current, message];
          return {
            ...current,
            log: [...current.log.slice(-199), { level: "warning", message }],
            warnings: taskWarningsRef.current,
          };
        }
        case "artifactsWritingStarted":
          return {
            ...current,
            phase: "artifacts",
            written: 0,
            estimated: event.artifacts,
            unit: "artifacts",
            currentItem: event.root,
            announcement: words.writingArtifacts,
          };
        case "artifactWritten":
          return { ...current, written: current.written + 1, currentItem: fileName(event.path) };
        case "finished":
          return { ...current, currentItem: fileName(event.manifestPath) };
      }
    });
  }, [words]);

  const runAnalysis = useCallback(async (path: string, expectedDemoSha256?: string) => {
    if (!isDemoFilePath(path)) {
      setGlobalError({ code: "invalid_demo_path", message: words.invalidDemo, path });
      return;
    }

    primeTaskSound();
    const token = ++taskTokenRef.current;
    const maxRoundSeconds = settings.maxRoundSeconds;
    analyzedMaxRoundSecondsRef.current = maxRoundSeconds;
    setGlobalError(null);
    setAnalysisError("");
    setValidationError("");
    setSourcePath(path);
    setAnalysis(null);
    setResult(null);
    setArchive(null);
    setArchivePath("");
    setSelectedArchiveRound(null);
    setOutputRoot("");
    setSelectedRounds(new Set());
    setInspectorSheetOpen(false);
    setCosmeticPhrase("");
    setSettings((current) => ({ ...current, exportCosmetics: false, includeSuspicious: false }));
    setProgress({ ...emptyProgress(), phase: "parsing" });
    setActiveSection("convert");
    setPhase("analyzing");
    taskWarningsRef.current = [];

    const events = new Channel<TaskEvent>();
    events.onmessage = (event) => absorbEvent(event, token);
    try {
      const next = await invoke<AnalysisResult>("analyze_demo", {
        request: {
          path,
          expectedDemoSha256: expectedDemoSha256 || null,
          maxRoundSeconds,
        },
        events,
      });
      if (token !== taskTokenRef.current) return;
      setSourcePath(next.sourcePath);
      setDemoSourceIndex((current) => rememberDemoSource(current, next.demoSha256, next.sourcePath));
      setAnalysis(next);
      setSelectedRounds(new Set(next.rounds.filter((round) => round.selectedByDefault).map((round) => round.round)));
      setOutputDir((current) => current || libraryRoot);
      setPhase("selecting");
      playTaskSound("success");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      const error = parseCommandError(reason);
      setAnalysisError(error.message);
      setPhase("analysisFailed");
      playTaskSound("failure");
    }
  }, [absorbEvent, libraryRoot, playTaskSound, primeTaskSound, settings.maxRoundSeconds, words.invalidDemo]);

  const runManifest = useCallback(async (path: string) => {
    if (isBusyRef.current) return;
    if (!path.toLowerCase().endsWith(".json")) {
      setGlobalError({ code: "invalid_manifest_path", message: words.invalidManifest, path });
      return;
    }

    const returnPhase = phase;
    const returnSection = activeSection;
    const token = ++taskTokenRef.current;
    setGlobalError(null);
    setArchivePath(path);
    setInspectorSheetOpen(false);
    setActiveSection("library");
    setPhase("openingArchive");
    try {
      const next = await invoke<ManifestArchive>("read_manifest", { path });
      if (token !== taskTokenRef.current) return;
      const availableRounds = next.rounds.filter((round) => round.available);
      const firstAvailableRound = availableRounds[0];
      setArchive(next);
      setArchivePath(next.manifestPath);
      setSourcePath(next.sourcePath ?? "");
      setSelectedArchiveRound(firstAvailableRound?.round ?? null);
      setCommandMode(availableRounds.length > 1 && (firstAvailableRound?.sequenceLength ?? 0) > 0 ? "sequence" : "round");
      setAnalysis(null);
      setResult(null);
      setOutputRoot(next.root);
      setSelectedRounds(new Set());
      setPhase("archive");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      setGlobalError(parseCommandError(reason));
      setPhase(returnPhase === "openingArchive" ? "idle" : returnPhase);
      setActiveSection(returnSection);
    }
  }, [activeSection, phase, words.invalidManifest]);

  const scanLibrary = useCallback(async (roots: string[]) => {
    const paths = uniqueLibraryRoots(roots);
    if (paths.length === 0 || !("__TAURI_INTERNALS__" in window)) return;
    const token = ++libraryScanTokenRef.current;
    setGlobalError(null);
    setLibraryLoading(true);
    try {
      const scans = await Promise.all(paths.map(async (root): Promise<DemoLibraryScan> => {
        try {
          return await invoke<DemoLibraryScan>("scan_demo_library", { root });
        } catch (reason) {
          const error = parseCommandError(reason);
          return {
            root,
            entries: [],
            skipped: [{ path: error.path ?? root, message: error.message }],
          };
        }
      }));
      if (token !== libraryScanTokenRef.current) return;
      const merged = mergeLibraryScans(scans, paths[0]);
      setLibraryScan(merged);
    } catch (reason) {
      if (token !== libraryScanTokenRef.current) return;
      const error = parseCommandError(reason);
      setGlobalError(error);
      setLibraryScan((current) => current ?? {
        root: paths[0],
        entries: [],
        skipped: [{ path: error.path ?? paths[0], message: error.message }],
      });
    } finally {
      if (token === libraryScanTokenRef.current) setLibraryLoading(false);
    }
  }, []);

  function applyBatchLedger(next: BatchLedger, generation: number, allowBatchSwitch = false) {
    if (generation !== batchGenerationRef.current) return;
    const activeBatchId = batchIdRef.current;
    if (activeBatchId && activeBatchId !== next.batchId && !allowBatchSwitch) return;
    batchIdRef.current = next.batchId;
    setBatchLedger((current) => {
      if (!current || current.batchId !== next.batchId || next.revision > current.revision) return next;
      return current;
    });
    setBatchFolderPath((current) => current || next.sourceRoot);
  }

  async function refreshBatchLedger(batchId: string, generation: number) {
    try {
      const next = await invoke<BatchLedger>("read_batch_import", { request: { batchId } });
      applyBatchLedger(next, generation);
    } catch {
      // The command that owns the batch will surface terminal errors. Event-time refreshes are
      // best effort so a transient read cannot hide the live per-item event stream.
    }
  }

  function updateBatchLedgerItem(batchId: string, itemId: string, patch: Partial<BatchItem>) {
    setBatchLedger((current) => {
      if (!current || current.batchId !== batchId) return current;
      return {
        ...current,
        items: current.items.map((item) => item.itemId === itemId ? { ...item, ...patch } : item),
      };
    });
  }

  function handleBatchEvent(event: BatchEvent, generation: number) {
    if (generation !== batchGenerationRef.current) return;
    if (batchIdRef.current && batchIdRef.current !== event.batchId) return;
    batchIdRef.current = event.batchId;
    if ("parseEtaSeconds" in event && event.parseEtaSeconds !== undefined) {
      setBatchEtaSeconds(event.parseEtaSeconds ?? null);
    }
    switch (event.kind) {
      case "started":
        void refreshBatchLedger(event.batchId, generation);
        if (batchStopPendingRef.current) void requestBatchCancel(event.batchId, generation);
        break;
      case "itemPhase":
        updateBatchLedgerItem(event.batchId, event.itemId, {
          status: event.phase === "complete" ? "completed" : event.phase === "failed" ? "failed" : "running",
          phase: event.phase,
        });
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: {
            ...(current[event.itemId] ?? { written: 0, estimated: 0 }),
            startedAtMs: current[event.itemId]?.startedAtMs ?? Date.now(),
          },
        }));
        break;
      case "itemTask":
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: nextBatchItemProgress(current[event.itemId], event.task),
        }));
        break;
      case "estimateUpdated":
        setBatchEtaSeconds(event.parseEtaSeconds);
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "itemCompleted":
        updateBatchLedgerItem(event.batchId, event.itemId, {
          status: "completed",
          phase: "complete",
          archiveRoot: event.archiveRoot,
          manifestPath: event.manifestPath,
          error: null,
        });
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: {
            ...(current[event.itemId] ?? { written: 0, estimated: 0 }),
            progress: 1,
            finishedAtMs: Date.now(),
          },
        }));
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "itemFailed":
        updateBatchLedgerItem(event.batchId, event.itemId, {
          status: "failed",
          phase: "failed",
          error: event.error,
        });
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: {
            ...(current[event.itemId] ?? { written: 0, estimated: 0 }),
            finishedAtMs: Date.now(),
          },
        }));
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "paused":
        setBatchLedger((current) => current?.batchId === event.batchId ? { ...current, status: "paused" } : current);
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "finished":
        setBatchLedger((current) => current?.batchId === event.batchId
          ? { ...current, status: event.failed > 0 ? "completedWithErrors" : "completed" }
          : current);
        void refreshBatchLedger(event.batchId, generation);
        break;
    }
  }

  async function scanBatchFolder(root = batchFolderPath) {
    const folder = root.trim();
    if (!folder || batchScanning || batchInvocationActive) return;
    setBatchScanning(true);
    setBatchScanError("");
    try {
      const scan = await invoke<DemoFolderScan>("scan_demo_folder", {
        request: { root: folder, recursive: true, limit: 512 },
      });
      setBatchFolderPath(scan.root);
      setBatchScan(scan);
      const imported = new Set(
        (libraryScan?.entries ?? [])
          .map((entry) => entry.sourcePath)
          .filter((path): path is string => Boolean(path))
          .map(normalizedDiagnosticPath),
      );
      setBatchSelectedIds(scan.candidates
        .filter((candidate) => !imported.has(normalizedDiagnosticPath(candidate.path)))
        .slice(0, BATCH_SELECTION_LIMIT)
        .map((candidate) => normalizedDiagnosticPath(candidate.path)));
      const notices = [
        scan.truncated
          ? (language === "zh" ? `扫描结果已限制为前 ${scan.limit} 个 Demo。` : `Scan results were limited to the first ${scan.limit} demos.`)
          : "",
        scan.skippedReparsePoints > 0
          ? (language === "zh" ? `为避免越界，跳过了 ${scan.skippedReparsePoints} 个链接或 junction。` : `Skipped ${scan.skippedReparsePoints} links or junctions to stay inside the selected tree.`)
          : "",
        ...scan.warnings.slice(0, 3),
      ].filter(Boolean);
      setBatchScanError(notices.join(" "));
    } catch (reason) {
      setBatchScan(null);
      setBatchSelectedIds([]);
      setBatchScanError(parseCommandError(reason).message);
    } finally {
      setBatchScanning(false);
    }
  }

  async function chooseBatchFolder() {
    if (batchScanning || batchInvocationActive) return;
    try {
      const path = await invoke<string | null>("choose_demo_batch_dir", {
        request: { initialPath: batchFolderPath || null },
      });
      if (!path) return;
      setBatchFolderPath(path);
      await scanBatchFolder(path);
    } catch (reason) {
      setBatchScanError(parseCommandError(reason).message);
    }
  }

  async function startBatchImport(candidateIds: string[]) {
    if (batchInvocationActive || candidateIds.length === 0) return;
    let destination = libraryRoot;
    if (!destination) destination = (await chooseLibraryRoot()) ?? "";
    if (!destination) return;
    const selected = new Set(candidateIds.slice(0, BATCH_SELECTION_LIMIT));
    const candidates = batchCandidates.filter((candidate) => selected.has(candidate.id) && candidate.status === "ready");
    if (candidates.length !== selected.size) {
      setBatchSelectedIds(candidates.map((candidate) => candidate.id));
      setGlobalError({
        code: "batch_selection_changed",
        message: language === "zh"
          ? "本地库状态刚刚发生变化，已从选择中移除已入库的 Demo。请确认剩余项目后再启动。"
          : "The local library changed, so already imported demos were removed from the selection. Review the remaining items and start again.",
      });
      return;
    }
    if (candidates.length === 0) return;

    primeTaskSound();
    const generation = ++batchGenerationRef.current;
    batchStopPendingRef.current = false;
    batchCancelGenerationRef.current = -1;
    setBatchStopPending(false);
    batchIdRef.current = "";
    setBatchLedger(null);
    setBatchProgressByItem({});
    setBatchEtaSeconds(null);
    setBatchStartingCandidates(candidates);
    setBatchInvocationActive(true);
    setGlobalError(null);
    const events = new Channel<BatchEvent>();
    events.onmessage = (event) => handleBatchEvent(event, generation);
    try {
      const next = await invoke<BatchLedger>("start_batch_import", {
        request: {
          sourceRoot: batchScan?.root ?? batchFolderPath,
          libraryRoot: destination,
          demoPaths: candidates.map((candidate) => candidate.path),
          concurrency: batchConcurrency === "auto" ? null : batchConcurrency,
          settings: {
            includeSuspicious: settings.includeSuspicious,
            fullRound: settings.fullRound,
            side: settings.side,
            subtickMode: settings.subtickMode,
            maxRoundSeconds: settings.maxRoundSeconds,
            freezePrerollSeconds: settings.freezePrerollSeconds,
            exportVoice: settings.exportVoice,
          },
        },
        events,
      });
      if (generation !== batchGenerationRef.current) return;
      applyBatchLedger(next, generation, true);
      setBatchSelectedIds([]);
      if (next.status === "paused") playTaskSound("stopped");
      else if (next.items.some((item) => item.status === "failed")) playTaskSound("failure");
      else playTaskSound("success");
      await scanLibrary(withExportRoot(libraryRoots, destination));
    } catch (reason) {
      if (generation !== batchGenerationRef.current) return;
      setGlobalError(parseCommandError(reason));
      playTaskSound("failure");
      if (batchIdRef.current) await refreshBatchLedger(batchIdRef.current, generation);
    } finally {
      if (generation === batchGenerationRef.current) {
        batchStopPendingRef.current = false;
        setBatchStopPending(false);
        setBatchStartingCandidates([]);
        setBatchInvocationActive(false);
      }
    }
  }

  async function resumeBatchImport(itemId?: string) {
    if (batchInvocationActive || !batchLedger) return;
    primeTaskSound();
    const generation = ++batchGenerationRef.current;
    batchStopPendingRef.current = false;
    batchCancelGenerationRef.current = -1;
    setBatchStopPending(false);
    setBatchInvocationActive(true);
    setBatchProgressByItem({});
    setBatchEtaSeconds(null);
    setGlobalError(null);
    const batchId = batchLedger.batchId;
    batchIdRef.current = batchId;
    const events = new Channel<BatchEvent>();
    events.onmessage = (event) => handleBatchEvent(event, generation);
    try {
      const next = await invoke<BatchLedger>("resume_batch_import", {
        request: {
          batchId,
          retryFailed: Boolean(itemId),
          itemId: itemId ?? null,
        },
        events,
      });
      if (generation !== batchGenerationRef.current) return;
      applyBatchLedger(next, generation);
      if (next.status === "paused") playTaskSound("stopped");
      else if (next.items.some((item) => item.status === "failed")) playTaskSound("failure");
      else playTaskSound("success");
      await scanLibrary(withExportRoot(libraryRoots, next.libraryRoot));
    } catch (reason) {
      if (generation !== batchGenerationRef.current) return;
      setGlobalError(parseCommandError(reason));
      playTaskSound("failure");
      await refreshBatchLedger(batchId, generation);
    } finally {
      if (generation === batchGenerationRef.current) {
        batchStopPendingRef.current = false;
        setBatchStopPending(false);
        setBatchInvocationActive(false);
      }
    }
  }

  async function requestBatchCancel(batchId: string, generation: number) {
    if (generation !== batchGenerationRef.current || batchCancelGenerationRef.current === generation) return;
    batchCancelGenerationRef.current = generation;
    setBatchLedger((current) => current?.batchId === batchId ? { ...current, status: "stopping", cancelRequested: true } : current);
    try {
      const next = await invoke<BatchLedger>("cancel_batch_import", { request: { batchId } });
      if (generation !== batchGenerationRef.current) return;
      applyBatchLedger(next, generation);
    } catch (reason) {
      if (generation !== batchGenerationRef.current) return;
      batchStopPendingRef.current = false;
      setBatchStopPending(false);
      setBatchLedger((current) => current?.batchId === batchId && current.status === "stopping"
        ? { ...current, status: "running", cancelRequested: false }
        : current);
      setGlobalError(parseCommandError(reason));
    } finally {
      if (batchCancelGenerationRef.current === generation) batchCancelGenerationRef.current = -1;
    }
  }

  function stopBatchImport() {
    if (!batchInvocationActive) return;
    batchStopPendingRef.current = true;
    setBatchStopPending(true);
    const batchId = batchIdRef.current || batchLedger?.batchId;
    if (batchId) void requestBatchCancel(batchId, batchGenerationRef.current);
  }

  useEffect(() => {
    if (libraryRoots.length > 0) void scanLibrary(libraryRoots);
  }, [libraryRoots, scanLibrary]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window) || !analysis || !outputDir || phase !== "selecting") return;
    let disposed = false;
    void invoke<OutputPreflight>("preflight_output", {
      request: { analysisId: analysis.analysisId, outputDir },
    }).then((preflight) => {
      if (!disposed) setOutputRoot(preflight.root);
    }).catch((reason) => {
      if (!disposed) setGlobalError(parseCommandError(reason));
    });
    return () => { disposed = true; };
  }, [analysis, outputDir, phase]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let unlisten: (() => void) | undefined;
    void getCurrentWebview().onDragDropEvent((event) => {
      if (event.payload.type === "enter" || event.payload.type === "over") {
        if (!isBusy) setDragActive(true);
        return;
      }
      if (event.payload.type === "leave") {
        setDragActive(false);
        return;
      }
      setDragActive(false);
      if (isBusy) return;
      if (event.payload.paths.length !== 1) {
        setGlobalError({ code: "single_demo_only", message: words.singleDemoOnly });
        return;
      }
      const path = event.payload.paths[0];
      const lowered = path.toLowerCase();
      if (isDemoFilePath(lowered)) void runAnalysis(path);
      else if (lowered.endsWith(".json")) void runManifest(path);
      else setGlobalError({ code: "invalid_input_path", message: words.invalidInput, path });
    }).then((stop) => { unlisten = stop; });
    return () => unlisten?.();
  }, [isBusy, runAnalysis, runManifest, words.invalidInput, words.singleDemoOnly]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    let unlisten: (() => void) | undefined;
    void getCurrentWindow().onCloseRequested((event) => {
      if (isBusyRef.current) {
        event.preventDefault();
        setCloseOpen(true);
      }
    }).then((stop) => {
      if (disposed) stop();
      else unlisten = stop;
    }).catch(() => undefined);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  useEffect(() => {
    const handleShortcut = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey || event.key.toLowerCase() !== "o") return;
      if (isBusy || overwriteConflict || cosmeticOpen || closeOpen || inspectorSheetOpen) return;
      event.preventDefault();
      if (event.shiftKey) void chooseManifest();
      else void chooseDemo();
    };
    window.addEventListener("keydown", handleShortcut);
    return () => window.removeEventListener("keydown", handleShortcut);
  });

  async function chooseDemo(initialPath = "") {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_demo", { initialPath: initialPath || null });
      if (path) await runAnalysis(path);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function chooseManifest() {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_manifest");
      if (path) await runManifest(path);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function addLibraryRoot() {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_library_dir");
      if (!path) return;
      setLibraryNotice("");
      setLibraryScan(null);
      setLibraryPreferences((current) => ({
        ...current,
        roots: withExportRoot([...current.roots, path], current.exportRoot),
      }));
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  function removeLibraryRoot(root: string) {
    if (isBusy || root.toLocaleLowerCase() === libraryRoot.toLocaleLowerCase()) return;
    setLibraryNotice("");
    setLibraryScan(null);
    setLibraryPreferences((current) => ({
      ...current,
      roots: current.roots.filter((item) => item.toLocaleLowerCase() !== root.toLocaleLowerCase()),
    }));
  }

  async function chooseLibraryRoot(): Promise<string | null> {
    if (isBusy) return null;
    try {
      const path = await invoke<string | null>("choose_library_dir");
      if (!path) return null;
      const root = normalizeLibraryRoot(path);
      setLibraryNotice("");
      setLibraryScan(null);
      setLibraryPreferences((current) => ({
        exportRoot: root,
        roots: withExportRoot(current.roots, root),
      }));
      setOutputDir(root);
      setOutputRoot("");
      return root;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
    }
  }

  async function repairArchiveMetadata(entry: DemoLibraryEntry) {
    if (isBusy) return;
    const needsMetadata = entry.metadataStatus !== "current";
    if (!needsMetadata) {
      try {
        setGlobalError(null);
        setLibraryNotice("");
        setRepairingManifest(entry.manifestPath);
        const resolvedSource = await resolveManifestDemoSource(entry);
        if (!resolvedSource) return;
        const name = entry.displayName || fileName(entry.demoPath) || entry.demoId;
        const notice = words.linkSourceResult.replace("{name}", name);
        setLibraryNotice(notice);
        setLiveMessage(notice);
        await scanLibrary(libraryRoots);
      } catch (reason) {
        setGlobalError(parseCommandError(reason));
      } finally {
        setRepairingManifest("");
      }
      return;
    }
    const recordedSource = entry.sourcePath?.trim() || "";
    const indexedSource = demoSourceIndex[entry.demoSha256.trim().toLocaleLowerCase()] || "";
    const recoverableSourceErrors = new Set([
      "source_demo_unavailable",
      "invalid_demo_path",
      "metadata_demo_read_failed",
      "metadata_demo_hash_mismatch",
    ]);
    try {
      setGlobalError(null);
      setLibraryNotice("");
      setRepairingManifest(entry.manifestPath);
      let result: RefreshArchiveMetadataResult | null = null;
      let sourceError: CommandErrorDto | null = null;
      const automaticCandidates: Array<string | null> = [null];
      if (indexedSource && indexedSource.toLocaleLowerCase() !== recordedSource.toLocaleLowerCase()) {
        automaticCandidates.push(indexedSource);
      }
      for (const demoPath of automaticCandidates) {
        try {
          result = await invoke<RefreshArchiveMetadataResult>("refresh_archive_metadata", {
            request: { manifestPath: entry.manifestPath, demoPath },
          });
          break;
        } catch (reason) {
          sourceError = parseCommandError(reason);
          if (!recoverableSourceErrors.has(sourceError.code)) throw reason;
        }
      }
      if (!result) {
        const demoPath = await invoke<string | null>("choose_demo", {
          initialPath: sourceError?.path || recordedSource || indexedSource || entry.demoPath || null,
        });
        if (!demoPath) return;
        result = await invoke<RefreshArchiveMetadataResult>("refresh_archive_metadata", {
          request: { manifestPath: entry.manifestPath, demoPath },
        });
      }
      setDemoSourceIndex((current) => rememberDemoSource(current, entry.demoSha256, result.sourcePath));
      const notice = words.repairArchiveResult.replace("{name}", result.displayName);
      setLibraryNotice(notice);
      setLiveMessage(notice);
      await scanLibrary(libraryRoots);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setRepairingManifest("");
    }
  }

  async function resolveManifestDemoSource(source: {
    manifestPath: string;
    demoSha256: string;
    demoPath: string;
    sourcePath?: string | null;
  }): Promise<string | null> {
    const recordedSource = source.sourcePath?.trim() || "";
    const indexedSource = demoSourceIndex[source.demoSha256.trim().toLocaleLowerCase()] || "";
    const recoverableSourceErrors = new Set([
      "source_demo_unavailable",
      "invalid_demo_path",
      "metadata_demo_read_failed",
      "metadata_demo_hash_mismatch",
    ]);
    let sourceError: CommandErrorDto | null = null;
    const automaticCandidates: Array<string | null> = [null];
    if (indexedSource && indexedSource.toLocaleLowerCase() !== recordedSource.toLocaleLowerCase()) {
      automaticCandidates.push(indexedSource);
    }
    let result: ResolveArchiveSourceResult | null = null;
    for (const demoPath of automaticCandidates) {
      try {
        result = await invoke<ResolveArchiveSourceResult>("resolve_archive_source", {
          request: { manifestPath: source.manifestPath, demoPath },
        });
        break;
      } catch (reason) {
        sourceError = parseCommandError(reason);
        if (!recoverableSourceErrors.has(sourceError.code)) throw reason;
      }
    }
    if (!result) {
      const demoPath = await invoke<string | null>("choose_demo", {
        initialPath: sourceError?.path || recordedSource || indexedSource || source.demoPath || null,
      });
      if (!demoPath) return null;
      result = await invoke<ResolveArchiveSourceResult>("resolve_archive_source", {
        request: { manifestPath: source.manifestPath, demoPath },
      });
    }
    setDemoSourceIndex((current) => rememberDemoSource(
      current,
      source.demoSha256,
      result.sourcePath,
    ));
    return result.sourcePath;
  }

  async function reconvertArchive(selectedArchive: ManifestArchive) {
    if (isBusy) return;
    try {
      setGlobalError(null);
      setRepairingManifest(selectedArchive.manifestPath);
      const resolvedSource = await resolveManifestDemoSource(selectedArchive);
      if (!resolvedSource) return;
      setArchive((current) => current?.manifestPath === selectedArchive.manifestPath
        ? { ...current, sourcePath: resolvedSource }
        : current);
      setSourcePath(resolvedSource);
      setRepairingManifest("");
      await runAnalysis(resolvedSource, selectedArchive.demoSha256);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setRepairingManifest("");
    }
  }

  async function repairLibraryMetadata() {
    const roots = uniqueLibraryRoots(libraryRoots);
    if (isBusy || roots.length === 0) return;
    let shouldRescan = false;
    try {
      setGlobalError(null);
      setLibraryNotice("");
      setRepairingLibrary(true);
      shouldRescan = true;
      let workingSourceIndex = { ...demoSourceIndex };
      const absorbVerifiedSources = (result: RefreshLibraryMetadataResult) => {
        workingSourceIndex = Object.entries(result.sourcePaths).reduce(
          (index, [hash, path]) => rememberDemoSource(index, hash, path),
          workingSourceIndex,
        );
      };
      const failedRootResult = (
        root: string,
        reason: unknown,
        fallback?: RefreshLibraryMetadataResult,
      ): RefreshLibraryMetadataResult => {
        const error = parseCommandError(reason);
        return {
          demosScanned: 0,
          demosMatched: 0,
          archivesUpdated: 0,
          archivesCurrent: fallback?.archivesCurrent ?? 0,
          archivesUnmatched: fallback?.archivesUnmatched ?? 0,
          sourceUnmatched: fallback?.sourceUnmatched ?? 0,
          sourcePaths: {},
          failures: [`${root}: ${error.message}`],
        };
      };
      const firstPass = new Map<string, RefreshLibraryMetadataResult>();
      for (const root of roots) {
        try {
          const result = await invoke<RefreshLibraryMetadataResult>("refresh_library_metadata", {
            request: { libraryRoot: root, demoRoot: null, sourcePaths: workingSourceIndex },
          });
          firstPass.set(root, result);
          absorbVerifiedSources(result);
        } catch (reason) {
          firstPass.set(root, failedRootResult(root, reason));
        }
      }
      const automaticRetry = new Map<string, RefreshLibraryMetadataResult>();
      for (const root of roots.filter((candidate) => (firstPass.get(candidate)?.sourceUnmatched ?? 0) > 0)) {
        try {
          const result = await invoke<RefreshLibraryMetadataResult>("refresh_library_metadata", {
            request: { libraryRoot: root, demoRoot: null, sourcePaths: workingSourceIndex },
          });
          automaticRetry.set(root, result);
          absorbVerifiedSources(result);
        } catch (reason) {
          automaticRetry.set(root, failedRootResult(root, reason, firstPass.get(root)));
        }
      }
      let unresolvedRoots = roots.filter((root) => (
        automaticRetry.get(root)?.sourceUnmatched
        ?? firstPass.get(root)?.sourceUnmatched
        ?? 0
      ) > 0);
      const directoryPasses = new Map<string, RefreshLibraryMetadataResult[]>();
      const searchDemoRoot = async (demoRoot: string) => {
        for (const root of unresolvedRoots) {
          const previous = directoryPasses.get(root)?.at(-1)
            ?? automaticRetry.get(root)
            ?? firstPass.get(root);
          try {
            const result = await invoke<RefreshLibraryMetadataResult>("refresh_library_metadata", {
              request: { libraryRoot: root, demoRoot, sourcePaths: workingSourceIndex },
            });
            directoryPasses.set(root, [...(directoryPasses.get(root) ?? []), result]);
            absorbVerifiedSources(result);
          } catch (reason) {
            directoryPasses.set(root, [
              ...(directoryPasses.get(root) ?? []),
              failedRootResult(root, reason, previous),
            ]);
          }
        }
        unresolvedRoots = roots.filter((root) => (
          directoryPasses.get(root)?.at(-1)?.sourceUnmatched
          ?? automaticRetry.get(root)?.sourceUnmatched
          ?? firstPass.get(root)?.sourceUnmatched
          ?? 0
        ) > 0);
      };
      for (const demoRoot of uniqueLibraryRoots(localEnvironment.demoRoots)) {
        if (unresolvedRoots.length === 0) break;
        await searchDemoRoot(demoRoot);
      }
      if (unresolvedRoots.length > 0) {
        const initialPath = libraryScan?.entries.find((entry) => entry.sourcePath)?.sourcePath
          || Object.values(workingSourceIndex).at(-1)
          || localEnvironment.demoRoots.at(-1)
          || null;
        const demoRoot = await invoke<string | null>("choose_demo_source_dir", { initialPath });
        if (demoRoot) {
          await searchDemoRoot(demoRoot);
        }
      }
      const result = roots.reduce<RefreshLibraryMetadataResult>((total, root) => {
        const first = firstPass.get(root)!;
        const retry = automaticRetry.get(root);
        const searchResults = directoryPasses.get(root) ?? [];
        const searched = searchResults.at(-1);
        const latest = searched ?? retry ?? first;
        return {
          demosScanned: total.demosScanned + first.demosScanned
            + (retry?.demosScanned ?? 0)
            + searchResults.reduce((sum, item) => sum + item.demosScanned, 0),
          demosMatched: total.demosMatched + first.demosMatched
            + (retry?.demosMatched ?? 0)
            + searchResults.reduce((sum, item) => sum + item.demosMatched, 0),
          archivesUpdated: total.archivesUpdated + first.archivesUpdated
            + (retry?.archivesUpdated ?? 0)
            + searchResults.reduce((sum, item) => sum + item.archivesUpdated, 0),
          archivesCurrent: total.archivesCurrent + latest.archivesCurrent,
          archivesUnmatched: total.archivesUnmatched + latest.archivesUnmatched,
          sourceUnmatched: total.sourceUnmatched + latest.sourceUnmatched,
          sourcePaths: {
            ...total.sourcePaths,
            ...first.sourcePaths,
            ...(retry?.sourcePaths ?? {}),
            ...Object.assign({}, ...searchResults.map((item) => item.sourcePaths)),
          },
          failures: [
            ...total.failures,
            ...first.failures,
            ...(retry?.failures ?? []),
            ...searchResults.flatMap((item) => item.failures),
          ],
        };
      }, {
        demosScanned: 0,
        demosMatched: 0,
        archivesUpdated: 0,
        archivesCurrent: 0,
        archivesUnmatched: 0,
        sourceUnmatched: 0,
        sourcePaths: {},
        failures: [],
      });
      setDemoSourceIndex(workingSourceIndex);
      const notice = words.repairLibraryResult
        .replace("{updated}", String(result.archivesUpdated))
        .replace("{unmatched}", String(result.sourceUnmatched))
        .replace("{failed}", String(result.failures.length));
      setLibraryNotice(notice);
      setLiveMessage(notice);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      if (shouldRescan) await scanLibrary(libraryRoots);
      setRepairingLibrary(false);
    }
  }

  async function importArchives() {
    if (isBusy || !libraryRoot) return;
    try {
      const sourceRoot = await invoke<string | null>("choose_library_dir");
      if (!sourceRoot) return;
      setGlobalError(null);
      setLibraryNotice("");
      setImportingArchives(true);
      const result = await invoke<ImportArchivesResult>("import_archives", {
        request: { libraryRoot, sourceRoot },
      });
      const notice = words.importArchivesResult
        .replace("{imported}", String(result.archivesImported))
        .replace("{duplicates}", String(result.duplicatesSkipped))
        .replace("{rejected}", String(result.archivesRejected));
      setLibraryNotice(notice);
      setLiveMessage(notice);
      await scanLibrary(libraryRoots);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setImportingArchives(false);
    }
  }

  async function chooseOutput(): Promise<string | null> {
    try {
      const path = await invoke<string | null>("choose_output_dir");
      if (path) {
        const root = normalizeLibraryRoot(path);
        setLibraryNotice("");
        setLibraryScan(null);
        setLibraryPreferences((current) => ({
          exportRoot: root,
          roots: withExportRoot(current.roots, root),
        }));
        setOutputDir(root);
        setOutputRoot("");
        return root;
      }
      return path;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
    }
  }

  async function runEnvironmentInspection(path = localEnvironment.cs2Path) {
    const candidate = path.trim();
    if (!candidate || inspectingEnvironment) return;
    const token = ++environmentInspectionTokenRef.current;
    setGlobalError(null);
    setInspectingEnvironment(true);
    try {
      const report = await invoke<EnvironmentDiagnosticReport>("inspect_cs2_install", { path: candidate });
      if (token !== environmentInspectionTokenRef.current) return;
      setLocalEnvironment((current) => ({ ...current, cs2Path: report.cs2Root || candidate }));
      setEnvironmentReport(report);
    } catch (reason) {
      if (token !== environmentInspectionTokenRef.current) return;
      setGlobalError(parseCommandError(reason));
    } finally {
      if (token === environmentInspectionTokenRef.current) setInspectingEnvironment(false);
    }
  }

  async function chooseCs2Directory() {
    if (detectingInstallations || inspectingEnvironment) return;
    try {
      const path = await invoke<string | null>("choose_cs2_dir", {
        initialPath: localEnvironment.cs2Path.trim() || null,
      });
      if (!path) return;
      setLocalEnvironment((current) => ({ ...current, cs2Path: path }));
      setEnvironmentReport(null);
      await runEnvironmentInspection(path);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function detectCs2Installations() {
    if (detectingInstallations || inspectingEnvironment) return;
    setGlobalError(null);
    setInstallCandidates([]);
    setInstallDetectionCompleted(false);
    setDetectingInstallations(true);
    try {
      const candidates = await invoke<Cs2InstallCandidate[]>("detect_cs2_installations");
      setInstallCandidates(candidates);
      setInstallDetectionCompleted(true);
      if (candidates.length === 1) {
        const [candidate] = candidates;
        setLocalEnvironment((current) => ({ ...current, cs2Path: candidate.path }));
        setEnvironmentReport(null);
        await runEnvironmentInspection(candidate.path);
      }
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setDetectingInstallations(false);
    }
  }

  function useCs2Candidate(candidate: Cs2InstallCandidate) {
    setLocalEnvironment((current) => ({ ...current, cs2Path: candidate.path }));
    setEnvironmentReport(null);
    void runEnvironmentInspection(candidate.path);
  }

  async function addDemoRoot() {
    if (isBusy) return;
    try {
      const initialPath = localEnvironment.demoRoots.at(-1) ?? "";
      const path = await invoke<string | null>("choose_demo_source_dir", {
        initialPath: initialPath || null,
      });
      if (!path) return;
      setLocalEnvironment((current) => ({
        ...current,
        demoRoots: uniqueLibraryRoots([...current.demoRoots, path]),
      }));
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  function removeDemoRoot(root: string) {
    setLocalEnvironment((current) => ({
      ...current,
      demoRoots: current.demoRoots.filter((candidate) => candidate.toLocaleLowerCase() !== root.toLocaleLowerCase()),
    }));
  }

  async function loadServerConfig() {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || loadingServerConfig || savingServerConfig) return;
    setLoadingServerConfig(true);
    setGlobalError(null);
    try {
      const document = await invoke<ServerConfigDocument>("load_server_config", { cs2Path });
      setServerConfigDocument(document);
      setServerConfigDraft(document.normalizedJson || document.json);
      setServerConfigValidation(document.validation);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setLoadingServerConfig(false);
    }
  }

  async function validateServerConfigDraft() {
    if (!serverConfigDraft.trim()) return;
    try {
      const validation = await invoke<ServerConfigValidation>("validate_server_config", {
        request: { json: serverConfigDraft },
      });
      setServerConfigValidation(validation);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function saveServerConfig() {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || !serverConfigDocument || savingServerConfig || !serverConfigDraft.trim()) return;
    primeTaskSound();
    setSavingServerConfig(true);
    setGlobalError(null);
    try {
      const saved = await invoke<SaveServerConfigResult>("save_server_config", {
        request: {
          cs2Path,
          json: serverConfigDraft,
          expectedFingerprint: serverConfigDocument.fingerprint ?? null,
          replaceExisting: false,
        },
      });
      setServerConfigDocument(saved.document);
      setServerConfigDraft(saved.document.normalizedJson || saved.document.json);
      setServerConfigValidation(saved.document.validation);
      playTaskSound("success");
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      playTaskSound("failure");
    } finally {
      setSavingServerConfig(false);
    }
  }

  async function preflightOutput(destination: string): Promise<OutputPreflight | null> {
    if (!analysis) return null;
    try {
      const preflight = await invoke<OutputPreflight>("preflight_output", {
        request: { analysisId: analysis.analysisId, outputDir: destination },
      });
      setOutputRoot(preflight.root);
      return preflight;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
    }
  }

  function toggleRound(round: RoundInfo) {
    if (round.status === "suspicious" && !settings.includeSuspicious) return;
    setSelectedRounds((current) => {
      const next = new Set(current);
      if (next.has(round.round)) next.delete(round.round);
      else next.add(round.round);
      return next;
    });
  }

  function restoreRecommended() {
    if (!analysis) return;
    setSelectedRounds(new Set(analysis.rounds.filter((round) => round.selectedByDefault).map((round) => round.round)));
  }

  function handleAllowSuspicious(checked: boolean) {
    setSettings((current) => ({ ...current, includeSuspicious: checked }));
    if (!checked && analysis) {
      const blocked = new Set(analysis.rounds.filter((round) => round.status === "suspicious").map((round) => round.round));
      setSelectedRounds((current) => new Set([...current].filter((round) => !blocked.has(round))));
    }
  }

  function updateSettings(patch: Partial<ConverterSettings>) {
    if (patch.exportCosmetics === false) setCosmeticPhrase("");
    setSettings((current) => ({ ...current, ...patch }));
  }

  function restoreDefaultSettings() {
    setSettings({ ...DEFAULT_SETTINGS });
    setCosmeticPhrase("");
    if (analysis) {
      const suspicious = new Set(analysis.rounds.filter((round) => round.status === "suspicious").map((round) => round.round));
      setSelectedRounds((current) => new Set([...current].filter((round) => !suspicious.has(round))));
    }
  }

  async function beginConvert() {
    if (!analysis || selectedRounds.size === 0) return;
    let destination = outputDir;
    if (!destination) destination = (await chooseOutput()) ?? "";
    if (!destination) return;
    if (settings.exportCosmetics && !consentIsValid(cosmeticPhrase)) {
      setCosmeticOpen(true);
      return;
    }
    const preflight = await preflightOutput(destination);
    if (!preflight) return;
    if (preflight.exists) {
      setOverwriteConflict(preflight);
      return;
    }
    await performConvert(false, destination);
  }

  async function performConvert(overwrite: boolean, destination = outputDir) {
    if (!analysis || selectedRounds.size === 0 || !destination) return;
    primeTaskSound();
    const token = ++taskTokenRef.current;
    taskWarningsRef.current = [];
    setGlobalError(null);
    setValidationError("");
    setConversionWarnings([]);
    setOverwriteConflict(null);
    setInspectorSheetOpen(false);
    setResult(null);
    setProgress(emptyProgress());
    setPhase("converting");

    const events = new Channel<TaskEvent>();
    events.onmessage = (event) => absorbEvent(event, token);
    try {
      const summary = await invoke<ConversionSummary>("convert_demo", {
        request: {
          analysisId: analysis.analysisId,
          outputDir: destination,
          selectedRounds: [...selectedRounds].sort((left, right) => left - right),
          includeSuspicious: settings.includeSuspicious,
          fullRound: settings.fullRound,
          side: settings.side,
          subtickMode: settings.subtickMode,
          freezePrerollSeconds: settings.freezePrerollSeconds,
          maxRoundSeconds: analyzedMaxRoundSecondsRef.current,
          exportVoice: settings.exportVoice,
          exportCosmetics: settings.exportCosmetics,
          exportStickers: settings.exportCosmetics && settings.exportStickers,
          exportCharms: settings.exportCosmetics && settings.exportCharms,
          cosmeticConsent: settings.exportCosmetics ? { phrase: cosmeticPhrase } : null,
          overwrite: overwrite ? "replace" : "deny",
        },
        events,
      });
      if (token !== taskTokenRef.current) return;
      setResult(summary);
      setOutputRoot(summary.root);
      setConversionWarnings(taskWarningsRef.current);
      setCommandMode(summary.rounds.length > 1 ? "sequence" : "round");
      setProgress((current) => ({ ...current, phase: "complete" }));
      setLibraryPreferences((current) => ({
        exportRoot: destination,
        roots: withExportRoot(current.roots, destination),
      }));
      setOutputDir(destination);
      void scanLibrary(withExportRoot(libraryRoots, destination));
      setPhase("complete");
      playTaskSound("success");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      const error = parseCommandError(reason);
      if (error.code === "output_exists") {
        setOverwriteConflict({ root: error.path || outputRoot, exists: true });
        setPhase("selecting");
      } else if (error.code === "validation_failed") {
        setValidationError(error.message);
        setPhase("validationFailed");
      } else {
        setGlobalError(error);
        setPhase("selecting");
      }
      playTaskSound("failure");
    }
  }

  async function openPath(path: string) {
    if (!path) return;
    try {
      await invoke("open_output", { request: { path } });
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function copyText(value: string, target: CopyTarget) {
    try {
      try {
        await navigator.clipboard.writeText(value);
      } catch {
        const textArea = document.createElement("textarea");
        textArea.value = value;
        textArea.style.position = "fixed";
        textArea.style.opacity = "0";
        document.body.appendChild(textArea);
        textArea.select();
        const copied = document.execCommand("copy");
        textArea.remove();
        if (!copied) throw new Error(words.copyFailed);
      }
      setCopiedTarget(target);
      setLiveMessage(words.copied);
      window.setTimeout(() => {
        setCopiedTarget((current) => current === target ? null : current);
        setLiveMessage("");
      }, 2000);
    } catch (reason) {
      setGlobalError({ code: "copy_failed", message: parseCommandError(reason).message });
      setLiveMessage(words.copyFailed);
    }
  }

  function resetSession() {
    ++taskTokenRef.current;
    setActiveSection("library");
    setPhase("idle");
    setSourcePath("");
    setOutputRoot("");
    setAnalysis(null);
    setResult(null);
    setArchive(null);
    setArchivePath("");
    setSelectedArchiveRound(null);
    setSelectedRounds(new Set());
    setProgress(emptyProgress());
    setAnalysisError("");
    setValidationError("");
    setGlobalError(null);
    setInspectorSheetOpen(false);
    setCosmeticPhrase("");
    setSettings((current) => ({ ...current, exportCosmetics: false, includeSuspicious: false }));
  }

  function cycleTheme() {
    setTheme((current) => current === "system" ? "light" : current === "light" ? "dark" : "system");
  }

  async function requestWindowClose() {
    if (!("__TAURI_INTERNALS__" in window)) return;
    try {
      await getCurrentWindow().close();
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  const selectingView = analysis && phase === "selecting" ? (
    <div className="selection-layout">
      <RoundWorkspace
        words={words}
        analysis={analysis}
        selectedRounds={selectedRounds}
        allowSuspicious={settings.includeSuspicious}
        outputDir={outputDir}
        outputRoot={outputRoot}
        onToggleRound={toggleRound}
        onRestoreRecommended={restoreRecommended}
        onClearSelection={() => setSelectedRounds(new Set())}
        onAllowSuspiciousChange={handleAllowSuspicious}
        onChooseOutput={() => void chooseOutput()}
        onOpenSettings={(trigger) => {
          settingsTriggerRef.current = trigger;
          setInspectorSheetOpen(true);
        }}
        onConvert={() => void beginConvert()}
        formatNumber={(value) => numberFormat.format(value)}
      />
      {inspectorVisible ? (
        <ExportInspector
          words={words}
          settings={settings}
          docked={inspectorDocked}
          returnFocusRef={settingsTriggerRef}
          onChange={updateSettings}
          onRequestCosmetics={() => {
            setCosmeticPhrase("");
            setCosmeticOpen(true);
          }}
          onClose={() => setInspectorSheetOpen(false)}
          onRestoreDefaults={restoreDefaultSettings}
        />
      ) : null}
    </div>
  ) : null;

  return (
    <div className="app-shell">
      <AppChrome
        words={words}
        language={language}
        theme={theme}
        themeTitle={themeTitle}
        phase={phase}
        sourcePath={sourcePath}
        sourceFileName={sourceFileName}
        analysis={analysis}
        busy={isBusy}
        onToggleLanguage={() => setLanguage((current) => current === "zh" ? "en" : "zh")}
        onCycleTheme={cycleTheme}
        onChangeDemo={() => void chooseDemo(sourcePath)}
        onRequestClose={() => void requestWindowClose()}
      />

      <div className="app-body">
        <AppSidebar
          words={words}
          activeSection={activeSection}
          busy={isBusy}
          onLibrary={resetSession}
          onConvert={() => {
            if (phase === "analyzing" || phase === "analysisFailed" || phase === "selecting" || phase === "converting" || phase === "validationFailed" || phase === "complete") {
              setActiveSection("convert");
            } else {
              void chooseDemo();
            }
          }}
          onBatch={() => setActiveSection("batch")}
          onSettings={() => setActiveSection("settings")}
          onFaq={() => setActiveSection("faq")}
        />
        <main className="app-workspace">
        {globalError ? (
          <div className="error-strip" role="alert">
            <AlertIcon size={17} />
            <div><strong>{words.errorTitle}</strong><span>{globalError.message}</span></div>
            <button className="icon-button" type="button" onClick={() => setGlobalError(null)} aria-label={words.dismiss}><CloseIcon size={15} /></button>
          </div>
        ) : null}

        {activeSection === "faq" ? (
          <FaqWorkspace words={words} language={language} />
        ) : activeSection === "batch" ? (
          <BatchWorkspace
            words={words}
            language={language}
            folderPath={batchFolderPath}
            scanning={batchScanning}
            scanError={batchScanError}
            candidates={batchCandidates}
            selectedCandidateIds={batchSelectedIds}
            concurrency={batchConcurrency}
            runState={currentBatchRunState}
            canResume={canResumeBatch}
            jobs={batchJobs}
            eta={batchEta}
            summary={batchSummary}
            soundNotifications={localEnvironment.soundNotifications}
            onChooseFolder={() => void chooseBatchFolder()}
            onScan={() => void scanBatchFolder()}
            onSelectionChange={setBatchSelectedIds}
            onConcurrencyChange={setBatchConcurrency}
            onSoundNotificationsChange={(enabled) => {
              if (enabled) primeTaskSound(true);
              setLocalEnvironment((current) => ({ ...current, soundNotifications: enabled }));
            }}
            onStart={(candidateIds) => void startBatchImport(candidateIds)}
            onResume={() => void resumeBatchImport()}
            onStop={() => void stopBatchImport()}
            onRetryJob={(jobId) => void resumeBatchImport(jobId)}
            onOpenArchive={(job) => {
              if (job.outputPath) void runManifest(job.outputPath);
            }}
          />
        ) : activeSection === "settings" ? (
          <SettingsWorkspace
            words={words}
            language={language}
            environment={localEnvironment}
            exportRoot={libraryRoot}
            archiveRoots={libraryRoots}
            converter={settings}
            playback={playbackPreset}
            candidates={installCandidates}
            report={environmentReport}
            serverConfigDocument={serverConfigDocument}
            serverConfigDraft={serverConfigDraft}
            serverConfigValidation={serverConfigValidation}
            loadingServerConfig={loadingServerConfig}
            savingServerConfig={savingServerConfig}
            detecting={detectingInstallations}
            detectionCompleted={installDetectionCompleted}
            inspecting={inspectingEnvironment}
            onCs2PathChange={(cs2Path) => {
              setLocalEnvironment((current) => ({ ...current, cs2Path }));
              setEnvironmentReport(null);
              setServerConfigDocument(null);
              setServerConfigDraft("");
              setServerConfigValidation(null);
            }}
            onBrowseCs2={() => void chooseCs2Directory()}
            onDetectCs2={() => void detectCs2Installations()}
            onUseCandidate={useCs2Candidate}
            onInspectEnvironment={() => void runEnvironmentInspection()}
            onLoadServerConfig={() => void loadServerConfig()}
            onServerConfigDraftChange={(json) => {
              setServerConfigDraft(json);
              setServerConfigValidation(null);
            }}
            onValidateServerConfig={() => void validateServerConfigDraft()}
            onSaveServerConfig={() => void saveServerConfig()}
            onChooseExportRoot={() => void chooseLibraryRoot()}
            onAddArchiveRoot={() => void addLibraryRoot()}
            onRemoveArchiveRoot={removeLibraryRoot}
            onAddDemoRoot={() => void addDemoRoot()}
            onRemoveDemoRoot={removeDemoRoot}
            onEnvironmentChange={(patch) => setLocalEnvironment((current) => ({ ...current, ...patch }))}
            onConverterChange={updateSettings}
            onPlaybackChange={(patch) => setPlaybackPreset((current) => ({ ...current, ...patch }))}
          />
        ) : (
          <>
        {phase === "idle" ? (
          <LibraryWorkspace
            words={words}
            language={language}
            exportRoot={libraryRoot}
            roots={libraryRoots}
            scan={libraryScan}
            loading={libraryLoading}
            repairingManifest={repairingManifest}
            repairingLibrary={repairingLibrary}
            importingArchives={importingArchives}
            notice={libraryNotice}
            query={libraryQuery}
            mapFilter={libraryMap}
            sort={librarySort}
            onQueryChange={setLibraryQuery}
            onMapFilterChange={setLibraryMap}
            onSortChange={setLibrarySort}
            onAddRoot={() => void addLibraryRoot()}
            onRemoveRoot={removeLibraryRoot}
            onChooseExportRoot={() => void chooseLibraryRoot()}
            onRefresh={() => void scanLibrary(libraryRoots)}
            onImportArchives={() => void importArchives()}
            onRepairLibrary={() => void repairLibraryMetadata()}
            onConvert={() => void chooseDemo()}
            onOpenEntry={(entry: DemoLibraryEntry) => void runManifest(entry.manifestPath)}
            onRepairEntry={(entry: DemoLibraryEntry) => void repairArchiveMetadata(entry)}
          />
        ) : null}
        {phase === "openingArchive" ? <OpeningArchiveView words={words} manifestName={fileName(archivePath)} /> : null}
        {phase === "archive" && archive ? (
          <ArchiveWorkspace
            words={words}
            archive={archive}
            busy={Boolean(repairingManifest)}
            selectedRound={selectedArchiveRound ?? -1}
            commandMode={commandMode}
            playbackPreset={playbackPreset}
            copiedTarget={copiedTarget}
            onSelectRound={(round) => {
              setSelectedArchiveRound(round);
              if (archive.rounds.find((item) => item.round === round)?.sequenceLength === 0) {
                setCommandMode("round");
              }
            }}
            onCommandModeChange={setCommandMode}
            onPlaybackPresetChange={(patch) => setPlaybackPreset((current) => ({ ...current, ...patch }))}
            onCopy={(value, target) => void copyText(value, target)}
            onOpenFolder={() => void openPath(archive.root)}
            onReconvert={() => void reconvertArchive(archive)}
            onChooseManifest={() => void chooseManifest()}
            onClose={resetSession}
          />
        ) : null}
        {phase === "analyzing" ? <AnalysisProgressView words={words} sourceFileName={sourceFileName} elapsedSeconds={elapsedSeconds} progressPhase={progress.phase} /> : null}
        {phase === "analysisFailed" ? (
          <AnalysisFailedView words={words} error={analysisError} retryButtonRef={retryButtonRef} onRetry={() => void runAnalysis(sourcePath)} onChangeDemo={() => void chooseDemo(sourcePath)} />
        ) : null}
        {selectingView}
        {phase === "converting" ? <ConversionProgressView words={words} progress={progress} outputRoot={outputRoot} /> : null}
        {phase === "validationFailed" ? (
          <ValidationFailedView words={words} error={validationError} outputRoot={outputRoot} onOpenFolder={() => void openPath(outputRoot)} onBack={() => setPhase("selecting")} />
        ) : null}
        {phase === "complete" && result ? (
          <ResultView
            words={words}
            result={result}
            warnings={conversionWarnings}
            copiedTarget={copiedTarget}
            resultHeadingRef={resultHeadingRef}
            onCopy={(value, target) => void copyText(value, target)}
            onOpenFolder={() => void openPath(result.root)}
            onBrowseManifest={() => void runManifest(result.manifestPath)}
            onBack={() => setPhase("selecting")}
            onNewDemo={resetSession}
            formatNumber={(value) => numberFormat.format(value)}
            formatBytes={formatBytes}
          />
        ) : null}
          </>
        )}
        </main>
      </div>

      {dragActive ? (
        <div className="drop-overlay" role="status">
          <FolderIcon size={24} />
          <strong>{words.dropDemo}</strong>
          <span>{words.dropTypes}</span>
        </div>
      ) : null}

      {overwriteConflict ? (
        <DialogPrimitive labelledBy="overwrite-title" describedBy="overwrite-description" onDismiss={() => setOverwriteConflict(null)} initialFocusRef={chooseOtherOutputRef} dismissOnScrimClick={false}>
          <header className="dialog-header">
            <h2 id="overwrite-title">{words.overwriteTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setOverwriteConflict(null)} aria-label={words.close}><CloseIcon size={16} /></button>
          </header>
          <p id="overwrite-description" className="dialog-description">{words.overwriteBody}</p>
          <code className="dialog-path">{overwriteConflict.root}</code>
          <button className="text-button dialog-inline-action" type="button" onClick={() => void openPath(overwriteConflict.root)}><FolderIcon size={15} />{words.openExisting}</button>
          <footer className="dialog-actions three-actions">
            <button className="secondary-button" type="button" onClick={() => setOverwriteConflict(null)}>{words.cancel}</button>
            <button ref={chooseOtherOutputRef} className="secondary-button" type="button" onClick={() => {
              setOverwriteConflict(null);
              void chooseOutput();
            }}>{words.chooseAnotherOutput}</button>
            <button className="danger-button" type="button" onClick={() => void performConvert(true)}>{words.replaceAndConvert}</button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {cosmeticOpen ? (
        <DialogPrimitive labelledBy="cosmetic-title" describedBy="cosmetic-description" onDismiss={() => setCosmeticOpen(false)} initialFocusRef={cosmeticInputRef} dismissOnScrimClick={false} className="dialog-surface cosmetic-dialog">
          <header className="dialog-header warning-header">
            <span><AlertIcon size={18} /></span>
            <h2 id="cosmetic-title">{words.cosmeticTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setCosmeticOpen(false)} aria-label={words.close}><CloseIcon size={16} /></button>
          </header>
          <p id="cosmetic-description" className="dialog-description">{words.cosmeticBody}</p>
          <div className="phrase-field">
            <label htmlFor="cosmetic-confirmation-phrase">{words.typePhrase}</label>
            <button className="phrase-copy-button" type="button" onClick={() => void copyText(COSMETIC_PHRASE, "phrase")} aria-label={words.copyPhrase}>
              <code>{COSMETIC_PHRASE}</code>
              <span>{copiedTarget === "phrase" ? <CheckIcon size={14} /> : <CopyIcon size={14} />}{copiedTarget === "phrase" ? words.copied : words.copyPhrase}</span>
            </button>
            <input id="cosmetic-confirmation-phrase" ref={cosmeticInputRef} autoComplete="off" spellCheck={false} value={cosmeticPhrase} onChange={(event) => setCosmeticPhrase(event.target.value)} />
            <small>{words.phraseCaseSensitive}</small>
          </div>
          <footer className="dialog-actions">
            <button className="secondary-button" type="button" onClick={() => setCosmeticOpen(false)}>{words.cancel}</button>
            <button className="primary-button" type="button" disabled={!consentIsValid(cosmeticPhrase)} onClick={() => {
              setSettings((current) => ({ ...current, exportCosmetics: true }));
              setCosmeticOpen(false);
            }}>{words.enableCosmetics}<ArrowIcon size={15} /></button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {closeOpen ? (
        <DialogPrimitive labelledBy="close-task-title" describedBy="close-task-description" onDismiss={() => setCloseOpen(false)} initialFocusRef={keepWorkingRef} dismissOnScrimClick={false}>
          <header className="dialog-header warning-header">
            <span><AlertIcon size={18} /></span>
            <h2 id="close-task-title">{words.closeTaskTitle}</h2>
          </header>
          <p id="close-task-description" className="dialog-description">{words.closeTaskBody}</p>
          <footer className="dialog-actions">
            <button ref={keepWorkingRef} className="primary-button" type="button" onClick={() => setCloseOpen(false)}>{words.keepWorking}</button>
            <button className="danger-button" type="button" onClick={() => void getCurrentWindow().destroy()}>{words.closeAnyway}</button>
          </footer>
        </DialogPrimitive>
      ) : null}

      <span className="sr-only" role="status" aria-live="polite">{liveMessage}</span>
    </div>
  );
}

export default App;
