/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotHiderImpl;

namespace DemoTracer.Tests;

public sealed class BotHiderIdentityReassertTests
{
    [Fact]
    public void InvalidatedPresentationForcesNativeIdentityPublishEvenWhenCacheMatches()
    {
        Assert.True(BotHiderPresentationService.RequiresNativeIdentityReassert(
            hasAppliedPresentation: false,
            appliedIncarnation: 0,
            effectiveIncarnation: 12));
    }

    [Fact]
    public void NewSlotIncarnationForcesNativeIdentityPublish()
    {
        Assert.True(BotHiderPresentationService.RequiresNativeIdentityReassert(
            hasAppliedPresentation: true,
            appliedIncarnation: 11,
            effectiveIncarnation: 12));
    }

    [Fact]
    public void StableAppliedPresentationDoesNotCreateContinuousIdentityTraffic()
    {
        Assert.False(BotHiderPresentationService.RequiresNativeIdentityReassert(
            hasAppliedPresentation: true,
            appliedIncarnation: 12,
            effectiveIncarnation: 12));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AvatarPublicationCanForceAnUnchangedLeaseToRepublishIdentity(
        bool forceReplace,
        bool expectedHeartbeat)
    {
        Assert.Equal(
            expectedHeartbeat,
            BotHiderPresentationLeasePolicy.ShouldHeartbeatExistingLease(
                forceReplace,
                hasLease: true,
                signatureMatches: true));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void FlairIsPublishedForMismatchOrScheduledNextFrameReassert(
        bool controllerMatches,
        bool nextFrameRepublishPending,
        bool expected)
    {
        Assert.Equal(
            expected,
            BotHiderPresentationService.ShouldPublishScoreboardFlair(
                controllerMatches,
                nextFrameRepublishPending));
    }
}
