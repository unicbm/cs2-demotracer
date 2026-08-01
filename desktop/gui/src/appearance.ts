/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Theme, UiSkin } from "./types";

export type ResolvedTheme = Exclude<Theme, "system">;
export const UI_SKINS: readonly UiSkin[] = ["trace", "cobalt", "ember", "signal"];
export const THEME_STORAGE_KEY = "demotracer.theme";
export const UI_SKIN_STORAGE_KEY = "demotracer.ui-skin.v1";
export const THEME_BACKGROUNDS: Record<UiSkin, Record<ResolvedTheme, string>> = {
  trace: { light: "#e8ebe8", dark: "#0e1211" },
  cobalt: { light: "#e9edf2", dark: "#0b1018" },
  ember: { light: "#ece9e4", dark: "#12100e" },
  signal: { light: "#e9ebe4", dark: "#090b08" },
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
    : "system";
}

export function normalizeUiSkin(value: unknown): UiSkin {
  return UI_SKINS.includes(value as UiSkin) ? value as UiSkin : "trace";
}

export function themeBackground(skin: UiSkin, theme: ResolvedTheme): string {
  return THEME_BACKGROUNDS[skin][theme];
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
