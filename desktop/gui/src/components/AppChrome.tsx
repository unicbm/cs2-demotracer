/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { getCurrentWindow } from "@tauri-apps/api/window";
import { useEffect, useState, type PointerEvent as ReactPointerEvent } from "react";
import {
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

const SIDEBAR_MIN_WIDTH = 184;
const SIDEBAR_MAX_WIDTH = 320;
export const SIDEBAR_DEFAULT_WIDTH = 224;

interface AppChromeProps {
  words: TextDictionary;
  sessionTitle: string;
  sessionMeta: string;
  sidebarCollapsed: boolean;
  sidebarWidth: number;
  onToggleSidebar: () => void;
  onRequestClose: () => void;
}

interface AppSidebarProps {
  words: TextDictionary;
  language: Language;
  resolvedTheme: "light" | "dark";
  appVersion: string;
  collapsed: boolean;
  width: number;
  busy: boolean;
  importActive: boolean;
  libraryActive: boolean;
  analysisActive: boolean;
  analysisAvailable: boolean;
  settingsActive: boolean;
  faqActive: boolean;
  onWidthChange: (width: number) => void;
  onOpenImport: () => void;
  onOpenLibrary: () => void;
  onOpenAnalysis: () => void;
  onOpenSettings: () => void;
  onOpenFaq: () => void;
  onLanguageChange: (language: Language) => void;
  onToggleTheme: () => void;
}

function clampSidebarWidth(width: number): number {
  return Math.min(SIDEBAR_MAX_WIDTH, Math.max(SIDEBAR_MIN_WIDTH, Math.round(width)));
}

export function AppChrome({
  words,
  sessionTitle,
  sessionMeta,
  sidebarCollapsed,
  sidebarWidth,
  onToggleSidebar,
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
          className={`product-lockup${sidebarCollapsed ? " is-collapsed" : ""}`}
          style={{ width: sidebarCollapsed ? 64 : sidebarWidth }}
          aria-label={words.appName}
          data-tauri-drag-region="deep"
        >
          <TraceMark size={24} />
          {!sidebarCollapsed ? (
            <span className="product-lockup-copy">
              <strong>{words.appName}</strong>
            </span>
          ) : null}
          <button
            className="sidebar-toggle-button"
            type="button"
            onClick={onToggleSidebar}
            aria-label={sidebarCollapsed ? words.sidebarExpand : words.sidebarCollapse}
            title={sidebarCollapsed ? words.sidebarExpand : words.sidebarCollapse}
          >
            <SidebarIcon size={17} />
          </button>
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
  collapsed,
  width,
  busy,
  importActive,
  libraryActive,
  analysisActive,
  analysisAvailable,
  settingsActive,
  faqActive,
  onWidthChange,
  onOpenImport,
  onOpenLibrary,
  onOpenAnalysis,
  onOpenSettings,
  onOpenFaq,
  onLanguageChange,
  onToggleTheme,
}: AppSidebarProps) {
  const languageOption = LANGUAGE_OPTIONS[language];
  const nextLanguageOption = LANGUAGE_OPTIONS[languageOption.next];

  const beginResize = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (collapsed || event.button !== 0) return;
    event.preventDefault();
    const startX = event.clientX;
    const startWidth = width;
    const pointerId = event.pointerId;
    event.currentTarget.setPointerCapture(pointerId);
    document.documentElement.dataset.sidebarResizing = "true";

    const move = (moveEvent: PointerEvent) => {
      onWidthChange(clampSidebarWidth(startWidth + moveEvent.clientX - startX));
    };
    const stop = () => {
      delete document.documentElement.dataset.sidebarResizing;
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", stop);
      window.removeEventListener("pointercancel", stop);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", stop, { once: true });
    window.addEventListener("pointercancel", stop, { once: true });
  };

  const itemClass = (active: boolean) => `sidebar-nav-item${active ? " is-active" : ""}`;
  return (
    <aside
      className={`app-sidebar${collapsed ? " is-collapsed" : ""}`}
      style={{ width: collapsed ? 64 : width }}
      aria-label={words.mainNavigation}
    >
      <nav className="sidebar-navigation" aria-label={words.mainNavigation}>
        {!collapsed ? <span className="sidebar-group-label">{words.navGroupWorkspace}</span> : null}
        <button className={itemClass(importActive)} type="button" disabled={busy} onClick={onOpenImport} aria-current={importActive ? "page" : undefined} title={collapsed ? words.navImport : undefined}>
          <PlusIcon size={17} />
          {!collapsed ? <span>{words.navImport}</span> : null}
        </button>
        <button className={itemClass(libraryActive)} type="button" onClick={onOpenLibrary} aria-current={libraryActive ? "page" : undefined} title={collapsed ? words.navLibrary : undefined}>
          <LibraryIcon size={17} />
          {!collapsed ? <span>{words.navLibrary}</span> : null}
        </button>
        <button className={itemClass(analysisActive)} type="button" disabled={!analysisAvailable} onClick={onOpenAnalysis} aria-current={analysisActive ? "page" : undefined} title={!analysisAvailable ? words.navAnalysisUnavailable : collapsed ? words.navAnalysis : undefined}>
          <ReplayIcon size={17} />
          {!collapsed ? <span>{words.navAnalysis}</span> : null}
        </button>

        {!collapsed ? <span className="sidebar-group-label sidebar-system-label">{words.navGroupSystem}</span> : <span className="sidebar-section-divider" />}
        <button className={itemClass(settingsActive)} type="button" onClick={onOpenSettings} aria-current={settingsActive ? "page" : undefined} title={collapsed ? words.navSettings : undefined}>
          <SlidersIcon size={17} />
          {!collapsed ? <span>{words.navSettings}</span> : null}
        </button>
        <button className={itemClass(faqActive)} type="button" onClick={onOpenFaq} aria-current={faqActive ? "page" : undefined} title={collapsed ? words.navFaq : undefined}>
          <HelpIcon size={17} />
          {!collapsed ? <span>{words.navFaq}</span> : null}
        </button>
      </nav>

      <div className="sidebar-footer">
        {!collapsed ? <span className="sidebar-version">v{appVersion}</span> : null}
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
          {!collapsed ? (
            <span className="sidebar-language-copy">
              <strong>{languageOption.label}</strong>
            </span>
          ) : null}
          {!collapsed ? <span className="sidebar-language-target" aria-hidden="true">{nextLanguageOption.shortLabel}</span> : null}
        </button>
        <button className="sidebar-theme" type="button" onClick={onToggleTheme} title={resolvedTheme === "dark" ? words.lightTheme : words.darkTheme}>
          {resolvedTheme === "dark" ? <SunIcon size={16} /> : <MoonIcon size={16} />}
          {!collapsed ? <span>{resolvedTheme === "dark" ? words.lightTheme : words.darkTheme}</span> : null}
        </button>
      </div>

      {!collapsed ? (
        <div
          className="sidebar-resize-handle"
          role="separator"
          aria-orientation="vertical"
          aria-label={words.sidebarResize}
          aria-valuemin={SIDEBAR_MIN_WIDTH}
          aria-valuemax={SIDEBAR_MAX_WIDTH}
          aria-valuenow={width}
          tabIndex={0}
          onDoubleClick={() => onWidthChange(SIDEBAR_DEFAULT_WIDTH)}
          onPointerDown={beginResize}
          onKeyDown={(event) => {
            if (event.key === "ArrowLeft") onWidthChange(clampSidebarWidth(width - 8));
            if (event.key === "ArrowRight") onWidthChange(clampSidebarWidth(width + 8));
            if (event.key === "Home") onWidthChange(SIDEBAR_MIN_WIDTH);
            if (event.key === "End") onWidthChange(SIDEBAR_MAX_WIDTH);
          }}
        />
      ) : null}
    </aside>
  );
}
