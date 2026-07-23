import { decodeCrosshairShareCode, type Crosshair } from "csgo-sharecode";
import { useState } from "react";
import ancientSceneUrl from "../assets/crosshair-scenes/ancient.webp";
import anubisSceneUrl from "../assets/crosshair-scenes/anubis.webp";
import cacheSceneUrl from "../assets/crosshair-scenes/cache.webp";
import dust2SceneUrl from "../assets/crosshair-scenes/dust2.webp";
import infernoSceneUrl from "../assets/crosshair-scenes/inferno.webp";
import mirageSceneUrl from "../assets/crosshair-scenes/mirage.webp";
import nukeSceneUrl from "../assets/crosshair-scenes/nuke.webp";
import {
  buildCrosshairRects,
  resolveCrosshairColor,
  resolveCrosshairOpacity,
  resolveCrosshairOutline,
  resolveCrosshairViewBox,
} from "../crosshairPreviewModel";
import { ArrowIcon } from "../icons";
import type { TextDictionary } from "../i18n";

const VIEWBOX_SIZE = 64;
const PREVIEW_SCENES = [
  { map: "Dust II", src: dust2SceneUrl },
  { map: "Mirage", src: mirageSceneUrl },
  { map: "Inferno", src: infernoSceneUrl },
  { map: "Ancient", src: ancientSceneUrl },
  { map: "Nuke", src: nukeSceneUrl },
  { map: "Cache", src: cacheSceneUrl },
  { map: "Anubis", src: anubisSceneUrl },
] as const;

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

export function CrosshairPreview({ code, label, unavailableLabel, words }: {
  code: string;
  label: string;
  unavailableLabel: string;
  words: TextDictionary;
}) {
  const [sceneIndex, setSceneIndex] = useState(0);
  let crosshair: Crosshair | null = null;
  try {
    crosshair = decodeCrosshairShareCode(code);
  } catch {
    crosshair = null;
  }
  const moveScene = (offset: number) => {
    setSceneIndex((current) => (current + offset + PREVIEW_SCENES.length) % PREVIEW_SCENES.length);
  };

  return (
    <figure className={`crosshair-preview${crosshair ? "" : " is-unavailable"}`} aria-label={crosshair ? label : unavailableLabel}>
      <div className="crosshair-preview-stage">
        <div className="crosshair-preview-scenes" aria-hidden="true">
          {PREVIEW_SCENES.map((scene, index) => (
            <img className={index === sceneIndex ? "is-active" : ""} src={scene.src} alt="" draggable={false} key={scene.map} />
          ))}
        </div>
        <span className="crosshair-preview-map">{PREVIEW_SCENES[sceneIndex].map}</span>
        {crosshair ? <CrosshairSvg crosshair={crosshair} /> : <span aria-hidden="true">×</span>}
        <button className="crosshair-scene-arrow is-previous" type="button" onClick={() => moveScene(-1)} aria-label={words.previousCrosshairScene}><ArrowIcon size={16} /></button>
        <button className="crosshair-scene-arrow is-next" type="button" onClick={() => moveScene(1)} aria-label={words.nextCrosshairScene}><ArrowIcon size={16} /></button>
        <div className="crosshair-scene-dots" role="group" aria-label={words.crosshairSceneSelector}>
          {PREVIEW_SCENES.map((scene, index) => (
            <button className={index === sceneIndex ? "is-active" : ""} type="button" onClick={() => setSceneIndex(index)} aria-label={scene.map} aria-current={index === sceneIndex ? "true" : undefined} key={scene.map} />
          ))}
        </div>
      </div>
    </figure>
  );
}
