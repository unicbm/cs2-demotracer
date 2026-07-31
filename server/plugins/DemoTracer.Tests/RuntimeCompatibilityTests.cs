/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class RuntimeCompatibilityTests
{
    [Theory]
    [InlineData("1.41.6.9", true)]
    [InlineData("1.41.7.0", true)]
    [InlineData("1.41.7.2", true)]
    [InlineData("1.41.7.3", true)]
    [InlineData("1.41.7.4", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void ManagedSchemaWritesFailClosedOutsideVerifiedPatchRange(
        string patch,
        bool expected)
    {
        Assert.Equal(expected, ReplayRuntimePolicy.IsManagedSchemaPatchSupported(patch));
    }

    [Theory]
    [InlineData(false, true, 70, false)]
    [InlineData(true, false, 70, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 70, true)]
    public void MusicKitRequiresCosmeticOptInAndSupportedRuntime(
        bool cosmeticsEnabled,
        bool runtimeSupported,
        int musicKitId,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayRuntimePolicy.ShouldApplyMusicKit(
                cosmeticsEnabled,
                runtimeSupported,
                musicKitId));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ScoreboardFlairRequiresMatchOptInAndSupportedIdentity(
        bool scoreboardEnabled,
        bool identitySupportsFlair,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayRuntimePolicy.ShouldApplyScoreboardFlair(
                scoreboardEnabled,
                identitySupportsFlair));
    }
}
