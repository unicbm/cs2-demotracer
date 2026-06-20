using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private void HandoffActiveReplays(string reason, int triggerSlot = -1)
    {
        if (triggerSlot < 0 && !_handoffAllSlots)
            return;

        var stopped = 0;
        var slots = (!_handoffAllSlots && triggerSlot >= 0)
            ? [triggerSlot]
            : _loadedSlots.ToArray();
        foreach (var slot in slots)
        {
            if (!BotControllerNative.GetReplayState(slot).Playing)
                continue;

            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason);
            stopped++;

            if (!_handoffAllSlots)
                break;
        }

        if (stopped > 0)
            Server.PrintToConsole($"dtr: handoff stopped {stopped} replay slot(s), reason={reason}");
    }

    private int GetDeathHandoffSlot(EventPlayerDeath @event)
    {
        if (@event.Userid is { IsValid: true } victim && IsReplaySlotPlaying(victim.Slot))
            return victim.Slot;
        if (@event.Attacker is { IsValid: true } attacker && IsReplaySlotPlaying(attacker.Slot))
            return attacker.Slot;
        return -1;
    }

    private bool TryGetEnemyBulletHandoffPair(
        CCSPlayerController? attacker,
        CCSPlayerController? victim,
        out int victimSlot,
        out int attackerSlot)
    {
        victimSlot = -1;
        attackerSlot = -1;

        if (attacker is not { IsValid: true } ||
            victim is not { IsValid: true } ||
            attacker.Slot == victim.Slot ||
            attacker.Team == victim.Team ||
            !victim.PawnIsAlive ||
            !attacker.PawnIsAlive)
            return false;

        if (!IsReplaySlotPlaying(victim.Slot) || !ReplayHasPassedHandoffGrace(victim.Slot))
            return false;

        victimSlot = victim.Slot;
        attackerSlot = attacker.Slot;
        return true;
    }

    private bool TryHandoffBulletDamagedReplay(int victimSlot, int attackerSlot, int damage)
    {
        if (damage < BulletHandoffMinDamage ||
            !IsReplaySlotPlaying(victimSlot) ||
            !ReplayHasPassedHandoffGrace(victimSlot))
            return false;

        HandoffActiveReplays(
            $"bullet_damage_slot{victimSlot}_attacker{attackerSlot}_dmg{damage}",
            victimSlot);
        return true;
    }

    private void PruneExpiredBulletHandoffState()
    {
        if (_pendingBulletHits.Count == 0 && _pendingBulletDamages.Count == 0)
            return;

        foreach (var (slot, hit) in _pendingBulletHits.ToArray())
        {
            if (!IsFreshBulletHandoffEvent(hit.Time))
                _pendingBulletHits.Remove(slot);
        }

        foreach (var (slot, damage) in _pendingBulletDamages.ToArray())
        {
            if (!IsFreshBulletHandoffEvent(damage.Time))
                _pendingBulletDamages.Remove(slot);
        }
    }

    private static bool IsFreshBulletHandoffEvent(float eventTime)
        => Server.CurrentTime - eventTime <= BulletHandoffMatchSeconds;

    private static bool IsReplaySlotPlaying(int slot)
    {
        return slot >= 0 && BotControllerNative.GetReplayState(slot).Playing;
    }

    private bool ReplayHasPassedHandoffGrace(int slot)
    {
        return !_replayStartedAt.TryGetValue(slot, out var startedAt) ||
               Server.CurrentTime - startedAt >= HandoffGraceSeconds;
    }

    private static void ResetBotBrainForHandoff(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        var pawn = player.PlayerPawn.Value;
        var bot = pawn.Bot;
        if (bot == null)
            return;

        ref bool isAttacking = ref bot.IsAttacking;
        isAttacking = false;

        ref bool isCrouching = ref bot.IsCrouching;
        isCrouching = false;

        ref bool eyeAnglesUnderPathFinderControl = ref bot.EyeAnglesUnderPathFinderControl;
        eyeAnglesUnderPathFinderControl = false;

        ref float fireWeaponTimestamp = ref bot.FireWeaponTimestamp;
        fireWeaponTimestamp = 0f;

        ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
        inhibitLookAroundTimestamp = 0f;

        ref int checkedHidingSpotCount = ref bot.CheckedHidingSpotCount;
        checkedHidingSpotCount = 0;

        ref float lookAroundStateTimestamp = ref bot.LookAroundStateTimestamp;
        lookAroundStateTimestamp = 0f;

        var ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
        ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
        ignoreDuration = 0f;
        ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
        ignoreTimestamp = 0f;
        ref float ignoreTimescale = ref ignoreEnemiesTimer.Timescale;
        ignoreTimescale = 1f;

        var panicTimer = bot.PanicTimer;
        ref float panicDuration = ref panicTimer.Duration;
        panicDuration = 0f;
        ref float panicTimestamp = ref panicTimer.Timestamp;
        panicTimestamp = 0f;
        ref float panicTimescale = ref panicTimer.Timescale;
        panicTimescale = 1f;
    }
}
