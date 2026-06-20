using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    [ConsoleCommand("dtr_moment", "dtr_moment <manifest.json> <source_round> <bomb|seconds|bomb+seconds> <player_name|steamid> [human_slot] [loop:0|1]")]
    public void MomentCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 5)
        {
            command.ReplyToCommand("usage: dtr_moment <manifest.json> <source_round> <bomb|seconds|bomb+seconds> <player_name|steamid> [human_slot] [loop:0|1]");
            return;
        }

        var manifestPath = command.GetArg(1);
        if (!int.TryParse(command.GetArg(2), out var round) || round < 0)
        {
            command.ReplyToCommand("dtr: source_round must be a non-negative integer");
            return;
        }

        var anchor = command.GetArg(3);
        var selector = command.GetArg(4);
        var humanSlotText = player is { IsValid: true } ? null : command.ArgCount > 5 ? command.GetArg(5) : null;
        if (!TryResolveMomentHuman(player, humanSlotText, out var human, out var humanError))
        {
            command.ReplyToCommand(humanError);
            return;
        }

        var loopArgIndex = player is { IsValid: true } ? 5 : 6;
        var loop = command.ArgCount > loopArgIndex && ParseLoopArgument(command.GetArg(loopArgIndex));
        RunMoment(manifestPath, round, anchor, selector, human, loop, message => command.ReplyToCommand(message));
    }

    private void RunMomentChatCommand(CCSPlayerController? player, IReadOnlyList<string> tokens)
    {
        void Reply(string message) => ReplyToReplayChat(player, message);

        if (tokens.Count == 1 || tokens[1].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Reply("usage: .moment \"<manifest.json>\" <source_round> <player_name|steamid> [bomb|seconds|bomb+seconds] [loop:0|1]");
            Reply("usage: .moment stop");
            return;
        }

        if (tokens[1].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            StopAllState("chat_moment_stop");
            Reply("[DTR OK] moment stopped");
            return;
        }

        if (tokens.Count < 4)
        {
            Reply("usage: .moment \"<manifest.json>\" <source_round> <player_name|steamid> [bomb|seconds|bomb+seconds] [loop:0|1]");
            return;
        }

        if (player is not { IsValid: true })
        {
            Reply("[DTR ERR] .moment must be run by an in-game player.");
            return;
        }

        var manifestPath = tokens[1];
        if (!int.TryParse(tokens[2], out var round) || round < 0)
        {
            Reply("dtr: source_round must be a non-negative integer");
            return;
        }

        var selector = tokens[3];
        var anchor = tokens.Count > 4 ? tokens[4] : "bomb";
        var loop = tokens.Count > 5 && ParseLoopArgument(tokens[5]);
        RunMoment(manifestPath, round, anchor, selector, player, loop, Reply);
    }

    private void RunMoment(
        string manifestPath,
        int round,
        string anchor,
        string selector,
        CCSPlayerController human,
        bool loop,
        Action<string> reply)
    {
        if (!BotControllerNative.IsCompatible)
        {
            reply($"dtr: ABI mismatch, runtime={BotControllerNative.AbiVersion}, expected={BotControllerNative.ExpectedAbiVersion}");
            return;
        }
        if (!TryReadManifest(manifestPath, out var manifest, out var readError))
        {
            reply($"[DTR ERR] failed to read manifest: {readError}");
            return;
        }
        if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
        {
            reply($"[DTR ERR] map mismatch: server=\"{currentMap}\" manifest=\"{manifest.Map}\" path=\"{manifestPath}\"");
            return;
        }
        if (!TryResolveReplayStartAnchor(anchor, reply, "dtr_moment", manifest, round, out var secondsAfterLive))
            return;

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        var roundFiles = manifest.Files
            .Where(file => file.Round == round)
            .OrderBy(file => file.Side, StringComparer.Ordinal)
            .ThenBy(file => file.SteamId)
            .ToList();
        if (roundFiles.Count == 0)
        {
            reply($"[DTR ERR] manifest has no files for source_round={round}");
            return;
        }

        var matches = roundFiles.Where(file => MomentPlayerMatches(file, selector)).ToList();
        if (matches.Count != 1)
        {
            reply(matches.Count == 0
                ? $"[DTR ERR] no player matched \"{selector}\" in source_round={round}"
                : $"[DTR ERR] player selector \"{selector}\" is ambiguous: {string.Join(", ", matches.Select(file => file.PlayerName))}");
            return;
        }

        var target = matches[0];
        if (!TryBuildMomentCandidate(manifestDir, target, secondsAfterLive, out var targetCandidate, out var targetError))
        {
            reply($"[DTR ERR] target {target.PlayerName} is not alive/playable at +{F(secondsAfterLive)}s: {targetError}");
            return;
        }

        var otherFiles = roundFiles.Where(file => !ReferenceEquals(file, target)).ToList();
        var candidates = new List<MomentCandidate>();
        foreach (var file in otherFiles)
        {
            if (TryBuildMomentCandidate(manifestDir, file, secondsAfterLive, out var candidate, out _))
                candidates.Add(candidate);
        }

        var targetTeam = ManifestSideToTeam(target.Side);
        if (targetTeam is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            reply($"[DTR ERR] target side \"{target.Side}\" is not playable");
            return;
        }

        var targets = FindReplayTargets()
            .Where(bot => bot.Slot != human.Slot)
            .ToList();
        var tBots = targets.Where(bot => bot.Team == CsTeam.Terrorist).OrderBy(bot => bot.Slot).ToList();
        var ctBots = targets.Where(bot => bot.Team == CsTeam.CounterTerrorist).OrderBy(bot => bot.Slot).ToList();
        var tFiles = candidates
            .Where(candidate => candidate.File.Side.Equals("t", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.File)
            .ToList();
        var ctFiles = candidates
            .Where(candidate => candidate.File.Side.Equals("ct", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.File)
            .ToList();

        var tAssignments = BuildReplayAssignments(tFiles, tBots);
        var ctAssignments = BuildReplayAssignments(ctFiles, ctBots);
        var skippedT = tFiles.Count - tAssignments.Count;
        var skippedCt = ctFiles.Count - ctAssignments.Count;

        StopAndUnloadLoaded();
        var loaded = new List<string>();
        if (!LoadSide(tAssignments, manifestDir, loaded, out var loadError) ||
            !LoadSide(ctAssignments, manifestDir, loaded, out loadError))
        {
            StopAndUnloadLoaded();
            reply($"[DTR ERR] failed to load moment bots: {loadError}");
            return;
        }

        EnsureHumanMomentTeam(human, targetTeam);
        Server.NextFrame(() =>
        {
            if (human is { IsValid: true } && !human.PawnIsAlive)
                human.Respawn();
            Server.NextFrame(() =>
            {
                ApplyMomentToHuman(human, target, targetCandidate.Tick.Pre, targetCandidate.Tick.WeaponDefIndex);
                PreloadLoadedReplays(ReplayStartAnchor.Live, null, secondsAfterLive);
                var start = StartLoaded(loop, ReplayStartAnchor.Live, null, secondsAfterLive);
                reply($"[DTR OK] moment player={target.PlayerName} round={round} start=+{F(secondsAfterLive)}s bots={loaded.Count} skipped T={skippedT}/CT={skippedCt}: {start}");
                reply("[DTR WARN] moment v1 uses round loadout and 100 HP; exact used utility/ammo/anchor HP need converter snapshot support.");
            });
        });
    }

    private static bool TryResolveMomentHuman(
        CCSPlayerController? commandPlayer,
        string? slotText,
        out CCSPlayerController human,
        out string error)
    {
        human = null!;
        error = string.Empty;
        if (commandPlayer is { IsValid: true, IsBot: false })
        {
            human = commandPlayer;
            return true;
        }

        if (string.IsNullOrWhiteSpace(slotText))
        {
            error = "[DTR ERR] server console usage requires human_slot after player selector.";
            return false;
        }

        if (!int.TryParse(slotText, out var slot) || slot < 0 || slot >= MaxPlayerSlots)
        {
            error = $"[DTR ERR] human_slot must be 0..{MaxPlayerSlots - 1}";
            return false;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } || player.IsBot)
        {
            error = $"[DTR ERR] slot {slot} is not a valid human player";
            return false;
        }

        human = player;
        return true;
    }

    private static bool MomentPlayerMatches(ManifestFile file, string selector)
    {
        selector = selector.Trim();
        if (ulong.TryParse(selector, out var steamId) && steamId == file.SteamId)
            return true;

        return file.PlayerName.Equals(selector, StringComparison.OrdinalIgnoreCase);
    }

    private static CsTeam ManifestSideToTeam(string side)
        => side.Equals("t", StringComparison.OrdinalIgnoreCase)
            ? CsTeam.Terrorist
            : side.Equals("ct", StringComparison.OrdinalIgnoreCase)
                ? CsTeam.CounterTerrorist
                : CsTeam.None;

    private static bool TryBuildMomentCandidate(
        string manifestDir,
        ManifestFile file,
        float secondsAfterLive,
        out MomentCandidate candidate,
        out string error)
    {
        candidate = default;
        if (!TryResolveChildPathUnderRoot(manifestDir, file.Path, out var recPath, out error))
            return false;

        if (!BotControllerNative.TryReadReplayTickAt(
                recPath,
                secondsAfterLive,
                out var tick,
                out _,
                out _,
                out error))
            return false;

        candidate = new MomentCandidate(file, tick);
        return true;
    }

    private static void EnsureHumanMomentTeam(CCSPlayerController human, CsTeam targetTeam)
    {
        if (human.Team != targetTeam)
            human.SwitchTeam(targetTeam);
    }

    private void ApplyMomentToHuman(
        CCSPlayerController human,
        ManifestFile file,
        NativeMovementSnapshot snapshot,
        int activeWeaponDefIndex)
    {
        if (human is not { IsValid: true })
            return;
        if (!human.PawnIsAlive)
            human.Respawn();
        if (human.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;
        var pawn = human.PlayerPawn.Value;

        var loadout = NormalizeReplayLoadout(file.Loadout ?? new ReplayLoadoutSnapshot());
        ApplyMomentLoadout(human, pawn, loadout, activeWeaponDefIndex);
        pawn.Health = ReplayStartHealth;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");

        var origin = new Vector(snapshot.OriginX, snapshot.OriginY, snapshot.OriginZ);
        var angles = new QAngle(snapshot.Pitch, snapshot.Yaw, snapshot.Roll);
        var velocity = new Vector(snapshot.VelX, snapshot.VelY, snapshot.VelZ);
        pawn.Teleport(origin, angles, velocity);
    }

    private static void ApplyMomentLoadout(
        CCSPlayerController human,
        CCSPlayerPawn pawn,
        ReplayLoadoutSnapshot loadout,
        int activeWeaponDefIndex)
    {
        ApplyReplayArmorAndKit(human, pawn, loadout);
        try
        {
            human.RemoveWeapons();
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: moment failed to remove weapons slot={human.Slot}: {ex.Message}");
        }

        TryGiveNamedItem(human, "weapon_knife");
        foreach (var def in loadout.WeaponDefIndices ?? [])
        {
            if (!TryGetWeaponClassByDefIndex(def, out var className))
                continue;
            if (GetReplayWeaponSlot(className) is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Other)
                continue;
            TryGiveNamedItem(human, className);
        }

        var active = NormalizeWeaponDefIndex(activeWeaponDefIndex);
        if (!TryGetWeaponClassByDefIndex(active, out var activeClass))
            return;
        if (!HasReplayWeapon(pawn, activeClass) &&
            GetReplayWeaponSlot(activeClass) is not (ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Other))
            TryGiveNamedItem(human, activeClass);

        if (human.UserId != null)
            NativeAPI.IssueClientCommand(human.UserId.Value, $"use {activeClass}");
    }

    private readonly record struct MomentCandidate(
        ManifestFile File,
        NativeReplayTick Tick);
}
