import { Fragment, type KeyboardEvent, useRef } from "react";
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
  showDetails: string;
  hideDetails: string;
}

interface RoundTableProps {
  labels: RoundTableLabels;
  rounds: RoundInfo[];
  selectedRounds: Set<number>;
  allowSuspicious: boolean;
  expandedRound: number | null;
  onToggle: (round: RoundInfo) => void;
  onToggleDetails: (roundNumber: number) => void;
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
  expandedRound,
  onToggle,
  onToggleDetails,
  formatNumber = (value) => value.toLocaleString(),
  formatDuration = defaultFormatDuration,
}: RoundTableProps) {
  const tableRef = useRef<HTMLTableElement>(null);

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
    checkboxes[nextIndex]?.focus();
  }

  return (
    <div className="round-table-scroll">
      <table className="round-data-table" ref={tableRef}>
        <caption className="sr-only">{labels.caption}</caption>
        <thead>
          <tr>
            <th className="round-select-column" scope="col">
              <span className="sr-only">{labels.select}</span>
            </th>
            <th scope="col">{labels.round}</th>
            <th scope="col">{labels.status}</th>
            <th scope="col">{labels.duration}</th>
            <th scope="col">{labels.teams}</th>
            <th scope="col">{labels.validRows}</th>
            <th scope="col">{labels.problems}</th>
          </tr>
        </thead>
        <tbody>
          {rounds.map((round) => {
            const suspicious = round.status === "suspicious";
            const selectionDisabled = suspicious && !allowSuspicious;
            const selected = selectedRounds.has(round.round);
            const expanded = expandedRound === round.round && round.problems.length > 0;
            const detailId = `round-${round.round}-problems`;
            const statusLabel = suspicious ? labels.suspicious : labels.recommended;

            return (
              <Fragment key={round.round}>
                <tr
                  className={`round-data-row${selected ? " is-selected" : ""}${selectionDisabled ? " is-selection-locked" : ""}`}
                >
                  <td className="round-select-cell">
                    <input
                      type="checkbox"
                      data-round-select="true"
                      checked={selected}
                      disabled={selectionDisabled}
                      aria-label={`${labels.select} ${labels.round} ${round.round}, ${statusLabel}`}
                      title={selectionDisabled ? labels.suspiciousLocked : undefined}
                      onChange={() => onToggle(round)}
                      onKeyDown={moveCheckboxFocus}
                    />
                  </td>
                  <th className="round-number-cell" scope="row">
                    {String(round.round).padStart(2, "0")}
                  </th>
                  <td>
                    <span className={`round-status round-status-${round.status}`}>
                      <span className="round-status-icon" aria-hidden="true">
                        {suspicious ? "!" : "\u2713"}
                      </span>
                      {statusLabel}
                    </span>
                  </td>
                  <td className="round-duration-cell">{formatDuration(round.durationSeconds)}</td>
                  <td className="round-team-cell">
                    <span>T {round.tPlayers}</span>
                    <span aria-hidden="true">/</span>
                    <span>CT {round.ctPlayers}</span>
                  </td>
                  <td className="round-rows-cell">{formatNumber(round.validRows)}</td>
                  <td className="round-problem-cell">
                    {round.problems.length > 0 ? (
                      <button
                        className="round-problem-toggle"
                        type="button"
                        aria-expanded={expanded}
                        aria-controls={detailId}
                        title={round.problems.join(" \u00b7 ")}
                        onClick={() => onToggleDetails(round.round)}
                      >
                        <span>{round.problems[0]}</span>
                        <span className="round-detail-chevron" aria-hidden="true">
                          {expanded ? "\u2212" : "+"}
                        </span>
                        <span className="sr-only">
                          {expanded ? labels.hideDetails : labels.showDetails}
                        </span>
                      </button>
                    ) : (
                      <span className="round-no-problems">
                        <span aria-hidden="true">✓</span>
                        {labels.noProblems}
                      </span>
                    )}
                  </td>
                </tr>
                {expanded ? (
                  <tr className="round-detail-row">
                    <td colSpan={7}>
                      <div id={detailId} className="round-detail-content">
                        <strong>{labels.problems}</strong>
                        <ul>
                          {round.problems.map((problem, index) => (
                            <li key={`${round.round}-${index}`}>{problem}</li>
                          ))}
                        </ul>
                      </div>
                    </td>
                  </tr>
                ) : null}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
