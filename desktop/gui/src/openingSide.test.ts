/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { rosterOpeningSide } from "./openingSide.ts";

describe("roster opening side", () => {
  const team = [{ steamId: "1" }, { steamId: "2" }, { steamId: "3" }];

  it("uses the earliest round with roster evidence", () => {
    assert.equal(rosterOpeningSide(team, [
      { round: 13, tSteamIds: [], ctSteamIds: ["1", "2", "3"] },
      { round: 1, tSteamIds: ["1", "2", "3"], ctSteamIds: [] },
    ]), "t");
  });

  it("does not confuse a later halftime side swap with the opening side", () => {
    assert.equal(rosterOpeningSide(team, [
      { round: 1, tSteamIds: [], ctSteamIds: ["1", "2", "3"] },
      { round: 13, tSteamIds: ["1", "2", "3"], ctSteamIds: [] },
    ]), "ct");
  });

  it("returns no label when the archive has no usable roster evidence", () => {
    assert.equal(rosterOpeningSide(team, [{ round: 1 }]), null);
  });
});
