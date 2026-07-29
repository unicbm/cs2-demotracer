namespace DemoTracerApi;

public interface IDemoTracerApi
{
    int ApiVersion { get; }

    bool IsSlotBusy(int slot);

    bool IsDemoTracerBot(int slot);

    bool TryGetBotCosmeticState(int slot, out DemoTracerBotCosmeticState state);

    bool TrySetBotHudCrosshairOverride(
        int slot,
        string crosshairCode,
        out DemoTracerCrosshairOverrideResult result);

    bool ClearBotHudCrosshairOverride(
        int slot,
        out DemoTracerCrosshairOverrideResult result);

    void ClearBotHudCrosshairOverrides();

    DemoTracerCrosshairOverrideStatus GetCrosshairOverrideStatus();
}

public sealed class DemoTracerBotCosmeticState
{
    public bool IsDemoTracerBot { get; set; }

    public bool IsSlotBusy { get; set; }

    public bool HasCosmeticEvidence { get; set; }

    public bool CosmeticWriterEnabled { get; set; }

    public bool ShouldDeferInventoryWrites { get; set; }
}

public sealed class DemoTracerCrosshairOverrideResult
{
    public bool Ok { get; set; }

    public int Slot { get; set; }

    public string CrosshairCode { get; set; } = string.Empty;

    public int PawnEntityIndex { get; set; } = -1;

    public int WeaponEntityIndex { get; set; } = -1;

    public int NativeResult { get; set; }

    public string Reason { get; set; } = string.Empty;
}

public sealed class DemoTracerCrosshairOverrideStatus
{
    public int MapCount { get; set; }

    public int LastNativeResult { get; set; }

    public int DecodeFailures { get; set; }

    public bool PatchConfigured { get; set; }
}
