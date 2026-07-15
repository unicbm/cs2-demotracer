import { CheckIcon, CopyIcon } from "../icons";
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

  // Dependencies are normalized here as well as in the handlers so stale or
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

  return (
    <section className="playback-command-builder" aria-labelledby="playback-command-title">
      <div className="section-heading-row playback-command-heading">
        <h2 id="playback-command-title">{words.playDemoCommand}</h2>
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
      <p className="playback-command-help">{words.playDemoCommandHelp}</p>

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
          checked={cosmetics}
          disabled={!cosmeticsAvailable}
          label={words.syncCosmetics}
          description={cosmeticsAvailable ? words.syncCosmeticsHelp : words.cosmeticsUnavailable}
          onChange={(checked) => onOptionsChange(checked
            ? { cosmetics: true, weapons: true }
            : { cosmetics: false })}
        />
        <PlaybackOption
          checked={steamIdentity}
          label={words.syncSteamIdentity}
          description={words.syncSteamIdentityHelp}
          onChange={(checked) => onOptionsChange(checked
            ? { steamIdentity: true }
            : { steamIdentity: false, avatar: false })}
        />
        <PlaybackOption
          checked={avatar}
          label={words.syncAvatar}
          description={words.syncAvatarHelp}
          onChange={(checked) => onOptionsChange(checked
            ? { avatar: true, steamIdentity: true }
            : { avatar: false })}
        />
        <PlaybackOption
          checked={voice}
          disabled={!voiceAvailable}
          label={words.syncVoice}
          description={voiceAvailable ? words.syncVoiceHelp : words.voiceUnavailableTitle}
          onChange={(checked) => onOptionsChange({ voice: checked })}
        />
        <PlaybackOption
          checked={playoff}
          disabled={!sequenceMode}
          label={words.playoffBeta}
          description={sequenceMode ? words.playoffHelp : words.sequenceOnly}
          onChange={(checked) => onOptionsChange({ playoff: checked })}
        />
      </div>

      <div className="command-box playback-command-output">
        <code title={command}>{command}</code>
        <button className="primary-button" type="button" onClick={() => onCopy(command)}>
          {copied ? <CheckIcon size={16} /> : <CopyIcon size={16} />}
          {copied ? words.copied : words.copyCommand}
        </button>
      </div>
    </section>
  );
}
