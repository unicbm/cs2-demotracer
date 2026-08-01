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
    public void KnifeCanNeverEnterTheDestructiveReplacementPath(string className)
    {
        Assert.False(ReplayWeaponReplacementPolicy.CanRemoveForReplacement(className));
    }

    [Theory]
    [InlineData("weapon_glock")]
    [InlineData("weapon_ak47")]
    [InlineData("weapon_smokegrenade")]
    public void NonKnifeReplayEquipmentCanStillUseTheReplacementPath(string className)
    {
        Assert.True(ReplayWeaponReplacementPolicy.CanRemoveForReplacement(className));
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void CancellationRestoresFallbackOnlyToTheSameEmptyPawn(
        bool samePlayer,
        bool samePawn,
        bool targetPresent,
        bool anySlotWeapon,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayWeaponReplacementPolicy.ShouldRestoreFallback(
                samePlayer,
                samePawn,
                targetPresent,
                anySlotWeapon));
    }

    [Fact]
    public void EmptyCtSidearmSlotFallsBackToP2000InsteadOfKnife()
    {
        Assert.Equal(
            "weapon_hkp2000",
            ReplayWeaponReplacementPolicy.EmptySlotFallbackItem(
                ReplayWeaponSlot.Secondary,
                counterTerrorist: true,
                targetItem: "weapon_usp_silencer"));
    }
}
