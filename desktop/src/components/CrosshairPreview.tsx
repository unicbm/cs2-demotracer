import { decodeCrosshairShareCode, type Crosshair } from "csgo-sharecode";
import {
  buildCrosshairRects,
  resolveCrosshairColor,
  resolveCrosshairGap,
  resolveCrosshairOpacity,
  resolveCrosshairOutline,
  resolveCrosshairViewBox,
} from "../crosshairPreviewModel";
import type { TextDictionary } from "../i18n";

const VIEWBOX_SIZE = 64;

function CrosshairSvg({ crosshair }: { crosshair: Crosshair }) {
  const shapes = buildCrosshairRects(crosshair, VIEWBOX_SIZE);
  const outline = resolveCrosshairOutline(crosshair);
  const opacity = resolveCrosshairOpacity(crosshair);
  const viewBox = resolveCrosshairViewBox(shapes, outline, VIEWBOX_SIZE);
  return (
    <svg
      className="crosshair-preview-svg"
      viewBox={`${viewBox.x} ${viewBox.y} ${viewBox.size} ${viewBox.size}`}
      aria-hidden="true"
      shapeRendering="crispEdges"
    >
      {outline > 0 ? shapes.map((shape, index) => (
        <rect
          key={`outline-${index}`}
          x={shape.x - outline}
          y={shape.y - outline}
          width={shape.width + outline * 2}
          height={shape.height + outline * 2}
          fill="#050607"
          fillOpacity={opacity}
        />
      )) : null}
      {shapes.map((shape, index) => (
        <rect
          key={`pip-${index}`}
          x={shape.x}
          y={shape.y}
          width={shape.width}
          height={shape.height}
          fill={resolveCrosshairColor(crosshair)}
          fillOpacity={opacity}
        />
      ))}
    </svg>
  );
}

export function CrosshairPreview({ code, words }: { code: string; words: TextDictionary }) {
  let crosshair: Crosshair | null = null;
  try {
    crosshair = decodeCrosshairShareCode(code);
  } catch {
    // Keep the recorded code available even when it fails the share-code checksum.
  }

  if (!crosshair) {
    return (
      <figure className="crosshair-preview is-unavailable" aria-label={words.crosshairPreviewUnavailable}>
        <div className="crosshair-preview-stage"><span aria-hidden="true">×</span></div>
        <figcaption>{words.crosshairPreviewUnavailable}</figcaption>
      </figure>
    );
  }

  return (
    <figure className="crosshair-preview" aria-label={words.crosshairStaticPreview}>
      <div className="crosshair-preview-stage"><CrosshairSvg crosshair={crosshair} /></div>
      <figcaption>
        <span>{words.crosshairStaticPreview}</span>
        <code>
          {words.crosshairPreviewParameters
            .replace("{size}", String(crosshair.length))
            .replace("{gap}", String(resolveCrosshairGap(crosshair)))
            .replace("{thickness}", String(crosshair.thickness))}
        </code>
      </figcaption>
    </figure>
  );
}
