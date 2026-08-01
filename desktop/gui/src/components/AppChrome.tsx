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
  LibraryIcon,
  MaximizeIcon,
  MinimizeIcon,
  MoonIcon,
  PlusIcon,
  RestoreIcon,
  SlidersIcon,
  SunIcon,
  TraceMark,
} from "../icons";
import type { TextDictionary } from "../i18n";
import type { Language } from "../types";

interface AppChromeProps {
  words: TextDictionary;
  sourcePath: string;
  sessionTitle: string;
  sessionMeta: string;
  language: Language;
  resolvedTheme: "light" | "dark";
  libraryActive: boolean;
  settingsActive: boolean;
  faqActive: boolean;
  busy: boolean;
  onOpenLibrary: () => void;
  onExitSession: () => void;
  onToggleSettings: () => void;
  onToggleFaq: () => void;
  onLanguageChange: (language: Language) => void;
  onToggleTheme: () => void;
  onConvert: () => void;
  onRequestClose: () => void;
}

export function AppChrome({
  words,
  sourcePath,
  sessionTitle,
  sessionMeta,
  language,
  resolvedTheme,
  libraryActive,
  settingsActive,
  faqActive,
  busy,
  onOpenLibrary,
  onExitSession,
  onToggleSettings,
  onToggleFaq,
  onLanguageChange,
  onToggleTheme,
  onConvert,
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
          <span className="product-lockup-copy">
            <strong>{words.appName}</strong>
            <small>{words.appSubtitle}</small>
          </span>
        </div>
        <nav className="application-navigation" aria-label={words.mainNavigation}>
          <button
            className={`application-nav-button${libraryActive ? " is-active" : ""}`}
            type="button"
            disabled={busy}
            onClick={onOpenLibrary}
            aria-current={libraryActive ? "page" : undefined}
          >
            <LibraryIcon size={15} />
            <span>{words.navLibrary}</span>
          </button>
          <button
            className={`application-nav-button${settingsActive ? " is-active" : ""}`}
            type="button"
            disabled={busy}
            onClick={onToggleSettings}
            aria-current={settingsActive ? "page" : undefined}
          >
            <SlidersIcon size={15} />
            <span>{words.navSettings}</span>
          </button>
        </nav>
        <div className="titlebar-drag-surface" data-tauri-drag-region />
        <div className="application-actions">
          <button
            className="chrome-import-button"
            type="button"
            onClick={onConvert}
            disabled={busy}
          >
            <PlusIcon size={14} />
            <span>{words.convertDemo}</span>
          </button>
          <button
            className={`chrome-button application-help-button${faqActive ? " is-active" : ""}`}
            type="button"
            onClick={onToggleFaq}
            disabled={busy}
            aria-label={words.navFaq}
            aria-pressed={faqActive}
            title={words.navFaq}
          >
            <HelpIcon size={16} />
          </button>
          <label className="chrome-language-control" title={words.language}>
            <span className="sr-only">{words.language}</span>
            <select
              value={language}
              aria-label={words.language}
              onChange={(event) => onLanguageChange(event.target.value as Language)}
            >
              <option value="zh">中文</option>
              <option value="en">EN</option>
            </select>
          </label>
          <button
            className="chrome-button chrome-theme-button"
            type="button"
            onClick={onToggleTheme}
            aria-label={resolvedTheme === "dark" ? words.lightTheme : words.darkTheme}
            title={resolvedTheme === "dark" ? words.lightTheme : words.darkTheme}
          >
            {resolvedTheme === "dark" ? <SunIcon size={16} /> : <MoonIcon size={16} />}
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
          <button className="session-back-button" type="button" disabled={busy} onClick={onExitSession}>
            <LibraryIcon size={14} />
            <span>{words.backToLibrary}</span>
          </button>
          <span className="session-divider" aria-hidden="true" />
          <div className="source-identity">
            <div className="source-title-row">
              <strong title={sessionTitle}>{sessionTitle}</strong>
              {sessionMeta ? <span className="source-meta" title={sessionMeta}>{sessionMeta}</span> : null}
            </div>
          </div>
        </div>
      ) : null}
    </header>
  );
}
