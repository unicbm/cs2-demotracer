#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

function usage() {
  console.log(`Usage:
  node examples/node/convert-round.mjs --demo "<demo.dem>" --output "<output-dir>" [--rounds 0]

Options:
  --converter <path>        Path to cs2-demotracer.exe (default: cs2-demotracer.exe)
  --demo <path>             Input .dem file
  --output <dir>            Output directory
  --rounds <selector>       Round selector, for example 0 or 0,1,5-8 (default: 0)
  --side <both|t|ct>        Side to export (default: both)
  --full-round              Keep playback past the C4 plant
  --include-suspicious      Export rounds marked suspicious by the converter
`);
}

function parseArgs(argv) {
  const args = {
    converter: "cs2-demotracer.exe",
    rounds: "0",
    side: "both",
    fullRound: false,
    includeSuspicious: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") {
      usage();
      process.exit(0);
    }
    if (arg === "--full-round") {
      args.fullRound = true;
      continue;
    }
    if (arg === "--include-suspicious") {
      args.includeSuspicious = true;
      continue;
    }

    const value = argv[i + 1];
    if (!value) {
      throw new Error(`missing value for ${arg}`);
    }
    i += 1;

    switch (arg) {
      case "--converter":
        args.converter = value;
        break;
      case "--demo":
        args.demo = value;
        break;
      case "--output":
        args.output = value;
        break;
      case "--rounds":
        args.rounds = value;
        break;
      case "--side":
        if (!["both", "t", "ct"].includes(value)) {
          throw new Error("--side must be one of: both, t, ct");
        }
        args.side = value;
        break;
      default:
        throw new Error(`unknown argument: ${arg}`);
    }
  }

  if (!args.demo || !args.output) {
    usage();
    throw new Error("--demo and --output are required");
  }
  return args;
}

function collectManifests(dir, manifests = []) {
  if (!existsSync(dir)) {
    return manifests;
  }
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      collectManifests(fullPath, manifests);
    } else if (entry.isFile() && entry.name === "manifest.json") {
      manifests.push(fullPath);
    }
  }
  return manifests;
}

function newestManifest(outputDir) {
  const manifests = collectManifests(outputDir);
  if (manifests.length === 0) {
    throw new Error(`no manifest.json found under ${outputDir}`);
  }
  return manifests.sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];
}

function firstRound(rounds) {
  const token = rounds.split(",", 1)[0].split("-", 1)[0].trim();
  return Number.parseInt(token, 10);
}

function consoleQuotePath(filePath) {
  return path.resolve(filePath).replace(/"/g, '\\"');
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  mkdirSync(args.output, { recursive: true });

  const command = [
    args.converter,
    "convert",
    "--demo",
    args.demo,
    "--output",
    args.output,
    "--rounds",
    args.rounds,
  ];
  if (args.side !== "both") {
    command.push("--side", args.side);
  }
  if (args.fullRound) {
    command.push("--full-round");
  }
  if (args.includeSuspicious) {
    command.push("--include-suspicious");
  }

  console.log(`+ ${command.join(" ")}`);
  const result = spawnSync(command[0], command.slice(1), { stdio: "inherit" });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }

  const manifest = newestManifest(args.output);
  const data = JSON.parse(readFileSync(manifest, "utf8"));
  const rounds = Array.isArray(data.rounds) ? data.rounds.length : 0;
  const files = Array.isArray(data.files) ? data.files.length : 0;

  console.log(`manifest: ${path.resolve(manifest)}`);
  console.log(`rounds: ${rounds}`);
  console.log(`files: ${files}`);
  console.log("CS2 console:");
  console.log(`dtr_go round "${consoleQuotePath(manifest)}" ${firstRound(args.rounds)}; dtr_status 0`);
}

try {
  main();
} catch (error) {
  console.error(`error: ${error.message}`);
  process.exit(1);
}
