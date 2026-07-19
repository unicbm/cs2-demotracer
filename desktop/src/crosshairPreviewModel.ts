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

export function buildCrosshairRects(crosshair: Crosshair, viewboxSize = 64): CrosshairRect[] {
  const center = viewboxSize / 2;
  const baseLength = Math.max(0, Math.floor(crosshair.length * 2));
  const length = Math.floor(crosshair.length) > 2 ? baseLength + 1 : baseLength;
  const thickness = Math.max(1, Math.floor(crosshair.thickness * 2));
  const gap = Math.ceil(resolveCrosshairGap(crosshair) + 4);
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
