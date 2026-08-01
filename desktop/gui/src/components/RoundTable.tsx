/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { type KeyboardEvent, useRef } from "react";
import type { RoundInfo } from "../types";

export interface RoundTableLabels {
  caption: string;
  select: string;
  round: string;
  status: string;
  duration: string;
  teams: string;
  validRows: string;
  problems: string;
  recommended: string;
  suspicious: string;
  noProblems: string;
  suspiciousLocked: string;
}

interface RoundTableProps {
  labels: RoundTableLabels;
  rounds: RoundInfo[];
  selectedRounds: Set<number>;
  allowSuspicious: boolean;
  activeRound: number | null;
  disabled: boolean;
  onToggle: (round: RoundInfo) => void;
  onInspect: (round: RoundInfo) => void;
  formatNumber?: (value: number) => string;
  formatDuration?: (seconds: number) => string;
}

function defaultFormatDuration(seconds: number): string {
  const wholeSeconds = Math.max(0, Math.round(seconds));
  const minutes = Math.floor(wholeSeconds / 60);
  return `${minutes}:${String(wholeSeconds % 60).padStart(2, "0")}`;
}

export function RoundTable({
  labels,
  rounds,
  selectedRounds,
  allowSuspicious,
  activeRound,
  disabled,
  onToggle,
  onInspect,
  formatNumber = (value) => value.toLocaleString(),
  formatDuration = defaultFormatDuration,
}: RoundTableProps) {
  const tableRef = useRef<HTMLTableElement>(null);
  const inspectedRound = rounds.find((round) => round.round === activeRound) ?? rounds[0] ?? null;

  function moveCheckboxFocus(event: KeyboardEvent<HTMLInputElement>) {
    if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;

    const checkboxes = Array.from(
      tableRef.current?.querySelectorAll<HTMLInputElement>(
        'input[data-round-select="true"]:not(:disabled)',
      ) ?? [],
    );
    const currentIndex = checkboxes.indexOf(event.currentTarget);
    if (currentIndex < 0 || checkboxes.length === 0) return;

    let nextIndex = currentIndex;
    if (event.key === "ArrowUp") nextIndex = Math.max(0, currentIndex - 1);
    if (event.key === "ArrowDown") nextIndex = Math.min(checkboxes.length - 1, currentIndex + 1);
    if (event.key === "Home") nextIndex = 0;
    if (event.key === "End") nextIndex = checkboxes.length - 1;

    event.preventDefault();
    const nextCheckbox = checkboxes[nextIndex];
    nextCheckbox?.focus();
    const nextRoundNumber = Number(nextCheckbox?.dataset.roundNumber);
    const nextRound = rounds.find((round) => round.round === nextRoundNumber);
    if (nextRound) onInspect(nextRound);
  }

  return (
    <div className="round-inspector">
      <div className="round-master-pane">
        <div className="round-table-scroll">
          <table className="round-data-table" ref={tableRef}>
            <caption className="sr-only">{labels.caption}</caption>
            <thead>
              <tr>
                <th className="round-select-column" scope="col"><span className="sr-only">{labels.select}</span></th>
                <th scope="col">{labels.round}</th>
                <th scope="col">{labels.status}</th>
                <th scope="col">{labels.duration}</th>
              </tr>
            </thead>
            <tbody>
              {rounds.map((round) => {
                const suspicious = round.status === "suspicious";
                const selectionDisabled = suspicious && !allowSuspicious;
                const selected = selectedRounds.has(round.round);
                const current = inspectedRound?.round === round.round;
                const statusLabel = suspicious ? labels.suspicious : labels.recommended;

                return (
                  <tr
                    className={`round-data-row${selected ? " is-selected" : ""}${selectionDisabled ? " is-selection-locked" : ""}${current ? " is-current" : ""}`}
                    key={round.round}
                  >
                    <td className="round-select-cell">
                      <input
                        type="checkbox"
                        data-round-select="true"
                        data-round-number={round.round}
                        checked={selected}
                        disabled={disabled || selectionDisabled}
                        aria-label={`${labels.select} ${labels.round} ${round.round}, ${statusLabel}${selectionDisabled ? `, ${labels.suspiciousLocked}` : ""}`}
                        title={selectionDisabled ? labels.suspiciousLocked : undefined}
                        onChange={() => onToggle(round)}
                        onKeyDown={moveCheckboxFocus}
                      />
                    </td>
                    <th className="round-number-cell" scope="row">
                      <button
                        className="round-inspect-button"
                        type="button"
                        disabled={disabled}
                        aria-label={`${labels.round} ${round.round}, ${statusLabel}${selectionDisabled ? `, ${labels.suspiciousLocked}` : ""}`}
                        aria-current={current ? "true" : undefined}
                        onClick={() => onInspect(round)}
                      >
                        {String(round.round).padStart(2, "0")}
                      </button>
                    </th>
                    <td>
                      <span className={`round-status round-status-${round.status}`}>
                        <span className="round-status-icon" aria-hidden="true">{suspicious ? "!" : "✓"}</span>
                        {statusLabel}
                      </span>
                    </td>
                    <td className="round-duration-cell">{formatDuration(round.durationSeconds)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>

      <section className="round-detail-pane" aria-live="polite">
        {inspectedRound ? (
          <>
            <header className="round-detail-header">
              <div>
                <small>{labels.caption}</small>
                <h2>{labels.round} {String(inspectedRound.round).padStart(2, "0")}</h2>
              </div>
              <span className={`round-status round-status-${inspectedRound.status}`}>
                <span className="round-status-icon" aria-hidden="true">{inspectedRound.status === "suspicious" ? "!" : "✓"}</span>
                {inspectedRound.status === "suspicious" ? labels.suspicious : labels.recommended}
              </span>
            </header>

            <dl className="round-detail-facts">
              <div><dt>{labels.duration}</dt><dd>{formatDuration(inspectedRound.durationSeconds)}</dd></div>
              <div><dt>{labels.teams}</dt><dd>T {inspectedRound.tPlayers} / CT {inspectedRound.ctPlayers}</dd></div>
              <div><dt>{labels.validRows}</dt><dd>{formatNumber(inspectedRound.validRows)}</dd></div>
              <div><dt>{labels.select}</dt><dd>{selectedRounds.has(inspectedRound.round) ? "✓" : "—"}</dd></div>
            </dl>

            <section className="round-problem-panel" aria-labelledby="round-problem-title">
              <h3 id="round-problem-title">{labels.problems}</h3>
              {inspectedRound.problems.length > 0 ? (
                <ul>{inspectedRound.problems.map((problem, index) => <li key={`${inspectedRound.round}-${index}`}>{problem}</li>)}</ul>
              ) : (
                <p className="round-no-problems"><span aria-hidden="true">✓</span>{labels.noProblems}</p>
              )}
            </section>
          </>
        ) : null}
      </section>
    </div>
  );
}
