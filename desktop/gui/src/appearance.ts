import type { Theme } from "./types";

export type ResolvedTheme = Exclude<Theme, "system">;
export const UI_SCALE_STEPS = [0.9, 1, 1.1, 1.25] as const;
export type UiScale = (typeof UI_SCALE_STEPS)[number];

export function resolveTheme(theme: Theme, systemDark: boolean): ResolvedTheme {
  if (theme === "system") return systemDark ? "dark" : "light";
  return theme;
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
