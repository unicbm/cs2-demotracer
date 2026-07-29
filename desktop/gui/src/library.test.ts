import assert from "node:assert/strict";
import test from "node:test";
import { librarySeriesForManifest } from "./library.ts";
import type { DemoLibraryEntry } from "./types.ts";

function seriesEntry(
  manifestPath: string,
  seriesId: string | null,
  order = 1,
): DemoLibraryEntry {
  return {
    manifestPath,
    series: seriesId ? {
      id: seriesId,
      order,
      mapCount: 3,
      evidence: "hltvFilenameExactRoster",
    } : null,
  } as DemoLibraryEntry;
}

test("library series navigation resolves the active series and sorts by MAP index", () => {
  const entries = [
    seriesEntry("C:\\archive\\map-3\\manifest.json", "series-a", 3),
    seriesEntry("C:\\archive\\map-1\\manifest.json", "series-a", 1),
    seriesEntry("C:\\archive\\map-2\\manifest.json", "series-a", 2),
    seriesEntry("C:\\archive\\single\\manifest.json", null),
  ];

  assert.deepEqual(
    librarySeriesForManifest(entries, "c:/archive/map-2/manifest.json")
      .map((entry) => entry.series?.order),
    [1, 2, 3],
  );
  assert.deepEqual(
    librarySeriesForManifest(entries, "C:\\archive\\single\\manifest.json"),
    [],
  );
});
