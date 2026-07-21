import type { ReactNode } from "react";
import { CheckIcon, ChevronIcon, CopyIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConversionSummary } from "../types";

export interface PlaybackPresetOptions {
  weapons: boolean;
  cosmetics: boolean;
  steamIdentity: boolean;
  avatar: boolean;
  voice: boolean;
  playoff: boolean;
  projectileAlignment: PlaybackToggleOverride;
  crosshairAlignment: PlaybackToggleOverride;
  leftHandAlignment: PlaybackToggleOverride;
  matchPresentation: PlaybackMatchOverride;
  allowPartial: PlaybackToggleOverride;
  handoffMode: PlaybackHandoffMode;
  handoffScope: "slot" | "all";
  threat360: PlaybackToggleOverride;
  threat360Range: number;
  threat360Los: boolean;
}

export type PlaybackToggleOverride = "inherit" | "on" | "off";
export type PlaybackMatchOverride = "inherit" | "off" | "scoreboard";
export type PlaybackHandoffMode = "inherit" | "off" | "death" | "contact" | "death_or_contact" | "death_contact_c4";

type CommandMode = "sequence" | "round";

interface PlaybackCommandBuilderProps {
  words: TextDictionary;
  result: ConversionSummary;
  options: PlaybackPresetOptions;
  commandMode: CommandMode;
  sequenceDisabled?: boolean;
  copied: boolean;
  onOptionsChange: (patch: Partial<PlaybackPresetOptions>) => void;
  onCommandModeChange: (mode: CommandMode) => void;
  onCopy: (command: string) => void;
}

const PRESET_WEAPONS = 0x01;
const PRESET_COSMETICS = 0x02;
const PRESET_STEAM_IDENTITY = 0x04;
const PRESET_AVATAR = 0x08;
const PRESET_VOICE = 0x10;
const PRESET_PLAYOFF = 0x20;

interface SwitchControlProps {
  checked: boolean;
  disabled?: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}

function SwitchControl({ checked, disabled = false, label, onChange }: SwitchControlProps) {
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

interface PlaybackOptionProps extends SwitchControlProps {
  description: string;
}

function PlaybackOption({ checked, description, disabled, label, onChange }: PlaybackOptionProps) {
  return (
    <div className={`setting-line playback-option${disabled ? " is-disabled" : ""}`}>
      <div>
        <strong>{label}</strong>
        <small>{description}</small>
      </div>
      <SwitchControl checked={checked} disabled={disabled} label={label} onChange={onChange} />
    </div>
  );
}

function PlaybackSelect({
  label,
  description,
  value,
  children,
  onChange,
}: {
  label: string;
  description: string;
  value: string;
  children: ReactNode;
  onChange: (value: string) => void;
}) {
  return (
    <label className="playback-select-option">
      <span><strong>{label}</strong><small>{description}</small></span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>{children}</select>
    </label>
  );
}

function formatPreset(mask: number): string {
  return `0x${mask.toString(16).toUpperCase().padStart(2, "0")}`;
}

function toggleCommand(command: string, value: PlaybackToggleOverride): string | null {
  return value === "inherit" ? null : `${command} ${value}`;
}

export function buildPlaybackCommand(goCommand: string, mask: number, options: PlaybackPresetOptions): string {
  const commands = [
    `dtr_preset ${formatPreset(mask)}`,
    toggleCommand("dtr_align projectiles", options.projectileAlignment),
    toggleCommand("dtr_align crosshair", options.crosshairAlignment),
    toggleCommand("dtr_align left_hand", options.leftHandAlignment),
    options.matchPresentation === "inherit" ? null : `dtr_match ${options.matchPresentation}`,
    options.allowPartial === "inherit" ? null : `dtr_partial ${options.allowPartial === "on" ? 1 : 0}`,
    options.handoffMode === "inherit" ? null : `dtr_handoff ${options.handoffMode} ${options.handoffScope}`,
    options.threat360 === "inherit"
      ? null
      : `dtr_handoff_360 ${options.threat360 === "on" ? 1 : 0} ${options.threat360Range} ${options.threat360Los ? "los" : "nolos"}`,
    goCommand,
  ].filter((value): value is string => Boolean(value));
  return commands.join("; ");
}

function capabilityCopy(
  available: boolean,
  requested: boolean | null | undefined,
  included: boolean,
  availableIncluded: string,
  availableExcluded: string,
  requestedEmpty: string,
  notRequested: string,
  unknown: string,
): string {
  if (available) return included ? availableIncluded : availableExcluded;
  if (requested === true) return requestedEmpty;
  if (requested === false) return notRequested;
  return unknown;
}

export function PlaybackCommandBuilder({
  words,
  result,
  options,
  commandMode,
  sequenceDisabled = false,
  copied,
  onOptionsChange,
  onCommandModeChange,
  onCopy,
}: PlaybackCommandBuilderProps) {
  const cosmeticsAvailable = result.cosmetics.files > 0;
  const voiceAvailable = result.voice.sidecars > 0;
  const effectiveCommandMode: CommandMode = sequenceDisabled ? "round" : commandMode;
  const sequenceMode = effectiveCommandMode === "sequence";

  // Normalize dependencies here as well as in the handlers so stale or
  // manually edited localStorage can never produce an invalid preset.
  const cosmetics = cosmeticsAvailable && options.cosmetics;
  const weapons = options.weapons || cosmetics;
  const avatar = options.avatar;
  const steamIdentity = options.steamIdentity || avatar;
  const voice = voiceAvailable && options.voice;
  const playoff = sequenceMode && options.playoff;

  let mask = 0;
  if (weapons) mask |= PRESET_WEAPONS;
  if (cosmetics) mask |= PRESET_COSMETICS;
  if (steamIdentity) mask |= PRESET_STEAM_IDENTITY;
  if (avatar) mask |= PRESET_AVATAR;
  if (voice) mask |= PRESET_VOICE;
  if (playoff) mask |= PRESET_PLAYOFF;

  const goCommand = effectiveCommandMode === "round"
    ? result.commands.goRound
    : result.commands.goSequence;
  const command = buildPlaybackCommand(goCommand, mask, options);
  const activeOptions = [
    weapons ? words.syncWeaponsShort : null,
    steamIdentity ? words.syncIdentityShort : null,
    voice ? words.syncVoiceShort : null,
    cosmetics ? words.syncCosmeticsShort : null,
    avatar ? words.syncAvatarShort : null,
    playoff ? words.playoffShort : null,
  ].filter((value): value is string => Boolean(value));
  const voiceStatus = capabilityCopy(
    voiceAvailable,
    result.voice.requested,
    voice,
    words.voiceAvailableIncluded,
    words.voiceAvailableExcluded,
    words.voiceRequestedEmpty,
    words.voiceNotRequested,
    words.voiceUnknown,
  );
  const cosmeticStatus = capabilityCopy(
    cosmeticsAvailable,
    result.cosmetics.requested,
    cosmetics,
    words.cosmeticsAvailableIncluded,
    words.cosmeticsAvailableExcluded,
    words.cosmeticsRequestedEmpty,
    words.cosmeticsNotRequested,
    words.cosmeticsUnknown,
  );

  return (
    <section className="playback-command-builder" aria-labelledby="playback-command-title">
      <div className="playback-command-heading">
        <div className="playback-command-heading-copy">
          <span>{words.playDemoCommand}</span>
          <h2 id="playback-command-title">{words.standardPlayback}</h2>
        </div>
        {result.rounds.length > 1 ? (
          <div className="playback-mode-tabs" role="group" aria-label={words.playDemoMode}>
            <button
              className={sequenceMode ? "is-selected" : ""}
              type="button"
              aria-pressed={sequenceMode}
              disabled={sequenceDisabled}
              title={sequenceDisabled ? words.sequenceUnavailable : undefined}
              onClick={() => onCommandModeChange("sequence")}
            >
              {words.sequenceMode}
            </button>
            <button
              className={!sequenceMode ? "is-selected" : ""}
              type="button"
              aria-pressed={!sequenceMode}
              onClick={() => onCommandModeChange("round")}
            >
              {words.roundMode}
            </button>
          </div>
        ) : null}
      </div>

      <div className="playback-command-panel">
        <div className="playback-command-action">
          <div className="playback-command-preset">
            <small>{sequenceMode ? words.sequenceMode : words.roundMode}</small>
            <strong>{activeOptions.length > 0 ? activeOptions.join(" · ") : words.noSyncOptions}</strong>
          </div>
          <button className="primary-button" type="button" onClick={() => onCopy(command)}>
            {copied ? <CheckIcon size={16} /> : <CopyIcon size={16} />}
            {copied ? words.copied : words.copyPlaybackCommand}
          </button>
        </div>

        <div className="playback-command-meta">
          <dl className="playback-capabilities" aria-label={words.archiveCapabilities}>
            <div className={`playback-capability${voiceAvailable ? " is-available" : ""}`}>
              <dt>{words.voiceCapability}</dt>
              <dd>{voiceStatus}</dd>
            </div>
            <div className={`playback-capability${cosmeticsAvailable ? " is-available" : ""}${cosmetics ? " is-risk" : ""}`}>
              <dt>{words.cosmeticsCapability}</dt>
              <dd>{cosmeticStatus}</dd>
            </div>
          </dl>

          <details className="playback-command-source">
            <summary>{words.viewPlaybackCommand}<ChevronIcon size={14} /></summary>
            <code>{command}</code>
          </details>
        </div>
      </div>

      <details className="playback-advanced">
        <summary>
          <span>
            <strong>{words.playbackOptions}</strong>
            <small>{activeOptions.length > 0 ? activeOptions.join(" · ") : words.noSyncOptions}</small>
          </span>
          <ChevronIcon size={15} />
        </summary>
        <div className="playback-settings-body">
          <section className="playback-settings-group" aria-labelledby="playback-sync-title">
            <header>
              <strong id="playback-sync-title">{words.standardPlayback}</strong>
            </header>
            <div className="playback-option-grid" role="group" aria-label={words.playbackOptions}>
              <PlaybackOption
                checked={weapons}
                label={words.syncWeapons}
                description={words.syncWeaponsHelp}
                onChange={(checked) => onOptionsChange(checked
                  ? { weapons: true }
                  : { weapons: false, cosmetics: false })}
              />
              <PlaybackOption
                checked={steamIdentity}
                label={words.syncSteamIdentity}
                description={words.syncSteamIdentityHelp}
                onChange={(checked) => onOptionsChange(checked
                  ? { steamIdentity: true }
                  : { steamIdentity: false, avatar: false })}
              />
              {voiceAvailable ? (
                <PlaybackOption
                  checked={voice}
                  label={words.syncVoice}
                  description={voice ? words.syncVoiceIncludedHelp : words.syncVoiceExcludedHelp}
                  onChange={(checked) => onOptionsChange({ voice: checked })}
                />
              ) : null}
              {cosmeticsAvailable ? (
                <PlaybackOption
                  checked={cosmetics}
                  label={words.syncCosmetics}
                  description={cosmetics ? words.syncCosmeticsIncludedHelp : words.syncCosmeticsExcludedHelp}
                  onChange={(checked) => onOptionsChange(checked
                    ? { cosmetics: true, weapons: true }
                    : { cosmetics: false })}
                />
              ) : null}
              <PlaybackOption
                checked={avatar}
                label={words.syncAvatar}
                description={words.syncAvatarHelp}
                onChange={(checked) => onOptionsChange(checked
                  ? { avatar: true, steamIdentity: true }
                  : { avatar: false })}
              />
              <PlaybackOption
                checked={playoff}
                disabled={!sequenceMode}
                label={words.playoffBeta}
                description={sequenceMode ? words.playoffHelp : words.sequenceOnly}
                onChange={(checked) => onOptionsChange({ playoff: checked })}
              />
            </div>
          </section>

          <section className="playback-settings-group is-overrides" aria-labelledby="playback-overrides-title">
            <header>
              <strong id="playback-overrides-title">{words.playbackAdvancedOverrides}</strong>
              <small>{words.playbackAdvancedOverridesHelp}</small>
            </header>
            <div className="playback-override-grid" role="group" aria-label={words.playbackAdvancedOverrides}>
          <PlaybackSelect
            label={words.projectileAlignment}
            description={words.projectileAlignmentHelp}
            value={options.projectileAlignment}
            onChange={(value) => onOptionsChange({ projectileAlignment: value as PlaybackToggleOverride })}
          >
            <option value="inherit">{words.useServerConfig}</option>
            <option value="on">{words.enabled}</option>
            <option value="off">{words.disabled}</option>
          </PlaybackSelect>
          <PlaybackSelect
            label={words.crosshairAlignment}
            description={words.crosshairAlignmentHelp}
            value={options.crosshairAlignment}
            onChange={(value) => onOptionsChange({ crosshairAlignment: value as PlaybackToggleOverride })}
          >
            <option value="inherit">{words.useServerConfig}</option>
            <option value="on">{words.enabled}</option>
            <option value="off">{words.disabled}</option>
          </PlaybackSelect>
          <PlaybackSelect
            label={words.leftHandAlignment}
            description={words.leftHandAlignmentHelp}
            value={options.leftHandAlignment}
            onChange={(value) => onOptionsChange({ leftHandAlignment: value as PlaybackToggleOverride })}
          >
            <option value="inherit">{words.useServerConfig}</option>
            <option value="on">{words.enabled}</option>
            <option value="off">{words.disabled}</option>
          </PlaybackSelect>
          <PlaybackSelect
            label={words.matchPresentation}
            description={words.matchPresentationHelp}
            value={options.matchPresentation}
            onChange={(value) => onOptionsChange({ matchPresentation: value as PlaybackMatchOverride })}
          >
            <option value="inherit">{words.useServerConfig}</option>
            <option value="off">{words.disabled}</option>
            <option value="scoreboard">{words.scoreboardSync}</option>
          </PlaybackSelect>
          <PlaybackSelect
            label={words.partialReplay}
            description={words.partialReplayHelp}
            value={options.allowPartial}
            onChange={(value) => onOptionsChange({ allowPartial: value as PlaybackToggleOverride })}
          >
            <option value="inherit">{words.useServerConfig}</option>
            <option value="on">{words.enabled}</option>
            <option value="off">{words.disabled}</option>
          </PlaybackSelect>
          <PlaybackSelect
            label={words.handoffMode}
            description={words.handoffModeHelp}
            value={options.handoffMode}
            onChange={(value) => onOptionsChange({ handoffMode: value as PlaybackHandoffMode })}
          >
            <option value="inherit">{words.useServerConfig}</option>
            <option value="death_contact_c4">{words.handoffDeathContactC4}</option>
            <option value="death_or_contact">{words.handoffDeathOrContact}</option>
            <option value="death">{words.handoffDeath}</option>
            <option value="contact">{words.handoffContact}</option>
            <option value="off">{words.disabled}</option>
          </PlaybackSelect>
          {options.handoffMode !== "inherit" ? (
            <PlaybackSelect
              label={words.handoffScope}
              description={words.handoffScopeHelp}
              value={options.handoffScope}
              onChange={(value) => onOptionsChange({ handoffScope: value as "slot" | "all" })}
            >
              <option value="slot">{words.handoffScopeSlot}</option>
              <option value="all">{words.handoffScopeAll}</option>
            </PlaybackSelect>
          ) : null}
          <PlaybackSelect
            label={words.threat360}
            description={words.threat360Help}
            value={options.threat360}
            onChange={(value) => onOptionsChange({ threat360: value as PlaybackToggleOverride })}
          >
            <option value="inherit">{words.useServerConfig}</option>
            <option value="on">{words.enabled}</option>
            <option value="off">{words.disabled}</option>
          </PlaybackSelect>
          {options.threat360 !== "inherit" ? (
            <div className="playback-360-fields">
              <label>
                <span>{words.threat360Range}</span>
                <input
                  type="number"
                  min={150}
                  max={800}
                  step={10}
                  value={options.threat360Range}
                  onChange={(event) => {
                    const value = Number(event.target.value);
                    if (Number.isFinite(value) && value >= 150 && value <= 800) onOptionsChange({ threat360Range: value });
                  }}
                />
              </label>
              <label>
                <input type="checkbox" checked={options.threat360Los} onChange={(event) => onOptionsChange({ threat360Los: event.target.checked })} />
                {words.threat360RequireLos}
              </label>
            </div>
          ) : null}
            </div>
          </section>
        </div>
      </details>

    </section>
  );
}
