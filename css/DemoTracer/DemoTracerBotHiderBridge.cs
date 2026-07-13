using CounterStrikeSharp.API.Core.Capabilities;
using DemoTracerBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class DemoTracerBotHiderBridge
    {
        private static readonly PluginCapability<IBotHiderApi> Capability =
            new(DemoTracerBotHiderContract.Capability);

        private IBotHiderApi? _api;

        public void Refresh()
            => _api = null;

        public bool IsAvailable()
            => TryGetApi(out _);

        public bool IsManagedBot(int slot)
        {
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.IsManagedBot(slot);
            }
            catch
            {
                _api = null;
                return false;
            }
        }

        public bool TryGetManagedSlot(int slot, out BotHiderManagedSlot state)
        {
            state = new BotHiderManagedSlot { Slot = slot };
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.TryGetManagedSlot(slot, out state);
            }
            catch
            {
                _api = null;
                return false;
            }
        }

        public BotHiderPresentationLeaseResult Acquire(
            string owner,
            BotHiderPresentationOverride[] overrides)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.AcquirePresentationLease(owner, overrides);
            }
            catch (Exception ex)
            {
                _api = null;
                return Fail($"provider_error:{ex.Message}");
            }
        }

        public BotHiderPresentationLeaseResult Replace(
            string leaseToken,
            BotHiderPresentationOverride[] overrides)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.ReplacePresentationLease(leaseToken, overrides);
            }
            catch (Exception ex)
            {
                _api = null;
                return Fail($"provider_error:{ex.Message}");
            }
        }

        public bool Heartbeat(string leaseToken)
        {
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.HeartbeatPresentationLease(leaseToken);
            }
            catch
            {
                _api = null;
                return false;
            }
        }

        public bool Release(string leaseToken)
        {
            if (string.IsNullOrWhiteSpace(leaseToken))
                return true;
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.ReleasePresentationLease(leaseToken);
            }
            catch
            {
                _api = null;
                return false;
            }
        }

        public int ReleaseOwner(string owner)
        {
            if (!TryGetApi(out var api))
                return 0;
            try
            {
                return api.ReleasePresentationLeasesByOwner(owner);
            }
            catch
            {
                _api = null;
                return 0;
            }
        }

        public BotHiderProviderInfo? GetProviderInfo()
        {
            if (!TryGetApi(out var api))
                return null;
            try
            {
                return api.GetProviderInfo();
            }
            catch
            {
                _api = null;
                return null;
            }
        }

        public BotHiderDiagnostics? GetDiagnostics()
        {
            if (!TryGetApi(out var api))
                return null;
            try
            {
                return api.GetDiagnostics();
            }
            catch
            {
                _api = null;
                return null;
            }
        }

        private bool TryGetApi(out IBotHiderApi api)
        {
            if (_api != null && ProviderIsUsable(_api))
            {
                api = _api;
                return true;
            }

            _api = null;
            try
            {
                var candidate = Capability.Get();
                if (candidate != null && ProviderIsUsable(candidate))
                    _api = candidate;
            }
            catch
            {
                _api = null;
            }

            api = _api!;
            return api != null;
        }

        private static bool ProviderIsUsable(IBotHiderApi api)
        {
            try
            {
                var provider = api.GetProviderInfo();
                return api.ApiVersion == DemoTracerBotHiderContract.ApiVersion &&
                       provider.ApiVersion == DemoTracerBotHiderContract.ApiVersion &&
                       provider.Connected &&
                       !provider.Draining;
            }
            catch
            {
                return false;
            }
        }

        private static BotHiderPresentationLeaseResult Fail(string reason)
            => new()
            {
                Ok = false,
                Reason = reason
            };
    }
}
