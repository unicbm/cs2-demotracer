/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayPlanStateTests
{
    [Fact]
    public void ClearArmedResetsTheWholePlanAndInvalidatesPendingPolls()
    {
        var state = new ReplayPlanState
        {
            Armed = true,
            ArmedLoop = true,
            ArmedLabel = "round",
            ArmedManifestPath = "manifest.json",
            ArmedSourceRound = 7,
            ArmedPrepared = true,
            ArmedPreparePollToken = 4
        };

        state.ClearArmed();

        Assert.False(state.Armed);
        Assert.False(state.ArmedLoop);
        Assert.False(state.ArmedPrepared);
        Assert.Empty(state.ArmedLabel);
        Assert.Empty(state.ArmedManifestPath);
        Assert.Equal(-1, state.ArmedSourceRound);
        Assert.Equal(5, state.ArmedPreparePollToken);
    }

    [Fact]
    public void ClearSequenceResetsProgressAndInvalidatesPendingPolls()
    {
        var state = new ReplayPlanState
        {
            SequenceActive = true,
            SequenceManifestPath = "manifest.json",
            SequenceRounds = [3, 4],
            SequenceIndex = 1,
            SequencePrepared = true,
            SequencePreparedRound = 4,
            SequencePreparePollToken = 8
        };

        state.ClearSequence();

        Assert.False(state.SequenceActive);
        Assert.Empty(state.SequenceManifestPath);
        Assert.Empty(state.SequenceRounds);
        Assert.Equal(0, state.SequenceIndex);
        Assert.False(state.SequencePrepared);
        Assert.Equal(-1, state.SequencePreparedRound);
        Assert.Equal(9, state.SequencePreparePollToken);
    }

    [Fact]
    public void ClearPlayoffPendingReturnsPriorStateAndInvalidatesDecodeToken()
    {
        var state = new ReplayPlanState
        {
            PlayoffPreparePending = true,
            PlayoffPendingCanLoad = true,
            PlayoffPrepareToken = 2,
            PlayoffPendingTRound = 10,
            PlayoffPendingCtRound = 11,
            PlayoffPendingReason = "selected",
            PlayoffPendingPrepareReason = "round_start"
        };

        Assert.True(state.ClearPlayoffPending());
        Assert.False(state.PlayoffPreparePending);
        Assert.False(state.PlayoffPendingCanLoad);
        Assert.Equal(3, state.PlayoffPrepareToken);
        Assert.Equal(-1, state.PlayoffPendingTRound);
        Assert.Equal(-1, state.PlayoffPendingCtRound);
        Assert.Empty(state.PlayoffPendingReason);
        Assert.Empty(state.PlayoffPendingPrepareReason);
        Assert.False(state.ClearPlayoffPending());
    }

    [Fact]
    public void ClearPlayoffPreparedCanPreserveOrResetExtraRoundProgress()
    {
        var state = new ReplayPlanState
        {
            PlayoffPrepared = true,
            PlayoffPreparedTRound = 12,
            PlayoffPreparedCtRound = 13,
            PlayoffPreparedLabel = "ready",
            PlayoffRoundIndex = 2
        };

        Assert.True(state.ClearPlayoffPrepared(resetRoundIndex: false));
        Assert.Equal(2, state.PlayoffRoundIndex);
        Assert.False(state.PlayoffPrepared);
        Assert.Equal(-1, state.PlayoffPreparedTRound);
        Assert.Equal(-1, state.PlayoffPreparedCtRound);
        Assert.Empty(state.PlayoffPreparedLabel);

        state.ClearPlayoffPrepared(resetRoundIndex: true);
        Assert.Equal(0, state.PlayoffRoundIndex);
    }
}
