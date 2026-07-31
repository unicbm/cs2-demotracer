/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { getCurrentWindow } from "@tauri-apps/api/window";
import { useEffect, useState } from "react";
import {
  CloseIcon,
  HelpIcon,
  MaximizeIcon,
  MinimizeIcon,
  RestoreIcon,
  SlidersIcon,
  TraceMark,
} from "../icons";
import type { TextDictionary } from "../i18n";
import type { AnalysisResult } from "../types";

interface AppChromeProps {
  words: TextDictionary;
  sourcePath: string;
  sourceFileName: string;
  analysis: AnalysisResult | null;
  settingsActive: boolean;
  faqActive: boolean;
  onToggleSettings: () => void;
  onToggleFaq: () => void;
  onRequestClose: () => void;
}

export function AppChrome({
  words,
  sourcePath,
  sourceFileName,
  analysis,
  settingsActive,
  faqActive,
  onToggleSettings,
  onToggleFaq,
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
          <button
            className={`chrome-button application-nav-button${settingsActive ? " is-active" : ""}`}
            type="button"
            onClick={onToggleSettings}
            aria-label={words.navSettings}
            aria-pressed={settingsActive}
            title={words.navSettings}
          >
            <SlidersIcon size={16} />
          </button>
          <button
            className={`chrome-button application-nav-button${faqActive ? " is-active" : ""}`}
            type="button"
            onClick={onToggleFaq}
            aria-label={words.navFaq}
            aria-pressed={faqActive}
            title={words.navFaq}
          >
            <HelpIcon size={16} />
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
        </div>
      ) : null}
    </header>
  );
}
