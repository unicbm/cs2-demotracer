using BotRandomizerApi;

namespace DemoTracer.Tests;

public sealed class BotRandomizerCosmeticLeaseTests
{
    [Fact]
    public void EmptyEvidenceDoesNotCreateClaim()
    {
        var claim = BuildClaim(Evidence());

        Assert.Null(claim);
    }

    [Fact]
    public void KnifeEvidenceDoesNotClaimMissingGloves()
    {
        var claim = BuildClaim(Evidence(knife: true));

        Assert.NotNull(claim);
        Assert.True(claim.Knife);
        Assert.False(claim.Gloves);
        Assert.Empty(claim.Weapons);
    }

    [Fact]
    public void AkEvidenceDoesNotClaimM4()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, true, false, false, false)
            ]));

        var weapon = Assert.Single(Assert.IsType<BotRandomizerCosmeticWriteClaim>(claim).Weapons);
        Assert.Equal(7, weapon.WeaponDefinitionIndex);
        Assert.True(weapon.Paint);
        Assert.DoesNotContain(claim.Weapons, candidate => candidate.WeaponDefinitionIndex is 16 or 60);
    }

    [Fact]
    public void PaintOnlyPreservesRandomizerAttachmentFamilies()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, true, false, false, false)
            ]));

        var weapon = Assert.Single(Assert.IsType<BotRandomizerCosmeticWriteClaim>(claim).Weapons);
        Assert.True(weapon.Paint);
        Assert.False(weapon.Stickers);
        Assert.False(weapon.Keychain);
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponPaint));
    }

    [Fact]
    public void StickerEvidenceDoesNotClaimMissingKeychain()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, true, true, false, true)
            ]));

        var weapon = Assert.Single(Assert.IsType<BotRandomizerCosmeticWriteClaim>(claim).Weapons);
        Assert.True(weapon.Paint);
        Assert.True(weapon.Stickers);
        Assert.False(weapon.Keychain);
        Assert.True(weapon.PaintUsesLegacyModel);
    }

    [Fact]
    public void EmptyOriginalOrDefaultEvidenceRemainsUnclaimed()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, false, false, false, null)
            ]));

        Assert.Null(claim);
    }

    [Fact]
    public void SnapshotRejectsSlotReuseAndWrongSubject()
    {
        var apiClaim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, true, false, false, false)
            ])));
        var snapshot = new DemoTracerBotRandomizerLeaseSnapshot();
        snapshot.Activate("token", "epoch-a", [apiClaim]);

        Assert.True(snapshot.TryGet(Slot, SubjectSteamId, out var active));
        Assert.True(active.MatchesIdentity(Incarnation, SubjectSteamId));
        Assert.False(active.MatchesIdentity(Incarnation + 1, SubjectSteamId));
        Assert.False(active.MatchesIdentity(Incarnation, SubjectSteamId + 1));
        Assert.False(snapshot.TryGet(Slot, SubjectSteamId + 1, out _));
    }

    [Fact]
    public void LeaseInvalidationImmediatelyDisablesEveryClaim()
    {
        var apiClaim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            agent: true,
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, true, true, true, false)
            ])));
        var snapshot = new DemoTracerBotRandomizerLeaseSnapshot();
        snapshot.Activate("token", "epoch-a", [apiClaim]);

        snapshot.Invalidate();

        Assert.Equal(string.Empty, snapshot.Token);
        Assert.Equal(string.Empty, snapshot.ProviderEpoch);
        Assert.Empty(snapshot.Claims);
        Assert.False(snapshot.TryGet(Slot, SubjectSteamId, out _));
    }

    [Fact]
    public void WeaponFieldClaimsNeverAuthorizeWholeAttributeListClears()
    {
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponPaint));
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponStickers));
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponKeychain));
    }

    [Fact]
    public void WholeItemClaimsMayRebuildTheirAttributeLists()
    {
        Assert.True(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.Knife));
        Assert.True(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.Gloves));
    }

    private static BotRandomizerCosmeticWriteClaim? BuildClaim(
        DemoTracerBotRandomizerPositiveEvidence evidence)
        => DemoTracerPlugin.BuildBotRandomizerWriteClaim(
            Slot,
            Incarnation,
            SubjectSteamId,
            evidence);

    private static DemoTracerBotRandomizerPositiveEvidence Evidence(
        bool agent = false,
        bool knife = false,
        bool gloves = false,
        bool musicKit = false,
        IReadOnlyList<DemoTracerBotRandomizerWeaponEvidence>? weapons = null)
        => new(agent, knife, gloves, musicKit, weapons ?? []);

    private const int Slot = 3;
    private const ulong Incarnation = 11;
    private const ulong SubjectSteamId = 76561198000000003;
}
