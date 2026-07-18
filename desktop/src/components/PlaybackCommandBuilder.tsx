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
}

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

function formatPreset(mask: number): string {
  return `0x${mask.toString(16).toUpperCase().padStart(2, "0")}`;
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
  const command = `dtr_preset ${formatPreset(mask)}; ${goCommand}`;
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
      <div className="section-heading-row playback-command-heading">
        <div>
          <span className="playback-step-label">{words.playbackStep}</span>
          <h2 id="playback-command-title">{words.playDemoCommand}</h2>
        </div>
        {result.rounds.length > 1 ? (
          <div className="segmented-control compact" role="group" aria-label={words.playDemoMode}>
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

      <div className="playback-capabilities" aria-label={words.archiveCapabilities}>
        <div className={`playback-capability${voiceAvailable ? " is-available" : ""}`}>
          <span>{words.voiceCapability}</span>
          <strong>{voiceStatus}</strong>
        </div>
        <div className={`playback-capability${cosmeticsAvailable ? " is-available" : ""}${cosmetics ? " is-risk" : ""}`}>
          <span>{words.cosmeticsCapability}</span>
          <strong>{cosmeticStatus}</strong>
        </div>
      </div>

      <details className="playback-advanced">
        <summary>
          <span>
            <strong>{words.standardPlayback}</strong>
            <small>{activeOptions.length > 0 ? activeOptions.join(" · ") : words.noSyncOptions}</small>
          </span>
          <b>{words.advancedPlaybackSettings}</b>
          <ChevronIcon size={15} />
        </summary>
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
      </details>

      <div className="command-box playback-command-output">
        <code title={command}>{command}</code>
        <button className="primary-button" type="button" onClick={() => onCopy(command)}>
          {copied ? <CheckIcon size={16} /> : <CopyIcon size={16} />}
          {copied ? words.copied : words.copyPlaybackCommand}
        </button>
      </div>
      <p className="playback-command-help">{words.playDemoCommandHelp}</p>
    </section>
  );
}
