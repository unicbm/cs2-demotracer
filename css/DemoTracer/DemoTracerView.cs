using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly Dictionary<int, string> _replayCrosshairHudCodes = new();
    private readonly Dictionary<int, NativeHudReticlePaintConfig> _replayCrosshairHudConfigs = new();
    private readonly Dictionary<int, bool> _replayLeftHandDesiredLatches = new();
    private int _replayCrosshairHudMapCount;
    private int _replayCrosshairHudLastRc;
    private int _replayCrosshairHudDecodeFailures;
    private bool _replayCrosshairHudPatchConfigured;

    private static ReplayView NormalizeReplayView(ReplayView? view)
    {
        return new ReplayView
        {
            CrosshairCode = NormalizeCrosshairCode(view?.CrosshairCode),
            Viewmodel = NormalizeReplayViewmodel(view?.Viewmodel)
        };
    }

    private static string? NormalizeCrosshairCode(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 128
            ? null
            : trimmed;
    }

    private static bool HasCrosshairEvidence(ReplayView view)
        => !string.IsNullOrWhiteSpace(view.CrosshairCode);

    private static ReplayViewmodel? NormalizeReplayViewmodel(ReplayViewmodel? viewmodel)
    {
        if (viewmodel == null)
            return null;

        var normalized = new ReplayViewmodel
        {
            LeftHanded = viewmodel.LeftHanded,
            Fov = NormalizeViewmodelFloat(viewmodel.Fov, 0.0f, 120.0f),
            OffsetX = NormalizeViewmodelFloat(viewmodel.OffsetX, -64.0f, 64.0f),
            OffsetY = NormalizeViewmodelFloat(viewmodel.OffsetY, -64.0f, 64.0f),
            OffsetZ = NormalizeViewmodelFloat(viewmodel.OffsetZ, -64.0f, 64.0f)
        };

        return HasViewmodelEvidence(normalized) ? normalized : null;
    }

    private static float? NormalizeViewmodelFloat(float? value, float min, float max)
    {
        if (!value.HasValue || !float.IsFinite(value.Value))
            return null;

        return value.Value >= min && value.Value <= max ? value.Value : null;
    }

    private static bool HasViewmodelEvidence(ReplayView view)
        => HasViewmodelEvidence(view.Viewmodel);

    private static bool HasViewmodelEvidence(ReplayViewmodel? viewmodel)
        => viewmodel != null &&
           (viewmodel.LeftHanded.HasValue ||
            viewmodel.Fov.HasValue ||
            viewmodel.OffsetX.HasValue ||
            viewmodel.OffsetY.HasValue ||
            viewmodel.OffsetZ.HasValue);

    private void ResetCrosshairAlignState(bool resetCounters = false)
    {
        RestoreAllReplayViewerCrosshairs();
        ClearReplayCrosshairHudReticleMap(disablePatch: true);
    }

    private void ResetViewmodelAlignState(bool resetCounters = false)
    {
        RestoreAllReplayBotViewmodels();
    }

    private string FormatCrosshairStatusCounts()
        => $"crosshair_evidence={CountLoadedCrosshairEvidence()} crosshair_hud_map={_replayCrosshairHudMapCount} crosshair_hud_rc={_replayCrosshairHudLastRc} crosshair_decode_failed={_replayCrosshairHudDecodeFailures} crosshair_viewers={_viewerAppliedCrosshairCodes.Count}";

    private string FormatViewmodelStatusCounts()
        => $"viewmodel_evidence={CountLoadedViewmodelEvidence()} viewmodel_bots={_replayAppliedViewmodels.Count} viewmodel_failed={_replayFailedViewmodelSlots.Count} left_hand_latches={_replayLeftHandDesiredLatches.Count}";

    private int CountLoadedCrosshairEvidence()
        => _loadedReplays.Values.Count(replay => !replay.UtilityOnly && HasCrosshairEvidence(replay.View));

    private int CountLoadedViewmodelEvidence()
        => _loadedReplays.Values.Count(replay => !replay.UtilityOnly && HasViewmodelEvidence(replay.View));

    private void UpdateReplayViewerCrosshairs(TickPlayerSnapshot playerSnapshot)
    {
        if (!_crosshairAlignEnabled)
        {
            ClearReplayCrosshairHudReticleMap(disablePatch: true);
            RestoreAllReplayViewerCrosshairs();
            return;
        }

        RefreshReplayCrosshairHudReticleMap(playerSnapshot);
    }

    private bool RefreshReplayCrosshairHudReticleMap(TickPlayerSnapshot playerSnapshot)
    {
        RestoreAllReplayViewerCrosshairs();

        if (!EnsureReplayCrosshairHudReticlePatch())
            return false;

        var activeSlots = new HashSet<int>();
        var configured = 0;
        var decodeFailures = 0;
        var lastRc = 0;

        foreach (var slot in _loadedSlots.ToArray())
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay) ||
                replay.UtilityOnly ||
                !HasCrosshairEvidence(replay.View) ||
                !playerSnapshot.TryGetSlot(slot, out var replayBot) ||
                replayBot is not { IsValid: true } ||
                !IsReplayTargetBot(replayBot, playerSnapshot.Controllers))
            {
                ClearReplayCrosshairHudReticleMapEntry(slot);
                continue;
            }

            activeSlots.Add(slot);
            var code = replay.View.CrosshairCode!;
            if (!_replayCrosshairHudConfigs.TryGetValue(slot, out var config) ||
                !_replayCrosshairHudCodes.TryGetValue(slot, out var cachedCode) ||
                !string.Equals(cachedCode, code, StringComparison.Ordinal))
            {
                if (!TryDecodeCrosshairShareCodeToPaintConfig(code, out config, out _))
                {
                    decodeFailures++;
                    ClearReplayCrosshairHudReticleMapEntry(slot);
                    continue;
                }

                _replayCrosshairHudCodes[slot] = code;
                _replayCrosshairHudConfigs[slot] = config;
            }

            var pawnIndex = PawnEntityIndex(replayBot);
            var weaponIndex = ActiveWeaponEntityIndex(replayBot);
            if (pawnIndex < 0 && weaponIndex < 0)
            {
                ClearReplayCrosshairHudReticleMapEntry(slot);
                continue;
            }

            lastRc = BotControllerNative.HudReticleSetPaintConfigMapEntry(slot, pawnIndex, weaponIndex, config);
            if (lastRc == 0)
                configured++;
        }

        foreach (var slot in _replayCrosshairHudConfigs.Keys.ToArray())
        {
            if (!activeSlots.Contains(slot))
                ClearReplayCrosshairHudReticleMapEntry(slot);
        }

        _replayCrosshairHudMapCount = configured;
        _replayCrosshairHudLastRc = lastRc;
        _replayCrosshairHudDecodeFailures = decodeFailures;
        return configured > 0;
    }

    private bool EnsureReplayCrosshairHudReticlePatch()
    {
        if (!BotControllerNative.IsCompatible || !BotControllerNative.HasHudReticleProbeExports)
        {
            _replayCrosshairHudLastRc = -1;
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

        _replayCrosshairHudLastRc = rc != 0 ? rc : state.Rc;
        _replayCrosshairHudPatchConfigured = rc == 0 && state.Rc == 0;
        return _replayCrosshairHudPatchConfigured;
    }

    private void ClearReplayCrosshairHudReticleMapEntry(int slot)
    {
        _replayCrosshairHudCodes.Remove(slot);
        _replayCrosshairHudConfigs.Remove(slot);
        if (BotControllerNative.IsCompatible)
            _ = BotControllerNative.HudReticleClearPaintConfigMapEntry(slot);
    }

    private void ClearReplayCrosshairHudReticleMap(bool disablePatch)
    {
        if (_replayCrosshairHudMapCount == 0 &&
            _replayCrosshairHudCodes.Count == 0 &&
            _replayCrosshairHudConfigs.Count == 0 &&
            !_replayCrosshairHudPatchConfigured)
        {
            _replayCrosshairHudLastRc = 0;
            _replayCrosshairHudDecodeFailures = 0;
            return;
        }

        _replayCrosshairHudCodes.Clear();
        _replayCrosshairHudConfigs.Clear();
        _replayCrosshairHudMapCount = 0;
        _replayCrosshairHudDecodeFailures = 0;

        if (!BotControllerNative.IsCompatible)
        {
            _replayCrosshairHudLastRc = -1;
            _replayCrosshairHudPatchConfigured = false;
            return;
        }

        _ = BotControllerNative.HudReticleClearPaintConfigMap();
        if (disablePatch)
        {
            _ = BotControllerNative.HudReticleSetPaintConfigTarget(0, 0, 0, -1, -1);
            _replayCrosshairHudLastRc = BotControllerNative.HudReticleProbe(
                BotControllerNative.HudReticleActionConfigure,
                -1,
                int.MinValue,
                int.MinValue,
                0,
                out _);
            _replayCrosshairHudPatchConfigured = false;
        }
        else
        {
            _replayCrosshairHudLastRc = 0;
        }
    }

    private Dictionary<uint, int> BuildReplayPawnSlotMap(TickPlayerSnapshot playerSnapshot)
    {
        var replayPawnSlots = new Dictionary<uint, int>();
        foreach (var slot in _loadedSlots)
        {
            if (slot is < 0 or >= MaxPlayerSlots || !_lastPlayingSlots.Contains(slot))
                continue;

            if (!playerSnapshot.TryGetSlot(slot, out var replayController) ||
                replayController is not { IsValid: true })
            {
                continue;
            }

            if (replayController.PlayerPawn is { IsValid: true, Value.IsValid: true } replayPawn)
                replayPawnSlots[replayPawn.Value.Index] = slot;
        }

        return replayPawnSlots;
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

    private void UpdateReplayBotViewmodels(TickPlayerSnapshot playerSnapshot)
    {
        if (_loadedSlots.Count == 0)
        {
            RestoreAllReplayBotViewmodels();
            return;
        }
        if (!_leftHandDesiredEnabled)
            ClearReplayLeftHandDesiredLatches();

        var activeReplaySlots = new HashSet<int>();
        foreach (var slot in _loadedSlots)
        {
            if (slot is < 0 or >= MaxPlayerSlots ||
                !_lastPlayingSlots.Contains(slot) ||
                !_loadedReplays.TryGetValue(slot, out var replay) ||
                replay.UtilityOnly ||
                !HasViewmodelEvidence(replay.View))
            {
                ClearReplayLeftHandDesiredLatch(slot);
                continue;
            }

            if (!playerSnapshot.TryGetSlot(slot, out var replayBot) ||
                replayBot is not { IsValid: true, PawnIsAlive: true } ||
                !IsReplayTargetBot(replayBot, playerSnapshot.Controllers))
            {
                ClearReplayLeftHandDesiredLatch(slot);
                continue;
            }

            activeReplaySlots.Add(slot);
            ApplyReplayBotViewmodel(replayBot, replay.View.Viewmodel!);
            ApplyReplayLeftHandDesiredLatch(slot, replay.View.Viewmodel!.LeftHanded);
        }

        foreach (var slot in ViewmodelTrackedSlots())
        {
            if (!activeReplaySlots.Contains(slot))
                RestoreReplayBotViewmodel(slot);
        }
    }

    private IEnumerable<int> ViewmodelTrackedSlots()
    {
        return _replayOriginalViewmodels.Keys
            .Concat(_replayAppliedViewmodels.Keys)
            .Concat(_replayFailedViewmodelSlots)
            .Distinct()
            .ToArray();
    }

    private void ApplyReplayViewerCrosshair(CCSPlayerController viewer, string code)
    {
        _ = viewer;
        _ = code;
    }

    private void RestoreAllReplayViewerCrosshairs()
    {
        foreach (var slot in _viewerAppliedCrosshairCodes.Keys.ToArray())
            RestoreReplayViewerCrosshair(slot);
        _viewerOriginalCrosshairCodes.Clear();
    }

    private void DiscardReplayViewerCrosshairState()
    {
        _viewerAppliedCrosshairCodes.Clear();
        _viewerOriginalCrosshairCodes.Clear();
    }

    private void RestoreReplayViewerCrosshair(int slot)
    {
        _viewerAppliedCrosshairCodes.Remove(slot);
        if (!_viewerOriginalCrosshairCodes.TryGetValue(slot, out var original))
            return;

        _viewerOriginalCrosshairCodes.Remove(slot);
        if (string.IsNullOrWhiteSpace(original))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } || player.IsBot || _botHiderProbe.IsManagedBot(slot))
            return;

        _ = TryApplyCrosshairCodeToClient(player, original, "restore");
    }

    private void ApplyReplayBotViewmodel(CCSPlayerController bot, ReplayViewmodel viewmodel)
    {
        var slot = bot.Slot;
        if (slot is < 0 or >= MaxPlayerSlots)
            return;

        var pawn = bot.PlayerPawn.Value;
        if (pawn is not { IsValid: true })
            return;

        if (!_replayOriginalViewmodels.ContainsKey(slot))
            _replayOriginalViewmodels[slot] = ReadCurrentViewmodel(pawn);

        if (_replayAppliedViewmodels.TryGetValue(slot, out var current) &&
            ViewmodelsEquivalent(current, viewmodel))
        {
            return;
        }

        if (_replayFailedViewmodelSlots.Contains(slot))
            return;

        if (TryApplyViewmodelToPawn(pawn, viewmodel, $"slot={slot} replay_bot"))
        {
            _replayAppliedViewmodels[slot] = CopyViewmodel(viewmodel);
            _replayFailedViewmodelSlots.Remove(slot);
        }
        else
        {
            _replayFailedViewmodelSlots.Add(slot);
        }
    }

    private void RestoreAllReplayBotViewmodels()
    {
        foreach (var slot in ViewmodelTrackedSlots())
            RestoreReplayBotViewmodel(slot);
        _replayOriginalViewmodels.Clear();
        _replayAppliedViewmodels.Clear();
        _replayFailedViewmodelSlots.Clear();
        ClearReplayLeftHandDesiredLatches();
    }

    private void RestoreReplayBotViewmodel(int slot)
    {
        _replayAppliedViewmodels.Remove(slot);
        _replayFailedViewmodelSlots.Remove(slot);
        ClearReplayLeftHandDesiredLatch(slot);
        if (!_replayOriginalViewmodels.TryGetValue(slot, out var original))
            return;

        _replayOriginalViewmodels.Remove(slot);

        var bot = Utilities.GetPlayerFromSlot(slot);
        var pawn = bot?.PlayerPawn.Value;
        if (bot is not { IsValid: true } || pawn is not { IsValid: true } || !IsReplayTargetBot(bot))
            return;

        _ = TryApplyViewmodelToPawn(pawn, original, $"slot={slot} restore");
    }

    private static ReplayViewmodel ReadCurrentViewmodel(CCSPlayerPawn pawn)
    {
        return new ReplayViewmodel
        {
            LeftHanded = pawn.LeftHanded,
            Fov = pawn.ViewmodelFOV,
            OffsetX = pawn.ViewmodelOffsetX,
            OffsetY = pawn.ViewmodelOffsetY,
            OffsetZ = pawn.ViewmodelOffsetZ
        };
    }

    private static ReplayViewmodel CopyViewmodel(ReplayViewmodel viewmodel)
    {
        return new ReplayViewmodel
        {
            LeftHanded = viewmodel.LeftHanded,
            Fov = viewmodel.Fov,
            OffsetX = viewmodel.OffsetX,
            OffsetY = viewmodel.OffsetY,
            OffsetZ = viewmodel.OffsetZ
        };
    }

    private static bool ViewmodelsEquivalent(ReplayViewmodel left, ReplayViewmodel right)
    {
        return left.LeftHanded == right.LeftHanded &&
               NullableFloatBitsEqual(left.Fov, right.Fov) &&
               NullableFloatBitsEqual(left.OffsetX, right.OffsetX) &&
               NullableFloatBitsEqual(left.OffsetY, right.OffsetY) &&
               NullableFloatBitsEqual(left.OffsetZ, right.OffsetZ);
    }

    private static bool NullableFloatBitsEqual(float? left, float? right)
    {
        if (!left.HasValue || !right.HasValue)
            return left.HasValue == right.HasValue;
        if (left.Value == 0.0f && right.Value == 0.0f)
            return true;
        return BitConverter.SingleToInt32Bits(left.Value) == BitConverter.SingleToInt32Bits(right.Value);
    }

    private static bool TryApplyViewmodelToPawn(CCSPlayerPawn pawn, ReplayViewmodel viewmodel, string reason)
    {
        try
        {
            if (viewmodel.LeftHanded.HasValue)
            {
                pawn.LeftHanded = viewmodel.LeftHanded.Value;
                TrySetPawnStateChanged(pawn, "m_bLeftHanded");
            }
            if (viewmodel.Fov.HasValue)
            {
                pawn.ViewmodelFOV = viewmodel.Fov.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelFOV");
            }
            if (viewmodel.OffsetX.HasValue)
            {
                pawn.ViewmodelOffsetX = viewmodel.OffsetX.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelOffsetX");
            }
            if (viewmodel.OffsetY.HasValue)
            {
                pawn.ViewmodelOffsetY = viewmodel.OffsetY.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelOffsetY");
            }
            if (viewmodel.OffsetZ.HasValue)
            {
                pawn.ViewmodelOffsetZ = viewmodel.OffsetZ.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelOffsetZ");
            }

            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: viewmodel bot apply failed reason={reason}: {ex.Message}");
            return false;
        }
    }

    private void ApplyReplayLeftHandDesiredLatch(int slot, bool? leftHanded)
    {
        if (!_leftHandDesiredEnabled || !leftHanded.HasValue)
        {
            ClearReplayLeftHandDesiredLatch(slot);
            return;
        }

        if (_replayLeftHandDesiredLatches.TryGetValue(slot, out var current) &&
            current == leftHanded.Value)
        {
            return;
        }

        var rc = BotControllerNative.SetLeftHandDesiredLatch(slot, enabled: true, leftHandDesired: leftHanded.Value);
        if (rc == 0)
            _replayLeftHandDesiredLatches[slot] = leftHanded.Value;
    }

    private void ClearReplayLeftHandDesiredLatch(int slot)
    {
        _replayLeftHandDesiredLatches.Remove(slot);
        _ = BotControllerNative.SetLeftHandDesiredLatch(slot, enabled: false, leftHandDesired: false);
    }

    private void ClearReplayLeftHandDesiredLatches()
    {
        _replayLeftHandDesiredLatches.Clear();
        _ = BotControllerNative.ClearAllLeftHandDesiredLatches();
    }

    private static void TrySetPawnStateChanged(CCSPlayerPawn pawn, string field)
    {
        try
        {
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", field);
        }
        catch
        {
        }
    }

    private static bool TryApplyCrosshairCodeToClient(CCSPlayerController player, string code, string reason)
    {
        try
        {
            player.ExecuteClientCommandFromServer($"apply_crosshair_code {code}");
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: crosshair client import failed slot={player.Slot} reason={reason}: {ex.Message}");
            return false;
        }
    }
}
