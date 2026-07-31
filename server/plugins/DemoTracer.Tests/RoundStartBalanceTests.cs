namespace DemoTracer.Tests;

public sealed class RoundStartBalanceTests
{
    [Theory]
    [InlineData(false, true, 5_250U)]
    [InlineData(true, false, 5_250U)]
    [InlineData(true, true, null)]
    public void RequiresOptInRuntimeSupportAndDemoEvidence(
        bool enabled,
        bool runtimeSupported,
        uint? evidence)
    {
        var resolved = ReplayRuntimePolicy.TryResolveRoundStartBalance(
            enabled,
            runtimeSupported,
            evidence,
            16_000,
            out var balance);

        Assert.False(resolved);
        Assert.Equal(0, balance);
    }

    [Fact]
    public void PreservesZeroAsPositiveEvidence()
    {
        var resolved = ReplayRuntimePolicy.TryResolveRoundStartBalance(
            enabled: true,
            runtimeSupported: true,
            evidence: 0,
            serverMaxMoney: 16_000,
            out var balance);

        Assert.True(resolved);
        Assert.Equal(0, balance);
    }

    [Fact]
    public void ClampsDemoEvidenceToCurrentServerMaximum()
    {
        var resolved = ReplayRuntimePolicy.TryResolveRoundStartBalance(
            enabled: true,
            runtimeSupported: true,
            evidence: 20_000,
            serverMaxMoney: 16_000,
            out var balance);

        Assert.True(resolved);
        Assert.Equal(16_000, balance);
    }
}
