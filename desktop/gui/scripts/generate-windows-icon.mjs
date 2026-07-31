/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { spawnSync } from "node:child_process";
import {
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// Tauri decodes the first ICO entry as the default window icon on Windows.
// Keep the largest source first so high-DPI taskbars never upscale a tiny frame.
const WINDOWS_ICON_SIZES = [256, 128, 96, 64, 48, 40, 36, 32, 28, 24, 20, 16];
const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = dirname(scriptsDir);
const sourcePath = join(desktopDir, "app-icon-taskbar.svg");
const outputPath = join(desktopDir, "src-tauri", "icons", "icon.ico");
const tauriCliPath = join(desktopDir, "node_modules", "@tauri-apps", "cli", "tauri.js");
const tempDir = mkdtempSync(join(tmpdir(), "cs2-demotracer-icon-"));

function validatePng(png, size) {
  const signature = "89504e470d0a1a0a";
  if (png.length < 24 || png.subarray(0, 8).toString("hex") !== signature) {
    throw new Error(`Tauri did not produce a valid ${size}x${size} PNG`);
  }
  if (png.readUInt32BE(16) !== size || png.readUInt32BE(20) !== size) {
    throw new Error(`Tauri produced the wrong dimensions for ${size}x${size}`);
  }
}

function buildIco(images) {
  const directorySize = 6 + images.length * 16;
  const header = Buffer.alloc(directorySize);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);

  let imageOffset = directorySize;
  for (const [index, { size, png }] of images.entries()) {
    const entryOffset = 6 + index * 16;
    const encodedSize = size === 256 ? 0 : size;
    header.writeUInt8(encodedSize, entryOffset);
    header.writeUInt8(encodedSize, entryOffset + 1);
    header.writeUInt8(0, entryOffset + 2);
    header.writeUInt8(0, entryOffset + 3);
    header.writeUInt16LE(1, entryOffset + 4);
    header.writeUInt16LE(32, entryOffset + 6);
    header.writeUInt32LE(png.length, entryOffset + 8);
    header.writeUInt32LE(imageOffset, entryOffset + 12);
    imageOffset += png.length;
  }

  return Buffer.concat([header, ...images.map(({ png }) => png)]);
}

try {
  const result = spawnSync(
    process.execPath,
    [
      tauriCliPath,
      "icon",
      sourcePath,
      "--output",
      tempDir,
      ...WINDOWS_ICON_SIZES.flatMap((size) => ["--png", String(size)]),
    ],
    { stdio: "inherit" },
  );
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`Tauri icon generation exited with code ${result.status}`);
  }

  const images = WINDOWS_ICON_SIZES.map((size) => {
    const png = readFileSync(join(tempDir, `${size}x${size}.png`));
    validatePng(png, size);
    return { size, png };
  });
  writeFileSync(outputPath, buildIco(images));
  console.log(`Wrote ${outputPath} with ${images.length} native Windows DPI sizes.`);
} finally {
  rmSync(tempDir, { recursive: true, force: true });
}
