import type { CSSProperties } from "react";
import ancientUrl from "../assets/maps/ancient.webp";
import anubisUrl from "../assets/maps/anubis.webp";
import cacheUrl from "../assets/maps/cache.webp";
import dust2Url from "../assets/maps/dust2.webp";
import infernoUrl from "../assets/maps/inferno.webp";
import mirageUrl from "../assets/maps/mirage.webp";
import nukeUrl from "../assets/maps/nuke.webp";
import overpassUrl from "../assets/maps/overpass.webp";
import trainUrl from "../assets/maps/train.webp";
import vertigoUrl from "../assets/maps/vertigo.webp";
import "./map-artwork.css";

interface MapVisual {
  src: string;
  accent: string;
  depth: string;
  ground: string;
  position: string;
}

const MAP_VISUALS: Record<string, MapVisual> = {
  ancient: { src: ancientUrl, accent: "#9fd45f", depth: "#203f34", ground: "#142821", position: "72% center" },
  anubis: { src: anubisUrl, accent: "#e5bd62", depth: "#604522", ground: "#2f281c", position: "72% center" },
  cache: { src: cacheUrl, accent: "#86c97a", depth: "#28483b", ground: "#182823", position: "70% center" },
  dust2: { src: dust2Url, accent: "#e7c177", depth: "#65502a", ground: "#312a1e", position: "70% center" },
  inferno: { src: infernoUrl, accent: "#ef9b63", depth: "#5d3027", ground: "#2e211e", position: "70% center" },
  mirage: { src: mirageUrl, accent: "#ffad51", depth: "#633b28", ground: "#30251e", position: "72% center" },
  nuke: { src: nukeUrl, accent: "#7db9e8", depth: "#283f58", ground: "#17242e", position: "72% center" },
  overpass: { src: overpassUrl, accent: "#6cc5c9", depth: "#244b50", ground: "#172a2d", position: "70% center" },
  train: { src: trainUrl, accent: "#83b9bd", depth: "#2b4648", ground: "#19292a", position: "72% center" },
  vertigo: { src: vertigoUrl, accent: "#e0b36d", depth: "#5a4531", ground: "#2b2621", position: "72% center" },
};

const FALLBACK_VISUAL: Omit<MapVisual, "src"> = {
  accent: "#86a4b8",
  depth: "#31424c",
  ground: "#1b252b",
  position: "center",
};

export function mapSlug(map: string): string {
  return map.trim().replace(/^(de|cs|ar)_/i, "").replace(/[^a-z0-9]/gi, "").toLowerCase();
}

export function displayMap(map: string): string {
  const value = map.trim().replace(/^(de|cs|ar)_/i, "");
  return (value || "unknown").toUpperCase();
}

export function mapArtworkStyle(map: string): CSSProperties {
  const visual = MAP_VISUALS[mapSlug(map)] ?? FALLBACK_VISUAL;
  return {
    "--map-accent": visual.accent,
    "--map-depth": visual.depth,
    "--map-ground": visual.ground,
    "--map-position": visual.position,
  } as CSSProperties;
}

export function MapArtwork({
  map,
  className = "",
  loading = "lazy",
}: {
  map: string;
  className?: string;
  loading?: "eager" | "lazy";
}) {
  const visual = MAP_VISUALS[mapSlug(map)];
  return (
    <div
      className={`map-artwork ${visual ? "has-image" : "is-fallback"} ${className}`.trim()}
      aria-hidden="true"
    >
      {visual ? (
        <img
          className="map-artwork-image"
          src={visual.src}
          alt=""
          loading={loading}
          decoding="async"
          draggable={false}
        />
      ) : null}
    </div>
  );
}
