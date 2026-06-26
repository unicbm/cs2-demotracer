using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static ReplayView NormalizeReplayView(ReplayView? view)
    {
        return new ReplayView
        {
            CrosshairCode = NormalizeCrosshairCode(view?.CrosshairCode)
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

    private void ResetCrosshairAlignState(bool resetCounters = false)
    {
        RestoreAllReplayViewerCrosshairs();
    }

    private void ClearCrosshairAlignStateForLifecycle()
    {
        _viewerAppliedCrosshairCodes.Clear();
        _viewerOriginalCrosshairCodes.Clear();
    }

    private string FormatCrosshairStatusCounts()
        => $"crosshair_evidence={CountLoadedCrosshairEvidence()} crosshair_viewers={_viewerAppliedCrosshairCodes.Count}";

    private int CountLoadedCrosshairEvidence()
        => _loadedReplays.Values.Count(replay => !replay.UtilityOnly && HasCrosshairEvidence(replay.View));

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
