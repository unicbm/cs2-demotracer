import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
  decodeCrosshairShareCode,
  encodeCrosshair,
  InvalidCrosshairShareCode,
} from "csgo-sharecode";
import ts from "typescript";

const modelSource = await readFile(new URL("../src/crosshairPreviewModel.ts", import.meta.url), "utf8");
const modelJavaScript = ts.transpileModule(modelSource, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
}).outputText;
const model = await import(`data:text/javascript;base64,${Buffer.from(modelJavaScript).toString("base64")}`);

const code = "CSGO-WsnnD-eHaMw-QNDf9-oxuDh-ydOUD";
const crosshair = decodeCrosshairShareCode(code);

assert.deepEqual(crosshair, {
  gap: -2.2,
  outline: 1,
  red: 50,
  green: 250,
  blue: 50,
  alpha: 200,
  splitDistance: 3,
  followRecoil: true,
  fixedCrosshairGap: 3,
  color: 1,
  outlineEnabled: true,
  innerSplitAlpha: 0,
  outerSplitAlpha: 1,
  splitSizeRatio: 1,
  thickness: 0.6,
  centerDotEnabled: false,
  deployedWeaponGapEnabled: true,
  alphaEnabled: true,
  tStyleEnabled: false,
  style: 2,
  length: 10,
});
assert.equal(encodeCrosshair(crosshair), code);
assert.throws(
  () => decodeCrosshairShareCode("CSGO-WsnnD-eHaMw-QNDf9-oxuDh-ydOUC"),
  InvalidCrosshairShareCode,
);

assert.equal(model.resolveCrosshairColor(crosshair), "#00ff00");
assert.equal(model.resolveCrosshairColor({ ...crosshair, color: 5, red: 12, green: 34, blue: 56 }), "rgb(12 34 56)");
assert.equal(model.resolveCrosshairOpacity({ ...crosshair, alphaEnabled: false, alpha: 1 }), 1);
assert.equal(model.resolveCrosshairOutline({ ...crosshair, outlineEnabled: true, outline: 0 }), 0);
assert.equal(model.resolveCrosshairGap({ ...crosshair, style: 1, gap: -4, fixedCrosshairGap: 7 }), 7);
assert.equal(model.resolveCrosshairGap({ ...crosshair, style: 2, gap: -4, fixedCrosshairGap: 7 }), -4);
assert.equal(model.buildCrosshairRects({ ...crosshair, length: 0, centerDotEnabled: false }).length, 0);
assert.equal(model.buildCrosshairRects({ ...crosshair, length: 0, centerDotEnabled: true }).length, 1);
assert.equal(model.buildCrosshairRects({ ...crosshair, length: 1, centerDotEnabled: false, tStyleEnabled: true }).length, 3);
assert.equal(model.buildCrosshairRects({ ...crosshair, length: 1, centerDotEnabled: false, tStyleEnabled: false }).length, 4);
assert.equal(model.buildCrosshairRects({ ...crosshair, length: 2, centerDotEnabled: false })[0].width, 4);
assert.equal(model.buildCrosshairRects({ ...crosshair, length: 3, centerDotEnabled: false })[0].width, 7);

const zeroLength = decodeCrosshairShareCode("CSGO-mok39-fxhPJ-6yFvM-YdVGF-EtFKO");
assert.equal(zeroLength.length, 0);
assert.equal(model.buildCrosshairRects(zeroLength).length, 0);

const dotOnly = decodeCrosshairShareCode(encodeCrosshair({ ...zeroLength, centerDotEnabled: true }));
assert.equal(dotOnly.centerDotEnabled, true);
assert.equal(model.buildCrosshairRects(dotOnly).length, 1);

const zeroOutline = decodeCrosshairShareCode("CSGO-32Gf6-EHqBw-FkJtm-mYMbP-VWDND");
assert.equal(zeroOutline.outlineEnabled, true);
assert.equal(zeroOutline.outline, 0);
assert.equal(model.resolveCrosshairOutline(zeroOutline), 0);

const regularShapes = model.buildCrosshairRects({ ...crosshair, length: 1, gap: 0 });
assert.deepEqual(model.resolveCrosshairViewBox(regularShapes, 1), { x: 0, y: 0, size: 64 });

const largeShapes = model.buildCrosshairRects({ ...crosshair, length: 15, gap: 8 });
const largeViewBox = model.resolveCrosshairViewBox(largeShapes, 1);
assert.ok(largeViewBox.size > 64);
for (const shape of largeShapes) {
  assert.ok(shape.x - 1 >= largeViewBox.x);
  assert.ok(shape.y - 1 >= largeViewBox.y);
  assert.ok(shape.x + shape.width + 1 <= largeViewBox.x + largeViewBox.size);
  assert.ok(shape.y + shape.height + 1 <= largeViewBox.y + largeViewBox.size);
}
