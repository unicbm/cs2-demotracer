/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Language } from "./types";

interface LocalizedReleaseNotes {
  zh?: unknown;
  en?: unknown;
}

export function releaseNotesForLanguage(raw: string | null | undefined, language: Language): string {
  const value = raw?.trim() ?? "";
  if (!value) return "";

  try {
    const parsed = JSON.parse(value) as LocalizedReleaseNotes;
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return value;
    const preferred = parsed[language];
    if (typeof preferred === "string" && preferred.trim()) return preferred.trim();
    const fallback = parsed[language === "zh" ? "en" : "zh"];
    if (typeof fallback === "string" && fallback.trim()) return fallback.trim();
  } catch {
    // Plain-text release notes from older manifests remain readable.
  }

  return value;
}
