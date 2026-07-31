/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

export type ProSteamIdEvidence = "direct" | "curated";

export interface ProSteamIdMappingSource {
  sourceId: string;
  url: string;
  retrievedAt: string;
  identityEvidence: ProSteamIdEvidence;
  revisionId: string | null;
  revisionTimestamp: string | null;
}

export interface ProSteamIdIdentity {
  steamId: string;
  playerId: string;
  handle: string;
  aliases: readonly string[];
  mappingVerifiedAt: string;
  nameNative: string | null;
  nameLatin: string | null;
  country: string | null;
  countryCode: string | null;
  birthDate: string | null;
  status: string | null;
  roles: readonly string[];
  externalIds: Readonly<Record<string, string>>;
  mappingSources: readonly ProSteamIdMappingSource[];
}

export interface ProSteamIdCatalog {
  schemaVersion: 1;
  catalogVersion: string;
  repository: string;
  commit: string;
  dataLicense: string;
  players: ReadonlyMap<string, ProSteamIdIdentity>;
}

function objectValue(value: unknown, context: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${context} must be an object`);
  }
  return value as Record<string, unknown>;
}

function stringValue(value: unknown, context: string): string {
  if (typeof value !== "string" || value.trim() === "") {
    throw new Error(`${context} must be a non-empty string`);
  }
  return value.trim();
}

function optionalString(value: unknown, context: string): string | null {
  return value === undefined ? null : stringValue(value, context);
}

function dateValue(value: unknown, context: string): string {
  const date = stringValue(value, context);
  const match = date.match(/^(\d{4})-(\d{2})-(\d{2})$/);
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
  return date;
}

function stringRecord(value: unknown, context: string): Readonly<Record<string, string>> {
  const record = objectValue(value, context);
  return Object.fromEntries(Object.entries(record).map(([key, candidate]) => [
    key,
    stringValue(candidate, `${context}.${key}`),
  ]));
}

function parseMappingSource(value: unknown, context: string): ProSteamIdMappingSource {
  const source = objectValue(value, context);
  const identityEvidence = stringValue(source.identityEvidence, `${context}.identityEvidence`);
  if (identityEvidence !== "direct" && identityEvidence !== "curated") {
    throw new Error(`${context}.identityEvidence must be direct or curated`);
  }
  const url = stringValue(source.url, `${context}.url`);
  if (!url.startsWith("https://")) throw new Error(`${context}.url must use HTTPS`);
  const revisionId = optionalString(source.revisionId, `${context}.revisionId`);
  const revisionTimestamp = optionalString(source.revisionTimestamp, `${context}.revisionTimestamp`);
  if (/[?&]oldid=/.test(url) && (!revisionId || !revisionTimestamp)) {
    throw new Error(`${context} must retain its Liquipedia revision ID and timestamp`);
  }
  return {
    sourceId: stringValue(source.sourceId, `${context}.sourceId`),
    url,
    retrievedAt: dateValue(source.retrievedAt, `${context}.retrievedAt`),
    identityEvidence,
    revisionId,
    revisionTimestamp,
  };
}

function parsePlayer(value: unknown, index: number): ProSteamIdIdentity {
  const context = `players[${index}]`;
  const player = objectValue(value, context);
  const steamId = stringValue(player.steamId, `${context}.steamId`);
  if (!/^[1-9]\d{16}$/.test(steamId)) throw new Error(`${context}.steamId must be a SteamID64`);
  const playerId = stringValue(player.playerId, `${context}.playerId`);
  if (!/^[a-z0-9][a-z0-9-]*$/.test(playerId)) throw new Error(`${context}.playerId is invalid`);
  if (!Array.isArray(player.aliases)) throw new Error(`${context}.aliases must be an array`);
  const aliases = player.aliases.map((alias, aliasIndex) => (
    stringValue(alias, `${context}.aliases[${aliasIndex}]`)
  ));
  if (new Set(aliases).size !== aliases.length) throw new Error(`${context}.aliases contains duplicates`);
  if (player.roles !== undefined && !Array.isArray(player.roles)) {
    throw new Error(`${context}.roles must be an array`);
  }
  const roles = (player.roles ?? []).map((role, roleIndex) => (
    stringValue(role, `${context}.roles[${roleIndex}]`)
  ));
  if (new Set(roles).size !== roles.length) throw new Error(`${context}.roles contains duplicates`);
  if (!Array.isArray(player.mappingSources) || player.mappingSources.length === 0) {
    throw new Error(`${context}.mappingSources must not be empty`);
  }
  const externalIds = stringRecord(player.externalIds, `${context}.externalIds`);
  if (externalIds.esea && /[|={}]/.test(externalIds.esea)) {
    throw new Error(`${context}.externalIds.esea contains template-field syntax`);
  }
  return {
    steamId,
    playerId,
    handle: stringValue(player.handle, `${context}.handle`),
    aliases,
    mappingVerifiedAt: dateValue(player.mappingVerifiedAt, `${context}.mappingVerifiedAt`),
    nameNative: optionalString(player.nameNative, `${context}.nameNative`),
    nameLatin: optionalString(player.nameLatin, `${context}.nameLatin`),
    country: optionalString(player.country, `${context}.country`),
    countryCode: optionalString(player.countryCode, `${context}.countryCode`),
    birthDate: player.birthDate === undefined ? null : dateValue(player.birthDate, `${context}.birthDate`),
    status: optionalString(player.status, `${context}.status`),
    roles,
    externalIds,
    mappingSources: player.mappingSources.map((source, sourceIndex) => (
      parseMappingSource(source, `${context}.mappingSources[${sourceIndex}]`)
    )),
  };
}

export function parseProSteamIdCatalogJsonl(text: string): ProSteamIdCatalog {
  const lines = text.split(/\r?\n/).filter((line) => line.trim() !== "");
  if (lines.length < 2) throw new Error("CS2 Pro SteamID catalog is empty");
  const metadataRoot = objectValue(JSON.parse(lines[0]), "catalog metadata");
  const metadata = objectValue(metadataRoot._meta, "catalog metadata._meta");
  if (metadata.schemaVersion !== 1) throw new Error("CS2 Pro SteamID catalog schemaVersion must be 1");
  const expectedRecords = metadata.records;
  if (typeof expectedRecords !== "number" || !Number.isSafeInteger(expectedRecords) || expectedRecords <= 0) {
    throw new Error("catalog metadata.records must be a positive integer");
  }
  if (lines.length - 1 !== expectedRecords) {
    throw new Error(`catalog expected ${expectedRecords} records but contains ${lines.length - 1}`);
  }

  const players = new Map<string, ProSteamIdIdentity>();
  const playerIds = new Set<string>();
  lines.slice(1).forEach((line, index) => {
    const player = parsePlayer(JSON.parse(line), index);
    if (players.has(player.steamId)) throw new Error(`catalog contains duplicate SteamID64 ${player.steamId}`);
    if (playerIds.has(player.playerId)) throw new Error(`catalog contains duplicate player ID ${player.playerId}`);
    players.set(player.steamId, player);
    playerIds.add(player.playerId);
  });
  return {
    schemaVersion: 1,
    catalogVersion: stringValue(metadata.catalogVersion, "catalog metadata.catalogVersion"),
    repository: stringValue(metadata.repository, "catalog metadata.repository"),
    commit: stringValue(metadata.commit, "catalog metadata.commit"),
    dataLicense: stringValue(metadata.dataLicense, "catalog metadata.dataLicense"),
    players,
  };
}

export function resolveProSteamIdFromCatalog(
  catalog: ProSteamIdCatalog,
  steamId: string,
): ProSteamIdIdentity | null {
  return catalog.players.get(steamId) ?? null;
}
