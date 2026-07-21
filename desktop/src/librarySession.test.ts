import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  EMPTY_LIBRARY_WORKSPACE,
  LIBRARY_SESSION_STORAGE_KEY,
  libraryWorkspaceReducer,
  readStoredLibrarySession,
} from "./librarySession.ts";
import type { ManifestArchive } from "./types";

function archive(): ManifestArchive {
  return {
    manifestPath: "C:\\Library\\match\\manifest.json",
    root: "C:\\Library\\match",
    demoPath: "match.dem",
    demoId: "match-aabbccdd",
    demoSha256: "aa".repeat(32),
    map: "de_mirage",
    tickRate: 64,
    abi: 17,
    formatVersion: 7,
    rounds: [
      { round: 0, available: true, sequenceLength: 2 },
      { round: 1, available: true, sequenceLength: 1 },
    ],
    issues: [],
    players: [],
  } as ManifestArchive;
}

describe("library workspace session", () => {
  it("keeps the opened archive while navigating through FAQ", () => {
    const opened = libraryWorkspaceReducer(EMPTY_LIBRARY_WORKSPACE, { type: "open", archive: archive() });
    const faq = libraryWorkspaceReducer(opened, { type: "navigate", section: "faq" });
    const returned = libraryWorkspaceReducer(faq, { type: "navigate", section: "library" });

    assert.equal(returned.archive?.manifestPath, archive().manifestPath);
    assert.equal(returned.selectedRound, 0);
  });

  it("restores a valid round, player and command mode without reopening defaults", () => {
    const state = libraryWorkspaceReducer(EMPTY_LIBRARY_WORKSPACE, {
      type: "open",
      archive: archive(),
      restored: {
        manifestPath: archive().manifestPath,
        selectedRound: 1,
        selectedPlayer: { teamId: "a", playerIndex: 3 },
        commandMode: "round",
      },
    });

    assert.equal(state.selectedRound, 1);
    assert.deepEqual(state.selectedPlayer, { teamId: "a", playerIndex: 3 });
    assert.equal(state.commandMode, "round");
  });

  it("rejects malformed persisted sessions", () => {
    const storage = {
      getItem: (key: string) => key === LIBRARY_SESSION_STORAGE_KEY ? "{broken" : null,
    };
    assert.equal(readStoredLibrarySession(storage), null);
  });
});
