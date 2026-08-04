/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import { activeBatchItemCount, findRestorableBatch } from "./batchSession.ts";
import type { BatchLedger } from "./types.ts";

function ledger(status: BatchLedger["status"], itemStatuses: BatchLedger["items"][number]["status"][]): BatchLedger {
  return {
    status,
    items: itemStatuses.map((itemStatus, index) => ({
      itemId: String(index),
      status: itemStatus,
    } as BatchLedger["items"][number])),
  } as BatchLedger;
}

test("completed batch history never reappears as the current import", () => {
  const completedWithErrors = ledger("completedWithErrors", ["completed", "failed", "failed"]);
  const completed = ledger("completed", ["completed", "completed", "completed"]);

  assert.equal(findRestorableBatch([completedWithErrors, completed]), undefined);
});

test("only interrupted work is restored and counted", () => {
  const interrupted = ledger("paused", ["completed", "pending", "running", "failed"]);

  assert.equal(findRestorableBatch([ledger("completed", ["completed"]), interrupted]), interrupted);
  assert.equal(activeBatchItemCount(interrupted), 2);
});
