using DemoTracer;

namespace DemoTracer.Tests;

public sealed class ReplayRetentionPriorityTests
{
    [Fact]
    public void ParsesOrderedSteamIdGroup()
    {
        var ok = ReplayRetentionPriorityParser.TryParseGroup(
            "76561198000000003,76561198000000001,76561198000000002",
            out var steamIds,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(
            [76561198000000003UL, 76561198000000001UL, 76561198000000002UL],
            steamIds);
    }

    [Theory]
    [InlineData("76561198000000001,76561198000000001")]
    [InlineData("not-a-steamid")]
    [InlineData("1,2,3,4,5,6")]
    public void RejectsInvalidPriorityGroup(string value)
    {
        Assert.False(ReplayRetentionPriorityParser.TryParseGroup(value, out _, out _));
    }

    [Fact]
    public void AcceptsExplicitEmptyGroup()
    {
        Assert.True(ReplayRetentionPriorityParser.TryParseGroup("-", out var steamIds, out var error), error);
        Assert.Empty(steamIds);
    }

    [Fact]
    public void PartialRosterDropsLowestRetentionRankAndKeepsManifestOrder()
    {
        var selected = ReplayRetentionPriorityParser.SelectPreferredIndices([5, 1, 4, 2, 3], 4);

        Assert.Equal([1, 2, 3, 4], selected);
    }
}
