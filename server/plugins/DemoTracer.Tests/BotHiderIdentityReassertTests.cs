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
}
