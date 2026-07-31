/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const countries = require("i18n-iso-countries");
countries.registerLocale(require("i18n-iso-countries/langs/en.json"));

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const desktopDirectory = resolve(scriptDirectory, "..");
const outputPath = join(desktopDirectory, "src", "data", "cs2-pro-steamid-lib.v1.jsonl");
const descriptorPath = join(desktopDirectory, "pro-steamid-catalog-source.json");
const sourceDirectory = process.argv[2] ? resolve(process.argv[2]) : null;

if (!sourceDirectory) {
  throw new Error("Usage: node desktop/gui/scripts/import-pro-steamid-catalog.mjs <cs2-pro-steamid-lib>");
}
const sourceDescriptor = readJson(descriptorPath);

function readJson(path) {
  return JSON.parse(readFileSync(path, "utf8"));
}

function requiredString(value, context) {
  if (typeof value !== "string" || value.trim() === "") {
    throw new Error(`${context} must be a non-empty string`);
  }
  return value.trim();
}

function optionalString(value) {
  return typeof value === "string" && value.trim() !== "" ? value.trim() : undefined;
}

function isoDate(value, context) {
  const normalized = requiredString(value, context);
  const match = normalized.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!match) throw new Error(`${context} must be an ISO date`);
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const parsed = new Date(Date.UTC(year, month - 1, day));
  if (
    parsed.getUTCFullYear() !== year
    || parsed.getUTCMonth() !== month - 1
    || parsed.getUTCDate() !== day
  ) {
    throw new Error(`${context} must be a valid calendar date`);
  }
  return normalized;
}

function countryCode(country, explicitCode) {
  const normalizedExplicitCode = optionalString(explicitCode)?.toUpperCase();
  if (normalizedExplicitCode) return normalizedExplicitCode;
  const name = optionalString(country);
  if (!name || name === "Non-representing" || name.toLowerCase() === "xx") return undefined;
  const aliases = {
    Moldova: "MD",
    Syria: "SY",
  };
  return aliases[name] ?? countries.getAlpha2Code(name, "en") ?? undefined;
}

function sourceCommit() {
  const status = execFileSync("git", ["-C", sourceDirectory, "status", "--porcelain"], {
    encoding: "utf8",
  }).trim();
  if (status) {
    throw new Error("CS2 Pro SteamID Lib source worktree must be clean before snapshot import");
  }
  return execFileSync("git", ["-C", sourceDirectory, "rev-parse", "HEAD"], {
    encoding: "utf8",
  }).trim();
}

function sourceRepository() {
  return execFileSync("git", ["-C", sourceDirectory, "config", "--get", "remote.origin.url"], {
    encoding: "utf8",
  }).trim().replace(/\.git$/, "");
}

function revisionMetadata(source) {
  const sourceId = requiredString(source.source_id, "source.source_id");
  const url = requiredString(source.url, `${sourceId}.url`);
  const revisionId = sourceId.match(/^liquipedia-revision-(\d+)$/)?.[1]
    ?? url.match(/[?&]oldid=(\d+)/)?.[1];
  const revisionTimestamp = optionalString(source.notes)?.match(/Revision timestamp:\s*([^\s]+)$/)?.[1];
  return {
    sourceId,
    url,
    retrievedAt: isoDate(source.retrieved_at, `${sourceId}.retrieved_at`),
    identityEvidence: requiredString(source.identity_evidence, `${sourceId}.identity_evidence`),
    ...(revisionId ? { revisionId } : {}),
    ...(revisionTimestamp ? { revisionTimestamp } : {}),
  };
}

function projectedRoles(record, context) {
  if (record.roles === undefined) return [];
  if (!Array.isArray(record.roles)) {
    throw new Error(`${context}.roles must be an array`);
  }
  const roles = record.roles.flatMap((role, roleIndex) => {
    if (!role || typeof role !== "object" || Array.isArray(role)) {
      throw new Error(`${context}.roles[${roleIndex}] must be an object`);
    }
    return requiredString(role.name, `${context}.roles[${roleIndex}].name`)
      .split(",")
      .map((name) => name.trim())
      .filter(Boolean);
  });
  return [...new Set(roles)];
}

function projectRecord(record, index) {
  const context = `record ${index + 1}`;
  if (record.schema_version !== 1) throw new Error(`${context} has an unsupported schema_version`);
  const steamId = requiredString(record.steamid64, `${context}.steamid64`);
  if (!/^[1-9]\d{16}$/.test(steamId)) throw new Error(`${context} has an invalid SteamID64`);
  const playerId = requiredString(record.player_id, `${context}.player_id`);
  if (!/^[a-z0-9][a-z0-9-]*$/.test(playerId)) throw new Error(`${context} has an invalid player_id`);
  if (!Array.isArray(record.mapping_source_ids) || record.mapping_source_ids.length === 0) {
    throw new Error(`${context} has no mapping sources`);
  }
  if (!Array.isArray(record.sources)) throw new Error(`${context}.sources must be an array`);
  const sourcesById = new Map(record.sources.map((source) => [source.source_id, source]));
  const mappingSources = record.mapping_source_ids.map((sourceId) => {
    const source = sourcesById.get(sourceId);
    if (!source) throw new Error(`${context} references missing mapping source ${sourceId}`);
    if (source.identity_evidence !== "direct" && source.identity_evidence !== "curated") {
      throw new Error(`${context} mapping source ${sourceId} is not direct or curated evidence`);
    }
    return revisionMetadata(source);
  });
  const identity = record.identity && typeof record.identity === "object" ? record.identity : {};
  const projectedCountryCode = countryCode(identity.country, identity.country_code);
  const externalIds = record.external_ids && typeof record.external_ids === "object"
    ? Object.fromEntries(Object.entries(record.external_ids)
      .filter(([, value]) => typeof value === "string" && value.trim() !== "")
      .map(([key, value]) => [key, value.trim()]))
    : {};
  if (externalIds.esea && /[|={}]/.test(externalIds.esea)) {
    throw new Error(`${context}.external_ids.esea contains template-field syntax`);
  }
  const birthDate = optionalString(identity.birth_date);
  const roles = projectedRoles(record, context);
  return {
    steamId,
    playerId,
    handle: requiredString(identity.handle, `${context}.identity.handle`),
    aliases: Array.isArray(identity.aliases)
      ? [...new Set(identity.aliases.map((alias) => requiredString(alias, `${context}.identity.aliases`)))]
      : [],
    mappingVerifiedAt: isoDate(record.mapping_verified_at, `${context}.mapping_verified_at`),
    ...(optionalString(identity.name_native) ? { nameNative: identity.name_native.trim() } : {}),
    ...(optionalString(identity.name_latin) ? { nameLatin: identity.name_latin.trim() } : {}),
    ...(optionalString(identity.country) ? { country: identity.country.trim() } : {}),
    ...(projectedCountryCode ? { countryCode: projectedCountryCode } : {}),
    ...(birthDate ? { birthDate: isoDate(birthDate, `${context}.identity.birth_date`) } : {}),
    ...(optionalString(identity.status) ? { status: identity.status.trim() } : {}),
    ...(roles.length > 0 ? { roles } : {}),
    externalIds,
    mappingSources,
  };
}

const commit = sourceCommit();
const repository = sourceRepository();
if (commit !== sourceDescriptor.commit || repository !== sourceDescriptor.repository) {
  throw new Error(
    `CS2 Pro SteamID Lib must match pinned source ${sourceDescriptor.repository}@${sourceDescriptor.commit}`,
  );
}
const playerDirectory = join(sourceDirectory, "src", "cs2_pro_steamid_lib", "data", "players");
const generatedRecords = readFileSync(join(playerDirectory, "liquipedia.jsonl"), "utf8")
  .split(/\r?\n/)
  .filter((line) => line.trim() !== "")
  .map((line) => JSON.parse(line));
const seedRecord = readJson(join(playerDirectory, "donk-2007.json"));
const projected = [...generatedRecords, seedRecord].map(projectRecord);
if (projected.length !== sourceDescriptor.records) {
  throw new Error(`expected ${sourceDescriptor.records} identities but projected ${projected.length}`);
}
const steamIds = new Set();
const playerIds = new Set();
for (const record of projected) {
  if (steamIds.has(record.steamId)) throw new Error(`duplicate SteamID64 ${record.steamId}`);
  if (playerIds.has(record.playerId)) throw new Error(`duplicate player_id ${record.playerId}`);
  steamIds.add(record.steamId);
  playerIds.add(record.playerId);
}
projected.sort((left, right) => left.steamId.localeCompare(right.steamId));

if (sourceCommit() !== commit) throw new Error("CS2 Pro SteamID Lib changed during snapshot import");
const verifiedDates = projected.map((record) => record.mappingVerifiedAt).sort();
const metadata = {
  _meta: {
    schemaVersion: 1,
    catalogVersion: `${verifiedDates.at(-1)}.${commit.slice(0, 8)}`,
    repository,
    commit,
    dataLicense: "CC0-1.0 AND CC-BY-SA-3.0",
    records: projected.length,
  },
};
const output = [metadata, ...projected].map((record) => JSON.stringify(record)).join("\n") + "\n";
writeFileSync(outputPath, output, "utf8");
console.log(`Wrote ${projected.length} identities to ${outputPath}`);
