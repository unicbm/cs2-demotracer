using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool _handlingReplayMusicKitMvp;

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnReplayRoundMvpMusicKit(EventRoundMvp @event, GameEventInfo info)
    {
        if (_handlingReplayMusicKitMvp)
            return HookResult.Continue;

        var player = @event.Userid;
        if (player is not { IsValid: true } ||
            !_loadedReplays.TryGetValue(player.Slot, out var replay) ||
            replay.MusicKitId <= 0 ||
            !IsReplaySlotStillSafe(player.Slot))
        {
            return HookResult.Continue;
        }

        info.DontBroadcast = true;
        _handlingReplayMusicKitMvp = true;
        EventRoundMvp? replayEvent = null;
        try
        {
            ApplyReplayMusicKit(player, replay.MusicKitId);
            replayEvent = new EventRoundMvp(true)
            {
                Userid = player,
                Musickitid = replay.MusicKitId,
                Nomusic = 0,
                Reason = @event.Reason,
                Value = @event.Value,
            };

            foreach (var human in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsHLTV && !p.IsBot))
            {
                try
                {
                    replayEvent.FireEventToClient(human);
                }
                catch
                {
                    // Ignore per-client send failures; the original event is already suppressed.
                }
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: music kit MVP event failed slot={player.Slot} kit={replay.MusicKitId}: {ex.Message}");
        }
        finally
        {
            try
            {
                replayEvent?.Free();
            }
            catch
            {
            }
            _handlingReplayMusicKitMvp = false;
        }

        return HookResult.Continue;
    }
}
