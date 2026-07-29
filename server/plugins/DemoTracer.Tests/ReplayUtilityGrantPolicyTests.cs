namespace DemoTracer.Tests;

public sealed class ReplayUtilityGrantPolicyTests
{
    private const ulong ReplaySteamId = 76561198000000001UL;

    [Theory]
    [InlineData("item_pickup", 45)]
    [InlineData("item_transfer", 43)]
    public void MatchingUtilityAcquisitionsAreGranted(string kind, int weaponDefIndex)
    {
        var replayEvent = new ReplayHifiEvent
        {
            Kind = kind,
            TargetSteamId = ReplaySteamId,
            WeaponDefIndex = weaponDefIndex,
            TargetCountAfter = 1
        };

        Assert.True(DemoTracerPlugin.ShouldQueueReplayUtilityGrant(replayEvent, ReplaySteamId));
    }

    [Fact]
    public void LiveWeaponPurchaseIsNotHandledByUtilityGrantPath()
    {
        var replayEvent = new ReplayHifiEvent
        {
            Kind = "item_pickup",
            TargetSteamId = ReplaySteamId,
            WeaponDefIndex = 36,
            TargetCountAfter = 1
        };

        Assert.False(DemoTracerPlugin.ShouldQueueReplayUtilityGrant(replayEvent, ReplaySteamId));
    }

    [Theory]
    [InlineData("item_drop", 45, 76561198000000001UL)]
    [InlineData("item_pickup", 45, 76561198000000002UL)]
    public void NonAcquisitionsAndOtherPlayersAreRejected(
        string kind,
        int weaponDefIndex,
        ulong targetSteamId)
    {
        var replayEvent = new ReplayHifiEvent
        {
            Kind = kind,
            TargetSteamId = targetSteamId,
            WeaponDefIndex = weaponDefIndex,
            TargetCountAfter = 1
        };

        Assert.False(DemoTracerPlugin.ShouldQueueReplayUtilityGrant(replayEvent, ReplaySteamId));
    }
}
