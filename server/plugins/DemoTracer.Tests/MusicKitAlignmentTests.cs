/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class MusicKitAlignmentTests
{
    [Fact]
    public void MatchingInventoryAndControllerStateNeedsNoRepair()
    {
        Assert.True(ReplayRuntimePolicy.MusicKitStateMatches(
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
        Assert.False(ReplayRuntimePolicy.MusicKitStateMatches(
            expectedMusicKitId: 70,
            inventoryMusicKitId,
            controllerMusicKitId,
            controllerMusicKitMvps: 0,
            mvpNoMusic));
    }

    [Fact]
    public void StaleMvpCountRequiresRepair()
    {
        Assert.False(ReplayRuntimePolicy.MusicKitStateMatches(
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
        Assert.False(ReplayRuntimePolicy.MusicKitStateMatches(
            expectedMusicKitId,
            inventoryMusicKitId: 70,
            controllerMusicKitId: 70,
            controllerMusicKitMvps: 0,
            mvpNoMusic: false));
    }
}
