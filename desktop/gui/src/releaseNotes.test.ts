/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { releaseNotesForLanguage } from "./releaseNotes.ts";

describe("localized release notes", () => {
  it("selects the active UI language", () => {
    const notes = JSON.stringify({ zh: "优化了更新体验。", en: "Improved the update experience." });
    assert.equal(releaseNotesForLanguage(notes, "zh"), "优化了更新体验。");
    assert.equal(releaseNotesForLanguage(notes, "en"), "Improved the update experience.");
  });

  it("keeps legacy plain-text manifests readable", () => {
    assert.equal(releaseNotesForLanguage("Small fixes.", "zh"), "Small fixes.");
  });

  it("uses the other language when one translation is missing", () => {
    assert.equal(releaseNotesForLanguage(JSON.stringify({ en: "Small fixes." }), "zh"), "Small fixes.");
  });
});
