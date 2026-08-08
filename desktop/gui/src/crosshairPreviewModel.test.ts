/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { decodeCrosshairShareCode, type Crosshair } from "csgo-sharecode";
import { buildCrosshairRects } from "./crosshairPreviewModel.ts";

function previewCrosshair(overrides: Partial<Crosshair>): Crosshair {
  return {
    length: 2.5,
    thickness: 2,
    gap: -3,
    fixedCrosshairGap: -3,
    style: 4,
    tStyleEnabled: false,
    centerDotEnabled: false,
    ...overrides,
  } as Crosshair;
}

describe("crosshair preview raster alignment", () => {
  it("keeps donk666's small Anubis crosshair symmetric on the 48px preview grid", () => {
    const crosshair = decodeCrosshairShareCode("CSGO-GA9km-msST6-yyjrG-PYKNi-DeCcO");
    const [right, left, bottom, top] = buildCrosshairRects(crosshair, 48);

    assert.deepEqual(right, { x: 25, y: 23, width: 2, height: 2 });
    assert.deepEqual(left, { x: 21, y: 23, width: 2, height: 2 });
    assert.deepEqual(bottom, { x: 23, y: 25, width: 2, height: 2 });
    assert.deepEqual(top, { x: 23, y: 21, width: 2, height: 2 });
  });

  it("keeps even-width strokes on integer pixel boundaries", () => {
    const shapes = buildCrosshairRects(previewCrosshair({ thickness: 1.5 }), 48);

    for (const shape of shapes) {
      assert.equal(Number.isInteger(shape.x), true);
      assert.equal(Number.isInteger(shape.y), true);
      assert.equal(Number.isInteger(shape.width), true);
      assert.equal(Number.isInteger(shape.height), true);
    }
  });
});
