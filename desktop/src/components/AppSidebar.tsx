import { BatchIcon, HelpIcon, LibraryIcon, SlidersIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { WorkspaceSection } from "../types";
import "./app-sidebar.css";

export function AppSidebar({
  words,
  activeSection,
  onLibrary,
  onBatch,
  onSettings,
  onFaq,
}: {
  words: TextDictionary;
  activeSection: WorkspaceSection;
  onLibrary: () => void;
  onBatch: () => void;
  onSettings: () => void;
  onFaq: () => void;
}) {
  const libraryActive = activeSection === "library";
  const batchActive = activeSection === "batch";
  const settingsActive = activeSection === "settings";
  const faqActive = activeSection === "faq";
  return (
    <aside className="app-sidebar" aria-label={words.mainNavigation}>
      <nav>
        <button
          className={libraryActive ? "is-active" : ""}
          type="button"
          onClick={onLibrary}
          aria-current={libraryActive ? "page" : undefined}
          title={words.navLibrary}
        >
          <LibraryIcon size={19} />
          <span>{words.navLibrary}</span>
        </button>
        <button
          className={batchActive ? "is-active" : ""}
          type="button"
          onClick={onBatch}
          aria-current={batchActive ? "page" : undefined}
          title={words.navBatch}
        >
          <BatchIcon size={19} />
          <span>{words.navBatch}</span>
        </button>
        <button
          className={settingsActive ? "is-active" : ""}
          type="button"
          onClick={onSettings}
          aria-current={settingsActive ? "page" : undefined}
          title={words.navSettings}
        >
          <SlidersIcon size={19} />
          <span>{words.navSettings}</span>
        </button>
        <button
          className={faqActive ? "is-active" : ""}
          type="button"
          onClick={onFaq}
          aria-current={faqActive ? "page" : undefined}
          title={words.navFaq}
        >
          <HelpIcon size={19} />
          <span>{words.navFaq}</span>
        </button>
      </nav>
      <div className="app-sidebar-status">
        <i aria-hidden="true" />
        <span>{words.localOnlyShort}</span>
      </div>
    </aside>
  );
}
