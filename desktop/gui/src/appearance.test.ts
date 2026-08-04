/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  cycleUiScale,
  normalizeUiScale,
  normalizeUiSkin,
  normalizeTheme,
  resolveTheme,
  stepUiScale,
  themeBackground,
  THEME_STORAGE_KEY,
  toggleResolvedTheme,
  UI_SKINS,
  UI_SKIN_STORAGE_KEY,
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
    assert.equal(normalizeTheme("invalid"), "system");
    assert.equal(normalizeTheme(null), "system");
  });

  it("resolves system theme using the current OS preference", () => {
    assert.equal(resolveTheme("system", false), "light");
    assert.equal(resolveTheme("system", true), "dark");
  });

  it("normalizes the four persistent UI skins", () => {
    assert.deepEqual(UI_SKINS, ["trace", "cobalt", "ember", "signal"]);
    assert.notEqual(UI_SKIN_STORAGE_KEY, THEME_STORAGE_KEY);
    assert.equal(normalizeUiSkin("trace"), "trace");
    assert.equal(normalizeUiSkin("cobalt"), "cobalt");
    assert.equal(normalizeUiSkin("ember"), "ember");
    assert.equal(normalizeUiSkin("signal"), "signal");
    assert.equal(normalizeUiSkin("invalid"), "trace");
    assert.equal(normalizeUiSkin(null), "trace");
  });

  it("uses a native background matched to both skin and theme", () => {
    assert.equal(themeBackground("trace", "light"), "#e7e5e0");
    assert.equal(themeBackground("trace", "dark"), "#0b0d0c");
    assert.equal(themeBackground("cobalt", "light"), "#e9edf2");
    assert.equal(themeBackground("cobalt", "dark"), "#0b1018");
    assert.equal(themeBackground("ember", "light"), "#ece9e4");
    assert.equal(themeBackground("ember", "dark"), "#12100e");
    assert.equal(themeBackground("signal", "light"), "#e9ebe4");
    assert.equal(themeBackground("signal", "dark"), "#090b08");
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
