import { Channel, invoke } from "@tauri-apps/api/core";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  type ReactNode,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  AlertIcon,
  ArrowIcon,
  CheckIcon,
  ChevronIcon,
  CloseIcon,
  CopyIcon,
  FolderIcon,
  MoonIcon,
  ReplayIcon,
  SlidersIcon,
  SunIcon,
  TraceMark,
} from "./icons";
import { COSMETIC_PHRASE, TEXT } from "./i18n";
import type {
  AnalysisResult,
  ConversionSummary,
  ConverterSettings,
  CosmeticConsent,
  Language,
  Phase,
  ProgressState,
  RoundInfo,
  TaskEvent,
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

const EMPTY_PROGRESS: ProgressState = {
  phase: "preparing",
  message: "",
  written: 0,
  estimated: 0,
  log: [],
};

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
    return saved ? { ...DEFAULT_SETTINGS, ...saved } : DEFAULT_SETTINGS;
  } catch {
    return DEFAULT_SETTINGS;
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

function formatDuration(seconds: number): string {
  const whole = Math.max(0, Math.round(seconds));
  const minutes = Math.floor(whole / 60);
  return `${minutes}:${String(whole % 60).padStart(2, "0")}`;
}

function describeError(error: unknown): string {
  if (typeof error === "string") {
    try {
      const parsed = JSON.parse(error) as { message?: string };
      return parsed.message ?? error;
    } catch {
      return error;
    }
  }
  if (error && typeof error === "object" && "message" in error) return String(error.message);
  return String(error);
}

function isOutputConflict(error: unknown): boolean {
  const value = typeof error === "string" ? error : JSON.stringify(error);
  return value.toLowerCase().includes("output_exists") || value.toLowerCase().includes("already exists");
}

function eventRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : undefined;
}

function extractProgressEvent(raw: TaskEvent): Record<string, unknown> {
  const nested = eventRecord(raw.progress) ?? eventRecord(raw.event);
  return nested ?? raw;
}

function phaseLabel(phase: string, words: (typeof TEXT)[Language]): string {
  const labels: Record<string, string> = {
    preparing: words.preparing,
    parsing: words.parsing,
    analyzing: words.analyzing,
    exporting: words.exporting,
    voice: words.voiceStage,
    validating: words.validating,
    complete: words.completeTitle,
  };
  return labels[phase] ?? words.preparing;
}

interface SwitchProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  label: string;
}

function Switch({ checked, onChange, disabled, label }: SwitchProps) {
  return (
    <button
      className="switch"
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

function useFocusBoundary(active: boolean, onDismiss?: () => void) {
  const boundary = useRef<HTMLElement | null>(null);
  const dismissRef = useRef(onDismiss);
  dismissRef.current = onDismiss;

  useEffect(() => {
    if (!active || !boundary.current) return;
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const container = boundary.current;
    const focusableSelector = "button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex='-1'])";
    const focusable = () => [...container.querySelectorAll<HTMLElement>(focusableSelector)].filter((element) => element.offsetParent !== null);
    (focusable()[0] ?? container).focus();

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && dismissRef.current) {
        event.preventDefault();
        dismissRef.current();
        return;
      }
      if (event.key !== "Tab") return;
      const candidates = focusable();
      if (candidates.length === 0) {
        event.preventDefault();
        container.focus();
        return;
      }
      const first = candidates[0];
      const last = candidates[candidates.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("keydown", handleKeyDown);
      previous?.focus();
    };
  }, [active]);

  return boundary;
}

function Modal({ children, onDismiss, labelledBy }: { children: ReactNode; onDismiss?: () => void; labelledBy: string }) {
  const boundary = useFocusBoundary(true, onDismiss);
  return (
    <div className="modal-layer" role="presentation" onMouseDown={onDismiss}>
      <section
        className="modal"
        ref={boundary}
        role="dialog"
        aria-modal="true"
        aria-labelledby={labelledBy}
        tabIndex={-1}
        onMouseDown={(event) => event.stopPropagation()}
      >
        {children}
      </section>
    </div>
  );
}

function Metric({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return (
    <div className="metric">
      <span>{label}</span>
      <strong>{value}</strong>
      {detail ? <small>{detail}</small> : null}
    </div>
  );
}

function App() {
  const [language, setLanguage] = useState<Language>(storedLanguage);
  const [theme, setTheme] = useState<Theme>(storedTheme);
  const [phase, setPhase] = useState<Phase>("idle");
  const [sourcePath, setSourcePath] = useState("");
  const [outputDir, setOutputDir] = useState(() => localStorage.getItem("demotracer.output") ?? "");
  const [analysis, setAnalysis] = useState<AnalysisResult | null>(null);
  const [selectedRounds, setSelectedRounds] = useState<Set<number>>(new Set());
  const [settings, setSettings] = useState<ConverterSettings>(storedSettings);
  const [progress, setProgress] = useState<ProgressState>(EMPTY_PROGRESS);
  const [result, setResult] = useState<ConversionSummary | null>(null);
  const [error, setError] = useState("");
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [overwriteOpen, setOverwriteOpen] = useState(false);
  const [cosmeticOpen, setCosmeticOpen] = useState(false);
  const [closeOpen, setCloseOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const [activityOpen, setActivityOpen] = useState(false);
  const [consent, setConsent] = useState<CosmeticConsent>({
    acknowledgeGsltRisk: false,
    acceptExportDisclaimer: false,
    phrase: "",
  });

  const words = TEXT[language];
  const numberFormat = useMemo(() => new Intl.NumberFormat(language === "zh" ? "zh-CN" : "en-US"), [language]);
  const stepIndex = phase === "ready" ? 1 : phase === "converting" || phase === "complete" ? 2 : 0;
  const isBusy = phase === "analyzing" || phase === "converting";

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.lang = language === "zh" ? "zh-CN" : "en";
    localStorage.setItem("demotracer.theme", theme);
    localStorage.setItem("demotracer.language", language);
  }, [language, theme]);

  useEffect(() => {
    localStorage.setItem("demotracer.settings", JSON.stringify(settings));
  }, [settings]);

  useEffect(() => {
    if (outputDir) localStorage.setItem("demotracer.output", outputDir);
  }, [outputDir]);

  const absorbEvent = useCallback((raw: TaskEvent) => {
    const kind = typeof raw.kind === "string" ? raw.kind : "";
    if (kind === "phase" && typeof raw.phase === "string") {
      setProgress((current) => ({ ...current, phase: raw.phase as string, message: String(raw.message ?? "") }));
      return;
    }
    if (kind === "log") {
      const message = String(raw.message ?? "");
      if (message) setProgress((current) => ({ ...current, log: [...current.log.slice(-9), message] }));
      return;
    }

    const event = extractProgressEvent(raw);
    const name = String(event.event ?? event.kind ?? "");
    setProgress((current) => {
      const next = { ...current };
      if (name === "analysis_finished" || name === "analysisFinished") next.estimated = Number(event.estimatedFiles ?? event.estimated_files ?? 0);
      if (name === "round_started" || name === "roundStarted") next.currentRound = Number(event.round);
      if (name === "player_written" || name === "playerWritten") next.written = current.written + 1;
      if (name === "artifacts_writing_started" || name === "artifactsWritingStarted") next.phase = "exporting";
      if (name === "finished") next.phase = "validating";
      return next;
    });
  }, []);

  const runAnalysis = useCallback(
    async (path: string) => {
      if (!path.toLowerCase().endsWith(".dem")) {
        setError(language === "zh" ? "请选择 CS2 .dem 文件。" : "Choose a CS2 .dem file.");
        return;
      }
      setError("");
      setSourcePath(path);
      setAnalysis(null);
      setResult(null);
      setPhase("analyzing");
      setProgress({ ...EMPTY_PROGRESS, phase: "parsing" });
      const events = new Channel<TaskEvent>();
      events.onmessage = absorbEvent;
      try {
        const next = await invoke<AnalysisResult>("analyze_demo", { request: { path }, events });
        setAnalysis(next);
        setSelectedRounds(new Set(next.rounds.filter((round) => round.selectedByDefault).map((round) => round.round)));
        setOutputDir((current) => current || suggestOutput(path));
        setPhase("ready");
      } catch (reason) {
        setError(describeError(reason));
        setPhase("idle");
      }
    },
    [absorbEvent, language],
  );

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let unlisten: (() => void) | undefined;
    void getCurrentWebview()
      .onDragDropEvent((event) => {
        if (event.payload.type === "drop") {
          const path = event.payload.paths.find((candidate) => candidate.toLowerCase().endsWith(".dem"));
          if (path && !isBusy) void runAnalysis(path);
        }
      })
      .then((stop) => {
        unlisten = stop;
      });
    return () => unlisten?.();
  }, [isBusy, runAnalysis]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let unlisten: (() => void) | undefined;
    void getCurrentWindow()
      .onCloseRequested((event) => {
        if (isBusy) {
          event.preventDefault();
          setCloseOpen(true);
        }
      })
      .then((stop) => {
        unlisten = stop;
      });
    return () => unlisten?.();
  }, [isBusy]);

  async function chooseDemo() {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_demo");
      if (path) await runAnalysis(path);
    } catch (reason) {
      setError(describeError(reason));
    }
  }

  async function chooseOutput(): Promise<string | null> {
    try {
      const path = await invoke<string | null>("choose_output_dir");
      if (path) setOutputDir(path);
      return path;
    } catch (reason) {
      setError(describeError(reason));
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

  function updateSettings(patch: Partial<ConverterSettings>) {
    setSettings((current) => ({ ...current, ...patch }));
  }

  const performConvert = useCallback(
    async (overwrite: boolean, approvedConsent?: CosmeticConsent, destinationOverride?: string) => {
      const destination = destinationOverride ?? outputDir;
      if (!analysis || selectedRounds.size === 0 || !destination) return;
      setError("");
      setOverwriteOpen(false);
      setCosmeticOpen(false);
      setPhase("converting");
      setActivityOpen(false);
      setProgress({ ...EMPTY_PROGRESS, phase: "preparing" });
      const events = new Channel<TaskEvent>();
      events.onmessage = absorbEvent;
      const cosmeticConsent = settings.exportCosmetics ? (approvedConsent ?? consent) : null;
      try {
        const summary = await invoke<ConversionSummary>("convert_demo", {
          request: {
            analysisId: analysis.analysisId,
            outputDir: destination,
            selectedRounds: [...selectedRounds].sort((a, b) => a - b),
            includeSuspicious: settings.includeSuspicious,
            fullRound: settings.fullRound,
            side: settings.side,
            freezePrerollSeconds: settings.freezePrerollSeconds,
            exportVoice: settings.exportVoice,
            exportCosmetics: settings.exportCosmetics,
            exportStickers: settings.exportCosmetics && settings.exportStickers,
            exportCharms: settings.exportCosmetics && settings.exportCharms,
            cosmeticConsent,
            overwrite: overwrite ? "replace" : "deny",
          },
          events,
        });
        setResult(summary);
        setProgress((current) => ({ ...current, phase: "complete" }));
        setPhase("complete");
      } catch (reason) {
        setPhase("ready");
        if (isOutputConflict(reason)) setOverwriteOpen(true);
        else setError(describeError(reason));
      }
    },
    [absorbEvent, analysis, consent, outputDir, selectedRounds, settings],
  );

  async function beginConvert() {
    if (!analysis || selectedRounds.size === 0) return;
    let destination = outputDir;
    if (!destination) destination = (await chooseOutput()) ?? "";
    if (!destination) return;
    if (settings.exportCosmetics) {
      setCosmeticOpen(true);
      return;
    }
    await performConvert(false, undefined, destination);
  }

  async function openOutput() {
    if (!result) return;
    try {
      await invoke("open_output", { request: { path: result.root } });
    } catch (reason) {
      setError(describeError(reason));
    }
  }

  async function copyCommand() {
    if (!result) return;
    const command = result.commands.cosmeticSequence || result.commands.sequence;
    try {
      await navigator.clipboard.writeText(command);
    } catch {
      const textArea = document.createElement("textarea");
      textArea.value = command;
      textArea.style.position = "fixed";
      textArea.style.opacity = "0";
      document.body.appendChild(textArea);
      textArea.select();
      document.execCommand("copy");
      textArea.remove();
    }
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1800);
  }

  function resetAll() {
    setPhase("idle");
    setSourcePath("");
    setAnalysis(null);
    setResult(null);
    setSelectedRounds(new Set());
    setError("");
    setProgress(EMPTY_PROGRESS);
  }

  const progressFraction = progress.estimated > 0 ? Math.min(progress.written / progress.estimated, 0.96) : 0.08;
  const recommendedCount = analysis?.rounds.filter((round) => round.status === "recommended").length ?? 0;
  const suspiciousCount = analysis?.rounds.filter((round) => round.status === "suspicious").length ?? 0;
  const themeTitle = theme === "system" ? words.systemTheme : theme === "light" ? words.lightTheme : words.darkTheme;
  const closeAdvanced = useCallback(() => setAdvancedOpen(false), []);
  const settingsBoundary = useFocusBoundary(advancedOpen, closeAdvanced);

  function cycleTheme() {
    setTheme((current) => (current === "system" ? "light" : current === "light" ? "dark" : "system"));
  }

  function renderIdle() {
    return (
      <div className="idle-view enter">
        <header className="pane-header">
          <h1>{words.idleTitle}</h1>
          <p>{words.idleBody}</p>
        </header>
        <div className="file-row">
          <span className="file-row-icon" aria-hidden="true"><TraceMark size={30} /></span>
          <div>
            <strong>{words.notSelected}</strong>
            <span>{words.fullParse}</span>
          </div>
          <button className="primary-button" type="button" onClick={() => void chooseDemo()}>
            <FolderIcon />
            {words.chooseDemo}
          </button>
        </div>
        <button className="drop-inline" type="button" onClick={() => void chooseDemo()}>{words.dropHint} · .dem</button>
      </div>
    );
  }

  function renderAnalyzing() {
    return (
      <div className="analyzing-view enter">
        <header className="pane-header centered">
          <h1>{words.analyzingTitle}</h1>
          <p>{words.analyzingBody}</p>
        </header>
        <div className="trace-loader" aria-label={words.analyzing}>
          <svg viewBox="0 0 680 92" preserveAspectRatio="none" aria-hidden="true">
            <path className="trace-bed" d="M4 56C86 56 80 21 164 21s78 50 164 50 80-50 166-50 78 35 182 35" />
            <path className="trace-live" d="M4 56C86 56 80 21 164 21s78 50 164 50 80-50 166-50 78 35 182 35" />
          </svg>
          <div className="loader-meta" role="status" aria-live="polite">
            <span>{fileName(sourcePath)}</span>
            <span>{phaseLabel(progress.phase, words)}</span>
          </div>
        </div>
        <div className="quiet-note centered">
          <span className="note-dot pulse" />
          <span>{words.localOnly}</span>
        </div>
      </div>
    );
  }

  function renderReady() {
    if (!analysis) return null;
    return (
      <div className="ready-view enter">
        <header className="workspace-header">
          <div>
            <h1>{analysis.fileName}</h1>
            <p>{words.readyTitle}</p>
          </div>
          <button className="secondary-button" type="button" onClick={() => setAdvancedOpen(true)}>
            <SlidersIcon />
            {words.advanced}
          </button>
        </header>

        <section className="demo-facts" aria-label={words.demoSummary}>
          <Metric label={words.map} value={analysis.map || "—"} />
          <Metric label={words.tickRate} value={analysis.tickRate.toFixed(2)} />
          <Metric label={words.rounds} value={numberFormat.format(analysis.rounds.length)} detail={`${recommendedCount} ${words.recommended.toLowerCase()}`} />
          <Metric label={words.rows} value={numberFormat.format(analysis.rowCount)} />
        </section>

        <section className="round-panel">
          <div className="round-toolbar">
            <div className="round-legend">
              <span><i className="legend-dot stable" />{recommendedCount} {words.recommended}</span>
              {suspiciousCount > 0 ? <span><i className="legend-dot review" />{suspiciousCount} {words.suspicious}</span> : null}
            </div>
            <div className="toolbar-actions">
              <button type="button" onClick={restoreRecommended}>{words.allRecommended}</button>
              <button type="button" onClick={() => setSelectedRounds(new Set())}>{words.clear}</button>
            </div>
          </div>

          {suspiciousCount > 0 ? (
            <label className="suspicious-control">
              <Switch
                checked={settings.includeSuspicious}
                onChange={(checked) => {
                  updateSettings({ includeSuspicious: checked });
                  if (!checked && analysis) {
                    const blocked = new Set(analysis.rounds.filter((round) => round.status === "suspicious").map((round) => round.round));
                    setSelectedRounds((current) => new Set([...current].filter((round) => !blocked.has(round))));
                  }
                }}
                label={words.includeSuspicious}
              />
              <span>{words.includeSuspicious}</span>
            </label>
          ) : null}

          <div className="round-table" aria-label={words.rounds}>
            <div className="round-table-head" aria-hidden="true">
              <span />
              <span>{words.round}</span>
              <span>{words.quality}</span>
              <span>{words.duration}</span>
              <span>{words.players}</span>
              <span>{words.data}</span>
            </div>
            <div className="round-table-body">
              {analysis.rounds.map((round) => {
                const disabled = round.status === "suspicious" && !settings.includeSuspicious;
                const checked = selectedRounds.has(round.round);
                return (
                  <button
                    className={`round-row ${checked ? "selected" : ""} ${disabled ? "disabled" : ""}`}
                    type="button"
                    key={round.round}
                    onClick={() => toggleRound(round)}
                    aria-pressed={checked}
                    disabled={disabled}
                  >
                    <span className="round-check" aria-hidden="true">{checked ? <CheckIcon size={15} /> : null}</span>
                    <span className="round-number"><b>{String(round.round).padStart(2, "0")}</b><small>#{round.startTick}</small></span>
                    <span className="quality-cell">
                      <i className={`quality-pill ${round.status}`}>
                        {round.status === "recommended" ? words.recommendedLabel : words.suspiciousLabel}
                      </i>
                      <small title={round.problems.join(" · ")}>{round.problems[0] || words.noProblems}</small>
                    </span>
                    <span>{formatDuration(round.durationSeconds)}</span>
                    <span className="team-count"><i>T {round.tPlayers}</i><i>CT {round.ctPlayers}</i></span>
                    <span>{numberFormat.format(round.validRows)}</span>
                  </button>
                );
              })}
            </div>
          </div>
        </section>

        <div className="conversion-dock">
          <div>
            <strong>{words.selected.replace("{count}", String(selectedRounds.size))}</strong>
            <span>{outputDir ? fileName(outputDir) : words.notSelected}</span>
          </div>
          <button className="primary-button" type="button" disabled={selectedRounds.size === 0} onClick={() => void beginConvert()}>
            <span>{outputDir ? words.convert : words.chooseOutput}</span>
            <ArrowIcon />
          </button>
        </div>
      </div>
    );
  }

  function renderConverting() {
    return (
      <div className="converting-view enter">
        <header className="pane-header centered">
          <h1>{words.convertingTitle}</h1>
          <p>{words.convertingBody}</p>
        </header>

        <div className="progress-stage">
          <div className="progress-trace" aria-hidden="true">
            <svg viewBox="0 0 720 84" preserveAspectRatio="none">
              <path d="M4 50C88 50 96 17 180 17s92 50 180 50 96-50 180-50 92 33 176 33" />
            </svg>
            <span className="progress-fill" style={{ width: `${Math.max(progressFraction * 100, 7)}%` }} />
            {[0, 0.25, 0.5, 0.75, 1].map((point) => (
              <i key={point} className={progressFraction >= point ? "passed" : ""} style={{ left: `${point * 100}%` }} />
            ))}
          </div>
          <div className="progress-meta" role="status" aria-live="polite">
            <strong>{phaseLabel(progress.phase, words)}</strong>
            <span>
              {progress.estimated > 0
                ? words.filesWritten.replace("{written}", String(progress.written)).replace("{total}", String(progress.estimated))
                : progress.currentRound
                  ? words.roundWorking.replace("{round}", String(progress.currentRound))
                  : fileName(sourcePath)}
            </span>
          </div>
        </div>

        {progress.log.length > 0 ? (
          <div className={`activity ${activityOpen ? "open" : ""}`}>
            <button type="button" onClick={() => setActivityOpen((current) => !current)}>
              <span>{words.activity}</span>
              <ChevronIcon />
            </button>
            {activityOpen ? (
              <div className="activity-log">
                {progress.log.map((line, index) => <span key={`${line}-${index}`}>{line}</span>)}
              </div>
            ) : null}
          </div>
        ) : null}
      </div>
    );
  }

  function renderComplete() {
    if (!result) return null;
    const command = result.commands.cosmeticSequence || result.commands.sequence;
    return (
      <div className="complete-view enter">
        <div className="success-mark"><CheckIcon size={32} /></div>
        <h1>{words.completeTitle}</h1>
        <p>{words.completeBody}</p>
        <div className="complete-actions">
          <button className="primary-button" type="button" onClick={() => void openOutput()}>
            <FolderIcon />
            {words.openFolder}
          </button>
          <button className="secondary-button" type="button" onClick={() => void copyCommand()}>
            {copied ? <CheckIcon /> : <CopyIcon />}
            {copied ? words.copied : words.copySequence}
          </button>
        </div>

        <section className="result-summary">
          <Metric label={words.exportedRounds} value={numberFormat.format(result.rounds.length)} />
          <Metric label={words.validated} value={numberFormat.format(result.validatedFiles)} />
          <Metric label={words.outputSize} value={formatBytes(result.outputBytes)} />
          <Metric label={words.voiceFiles} value={numberFormat.format(result.voice.sidecars)} />
        </section>

        <section className="result-details">
          <details open>
            <summary>{words.commands}<ChevronIcon /></summary>
            <button className="command-line" type="button" onClick={() => void copyCommand()} title={command}>
              <code>{command}</code><CopyIcon size={16} />
            </button>
          </details>
          <details>
            <summary>{words.playerFiles} · {result.players.length}<ChevronIcon /></summary>
            <div className="player-grid">
              {result.players.map((player) => (
                <div key={`${player.steamId}-${player.team}`}>
                  <span>{typeof player.team === "number" ? `TEAM ${player.team}` : player.team}</span>
                  <strong>{player.name}</strong>
                  <small>{player.files} files · {player.rounds} rounds</small>
                </div>
              ))}
            </div>
          </details>
          <details>
            <summary>{words.manifest}<ChevronIcon /></summary>
            <div className="path-line">{result.manifestPath}</div>
          </details>
        </section>

        <div className="complete-footer">
          <button type="button" onClick={() => setPhase("ready")}><ReplayIcon />{words.exportAgain}</button>
          <button type="button" onClick={resetAll}>{words.newDemo}<ArrowIcon /></button>
        </div>
      </div>
    );
  }

  return (
    <div className="app-shell">
      <aside className="rail">
        <div className="brand">
          <span className="brand-mark"><TraceMark /></span>
          <span><strong>DemoTracer</strong><small>{words.appSubtitle}</small></span>
        </div>

        <nav className="workflow" aria-label={words.workflowLabel}>
          {words.steps.map((step, index) => (
            <div className={`workflow-step ${index === stepIndex ? "active" : ""} ${index < stepIndex ? "done" : ""}`} key={step}>
              <span>{index < stepIndex ? <CheckIcon size={14} /> : index + 1}</span>
              <div><strong>{step}</strong><i /></div>
            </div>
          ))}
        </nav>

        <div className="rail-context">
          <section>
            <header><span>{words.source}</span>{sourcePath && !isBusy ? <button type="button" onClick={() => void chooseDemo()}>{words.change}</button> : null}</header>
            <div className={sourcePath ? "has-value" : ""}>
              <i>{words.sourceBadge}</i>
              <strong title={sourcePath}>{sourcePath ? fileName(sourcePath) : words.notSelected}</strong>
            </div>
          </section>
          <section>
            <header><span>{words.output}</span>{!isBusy ? <button type="button" onClick={() => void chooseOutput()}>{words.change}</button> : null}</header>
            <div className={outputDir ? "has-value" : ""}>
              <FolderIcon size={17} />
              <strong title={outputDir}>{outputDir ? fileName(outputDir) : words.notSelected}</strong>
            </div>
          </section>
        </div>

        <div className="rail-footer">
          <button type="button" onClick={cycleTheme} title={`${words.theme}: ${themeTitle}`}>
            {theme === "dark" ? <MoonIcon /> : <SunIcon />}
            <span>{themeTitle}</span>
          </button>
          <button type="button" onClick={() => setLanguage((current) => (current === "zh" ? "en" : "zh"))} title={words.language}>
            <span className="language-glyph">{language === "zh" ? "中" : "EN"}</span>
            <span>{language === "zh" ? "中文" : "English"}</span>
          </button>
        </div>
      </aside>

      <main className="workspace">
        {error ? (
          <div className="error-banner" role="alert">
            <AlertIcon />
            <span><strong>{words.errorTitle}</strong>{error}</span>
            <button type="button" aria-label={words.close} onClick={() => setError("")}><CloseIcon /></button>
          </div>
        ) : null}
        {phase === "idle" ? renderIdle() : null}
        {phase === "analyzing" ? renderAnalyzing() : null}
        {phase === "ready" ? renderReady() : null}
        {phase === "converting" ? renderConverting() : null}
        {phase === "complete" ? renderComplete() : null}
      </main>

      {advancedOpen ? (
        <div className="sheet-layer" onMouseDown={closeAdvanced}>
          <aside className="settings-sheet" ref={settingsBoundary} role="dialog" aria-modal="true" aria-labelledby="settings-title" tabIndex={-1} onMouseDown={(event) => event.stopPropagation()}>
            <header>
              <div><span className="eyebrow">DEMOTRACER</span><h2 id="settings-title">{words.settingsTitle}</h2><p>{words.settingsBody}</p></div>
              <button type="button" aria-label={words.close} onClick={closeAdvanced}><CloseIcon /></button>
            </header>
            <div className="settings-group">
              <label>{words.side}</label>
              <div className="segmented">
                {(["both", "t", "ct"] as const).map((side) => (
                  <button className={settings.side === side ? "active" : ""} type="button" key={side} onClick={() => updateSettings({ side })}>
                    {side === "both" ? words.both : side === "t" ? words.t : words.ct}
                  </button>
                ))}
              </div>
            </div>
            <SettingRow title={words.fullRoundLabel} help={words.fullRoundHelp}>
              <Switch checked={settings.fullRound} onChange={(fullRound) => updateSettings({ fullRound })} label={words.fullRoundLabel} />
            </SettingRow>
            <div className="settings-group preroll-setting">
              <label htmlFor="preroll">{words.preroll}<span>{settings.freezePrerollSeconds}s</span></label>
              <input id="preroll" type="range" min="0" max="120" step="1" value={settings.freezePrerollSeconds} onChange={(event) => updateSettings({ freezePrerollSeconds: Number(event.target.value) })} />
            </div>
            <SettingRow title={words.voice} help={words.voiceHelp}>
              <Switch checked={settings.exportVoice} onChange={(exportVoice) => updateSettings({ exportVoice })} label={words.voice} />
            </SettingRow>
            <div className={`risk-setting ${settings.exportCosmetics ? "enabled" : ""}`}>
              <SettingRow title={words.cosmetics} help={words.cosmeticsHelp} risk>
                <Switch
                  checked={settings.exportCosmetics}
                  onChange={(exportCosmetics) => {
                    updateSettings({ exportCosmetics, exportStickers: exportCosmetics && settings.exportStickers, exportCharms: exportCosmetics && settings.exportCharms });
                    if (!exportCosmetics) setConsent({ acknowledgeGsltRisk: false, acceptExportDisclaimer: false, phrase: "" });
                  }}
                  label={words.cosmetics}
                />
              </SettingRow>
              {settings.exportCosmetics ? (
                <div className="sub-options">
                  <label><input type="checkbox" checked={settings.exportStickers} onChange={(event) => updateSettings({ exportStickers: event.target.checked })} />{words.stickers}</label>
                  <label><input type="checkbox" checked={settings.exportCharms} onChange={(event) => updateSettings({ exportCharms: event.target.checked })} />{words.charms}</label>
                </div>
              ) : null}
            </div>
            <button className="primary-button sheet-done" type="button" onClick={closeAdvanced}>{words.done}</button>
          </aside>
        </div>
      ) : null}

      {overwriteOpen ? (
        <Modal labelledBy="overwrite-title" onDismiss={() => setOverwriteOpen(false)}>
          <span className="modal-icon"><ReplayIcon /></span>
          <h2 id="overwrite-title">{words.overwriteTitle}</h2>
          <p>{words.overwriteBody}</p>
          <div className="modal-actions">
            <button className="secondary-button" type="button" onClick={() => setOverwriteOpen(false)}>{words.cancel}</button>
            <button className="danger-button" type="button" onClick={() => void performConvert(true)}>{words.replace}</button>
          </div>
        </Modal>
      ) : null}

      {cosmeticOpen ? (
        <Modal labelledBy="cosmetic-title" onDismiss={() => setCosmeticOpen(false)}>
          <span className="modal-icon warning"><AlertIcon /></span>
          <h2 id="cosmetic-title">{words.cosmeticTitle}</h2>
          <p>{words.cosmeticBody}</p>
          <div className="consent-list">
            <label><input type="checkbox" checked={consent.acknowledgeGsltRisk} onChange={(event) => setConsent((current) => ({ ...current, acknowledgeGsltRisk: event.target.checked }))} /><span>{words.acknowledgeRisk}</span></label>
            <label><input type="checkbox" checked={consent.acceptExportDisclaimer} onChange={(event) => setConsent((current) => ({ ...current, acceptExportDisclaimer: event.target.checked }))} /><span>{words.acceptDisclaimer}</span></label>
          </div>
          <label className="phrase-field"><span>{words.typePhrase}</span><code>{COSMETIC_PHRASE}</code><input autoComplete="off" spellCheck={false} value={consent.phrase} onChange={(event) => setConsent((current) => ({ ...current, phrase: event.target.value }))} /></label>
          <div className="modal-actions">
            <button className="secondary-button" type="button" onClick={() => setCosmeticOpen(false)}>{words.cancel}</button>
            <button
              className="primary-button"
              type="button"
              disabled={!consent.acknowledgeGsltRisk || !consent.acceptExportDisclaimer || consent.phrase.trim() !== COSMETIC_PHRASE}
              onClick={() => void performConvert(false, consent)}
            >
              {words.confirmCosmetics}<ArrowIcon />
            </button>
          </div>
        </Modal>
      ) : null}

      {closeOpen ? (
        <Modal labelledBy="close-title" onDismiss={() => setCloseOpen(false)}>
          <span className="modal-icon warning"><AlertIcon /></span>
          <h2 id="close-title">{words.closeTitle}</h2>
          <p>{words.closeBody}</p>
          <div className="modal-actions">
            <button className="secondary-button" type="button" onClick={() => setCloseOpen(false)}>{words.keepOpen}</button>
            <button className="danger-button" type="button" onClick={() => void getCurrentWindow().destroy()}>{words.closeAnyway}</button>
          </div>
        </Modal>
      ) : null}
    </div>
  );
}

function SettingRow({
  title,
  help,
  children,
  risk,
}: {
  title: string;
  help: string;
  children: ReactNode;
  risk?: boolean;
}) {
  return (
    <div className="setting-row">
      <div><strong>{title}{risk ? <AlertIcon size={15} /> : null}</strong><p>{help}</p></div>
      {children}
    </div>
  );
}

export default App;
