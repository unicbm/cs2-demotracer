using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static ReplayPlayerScoreboard NormalizeReplayScoreboard(ReplayPlayerScoreboard? scoreboard)
    {
        if (scoreboard == null)
            return new ReplayPlayerScoreboard();

        return new ReplayPlayerScoreboard
        {
            Score = scoreboard.Score,
            Kills = NormalizeScoreboardCount(scoreboard.Kills),
            Deaths = NormalizeScoreboardCount(scoreboard.Deaths),
            Assists = NormalizeScoreboardCount(scoreboard.Assists),
            MVPs = NormalizeScoreboardCount(scoreboard.MVPs)
        };
    }

    private static int? NormalizeScoreboardCount(int? value)
    {
        if (!value.HasValue || value.Value < 0 || value.Value > 1000)
            return null;
        return value.Value;
    }

    private static bool HasScoreboardEvidence(ReplayPlayerScoreboard scoreboard)
        => scoreboard.Score.HasValue ||
           scoreboard.Kills.HasValue ||
           scoreboard.Deaths.HasValue ||
           scoreboard.Assists.HasValue ||
           scoreboard.MVPs.HasValue;

    private void ResetScoreboardAlignState(bool resetCounters = false)
    {
        _scoreboardSyncedSlots.Clear();
        if (resetCounters)
        {
            _scoreboardAppliedCount = 0;
            _scoreboardSkippedCount = 0;
        }
    }

    private string FormatScoreboardStatusCounts()
        => $"scoreboard_evidence={CountLoadedScoreboardEvidence()} scoreboard_applied={_scoreboardAppliedCount} scoreboard_skipped={_scoreboardSkippedCount}";

    private int CountLoadedScoreboardEvidence()
        => _loadedReplays.Values.Count(replay => !replay.UtilityOnly && HasScoreboardEvidence(replay.Scoreboard));

    private void ApplyLoadedReplayScoreboards()
    {
        if (!_scoreboardAlignEnabled)
            return;

        ApplyLoadedRoundScoreboard();
        foreach (var slot in _loadedSlots)
        {
            if (_scoreboardSyncedSlots.Contains(slot))
                continue;
            if (!_loadedReplays.TryGetValue(slot, out var replay) || replay.UtilityOnly)
                continue;
            ApplyReplayPlayerScoreboardForSlot(slot, replay.Scoreboard);
        }
    }

    private void ApplyLoadedRoundScoreboard()
    {
        var scoreboard = _loadedRoundScoreboard;
        if (scoreboard == null)
            return;

        var wroteAny = false;
        foreach (var team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
        {
            if (team is not { IsValid: true })
                continue;

            if ((int)team.TeamNum == 2)
            {
                team.Score = scoreboard.TScore;
                wroteAny = true;
                TrySetScoreboardStateChanged(team, "CTeam", "m_iScore");
            }
            else if ((int)team.TeamNum == 3)
            {
                team.Score = scoreboard.CtScore;
                wroteAny = true;
                TrySetScoreboardStateChanged(team, "CTeam", "m_iScore");
            }
        }

        if (!wroteAny)
            _scoreboardSkippedCount++;
    }

    private void ApplyReplayPlayerScoreboardForSlot(int slot, ReplayPlayerScoreboard scoreboard)
    {
        if (!HasScoreboardEvidence(scoreboard))
        {
            _scoreboardSyncedSlots.Add(slot);
            return;
        }

        if (!IsReplaySlotStillSafe(slot))
        {
            _scoreboardSkippedCount++;
            return;
        }

        var player = FindTeamPlayers().FirstOrDefault(candidate => candidate.Slot == slot);
        if (player is not { IsValid: true } || !IsReplayTargetBot(player))
        {
            _scoreboardSkippedCount++;
            return;
        }

        try
        {
            if (scoreboard.Score.HasValue)
            {
                player.Score = scoreboard.Score.Value;
                TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_iScore");
            }
            if (scoreboard.MVPs.HasValue)
            {
                player.MVPs = scoreboard.MVPs.Value;
                TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_iMVPs");
            }

            var tracking = player.ActionTrackingServices;
            if (tracking != null)
            {
                if (scoreboard.Kills.HasValue)
                    tracking.MatchStats.Kills = scoreboard.Kills.Value;
                if (scoreboard.Deaths.HasValue)
                    tracking.MatchStats.Deaths = scoreboard.Deaths.Value;
                if (scoreboard.Assists.HasValue)
                    tracking.MatchStats.Assists = scoreboard.Assists.Value;

                if (tracking.PerRoundStats.Count > 0)
                {
                    var roundStats = tracking.PerRoundStats[0];
                    if (scoreboard.Kills.HasValue)
                        roundStats.Kills = scoreboard.Kills.Value;
                    if (scoreboard.Deaths.HasValue)
                        roundStats.Deaths = scoreboard.Deaths.Value;
                    if (scoreboard.Assists.HasValue)
                        roundStats.Assists = scoreboard.Assists.Value;
                }

                TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_pActionTrackingServices");
            }

            _scoreboardSyncedSlots.Add(slot);
            _scoreboardAppliedCount++;
            Server.PrintToConsole(
                $"dtr: scoreboard align applied slot={slot} player={player.PlayerName} score={FormatScoreboardValue(scoreboard.Score)} k={FormatScoreboardValue(scoreboard.Kills)} d={FormatScoreboardValue(scoreboard.Deaths)} a={FormatScoreboardValue(scoreboard.Assists)} mvp={FormatScoreboardValue(scoreboard.MVPs)}");
        }
        catch (Exception ex)
        {
            _scoreboardSkippedCount++;
            Server.PrintToConsole($"dtr: scoreboard align skipped slot={slot}: {ex.Message}");
        }
    }

    private static string FormatScoreboardValue(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

    private static void TrySetScoreboardStateChanged(CBaseEntity entity, string className, string fieldName)
    {
        try
        {
            Utilities.SetStateChanged(entity, className, fieldName);
        }
        catch
        {
            // Scoreboard fields vary across game/CSS builds; direct writes are still useful for probing.
        }
    }
}
