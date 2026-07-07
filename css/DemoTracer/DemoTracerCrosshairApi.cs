using CounterStrikeSharp.API;
using DemoTracerApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool TrySetBotHudCrosshairOverride(
        int slot,
        string crosshairCode,
        out DemoTracerCrosshairOverrideResult result)
    {
        if (slot is < 0 or >= MaxPlayerSlots)
        {
            result = CrosshairApiResult(false, slot, crosshairCode, "invalid_slot", -2);
            return false;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true })
        {
            result = CrosshairApiResult(false, slot, crosshairCode, "slot_not_found", -3);
            return false;
        }

        if (!IsReplaySlotStillSafe(slot))
        {
            result = CrosshairApiResult(false, slot, crosshairCode, "unsafe_replay_slot", -4);
            return false;
        }

        var apply = _hudCrosshairOverrides.TryApplySlot(slot, player, crosshairCode);
        result = CrosshairApiResult(apply);
        return apply.Ok;
    }

    private bool ClearBotHudCrosshairOverride(
        int slot,
        out DemoTracerCrosshairOverrideResult result)
    {
        if (slot is < 0 or >= MaxPlayerSlots)
        {
            result = CrosshairApiResult(false, slot, string.Empty, "invalid_slot", -2);
            return false;
        }

        _hudCrosshairOverrides.ClearSlot(slot);
        result = CrosshairApiResult(true, slot, string.Empty, "cleared", 0);
        return true;
    }

    private void ClearBotHudCrosshairOverrides()
        => _hudCrosshairOverrides.ClearAll(disablePatch: true);

    private DemoTracerCrosshairOverrideStatus GetCrosshairOverrideStatus()
    {
        var status = _hudCrosshairOverrides.Status;
        return new DemoTracerCrosshairOverrideStatus
        {
            MapCount = status.MapCount,
            LastNativeResult = status.LastRc,
            DecodeFailures = status.DecodeFailures,
            PatchConfigured = status.PatchConfigured
        };
    }

    private static DemoTracerCrosshairOverrideResult CrosshairApiResult(
        HudCrosshairOverrideApplyResult apply)
    {
        return new DemoTracerCrosshairOverrideResult
        {
            Ok = apply.Ok,
            Slot = apply.Slot,
            CrosshairCode = apply.Code,
            PawnEntityIndex = apply.PawnEntityIndex,
            WeaponEntityIndex = apply.WeaponEntityIndex,
            NativeResult = apply.Rc,
            Reason = apply.Reason
        };
    }

    private static DemoTracerCrosshairOverrideResult CrosshairApiResult(
        bool ok,
        int slot,
        string code,
        string reason,
        int rc)
    {
        return new DemoTracerCrosshairOverrideResult
        {
            Ok = ok,
            Slot = slot,
            CrosshairCode = DemoTracerCrosshairCode.Normalize(code) ?? string.Empty,
            NativeResult = rc,
            Reason = reason
        };
    }
}
