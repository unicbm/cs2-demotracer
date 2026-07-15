import { Channel, invoke } from "@tauri-apps/api/core";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AppChrome } from "./components/AppChrome";
import { ArchiveWorkspace } from "./components/ArchiveWorkspace";
import { DialogPrimitive } from "./components/Dialog";
import { ExportInspector } from "./components/ExportInspector";
import type { PlaybackPresetOptions } from "./components/PlaybackCommandBuilder";
import { RoundWorkspace } from "./components/RoundWorkspace";
import {
  AnalysisFailedView,
  AnalysisProgressView,
  type CommandMode,
  ConversionProgressView,
  type CopyTarget,
  DemoPickerView,
  OpeningArchiveView,
  ResultView,
  ValidationFailedView,
} from "./components/TaskViews";
import { AlertIcon, ArrowIcon, CheckIcon, CloseIcon, CopyIcon, FolderIcon } from "./icons";
import { COSMETIC_PHRASE, TEXT } from "./i18n";
import type {
  AnalysisResult,
  CommandErrorDto,
  ConversionProgressEvent,
  ConversionSummary,
  ConverterSettings,
  Language,
  ManifestArchive,
  OutputPreflight,
  Phase,
  ProgressPhase,
  ProgressState,
  RoundInfo,
  TaskEvent,
  TaskPhase,
  Theme,
} from "./types";

const DEFAULT_SETTINGS: ConverterSettings = {
  side: "both",
  fullRound: false,
  freezePrerollSeconds: 10,
  exportVoice: true,
  exportCosmetics: false,
  exportStickers: false,
  exportCharms: false,
  includeSuspicious: false,
};

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
  return saved === "light" || saved === "dark" || saved === "system" ? saved : "light";
}

function storedSettings(): ConverterSettings {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.settings") ?? "null") as Partial<ConverterSettings> | null;
    return saved
      ? { ...DEFAULT_SETTINGS, ...saved, exportCosmetics: false, includeSuspicious: false }
      : { ...DEFAULT_SETTINGS };
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

function fileName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path;
}

function suggestOutput(path: string): string {
  const index = Math.max(path.lastIndexOf("\\"), path.lastIndexOf("/"));
  if (index < 0) return "output";
  const separator = path.includes("\\") ? "\\" : "/";
  return `${path.slice(0, index)}${separator}output`;
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
  const [sourcePath, setSourcePath] = useState("");
  const [outputDir, setOutputDir] = useState(() => localStorage.getItem("demotracer.output") ?? "");
  const [outputRoot, setOutputRoot] = useState("");
  const [analysis, setAnalysis] = useState<AnalysisResult | null>(null);
  const [selectedRounds, setSelectedRounds] = useState<Set<number>>(new Set());
  const [settings, setSettings] = useState<ConverterSettings>(storedSettings);
  const [playbackPreset, setPlaybackPreset] = useState<PlaybackPresetOptions>(storedPlaybackPreset);
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
  const taskWarningsRef = useRef<string[]>([]);
  const isBusyRef = useRef(false);
  const chooseButtonRef = useRef<HTMLButtonElement | null>(null);
  const retryButtonRef = useRef<HTMLButtonElement | null>(null);
  const resultHeadingRef = useRef<HTMLHeadingElement | null>(null);
  const settingsTriggerRef = useRef<HTMLElement | null>(null);
  const cosmeticInputRef = useRef<HTMLInputElement | null>(null);
  const chooseOtherOutputRef = useRef<HTMLButtonElement | null>(null);
  const keepWorkingRef = useRef<HTMLButtonElement | null>(null);

  const words = TEXT[language];
  const numberFormat = useMemo(() => new Intl.NumberFormat(language === "zh" ? "zh-CN" : "en-US"), [language]);
  const isBusy = phase === "analyzing" || phase === "converting" || phase === "openingArchive";
  isBusyRef.current = phase === "analyzing" || phase === "converting";
  const inspectorDocked = useMediaQuery("(min-width: 1080px)");
  const inspectorVisible = inspectorDocked || inspectorSheetOpen;
  const elapsedSeconds = useElapsed(phase === "analyzing");
  const sourceFileName = analysis?.fileName || fileName(sourcePath);
  const themeTitle = theme === "system" ? words.systemTheme : theme === "light" ? words.lightTheme : words.darkTheme;

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.lang = language === "zh" ? "zh-CN" : "en";
    localStorage.setItem("demotracer.theme", theme);
    localStorage.setItem("demotracer.language", language);
    if ("__TAURI_INTERNALS__" in window) {
      void getCurrentWindow().setTheme(theme === "system" ? null : theme).catch(() => undefined);
    }
  }, [language, theme]);

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
    if (outputDir) localStorage.setItem("demotracer.output", outputDir);
  }, [outputDir]);

  useEffect(() => {
    if (phase === "idle") chooseButtonRef.current?.focus({ preventScroll: true });
    if (phase === "analysisFailed") retryButtonRef.current?.focus({ preventScroll: true });
    if (phase === "complete") resultHeadingRef.current?.focus({ preventScroll: true });
    if (phase === "archive") {
      window.requestAnimationFrame(() => {
        const firstRound = document.querySelector<HTMLInputElement>('.archive-round-table input[type="radio"]:not(:disabled)');
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

  const runAnalysis = useCallback(async (path: string) => {
    if (!path.toLowerCase().endsWith(".dem")) {
      setGlobalError({ code: "invalid_demo_path", message: words.invalidDemo, path });
      return;
    }

    const token = ++taskTokenRef.current;
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
    setPhase("analyzing");
    taskWarningsRef.current = [];

    const events = new Channel<TaskEvent>();
    events.onmessage = (event) => absorbEvent(event, token);
    try {
      const next = await invoke<AnalysisResult>("analyze_demo", { request: { path }, events });
      if (token !== taskTokenRef.current) return;
      setSourcePath(next.sourcePath);
      setAnalysis(next);
      setSelectedRounds(new Set(next.rounds.filter((round) => round.selectedByDefault).map((round) => round.round)));
      setOutputDir((current) => current || suggestOutput(next.sourcePath));
      setPhase("selecting");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      const error = parseCommandError(reason);
      setAnalysisError(error.message);
      setPhase("analysisFailed");
    }
  }, [absorbEvent, words.invalidDemo]);

  const runManifest = useCallback(async (path: string) => {
    if (!path.toLowerCase().endsWith(".json")) {
      setGlobalError({ code: "invalid_manifest_path", message: words.invalidManifest, path });
      return;
    }

    const returnPhase = phase;
    const token = ++taskTokenRef.current;
    setGlobalError(null);
    setArchivePath(path);
    setInspectorSheetOpen(false);
    setPhase("openingArchive");
    try {
      const next = await invoke<ManifestArchive>("read_manifest", { path });
      if (token !== taskTokenRef.current) return;
      const availableRounds = next.rounds.filter((round) => round.available);
      const firstAvailableRound = availableRounds[0];
      setArchive(next);
      setArchivePath(next.manifestPath);
      setSelectedArchiveRound(firstAvailableRound?.round ?? null);
      setCommandMode(availableRounds.length > 1 && (firstAvailableRound?.sequenceLength ?? 0) > 0 ? "sequence" : "round");
      setSourcePath("");
      setAnalysis(null);
      setResult(null);
      setOutputRoot(next.root);
      setSelectedRounds(new Set());
      setPhase("archive");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      setGlobalError(parseCommandError(reason));
      setPhase(returnPhase === "openingArchive" ? "idle" : returnPhase);
    }
  }, [phase, words.invalidManifest]);

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
      if (lowered.endsWith(".dem")) void runAnalysis(path);
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

  async function chooseDemo() {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_demo");
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

  async function chooseOutput(): Promise<string | null> {
    try {
      const path = await invoke<string | null>("choose_output_dir");
      if (path) {
        setOutputDir(path);
        setOutputRoot("");
      }
      return path;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
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
          freezePrerollSeconds: settings.freezePrerollSeconds,
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
      setPhase("complete");
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
        onChangeDemo={() => void chooseDemo()}
        onRequestClose={() => void requestWindowClose()}
      />

      <main className="app-workspace">
        {globalError ? (
          <div className="error-strip" role="alert">
            <AlertIcon size={17} />
            <div><strong>{words.errorTitle}</strong><span>{globalError.message}</span></div>
            <button className="icon-button" type="button" onClick={() => setGlobalError(null)} aria-label={words.dismiss}><CloseIcon size={15} /></button>
          </div>
        ) : null}

        {phase === "idle" ? <DemoPickerView words={words} chooseButtonRef={chooseButtonRef} onChoose={() => void chooseDemo()} onOpenManifest={() => void chooseManifest()} /> : null}
        {phase === "openingArchive" ? <OpeningArchiveView words={words} manifestName={fileName(archivePath)} /> : null}
        {phase === "archive" && archive ? (
          <ArchiveWorkspace
            words={words}
            archive={archive}
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
            onChooseManifest={() => void chooseManifest()}
            onClose={resetSession}
          />
        ) : null}
        {phase === "analyzing" ? <AnalysisProgressView words={words} sourceFileName={sourceFileName} elapsedSeconds={elapsedSeconds} /> : null}
        {phase === "analysisFailed" ? (
          <AnalysisFailedView words={words} error={analysisError} retryButtonRef={retryButtonRef} onRetry={() => void runAnalysis(sourcePath)} onChangeDemo={() => void chooseDemo()} />
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
            commandMode={commandMode}
            playbackPreset={playbackPreset}
            resultHeadingRef={resultHeadingRef}
            onCommandModeChange={setCommandMode}
            onPlaybackPresetChange={(patch) => setPlaybackPreset((current) => ({ ...current, ...patch }))}
            onCopy={(value, target) => void copyText(value, target)}
            onOpenFolder={() => void openPath(result.root)}
            onBrowseManifest={() => void runManifest(result.manifestPath)}
            onBack={() => setPhase("selecting")}
            onNewDemo={resetSession}
            formatNumber={(value) => numberFormat.format(value)}
            formatBytes={formatBytes}
          />
        ) : null}
      </main>

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
