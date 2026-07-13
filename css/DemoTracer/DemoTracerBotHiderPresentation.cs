using System.Security.Cryptography;
using System.Text;
using CounterStrikeSharp.API;
using DemoTracerBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const float BotHiderLeaseHeartbeatSeconds = 1.0f;
    private const float BotHiderLeaseRetrySeconds = 1.0f;
    private readonly Dictionary<int, string> _companionCrosshairOverrides = new();
    private string _botHiderPresentationLeaseToken = string.Empty;
    private string _botHiderPresentationSignature = string.Empty;
    private string _lastBotHiderPresentationError = string.Empty;
    private float _nextBotHiderLeaseHeartbeatAt;
    private float _nextBotHiderLeaseRetryAt;
    private int _botHiderPresentationTransitionDepth;

    private void BeginBotHiderPresentationTransition()
    {
        _botHiderPresentationTransitionDepth++;
    }

    private void EndBotHiderPresentationTransition()
    {
        if (_botHiderPresentationTransitionDepth <= 0)
            return;
        if (--_botHiderPresentationTransitionDepth > 0)
            return;

        _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void EnsureBotHiderPresentationLease()
    {
        if (_botHiderPresentationTransitionDepth > 0)
            return;

        if (_loadedSlots.Count == 0 && _companionCrosshairOverrides.Count == 0)
        {
            ReleaseBotHiderPresentationLease("no_overrides");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) &&
            Server.CurrentTime >= _nextBotHiderLeaseHeartbeatAt)
        {
            _nextBotHiderLeaseHeartbeatAt = Server.CurrentTime + BotHiderLeaseHeartbeatSeconds;
            if (!_botHiderBridge.Heartbeat(_botHiderPresentationLeaseToken))
            {
                _botHiderPresentationLeaseToken = string.Empty;
                _botHiderPresentationSignature = string.Empty;
                _nextBotHiderLeaseRetryAt = Server.CurrentTime;
            }
        }

        if (string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) &&
            Server.CurrentTime >= _nextBotHiderLeaseRetryAt)
        {
            SyncBotHiderPresentationLease(announce: false);
        }
    }

    private bool SyncBotHiderPresentationLease(bool announce)
    {
        if (_botHiderPresentationTransitionDepth > 0)
            return true;

        var wantsPresentation = WantsBotHiderPresentationLease();
        if (!wantsPresentation)
        {
            ReleaseBotHiderPresentationLease("no_requested_overrides");
            return true;
        }
        if (!_botHiderBridge.IsAvailable())
        {
            ReleaseBotHiderPresentationLease("provider_unavailable");
            _nextBotHiderLeaseRetryAt = Server.CurrentTime + BotHiderLeaseRetrySeconds;
            ReportBotHiderPresentationError("provider_unavailable", announce);
            return false;
        }

        var requests = BuildBotHiderPresentationOverrides(out var signature);
        if (requests.Length == 0)
        {
            ReleaseBotHiderPresentationLease("no_managed_override_slots");
            _nextBotHiderLeaseRetryAt = Server.CurrentTime + BotHiderLeaseRetrySeconds;
            ReportBotHiderPresentationError("no_managed_override_slots", announce);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) &&
            signature.Equals(_botHiderPresentationSignature, StringComparison.Ordinal))
        {
            _ = _botHiderBridge.Heartbeat(_botHiderPresentationLeaseToken);
            _nextBotHiderLeaseHeartbeatAt = Server.CurrentTime + BotHiderLeaseHeartbeatSeconds;
            return true;
        }

        BotHiderPresentationLeaseResult result;
        if (string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken))
        {
            result = _botHiderBridge.Acquire(
                DemoTracerBotHiderContract.DemoTracerOwner,
                requests);
            if (!result.Ok && result.Reason.StartsWith("slot_leased:", StringComparison.Ordinal))
            {
                _ = _botHiderBridge.ReleaseOwner(DemoTracerBotHiderContract.DemoTracerOwner);
                result = _botHiderBridge.Acquire(
                    DemoTracerBotHiderContract.DemoTracerOwner,
                    requests);
            }
        }
        else
        {
            result = _botHiderBridge.Replace(_botHiderPresentationLeaseToken, requests);
            if (!result.Ok)
            {
                _botHiderPresentationLeaseToken = string.Empty;
                _botHiderPresentationSignature = string.Empty;
                result = _botHiderBridge.Acquire(
                    DemoTracerBotHiderContract.DemoTracerOwner,
                    requests);
            }
        }

        if (!result.Ok)
        {
            _nextBotHiderLeaseRetryAt = Server.CurrentTime + BotHiderLeaseRetrySeconds;
            ReportBotHiderPresentationError(result.Reason, announce);
            return false;
        }

        _botHiderPresentationLeaseToken = result.LeaseToken;
        _botHiderPresentationSignature = signature;
        _lastBotHiderPresentationError = string.Empty;
        _nextBotHiderLeaseHeartbeatAt = Server.CurrentTime + BotHiderLeaseHeartbeatSeconds;
        _nextBotHiderLeaseRetryAt = 0.0f;
        if (announce)
        {
            Server.PrintToConsole(
                $"dtr: BotHider presentation lease active slots={string.Join(',', result.Slots)} " +
                $"provider_epoch={result.ProviderEpoch}");
        }
        return true;
    }

    private bool WantsBotHiderPresentationLease()
    {
        if (_companionCrosshairOverrides.Count > 0)
            return true;

        foreach (var slot in _loadedSlots)
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay))
            {
                continue;
            }

            if (_replayIdentityMode != ReplayIdentityMode.Off ||
                (_crosshairAlignEnabled && HasCrosshairEvidence(replay.View)))
            {
                return true;
            }
        }

        return false;
    }

    private void ReportBotHiderPresentationError(string reason, bool announce)
    {
        if (announce || !_lastBotHiderPresentationError.Equals(reason, StringComparison.Ordinal))
        {
            Server.PrintToConsole(
                $"dtr: BotHider presentation lease unavailable: {reason}");
        }
        _lastBotHiderPresentationError = reason;
    }

    private BotHiderPresentationOverride[] BuildBotHiderPresentationOverrides(out string signature)
    {
        var bySlot = new Dictionary<int, BotHiderPresentationOverride>();
        foreach (var slot in _loadedSlots.ToArray())
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay) ||
                !IsReplaySlotStillSafe(slot) ||
                !_botHiderBridge.TryGetManagedSlot(slot, out var managed))
            {
                continue;
            }

            var playerName = _replayIdentityMode == ReplayIdentityMode.Off ||
                             string.IsNullOrWhiteSpace(replay.PlayerName)
                ? null
                : replay.PlayerName;
            ulong? steamId = _replayIdentityMode is ReplayIdentityMode.Steam or ReplayIdentityMode.Avatar &&
                             replay.SteamId != 0
                ? replay.SteamId
                : null;
            uint? flair = ReplayIdentityShouldApplyScoreboardFlair() && replay.ScoreboardFlair != null
                ? replay.ScoreboardFlair.ItemDefIndex
                : null;
            string? crosshair = null;
            if (_companionCrosshairOverrides.TryGetValue(slot, out var companionCrosshair))
                crosshair = companionCrosshair;
            else if (_crosshairAlignEnabled && HasCrosshairEvidence(replay.View))
                crosshair = replay.View.CrosshairCode;

            if (playerName == null && !steamId.HasValue && !flair.HasValue && crosshair == null)
                continue;

            bySlot[slot] = new BotHiderPresentationOverride
            {
                Slot = slot,
                Incarnation = managed.Incarnation,
                PlayerName = playerName,
                SteamId = steamId,
                ScoreboardFlair = flair,
                CrosshairCode = crosshair
            };
        }

        foreach (var pair in _companionCrosshairOverrides.ToArray())
        {
            var slot = pair.Key;
            if (bySlot.ContainsKey(slot) ||
                !IsReplaySlotStillSafe(slot) ||
                !_botHiderBridge.TryGetManagedSlot(slot, out var managed))
            {
                continue;
            }

            bySlot[slot] = new BotHiderPresentationOverride
            {
                Slot = slot,
                Incarnation = managed.Incarnation,
                CrosshairCode = pair.Value
            };
        }

        var requests = bySlot.Values.OrderBy(request => request.Slot).ToArray();
        var signatureBuilder = new StringBuilder();
        var provider = _botHiderBridge.GetProviderInfo();
        signatureBuilder.Append(provider?.ProviderEpoch).Append(':').Append(provider?.MapEpoch).Append('|');
        foreach (var request in requests)
        {
            signatureBuilder
                .Append(request.Slot).Append(':')
                .Append(request.Incarnation).Append(':')
                .Append(request.PlayerName).Append(':')
                .Append(request.SteamId).Append(':')
                .Append(request.ScoreboardFlair).Append(':')
                .Append(request.CrosshairCode).Append('|');
        }
        signature = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(signatureBuilder.ToString())));
        return requests;
    }

    private void ReleaseBotHiderPresentationLease(string reason)
    {
        if (_botHiderPresentationTransitionDepth > 0)
            return;

        var token = _botHiderPresentationLeaseToken;
        _botHiderPresentationLeaseToken = string.Empty;
        _botHiderPresentationSignature = string.Empty;
        _nextBotHiderLeaseHeartbeatAt = 0.0f;
        _nextBotHiderLeaseRetryAt = 0.0f;
        if (string.IsNullOrWhiteSpace(token))
            return;

        if (!_botHiderBridge.Release(token))
            _ = _botHiderBridge.ReleaseOwner(DemoTracerBotHiderContract.DemoTracerOwner);
        Server.PrintToConsole($"dtr: BotHider presentation lease released reason={reason}");
    }

    private int CountActiveBotHiderCrosshairOverrides()
    {
        var slots = new HashSet<int>(_companionCrosshairOverrides.Keys);
        if (_crosshairAlignEnabled)
        {
            foreach (var pair in _loadedReplays)
            {
                if (HasCrosshairEvidence(pair.Value.View))
                    slots.Add(pair.Key);
            }
        }
        return slots.Count;
    }
}
