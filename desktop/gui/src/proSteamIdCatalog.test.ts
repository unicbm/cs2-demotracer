import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  parseProSteamIdCatalogJsonl,
  resolveProSteamIdFromCatalog,
} from "./proSteamIdCatalog.ts";

const source = readFileSync(
  new URL("./data/cs2-pro-steamid-lib.v1.jsonl", import.meta.url),
  "utf8",
);

test("attributed registry snapshot resolves a curated professional identity", () => {
  const catalog = parseProSteamIdCatalogJsonl(source);
  const donk = resolveProSteamIdFromCatalog(catalog, "76561198386265483");
  const karrigan = resolveProSteamIdFromCatalog(catalog, "76561197989430253");
  const niko = resolveProSteamIdFromCatalog(catalog, "76561198041683378");
  const sh1ro = resolveProSteamIdFromCatalog(catalog, "76561198081484775");
  const zywoo = resolveProSteamIdFromCatalog(catalog, "76561198113666193");
  const jamesBardolph = resolveProSteamIdFromCatalog(catalog, "76561197960268122");

  assert.equal(catalog.players.size, 4_576);
  assert.equal(donk?.handle, "donk");
  assert.equal(donk?.nameLatin, "Danil Kryshkovets");
  assert.equal(donk?.countryCode, "RU");
  assert.equal(donk?.mappingSources[0]?.identityEvidence, "curated");
  assert.match(donk?.mappingSources[0]?.url ?? "", /^https:\/\/liquipedia\.net\//);
  assert.equal(karrigan?.nameLatin, "Finn Andersen");
  assert.equal(karrigan?.country, "Denmark");
  assert.equal(karrigan?.countryCode, "DK");
  assert.equal(niko?.nameLatin, "Nikola Kovač");
  assert.equal(niko?.countryCode, "BA");
  assert.equal(niko?.birthDate, "1997-02-16");
  assert.equal(niko?.externalIds.esea, "571970");
  assert.equal(sh1ro?.birthDate, "2001-07-15");
  assert.deepEqual(zywoo?.roles, ["awp", "rifle"]);
  assert.equal(jamesBardolph?.externalIds.esea, undefined);
  assert.ok([...catalog.players.values()].filter((player) => player.birthDate).length >= 3_900);
});

test("registry parser rejects duplicate SteamID64 records", () => {
  const lines = source.trimEnd().split("\n");
  const metadata = JSON.parse(lines[0]) as { _meta: { records: number } };
  metadata._meta.records += 1;
  const duplicate = [JSON.stringify(metadata), ...lines.slice(1), lines[1]].join("\n");

  assert.throws(() => parseProSteamIdCatalogJsonl(duplicate), /duplicate SteamID64/);
});

test("registry parser requires Liquipedia revision attribution", () => {
  const lines = source.trimEnd().split("\n");
  const player = JSON.parse(lines[1]) as {
    mappingSources: Array<{ revisionTimestamp?: string }>;
  };
  delete player.mappingSources[0].revisionTimestamp;
  lines[1] = JSON.stringify(player);

  assert.throws(() => parseProSteamIdCatalogJsonl(lines.join("\n")), /revision ID and timestamp/);
});

test("registry parser rejects impossible calendar dates", () => {
  const lines = source.trimEnd().split("\n");
  const player = JSON.parse(lines[1]) as { birthDate: string };
  player.birthDate = "2025-02-29";
  lines[1] = JSON.stringify(player);

  assert.throws(() => parseProSteamIdCatalogJsonl(lines.join("\n")), /valid calendar date/);
});

test("registry parser rejects swallowed template fields in ESEA values", () => {
  const lines = source.trimEnd().split("\n");
  const player = JSON.parse(lines[1]) as { externalIds: Record<string, string> };
  player.externalIds.esea = "|faceitdb=not-an-esea-id";
  lines[1] = JSON.stringify(player);

  assert.throws(() => parseProSteamIdCatalogJsonl(lines.join("\n")), /externalIds\.esea contains template-field syntax/);
});
