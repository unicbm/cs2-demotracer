import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const desktopDirectory = resolve(scriptDirectory, "..");
const descriptorPath = join(desktopDirectory, "pro-steamid-catalog-source.json");
const catalogPath = join(desktopDirectory, "src", "data", "cs2-pro-steamid-lib.v1.jsonl");
const descriptor = JSON.parse(readFileSync(descriptorPath, "utf8"));

if (!existsSync(catalogPath)) {
  throw new Error(
    "Generated professional identity snapshot is missing. "
    + "Run node desktop/gui/scripts/import-pro-steamid-catalog.mjs <cs2-pro-steamid-lib> from the repository root.",
  );
}

const lines = readFileSync(catalogPath, "utf8")
  .split(/\r?\n/)
  .filter((line) => line.trim() !== "");
const metadata = JSON.parse(lines[0])?._meta;
if (!metadata || metadata.schemaVersion !== 1) {
  throw new Error("Generated professional identity snapshot metadata is invalid");
}
if (metadata.repository !== descriptor.repository || metadata.commit !== descriptor.commit) {
  throw new Error("Generated professional identity snapshot does not match its pinned source revision");
}
if (metadata.records !== descriptor.records || lines.length - 1 !== descriptor.records) {
  throw new Error("Generated professional identity snapshot has an unexpected record count");
}

console.log(`Professional identity snapshot matches ${descriptor.commit.slice(0, 8)} (${descriptor.records} records).`);
