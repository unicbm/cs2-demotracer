namespace DemoTracer.Tests;

public sealed class WeaponSlotReplacementTests
{
    [Fact]
    public void RoundZeroP2000ToUspWaitsForOldPistolBeforeGrantingTarget()
    {
        Assert.Equal(
            DemoTracerPlugin.WeaponSlotReplacementAction.WaitForClear,
            DemoTracerPlugin.DecideWeaponSlotReplacement(
                targetPresent: false,
                anySlotWeapon: true,
                clearWaitFramesRemaining: 8));

        Assert.Equal(
            DemoTracerPlugin.WeaponSlotReplacementAction.GrantTarget,
            DemoTracerPlugin.DecideWeaponSlotReplacement(
                targetPresent: false,
                anySlotWeapon: false,
                clearWaitFramesRemaining: 7));

        Assert.Equal(
            DemoTracerPlugin.WeaponSlotReplacementAction.TargetReady,
            DemoTracerPlugin.DecideWeaponSlotReplacement(
                targetPresent: true,
                anySlotWeapon: true,
                clearWaitFramesRemaining: 7));
    }

    [Fact]
    public void ReplacementTimeoutPreservesExistingPistolInsteadOfGrantingIntoConflict()
    {
        Assert.Equal(
            DemoTracerPlugin.WeaponSlotReplacementAction.PreserveExisting,
            DemoTracerPlugin.DecideWeaponSlotReplacement(
                targetPresent: false,
                anySlotWeapon: true,
                clearWaitFramesRemaining: 0));
    }

    [Theory]
    [InlineData("weapon_knife")]
    [InlineData("weapon_knife_t")]
    [InlineData("weapon_knife_karambit")]
    [InlineData("weapon_bayonet")]
    public void KnifeCanNeverEnterTheDestructiveDropAndKillPath(string className)
    {
        Assert.False(DemoTracerPlugin.CanDropAndKillReplayWeapon(className));
    }

    [Theory]
    [InlineData("weapon_glock")]
    [InlineData("weapon_ak47")]
    [InlineData("weapon_smokegrenade")]
    public void NonKnifeReplayEquipmentCanStillUseTheDropAndKillPath(string className)
    {
        Assert.True(DemoTracerPlugin.CanDropAndKillReplayWeapon(className));
    }
}
