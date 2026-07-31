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

    private static PlayoffRoundCandidate Candidate(
        int round,
        bool pistol,
        string economy,
        params ulong[] steamIds)
        => new(round, pistol, economy, steamIds);
}
