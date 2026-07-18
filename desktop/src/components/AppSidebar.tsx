import { LibraryIcon, PlusIcon, SlidersIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { WorkspaceSection } from "../types";
import "./app-sidebar.css";

export function AppSidebar({
  words,
  activeSection,
  busy,
  onLibrary,
  onConvert,
  onSettings,
}: {
  words: TextDictionary;
  activeSection: WorkspaceSection;
  busy: boolean;
  onLibrary: () => void;
  onConvert: () => void;
  onSettings: () => void;
}) {
  const libraryActive = activeSection === "library";
  const conversionActive = activeSection === "convert";
  const settingsActive = activeSection === "settings";
  return (
    <aside className="app-sidebar" aria-label={words.mainNavigation}>
      <nav>
        <button
          className={libraryActive ? "is-active" : ""}
          type="button"
          onClick={onLibrary}
          disabled={busy}
          aria-current={libraryActive ? "page" : undefined}
          title={words.navLibrary}
        >
          <LibraryIcon size={19} />
          <span>{words.navLibrary}</span>
        </button>
        <button
          className={conversionActive ? "is-active" : ""}
          type="button"
          onClick={onConvert}
          disabled={busy}
          aria-current={conversionActive ? "page" : undefined}
          title={words.navConvert}
        >
          <PlusIcon size={19} />
          <span>{words.navConvert}</span>
        </button>
        <button
          className={settingsActive ? "is-active" : ""}
          type="button"
          onClick={onSettings}
          disabled={busy}
          aria-current={settingsActive ? "page" : undefined}
          title={words.navSettings}
        >
          <SlidersIcon size={19} />
          <span>{words.navSettings}</span>
        </button>
      </nav>
      <div className="app-sidebar-status">
        <i aria-hidden="true" />
        <span>{words.localOnlyShort}</span>
      </div>
    </aside>
  );
}
