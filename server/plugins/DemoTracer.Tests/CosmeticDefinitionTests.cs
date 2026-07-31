namespace DemoTracer.Tests;

public sealed class CosmeticDefinitionTests
{
    [Fact]
    public void ZeusIsAWeaponCosmeticDefinition()
    {
        Assert.True(DemoTracerPlugin.IsWeaponCosmeticCategory(31));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(49)]
    public void EquipmentWithoutWeaponPaintsIsNotAWeaponCosmeticDefinition(int weaponDefIndex)
    {
        Assert.False(DemoTracerPlugin.IsWeaponCosmeticCategory(weaponDefIndex));
    }
}
