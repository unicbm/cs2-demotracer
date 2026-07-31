/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class WeaponSlotReplacementTests
{
    [Fact]
    public void RoundZeroP2000ToUspWaitsForOldPistolBeforeGrantingTarget()
    {
        Assert.Equal(
            WeaponSlotReplacementAction.WaitForClear,
            ReplayWeaponReplacementPolicy.Decide(
                targetPresent: false,
                anySlotWeapon: true,
                clearWaitFramesRemaining: 8));

        Assert.Equal(
            WeaponSlotReplacementAction.GrantTarget,
            ReplayWeaponReplacementPolicy.Decide(
                targetPresent: false,
                anySlotWeapon: false,
                clearWaitFramesRemaining: 7));

        Assert.Equal(
            WeaponSlotReplacementAction.TargetReady,
            ReplayWeaponReplacementPolicy.Decide(
                targetPresent: true,
                anySlotWeapon: true,
                clearWaitFramesRemaining: 7));
    }

    [Fact]
    public void ReplacementTimeoutPreservesExistingPistolInsteadOfGrantingIntoConflict()
    {
        Assert.Equal(
            WeaponSlotReplacementAction.PreserveExisting,
            ReplayWeaponReplacementPolicy.Decide(
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
        Assert.False(ReplayWeaponReplacementPolicy.CanDropAndKill(className));
    }

    [Theory]
    [InlineData("weapon_glock")]
    [InlineData("weapon_ak47")]
    [InlineData("weapon_smokegrenade")]
    public void NonKnifeReplayEquipmentCanStillUseTheDropAndKillPath(string className)
    {
        Assert.True(ReplayWeaponReplacementPolicy.CanDropAndKill(className));
    }
}
