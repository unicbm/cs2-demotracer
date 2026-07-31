/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { readFileSync } from "node:fs";

const config = JSON.parse(readFileSync(new URL("../src-tauri/tauri.conf.json", import.meta.url), "utf8"));

if (config.plugins?.updater || config.plugins?.demotracerRelease) {
  throw new Error("tauri.conf.json must not configure remote update channels");
}
if (config.bundle?.active !== true || !config.bundle.targets?.includes("nsis")) {
  throw new Error("tauri.conf.json must build the supported NSIS installer");
}
if (config.bundle?.windows?.nsis?.installMode !== "currentUser") {
  throw new Error("tauri.conf.json NSIS install mode must remain currentUser");
}

console.log("Tauri NSIS configuration is valid and contains no remote updater.");
