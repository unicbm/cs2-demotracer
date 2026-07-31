namespace DemoTracer.Tests;

public sealed class FreezePrerollTimingTests
{
    [Theory]
    [InlineData(15.0f, 15.0f, 15.0f, 0.0f, 15.0f)]
    [InlineData(20.0f, 20.0f, 15.0f, 5.0f, 15.0f)]
    [InlineData(15.0f, 6.0f, 15.0f, 0.0f, 6.0f)]
    [InlineData(15.0f, 10.0f, 4.0f, 6.0f, 4.0f)]
    public void UsesOnlyTheFreezeWindowThatActuallyRemains(
        float freezeTimeSeconds,
        float phaseRemainingSeconds,
        float recordedPrerollSeconds,
        float expectedDelaySeconds,
        float expectedPlaybackSeconds)
    {
        var timing = ReplayRuntimePolicy.ComputeFreezePrerollTiming(
            freezeTimeSeconds,
            phaseRemainingSeconds,
            recordedPrerollSeconds);

        Assert.Equal(expectedDelaySeconds, timing.DelaySeconds);
        Assert.Equal(expectedPlaybackSeconds, timing.PlaybackSeconds);
    }
}
