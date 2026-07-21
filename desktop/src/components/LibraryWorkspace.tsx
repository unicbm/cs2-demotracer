import { type MouseEvent as ReactMouseEvent, useEffect, useRef, useState } from "react";
import { ArrowIcon, CloseIcon, FolderIcon, LibraryIcon, PlusIcon, RefreshIcon, ReplayIcon, SearchIcon, TraceMark } from "../icons";
import type { TextDictionary } from "../i18n";
import type { DemoLibraryEntry, DemoLibraryScan, Language, LibraryPlayerSummary } from "../types";
import { displayMap, MapArtwork, mapArtworkStyle } from "./MapArtwork";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import "./library-workspace.css";

export type LibrarySort = "recent" | "map" | "platform";

interface LibraryWorkspaceProps {
  words: TextDictionary;
  language: Language;
  exportRoot: string;
  roots: string[];
  scan: DemoLibraryScan | null;
  loading: boolean;
  taskBusy: boolean;
  archiveOpenDisabled: boolean;
  repairingManifest: string;
  repairingLibrary: boolean;
  importingArchives: boolean;
  notice: string;
  query: string;
  mapFilter: string;
  platformFilter: string;
  sort: LibrarySort;
  onQueryChange: (value: string) => void;
  onMapFilterChange: (value: string) => void;
  onPlatformFilterChange: (value: string) => void;
  onSortChange: (value: LibrarySort) => void;
  onAddRoot: () => void;
  onRemoveRoot: (root: string) => void;
  onChooseExportRoot: () => void;
  onRefresh: () => void;
  onImportArchives: () => void;
  onRepairLibrary: () => void;
  onConvert: () => void;
  onOpenEntry: (entry: DemoLibraryEntry) => void;
  onRepairEntry: (entry: DemoLibraryEntry) => void;
  onRevealManifest: (entry: DemoLibraryEntry) => void;
  onRevealDemo: (entry: DemoLibraryEntry) => void;
  onReparseEntry: (entry: DemoLibraryEntry) => void;
}

function formatDateParts(value: number, language: Language): { date: string; time: string; iso?: string } {
  if (!Number.isFinite(value) || value <= 0) return { date: "—", time: "" };
  const locale = language === "zh" ? "zh-CN" : "en-US";
  const date = new Intl.DateTimeFormat(locale, {
    year: "numeric",
    month: "short",
    day: "numeric",
  }).format(new Date(value));
  const time = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" }).format(new Date(value));
  return { date, time, iso: new Date(value).toISOString() };
}

function formatDuration(value: number | null | undefined): string | null {
  if (!value || !Number.isFinite(value)) return null;
  const totalSeconds = Math.max(0, Math.round(value));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function platformName(value: string): string {
  return value.toLowerCase() === "faceit" ? "FACEIT" : value;
}

function compatibilityLabel(entry: DemoLibraryEntry, words: TextDictionary): string {
  if (entry.compatibility === "current") return words.versionCurrent;
  if (entry.compatibility === "supported") return words.versionSupported;
  if (entry.compatibility === "legacy") return words.versionLegacy;
  return words.versionUnsupported;
}

function playerSearchText(player: LibraryPlayerSummary): string {
  return `${player.name} ${player.steamId} ${player.teamName ?? ""}`.toLowerCase();
}

function entrySearchText(entry: DemoLibraryEntry): string {
  return [
    entry.demoPath,
    entry.sourcePath,
    entry.demoId,
    entry.displayName,
    entry.map,
    entry.demoSource?.name,
    entry.serverName,
    entry.score?.teamA.name,
    entry.score?.teamB.name,
    ...entry.players.map(playerSearchText),
  ].filter(Boolean).join(" ").toLowerCase();
}

function cleanTeamName(value: string | null | undefined): string | null {
  const name = value?.trim();
  if (!name) return null;
  const normalized = name.toLowerCase().replace(/[\s_-]+/g, "");
  return ["t", "ct", "terrorist", "terrorists", "counterterrorist", "counterterrorists"].includes(normalized)
    ? null
    : name;
}

function sameTeamName(left: string, right: string): boolean {
  return left.localeCompare(right, undefined, { sensitivity: "base" }) === 0;
}

function teamNameForIdentity(players: LibraryPlayerSummary[], team: "a" | "b"): string | null {
  const counts = new Map<string, { name: string; count: number }>();
  for (const player of players) {
    if (player.team?.toLowerCase() !== team) continue;
    const name = cleanTeamName(player.teamName);
    if (!name) continue;
    const key = name.toLocaleLowerCase();
    const current = counts.get(key);
    counts.set(key, { name, count: (current?.count ?? 0) + 1 });
  }
  return [...counts.values()].sort((left, right) => right.count - left.count)[0]?.name ?? null;
}

function playerNamesForIdentity(
  players: LibraryPlayerSummary[],
  team: "a" | "b",
  identity: string | null,
): string[] {
  const direct = players.filter((player) => player.team?.toLowerCase() === team);
  const matching = direct.length > 0
    ? direct
    : identity
      ? players.filter((player) => player.teamName && sameTeamName(player.teamName, identity))
      : [];
  const names = new Map<string, string>();
  for (const player of matching) {
    const name = player.name.trim();
    if (name) names.set(name.toLocaleLowerCase(), name);
  }
  return [...names.values()];
}

function LibraryRow({
  entry,
  words,
  language,
  onOpen,
  onRepair,
  onRevealManifest,
  onRevealDemo,
  onReparse,
  repairing,
  disabled,
  taskBusy,
}: {
  entry: DemoLibraryEntry;
  words: TextDictionary;
  language: Language;
  onOpen: () => void;
  onRepair: () => void;
  onRevealManifest: () => void;
  onRevealDemo: () => void;
  onReparse: () => void;
  repairing: boolean;
  disabled: boolean;
  taskBusy: boolean;
}) {
  const [menu, setMenu] = useState<ContextMenuState | null>(null);
  const scoreFirstIdentity = cleanTeamName(entry.score?.teamA.name);
  const scoreSecondIdentity = cleanTeamName(entry.score?.teamB.name);
  const firstIdentity = scoreFirstIdentity
    || teamNameForIdentity(entry.players, "a")
    || null;
  const secondIdentity = [
    scoreSecondIdentity,
    teamNameForIdentity(entry.players, "b"),
  ].find((name): name is string => name !== null
    && (!firstIdentity || !sameTeamName(name, firstIdentity))) ?? null;
  const firstName = firstIdentity || words.teamA;
  const secondName = secondIdentity || words.teamB;
  const firstPlayers = playerNamesForIdentity(entry.players, "a", firstIdentity);
  const secondPlayers = playerNamesForIdentity(entry.players, "b", secondIdentity);
  const duration = formatDuration(entry.durationSeconds);
  const demoDate = formatDateParts(entry.sourceModifiedAtMs ?? 0, language);
  const sourceName = entry.demoSource ? platformName(entry.demoSource.name) : words.unknownPlatform;
  const scoreStatus = entry.score?.status || (entry.scoreIsSnapshot ? "snapshot" : "final");
  const needsMetadata = entry.metadataStatus !== "current";
  const needsSourceLink = !entry.sourcePath || entry.sourceAvailable === false;
  const needsRepair = needsMetadata || needsSourceLink;
  const repairLabel = needsMetadata ? words.repairMetadata : words.linkSourceDemo;
  const scoreTitle = scoreStatus === "snapshot"
    ? words.archiveScoreSnapshot
    : scoreStatus === "completed"
      ? words.completedScore
      : undefined;

  const activate = () => {
    if (!disabled) onOpen();
  };
  const openMenu = (event: ReactMouseEvent) => {
    event.preventDefault();
    setMenu({
      x: event.clientX,
      y: event.clientY,
      label: words.archiveContextMenu,
      items: [
        { label: words.openArchive, icon: <ReplayIcon size={15} />, disabled, onSelect: onOpen },
        { label: words.openManifestLocation, icon: <FolderIcon size={15} />, onSelect: onRevealManifest },
        { label: words.openDemoLocation, icon: <FolderIcon size={15} />, disabled: needsSourceLink, onSelect: onRevealDemo },
        { label: words.reparseDemo, icon: <RefreshIcon size={15} />, dividerBefore: true, disabled: disabled || taskBusy, onSelect: onReparse },
        ...(needsRepair ? [{ label: repairLabel, icon: <RefreshIcon size={15} />, disabled: repairing || disabled || taskBusy, onSelect: onRepair }] : []),
      ],
    });
  };

  return (
    <>
    <article
      className="library-row"
      style={mapArtworkStyle(entry.map)}
      tabIndex={disabled ? -1 : 0}
      role="button"
      aria-disabled={disabled}
      onClick={activate}
      onKeyDown={(event) => {
        if (event.key !== "Enter" && event.key !== " ") return;
        event.preventDefault();
        activate();
      }}
      onContextMenu={openMenu}
    >
      <div className="library-row-date" title={entry.serverName ? `${words.demoServerName}: ${entry.serverName}` : undefined}>
        <time dateTime={demoDate.iso}>
          <strong>{demoDate.date}</strong>
          <small>
            <span>{demoDate.time || words.timeUnknown}</span>
            {entry.demoSource ? <b>{sourceName}</b> : null}
          </small>
        </time>
      </div>
      <div className="library-row-match" aria-label={`${firstName} vs ${secondName}`}>
        <div className="library-row-team">
          <strong title={firstName}>{firstName}</strong>
          {firstPlayers.length > 0 ? <small title={firstPlayers.join(" · ")}>{firstPlayers.join(" · ")}</small> : null}
        </div>
        <span>vs</span>
        <div className="library-row-team is-opponent">
          <strong title={secondName}>{secondName}</strong>
          {secondPlayers.length > 0 ? <small title={secondPlayers.join(" · ")}>{secondPlayers.join(" · ")}</small> : null}
        </div>
      </div>
      <div
        className="library-row-score"
        aria-label={entry.score && scoreStatus !== "snapshot" ? `${entry.score.teamA.score} : ${entry.score.teamB.score}` : words.scoreUnavailable}
        title={scoreTitle}
      >
        <span className="library-row-score-numbers">
          {entry.score && scoreStatus !== "snapshot"
            ? <><strong>{entry.score.teamA.score}</strong><i>:</i><strong>{entry.score.teamB.score}</strong></>
            : <span>— : —</span>}
        </span>
        <small>
          <span>{words.archiveRoundsShort.replace("{count}", String(entry.rounds))}</span>
          {duration ? <span>{duration}</span> : null}
        </small>
        {entry.compatibility !== "current" ? <em className={`is-${entry.compatibility}`}>{compatibilityLabel(entry, words)}</em> : null}
        {needsRepair ? <em>{repairLabel}</em> : null}
      </div>
      <div className="library-row-map">
        <MapArtwork map={entry.map} className="library-row-map-artwork" />
        <strong>{displayMap(entry.map)}</strong>
      </div>
      <ArrowIcon className="library-row-arrow" size={14} />

    </article>
    {menu ? <ContextMenu menu={menu} onClose={() => setMenu(null)} /> : null}
    </>
  );
}

function LibrarySkeleton() {
  return (
    <div className="library-list" aria-hidden="true">
      {[0, 1, 2, 3].map((index) => (
        <div className="library-row library-card-skeleton" key={index}>
          <div /><span /><span /><span />
        </div>
      ))}
    </div>
  );
}

export function LibraryWorkspace({
  words,
  language,
  exportRoot,
  roots,
  scan,
  loading,
  taskBusy,
  archiveOpenDisabled,
  repairingManifest,
  repairingLibrary,
  importingArchives,
  notice,
  query,
  mapFilter,
  platformFilter,
  sort,
  onQueryChange,
  onMapFilterChange,
  onPlatformFilterChange,
  onSortChange,
  onAddRoot,
  onRemoveRoot,
  onChooseExportRoot,
  onRefresh,
  onImportArchives,
  onRepairLibrary,
  onConvert,
  onOpenEntry,
  onRepairEntry,
  onRevealManifest,
  onRevealDemo,
  onReparseEntry,
}: LibraryWorkspaceProps) {
  const rootsMenuRef = useRef<HTMLDetailsElement | null>(null);
  useEffect(() => {
    const closeOnPointer = (event: PointerEvent) => {
      const menu = rootsMenuRef.current;
      if (menu?.open && event.target instanceof Node && !menu.contains(event.target)) menu.open = false;
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      const menu = rootsMenuRef.current;
      if (event.key === "Escape" && menu?.open) {
        menu.open = false;
        menu.querySelector<HTMLElement>("summary")?.focus();
      }
    };
    document.addEventListener("pointerdown", closeOnPointer);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnPointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, []);
  const closeRootsMenu = () => {
    if (rootsMenuRef.current) rootsMenuRef.current.open = false;
  };
  const maps = [...new Set((scan?.entries ?? []).map((entry) => entry.map).filter(Boolean))]
    .sort((left, right) => left.localeCompare(right));
  const platforms = [...new Set((scan?.entries ?? []).map((entry) => entry.demoSource?.name).filter((value): value is string => Boolean(value)))]
    .sort((left, right) => left.localeCompare(right));
  const normalizedQuery = query.trim().toLowerCase();
  const isScanning = loading || scan === null;
  const maintenanceBusy = repairingLibrary || importingArchives || Boolean(repairingManifest);
  const hasRepairableArchives = (scan?.entries ?? []).some((entry) => (
    entry.metadataStatus !== "current" || !entry.sourcePath || entry.sourceAvailable === false
  ));
  const entries = (scan?.entries ?? [])
    .filter((entry) => !normalizedQuery || entrySearchText(entry).includes(normalizedQuery))
    .filter((entry) => !mapFilter || entry.map === mapFilter)
    .filter((entry) => !platformFilter || entry.demoSource?.name === platformFilter)
    .sort((left, right) => {
      const leftDate = left.sourceModifiedAtMs ?? left.modifiedAtMs;
      const rightDate = right.sourceModifiedAtMs ?? right.modifiedAtMs;
      if (sort === "map") return left.map.localeCompare(right.map) || rightDate - leftDate;
      if (sort === "platform") return (left.demoSource?.name ?? "").localeCompare(right.demoSource?.name ?? "") || rightDate - leftDate;
      return rightDate - leftDate;
    });

  return (
    <section className="library-workspace" aria-labelledby="library-title">
      <header className="library-heading">
        <div>
          <span className="library-eyebrow"><LibraryIcon size={15} />{words.libraryFolder}</span>
          <h1 id="library-title">{words.libraryTitle}</h1>
          <p>{words.librarySubtitle}</p>
        </div>
        <div className="library-primary-actions">
          <button className="primary-button" type="button" onClick={onConvert} disabled={maintenanceBusy || taskBusy}><PlusIcon size={16} />{words.convertDemo}</button>
        </div>
      </header>

      {!exportRoot ? (
        <div className="library-first-run">
          <div className="library-empty-mark"><TraceMark size={58} /></div>
          <div>
            <span>{words.libraryFolder}</span>
            <h2>{words.libraryEmptyTitle}</h2>
            <p>{words.libraryEmptyBody}</p>
          </div>
          <button className="primary-button" type="button" onClick={onChooseExportRoot}><FolderIcon size={17} />{words.chooseLibrary}</button>
          <small>{words.libraryDefaultLocation}</small>
        </div>
      ) : (
        <>
          <div className="library-command-bar">
            <details className="library-roots-menu" ref={rootsMenuRef}>
              <summary className="library-root-button" title={exportRoot}>
                <FolderIcon size={16} />
                <span><small>{words.exportFolder}</small><code>{exportRoot}</code></span>
                <b>{words.libraryFolderCount.replace("{count}", String(roots.length))}</b>
              </summary>
              <div className="library-roots-popover">
                <header>
                  <strong>{words.indexedFolders}</strong>
                  <button className="quiet-button" type="button" onClick={() => { closeRootsMenu(); onAddRoot(); }} disabled={maintenanceBusy}><PlusIcon size={14} />{words.addFolder}</button>
                </header>
                <ul>
                  {roots.map((root) => {
                    const isExport = root.toLocaleLowerCase() === exportRoot.toLocaleLowerCase();
                    return (
                      <li key={root}>
                        <span><code title={root}>{root}</code>{isExport ? <small>{words.defaultExport}</small> : null}</span>
                        {!isExport ? (
                          <button className="icon-button" type="button" onClick={() => onRemoveRoot(root)} disabled={maintenanceBusy} aria-label={`${words.removeFolder}: ${root}`} title={words.removeFolder}>
                            <CloseIcon size={14} />
                          </button>
                        ) : null}
                      </li>
                    );
                  })}
                </ul>
                <button className="secondary-button" type="button" onClick={() => { closeRootsMenu(); onChooseExportRoot(); }} disabled={maintenanceBusy}>
                  <FolderIcon size={14} />{words.changeExportFolder}
                </button>
                <section
                  className="library-maintenance"
                  aria-label={words.libraryMaintenance}
                >
                  <small>{words.libraryMaintenance}</small>
                  <button
                    type="button"
                    onClick={() => { closeRootsMenu(); onImportArchives(); }}
                    disabled={maintenanceBusy}
                    title={words.importArchivesHelp}
                  >
                    <FolderIcon size={15} />
                    <span>
                      <strong>{importingArchives ? words.importingArchives : words.importLegacyArchives}</strong>
                      <em>{words.importArchivesHelp}</em>
                    </span>
                  </button>
                  {hasRepairableArchives ? (
                    <button
                      type="button"
                      onClick={() => { closeRootsMenu(); onRepairLibrary(); }}
                      disabled={maintenanceBusy}
                      title={words.repairMetadataHelp}
                    >
                      <RefreshIcon size={15} />
                      <span>
                        <strong>{repairingLibrary ? words.repairingLibrary : words.repairLegacyLibrary}</strong>
                        <em>{words.repairLibraryHelp}</em>
                      </span>
                    </button>
                  ) : null}
                </section>
              </div>
            </details>

            <label className="library-search">
              <SearchIcon size={17} />
              <span className="sr-only">{words.librarySearch}</span>
              <input value={query} onChange={(event) => onQueryChange(event.target.value)} placeholder={words.librarySearch} />
            </label>

            <select value={mapFilter} onChange={(event) => onMapFilterChange(event.target.value)} aria-label={words.map}>
              <option value="">{words.allMaps}</option>
              {maps.map((map) => <option value={map} key={map}>{displayMap(map)}</option>)}
            </select>

            <select value={platformFilter} onChange={(event) => onPlatformFilterChange(event.target.value)} aria-label={words.demoSource}>
              <option value="">{words.allPlatforms}</option>
              {platforms.map((platform) => <option value={platform} key={platform}>{platformName(platform)}</option>)}
            </select>

            <select value={sort} onChange={(event) => onSortChange(event.target.value as LibrarySort)} aria-label={words.recentFirst}>
              <option value="recent">{words.recentFirst}</option>
              <option value="map">{words.mapOrder}</option>
              <option value="platform">{words.platformOrder}</option>
            </select>

            <button className="icon-button library-refresh" type="button" disabled={loading || maintenanceBusy} onClick={onRefresh} aria-label={words.scanLibrary} title={words.scanLibrary}>
              <RefreshIcon size={17} />
            </button>
          </div>

          <div className="library-result-meta">
            <strong>{importingArchives
              ? words.importingArchives
              : repairingLibrary
              ? words.repairingLibrary
              : isScanning ? words.scanningLibrary : words.libraryCount.replace("{count}", String(entries.length))}</strong>
            <span>{words.indexedFolderSummary.replace("{count}", String(roots.length))}</span>
            {notice ? <em className="library-notice">{notice}</em>
              : scan && scan.skipped.length > 0 ? <em>{words.libraryScanNotes.replace("{count}", String(scan.skipped.length))}</em> : null}
          </div>

          {isScanning ? <LibrarySkeleton /> : entries.length > 0 ? (
            <div className="library-list">
              {entries.map((entry) => (
                <LibraryRow
                  key={entry.manifestPath}
                  entry={entry}
                  words={words}
                  language={language}
                  onOpen={() => onOpenEntry(entry)}
                  onRepair={() => onRepairEntry(entry)}
                  onRevealManifest={() => onRevealManifest(entry)}
                  onRevealDemo={() => onRevealDemo(entry)}
                  onReparse={() => onReparseEntry(entry)}
                  repairing={repairingManifest === entry.manifestPath}
                  disabled={maintenanceBusy || archiveOpenDisabled}
                  taskBusy={taskBusy}
                />
              ))}
            </div>
          ) : (
            <div className="library-no-results">
              <SearchIcon size={24} />
              <strong>{scan?.entries.length === 0 ? words.libraryDirectoryEmptyTitle : words.libraryNoResultsTitle}</strong>
              <p>{scan?.entries.length === 0 ? words.libraryDirectoryEmptyBody : words.libraryNoResultsBody}</p>
              {scan?.entries.length === 0 ? <button className="primary-button" type="button" onClick={onConvert}><PlusIcon size={15} />{words.convertDemo}</button> : null}
            </div>
          )}
        </>
      )}
    </section>
  );
}
