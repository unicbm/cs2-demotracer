import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildReplayRetentionCommand,
  moveReplayRetentionPlayer,
  normalizeReplayRetentionOrder,
} from "./replayRetention.ts";

const a = ["76561198000000001", "76561198000000002", "76561198000000003"];
const b = ["76561198000000011", "76561198000000012"];

describe("replay retention priority", () => {
  it("keeps a stored permutation and rejects stale membership", () => {
    assert.deepEqual(normalizeReplayRetentionOrder(a, [a[2], a[0], a[1]]), [a[2], a[0], a[1]]);
    assert.deepEqual(normalizeReplayRetentionOrder(a, [a[0], a[1], b[0]]), a);
  });

  it("moves exactly one player", () => {
    assert.deepEqual(moveReplayRetentionPlayer(a, 2, 0), [a[2], a[0], a[1]]);
  });

  it("builds one manifest-plan command", () => {
    assert.equal(buildReplayRetentionCommand({ a, b }), `dtr_retain ${a.join(",")} ${b.join(",")}`);
  });
});
