import { decodeCrosshairShareCode, type Crosshair } from "csgo-sharecode";
import {
  buildCrosshairRects,
  resolveCrosshairColor,
  resolveCrosshairOpacity,
  resolveCrosshairOutline,
  resolveCrosshairViewBox,
} from "../crosshairPreviewModel";

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

export function CrosshairPreview({ code, label, unavailableLabel }: {
  code: string;
  label: string;
  unavailableLabel: string;
}) {
  let crosshair: Crosshair | null = null;
  try {
    crosshair = decodeCrosshairShareCode(code);
  } catch {
    crosshair = null;
  }

  return (
    <figure className={`crosshair-preview${crosshair ? "" : " is-unavailable"}`} aria-label={crosshair ? label : unavailableLabel}>
      <div className="crosshair-preview-stage">
        {crosshair ? <CrosshairSvg crosshair={crosshair} /> : <span aria-hidden="true">×</span>}
      </div>
    </figure>
  );
}
