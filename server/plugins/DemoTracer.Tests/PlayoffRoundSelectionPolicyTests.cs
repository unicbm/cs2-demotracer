/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class PlayoffRoundSelectionPolicyTests
{
    private static readonly HashSet<ulong> Roster = [101, 102];

    [Fact]
    public void SelectsOnlyFullBuyNonPistolRoundsCoveringTheRosterExactlyOnce()
    {
        var candidates = new[]
        {
            Candidate(8, pistol: false, economy: "full", 101, 102),
            Candidate(9, pistol: true, economy: "full", 101, 102),
            Candidate(10, pistol: false, economy: "force", 101, 102),
            Candidate(11, pistol: false, economy: "full", 101),
            Candidate(12, pistol: false, economy: "full", 101, 102, 102),
            Candidate(8, pistol: false, economy: "full", 101, 102)
        };

        Assert.Equal([8], PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster));
    }

    [Fact]
    public void ExtraReplayPlayersDoNotInvalidateCompleteRosterCoverage()
    {
        var candidates = new[]
        {
            Candidate(18, pistol: false, economy: "FULL", 101, 102, 999)
        };

        Assert.Equal([18], PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster));
    }

    [Fact]
    public void EmptyRosterHasNoEligibleRound()
    {
        var candidates = new[]
        {
            Candidate(8, pistol: false, economy: "full", 101, 102)
        };

        Assert.Empty(PlayoffRoundSelectionPolicy.FindEligibleRounds(
            candidates,
            new HashSet<ulong>()));
    }

    [Theory]
    [InlineData(2, 2, 2, 2, true)]
    [InlineData(3, 4, 2, 5, true)]
    [InlineData(1, 2, 2, 2, false)]
    [InlineData(2, 0, 2, 2, false)]
    public void RequiresTwoFullBuySamplesForBothRostersOnBothSides(
        int firstAsT,
        int firstAsCt,
        int secondAsT,
        int secondAsCt,
        bool expected)
    {
        Assert.Equal(
            expected,
            new PlayoffCoverageCounts(firstAsT, firstAsCt, secondAsT, secondAsCt)
                .IsEligible(minimumPerRosterSide: 2));
    }

    private static PlayoffRoundCandidate Candidate(
        int round,
        bool pistol,
        string economy,
        params ulong[] steamIds)
        => new(round, pistol, economy, steamIds);
}

public sealed class PlayoffReplayFallbackPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetainsLoadedReplayUntilPendingPlayoffPrefetchIsReady(
        bool preparationPending,
        bool prefetchReady)
    {
        Assert.Equal(
            !preparationPending || !prefetchReady,
            PlayoffReplayFallbackPolicy.ShouldRetainLoadedReplay(
                planReady: true,
                prepared: false,
                preparationPending,
                prefetchReady,
                hasLoadedReplay: true));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void DoesNotRetainFallbackOutsideAnUnpreparedPlayoffPlan(
        bool planReady,
        bool prepared,
        bool hasLoadedReplay)
    {
        Assert.False(PlayoffReplayFallbackPolicy.ShouldRetainLoadedReplay(
            planReady,
            prepared,
            preparationPending: true,
            prefetchReady: false,
            hasLoadedReplay));
    }
}
