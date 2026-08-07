/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { getCurrentWindow } from "@tauri-apps/api/window";
import { useEffect, useState } from "react";
import {
  ChevronIcon,
  CloseIcon,
  HelpIcon,
  LanguageIcon,
  LibraryIcon,
  MaximizeIcon,
  MinimizeIcon,
  MoonIcon,
  PlusIcon,
  ReplayIcon,
  RestoreIcon,
  SidebarIcon,
  SlidersIcon,
  SunIcon,
  TraceMark,
} from "../icons";
import { LANGUAGE_OPTIONS, type TextDictionary } from "../i18n";
import type { Language } from "../types";

interface AppChromeProps {
  words: TextDictionary;
  sessionTitle: string;
  sessionMeta: string;
  onRequestClose: () => void;
}

interface AppSidebarProps {
  words: TextDictionary;
  language: Language;
  resolvedTheme: "light" | "dark";
  appVersion: string;
  busy: boolean;
  importActive: boolean;
  libraryActive: boolean;
  analysisActive: boolean;
  analysisAvailable: boolean;
  settingsActive: boolean;
  faqActive: boolean;
  collapsed: boolean;
  onOpenImport: () => void;
  onOpenLibrary: () => void;
  onOpenAnalysis: () => void;
  onOpenSettings: () => void;
  onOpenFaq: () => void;
  onLanguageChange: (language: Language) => void;
  onToggleTheme: () => void;
  onToggleCollapsed: () => void;
}

export function AppChrome({
  words,
  sessionTitle,
  sessionMeta,
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
    if ("__TAURI_INTERNALS__" in window) void getCurrentWindow().minimize().catch(() => undefined);
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
        <div
          className="product-lockup"
          aria-label={words.appName}
          data-tauri-drag-region="deep"
        >
          <TraceMark size={24} />
          <span className="product-lockup-copy"><strong>{words.appName}</strong></span>
        </div>
        {sessionTitle ? (
          <div className="titlebar-context" data-tauri-drag-region="deep">
            <strong title={sessionTitle}>{sessionTitle}</strong>
            {sessionMeta ? <span title={sessionMeta}>{sessionMeta}</span> : null}
          </div>
        ) : null}
        <div className="titlebar-drag-surface" data-tauri-drag-region />
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
    </header>
  );
}

export function AppSidebar({
  words,
  language,
  resolvedTheme,
  appVersion,
  busy,
  importActive,
  libraryActive,
  analysisActive,
  analysisAvailable,
  settingsActive,
  faqActive,
  collapsed,
  onOpenImport,
  onOpenLibrary,
  onOpenAnalysis,
  onOpenSettings,
  onOpenFaq,
  onLanguageChange,
  onToggleTheme,
  onToggleCollapsed,
}: AppSidebarProps) {
  const languageOption = LANGUAGE_OPTIONS[language];
  const nextLanguageOption = LANGUAGE_OPTIONS[languageOption.next];

  const itemClass = (active: boolean) => `sidebar-nav-item${active ? " is-active" : ""}`;
  return (
    <aside className={`app-sidebar${collapsed ? " is-collapsed" : ""}`} aria-label={words.mainNavigation}>
      <div className="sidebar-collapse-row">
        <button
          className="sidebar-collapse-button"
          type="button"
          onClick={onToggleCollapsed}
          aria-label={collapsed ? words.expandSidebar : words.collapseSidebar}
          aria-controls="app-sidebar-navigation"
          aria-expanded={!collapsed}
          title={collapsed ? words.expandSidebar : words.collapseSidebar}
        >
          <SidebarIcon size={17} />
          <span>{words.collapseSidebar}</span>
          <ChevronIcon className="sidebar-collapse-chevron" size={14} />
        </button>
      </div>
      <div className="sidebar-quick-actions">
        <button
          className={`sidebar-import-action${importActive ? " is-active" : ""}`}
          type="button"
          disabled={busy}
          onClick={onOpenImport}
          aria-current={importActive ? "page" : undefined}
          title={words.navImport}
        >
          <PlusIcon size={17} />
          <span>{words.navImport}</span>
        </button>
      </div>
      <nav id="app-sidebar-navigation" className="sidebar-navigation" aria-label={words.mainNavigation}>
        <button className={itemClass(libraryActive)} type="button" onClick={onOpenLibrary} aria-current={libraryActive ? "page" : undefined} title={words.navLibrary}>
          <LibraryIcon size={17} />
          <span>{words.navLibrary}</span>
        </button>
        <button className={itemClass(analysisActive)} type="button" disabled={!analysisAvailable} onClick={onOpenAnalysis} aria-current={analysisActive ? "page" : undefined} title={!analysisAvailable ? words.navAnalysisUnavailable : words.navAnalysis}>
          <ReplayIcon size={17} />
          <span><b>{words.navAnalysis}</b>{!analysisAvailable ? <small>{words.navAnalysisUnavailable}</small> : null}</span>
        </button>

        <span className="sidebar-section-divider" />
        <button className={itemClass(settingsActive)} type="button" onClick={onOpenSettings} aria-current={settingsActive ? "page" : undefined} title={words.navSettings}>
          <SlidersIcon size={17} />
          <span>{words.navSettings}</span>
        </button>
        <button className={itemClass(faqActive)} type="button" onClick={onOpenFaq} aria-current={faqActive ? "page" : undefined} title={words.navFaq}>
          <HelpIcon size={17} />
          <span>{words.navFaq}</span>
        </button>
      </nav>

      <div className="sidebar-footer">
        <span className="sidebar-version">v{appVersion}</span>
        <button
          className="sidebar-language"
          type="button"
          onClick={() => onLanguageChange(languageOption.next)}
          aria-label={languageOption.switchLabel}
          title={languageOption.switchLabel}
        >
          <span className="sidebar-language-icon" aria-hidden="true">
            <LanguageIcon size={16} />
          </span>
          <span className="sidebar-language-copy"><strong>{languageOption.label}</strong></span>
          <span className="sidebar-language-target" aria-hidden="true">{nextLanguageOption.shortLabel}</span>
        </button>
        <button className="sidebar-theme" type="button" onClick={onToggleTheme} aria-label={resolvedTheme === "dark" ? words.lightTheme : words.darkTheme} title={resolvedTheme === "dark" ? words.lightTheme : words.darkTheme}>
          {resolvedTheme === "dark" ? <SunIcon size={16} /> : <MoonIcon size={16} />}
          <span>{resolvedTheme === "dark" ? words.lightTheme : words.darkTheme}</span>
        </button>
      </div>
    </aside>
  );
}
