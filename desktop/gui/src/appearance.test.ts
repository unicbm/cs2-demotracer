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
  resolveTheme,
  stepUiScale,
  toggleResolvedTheme,
} from "./appearance.ts";

describe("appearance preferences", () => {
  it("toggles the visible system theme on the first click", () => {
    assert.equal(toggleResolvedTheme("system", false), "dark");
    assert.equal(toggleResolvedTheme("system", true), "light");
    assert.equal(toggleResolvedTheme("light", true), "dark");
    assert.equal(toggleResolvedTheme("dark", false), "light");
  });

  it("resolves system theme using the current OS preference", () => {
    assert.equal(resolveTheme("system", false), "light");
    assert.equal(resolveTheme("system", true), "dark");
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
