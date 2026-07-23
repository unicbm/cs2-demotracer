import { getCurrentWindow } from "@tauri-apps/api/window";
import { useEffect, useState } from "react";
import {
  CloseIcon,
  MaximizeIcon,
  MinimizeIcon,
  MoonIcon,
  RestoreIcon,
  SunIcon,
  TraceMark,
} from "../icons";
import type { TextDictionary } from "../i18n";
import type { AnalysisResult, Language, Theme } from "../types";

interface AppChromeProps {
  words: TextDictionary;
  language: Language;
  theme: Theme;
  themeTitle: string;
  sourcePath: string;
  sourceFileName: string;
  analysis: AnalysisResult | null;
  busy: boolean;
  onToggleLanguage: () => void;
  onCycleTheme: () => void;
  onChangeDemo: () => void;
  onRequestClose: () => void;
}

export function AppChrome({
  words,
  language,
  theme,
  themeTitle,
  sourcePath,
  sourceFileName,
  analysis,
  busy,
  onToggleLanguage,
  onCycleTheme,
  onChangeDemo,
  onRequestClose,
}: AppChromeProps) {
  const [maximized, setMaximized] = useState(false);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    const appWindow = getCurrentWindow();
    let disposed = false;
    let unlisten: (() => void) | undefined;
    const syncMaximized = () => {
      void appWindow.isMaximized().then((value) => {
        if (!disposed) setMaximized(value);
      }).catch(() => undefined);
    };
    syncMaximized();
    void appWindow.onResized(syncMaximized).then((stop) => {
      if (disposed) stop();
      else unlisten = stop;
    });
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  const minimizeWindow = () => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    void getCurrentWindow().minimize().catch(() => undefined);
  };

  const toggleMaximizeWindow = () => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    const appWindow = getCurrentWindow();
    void appWindow.toggleMaximize()
      .then(() => appWindow.isMaximized())
      .then(setMaximized)
      .catch(() => undefined);
  };

  return (
    <header className="app-chrome">
      <div className="application-toolbar">
        <div className="product-lockup" aria-label={words.appName} data-tauri-drag-region="deep">
          <TraceMark size={24} />
          <strong>{words.appName}</strong>
          <span>{words.appSubtitle}</span>
        </div>
        <div className="titlebar-drag-surface" data-tauri-drag-region />
        <div className="application-actions">
          <button className="chrome-button language-button" type="button" onClick={onToggleLanguage} aria-label={words.language}>
            {language === "zh" ? "中" : "EN"}
          </button>
          <button className="chrome-button" type="button" onClick={onCycleTheme} aria-label={`${words.theme}: ${themeTitle}`} title={`${words.theme}: ${themeTitle}`}>
            {theme === "dark" ? <MoonIcon size={16} /> : <SunIcon size={16} />}
          </button>
        </div>
        <div className="window-controls" role="group" aria-label={words.windowControls}>
          <button className="window-control" type="button" onClick={minimizeWindow} aria-label={words.minimizeWindow} title={words.minimizeWindow}>
            <MinimizeIcon />
          </button>
          <button className="window-control" type="button" onClick={toggleMaximizeWindow} aria-label={maximized ? words.restoreWindow : words.maximizeWindow} title={maximized ? words.restoreWindow : words.maximizeWindow}>
            {maximized ? <RestoreIcon /> : <MaximizeIcon />}
          </button>
          <button className="window-control window-close-control" type="button" onClick={onRequestClose} aria-label={words.closeWindow} title={words.closeWindow}>
            <CloseIcon size={16} />
          </button>
        </div>
      </div>

      {sourcePath ? (
        <div className="session-header">
          <div className="source-identity">
            <div className="source-title-row">
              <strong>{sourceFileName}</strong>
              {analysis ? (
                <span className="source-meta">
                  {analysis.map || "—"} · {analysis.rounds.length} {words.rounds}
                </span>
              ) : null}
            </div>
          </div>

          <button className="quiet-button change-demo-button" type="button" disabled={busy} onClick={onChangeDemo}>
            {words.changeDemo}
          </button>
        </div>
      ) : null}
    </header>
  );
}
