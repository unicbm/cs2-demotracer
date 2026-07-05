using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
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
    }

    private void ResetViewmodelAlignState(bool resetCounters = false)
    {
        RestoreAllReplayBotViewmodels();
    }

    private string FormatCrosshairStatusCounts()
        => $"crosshair_evidence={CountLoadedCrosshairEvidence()} crosshair_viewers={_viewerAppliedCrosshairCodes.Count}";

    private string FormatViewmodelStatusCounts()
        => $"viewmodel_evidence={CountLoadedViewmodelEvidence()} viewmodel_bots={_replayAppliedViewmodels.Count} viewmodel_failed={_replayFailedViewmodelSlots.Count}";

    private int CountLoadedCrosshairEvidence()
        => _loadedReplays.Values.Count(replay => !replay.UtilityOnly && HasCrosshairEvidence(replay.View));

    private int CountLoadedViewmodelEvidence()
        => _loadedReplays.Values.Count(replay => !replay.UtilityOnly && HasViewmodelEvidence(replay.View));

    private void UpdateReplayViewerCrosshairs(TickPlayerSnapshot playerSnapshot)
    {
        if (!_crosshairAlignEnabled)
        {
            RestoreAllReplayViewerCrosshairs();
            return;
        }

        var replayPawnSlots = BuildReplayPawnSlotMap(playerSnapshot);
        if (replayPawnSlots.Count == 0)
        {
            RestoreAllReplayViewerCrosshairs();
            return;
        }

        var activeViewers = new HashSet<int>();
        foreach (var viewer in playerSnapshot.Controllers)
        {
            if (viewer is not { IsValid: true } ||
                viewer.Slot < 0 ||
                viewer.IsBot ||
                _botHiderProbe.IsManagedBot(viewer.Slot))
            {
                continue;
            }

            if (!TryGetInEyeObserverTargetIndex(viewer, out var targetIndex) ||
                !replayPawnSlots.TryGetValue(targetIndex, out var replaySlot) ||
                !_loadedReplays.TryGetValue(replaySlot, out var replay) ||
                !HasCrosshairEvidence(replay.View))
            {
                continue;
            }

            activeViewers.Add(viewer.Slot);
            ApplyReplayViewerCrosshair(viewer, replay.View.CrosshairCode!);
        }

        foreach (var slot in _viewerAppliedCrosshairCodes.Keys.ToArray())
        {
            if (!activeViewers.Contains(slot))
                RestoreReplayViewerCrosshair(slot);
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

    private void UpdateReplayBotViewmodels(TickPlayerSnapshot playerSnapshot)
    {
        if (_loadedSlots.Count == 0)
        {
            RestoreAllReplayBotViewmodels();
            return;
        }

        var activeReplaySlots = new HashSet<int>();
        foreach (var slot in _loadedSlots)
        {
            if (slot is < 0 or >= MaxPlayerSlots ||
                !_lastPlayingSlots.Contains(slot) ||
                !_loadedReplays.TryGetValue(slot, out var replay) ||
                replay.UtilityOnly ||
                !HasViewmodelEvidence(replay.View))
            {
                continue;
            }

            if (!playerSnapshot.TryGetSlot(slot, out var replayBot) ||
                replayBot is not { IsValid: true, PawnIsAlive: true } ||
                !IsReplayTargetBot(replayBot, playerSnapshot.Controllers))
            {
                continue;
            }

            activeReplaySlots.Add(slot);
            ApplyReplayBotViewmodel(replayBot, replay.View.Viewmodel!);
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
        var slot = viewer.Slot;
        if (!_viewerOriginalCrosshairCodes.ContainsKey(slot))
            _viewerOriginalCrosshairCodes[slot] = NormalizeCrosshairCode(viewer.CrosshairCodes);

        if (_viewerAppliedCrosshairCodes.TryGetValue(slot, out var current) &&
            string.Equals(current, code, StringComparison.Ordinal))
        {
            return;
        }

        if (TryApplyCrosshairCodeToClient(viewer, code, "viewer"))
            _viewerAppliedCrosshairCodes[slot] = code;
    }

    private void RestoreAllReplayViewerCrosshairs()
    {
        foreach (var slot in _viewerAppliedCrosshairCodes.Keys.ToArray())
            RestoreReplayViewerCrosshair(slot);
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
    }

    private void RestoreReplayBotViewmodel(int slot)
    {
        _replayAppliedViewmodels.Remove(slot);
        _replayFailedViewmodelSlots.Remove(slot);
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
