/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  cycleUiScale,
  LEGACY_APPEARANCE_STORAGE_KEYS,
  normalizeUiScale,
  normalizeTheme,
  resolveTheme,
  stepUiScale,
  themeBackground,
  toggleResolvedTheme,
} from "./appearance.ts";

describe("appearance preferences", () => {
  it("toggles the visible system theme on the first click", () => {
    assert.equal(toggleResolvedTheme("system", false), "dark");
    assert.equal(toggleResolvedTheme("system", true), "light");
    assert.equal(toggleResolvedTheme("light", true), "dark");
    assert.equal(toggleResolvedTheme("dark", false), "light");
  });

  it("normalizes stored theme values", () => {
    assert.equal(normalizeTheme("light"), "light");
    assert.equal(normalizeTheme("dark"), "dark");
    assert.equal(normalizeTheme("system"), "system");
    assert.equal(normalizeTheme("invalid"), "dark");
    assert.equal(normalizeTheme(null), "dark");
  });

  it("resolves system theme using the current OS preference", () => {
    assert.equal(resolveTheme("system", false), "light");
    assert.equal(resolveTheme("system", true), "dark");
  });

  it("lists obsolete appearance preferences for cleanup", () => {
    assert.deepEqual(LEGACY_APPEARANCE_STORAGE_KEYS, [
      "demotracer.ui-skin.v1",
      "demotracer.sidebar-width.v1",
      "demotracer.sidebar-collapsed.v1",
    ]);
  });

  it("uses one native background per color mode", () => {
    assert.equal(themeBackground("light"), "#eceef0");
    assert.equal(themeBackground("dark"), "#0d0e10");
  });

  it("normalizes and steps persistent UI scale values", () => {
    assert.equal(normalizeUiScale("1.1"), 1.1);
    assert.equal(normalizeUiScale(1.22), 1.25);
    assert.equal(normalizeUiScale(null), 1);
    assert.equal(normalizeUiScale("invalid"), 1);
    assert.equal(stepUiScale(1, 1), 1.1);
    assert.equal(stepUiScale(1, -1), 0.9);
    assert.equal(stepUiScale(1.25, 1), 1.25);
    assert.equal(cycleUiScale(1.25), 0.9);
  });
});
