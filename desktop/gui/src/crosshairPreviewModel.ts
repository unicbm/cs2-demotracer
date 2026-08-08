/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Crosshair } from "csgo-sharecode";

export interface CrosshairRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface CrosshairViewBox {
  x: number;
  y: number;
  size: number;
}

const PRESET_COLORS = ["#ff0000", "#00ff00", "#ffff00", "#0000ff", "#00ffff"];

export function resolveCrosshairColor(crosshair: Crosshair): string {
  if (crosshair.color >= 0 && crosshair.color < PRESET_COLORS.length) {
    return PRESET_COLORS[crosshair.color];
  }
  return `rgb(${crosshair.red} ${crosshair.green} ${crosshair.blue})`;
}

export function resolveCrosshairOpacity(crosshair: Crosshair): number {
  return crosshair.alphaEnabled ? crosshair.alpha / 255 : 1;
}

export function resolveCrosshairOutline(crosshair: Crosshair): number {
  return crosshair.outlineEnabled ? Math.max(0, crosshair.outline) : 0;
}

export function resolveCrosshairGap(crosshair: Crosshair): number {
  return crosshair.style === 1 ? crosshair.fixedCrosshairGap : crosshair.gap;
}

export function buildCrosshairRects(crosshair: Crosshair, viewboxSize = 48): CrosshairRect[] {
  const pixelScale = viewboxSize / 64;
  const baseLength = Math.max(0, Math.floor(crosshair.length * 2));
  const logicalLength = Math.floor(crosshair.length) > 2 ? baseLength + 1 : baseLength;
  const logicalThickness = Math.max(1, Math.floor(crosshair.thickness * 2));
  const length = logicalLength > 0 ? Math.max(1, Math.round(logicalLength * pixelScale)) : 0;
  const thickness = Math.max(1, Math.round(logicalThickness * pixelScale));
  const gap = Math.round(Math.ceil(resolveCrosshairGap(crosshair) + 4) * pixelScale);
  // An even-sized SVG has no single center pixel. Align odd-width strokes to
  // a pixel center and even-width strokes to a pixel boundary so every rect
  // starts and ends on the same raster grid in all four directions.
  const center = Math.floor(viewboxSize / 2) + (thickness % 2 === 0 ? 0 : 0.5);
  const offset = thickness / 2 + gap;
  const shapes: CrosshairRect[] = [];

  if (length > 0) {
    shapes.push(
      { x: center + offset, y: center - thickness / 2, width: length, height: thickness },
      { x: center - offset - length, y: center - thickness / 2, width: length, height: thickness },
      { x: center - thickness / 2, y: center + offset, width: thickness, height: length },
    );
    if (!crosshair.tStyleEnabled) {
      shapes.push({ x: center - thickness / 2, y: center - offset - length, width: thickness, height: length });
    }
  }

  if (crosshair.centerDotEnabled) {
    shapes.push({ x: center - thickness / 2, y: center - thickness / 2, width: thickness, height: thickness });
  }
  return shapes;
}

export function resolveCrosshairViewBox(
  shapes: CrosshairRect[],
  outline: number,
  baseSize = 64,
): CrosshairViewBox {
  const center = baseSize / 2;
  if (shapes.length === 0) return { x: 0, y: 0, size: baseSize };

  const minX = Math.min(...shapes.map((shape) => shape.x - outline));
  const minY = Math.min(...shapes.map((shape) => shape.y - outline));
  const maxX = Math.max(...shapes.map((shape) => shape.x + shape.width + outline));
  const maxY = Math.max(...shapes.map((shape) => shape.y + shape.height + outline));
  const halfExtent = Math.max(center - minX, maxX - center, center - minY, maxY - center);
  const halfView = Math.max(baseSize / 2, Math.ceil(halfExtent + 2));
  return { x: center - halfView, y: center - halfView, size: halfView * 2 };
}
