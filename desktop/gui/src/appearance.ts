/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Theme } from "./types";

export type ResolvedTheme = Exclude<Theme, "system">;
export const THEME_STORAGE_KEY = "demotracer.theme";
export const LEGACY_APPEARANCE_STORAGE_KEYS = [
  "demotracer.ui-skin.v1",
  "demotracer.sidebar-width.v1",
  "demotracer.sidebar-collapsed.v1",
] as const;
export const THEME_BACKGROUNDS: Record<ResolvedTheme, string> = {
  light: "#eceef0",
  dark: "#0d0e10",
};
export const UI_SCALE_STEPS = [0.9, 1, 1.1, 1.25] as const;
export type UiScale = (typeof UI_SCALE_STEPS)[number];

export function resolveTheme(theme: Theme, systemDark: boolean): ResolvedTheme {
  if (theme === "system") return systemDark ? "dark" : "light";
  return theme;
}

export function normalizeTheme(value: unknown): Theme {
  return value === "light" || value === "dark" || value === "system"
    ? value
    : "dark";
}

export function themeBackground(theme: ResolvedTheme): string {
  return THEME_BACKGROUNDS[theme];
}

export function toggleResolvedTheme(theme: Theme, systemDark: boolean): ResolvedTheme {
  return resolveTheme(theme, systemDark) === "dark" ? "light" : "dark";
}

export function normalizeUiScale(value: unknown): UiScale {
  if (value === null || value === undefined || value === "") return 1;
  const numeric = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(numeric)) return 1;
  return UI_SCALE_STEPS.reduce((nearest, candidate) => (
    Math.abs(candidate - numeric) < Math.abs(nearest - numeric) ? candidate : nearest
  ), 1 as UiScale);
}

export function stepUiScale(current: number, direction: 1 | -1): UiScale {
  const normalized = normalizeUiScale(current);
  const index = UI_SCALE_STEPS.indexOf(normalized);
  const next = Math.min(UI_SCALE_STEPS.length - 1, Math.max(0, index + direction));
  return UI_SCALE_STEPS[next];
}

export function cycleUiScale(current: number): UiScale {
  const normalized = normalizeUiScale(current);
  const index = UI_SCALE_STEPS.indexOf(normalized);
  return UI_SCALE_STEPS[(index + 1) % UI_SCALE_STEPS.length];
}
