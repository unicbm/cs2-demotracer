using CounterStrikeSharp.API.Core;

namespace DemoTracer;

internal sealed class HudCrosshairOverrideService
{
    private readonly Dictionary<int, string> _activeCodes = new();
    private readonly Dictionary<int, NativeHudReticlePaintConfig> _activeConfigs = new();
    private bool _patchConfigured;
    private int _mapCount;
    private int _lastRc;
    private int _decodeFailures;

    public HudCrosshairOverrideStatus Status => new(
        _mapCount,
        _lastRc,
        _decodeFailures,
        _patchConfigured);

    public HudCrosshairOverrideApplyResult TryApplySlot(
        int slot,
        CCSPlayerController target,
        string code)
    {
        var normalized = DemoTracerCrosshairCode.Normalize(code);
        if (string.IsNullOrWhiteSpace(normalized))
            return Fail(slot, code, "invalid_crosshair_code", -2);

        if (!EnsurePatch())
            return Fail(slot, normalized, "hud_reticle_unavailable", _lastRc);

        if (!_activeConfigs.TryGetValue(slot, out var config) ||
            !_activeCodes.TryGetValue(slot, out var cachedCode) ||
            !string.Equals(cachedCode, normalized, StringComparison.Ordinal))
        {
            if (!DemoTracerCrosshairCode.TryDecodeToPaintConfig(normalized, out config, out var reason))
            {
                _decodeFailures++;
                ClearSlot(slot);
                return Fail(slot, normalized, reason, -3);
            }

            _activeCodes[slot] = normalized;
            _activeConfigs[slot] = config;
        }

        var pawnIndex = PawnEntityIndex(target);
        var weaponIndex = ActiveWeaponEntityIndex(target);
        if (pawnIndex < 0 && weaponIndex < 0)
        {
            ClearSlot(slot);
            return Fail(slot, normalized, "target_entity_unresolved", -4);
        }

        var rc = BotControllerNative.HudReticleSetPaintConfigMapEntry(slot, pawnIndex, weaponIndex, config);
        _lastRc = rc;
        if (rc != 0)
        {
            _activeCodes.Remove(slot);
            _activeConfigs.Remove(slot);
            _ = BotControllerNative.HudReticleClearPaintConfigMapEntry(slot);
            _mapCount = _activeConfigs.Count;
            _lastRc = rc;
            return Fail(slot, normalized, "native_map_entry_failed", rc, pawnIndex, weaponIndex);
        }

        _mapCount = _activeConfigs.Count;
        return new HudCrosshairOverrideApplyResult(
            true,
            slot,
            normalized,
            pawnIndex,
            weaponIndex,
            rc,
            "ok");
    }

    public void ClearStaleExcept(IReadOnlySet<int> activeSlots)
    {
        foreach (var slot in _activeConfigs.Keys.ToArray())
        {
            if (!activeSlots.Contains(slot))
                ClearSlot(slot);
        }
    }

    public void ClearSlot(int slot)
    {
        _activeCodes.Remove(slot);
        _activeConfigs.Remove(slot);
        if (BotControllerNative.IsCompatible)
            _lastRc = BotControllerNative.HudReticleClearPaintConfigMapEntry(slot);
        _mapCount = _activeConfigs.Count;
    }

    public void ClearAll(bool disablePatch)
    {
        if (_mapCount == 0 &&
            _activeCodes.Count == 0 &&
            _activeConfigs.Count == 0 &&
            !_patchConfigured)
        {
            _lastRc = 0;
            _decodeFailures = 0;
            return;
        }

        _activeCodes.Clear();
        _activeConfigs.Clear();
        _mapCount = 0;
        _decodeFailures = 0;

        if (!BotControllerNative.IsCompatible)
        {
            _lastRc = -1;
            _patchConfigured = false;
            return;
        }

        _lastRc = BotControllerNative.HudReticleClearPaintConfigMap();
        if (!disablePatch)
            return;

        _lastRc = BotControllerNative.HudReticleProbe(
            BotControllerNative.HudReticleActionConfigure,
            -1,
            int.MinValue,
            int.MinValue,
            0,
            out _);
        _patchConfigured = false;
    }

    private bool EnsurePatch()
    {
        if (!BotControllerNative.IsCompatible || !BotControllerNative.HasHudReticleProbeExports)
        {
            _lastRc = -1;
            return false;
        }

        var rc = BotControllerNative.HudReticleProbe(
            BotControllerNative.HudReticleActionInstall | BotControllerNative.HudReticleActionConfigure,
            -1,
            int.MinValue,
            int.MinValue,
            BotControllerNative.HudReticleFlagPatchPaintConfig |
            BotControllerNative.HudReticleFlagUseForcedPaintConfig,
            out var state);

        _lastRc = rc != 0 ? rc : state.Rc;
        _patchConfigured = rc == 0 && state.Rc == 0;
        return _patchConfigured;
    }

    private static HudCrosshairOverrideApplyResult Fail(
        int slot,
        string code,
        string reason,
        int rc,
        int pawnIndex = -1,
        int weaponIndex = -1)
    {
        return new HudCrosshairOverrideApplyResult(
            false,
            slot,
            code,
            pawnIndex,
            weaponIndex,
            rc,
            reason);
    }

    private static int PawnEntityIndex(CCSPlayerController target)
        => target.PlayerPawn is { IsValid: true, Value.IsValid: true }
            ? checked((int)target.PlayerPawn.Value.Index)
            : -1;

    private static int ActiveWeaponEntityIndex(CCSPlayerController target)
    {
        if (target.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return -1;

        var pawn = target.PlayerPawn.Value;
        var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
        return weapon is { IsValid: true } ? checked((int)weapon.Index) : -1;
    }
}

internal readonly record struct HudCrosshairOverrideStatus(
    int MapCount,
    int LastRc,
    int DecodeFailures,
    bool PatchConfigured);

internal readonly record struct HudCrosshairOverrideApplyResult(
    bool Ok,
    int Slot,
    string Code,
    int PawnEntityIndex,
    int WeaponEntityIndex,
    int Rc,
    string Reason);
