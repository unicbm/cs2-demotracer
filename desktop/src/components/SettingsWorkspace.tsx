import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  AlertIcon,
  CheckIcon,
  ChevronIcon,
  FolderIcon,
  LibraryIcon,
  RefreshIcon,
  ReplayIcon,
  SearchIcon,
  SlidersIcon,
} from "../icons";
import type { TextDictionary } from "../i18n";
import type {
  Cs2InstallCandidate,
  ConverterSettings,
  EnvironmentCheckStatus,
  EnvironmentDiagnosticReport,
  EnvironmentOverallStatus,
  EnvironmentPluginClassification,
  Language,
  LocalEnvironmentSettings,
  RuntimeVerificationStatus,
  ServerConfigDocument,
  ServerConfigValidation,
} from "../types";
import { SERVER_CONFIG_GUIDE, type ServerConfigGuideGroup } from "../serverConfigGuide";
import type {
  PlaybackHandoffMode,
  PlaybackMatchOverride,
  PlaybackPresetOptions,
  PlaybackToggleOverride,
} from "./PlaybackCommandBuilder";
import "./settings-workspace.css";

type SettingsSection = "environment" | "paths" | "export" | "playback" | "serverConfig";

interface SettingsWorkspaceProps {
  words: TextDictionary;
  language: Language;
  environment: LocalEnvironmentSettings;
  exportRoot: string;
  archiveRoots: string[];
  converter: ConverterSettings;
  cosmeticConsentAccepted: boolean;
  playback: PlaybackPresetOptions;
  candidates: Cs2InstallCandidate[];
  report: EnvironmentDiagnosticReport | null;
  serverConfigDocument: ServerConfigDocument | null;
  serverConfigDraft: string;
  serverConfigValidation: ServerConfigValidation | null;
  loadingServerConfig: boolean;
  savingServerConfig: boolean;
  detecting: boolean;
  detectionCompleted: boolean;
  inspecting: boolean;
  onCs2PathChange: (path: string) => void;
  onBrowseCs2: () => void;
  onDetectCs2: () => void;
  onUseCandidate: (candidate: Cs2InstallCandidate) => void;
  onInspectEnvironment: () => void;
  onLoadServerConfig: () => void;
  onServerConfigDraftChange: (json: string) => void;
  onValidateServerConfig: () => void;
  onSaveServerConfig: () => void;
  onChooseExportRoot: () => void;
  onAddArchiveRoot: () => void;
  onRemoveArchiveRoot: (root: string) => void;
  onAddDemoRoot: () => void;
  onRemoveDemoRoot: (root: string) => void;
  onOpenPath: (path: string) => void;
  onEnvironmentChange: (patch: Partial<LocalEnvironmentSettings>) => void;
  onConverterChange: (patch: Partial<ConverterSettings>) => void;
  onRequestCosmetics: () => void;
  onPlaybackChange: (patch: Partial<PlaybackPresetOptions>) => void;
}

function SwitchControl({
  checked,
  disabled = false,
  label,
  onChange,
}: {
  checked: boolean;
  disabled?: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}) {
  return (
    <button
      className="switch-control"
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

function StatusMark({ status }: { status: EnvironmentCheckStatus }) {
  if (status === "pass") return <CheckIcon size={14} />;
  if (status === "warning" || status === "error") return <AlertIcon size={14} />;
  return <span aria-hidden="true">—</span>;
}

function statusLabel(words: TextDictionary, status: EnvironmentCheckStatus): string {
  if (status === "pass") return words.diagnosticStatusPass;
  if (status === "warning") return words.diagnosticStatusWarning;
  if (status === "error") return words.diagnosticStatusError;
  if (status === "notApplicable") return words.diagnosticStatusNotApplicable;
  return words.diagnosticStatusUnverified;
}

function overallCopy(words: TextDictionary, status: EnvironmentOverallStatus) {
  if (status === "pass") return [words.environmentReadyTitle, words.environmentReadyBody] as const;
  if (status === "warning") return [words.environmentWarningTitle, words.environmentWarningBody] as const;
  if (status === "error") return [words.environmentErrorTitle, words.environmentErrorBody] as const;
  return [words.environmentUnverifiedTitle, words.environmentUnverifiedBody] as const;
}

function runtimeVerificationLabel(words: TextDictionary, status: RuntimeVerificationStatus): string {
  if (status === "verified") return words.runtimeVerified;
  if (status === "notRunning") return words.runtimeNotRunning;
  if (status === "unavailable") return words.runtimeUnavailable;
  return words.runtimeNotVerified;
}

function pluginClassification(words: TextDictionary, classification: EnvironmentPluginClassification): string {
  if (classification === "demotracer") return words.pluginClassDemoTracer;
  if (classification === "dependency") return words.pluginClassDependency;
  if (classification === "potentialConflict") return words.pluginClassPotentialConflict;
  return words.pluginClassUnknown;
}

function pluginRuntimeState(words: TextDictionary, state: "loaded" | "notLoaded" | "unknown"): string {
  if (state === "loaded") return words.runtimePluginLoaded;
  if (state === "notLoaded") return words.runtimePluginNotLoaded;
  return words.runtimePluginUnknown;
}

function diagnosticGroupLabel(words: TextDictionary, group: string): string {
  if (group === "cs2") return "CS2";
  if (group === "dependencies") return words.diagnosticGroupDependencies;
  if (group === "demotracer") return "DemoTracer";
  if (group === "plugins") return words.diagnosticGroupPlugins;
  if (group === "compatibility") return words.diagnosticGroupCompatibility;
  if (group === "runtime") return words.diagnosticGroupRuntime;
  return group;
}

function confidenceLabel(words: TextDictionary, confidence: string): string {
  if (confidence === "high" || confidence === "certain") return words.confidenceHigh;
  if (confidence === "medium") return words.confidenceMedium;
  if (confidence === "low") return words.confidenceLow;
  return confidence;
}

function SettingLine({
  title,
  description,
  checked,
  disabled,
  onChange,
}: {
  title: string;
  description: string;
  checked: boolean;
  disabled?: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <div className={`settings-toggle-line${disabled ? " is-disabled" : ""}`}>
      <div>
        <strong>{title}</strong>
        <small>{description}</small>
      </div>
      <SwitchControl checked={checked} disabled={disabled} label={title} onChange={onChange} />
    </div>
  );
}

function SettingSelectLine({
  title,
  description,
  value,
  children,
  onChange,
}: {
  title: string;
  description: string;
  value: string;
  children: ReactNode;
  onChange: (value: string) => void;
}) {
  return (
    <label className="settings-select-line">
      <span><strong>{title}</strong><small>{description}</small></span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>{children}</select>
    </label>
  );
}

function PathRow({
  path,
  badge,
  removeLabel,
  openLabel,
  removable,
  onOpen,
  onRemove,
}: {
  path: string;
  badge?: string;
  removeLabel: string;
  openLabel: string;
  removable: boolean;
  onOpen: () => void;
  onRemove: () => void;
}) {
  return (
    <div className="settings-path-row">
      <FolderIcon size={16} />
      <code title={path}>{path}</code>
      {badge ? <span>{badge}</span> : null}
      <button className="text-button" type="button" onClick={onOpen}>{openLabel}</button>
      {removable ? (
        <button className="text-button" type="button" onClick={onRemove}>{removeLabel}</button>
      ) : null}
    </div>
  );
}

export function SettingsWorkspace({
  words,
  language,
  environment,
  exportRoot,
  archiveRoots,
  converter,
  cosmeticConsentAccepted,
  playback,
  candidates,
  report,
  serverConfigDocument,
  serverConfigDraft,
  serverConfigValidation,
  loadingServerConfig,
  savingServerConfig,
  detecting,
  detectionCompleted,
  inspecting,
  onCs2PathChange,
  onBrowseCs2,
  onDetectCs2,
  onUseCandidate,
  onInspectEnvironment,
  onLoadServerConfig,
  onServerConfigDraftChange,
  onValidateServerConfig,
  onSaveServerConfig,
  onChooseExportRoot,
  onAddArchiveRoot,
  onRemoveArchiveRoot,
  onAddDemoRoot,
  onRemoveDemoRoot,
  onOpenPath,
  onEnvironmentChange,
  onConverterChange,
  onRequestCosmetics,
  onPlaybackChange,
}: SettingsWorkspaceProps) {
  const [section, setSection] = useState<SettingsSection>("environment");
  const [serverGuideQuery, setServerGuideQuery] = useState("");
  const autoLoadedConfigPath = useRef("");
  const reportCopy = report ? overallCopy(words, report.overall) : null;
  const formattedCheckTime = useMemo(() => {
    if (!report) return "";
    return new Intl.DateTimeFormat(language === "zh" ? "zh-CN" : "en-US", {
      dateStyle: "medium",
      timeStyle: "medium",
    }).format(new Date(report.checkedAtMs));
  }, [language, report]);
  const defaultRootKey = exportRoot.replace(/\\/g, "/").toLocaleLowerCase();
  const normalizedGuideQuery = serverGuideQuery.trim().toLocaleLowerCase();
  const serverGuideGroups = useMemo(() => {
    const groups = new Map<ServerConfigGuideGroup, Array<(typeof SERVER_CONFIG_GUIDE)[number]>>();
    for (const field of SERVER_CONFIG_GUIDE) {
      const searchText = `${field.path} ${field.description[language]} ${field.accepted?.join(" ") ?? ""}`.toLocaleLowerCase();
      if (normalizedGuideQuery && !searchText.includes(normalizedGuideQuery)) continue;
      const fields = groups.get(field.group) ?? [];
      fields.push(field);
      groups.set(field.group, fields);
    }
    return groups;
  }, [language, normalizedGuideQuery]);

  useEffect(() => {
    const path = environment.cs2Path.trim();
    if (section !== "serverConfig" || !path || serverConfigDocument || loadingServerConfig) return;
    if (autoLoadedConfigPath.current === path) return;
    autoLoadedConfigPath.current = path;
    onLoadServerConfig();
  }, [environment.cs2Path, loadingServerConfig, onLoadServerConfig, section, serverConfigDocument]);

  const serverGuideGroupLabel = (group: ServerConfigGuideGroup): string => {
    if (group === "general") return words.serverConfigGroupGeneral;
    if (group === "handoff") return words.serverConfigGroupHandoff;
    if (group === "fidelity") return words.serverConfigGroupFidelity;
    if (group === "match") return words.serverConfigGroupMatch;
    return words.serverConfigGroupCosmetics;
  };

  const environmentView = (
    <div className="settings-pane settings-environment-pane">
      <header className="settings-pane-header">
        <div>
          <span className="settings-eyebrow">{words.settingsNavEnvironment}</span>
          <h2>{words.environmentTitle}</h2>
          <p>{words.environmentSubtitle}</p>
        </div>
        <div className="settings-header-actions">
          <button className="secondary-button" type="button" disabled={detecting || inspecting} onClick={onDetectCs2}>
            <SearchIcon size={16} />{detecting ? words.detectingCs2 : words.autoDetectCs2}
          </button>
          <button className="primary-button" type="button" disabled={!environment.cs2Path.trim() || detecting || inspecting} onClick={onInspectEnvironment}>
            <RefreshIcon size={16} />{inspecting ? words.inspectingEnvironment : words.inspectEnvironment}
          </button>
        </div>
      </header>

      <section className="settings-card cs2-location-card" aria-labelledby="cs2-location-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="cs2-location-title">{words.cs2Location}</h3>
            <p>{words.cs2LocationHelp}</p>
          </div>
          <span className="local-read-badge">{words.readOnlyInspection}</span>
        </div>
        <div className="settings-path-input">
          <input
            value={environment.cs2Path}
            disabled={detecting || inspecting}
            spellCheck={false}
            placeholder={words.cs2PathPlaceholder}
            aria-label={words.cs2Location}
            onChange={(event) => onCs2PathChange(event.target.value)}
          />
          <button className="secondary-button" type="button" disabled={detecting || inspecting} onClick={onBrowseCs2}>
            <FolderIcon size={15} />{words.browseFolder}
          </button>
        </div>
        <p className="settings-inline-help">{words.manualCs2PathHelp}</p>

        {candidates.length > 0 ? (
          <div className="detected-install-list">
            <div className="detected-install-heading">
              <strong>{words.detectedCs2Installs}</strong>
              <small>{words.detectedCs2InstallsHelp}</small>
            </div>
            {candidates.map((candidate) => (
              <button
                className="detected-install-option"
                key={`${candidate.source}:${candidate.gameCsgoPath}`}
                type="button"
                disabled={detecting || inspecting}
                onClick={() => onUseCandidate(candidate)}
              >
                <span><FolderIcon size={16} /></span>
                <span>
                  <strong>{candidate.label}</strong>
                  <code>{candidate.path}</code>
                </span>
                <small>{candidate.source}</small>
                <b>{words.useDetectedInstall}</b>
              </button>
            ))}
          </div>
        ) : detectionCompleted && !detecting ? (
          <div className="detected-install-empty">
            <strong>{words.noDetectedCs2Title}</strong>
            <small>{words.noDetectedCs2Help}</small>
          </div>
        ) : null}
      </section>

      <aside className="vendor-warning" aria-labelledby="vendor-warning-title">
        <span><AlertIcon size={18} /></span>
        <div>
          <strong id="vendor-warning-title">{words.vendorDifferenceTitle}</strong>
          <p>{words.vendorDifferenceBody}</p>
        </div>
      </aside>

      {!report ? (
        <section className="diagnostic-empty settings-card">
          <span><SearchIcon size={22} /></span>
          <div>
            <h3>{words.diagnosticNotRunTitle}</h3>
            <p>{words.diagnosticNotRunBody}</p>
          </div>
        </section>
      ) : (
        <>
          <section className={`diagnostic-overview is-${report.overall}`}>
            <span className="diagnostic-overview-mark"><StatusMark status={report.overall} /></span>
            <div>
              <h3>{reportCopy?.[0]}</h3>
              <p>{reportCopy?.[1]}</p>
              <small className="diagnostic-checked-at">
                {words.diagnosticCheckedAt.replace("{time}", formattedCheckTime)}
                {report.cached ? <b>{words.cachedDiagnosticBadge}</b> : null}
              </small>
              {report.cached ? <small className="cached-diagnostic-help">{words.cachedDiagnosticHelp}</small> : null}
            </div>
            <div className="diagnostic-mode">
              <span>{words.fileCompatibility}</span>
              <strong>{statusLabel(words, report.overall)}</strong>
              <span>{words.runtimeState}</span>
              <strong className={report.runtimeVerification === "verified" ? "is-verified" : ""}>
                {runtimeVerificationLabel(words, report.runtimeVerification)}
              </strong>
            </div>
          </section>

          <details className="diagnostic-detail-bundle">
            <summary>
              <span>
                <strong>{words.environmentDetails}</strong>
                <small>{words.environmentDetailsHelp}</small>
              </span>
              <b>{words.environmentDetailCount
                .replace("{checks}", String(report.checks.length))
                .replace("{plugins}", String(report.plugins.length))}</b>
              <ChevronIcon size={15} />
            </summary>
            <div className="diagnostic-detail-content">
          <section className="settings-card install-receipt" aria-labelledby="install-receipt-title">
            <div className="settings-card-heading">
              <div>
                <h3 id="install-receipt-title">{words.installReceiptTitle}</h3>
                <p>{words.installReceiptHelp}</p>
              </div>
              <span className={`count-badge${report.receipt.found && report.receipt.verified ? "" : " is-warning"}`}>
                {!report.receipt.found
                  ? words.installReceiptMissing
                  : report.receipt.verified
                    ? words.installReceiptVerified
                    : words.installReceiptUnverified}
              </span>
            </div>
            <div className="receipt-contract-grid">
              <div>
                <span>{words.bundleVersionLabel}</span>
                <strong>{report.receipt.bundleVersion ?? "—"}</strong>
              </div>
              <div>
                <span>{words.nativeContractLabel}</span>
                <strong>{report.receipt.botControllerAbi == null
                  ? "—"
                  : `ABI ${report.receipt.botControllerAbi}.${report.receipt.botControllerMinor ?? "?"}`}</strong>
              </div>
              <div>
                <span>{words.apiContractLabel}</span>
                <strong>{report.receipt.botHiderApi == null && report.receipt.demoTracerApi == null
                  ? "—"
                  : `BotHider ${report.receipt.botHiderApi ?? "?"} · DemoTracer ${report.receipt.demoTracerApi ?? "?"}`}</strong>
              </div>
              <div>
                <span>{words.receiptFilesLabel}</span>
                <strong>{words.receiptFilesValue
                  .replace("{checked}", String(report.receipt.filesChecked))
                  .replace("{mismatched}", String(report.receipt.filesMismatched))}</strong>
              </div>
            </div>
            {report.receipt.path ? <code className="receipt-path">{report.receipt.path}</code> : null}
          </section>

          {report.conflicts.length > 0 ? (
            <section className="settings-card diagnostic-conflicts" aria-labelledby="diagnostic-conflicts-title">
              <div className="settings-card-heading">
                <div>
                  <h3 id="diagnostic-conflicts-title">{words.conflictsTitle}</h3>
                  <p>{words.conflictsHelp}</p>
                </div>
                <span className="count-badge is-warning">{report.conflicts.length}</span>
              </div>
              <div className="conflict-list">
                {report.conflicts.map((conflict) => (
                  <article className={`conflict-item is-${conflict.severity}`} key={`${conflict.ruleId}:${conflict.evidencePath}`}>
                    <span><AlertIcon size={16} /></span>
                    <div>
                      <div><strong>{conflict.title}</strong><small>{confidenceLabel(words, conflict.confidence)}</small></div>
                      <p>{conflict.summary}</p>
                      {conflict.evidencePath ? <code>{conflict.evidencePath}</code> : null}
                      {conflict.affectedFeatures.length > 0 ? (
                        <footer>{conflict.affectedFeatures.map((feature) => <span key={feature}>{feature}</span>)}</footer>
                      ) : null}
                    </div>
                  </article>
                ))}
              </div>
            </section>
          ) : null}

          <section className="settings-card diagnostic-checks" aria-labelledby="diagnostic-checks-title">
            <div className="settings-card-heading">
              <div>
                <h3 id="diagnostic-checks-title">{words.diagnosticChecks}</h3>
                <p>{words.diagnosticChecksHelp}</p>
              </div>
              <span className="count-badge">{report.checks.length}</span>
            </div>
            <div className="diagnostic-check-list">
              {report.checks.map((check) => (
                <details className={`diagnostic-check is-${check.status}`} key={check.id}>
                  <summary>
                    <span className="diagnostic-check-mark"><StatusMark status={check.status} /></span>
                    <span>
                      <strong>{check.title}</strong>
                      <small>{check.summary}</small>
                    </span>
                    <b>{diagnosticGroupLabel(words, check.group)}</b>
                    <em>{statusLabel(words, check.status)}</em>
                  </summary>
                  {(check.expected || check.actual || check.evidencePath || check.action) ? (
                    <div className="diagnostic-check-detail">
                      {check.expected ? <div><span>{words.expectedValue}</span><code>{check.expected}</code></div> : null}
                      {check.actual ? <div><span>{words.actualValue}</span><code>{check.actual}</code></div> : null}
                      {check.evidencePath ? <div><span>{words.evidencePath}</span><code>{check.evidencePath}</code></div> : null}
                      {check.action ? <p><strong>{words.suggestedAction}</strong>{check.action}</p> : null}
                    </div>
                  ) : null}
                </details>
              ))}
            </div>
          </section>

          <section className="settings-card plugin-inventory" aria-labelledby="plugin-inventory-title">
            <div className="settings-card-heading">
              <div>
                <h3 id="plugin-inventory-title">{words.pluginInventory}</h3>
                <p>{words.pluginInventoryHelp}</p>
              </div>
              <span className="count-badge">{report.plugins.length}</span>
            </div>
            {report.plugins.length > 0 ? (
              <div className="plugin-list">
                {report.plugins.map((plugin) => (
                  <div className={`plugin-row is-${plugin.classification}`} key={`${plugin.directory}:${plugin.name}`}>
                    <span><LibraryIcon size={15} /></span>
                    <div>
                      <strong>{plugin.name}</strong>
                      <code>{plugin.directory}</code>
                    </div>
                    <small title={plugin.assemblyFiles.join("\n")}>
                      {words.assemblyCount.replace("{count}", String(plugin.assemblyFiles.length))} · {pluginRuntimeState(words, plugin.runtimeState)}
                    </small>
                    <b>{pluginClassification(words, plugin.classification)}</b>
                  </div>
                ))}
              </div>
            ) : <p className="settings-empty-list">{words.noCssPluginsFound}</p>}
          </section>
            </div>
          </details>
        </>
      )}
    </div>
  );

  const pathsView = (
    <div className="settings-pane">
      <header className="settings-pane-header">
        <div>
          <span className="settings-eyebrow">{words.settingsNavPaths}</span>
          <h2>{words.pathsSettingsTitle}</h2>
          <p>{words.pathsSettingsSubtitle}</p>
        </div>
      </header>

      <section className="settings-card" aria-labelledby="default-output-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="default-output-title">{words.defaultOutputDirectory}</h3>
            <p>{words.defaultOutputDirectoryHelp}</p>
          </div>
          <button className="secondary-button" type="button" onClick={onChooseExportRoot}>
            <FolderIcon size={15} />{words.changeFolder}
          </button>
        </div>
        <div className="primary-path-readout">
          <code>{exportRoot || words.notSelected}</code>
          {exportRoot ? <button className="text-button" type="button" onClick={() => onOpenPath(exportRoot)}>{words.openFolder}</button> : null}
        </div>
      </section>

      <section className="settings-card" aria-labelledby="archive-roots-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="archive-roots-title">{words.archiveLibraryDirectories}</h3>
            <p>{words.archiveLibraryDirectoriesHelp}</p>
          </div>
          <button className="secondary-button" type="button" onClick={onAddArchiveRoot}>
            <FolderIcon size={15} />{words.addFolder}
          </button>
        </div>
        <div className="settings-path-list">
          {archiveRoots.map((root) => {
            const isDefault = root.replace(/\\/g, "/").toLocaleLowerCase() === defaultRootKey;
            return (
              <PathRow
                key={root}
                path={root}
                 badge={isDefault ? words.defaultExport : undefined}
                 removeLabel={words.removeFolder}
                 openLabel={words.openFolder}
                 removable={!isDefault}
                 onOpen={() => onOpenPath(root)}
                 onRemove={() => onRemoveArchiveRoot(root)}
              />
            );
          })}
        </div>
      </section>

      <section className="settings-card" aria-labelledby="demo-roots-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="demo-roots-title">{words.rawDemoDirectories}</h3>
            <p>{words.rawDemoDirectoriesHelp}</p>
          </div>
          <button className="secondary-button" type="button" onClick={onAddDemoRoot}>
            <FolderIcon size={15} />{words.addDemoDirectory}
          </button>
        </div>
        {environment.demoRoots.length > 0 ? (
          <div className="settings-path-list">
            {environment.demoRoots.map((root) => (
              <PathRow key={root} path={root} removeLabel={words.removeFolder} openLabel={words.openFolder} removable onOpen={() => onOpenPath(root)} onRemove={() => onRemoveDemoRoot(root)} />
            ))}
          </div>
        ) : <p className="settings-empty-list">{words.noDemoDirectories}</p>}
      </section>

      <section className="settings-card settings-form-card" aria-labelledby="feedback-settings-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="feedback-settings-title">{words.taskFeedbackTitle}</h3>
            <p>{words.taskFeedbackHelp}</p>
          </div>
        </div>
        <SettingLine
          title={words.soundNotifications}
          description={words.soundNotificationsHelp}
          checked={environment.soundNotifications}
          onChange={(soundNotifications) => onEnvironmentChange({ soundNotifications })}
        />
      </section>
    </div>
  );

  const exportView = (
    <div className="settings-pane">
      <header className="settings-pane-header">
        <div>
          <span className="settings-eyebrow">{words.settingsNavExport}</span>
          <h2>{words.exportDefaultsTitle}</h2>
          <p>{words.exportDefaultsSubtitle}</p>
        </div>
        <span className="autosave-note"><CheckIcon size={14} />{words.settingsSavedAutomatically}</span>
      </header>

      <section className="settings-card settings-form-card">
        <div className="settings-choice-row">
          <div><strong>{words.side}</strong><small>{words.defaultSideHelp}</small></div>
          <div className="segmented-control" role="group" aria-label={words.side}>
            {(["both", "t", "ct"] as const).map((side) => (
              <button key={side} className={converter.side === side ? "is-selected" : ""} type="button" aria-pressed={converter.side === side} onClick={() => onConverterChange({ side })}>
                {side === "both" ? words.both : side === "t" ? words.t : words.ct}
              </button>
            ))}
          </div>
        </div>

        <div className="settings-choice-row">
          <div><strong>{words.playbackRange}</strong><small>{words.defaultPlaybackRangeHelp}</small></div>
          <div className="segmented-control" role="group" aria-label={words.playbackRange}>
            <button className={!converter.fullRound ? "is-selected" : ""} type="button" aria-pressed={!converter.fullRound} onClick={() => onConverterChange({ fullRound: false })}>{words.cutBeforePlant}</button>
            <button className={converter.fullRound ? "is-selected" : ""} type="button" aria-pressed={converter.fullRound} onClick={() => onConverterChange({ fullRound: true })}>{words.fullRoundLabel}</button>
          </div>
        </div>

        <SettingLine title={words.exportVoice} description={words.voiceHelp} checked={converter.exportVoice} onChange={(exportVoice) => onConverterChange({ exportVoice })} />

        <SettingLine
          title={words.exportCosmetics}
          description={cosmeticConsentAccepted ? words.cosmeticDefaultAcceptedHelp : words.cosmeticDefaultHelp}
          checked={converter.exportCosmetics}
          onChange={(exportCosmetics) => {
            if (exportCosmetics) onRequestCosmetics();
            else onConverterChange({ exportCosmetics: false });
          }}
        />

        {converter.exportCosmetics ? (
          <div className="settings-dependent-options">
            <label><input type="checkbox" checked={converter.exportStickers} onChange={(event) => onConverterChange({ exportStickers: event.target.checked })} />{words.exportStickers}</label>
            <label><input type="checkbox" checked={converter.exportCharms} onChange={(event) => onConverterChange({ exportCharms: event.target.checked })} />{words.exportCharms}</label>
          </div>
        ) : null}

        <div className="settings-number-row">
          <div><strong>{words.freezePreroll}</strong><small>{words.freezePrerollDefaultHelp}</small></div>
          <label>
            <input
              type="number"
              min={0}
              max={120}
              step={1}
              value={converter.freezePrerollSeconds}
              onChange={(event) => {
                const value = Number(event.target.value);
                if (Number.isFinite(value) && value >= 0 && value <= 120) {
                  onConverterChange({ freezePrerollSeconds: value });
                }
              }}
            />
            <span>{words.seconds}</span>
          </label>
        </div>

        <div className="settings-choice-row">
          <div><strong>{words.subtickCapture}</strong><small>{words.subtickCaptureHelp}</small></div>
          <div className="segmented-control" role="group" aria-label={words.subtickCapture}>
            <button className={converter.subtickMode === "auto" ? "is-selected" : ""} type="button" aria-pressed={converter.subtickMode === "auto"} onClick={() => onConverterChange({ subtickMode: "auto" })}>{words.subtickAuto}</button>
            <button className={converter.subtickMode === "off" ? "is-selected" : ""} type="button" aria-pressed={converter.subtickMode === "off"} onClick={() => onConverterChange({ subtickMode: "off" })}>{words.subtickOff}</button>
          </div>
        </div>

        <div className="settings-number-row">
          <div><strong>{words.maxRoundDuration}</strong><small>{words.maxRoundDurationHelp}</small></div>
          <label>
            <input
              type="number"
              min={30}
              max={1800}
              step={10}
              value={converter.maxRoundSeconds}
              onChange={(event) => {
                const value = Number(event.target.value);
                if (Number.isFinite(value) && value >= 30 && value <= 1800) {
                  onConverterChange({ maxRoundSeconds: value });
                }
              }}
            />
            <span>{words.seconds}</span>
          </label>
        </div>
      </section>

      <aside className="safe-defaults-note">
        <span><AlertIcon size={17} /></span>
        <div><strong>{words.sessionOnlySettingsTitle}</strong><p>{words.sessionOnlySettingsBody}</p></div>
        <button className="text-button" type="button" onClick={() => onConverterChange({ side: "both", fullRound: false, freezePrerollSeconds: 10, subtickMode: "auto", maxRoundSeconds: 240, exportVoice: true, exportCosmetics: false, exportStickers: false, exportCharms: false })}>{words.restoreSafeDefaults}</button>
      </aside>
    </div>
  );

  const playbackView = (
    <div className="settings-pane">
      <header className="settings-pane-header">
        <div>
          <span className="settings-eyebrow">{words.settingsNavPlayback}</span>
          <h2>{words.playbackDefaultsTitle}</h2>
          <p>{words.playbackDefaultsSubtitle}</p>
        </div>
        <span className="autosave-note"><CheckIcon size={14} />{words.settingsSavedAutomatically}</span>
      </header>

      <section className="settings-card settings-form-card playback-defaults-card">
        <SettingLine
          title={words.syncWeapons}
          description={words.syncWeaponsHelp}
          checked={playback.weapons || playback.cosmetics}
          onChange={(weapons) => onPlaybackChange(weapons ? { weapons: true } : { weapons: false, cosmetics: false })}
        />
        <SettingLine
          title={words.syncSteamIdentity}
          description={words.syncSteamIdentityHelp}
          checked={playback.steamIdentity || playback.avatar}
          onChange={(steamIdentity) => onPlaybackChange(steamIdentity ? { steamIdentity: true } : { steamIdentity: false, avatar: false })}
        />
        <SettingLine title={words.syncVoice} description={words.syncVoiceHelp} checked={playback.voice} onChange={(voice) => onPlaybackChange({ voice })} />
        <SettingLine
          title={words.syncCosmetics}
          description={words.playbackCosmeticsDefaultHelp}
          checked={playback.cosmetics}
          onChange={(cosmetics) => onPlaybackChange(cosmetics ? { cosmetics: true, weapons: true } : { cosmetics: false })}
        />
        <SettingLine
          title={words.syncAvatar}
          description={words.syncAvatarHelp}
          checked={playback.avatar}
          onChange={(avatar) => onPlaybackChange(avatar ? { avatar: true, steamIdentity: true } : { avatar: false })}
        />
        <SettingLine title={words.playoffBeta} description={words.playoffHelp} checked={playback.playoff} onChange={(playoff) => onPlaybackChange({ playoff })} />
        <details className="playback-settings-advanced">
          <summary>
            <strong>{words.playbackAdvancedOverrides}</strong>
            <ChevronIcon size={15} />
          </summary>
          <div className="playback-settings-advanced-body">
            <SettingSelectLine title={words.projectileAlignment} description={words.projectileAlignmentHelp} value={playback.projectileAlignment} onChange={(value) => onPlaybackChange({ projectileAlignment: value as PlaybackToggleOverride })}>
              <option value="on">{words.enabled}</option><option value="off">{words.disabled}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.crosshairAlignment} description={words.crosshairAlignmentHelp} value={playback.crosshairAlignment} onChange={(value) => onPlaybackChange({ crosshairAlignment: value as PlaybackToggleOverride })}>
              <option value="on">{words.enabled}</option><option value="off">{words.disabled}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.leftHandAlignment} description={words.leftHandAlignmentHelp} value={playback.leftHandAlignment} onChange={(value) => onPlaybackChange({ leftHandAlignment: value as PlaybackToggleOverride })}>
              <option value="on">{words.enabled}</option><option value="off">{words.disabled}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.matchPresentation} description={words.matchPresentationHelp} value={playback.matchPresentation} onChange={(value) => onPlaybackChange({ matchPresentation: value as PlaybackMatchOverride })}>
              <option value="off">{words.disabled}</option><option value="scoreboard">{words.scoreboardSync}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.partialReplay} description={words.partialReplayHelp} value={playback.allowPartial} onChange={(value) => onPlaybackChange({ allowPartial: value as PlaybackToggleOverride })}>
              <option value="on">{words.enabled}</option><option value="off">{words.disabled}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.handoffMode} description={words.handoffModeHelp} value={playback.handoffMode} onChange={(value) => onPlaybackChange({ handoffMode: value as PlaybackHandoffMode })}>
              <option value="death_contact_c4">{words.handoffDeathContactC4}</option>
              <option value="death_or_contact">{words.handoffDeathOrContact}</option>
              <option value="death">{words.handoffDeath}</option>
              <option value="contact">{words.handoffContact}</option>
              <option value="off">{words.disabled}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.handoffScope} description={words.handoffScopeHelp} value={playback.handoffScope} onChange={(value) => onPlaybackChange({ handoffScope: value as "slot" | "all" })}>
              <option value="slot">{words.handoffScopeSlot}</option><option value="all">{words.handoffScopeAll}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.threat360} description={words.threat360Help} value={playback.threat360} onChange={(value) => onPlaybackChange({ threat360: value as PlaybackToggleOverride })}>
              <option value="on">{words.enabled}</option><option value="off">{words.disabled}</option>
            </SettingSelectLine>
            {playback.threat360 === "on" ? (
              <div className="settings-advanced-inline">
                <label>
                  <span><strong>{words.threat360Range}</strong><small>150–800</small></span>
                  <input
                    type="number"
                    min={150}
                    max={800}
                    step={10}
                    value={playback.threat360Range}
                    onChange={(event) => {
                      const value = Number(event.target.value);
                      if (Number.isFinite(value) && value >= 150 && value <= 800) onPlaybackChange({ threat360Range: value });
                    }}
                  />
                </label>
                <label><input type="checkbox" checked={playback.threat360Los} onChange={(event) => onPlaybackChange({ threat360Los: event.target.checked })} />{words.threat360RequireLos}</label>
              </div>
            ) : null}
          </div>
        </details>
      </section>

      <p className="settings-footnote">{words.playbackDefaultsFootnote}</p>
    </div>
  );

  const effectiveServerValidation = serverConfigValidation ?? serverConfigDocument?.validation ?? null;
  const serverConfigView = (
    <div className="settings-pane server-config-pane">
      <header className="settings-pane-header">
        <div>
          <span className="settings-eyebrow">{words.settingsNavServerConfig}</span>
          <h2>{words.serverConfigTitle}</h2>
          <p>{words.serverConfigSubtitle}</p>
        </div>
        <div className="settings-header-actions">
          <button className="secondary-button" type="button" disabled={!environment.cs2Path.trim() || loadingServerConfig || savingServerConfig} onClick={onLoadServerConfig}>
            <RefreshIcon size={16} />{loadingServerConfig ? words.loadingServerConfig : words.loadServerConfig}
          </button>
          <button className="secondary-button" type="button" disabled={!serverConfigDraft.trim() || savingServerConfig} onClick={onValidateServerConfig}>
            <CheckIcon size={16} />{words.validateServerConfig}
          </button>
          <button className="primary-button" type="button" disabled={!serverConfigDocument || !serverConfigDraft.trim() || savingServerConfig || effectiveServerValidation?.valid === false} onClick={onSaveServerConfig}>
            <SlidersIcon size={16} />{savingServerConfig ? words.savingServerConfig : words.saveServerConfig}
          </button>
        </div>
      </header>

      {!environment.cs2Path.trim() ? (
        <section className="settings-card diagnostic-empty">
          <span><FolderIcon size={22} /></span>
          <div><h3>{words.serverConfigNeedsPath}</h3><p>{words.serverConfigNeedsPathHelp}</p></div>
        </section>
      ) : !serverConfigDocument ? (
        <section className="settings-card diagnostic-empty">
          <span><SlidersIcon size={22} /></span>
          <div><h3>{words.serverConfigNotLoaded}</h3><p>{words.serverConfigNotLoadedHelp}</p></div>
        </section>
      ) : (
        <>
          <section className="settings-card server-config-editor-card">
            <div className="settings-card-heading">
              <div>
                <h3>{words.serverConfigEditor}</h3>
                <p>{words.serverConfigEditorHelp}</p>
              </div>
              <span className={`count-badge${serverConfigDocument.exists ? "" : " is-warning"}`}>
                {serverConfigDocument.source === "installed"
                  ? words.serverConfigInstalled
                  : serverConfigDocument.source === "example"
                    ? words.serverConfigExample
                    : words.serverConfigBuiltIn}
              </span>
            </div>
            <code className="server-config-path">{serverConfigDocument.configPath}</code>
            <div className="server-config-workbench">
              <textarea
                className="server-config-editor"
                value={serverConfigDraft}
                spellCheck={false}
                aria-label={words.serverConfigEditor}
                onChange={(event) => onServerConfigDraftChange(event.target.value)}
              />
              <aside className="server-config-guide" aria-label={words.serverConfigFieldReference}>
                <header>
                  <div><strong>{words.serverConfigFieldReference}</strong><small>{words.serverConfigFieldReferenceHelp}</small></div>
                  <label>
                    <SearchIcon size={14} />
                    <input value={serverGuideQuery} onChange={(event) => setServerGuideQuery(event.target.value)} placeholder={words.serverConfigSearchFields} />
                  </label>
                </header>
                <div className="server-config-guide-groups">
                  {[...serverGuideGroups.entries()].map(([group, fields]) => (
                    <details key={group} open={Boolean(normalizedGuideQuery) || group === "general"}>
                      <summary><strong>{serverGuideGroupLabel(group)}</strong><span>{fields.length}</span><ChevronIcon size={13} /></summary>
                      <ul>
                        {fields.map((field) => (
                          <li key={field.path}>
                            <div><code>{field.path}</code><span>{field.type === "boolean" ? words.serverConfigTypeBoolean : field.type === "number" ? words.serverConfigTypeNumber : words.serverConfigTypeEnum}</span></div>
                            <p>{field.description[language]}</p>
                            <small>
                              {field.accepted?.length ? <span>{words.serverConfigAllowed}: <code>{field.accepted.join(" · ")}</code></span> : <span>{words.serverConfigAllowed}: <code>true · false · null</code></span>}
                              {field.defaultValue !== undefined ? <span>{words.serverConfigDefault}: <code>{field.defaultValue}</code></span> : null}
                            </small>
                          </li>
                        ))}
                      </ul>
                    </details>
                  ))}
                  {serverGuideGroups.size === 0 ? <p>{words.serverConfigNoMatchingFields}</p> : null}
                </div>
              </aside>
            </div>
          </section>

          {effectiveServerValidation ? (
            <section className={`settings-card server-config-validation is-${effectiveServerValidation.valid ? "valid" : "invalid"}`}>
              <div className="settings-card-heading">
                <div>
                  <h3>{effectiveServerValidation.valid ? words.serverConfigValid : words.serverConfigInvalid}</h3>
                  <p>{words.serverConfigValidationHelp}</p>
                </div>
                <span className={`count-badge${effectiveServerValidation.valid ? "" : " is-warning"}`}>
                  {effectiveServerValidation.errors.length} / {effectiveServerValidation.warnings.length}
                </span>
              </div>
              {[...effectiveServerValidation.errors, ...effectiveServerValidation.warnings].length > 0 ? (
                <ul className="server-config-issues">
                  {[...effectiveServerValidation.errors, ...effectiveServerValidation.warnings].map((issue) => (
                    <li key={`${issue.code}:${issue.path}:${issue.message}`}>
                      <AlertIcon size={15} /><div><code>{issue.path || "$"}</code><span>{issue.message}</span></div>
                    </li>
                  ))}
                </ul>
              ) : <p className="settings-empty-list">{words.serverConfigNoIssues}</p>}
              {effectiveServerValidation.unknownPaths.length > 0 ? (
                <details className="server-config-unknown">
                  <summary>{words.serverConfigUnknownFields.replace("{count}", String(effectiveServerValidation.unknownPaths.length))}</summary>
                  <p>{words.serverConfigUnknownFieldsHelp}</p>
                  <div>{effectiveServerValidation.unknownPaths.map((path) => <code key={path}>{path}</code>)}</div>
                </details>
              ) : null}
            </section>
          ) : null}

          <aside className="safe-defaults-note server-config-reload-note">
            <span><AlertIcon size={17} /></span>
            <div><strong>{words.serverConfigReloadTitle}</strong><p>{words.serverConfigReloadHelp}</p></div>
            <code>{serverConfigDocument.reloadCommand}</code>
          </aside>
        </>
      )}
    </div>
  );

  return (
    <section className="settings-workspace" aria-labelledby="settings-workspace-title">
      <div className="settings-titlebar">
        <div>
          <h1 id="settings-workspace-title">{words.settingsTitle}</h1>
          <p>{words.settingsSubtitle}</p>
        </div>
      </div>
      <div className="settings-layout">
        <nav className="settings-section-nav" aria-label={words.settingsSections}>
          <button className={section === "environment" ? "is-active" : ""} type="button" aria-current={section === "environment" ? "page" : undefined} onClick={() => setSection("environment")}>
            <SearchIcon size={17} /><span><strong>{words.settingsNavEnvironment}</strong><small>{words.settingsNavEnvironmentHelp}</small></span>
          </button>
          <button className={section === "paths" ? "is-active" : ""} type="button" aria-current={section === "paths" ? "page" : undefined} onClick={() => setSection("paths")}>
            <FolderIcon size={17} /><span><strong>{words.settingsNavPaths}</strong><small>{words.settingsNavPathsHelp}</small></span>
          </button>
          <button className={section === "export" ? "is-active" : ""} type="button" aria-current={section === "export" ? "page" : undefined} onClick={() => setSection("export")}>
            <SlidersIcon size={17} /><span><strong>{words.settingsNavExport}</strong><small>{words.settingsNavExportHelp}</small></span>
          </button>
          <button className={section === "playback" ? "is-active" : ""} type="button" aria-current={section === "playback" ? "page" : undefined} onClick={() => setSection("playback")}>
            <ReplayIcon size={17} /><span><strong>{words.settingsNavPlayback}</strong><small>{words.settingsNavPlaybackHelp}</small></span>
          </button>
          <button className={section === "serverConfig" ? "is-active" : ""} type="button" aria-current={section === "serverConfig" ? "page" : undefined} onClick={() => setSection("serverConfig")}>
            <LibraryIcon size={17} /><span><strong>{words.settingsNavServerConfig}</strong><small>{words.settingsNavServerConfigHelp}</small></span>
          </button>
        </nav>
        <div className="settings-content">
          {section === "environment" ? environmentView : null}
          {section === "paths" ? pathsView : null}
          {section === "export" ? exportView : null}
          {section === "playback" ? playbackView : null}
          {section === "serverConfig" ? serverConfigView : null}
        </div>
      </div>
    </section>
  );
}
