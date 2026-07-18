namespace DemoTracer.Tests;

public sealed class MusicKitAlignmentTests
{
    [Fact]
    public void MatchingInventoryAndControllerStateNeedsNoRepair()
    {
        Assert.True(DemoTracerPlugin.ReplayMusicKitStateMatches(
            expectedMusicKitId: 70,
            inventoryMusicKitId: 70,
            controllerMusicKitId: 70,
            controllerMusicKitMvps: 0,
            mvpNoMusic: false));
    }

    [Theory]
    [InlineData(null, 70, false)]
    [InlineData(1, 70, false)]
    [InlineData(70, 1, false)]
    [InlineData(70, 70, true)]
    public void MissingOrStaleRuntimeStateRequiresRepair(
        int? inventoryMusicKitId,
        int controllerMusicKitId,
        bool mvpNoMusic)
    {
        Assert.False(DemoTracerPlugin.ReplayMusicKitStateMatches(
            expectedMusicKitId: 70,
            inventoryMusicKitId,
            controllerMusicKitId,
            controllerMusicKitMvps: 0,
            mvpNoMusic));
    }

    [Fact]
    public void StaleMvpCountRequiresRepair()
    {
        Assert.False(DemoTracerPlugin.ReplayMusicKitStateMatches(
            expectedMusicKitId: 70,
            inventoryMusicKitId: 70,
            controllerMusicKitId: 70,
            controllerMusicKitMvps: 1,
            mvpNoMusic: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidManifestKitCannotMatchRuntimeState(int expectedMusicKitId)
    {
        Assert.False(DemoTracerPlugin.ReplayMusicKitStateMatches(
            expectedMusicKitId,
            inventoryMusicKitId: 70,
            controllerMusicKitId: 70,
            controllerMusicKitMvps: 0,
            mvpNoMusic: false));
    }
}
