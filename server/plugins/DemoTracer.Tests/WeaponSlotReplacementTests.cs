/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class WeaponSlotReplacementTests
{
    [Theory]
    [InlineData(true, true, true, (int)ReplayWeaponSlotPlanAction.Complete)]
    [InlineData(true, false, true, (int)ReplayWeaponSlotPlanAction.PreserveExisting)]
    [InlineData(false, false, true, (int)ReplayWeaponSlotPlanAction.PreserveExisting)]
    [InlineData(true, false, false, (int)ReplayWeaponSlotPlanAction.GrantIntoEmptySlot)]
    [InlineData(false, false, false, (int)ReplayWeaponSlotPlanAction.Complete)]
    public void PreparationNeverDetachesAnOccupiedPrimaryOrSecondary(
        bool hasTarget,
        bool targetPresent,
        bool anySlotWeapon,
        int expected)
    {
        Assert.Equal(
            (ReplayWeaponSlotPlanAction)expected,
            ReplayWeaponReplacementPolicy.DecideSlotPlanAction(
                hasTarget,
                targetPresent,
                anySlotWeapon));
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
    [InlineData(false, 1, 0, false, (int)SafeC4AlignmentAction.DropForeignOwners)]
    [InlineData(true, 0, 1, false, (int)SafeC4AlignmentAction.WaitForCleanup)]
    [InlineData(true, 0, 0, false, (int)SafeC4AlignmentAction.TargetReady)]
    [InlineData(false, 0, 0, true, (int)SafeC4AlignmentAction.WaitForCleanup)]
    [InlineData(false, 0, 0, false, (int)SafeC4AlignmentAction.GrantTarget)]
    public void SafeC4TransferNeverGrantsUntilForeignOwnershipAndCleanupAreClear(
        bool targetHasC4,
        int foreignOwnerCount,
        int pendingDropCount,
        bool grantPending,
        int expected)
    {
        Assert.Equal(
            (SafeC4AlignmentAction)expected,
            ReplayWeaponReplacementPolicy.DecideSafeC4Alignment(
                targetHasC4,
                foreignOwnerCount,
                pendingDropCount,
                grantPending));
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
