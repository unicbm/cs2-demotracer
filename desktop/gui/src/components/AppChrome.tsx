/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { getCurrentWindow } from "@tauri-apps/api/window";
import { useEffect, useState, type PointerEvent as ReactPointerEvent } from "react";
import {
  BatchIcon,
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
  libraryActive: boolean;
  workspaceActive: boolean;
  batchActive: boolean;
  settingsActive: boolean;
  faqActive: boolean;
  hasWorkspace: boolean;
  workspaceTitle: string;
  batchCount: number;
  onWidthChange: (width: number) => void;
  onOpenLibrary: () => void;
  onOpenWorkspace: () => void;
  onOpenBatch: () => void;
  onOpenSettings: () => void;
  onOpenFaq: () => void;
  onLanguageChange: (language: Language) => void;
  onToggleTheme: () => void;
  onConvert: () => void;
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
            <ChevronIcon size={14} />
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
  libraryActive,
  workspaceActive,
  batchActive,
  settingsActive,
  faqActive,
  hasWorkspace,
  workspaceTitle,
  batchCount,
  onWidthChange,
  onOpenLibrary,
  onOpenWorkspace,
  onOpenBatch,
  onOpenSettings,
  onOpenFaq,
  onLanguageChange,
  onToggleTheme,
  onConvert,
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
      <div className="sidebar-primary-action">
        <button type="button" disabled={busy} onClick={onConvert} title={collapsed ? words.convertDemo : undefined}>
          <PlusIcon size={17} />
          {!collapsed ? <span>{words.convertDemo}</span> : null}
        </button>
      </div>

      <nav className="sidebar-navigation" aria-label={words.mainNavigation}>
        {!collapsed ? <span className="sidebar-group-label">{words.navGroupWorkspace}</span> : null}
        <button className={itemClass(libraryActive)} type="button" disabled={busy} onClick={onOpenLibrary} aria-current={libraryActive ? "page" : undefined} title={collapsed ? words.navLibrary : undefined}>
          <LibraryIcon size={17} />
          {!collapsed ? <span>{words.navLibrary}</span> : null}
        </button>
        {hasWorkspace ? (
          <button className={itemClass(workspaceActive)} type="button" disabled={busy} onClick={onOpenWorkspace} aria-current={workspaceActive ? "page" : undefined} title={collapsed ? words.navWorkspace : workspaceTitle}>
            <ReplayIcon size={17} />
            {!collapsed ? <span><b>{words.navWorkspace}</b><small>{workspaceTitle}</small></span> : null}
            {!collapsed ? <i className="sidebar-live-dot" aria-hidden="true" /> : null}
          </button>
        ) : null}
        <button className={itemClass(batchActive)} type="button" disabled={busy && !batchActive} onClick={onOpenBatch} aria-current={batchActive ? "page" : undefined} title={collapsed ? words.navBatch : undefined}>
          <BatchIcon size={17} />
          {!collapsed ? <span>{words.navBatch}</span> : null}
          {batchCount > 0 ? <em>{Math.min(99, batchCount)}</em> : null}
        </button>

        {!collapsed ? <span className="sidebar-group-label sidebar-system-label">{words.navGroupSystem}</span> : <span className="sidebar-section-divider" />}
        <button className={itemClass(settingsActive)} type="button" disabled={busy} onClick={onOpenSettings} aria-current={settingsActive ? "page" : undefined} title={collapsed ? words.navSettings : undefined}>
          <SlidersIcon size={17} />
          {!collapsed ? <span>{words.navSettings}</span> : null}
        </button>
        <button className={itemClass(faqActive)} type="button" disabled={busy} onClick={onOpenFaq} aria-current={faqActive ? "page" : undefined} title={collapsed ? words.navFaq : undefined}>
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
              <small>{words.language}</small>
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
