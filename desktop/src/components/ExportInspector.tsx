import { type RefObject, useEffect, useRef } from "react";
import { CheckIcon, CloseIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConverterSettings, SideChoice } from "../types";
import { DialogPrimitive } from "./Dialog";

interface SwitchControlProps {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}

function SwitchControl({ checked, label, onChange }: SwitchControlProps) {
  return (
    <button
      className="switch-control"
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

interface ExportInspectorProps {
  words: TextDictionary;
  settings: ConverterSettings;
  docked: boolean;
  returnFocusRef: RefObject<HTMLElement | null>;
  onChange: (patch: Partial<ConverterSettings>) => void;
  onRequestCosmetics: () => void;
  onClose: () => void;
  onRestoreDefaults: () => void;
}

function InspectorContents({
  words,
  settings,
  firstControlRef,
  dismissible,
  onChange,
  onRequestCosmetics,
  onClose,
  onRestoreDefaults,
}: Omit<ExportInspectorProps, "docked" | "returnFocusRef"> & {
  firstControlRef: RefObject<HTMLButtonElement | null>;
  dismissible: boolean;
}) {
  const sideOptions: Array<{ value: SideChoice; label: string }> = [
    { value: "both", label: words.both },
    { value: "t", label: words.t },
    { value: "ct", label: words.ct },
  ];

  return (
    <>
      <header className="inspector-header">
        <h2 id="export-inspector-title">{words.inspectorTitle}</h2>
        {dismissible ? (
          <button className="icon-button" type="button" onClick={onClose} aria-label={words.close} title={words.close}>
            <CloseIcon size={17} />
          </button>
        ) : null}
      </header>

      <div className="inspector-body">
        <section className="inspector-section">
          <h3>{words.playback}</h3>
          <div className="field-group">
            <span className="field-label">{words.side}</span>
            <div className="segmented-control" role="group" aria-label={words.side}>
              {sideOptions.map(({ value, label }, index) => (
                <button
                  ref={index === 0 ? firstControlRef : undefined}
                  className={settings.side === value ? "is-selected" : ""}
                  type="button"
                  aria-pressed={settings.side === value}
                  key={value}
                  onClick={() => onChange({ side: value })}
                >
                  {label}
                </button>
              ))}
            </div>
          </div>

          <fieldset className="choice-fieldset">
            <legend>{words.playbackRange}</legend>
            <label className="radio-choice">
              <input type="radio" name="playback-range" checked={!settings.fullRound} onChange={() => onChange({ fullRound: false })} />
              <span><strong>{words.cutBeforePlant}</strong><small>{words.cutBeforePlantHelp}</small></span>
            </label>
            <label className="radio-choice compact">
              <input type="radio" name="playback-range" checked={settings.fullRound} onChange={() => onChange({ fullRound: true })} />
              <span><strong>{words.fullRoundLabel}</strong></span>
            </label>
          </fieldset>
        </section>

        <section className="inspector-section">
          <h3>{words.media}</h3>
          <div className="setting-line">
            <div><strong>{words.exportVoice}</strong><small>{words.voiceHelp}</small></div>
            <SwitchControl checked={settings.exportVoice} label={words.exportVoice} onChange={(exportVoice) => onChange({ exportVoice })} />
          </div>
        </section>

        <details className="inspector-disclosure">
          <summary>{words.advanced}</summary>
          <label className="number-setting" htmlFor="freeze-preroll">
            <span>{words.freezePreroll}</span>
            <span className="number-input-wrap">
              <input
                id="freeze-preroll"
                type="number"
                min="0"
                max="120"
                step="1"
                value={settings.freezePrerollSeconds}
                onChange={(event) => {
                  const value = Math.min(120, Math.max(0, Number(event.target.value) || 0));
                  onChange({ freezePrerollSeconds: value });
                }}
              />
              <i>{words.seconds}</i>
            </span>
          </label>
        </details>

        <section className="inspector-section risk-section">
          <h3>{words.highRisk}</h3>
          <div className="setting-line">
            <div>
              <strong className="risk-label">
                {words.exportCosmetics}
                {settings.exportCosmetics ? <span><CheckIcon size={12} />{words.sessionConfirmed}</span> : null}
              </strong>
              <small>{words.cosmeticsHelp}</small>
            </div>
            <SwitchControl
              checked={settings.exportCosmetics}
              label={words.exportCosmetics}
              onChange={(checked) => {
                if (checked) onRequestCosmetics();
                else onChange({ exportCosmetics: false });
              }}
            />
          </div>
          {settings.exportCosmetics ? (
            <div className="sub-settings">
              <label><input type="checkbox" checked={settings.exportStickers} onChange={(event) => onChange({ exportStickers: event.target.checked })} />{words.exportStickers}</label>
              <label><input type="checkbox" checked={settings.exportCharms} onChange={(event) => onChange({ exportCharms: event.target.checked })} />{words.exportCharms}</label>
            </div>
          ) : null}
        </section>
      </div>

      <footer className="inspector-footer">
        <button className="text-button" type="button" onClick={onRestoreDefaults}>{words.restoreDefaults}</button>
      </footer>
    </>
  );
}

export function ExportInspector(props: ExportInspectorProps) {
  const firstControlRef = useRef<HTMLButtonElement | null>(null);
  const previousDockedRef = useRef(props.docked);

  useEffect(() => {
    if (props.docked && !previousDockedRef.current) {
      firstControlRef.current?.focus({ preventScroll: true });
    }
    previousDockedRef.current = props.docked;
  }, [props.docked]);

  if (!props.docked) {
    return (
      <DialogPrimitive
        labelledBy="export-inspector-title"
        onDismiss={props.onClose}
        initialFocusRef={firstControlRef}
        returnFocusRef={props.returnFocusRef}
        scrimClassName="inspector-scrim"
        className="export-inspector is-sheet"
      >
        <InspectorContents {...props} dismissible firstControlRef={firstControlRef} />
      </DialogPrimitive>
    );
  }

  return (
    <aside
      className="export-inspector is-docked"
      aria-labelledby="export-inspector-title"
    >
      <InspectorContents {...props} dismissible={false} firstControlRef={firstControlRef} />
    </aside>
  );
}
