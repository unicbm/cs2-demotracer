using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer;

internal static class ReplayRetentionPriorityParser
{
    internal const int MaxPlayersPerTeam = 5;

    internal static bool TryParseGroup(string value, out ulong[] steamIds, out string error)
    {
        steamIds = [];
        error = string.Empty;
        var normalized = value.Trim();
        if (normalized is "-" || normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;

        var parts = normalized.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > MaxPlayersPerTeam)
        {
            error = $"a retention group must contain 1-{MaxPlayersPerTeam} SteamID64 values";
            return false;
        }

        var parsed = new ulong[parts.Length];
        var seen = new HashSet<ulong>();
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length != 17 ||
                !ulong.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) ||
                steamId == 0)
            {
                error = $"invalid SteamID64 \"{parts[index]}\"";
                return false;
            }
            if (!seen.Add(steamId))
            {
                error = $"duplicate SteamID64 {steamId.ToString(CultureInfo.InvariantCulture)}";
                return false;
            }
            parsed[index] = steamId;
        }

        steamIds = parsed;
        return true;
    }

    internal static int[] SelectPreferredIndices(IReadOnlyList<int> ranks, int availableSlots)
    {
        var count = Math.Min(Math.Max(availableSlots, 0), ranks.Count);
        if (count == ranks.Count)
            return Enumerable.Range(0, count).ToArray();
        return Enumerable.Range(0, ranks.Count)
            .OrderBy(index => ranks[index])
            .ThenBy(index => index)
            .Take(count)
            .Order()
            .ToArray();
    }
}

public sealed partial class DemoTracerPlugin
{
    private readonly Dictionary<ulong, int> _pendingReplayRetentionRanks = new();
    private readonly Dictionary<ulong, int> _activeReplayRetentionRanks = new();
    private readonly HashSet<int> _pendingHumanTeamChangeSlots = new();

    [ConsoleCommand("dtr_retain", "dtr_retain <team-a-steamids> <team-b-steamids>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RetainCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount == 2 && command.GetArg(1).Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _pendingReplayRetentionRanks.Clear();
            command.ReplyToCommand("[DTR OK] pending replay retention priority cleared");
            return;
        }
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_retain <team-a-steamids> <team-b-steamids>");
            command.ReplyToCommand("example: dtr_retain 76561198000000001,76561198000000002 76561198000000003,76561198000000004");
            return;
        }
        if (!ReplayRetentionPriorityParser.TryParseGroup(command.GetArg(1), out var firstGroup, out var firstError))
        {
            command.ReplyToCommand($"[DTR ERR] team A retention priority: {firstError}");
            return;
        }
        if (!ReplayRetentionPriorityParser.TryParseGroup(command.GetArg(2), out var secondGroup, out var secondError))
        {
            command.ReplyToCommand($"[DTR ERR] team B retention priority: {secondError}");
            return;
        }

        var next = new Dictionary<ulong, int>();
        if (!TryAddRetentionGroup(next, firstGroup, out var duplicate) ||
            !TryAddRetentionGroup(next, secondGroup, out duplicate))
        {
            command.ReplyToCommand($"[DTR ERR] SteamID64 {duplicate.ToString(CultureInfo.InvariantCulture)} appears in both retention groups");
            return;
        }

        _pendingReplayRetentionRanks.Clear();
        foreach (var pair in next)
            _pendingReplayRetentionRanks[pair.Key] = pair.Value;
        command.ReplyToCommand(
            $"[DTR OK] retention priority queued for the next manifest plan ({next.Count} players)");
    }

    private static bool TryAddRetentionGroup(
        IDictionary<ulong, int> destination,
        IReadOnlyList<ulong> steamIds,
        out ulong duplicate)
    {
        for (var index = 0; index < steamIds.Count; index++)
        {
            var steamId = steamIds[index];
            if (destination.ContainsKey(steamId))
            {
                duplicate = steamId;
                return false;
            }
            destination[steamId] = index + 1;
        }

        duplicate = 0;
        return true;
    }

    private void ActivatePendingReplayRetentionPriority()
    {
        _activeReplayRetentionRanks.Clear();
        foreach (var pair in _pendingReplayRetentionRanks)
            _activeReplayRetentionRanks[pair.Key] = pair.Value;
        _pendingReplayRetentionRanks.Clear();
    }

    private void ClearReplayRetentionPriority(bool clearPending)
    {
        _activeReplayRetentionRanks.Clear();
        if (clearPending)
            _pendingReplayRetentionRanks.Clear();
        _pendingHumanTeamChangeSlots.Clear();
    }

    private int ResolveReplayRetentionRank(ulong steamId, int fallbackRank)
    {
        if (steamId != 0 && _activeReplayRetentionRanks.TryGetValue(steamId, out var rank))
            return rank;
        return Math.Clamp(fallbackRank, 1, ReplayRetentionPriorityParser.MaxPlayersPerTeam);
    }

    private void RegisterReplayRetentionJoinHook()
        => AddCommandListener("jointeam", OnJoinTeamForReplayRetention, HookMode.Pre);

    private void UnregisterReplayRetentionJoinHook()
        => RemoveCommandListener("jointeam", OnJoinTeamForReplayRetention, HookMode.Pre);

    private HookResult OnJoinTeamForReplayRetention(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } ||
            player.IsBot ||
            _botHiderBridge.IsManagedBot(player.Slot) ||
            command.ArgCount < 2 ||
            !TryParseJoinTeam(command.GetArg(1), out var destination) ||
            player.Team == destination)
        {
            return HookResult.Continue;
        }

        if (_pendingHumanTeamChangeSlots.Contains(player.Slot))
            return HookResult.Handled;

        var snapshot = BuildTickPlayerSnapshot();
        // Team capacity is controller-based. Requiring a live pawn undercounts
        // bots while they are dead or between CT/T spawn transitions, which can
        // leave the engine reporting a full team after this hook continued.
        var destinationPlayers = snapshot.Controllers.Count(candidate =>
            candidate is { IsValid: true } &&
            candidate.UserId.HasValue &&
            candidate.Team == destination);
        if (destinationPlayers < StandardTeamSize)
            return HookResult.Continue;

        var candidates = BuildKickCandidates(snapshot)
            .Where(candidate => candidate.Team == destination && candidate.UserId.HasValue)
            .OrderByDescending(candidate => candidate.RetentionRank)
            .ThenByDescending(candidate => candidate.Slot)
            .ToList();
        if (candidates.Count == 0)
            return HookResult.Continue;

        var evicted = candidates[0];
        if (!TryReleaseAndKickReplayCandidate(evicted, "human_join_retention", out _, out _))
            return HookResult.Continue;

        var joiningSlot = player.Slot;
        var joiningUserId = player.UserId;
        _pendingHumanTeamChangeSlots.Add(joiningSlot);
        Server.NextFrame(() => CompleteRetainedHumanTeamChange(joiningSlot, joiningUserId, destination));
        Server.PrintToConsole(
            $"dtr: retained human join team={destination}; released replay slot={evicted.Slot} keep_rank={evicted.RetentionRank}");
        return HookResult.Handled;
    }

    private void CompleteRetainedHumanTeamChange(int slot, int? expectedUserId, CsTeam destination)
    {
        _pendingHumanTeamChangeSlots.Remove(slot);
        var current = Utilities.GetPlayerFromSlot(slot);
        if (current is not { IsValid: true } ||
            current.IsBot ||
            current.UserId != expectedUserId ||
            current.Team == destination)
        {
            return;
        }

        try
        {
            current.ChangeTeam(destination);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: retained human join failed slot={slot} team={destination}: {ex.Message}");
        }
    }

    private static bool TryParseJoinTeam(string value, out CsTeam team)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "2":
            case "t":
            case "terrorist":
                team = CsTeam.Terrorist;
                return true;
            case "3":
            case "ct":
            case "counterterrorist":
                team = CsTeam.CounterTerrorist;
                return true;
            default:
                team = CsTeam.None;
                return false;
        }
    }
}
