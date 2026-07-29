import { readFileSync } from "node:fs";

const config = JSON.parse(readFileSync(new URL("../src-tauri/tauri.conf.json", import.meta.url), "utf8"));
const updater = config.plugins?.updater;

if (!updater || typeof updater !== "object" || Array.isArray(updater)) {
  throw new Error("tauri.conf.json plugins.updater must be an object; a missing or null value crashes the desktop app at startup");
}
if (typeof updater.pubkey !== "string" || updater.pubkey.length < 80) {
  throw new Error("tauri.conf.json plugins.updater.pubkey is missing or invalid");
}
if (!Array.isArray(updater.endpoints) || updater.endpoints.length === 0) {
  throw new Error("tauri.conf.json plugins.updater.endpoints must contain at least one HTTPS endpoint");
}
for (const endpoint of updater.endpoints) {
  const url = new URL(endpoint);
  if (url.protocol !== "https:") {
    throw new Error(`tauri.conf.json updater endpoint must use HTTPS: ${endpoint}`);
  }
}

console.log("Tauri updater configuration is valid.");
