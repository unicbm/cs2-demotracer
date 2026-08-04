/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { BatchLedger } from "./types";

export function findRestorableBatch(ledgers: readonly BatchLedger[]): BatchLedger | undefined {
  return ledgers.find((ledger) => (
    ledger.status === "pending"
    || ledger.status === "paused"
    || ledger.status === "running"
    || ledger.status === "stopping"
  ));
}

export function activeBatchItemCount(ledger: BatchLedger | null): number {
  return ledger?.items.filter((item) => item.status === "pending" || item.status === "running").length ?? 0;
}
