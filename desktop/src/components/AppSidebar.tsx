import { LibraryIcon, PlusIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { Phase } from "../types";
import "./app-sidebar.css";

export function AppSidebar({
  words,
  phase,
  busy,
  onLibrary,
  onConvert,
}: {
  words: TextDictionary;
  phase: Phase;
  busy: boolean;
  onLibrary: () => void;
  onConvert: () => void;
}) {
  const libraryActive = phase === "idle" || phase === "openingArchive" || phase === "archive";
  const conversionActive = !libraryActive;
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
      </nav>
      <div className="app-sidebar-status">
        <i aria-hidden="true" />
        <span>{words.localOnlyShort}</span>
      </div>
    </aside>
  );
}
