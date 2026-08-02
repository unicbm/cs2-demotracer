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
    [InlineData(true, true, 4, 1, (int)WeaponGrantVerificationAction.TargetReady)]
    [InlineData(false, true, 4, 1, (int)WeaponGrantVerificationAction.Conflict)]
    [InlineData(false, false, 4, 1, (int)WeaponGrantVerificationAction.WaitForAttachment)]
    [InlineData(false, false, 0, 1, (int)WeaponGrantVerificationAction.RetryGrant)]
    [InlineData(false, false, 0, 0, (int)WeaponGrantVerificationAction.UseFallback)]
    public void GrantCompletionUsesObservedInventoryNotTheReturnedEntityPointer(
        bool targetPresent,
        bool anySlotWeapon,
        int grantWaitFramesRemaining,
        int grantRetryAttemptsRemaining,
        int expected)
    {
        Assert.Equal(
            (WeaponGrantVerificationAction)expected,
            ReplayWeaponReplacementPolicy.VerifyGrant(
                targetPresent,
                anySlotWeapon,
                grantWaitFramesRemaining,
                grantRetryAttemptsRemaining));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void FailedSwitchIsCachedOnlyWhenTheTargetWeaponExists(
        bool targetPresent,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayWeaponReplacementPolicy.ShouldCacheFailedSwitch(targetPresent));
    }

    [Theory]
    [InlineData(true, false, false, 0, 8, (int)DetachedWeaponCleanupAction.Retry)]
    [InlineData(true, false, false, 1, 8, (int)DetachedWeaponCleanupAction.Destroy)]
    [InlineData(true, true, false, 1, 8, (int)DetachedWeaponCleanupAction.Retry)]
    [InlineData(true, false, true, 1, 8, (int)DetachedWeaponCleanupAction.Retry)]
    [InlineData(true, true, true, 9, 0, (int)DetachedWeaponCleanupAction.Abandon)]
    [InlineData(false, false, false, 1, 8, (int)DetachedWeaponCleanupAction.Abandon)]
    public void DetachedRandomizerWeaponCleanupCrossesAFrameAndWaitsForReferences(
        bool identityMatches,
        bool ownedByPawn,
        bool activeWeaponReference,
        int framesSinceDetach,
        int retriesRemaining,
        int expected)
    {
        Assert.Equal(
            (DetachedWeaponCleanupAction)expected,
            ReplayWeaponReplacementPolicy.DecideDetachedWeaponCleanup(
                identityMatches,
                ownedByPawn,
                activeWeaponReference,
                framesSinceDetach,
                retriesRemaining));
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
