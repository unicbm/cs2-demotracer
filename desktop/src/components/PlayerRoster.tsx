import { useState } from "react";
import {
  resolveCharmCatalog,
  resolveCosmeticCatalog,
  resolveStickerCatalog,
  type CosmeticCatalogEntry,
} from "../cosmeticCatalog";
import { ArrowIcon, CheckIcon, ChevronIcon, CopyIcon, ExternalLinkIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { CosmeticEvidence, Language, PlayerDetails, ViewmodelEvidence } from "../types";
import type { CopyTarget } from "./TaskViews";

export interface RosterPlayer {
  name: string;
  steamId: string;
  kills?: number | null;
  deaths?: number | null;
  assists?: number | null;
  details?: PlayerDetails | null;
}

export interface PlayerSelection {
  teamId: string;
  playerIndex: number;
}

export function playerSelectionKey(selection: PlayerSelection): string {
  return `${selection.teamId}:${selection.playerIndex}`;
}

interface RosterTeamProps<T extends RosterPlayer> {
  teamId: string;
  name: string;
  players: T[];
  words: TextDictionary;
  countLabel: string;
  className?: string;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onOpenExternal: (url: string) => void;
}

function steamProfileUrl(steamId: string): string {
  return `https://steamcommunity.com/profiles/${steamId}`;
}

function hasSteamProfile(steamId: string): boolean {
  return /^[1-9]\d{16}$/.test(steamId);
}

function targetFor(
  playerKey: string,
  kind: "steam" | "crosshair" | "viewmodel" | "inspect",
  index = 0,
): CopyTarget {
  return `player:${playerKey}:${kind}:${index}`;
}

function formatConfigNumber(value: number): string {
  if (Object.is(value, -0) || value === 0) return "0";
  return value.toFixed(4).replace(/\.?(?:0+)$/, "");
}

function viewmodelCommand(viewmodel: ViewmodelEvidence): string {
  const commands: string[] = [];
  if (viewmodel.fov !== null && viewmodel.fov !== undefined) commands.push(`viewmodel_fov ${formatConfigNumber(viewmodel.fov)}`);
  if (viewmodel.offsetX !== null && viewmodel.offsetX !== undefined) commands.push(`viewmodel_offset_x ${formatConfigNumber(viewmodel.offsetX)}`);
  if (viewmodel.offsetY !== null && viewmodel.offsetY !== undefined) commands.push(`viewmodel_offset_y ${formatConfigNumber(viewmodel.offsetY)}`);
  if (viewmodel.offsetZ !== null && viewmodel.offsetZ !== undefined) commands.push(`viewmodel_offset_z ${formatConfigNumber(viewmodel.offsetZ)}`);
  return commands.length > 0 ? `${commands.join("; ")};` : "";
}

function wearLabel(wear: number, words: TextDictionary): string {
  if (wear <= 0.07) return words.wearFactoryNew;
  if (wear <= 0.15) return words.wearMinimalWear;
  if (wear <= 0.37) return words.wearFieldTested;
  if (wear <= 0.44) return words.wearWellWorn;
  return words.wearBattleScarred;
}

function cosmeticKindLabel(cosmetic: CosmeticEvidence, words: TextDictionary): string {
  if (cosmetic.kind === "knife") return words.cosmeticKnife;
  if (cosmetic.kind === "glove") return words.cosmeticGlove;
  if (cosmetic.kind === "agent") return words.cosmeticAgent;
  return words.cosmeticWeapon;
}

function isDisplayableCosmeticEvidence(cosmetic: CosmeticEvidence): boolean {
  if (cosmetic.kind !== "agent") return true;
  const name = cosmetic.itemName?.trim().toLowerCase();
  return cosmetic.itemDefIndex !== 5036
    && cosmetic.itemDefIndex !== 5037
    && name !== "customplayer_t_map_based"
    && name !== "customplayer_ct_map_based";
}

function markedCatalogName(name: string, marker: string, language: Language): string {
  if (!marker) return name;
  const separator = name.indexOf(" | ");
  const mark = language === "zh" ? `（${marker}）` : ` (${marker})`;
  return separator < 0
    ? `${name}${mark}`
    : `${name.slice(0, separator)}${mark}${name.slice(separator)}`;
}

function cosmeticTitle(
  cosmetic: CosmeticEvidence,
  words: TextDictionary,
  language: Language,
  catalogEntry: CosmeticCatalogEntry | null,
): string {
  const fallback = cosmetic.itemDefIndex !== null && cosmetic.itemDefIndex !== undefined
    ? `${cosmeticKindLabel(cosmetic, words)} #${cosmetic.itemDefIndex}`
    : cosmeticKindLabel(cosmetic, words);
  const marker = cosmetic.kind === "knife"
    ? "★"
    : cosmetic.quality === 9 || (cosmetic.stattrakCounter !== null && cosmetic.stattrakCounter !== undefined)
      ? "StatTrak™"
      : "";
  if (catalogEntry) {
    const wear = cosmetic.wear !== null && cosmetic.wear !== undefined ? ` (${wearLabel(cosmetic.wear, words)})` : "";
    return `${markedCatalogName(catalogEntry.name, marker, language)}${wear}`;
  }
  const item = markedCatalogName(cosmetic.itemName || fallback, marker, language);
  const finish = cosmetic.finishName
    || (cosmetic.paintKit !== null && cosmetic.paintKit !== undefined ? `${words.paintKit} #${cosmetic.paintKit}` : "");
  const wear = cosmetic.wear !== null && cosmetic.wear !== undefined ? ` (${wearLabel(cosmetic.wear, words)})` : "";
  return `${item}${finish ? ` | ${finish}` : ""}${wear}`;
}

function CatalogImage({
  entry,
  className,
}: {
  entry: CosmeticCatalogEntry;
  className: string;
}) {
  const [source, setSource] = useState<string | null>(entry.imageUrl);
  if (!source) return null;
  return (
    <img
      className={className}
      src={source}
      alt=""
      loading="lazy"
      decoding="async"
      onError={() => {
        if (entry.fallbackImageUrl && source !== entry.fallbackImageUrl) {
          setSource(entry.fallbackImageUrl);
        } else {
          setSource(null);
        }
      }}
    />
  );
}

function metricValues(player: RosterPlayer) {
  const kills = player.kills;
  const deaths = player.deaths;
  const headshots = player.details?.headshotKills;
  const totalDamage = player.details?.totalDamage;
  const rounds = player.details?.statsRounds;
  return {
    kd: kills !== null && kills !== undefined && deaths !== null && deaths !== undefined && deaths > 0
      ? (kills / deaths).toFixed(2)
      : null,
    adr: totalDamage !== null && totalDamage !== undefined && rounds !== null && rounds !== undefined && rounds > 0
      ? (totalDamage / rounds).toFixed(1)
      : null,
    hs: kills !== null && kills !== undefined && kills > 0 && headshots !== null && headshots !== undefined && headshots <= kills
      ? `${(headshots / kills * 100).toFixed(1)}% · ${headshots}`
      : null,
  };
}

function CopyAction({
  value,
  target,
  copiedTarget,
  label,
  copiedLabel,
  onCopy,
}: {
  value: string;
  target: CopyTarget;
  copiedTarget: CopyTarget | null;
  label: string;
  copiedLabel: string;
  onCopy: (value: string, target: CopyTarget) => void;
}) {
  const copied = copiedTarget === target;
  return (
    <button className="roster-copy-action" type="button" onClick={() => onCopy(value, target)} title={label}>
      {copied ? <CheckIcon size={13} /> : <CopyIcon size={13} />}
      <span>{copied ? copiedLabel : label}</span>
    </button>
  );
}

function CosmeticCard({
  cosmetic,
  index,
  playerKey,
  language,
  words,
  copiedTarget,
  onCopy,
  onOpenExternal,
}: {
  cosmetic: CosmeticEvidence;
  index: number;
  playerKey: string;
  language: Language;
  words: TextDictionary;
  copiedTarget: CopyTarget | null;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
}) {
  const stickers = cosmetic.stickers ?? [];
  const charms = cosmetic.charms ?? [];
  const side = cosmetic.side === "t" ? words.sideT : cosmetic.side === "ct" ? words.sideCt : null;
  const catalogEntry = resolveCosmeticCatalog(cosmetic, language);
  const title = cosmeticTitle(cosmetic, words, language, catalogEntry);
  return (
    <details className="roster-cosmetic-card">
      <summary>
        <div className="roster-cosmetic-summary">
          {catalogEntry ? <CatalogImage key={catalogEntry.imageUrl} entry={catalogEntry} className="roster-cosmetic-preview" /> : null}
          <span>
            <small>{cosmeticKindLabel(cosmetic, words)}{side ? ` · ${side}` : ""}</small>
            <strong title={title}>{title}</strong>
          </span>
        </div>
        <ChevronIcon size={14} />
      </summary>
      <div className="roster-cosmetic-details">
        <dl>
          {cosmetic.itemDefIndex !== null && cosmetic.itemDefIndex !== undefined ? <div><dt>{words.itemDefinition}</dt><dd>{cosmetic.itemDefIndex}</dd></div> : null}
          {cosmetic.paintKit !== null && cosmetic.paintKit !== undefined ? <div><dt>{words.paintKit}</dt><dd>{cosmetic.paintKit}</dd></div> : null}
          {cosmetic.seed !== null && cosmetic.seed !== undefined ? <div><dt>{words.patternTemplate}</dt><dd>{cosmetic.seed}</dd></div> : null}
          {cosmetic.wear !== null && cosmetic.wear !== undefined ? <div><dt>{words.wearRating}</dt><dd>{cosmetic.wear.toFixed(8)}</dd></div> : null}
          {cosmetic.stattrakCounter !== null && cosmetic.stattrakCounter !== undefined ? <div><dt>{words.stattrakCount}</dt><dd>{cosmetic.stattrakCounter}</dd></div> : null}
          {cosmetic.customName ? (
            <div className="is-custom-name">
              <dt>{words.customName}</dt>
              <dd title={cosmetic.customName}>{cosmetic.customName}</dd>
            </div>
          ) : null}
          {cosmetic.itemId ? <div><dt>{words.itemId}</dt><dd>{cosmetic.itemId}</dd></div> : null}
        </dl>

        {stickers.length > 0 ? (
          <section className="roster-attachments">
            <h5>{words.stickers.replace("{count}", String(stickers.length))}</h5>
            <ul>
              {stickers.map((sticker) => {
                const entry = resolveStickerCatalog(sticker.stickerId, language);
                return (
                  <li key={`${sticker.slot}-${sticker.stickerId}`}>
                    {entry ? <CatalogImage key={entry.imageUrl} entry={entry} className="roster-attachment-preview" /> : null}
                    <span>
                      <strong title={entry?.name}>{words.stickerSlot.replace("{slot}", String(sticker.slot))} · {entry?.name ?? `#${sticker.stickerId}`}</strong>
                      <code>
                        #{sticker.stickerId} · wear {formatConfigNumber(sticker.wear)} · xy {formatConfigNumber(sticker.offsetX)}, {formatConfigNumber(sticker.offsetY)}
                        {sticker.scale !== null && sticker.scale !== undefined ? ` · scale ${formatConfigNumber(sticker.scale)}` : ""}
                        {sticker.rotation !== null && sticker.rotation !== undefined ? ` · rotation ${formatConfigNumber(sticker.rotation)}°` : ""}
                      </code>
                    </span>
                  </li>
                );
              })}
            </ul>
          </section>
        ) : null}

        {charms.length > 0 ? (
          <section className="roster-attachments">
            <h5>{words.charms.replace("{count}", String(charms.length))}</h5>
            <ul>
              {charms.map((charm) => {
                const entry = resolveCharmCatalog(charm.charmId, charm.stickerId, language);
                return (
                  <li key={`${charm.slot}-${charm.charmId}`}>
                    {entry ? <CatalogImage key={entry.imageUrl} entry={entry} className="roster-attachment-preview" /> : null}
                    <span>
                      <strong title={entry?.name}>{words.charmSlot.replace("{slot}", String(charm.slot))} · {entry?.name ?? `#${charm.charmId}`}</strong>
                      <code>
                        #{charm.charmId} · xyz {formatConfigNumber(charm.offsetX)}, {formatConfigNumber(charm.offsetY)}, {formatConfigNumber(charm.offsetZ)}
                        {charm.seed !== null && charm.seed !== undefined ? ` · seed ${charm.seed}` : ""}
                        {charm.highlight !== null && charm.highlight !== undefined ? ` · highlight ${charm.highlight}` : ""}
                        {charm.stickerId !== null && charm.stickerId !== undefined ? ` · sticker #${charm.stickerId}` : ""}
                      </code>
                    </span>
                  </li>
                );
              })}
            </ul>
          </section>
        ) : null}

        {cosmetic.inspectCommand || cosmetic.inspectUrl ? (
          <div className="roster-cosmetic-actions">
            {cosmetic.inspectCommand ? (
              <CopyAction
                value={cosmetic.inspectCommand}
                target={targetFor(playerKey, "inspect", index)}
                copiedTarget={copiedTarget}
                label={words.copyInspectCommand}
                copiedLabel={words.copied}
                onCopy={onCopy}
              />
            ) : null}
            {cosmetic.inspectUrl ? (
              <button className="roster-external-action" type="button" onClick={() => onOpenExternal(cosmetic.inspectUrl!)}>
                <ExternalLinkIcon size={13} />{words.inspectInGame}
              </button>
            ) : null}
          </div>
        ) : null}
      </div>
    </details>
  );
}

function CrosshairEvidence({
  codes,
  playerKey,
  words,
  copiedTarget,
  onCopy,
}: {
  codes: string[];
  playerKey: string;
  words: TextDictionary;
  copiedTarget: CopyTarget | null;
  onCopy: (value: string, target: CopyTarget) => void;
}) {
  return (
    <section className="roster-evidence-section player-crosshair-section">
      <header><strong>{words.crosshairCodes}</strong><small>{words.crosshairCodesHelp}</small></header>
      <ul className="crosshair-config-list">
        {codes.map((code, index) => (
          <li key={`${code}-${index}`}>
            <span>
              <small>{words.sharedCrosshair} {index + 1}</small>
              <code>{code}</code>
            </span>
            <CopyAction value={code} target={targetFor(playerKey, "crosshair", index)} copiedTarget={copiedTarget} label={words.copyCommand} copiedLabel={words.copied} onCopy={onCopy} />
          </li>
        ))}
      </ul>
    </section>
  );
}

export function PlayerDossier({
  playerKey,
  player,
  language,
  words,
  copiedTarget,
  onCopy,
  onOpenExternal,
}: {
  playerKey: string;
  player: RosterPlayer;
  language: Language;
  words: TextDictionary;
  copiedTarget: CopyTarget | null;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
}) {
  const details = player.details;
  const metrics = metricValues(player);
  const crosshairCodes = details?.crosshairCodes ?? [];
  const viewmodels = (details?.viewmodels ?? []).map(viewmodelCommand).filter(Boolean);
  const cosmetics = (details?.cosmetics ?? []).filter(isDisplayableCosmeticEvidence);
  const steamProfileAvailable = hasSteamProfile(player.steamId);
  return (
    <div className="player-dossier">
      <div className="roster-profile-line">
        <span><small>{words.steamId}</small><code>{player.steamId || "—"}</code></span>
        <div>
          {steamProfileAvailable ? (
            <>
              <CopyAction
                value={player.steamId}
                target={targetFor(playerKey, "steam")}
                copiedTarget={copiedTarget}
                label={words.copySteamId}
                copiedLabel={words.copied}
                onCopy={onCopy}
              />
              <button className="roster-external-action" type="button" onClick={() => onOpenExternal(steamProfileUrl(player.steamId))}>
                <ExternalLinkIcon size={13} />{words.openSteamProfile}
              </button>
            </>
          ) : null}
        </div>
      </div>

      {metrics.kd || metrics.adr || metrics.hs ? (
        <div className="roster-metric-strip">
          {metrics.kd ? <span><small>{words.kd}</small><strong>{metrics.kd}</strong></span> : null}
          {metrics.adr ? <span><small>{words.adr}</small><strong>{metrics.adr}</strong></span> : null}
          {metrics.hs ? <span><small>{words.headshotRate}</small><strong>{metrics.hs}</strong></span> : null}
        </div>
      ) : null}

      <div className="player-setup-grid">
        {crosshairCodes.length > 0 ? (
          <CrosshairEvidence
            key={playerKey}
            codes={crosshairCodes}
            playerKey={playerKey}
            words={words}
            copiedTarget={copiedTarget}
            onCopy={onCopy}
          />
        ) : null}

        {viewmodels.length > 0 ? (
          <section className="roster-evidence-section player-viewmodel-section">
            <header><strong>{words.viewmodelProfiles}</strong><small>{words.viewmodelProfilesHelp}</small></header>
            <ul className="roster-command-list">
              {viewmodels.map((command, index) => (
                <li key={command}>
                  <span><small>{words.viewmodelProfile.replace("{index}", String(index + 1))}</small><code>{command}</code></span>
                  <CopyAction value={command} target={targetFor(playerKey, "viewmodel", index)} copiedTarget={copiedTarget} label={words.copyCommand} copiedLabel={words.copied} onCopy={onCopy} />
                </li>
              ))}
            </ul>
          </section>
        ) : null}
      </div>

      <section className="roster-evidence-section roster-cosmetics">
        <header>
          <strong>{words.cosmeticEvidence}{words.cosmeticEvidenceCount.replace("{count}", String(cosmetics.length))}</strong>
          <small>{words.cosmeticEvidenceHelp}</small>
        </header>
        {cosmetics.length > 0 ? (
          <div className="roster-cosmetic-list">
            {cosmetics.map((cosmetic, index) => (
              <CosmeticCard
                cosmetic={cosmetic}
                index={index}
                playerKey={playerKey}
                language={language}
                words={words}
                copiedTarget={copiedTarget}
                onCopy={onCopy}
                onOpenExternal={onOpenExternal}
                key={`${cosmetic.kind}-${cosmetic.itemId || cosmetic.itemDefIndex}-${cosmetic.paintKit}-${cosmetic.side || "both"}-${index}`}
              />
            ))}
          </div>
        ) : <p className="roster-evidence-empty">{words.cosmeticEvidenceEmpty}</p>}
      </section>

    </div>
  );
}

export function RosterTeam<T extends RosterPlayer>({
  teamId,
  name,
  players,
  words,
  countLabel,
  className = "",
  onSelectPlayer,
  onOpenExternal,
}: RosterTeamProps<T>) {
  const stat = (value: number | null | undefined) => value ?? "—";
  return (
    <section className={`archive-roster-team ${className}`.trim()} aria-label={name}>
      <header>
        <strong title={name}>{name}</strong>
        <span>{countLabel.replace("{count}", String(players.length))}</span>
      </header>
      <ul>
        {players.map((player, playerIndex) => {
          const selection = { teamId, playerIndex };
          const selectionKey = playerSelectionKey(selection);
          const steamProfileAvailable = hasSteamProfile(player.steamId);
          const hasKda = player.kills !== null && player.kills !== undefined
            || player.deaths !== null && player.deaths !== undefined
            || player.assists !== null && player.assists !== undefined;
          return (
            <li className="roster-player" key={selectionKey}>
              <button
                className="roster-player-summary"
                type="button"
                data-player-key={selectionKey}
                title={steamProfileAvailable ? words.rosterPlayerHint : words.playerAnalysis}
                onClick={() => onSelectPlayer(selection)}
                onContextMenu={(event) => {
                  if (!steamProfileAvailable) return;
                  event.preventDefault();
                  onOpenExternal(steamProfileUrl(player.steamId));
                }}
              >
                <strong title={player.name}>{player.name}</strong>
                {hasKda ? (
                  <span
                    className="archive-roster-kda"
                    title={`${words.kda}: ${stat(player.kills)} / ${stat(player.deaths)} / ${stat(player.assists)}`}
                  >
                    <b>{stat(player.kills)}</b><i>/</i><b>{stat(player.deaths)}</b><i>/</i><b>{stat(player.assists)}</b>
                  </span>
                ) : null}
                <code title={`${words.steamId} ${player.steamId}`}>{player.steamId}</code>
                <ArrowIcon size={13} />
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
