using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static bool ReplayBotSeesEnemy(int slot, out string contactReason)
    {
        return ReplayBotSeesEnemy(slot, FindTeamPlayers(), out contactReason, out _);
    }

    private static bool ReplayBotSeesEnemy(
        int slot,
        IReadOnlyList<CCSPlayerController> teamPlayers,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot == null || !HasLivePawn(bot))
            return false;

        foreach (var enemy in teamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy))
                continue;

            if (PlayerSeesTarget(bot, enemy, out contactReason))
            {
                contactSlot = enemy.Slot;
                return true;
            }
        }

        return false;
    }

    private static bool ReplayBotSeesEnemy(
        int slot,
        TickPlayerSnapshot playerSnapshot,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        if (!playerSnapshot.TryGetSlot(slot, out var bot) || !HasLivePawn(bot))
            return false;

        foreach (var enemy in playerSnapshot.TeamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy))
                continue;

            if (PlayerSeesTarget(bot, enemy, out contactReason))
            {
                contactSlot = enemy.Slot;
                return true;
            }
        }

        return false;
    }

    private bool ReplayBotHasContact(
        int slot,
        IReadOnlyList<CCSPlayerController> teamPlayers,
        out string contactReason,
        out int contactSlot)
    {
        if (ReplayBotSeesEnemy(slot, teamPlayers, out contactReason, out contactSlot))
            return true;

        if (_handoffThreat360Enabled &&
            ReplayBotHasNearby360Threat(slot, teamPlayers, out contactReason, out contactSlot))
            return true;

        contactSlot = -1;
        return false;
    }

    private bool ReplayBotHasContact(
        int slot,
        TickPlayerSnapshot playerSnapshot,
        out string contactReason,
        out int contactSlot)
    {
        if (ReplayBotSeesEnemy(slot, playerSnapshot, out contactReason, out contactSlot))
            return true;

        if (_handoffThreat360Enabled &&
            ReplayBotHasNearby360Threat(slot, playerSnapshot, out contactReason, out contactSlot))
            return true;

        contactSlot = -1;
        return false;
    }

    private bool ReplayBotHasNearby360Threat(
        int slot,
        IReadOnlyList<CCSPlayerController> teamPlayers,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot == null ||
            !HasLivePawn(bot) ||
            !TryGetPawnOrigin(bot, out var botOrigin))
        {
            _pendingThreat360.Remove(slot);
            return false;
        }

        var rangeSq = _handoffThreat360Range * _handoffThreat360Range;
        var bestEnemySlot = -1;
        var bestDistanceSq = float.MaxValue;

        foreach (var enemy in teamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy) ||
                !IsHandoff360ThreatActor(enemy) ||
                !TryGetPawnOrigin(enemy, out var enemyOrigin))
                continue;

            var dz = MathF.Abs(enemyOrigin.Z - botOrigin.Z);
            if (dz > HandoffThreat360MaxVerticalDelta)
                continue;

            var dx = enemyOrigin.X - botOrigin.X;
            var dy = enemyOrigin.Y - botOrigin.Y;
            var distanceSq = dx * dx + dy * dy;
            if (distanceSq > rangeSq || distanceSq >= bestDistanceSq)
                continue;
            if (_handoffThreat360LosEnabled && !HasHandoff360LineOfSight(bot, enemy))
                continue;

            bestEnemySlot = enemy.Slot;
            bestDistanceSq = distanceSq;
        }

        if (bestEnemySlot < 0)
        {
            _pendingThreat360.Remove(slot);
            return false;
        }

        var distance = MathF.Sqrt(bestDistanceSq);
        if (distance <= MathF.Min(HandoffThreat360ImmediateRange, _handoffThreat360Range))
        {
            _pendingThreat360.Remove(slot);
            contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: true);
            contactSlot = bestEnemySlot;
            return true;
        }

        var now = Server.CurrentTime;
        if (!_pendingThreat360.TryGetValue(slot, out var pending) ||
            pending.EnemySlot != bestEnemySlot)
        {
            _pendingThreat360[slot] = new PendingThreat360(bestEnemySlot, now);
            return false;
        }

        if (now - pending.FirstSeenAt < HandoffThreat360HoldSeconds)
            return false;

        _pendingThreat360.Remove(slot);
        contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: false);
        contactSlot = bestEnemySlot;
        return true;
    }

    private bool ReplayBotHasNearby360Threat(
        int slot,
        TickPlayerSnapshot playerSnapshot,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        if (!playerSnapshot.TryGetSlot(slot, out var bot) ||
            !HasLivePawn(bot) ||
            !TryGetPawnOrigin(bot, out var botOrigin))
        {
            _pendingThreat360.Remove(slot);
            return false;
        }

        var rangeSq = _handoffThreat360Range * _handoffThreat360Range;
        var bestEnemySlot = -1;
        var bestDistanceSq = float.MaxValue;

        foreach (var enemy in playerSnapshot.TeamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy) ||
                !IsHandoff360ThreatActor(enemy, playerSnapshot) ||
                !TryGetPawnOrigin(enemy, out var enemyOrigin))
                continue;

            var dz = MathF.Abs(enemyOrigin.Z - botOrigin.Z);
            if (dz > HandoffThreat360MaxVerticalDelta)
                continue;

            var dx = enemyOrigin.X - botOrigin.X;
            var dy = enemyOrigin.Y - botOrigin.Y;
            var distanceSq = dx * dx + dy * dy;
            if (distanceSq > rangeSq || distanceSq >= bestDistanceSq)
                continue;
            if (_handoffThreat360LosEnabled && !HasHandoff360LineOfSight(bot, enemy))
                continue;

            bestEnemySlot = enemy.Slot;
            bestDistanceSq = distanceSq;
        }

        if (bestEnemySlot < 0)
        {
            _pendingThreat360.Remove(slot);
            return false;
        }

        var distance = MathF.Sqrt(bestDistanceSq);
        if (distance <= MathF.Min(HandoffThreat360ImmediateRange, _handoffThreat360Range))
        {
            _pendingThreat360.Remove(slot);
            contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: true);
            contactSlot = bestEnemySlot;
            return true;
        }

        var now = Server.CurrentTime;
        if (!_pendingThreat360.TryGetValue(slot, out var pending) ||
            pending.EnemySlot != bestEnemySlot)
        {
            _pendingThreat360[slot] = new PendingThreat360(bestEnemySlot, now);
            return false;
        }

        if (now - pending.FirstSeenAt < HandoffThreat360HoldSeconds)
            return false;

        _pendingThreat360.Remove(slot);
        contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: false);
        contactSlot = bestEnemySlot;
        return true;
    }

    private bool IsHandoff360ThreatActor(CCSPlayerController enemy)
    {
        if (enemy is not { IsValid: true } || enemy.Slot < 0)
            return false;
        if (_loadedSlots.Contains(enemy.Slot) || IsReplaySlotPlaying(enemy.Slot))
            return false;
        if (_botHiderProbe.IsManagedBot(enemy.Slot))
            return false;
        var controllingBot = TryGetControllingBotState(enemy, out var controlsBot) && controlsBot;
        return !enemy.IsBot || controllingBot;
    }

    private bool IsHandoff360ThreatActor(
        CCSPlayerController enemy,
        TickPlayerSnapshot playerSnapshot)
    {
        if (enemy is not { IsValid: true } || enemy.Slot < 0)
            return false;
        if (_loadedSlots.Contains(enemy.Slot) || _lastPlayingSlots.Contains(enemy.Slot))
            return false;
        if (_botHiderProbe.IsManagedBot(enemy.Slot))
            return false;
        var controllingBot = TryGetControllingBotState(enemy, out var controlsBot) && controlsBot;
        return !enemy.IsBot || controllingBot;
    }

    private bool HasHandoff360LineOfSight(CCSPlayerController bot, CCSPlayerController enemy)
    {
        if (!TryGetPawnEyePosition(enemy, out var enemyEye) ||
            !TryGetPawnEyePosition(bot, out var botEye) ||
            !TryGetPawnPoint(bot, HandoffThreat360ChestZScale, out var botChest))
            return false;

        if (!_rayTraceLosProbe.TryIsWorldLineClear(enemyEye, botEye, out var eyeClear))
            return true;
        if (eyeClear)
            return true;

        return _rayTraceLosProbe.TryIsWorldLineClear(enemyEye, botChest, out var chestClear) && chestClear;
    }

    private static bool HasLivePawn(CCSPlayerController? player)
    {
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true } pawn)
            return false;

        if (player.PawnIsAlive)
            return true;

        try
        {
            return pawn.Value.Health > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetPawnOrigin(CCSPlayerController player, out Vector origin)
    {
        origin = new Vector(0.0f, 0.0f, 0.0f);
        if (player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;
        var value = player.PlayerPawn.Value.AbsOrigin;
        if (value == null)
            return false;
        origin = value;
        return true;
    }

    private static bool TryGetPawnEyePosition(CCSPlayerController player, out Vector eye)
    {
        return TryGetPawnPoint(player, 1.0f, out eye);
    }

    private static bool TryGetPawnPoint(CCSPlayerController player, float viewOffsetScale, out Vector point)
    {
        point = new Vector(0.0f, 0.0f, 0.0f);
        if (player.PlayerPawn is not { IsValid: true, Value.IsValid: true } pawn)
            return false;
        var origin = pawn.Value.AbsOrigin;
        var viewOffset = pawn.Value.ViewOffset;
        if (origin == null || viewOffset == null)
            return false;
        point = new Vector(
            origin.X + viewOffset.X,
            origin.Y + viewOffset.Y,
            origin.Z + viewOffset.Z * viewOffsetScale);
        return true;
    }

    private static string FormatThreat360Reason(int enemySlot, float distance, bool immediate)
    {
        var kind = immediate ? "near360" : "near360_held";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{kind}_enemy{enemySlot}_dist{distance:F0}");
    }

    private static bool PlayerSeesTarget(
        CCSPlayerController observer,
        CCSPlayerController target,
        out string contactReason)
    {
        contactReason = string.Empty;
        if (observer.Slot < 0)
            return false;
        if (target.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        try
        {
            var spotted = target.PlayerPawn.Value.EntitySpottedState;
            if (spotted != null)
            {
                var mask = spotted.SpottedByMask;
                var word = observer.Slot / 32;
                var bit = observer.Slot % 32;
                if (word >= 0 && word < mask.Length && (mask[word] & (1u << bit)) != 0)
                {
                    contactReason = "spotted";
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
