using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool _poolPreparePending;
    private int _poolPrepareToken;
    private int _poolPendingRoundIndex = -1;
    private string _poolPendingManifestPath = string.Empty;
    private string _poolPendingReason = string.Empty;
    private string _poolPendingPrepareReason = string.Empty;
    private RoundPoolCandidate? _poolPendingCandidate;
    private bool _playoffEnabled;
    private bool _playoffPreparePending;
    private bool _playoffPendingCanLoad;
    private int _playoffPrepareToken;
    private int _playoffPendingTRound = -1;
    private int _playoffPendingCtRound = -1;
    private string _playoffPendingReason = string.Empty;
    private string _playoffPendingPrepareReason = string.Empty;
    private bool _playoffPrepared;
    private int _playoffPreparedTRound = -1;
    private int _playoffPreparedCtRound = -1;
    private string _playoffPreparedLabel = string.Empty;
    private int _playoffRoundIndex;

    [ConsoleCommand("dtr_go", "dtr_go <seq|round|pool> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void GoCommand(CCSPlayerController? player, CommandInfo command)
        => DispatchPlanCommand(command, "dtr_go", restart: true);

    [ConsoleCommand("dtr_arm", "dtr_arm <seq|round|pool> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ArmCommand(CCSPlayerController? player, CommandInfo command)
        => DispatchPlanCommand(command, "dtr_arm", restart: false);

    [ConsoleCommand("dtr_seq_restart", "dtr_seq_restart <manifest.json> [from_source_round]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void SequenceRestartCommand(CCSPlayerController? player, CommandInfo command)
        => RunManifestSequence(command, "dtr_seq_restart", restart: true);

    [ConsoleCommand("dtr_round_restart", "dtr_round_restart <manifest.json> <source_round>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RoundRestartCommand(CCSPlayerController? player, CommandInfo command)
        => ArmSingleRound(command, "dtr_round_restart", restart: true);

    [ConsoleCommand("dtr_run_manifest", "dtr_run_manifest <manifest.json> [from_source_round]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RunManifestCommand(CCSPlayerController? player, CommandInfo command)
        => RunManifestSequence(command, "dtr_run_manifest", restart: false);

    [ConsoleCommand("dtr_stop_sequence", "dtr_stop_sequence")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StopSequenceCommand(CCSPlayerController? player, CommandInfo command)
    {
        StopSequenceState();
        command.ReplyToCommand("dtr: sequence stopped");
    }

    [ConsoleCommand("dtr_playoff", "dtr_playoff <true|false>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PlayoffCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand(
                $"[DTR OK] playoff={FormatOnOff(_playoffEnabled)} plan={FormatPlayoffPlanStatus()}");
            command.ReplyToCommand("usage: dtr_playoff <true|false>");
            return;
        }

        if (!TryParsePlayoffToggle(command.GetArg(1), out var enabled))
        {
            command.ReplyToCommand("usage: dtr_playoff <true|false>");
            return;
        }

        _playoffEnabled = enabled;
        if (!enabled)
            CancelPlayoffPreparation(unloadPrepared: true);

        command.ReplyToCommand(
            $"[DTR OK] playoff={FormatOnOff(_playoffEnabled)} plan={FormatPlayoffPlanStatus()}");
        if (enabled && string.IsNullOrWhiteSpace(_sequenceManifestPath))
        {
            command.ReplyToCommand(
                "[DTR HINT] playoff is enabled and will attach to the next manifest sequence.");
        }
        else if (!enabled)
        {
            command.ReplyToCommand(
                "[DTR OK] Future playoff scheduling is disabled; an already-live replay is not stopped.");
        }
    }

    [ConsoleCommand("dtr_arm_round", "dtr_arm_round <manifest.json> <source_round> [loop:0|1]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ArmRoundCommand(CCSPlayerController? player, CommandInfo command)
        => ArmSingleRound(command, "dtr_arm_round", restart: false);

    private void DispatchPlanCommand(CommandInfo command, string commandName, bool restart)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[DTR ERR] Missing mode. Usage: {commandName} <seq|round|pool> ...");
            command.ReplyToCommand($"[DTR HINT] {commandName} seq <manifest_json> [from_source_round]");
            command.ReplyToCommand($"[DTR HINT] {commandName} round <manifest_json> <source_round>");
            command.ReplyToCommand($"[DTR HINT] {commandName} pool <pool_manifest_json> [server_round]");
            return;
        }

        switch (command.GetArg(1).ToLowerInvariant())
        {
            case "seq":
            case "sequence":
                RunManifestSequence(command, $"{commandName} seq", restart, argOffset: 2);
                return;
            case "round":
                ArmSingleRound(command, $"{commandName} round", restart, argOffset: 2);
                return;
            case "pool":
                RunPoolPlan(command, $"{commandName} pool", restart, argOffset: 2);
                return;
            default:
                command.ReplyToCommand("[DTR ERR] Ambiguous command. Choose a mode: seq, round, or pool.");
                command.ReplyToCommand($"[DTR HINT] Use \"{commandName} seq <manifest_json> 0\" for sequence playback.");
                command.ReplyToCommand($"[DTR HINT] Use \"{commandName} round <manifest_json> 0\" for single-round playback.");
                return;
        }
    }

    private void RunManifestSequence(
        CommandInfo command,
        string commandName,
        bool restart,
        int argOffset = 1)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount <= argOffset)
        {
            command.ReplyToCommand($"usage: {commandName} <manifest.json> [from_source_round]");
            return;
        }

        var manifestPath = command.GetArg(argOffset);
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        var hasManifestStampBefore = ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampBefore);
        if (!TryReadManifest(resolvedManifestPath, out var manifest, out var readError))
        {
            command.ReplyToCommand($"dtr: failed to read manifest: {readError}");
            return;
        }
        var stableManifestStamp = hasManifestStampBefore &&
                                  ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampAfter) &&
                                  manifestStampBefore == manifestStampAfter
            ? manifestStampAfter
            : (ReplayFileStamp?)null;
        if (!CheckManifestMap(command, manifest.Map, manifestPath))
            return;

        var rounds = manifest.Files
            .Select(file => file.Round)
            .Distinct()
            .Order()
            .ToArray();

        if (rounds.Length == 0)
        {
            command.ReplyToCommand("dtr: manifest has no playable rounds");
            return;
        }

        var startRound = rounds[0];
        if (command.ArgCount > argOffset + 1 &&
            (!int.TryParse(command.GetArg(argOffset + 1), out startRound) || !rounds.Contains(startRound)))
        {
            command.ReplyToCommand($"[DTR ERR] from_source_round={command.GetArg(argOffset + 1)} was not found in manifest.");
            command.ReplyToCommand($"[DTR HINT] Available source rounds: {string.Join(", ", rounds)}.");
            return;
        }

        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: true))
            return;

        ActivatePendingReplayRetentionPriority();
        StopAndUnloadLoaded();
        CancelReplayPrefetch();
        ResetPlayoffProgress();
        ClearPoolPreparedState();
        _sequenceManifestPath = manifestPath;
        _sequenceRounds = rounds;
        _sequenceIndex = Array.IndexOf(rounds, startRound);
        _sequenceActive = _sequenceIndex >= 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        InvalidateFreezePreroll();
        _armed = false;
        _armedPrepared = false;
        _armedManifestPath = string.Empty;
        _armedSourceRound = -1;
        _poolActive = false;
        PrefetchRoundReplays(manifestPath, manifest, startRound, stableManifestStamp);

        command.ReplyToCommand(
            restart
                ? $"[DTR OK] Planned SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; restart=now."
                : $"[DTR OK] Armed SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; waiting for next round_start.");
        command.ReplyToCommand(
            $"[DTR OK] Sequence has {rounds.Length - _sequenceIndex} round(s) remaining from source_round={startRound}.");
        IssueRestartIfRequested(command, restart);
    }

    private void ArmSingleRound(
        CommandInfo command,
        string commandName,
        bool restart,
        int argOffset = 1)
    {
        if (!TryParseRoundArgs(command, commandName, out var manifestPath, out var round, argOffset))
            return;

        var loop = command.ArgCount > argOffset + 2 && command.GetArg(argOffset + 2) != "0";
        PlanSingleRound(
            commandName,
            manifestPath,
            round,
            loop,
            restart,
            message => command.ReplyToCommand(message));
    }

    private void PlanSingleRound(
        string commandName,
        string manifestPath,
        int round,
        bool loop,
        bool restart,
        Action<string> reply)
    {
        if (!BotControllerNative.IsCompatible)
        {
            reply($"dtr: ABI mismatch; {BotControllerNative.RuntimeSummary}");
            return;
        }
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        var hasManifestStampBefore = ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampBefore);
        if (!TryReadManifest(resolvedManifestPath, out var manifest, out var readError))
        {
            reply($"[DTR ERR] failed to read manifest: {readError}");
            return;
        }
        var stableManifestStamp = hasManifestStampBefore &&
                                  ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampAfter) &&
                                  manifestStampBefore == manifestStampAfter
            ? manifestStampAfter
            : (ReplayFileStamp?)null;
        if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
        {
            reply($"[DTR ERR] map mismatch: server=\"{currentMap}\" manifest=\"{manifest.Map}\" path=\"{manifestPath}\"");
            return;
        }
        if (!ManifestContainsSourceRound(manifest, round, out var validateError))
        {
            reply(validateError);
            return;
        }

        if (!CheckReplayStartGates(reply, stopCurrentForOverride: true))
            return;

        ActivatePendingReplayRetentionPriority();
        StopAndUnloadLoaded();
        CancelReplayPrefetch();
        ClearPoolPreparedState();
        _sequenceActive = false;
        _sequenceManifestPath = string.Empty;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        ResetPlayoffProgress();
        _poolActive = false;
        InvalidateFreezePreroll();
        _armed = true;
        _armedLoop = loop;
        _armedPrepared = false;
        _armedManifestPath = manifestPath;
        _armedSourceRound = round;
        _armedLabel = $"source_round={round} manifest={manifestPath}";
        PrefetchRoundReplays(manifestPath, manifest, round, stableManifestStamp);
        reply(
            restart
                ? $"[DTR OK] Planned SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; restart=now."
                : $"[DTR OK] Armed SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; waiting for next round_start.");
        reply("[DTR OK] This plan will not advance to later manifest rounds.");
        IssueRestartIfRequested(restart, reply);
    }

    private static string StripOuterQuotes(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }

    private static bool ParseLoopArgument(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("loop:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["loop:".Length..];

        return !normalized.Equals("0", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("off", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("false", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private bool PrepareNextSequenceRound(string reason)
    {
        if (_sequenceIndex < 0 || _sequenceIndex >= _sequenceRounds.Length)
        {
            _sequenceActive = false;
            Server.PrintToConsole("dtr: sequence complete");
            return false;
        }

        if (_sequencePrepared)
            return true;

        var round = _sequenceRounds[_sequenceIndex];
        var load = LoadRound(_sequenceManifestPath, round);
        if (!load.Ok)
        {
            _sequenceActive = false;
            _sequencePrepared = false;
            _sequencePreparedRound = -1;
            Server.PrintToConsole($"dtr: sequence stopped while preparing round {round}: {load.Message}");
            return false;
        }

        PreloadLoadedReplays();
        _sequencePrepared = true;
        _sequencePreparedRound = round;
        TryStartDtrRoundBanner($"sequence_r{round}");
        Server.PrintToConsole($"dtr: prepared sequence round {round} on {reason}: {load.Message}");
        return true;
    }

    private bool PrepareArmedRound(string reason)
    {
        if (!_armed)
            return false;
        if (_armedPrepared)
            return true;
        if (string.IsNullOrWhiteSpace(_armedManifestPath) || _armedSourceRound < 0)
        {
            _armed = false;
            _armedPrepared = false;
            Server.PrintToConsole("[DTR ERR] single-round plan is missing manifest/source_round");
            return false;
        }

        var manifestPath = _armedManifestPath;
        var sourceRound = _armedSourceRound;
        var loop = _armedLoop;
        var label = _armedLabel;
        var load = LoadRound(manifestPath, sourceRound);
        if (!load.Ok)
        {
            _armed = false;
            _armedPrepared = false;
            _armedManifestPath = string.Empty;
            _armedSourceRound = -1;
            Server.PrintToConsole($"[DTR ERR] single source_round={sourceRound} failed while preparing on {reason}: {load.Message}");
            return false;
        }

        _armed = true;
        _armedPrepared = true;
        _armedManifestPath = manifestPath;
        _armedSourceRound = sourceRound;
        _armedLoop = loop;
        _armedLabel = label;
        PreloadLoadedReplays();
        TryStartDtrRoundBanner($"single_r{sourceRound}");
        Server.PrintToConsole($"[DTR OK] round_start: loaded SINGLE source_round={sourceRound} on {reason}: {load.Message}");
        return true;
    }

    private void StartPreparedSequenceRound()
    {
        if (!_sequencePrepared && !PrepareNextSequenceRound("round_freeze_end fallback"))
        {
            return;
        }

        var round = _sequencePreparedRound;
        var play = StartLoaded(loop: false);
        Server.PrintToConsole($"dtr: sequence round {round} start on round_freeze_end: {play}");

        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _sequenceIndex++;
        if (_sequenceIndex >= _sequenceRounds.Length)
        {
            _sequenceActive = false;
            Server.PrintToConsole(
                _playoffEnabled
                    ? "dtr: sequence complete; playoff continuation is armed"
                    : "dtr: sequence complete");
        }
    }

    private void StopSequenceState()
    {
        var hadSequencePrefetch = _sequenceActive || _sequencePrepared ||
                                  _playoffPreparePending || _playoffPrepared;
        CancelPlayoffPreparation(unloadPrepared: true);
        _sequenceActive = false;
        _sequenceManifestPath = string.Empty;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        ResetPlayoffProgress();
        InvalidateFreezePreroll();
        if (hadSequencePrefetch)
        {
            CancelReplayPrefetch();
            ReleaseUnusedWarmReplayBuffers();
        }
    }

    private static bool TryParsePlayoffToggle(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
            case "yes":
                enabled = true;
                return true;
            case "0":
            case "false":
            case "off":
            case "no":
                enabled = false;
                return true;
            default:
                enabled = false;
                return false;
        }
    }

    private bool IsPlayoffPlanReady()
    {
        return _playoffEnabled &&
               !_sequenceActive &&
               !string.IsNullOrWhiteSpace(_sequenceManifestPath) &&
               _sequenceRounds.Length > 0 &&
               _sequenceIndex >= _sequenceRounds.Length;
    }

    private bool HasPlayoffSchedulingState()
        => IsPlayoffPlanReady() || _playoffPreparePending || _playoffPrepared;

    private string FormatPlayoffPlanStatus()
    {
        if (_playoffPrepared)
            return $"prepared:T=r{_playoffPreparedTRound},CT=r{_playoffPreparedCtRound}";
        if (_playoffPreparePending)
            return $"decoding:T=r{_playoffPendingTRound},CT=r{_playoffPendingCtRound}";
        if (IsPlayoffPlanReady())
            return $"ready:extra_round={_playoffRoundIndex + 1}";
        if (_sequenceActive && _playoffEnabled)
            return "waiting_for_sequence_end";
        return "none";
    }

    private void ResetPlayoffProgress()
    {
        ClearPlayoffPendingPreparation(cancelDecode: true);
        _playoffPrepared = false;
        _playoffPreparedTRound = -1;
        _playoffPreparedCtRound = -1;
        _playoffPreparedLabel = string.Empty;
        _playoffRoundIndex = 0;
    }

    private void CancelPlayoffPreparation(bool unloadPrepared)
    {
        var hadPrepared = _playoffPrepared;
        ClearPlayoffPendingPreparation(cancelDecode: true);
        _playoffPrepared = false;
        _playoffPreparedTRound = -1;
        _playoffPreparedCtRound = -1;
        _playoffPreparedLabel = string.Empty;
        if (!unloadPrepared || !hadPrepared)
            return;

        InvalidateFreezePreroll();
        StopAndUnloadLoaded(clearArmedPlan: false);
    }

    private void ClearPlayoffPendingPreparation(bool cancelDecode)
    {
        var wasPending = _playoffPreparePending;
        _playoffPreparePending = false;
        _playoffPendingCanLoad = false;
        _playoffPrepareToken++;
        _playoffPendingTRound = -1;
        _playoffPendingCtRound = -1;
        _playoffPendingReason = string.Empty;
        _playoffPendingPrepareReason = string.Empty;
        if (cancelDecode && wasPending)
            FinishReplayPrefetchRound();
    }

    private bool PrepareNextPlayoffRound(string prepareReason, bool allowLoad = true)
    {
        if (!IsPlayoffPlanReady())
            return false;
        if (_playoffPrepared)
            return true;
        if (_playoffPreparePending)
        {
            if (!allowLoad)
                return false;

            _playoffPendingCanLoad = true;
            if (ReplayPrefetchReady())
            {
                return CompletePendingPlayoffPreparation(
                    waitForDecode: false,
                    scheduleFreezePreroll: false);
            }

            PollPendingPlayoffPreparation(_playoffPrepareToken);
            return false;
        }

        var manifestPath = ResolveReadableManifestPath(_sequenceManifestPath);
        if (!TryGetPrefetchedManifest(manifestPath, out var manifest) &&
            !TryReadManifest(manifestPath, out manifest, out var readError))
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: failed to read manifest: {readError}");
            return false;
        }
        if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: map mismatch server={currentMap} manifest={manifest.Map}");
            return false;
        }

        var hasTRoster = TryGetPlayoffRosterSteamIds(
            CsTeam.Terrorist,
            out var tSteamIds,
            out var tRosterError);
        var hasCtRoster = TryGetPlayoffRosterSteamIds(
            CsTeam.CounterTerrorist,
            out var ctSteamIds,
            out var ctRosterError);
        if (!hasTRoster || !hasCtRoster)
        {
            var rosterError = !string.IsNullOrWhiteSpace(tRosterError) ? tRosterError : ctRosterError;
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: {rosterError}");
            return false;
        }
        if (tSteamIds.Count == 0 && ctSteamIds.Count == 0)
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: no replay bot targets");
            return false;
        }

        var hasTRound = TryChoosePlayoffSourceRound(
                manifest,
                "t",
                tSteamIds,
                out var tRound,
                out var tCandidateCount,
                out var tChooseError);
        var hasCtRound = TryChoosePlayoffSourceRound(
                manifest,
                "ct",
                ctSteamIds,
                out var ctRound,
                out var ctCandidateCount,
                out var ctChooseError);
        if (!hasTRound || !hasCtRound)
        {
            var chooseError = !string.IsNullOrWhiteSpace(tChooseError) ? tChooseError : ctChooseError;
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: {chooseError}");
            return false;
        }

        PrefetchPlayoffRoundReplays(
            manifestPath,
            manifest,
            tRound,
            ctRound,
            tSteamIds,
            ctSteamIds);
        _playoffPreparePending = true;
        _playoffPendingCanLoad = allowLoad;
        _playoffPendingTRound = tRound;
        _playoffPendingCtRound = ctRound;
        _playoffPendingReason =
            $"T=r{tRound} from {tCandidateCount} full-buy candidate(s), " +
            $"CT=r{ctRound} from {ctCandidateCount} full-buy candidate(s)";
        _playoffPendingPrepareReason = prepareReason;
        var token = ++_playoffPrepareToken;
        Server.PrintToConsole(
            $"dtr: playoff extra round {_playoffRoundIndex + 1} selected on {prepareReason}; " +
            $"{_playoffPendingReason}; decoding replay data off-thread");
        if (allowLoad)
            PollPendingPlayoffPreparation(token);
        return false;
    }

    private bool TryGetPlayoffRosterSteamIds(
        CsTeam team,
        out HashSet<ulong> steamIds,
        out string error)
    {
        steamIds = new HashSet<ulong>();
        error = string.Empty;
        var targets = FindReplayTargets().Where(bot => bot.Team == team).ToList();
        foreach (var bot in targets)
        {
            ulong steamId = 0;
            if (_loadedReplays.TryGetValue(bot.Slot, out var loaded))
                steamId = loaded.SteamId;
            else if (_retainedBotHiderPresentation.TryGetValue(bot.Slot, out var retained))
                steamId = retained.SteamId;

            if (steamId == 0)
            {
                error = $"team={team} slot={bot.Slot} has no retained DTR SteamID evidence";
                return false;
            }
            if (!steamIds.Add(steamId))
            {
                error = $"team={team} has duplicate retained DTR SteamID {steamId}";
                return false;
            }
        }
        return true;
    }

    private static bool TryChoosePlayoffSourceRound(
        ConversionManifest manifest,
        string side,
        IReadOnlySet<ulong> steamIds,
        out int selectedRound,
        out int candidateCount,
        out string error)
    {
        selectedRound = -1;
        candidateCount = 0;
        error = string.Empty;
        if (steamIds.Count == 0)
            return true;

        var candidates = manifest.Rounds
            .Where(round => !round.PistolRound && IsPlayoffFullBuy(round, side))
            .Select(round => round.Round)
            .Where(round => PlayoffRoundCoversRoster(manifest, round, side, steamIds))
            .Distinct()
            .Order()
            .ToArray();
        candidateCount = candidates.Length;
        if (candidates.Length == 0)
        {
            error = $"side={side} has no full-buy source round covering every retained SteamID";
            return false;
        }

        selectedRound = candidates[Random.Shared.Next(candidates.Length)];
        return true;
    }

    private static bool IsPlayoffFullBuy(ManifestRound round, string side)
    {
        var economy = side.Equals("t", StringComparison.OrdinalIgnoreCase)
            ? round.TEconomy
            : round.CtEconomy;
        return string.Equals(economy?.Class, "full", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PlayoffRoundCoversRoster(
        ConversionManifest manifest,
        int round,
        string side,
        IReadOnlySet<ulong> steamIds)
    {
        var fileCountsBySteamId = manifest.Files
            .Where(file => file.Round == round &&
                           file.Side.Equals(side, StringComparison.OrdinalIgnoreCase) &&
                           file.SteamId != 0)
            .GroupBy(file => file.SteamId)
            .ToDictionary(group => group.Key, group => group.Count());
        return steamIds.All(steamId =>
            fileCountsBySteamId.TryGetValue(steamId, out var count) && count == 1);
    }

    private void PollPendingPlayoffPreparation(int token)
    {
        Server.NextFrame(() =>
        {
            if (!_playoffPreparePending || token != _playoffPrepareToken)
                return;
            if (!_playoffPendingCanLoad)
                return;
            if (!ReplayPrefetchReady())
            {
                PollPendingPlayoffPreparation(token);
                return;
            }

            _ = CompletePendingPlayoffPreparation(
                waitForDecode: false,
                scheduleFreezePreroll: true);
        });
    }

    private bool CompletePendingPlayoffPreparation(
        bool waitForDecode,
        bool scheduleFreezePreroll)
    {
        if (!_playoffPreparePending)
            return _playoffPrepared;
        if (!waitForDecode && !ReplayPrefetchReady())
            return false;

        var tRound = _playoffPendingTRound;
        var ctRound = _playoffPendingCtRound;
        var reason = _playoffPendingReason;
        var prepareReason = _playoffPendingPrepareReason;
        ClearPlayoffPendingPreparation(cancelDecode: false);
        if (!IsPlayoffPlanReady())
            return false;

        var load = LoadPlayoffRound(_sequenceManifestPath, tRound, ctRound);
        if (!load.Ok)
        {
            Server.PrintToConsole(
                $"dtr: playoff failed extra round {_playoffRoundIndex + 1}: {load.Message}");
            return false;
        }

        PreloadLoadedReplays();
        _playoffPrepared = true;
        _playoffPreparedTRound = tRound;
        _playoffPreparedCtRound = ctRound;
        _playoffPreparedLabel = $"{reason}; {load.Message}";
        TryStartDtrRoundBanner($"playoff_t{tRound}_ct{ctRound}");
        Server.PrintToConsole(
            $"dtr: prepared playoff extra round {_playoffRoundIndex + 1} on {prepareReason} -> {_playoffPreparedLabel}");
        if (scheduleFreezePreroll &&
            TryReadFreezePhaseRemaining(out var freezeRemaining, out _) &&
            freezeRemaining > 0.0f)
        {
            ScheduleFreezePrerollStart($"playoff extra round {_playoffRoundIndex + 1}");
        }
        return true;
    }

    private void StartPreparedPlayoffRound()
    {
        if (!_playoffPrepared && _playoffPreparePending)
            _ = CompletePendingPlayoffPreparation(waitForDecode: true, scheduleFreezePreroll: false);

        var extraRound = _playoffRoundIndex + 1;
        if (!_playoffPrepared)
        {
            Server.PrintToConsole($"dtr: playoff skipped start for extra round {extraRound}: not prepared by round_freeze_end");
            _playoffRoundIndex++;
            return;
        }

        var label = _playoffPreparedLabel;
        var play = StartLoaded(loop: false);
        Server.PrintToConsole(
            $"dtr: playoff extra round {extraRound} start on round_freeze_end -> {label}; {play}");
        _playoffRoundIndex++;
        _playoffPrepared = false;
        _playoffPreparedTRound = -1;
        _playoffPreparedCtRound = -1;
        _playoffPreparedLabel = string.Empty;
        ReleaseUnusedWarmReplayBuffers();
    }

    [ConsoleCommand("dtr_pool_restart", "dtr_pool_restart <pool_manifest.json> [server_round]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PoolRestartCommand(CCSPlayerController? player, CommandInfo command)
        => RunPoolPlan(command, "dtr_pool_restart", restart: true);

    [ConsoleCommand("dtr_run_pool", "dtr_run_pool <pool_manifest.json> [server_round]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RunPoolCommand(CCSPlayerController? player, CommandInfo command)
        => RunPoolPlan(command, "dtr_run_pool", restart: false);

    [ConsoleCommand("dtr_stop_pool", "dtr_stop_pool")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StopPoolCommand(CCSPlayerController? player, CommandInfo command)
    {
        StopPoolState();
        command.ReplyToCommand("dtr: pool stopped");
    }

    private void RunPoolPlan(
        CommandInfo command,
        string commandName,
        bool restart,
        int argOffset = 1)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount <= argOffset)
        {
            command.ReplyToCommand($"usage: {commandName} <pool_manifest.json> [server_round]");
            return;
        }

        var poolPath = command.GetArg(argOffset);
        if (!TryReadPoolManifest(poolPath, out var pool, out var readError))
        {
            command.ReplyToCommand($"dtr: failed to read pool manifest: {readError}");
            return;
        }
        if (!CheckManifestMap(command, pool.Map, poolPath))
            return;

        var startRound = 0;
        if (command.ArgCount > argOffset + 1 &&
            (!int.TryParse(command.GetArg(argOffset + 1), out startRound) || startRound < 0))
        {
            command.ReplyToCommand("dtr: server_round must be a non-negative integer");
            return;
        }

        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: true))
            return;

        ClearReplayRetentionPriority(clearPending: true);
        StopAndUnloadLoaded();
        CancelReplayPrefetch();
        _sequenceActive = false;
        _sequenceManifestPath = string.Empty;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        ResetPlayoffProgress();
        _armed = false;
        _armedPrepared = false;
        _armedManifestPath = string.Empty;
        _armedSourceRound = -1;
        InvalidateFreezePreroll();
        _poolManifestPath = ResolveReadableManifestPath(poolPath);
        _poolManifest = pool;
        _poolRoundIndex = startRound;
        ClearPoolRecentHistory();
        ClearPoolPreparedState();
        _poolActive = pool.Candidates.Count > 0;

        command.ReplyToCommand(
            _poolActive
                ? restart
                    ? $"[DTR OK] Planned POOL. pool_manifest=\"{poolPath}\"; server_round={_poolRoundIndex}; restart=now."
                    : $"[DTR OK] Armed POOL. pool_manifest=\"{poolPath}\"; server_round={_poolRoundIndex}; waiting for next round_start."
                : "dtr: pool manifest has no candidates");
        if (_poolActive)
        {
            command.ReplyToCommand("[DTR OK] Pool candidates are prepared at round_start; economy matching uses that snapshot.");
            IssueRestartIfRequested(command, restart);
        }
    }

    private bool PrepareNextPoolRound(string prepareReason)
    {
        var pool = _poolManifest;
        if (pool == null || pool.Candidates.Count == 0)
        {
            _poolActive = false;
            ReleaseUnusedWarmReplayBuffers();
            Server.PrintToConsole("dtr: pool stopped, no candidates");
            return false;
        }

        if (_poolPrepared && _poolPreparedRoundIndex == _poolRoundIndex)
            return true;
        if (_poolPreparePending && _poolPendingRoundIndex == _poolRoundIndex)
            return false;

        if (!TryChoosePoolCandidate(pool, _poolRoundIndex, out var candidate, out var reason) ||
            candidate == null)
        {
            ReleaseUnusedWarmReplayBuffers();
            Server.PrintToConsole($"dtr: pool skipped round {_poolRoundIndex}: {reason}");
            return false;
        }

        var poolDir = Path.GetDirectoryName(_poolManifestPath) ?? ".";
        if (!TryResolveChildPathUnderRoot(poolDir, candidate.Manifest, out var manifestPath, out var pathError))
        {
            ReleaseUnusedWarmReplayBuffers();
            Server.PrintToConsole($"dtr: pool skipped round {_poolRoundIndex}: {pathError}");
            return false;
        }

        PrefetchRoundReplays(manifestPath, candidate.SourceRound);
        _poolPreparePending = true;
        _poolPendingRoundIndex = _poolRoundIndex;
        _poolPendingManifestPath = manifestPath;
        _poolPendingCandidate = candidate;
        _poolPendingReason = reason;
        _poolPendingPrepareReason = prepareReason;
        var token = ++_poolPrepareToken;
        Server.PrintToConsole(
            $"dtr: pool round {_poolRoundIndex} selected on {prepareReason}; decoding replay data off-thread");
        PollPendingPoolPreparation(token);
        return false;
    }

    private void StartPreparedPoolRound()
    {
        if (!_poolPrepared && _poolPreparePending)
            _ = CompletePendingPoolPreparation(waitForDecode: true, scheduleFreezePreroll: false);

        if (!_poolPrepared)
        {
            Server.PrintToConsole($"dtr: pool skipped start for round {_poolRoundIndex}: not prepared at round_start");
            _poolRoundIndex++;
            ClearPoolPreparedState();
            return;
        }

        var roundIndex = _poolPreparedRoundIndex;
        var label = _poolPreparedLabel;
        var play = StartLoaded(loop: false);

        Server.PrintToConsole($"dtr: pool round {roundIndex} start on round_freeze_end -> {label}; {play}");
        _poolRoundIndex = Math.Max(_poolRoundIndex, roundIndex + 1);
        ClearPoolPreparedState();
    }

    private void PollPendingPoolPreparation(int token)
    {
        Server.NextFrame(() =>
        {
            if (!_poolPreparePending || token != _poolPrepareToken)
                return;
            if (!ReplayPrefetchReady())
            {
                PollPendingPoolPreparation(token);
                return;
            }

            _ = CompletePendingPoolPreparation(
                waitForDecode: false,
                scheduleFreezePreroll: true);
        });
    }

    private bool CompletePendingPoolPreparation(
        bool waitForDecode,
        bool scheduleFreezePreroll)
    {
        if (!_poolPreparePending || _poolPendingCandidate == null)
            return _poolPrepared;
        if (!waitForDecode && !ReplayPrefetchReady())
            return false;

        var pool = _poolManifest;
        var candidate = _poolPendingCandidate;
        var roundIndex = _poolPendingRoundIndex;
        var manifestPath = _poolPendingManifestPath;
        var reason = _poolPendingReason;
        var prepareReason = _poolPendingPrepareReason;
        ClearPoolPendingPreparation(cancelDecode: false);

        if (!_poolActive || pool == null || roundIndex != _poolRoundIndex)
            return false;

        var load = LoadRound(manifestPath, candidate.SourceRound);
        if (!load.Ok)
        {
            Server.PrintToConsole(
                $"dtr: pool failed round {roundIndex}: {load.Message}; candidate={candidate.DemoStem} r{candidate.SourceRound}");
            return false;
        }

        PreloadLoadedReplays();
        MarkPoolCandidateUsed(candidate, pool);
        _poolPrepared = true;
        _poolPreparedRoundIndex = roundIndex;
        _poolPreparedLabel =
            $"{candidate.DemoStem} r{candidate.SourceRound} ({reason}); {load.Message}";
        TryStartDtrRoundBanner($"pool_r{candidate.SourceRound}");

        Server.PrintToConsole(
            $"dtr: prepared pool round {roundIndex} on {prepareReason} -> {_poolPreparedLabel}");
        if (scheduleFreezePreroll &&
            TryReadFreezePhaseRemaining(out var freezeRemaining, out _) &&
            freezeRemaining > 0.0f)
        {
            ScheduleFreezePrerollStart($"pool round {roundIndex}");
        }
        return true;
    }

    private bool TryChoosePoolCandidate(
        RoundPoolManifest pool,
        int roundIndex,
        out RoundPoolCandidate? selected,
        out string reason)
    {
        selected = null;
        var pistolRound = IsPistolRoundIndex(roundIndex);
        var tEconomy = SnapshotCurrentTeamEconomy(CsTeam.Terrorist, pistolRound);
        var ctEconomy = SnapshotCurrentTeamEconomy(CsTeam.CounterTerrorist, pistolRound);

        var ratings = new List<PoolCandidateRating>();
        foreach (var candidate in pool.Candidates)
        {
            if (candidate.PistolRound != pistolRound)
                continue;
            if (pistolRound && candidate.SourceRound is not 0 and not 12)
                continue;
            if (!pistolRound && candidate.SourceRound is 0 or 12)
                continue;

            ratings.Add(ScorePoolCandidate(candidate, tEconomy, ctEconomy, roundIndex));
        }

        if (ratings.Count == 0)
        {
            reason = pistolRound
                ? "no pistol candidates from source round 0/12"
                : "no non-pistol candidates";
            return false;
        }

        ratings.Sort(static (left, right) => left.Score.CompareTo(right.Score));
        var window = BuildPoolCandidateWindow(ratings);
        var chosen = ChooseWeightedPoolCandidate(window);
        selected = chosen.Candidate;

        reason =
            $"target T={FormatTeamEconomySnapshot(tEconomy)} CT={FormatTeamEconomySnapshot(ctEconomy)}, " +
            $"selected_score={chosen.Score}, best={ratings[0].Score}, candidates={ratings.Count}, sampled={window.Count}, " +
            FormatPoolCounterfactual(chosen);
        return true;
    }

    private PoolCandidateRating ScorePoolCandidate(
        RoundPoolCandidate candidate,
        TeamEconomySnapshot targetT,
        TeamEconomySnapshot targetCt,
        int roundIndex)
    {
        var tScore = ScorePoolTeamEconomy(candidate.TEconomy, targetT, out var tRankDelta);
        var ctScore = ScorePoolTeamEconomy(candidate.CtEconomy, targetCt, out var ctRankDelta);
        var economyScore = tScore + ctScore;
        var recentPenalty = 0L;
        if (_poolUsedCandidates.Contains(PoolCandidateKey(candidate)))
            recentPenalty += 25_000L;
        if (_poolRecentManifests.Contains(PoolManifestKey(candidate)))
            recentPenalty += 6_500L;

        var score = economyScore + recentPenalty;
        score += Math.Abs(candidate.SourceRound - (roundIndex % 24)) * 10L;
        score += StableHash(PoolCandidateKey(candidate), roundIndex) % 211;

        var upwardSides = (tRankDelta > 0 ? 1 : 0) + (ctRankDelta > 0 ? 1 : 0);
        var downwardSides = (tRankDelta < 0 ? 1 : 0) + (ctRankDelta < 0 ? 1 : 0);
        return new PoolCandidateRating(candidate, score, economyScore, recentPenalty, upwardSides, downwardSides);
    }

    private static long ScorePoolTeamEconomy(
        PoolTeamEconomy candidate,
        TeamEconomySnapshot target,
        out int rankDelta)
    {
        var candidateValue = candidate.MatchEquipmentValue;
        var valueDelta = (long)candidateValue - target.MatchValue;
        var score = valueDelta >= 0
            ? valueDelta * 55L / 100L
            : (-valueDelta * 155L / 100L) + 1_200L;

        var candidateRank = EconomyClassRank(candidate.Class);
        var targetRank = EconomyClassRank(target.Class);
        rankDelta = candidateRank - targetRank;
        if (IsUnknownEconomyClass(candidate.Class) || IsUnknownEconomyClass(target.Class))
            return score + 1_500L;
        if (rankDelta == 0)
            return score;
        if (rankDelta > 0)
        {
            score += rankDelta switch
            {
                1 => 450L,
                2 => 1_100L,
                _ => 2_400L
            };
            return score;
        }

        score += (-rankDelta) switch
        {
            1 => 3_500L,
            2 => 8_500L,
            _ => 14_000L
        };
        return score;
    }

    private static List<PoolCandidateRating> BuildPoolCandidateWindow(IReadOnlyList<PoolCandidateRating> ratings)
    {
        var targetSize = ClampInt(ratings.Count / 6, 8, 32);
        targetSize = Math.Min(targetSize, ratings.Count);
        var window = new List<PoolCandidateRating>(targetSize);
        for (var i = 0; i < targetSize; i++)
            window.Add(ratings[i]);
        return window;
    }

    private static PoolCandidateRating ChooseWeightedPoolCandidate(IReadOnlyList<PoolCandidateRating> window)
    {
        var bestScore = window[0].Score;
        var totalWeight = 0.0;
        Span<double> weights = window.Count <= 64
            ? stackalloc double[window.Count]
            : new double[window.Count];

        for (var i = 0; i < window.Count; i++)
        {
            var distance = Math.Max(0L, window[i].Score - bestScore);
            var weight = 1.0 / Math.Pow(1.0 + (distance / 2_500.0), 1.35);
            weights[i] = weight;
            totalWeight += weight;
        }

        var roll = Random.Shared.NextDouble() * totalWeight;
        for (var i = 0; i < window.Count; i++)
        {
            roll -= weights[i];
            if (roll <= 0.0)
                return window[i];
        }

        return window[^1];
    }

    private static string FormatPoolCounterfactual(PoolCandidateRating rating)
    {
        if (rating.DownwardSides > 0)
            return $"counterfactual=down:{rating.DownwardSides}";
        if (rating.UpwardSides > 0)
            return $"counterfactual=up:{rating.UpwardSides}";
        return "counterfactual=matched";
    }

    private TeamEconomySnapshot SnapshotCurrentTeamEconomy(CsTeam team, bool pistolRound)
    {
        var bots = FindReplayTargets()
            .Where(bot => bot.Team == team && bot.PawnIsAlive)
            .ToList();
        uint equipment = 0;
        uint money = 0;
        foreach (var bot in bots)
        {
            money = SaturatingAdd(money, ReadReplayTargetMoney(bot));

            if (bot.PlayerPawn is not { IsValid: true, Value.IsValid: true })
                continue;

            var pawn = bot.PlayerPawn.Value;
            if (pawn.WeaponServices == null)
                continue;

            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var weapon = handle.Value;
                if (weapon == null || !weapon.IsValid)
                    continue;
                equipment += WeaponClassValue(weapon.DesignerName);
            }
        }

        var matchValue = EstimateTeamBuyPower(equipment, money, bots.Count, pistolRound);
        var economyClass = ClassifyEconomy(bots.Count, matchValue, pistolRound);
        return new TeamEconomySnapshot(equipment, money, matchValue, economyClass);
    }

    private static uint ReadReplayTargetMoney(CCSPlayerController bot)
    {
        try
        {
            var money = bot.InGameMoneyServices;
            if (money == null)
                return 0;
            var account = Math.Max(money.Account, money.StartAccount);
            return account > 0 ? (uint)account : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static uint EstimateTeamBuyPower(uint equipment, uint money, int players, bool pistolRound)
    {
        if (pistolRound || players <= 0)
            return equipment;

        var cappedSpendable = Math.Min(money, (uint)players * 5_500U);
        return SaturatingAdd(equipment, cappedSpendable);
    }

    private static uint SaturatingAdd(uint left, uint right)
    {
        var sum = (ulong)left + right;
        return sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
    }

    private static string FormatTeamEconomySnapshot(TeamEconomySnapshot snapshot)
        => $"{snapshot.Class}:eq{snapshot.EquipmentValue}/cash{snapshot.MoneyTotal}/buy{snapshot.MatchValue}";

    private static string ClassifyEconomy(int players, uint equipment, bool pistolRound)
    {
        if (pistolRound)
            return "pistol";
        if (players <= 0)
            return "unknown";
        var perPlayer = equipment / Math.Max(1.0f, players);
        if (perPlayer < 1_400.0f)
            return "eco";
        if (perPlayer < 3_600.0f)
            return "force";
        return "full";
    }

    private static long EconomyClassPenalty(string candidate, string target)
    {
        if (candidate.Equals(target, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (candidate.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return 2_000;
        return Math.Abs(EconomyClassRank(candidate) - EconomyClassRank(target)) switch
        {
            1 => 4_000,
            _ => 9_000
        };
    }

    private static int EconomyClassRank(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "pistol" => 0,
            "eco" => 1,
            "force" => 2,
            "full" => 3,
            _ => 2
        };
    }

    private void MarkPoolCandidateUsed(RoundPoolCandidate candidate, RoundPoolManifest pool)
    {
        AddRecentPoolKey(
            _poolRecentCandidateQueue,
            _poolUsedCandidates,
            PoolCandidateKey(candidate),
            ClampInt(pool.Candidates.Count / 5, 16, 96));
        AddRecentPoolKey(
            _poolRecentManifestQueue,
            _poolRecentManifests,
            PoolManifestKey(candidate),
            ClampInt(pool.Candidates.Count / 40, 6, 16));
    }

    private void ClearPoolRecentHistory()
    {
        _poolUsedCandidates.Clear();
        _poolRecentCandidateQueue.Clear();
        _poolRecentManifests.Clear();
        _poolRecentManifestQueue.Clear();
    }

    private void ClearPoolPreparedState()
    {
        ClearPoolPendingPreparation(cancelDecode: true);
        ReleaseUnusedWarmReplayBuffers();
        _poolPrepared = false;
        _poolPreparedRoundIndex = -1;
        _poolPreparedLabel = string.Empty;
    }

    private void ClearPoolPendingPreparation(bool cancelDecode)
    {
        _poolPreparePending = false;
        _poolPrepareToken++;
        _poolPendingRoundIndex = -1;
        _poolPendingManifestPath = string.Empty;
        _poolPendingReason = string.Empty;
        _poolPendingPrepareReason = string.Empty;
        _poolPendingCandidate = null;
        if (cancelDecode)
            FinishReplayPrefetchRound();
    }

    private static void AddRecentPoolKey(Queue<string> queue, HashSet<string> set, string key, int limit)
    {
        queue.Enqueue(key);
        set.Add(key);
        while (queue.Count > limit)
        {
            var evicted = queue.Dequeue();
            if (!queue.Contains(evicted))
                set.Remove(evicted);
        }
    }

    private static bool IsUnknownEconomyClass(string value)
        => value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static int ClampInt(int value, int min, int max)
        => Math.Min(max, Math.Max(min, value));

    private static bool IsPistolRoundIndex(int round) => round is 0 or 12;

    private static string PoolCandidateKey(RoundPoolCandidate candidate)
        => $"{candidate.Manifest}|{candidate.SourceRound}";

    private static string PoolManifestKey(RoundPoolCandidate candidate)
        => candidate.Manifest;

    private readonly record struct PoolCandidateRating(
        RoundPoolCandidate Candidate,
        long Score,
        long EconomyScore,
        long RecentPenalty,
        int UpwardSides,
        int DownwardSides);

    private static int StableHash(string value, int seed)
    {
        unchecked
        {
            var hash = 23 + seed;
            foreach (var ch in value)
                hash = hash * 31 + ch;
            return hash & 0x7fffffff;
        }
    }

    private void StopPoolState()
    {
        var hadPoolPrefetch = _poolActive || _poolPrepared || _poolPreparePending;
        _poolActive = false;
        _poolManifest = null;
        _poolManifestPath = string.Empty;
        _poolRoundIndex = 0;
        ClearPoolRecentHistory();
        ClearPoolPreparedState();
        InvalidateFreezePreroll();
        if (hadPoolPrefetch)
            CancelReplayPrefetch();
    }

    [ConsoleCommand("dtr_load", "dtr_load <round|slot> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void LoadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_load round <manifest.json> <source_round> | dtr_load slot <slot> <path.dtr>");
            return;
        }

        var mode = command.GetArg(1).ToLowerInvariant();
        if (mode == "round")
        {
            if (!TryParseRoundArgs(command, "dtr_load round", out var manifestPath, out var round, argOffset: 2))
                return;

            ActivatePendingReplayRetentionPriority();
            var result = LoadRound(manifestPath, round);
            command.ReplyToCommand(result.Message);
            return;
        }

        var slotArg = mode == "slot" ? 2 : 1;
        if (!TryParseSlotAt(command, slotArg, out var slot) || command.ArgCount <= slotArg + 1)
        {
            command.ReplyToCommand("usage: dtr_load slot <slot> <path.dtr>");
            command.ReplyToCommand("legacy usage: dtr_load <slot> <path.dtr>");
            return;
        }

        var path = command.GetArg(slotArg + 1);
        if (!IsReplaySlotStillSafe(slot))
        {
            command.ReplyToCommand($"dtr: refused to load slot {slot}: not a safe bot target");
            return;
        }

        var ok = BotControllerNative.LoadReplayFromFile(slot, path, out var replayMetadata);
        if (ok)
        {
            RememberLoadedSlot(slot);
            TrackLoadedReplay(slot, path, $"slot{slot}", replayMetadata: replayMetadata);
        }

        command.ReplyToCommand(ok
            ? $"dtr: loaded slot {slot}: {path}"
            : $"dtr: failed to load slot {slot}: {path} ({BotControllerNative.LastLoadError})");
    }

    [ConsoleCommand("dtr_load_round", "dtr_load_round <manifest.json> <source_round>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void LoadRoundCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseRoundArgs(command, "dtr_load_round", out var manifestPath, out var round))
            return;

        ActivatePendingReplayRetentionPriority();
        var result = LoadRound(manifestPath, round);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("dtr_play_loaded", "dtr_play_loaded [loop:0|1]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PlayLoadedCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        var loop = command.ArgCount >= 2 && command.GetArg(1) != "0";
        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: false))
            return;
        command.ReplyToCommand("[DTR WARN] dtr_play loaded is manual/debug playback; it bypasses round_start/round_freeze_end lifecycle alignment.");
        command.ReplyToCommand(PlayLoaded(loop));
    }

    [ConsoleCommand("dtr_play", "dtr_play <loaded|slot> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PlayCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_play loaded [loop:0|1] | dtr_play slot <slot> [loop:0|1]");
            command.ReplyToCommand("legacy usage: dtr_play <slot> [loop:0|1]");
            return;
        }

        var mode = command.GetArg(1).ToLowerInvariant();
        if (mode == "loaded")
        {
            var loopLoaded = command.ArgCount >= 3 && command.GetArg(2) != "0";
            if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: false))
                return;
            command.ReplyToCommand("[DTR WARN] dtr_play loaded is manual/debug playback; it bypasses round_start/round_freeze_end lifecycle alignment.");
            command.ReplyToCommand(PlayLoaded(loopLoaded));
            return;
        }

        var slotArg = mode == "slot" ? 2 : 1;
        if (!TryParseSlotAt(command, slotArg, out var slot))
            return;
        var loop = command.ArgCount > slotArg + 1 && command.GetArg(slotArg + 1) != "0";
        if (_loadedReplays.TryGetValue(slot, out var replay))
            PreloadReplayWeaponsForSlot(slot, replay);
        _lastEnsuredWeaponDef.Remove(slot);

        if (!IsReplaySlotStillSafe(slot))
        {
            command.ReplyToCommand($"dtr: refused to play slot {slot}: not a safe bot target");
            return;
        }
        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: false))
            return;

        var ok = StartReplayForSlot(slot, loop);
        if (ok)
        {
            MarkReplayStarted(slot);
        }
        var state = ok ? default : BotControllerNative.GetReplayState(slot);
        command.ReplyToCommand(ok
            ? $"dtr: playing slot {slot}, loop={loop}"
            : $"dtr: failed to play slot {slot} (cursor={state.Cursor}, total={state.Total})");
    }

    [ConsoleCommand("dtr_stop", "dtr_stop <sequence|pool|replay|slot|all> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_stop sequence|pool|replay|slot <slot>|all");
            command.ReplyToCommand("legacy usage: dtr_stop <slot>");
            return;
        }

        switch (command.GetArg(1).ToLowerInvariant())
        {
            case "sequence":
            case "seq":
                StopSequenceState();
                command.ReplyToCommand("[DTR OK] sequence scheduling stopped");
                return;
            case "pool":
                StopPoolState();
                command.ReplyToCommand("[DTR OK] pool scheduling stopped");
                return;
            case "replay":
            case "loaded":
                StopLoadedReplaySlots("manual_stop_replay");
                command.ReplyToCommand("[DTR OK] current loaded/running replay slots stopped");
                return;
            case "all":
                StopAllState("manual_stop_all");
                command.ReplyToCommand("[DTR OK] all DemoTracer replay state stopped");
                return;
            case "slot":
                if (!TryParseSlotAt(command, 2, out var namedSlot))
                    return;
                StopOneSlot(command, namedSlot, "manual_stop");
                return;
            default:
                if (!TryParseSlotAt(command, 1, out var legacySlot))
                    return;
                StopOneSlot(command, legacySlot, "manual_stop");
                return;
        }
    }

    [ConsoleCommand("dtr_kick", "dtr_kick <exact-name>|slot <slot>|sid <steamid64>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void KickCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            ReplyKickUsage(command);
            return;
        }

        var snapshot = BuildTickPlayerSnapshot();
        var candidates = BuildKickCandidates(snapshot);
        if (candidates.Count == 0)
        {
            command.ReplyToCommand("[DTR ERR] no kickable DemoTracer replay bots found");
            return;
        }

        var mode = command.GetArg(1).Trim().ToLowerInvariant();
        List<DtrKickCandidate> matches;
        string label;
        if (mode is "slot")
        {
            if (!TryParseSlotAt(command, 2, out var slot))
                return;
            matches = candidates.Where(candidate => candidate.Slot == slot).ToList();
            label = $"slot={slot}";
        }
        else if (mode is "sid" or "steamid" or "steam")
        {
            if (command.ArgCount < 3 ||
                !ulong.TryParse(command.GetArg(2), NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) ||
                steamId == 0)
            {
                command.ReplyToCommand("usage: dtr_kick sid <steamid64>");
                return;
            }
            matches = candidates.Where(candidate => candidate.SteamId == steamId).ToList();
            label = $"sid={steamId}";
        }
        else
        {
            var name = CommandArgumentsFrom(command, 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                ReplyKickUsage(command);
                return;
            }
            matches = candidates
                .Where(candidate =>
                    candidate.LoadedName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    candidate.LiveName.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            label = $"name=\"{name}\"";
        }

        if (matches.Count == 0)
        {
            command.ReplyToCommand($"[DTR ERR] no unique DemoTracer replay bot matched {label}");
            return;
        }
        if (matches.Count > 1)
        {
            command.ReplyToCommand($"[DTR ERR] ambiguous dtr_kick target for {label}; choose a slot explicitly.");
            foreach (var candidate in matches)
                command.ReplyToCommand($"[DTR HINT] dtr_kick slot {candidate.Slot}  {FormatKickCandidate(candidate)}");
            return;
        }

        KickReplayCandidate(command, matches[0]);
    }

    private void ReplyKickUsage(CommandInfo command)
    {
        command.ReplyToCommand("usage: dtr_kick <exact-name>");
        command.ReplyToCommand("usage: dtr_kick slot <slot>");
        command.ReplyToCommand("usage: dtr_kick sid <steamid64>");
    }

    private List<DtrKickCandidate> BuildKickCandidates(TickPlayerSnapshot snapshot)
    {
        var slots = new SortedSet<int>();
        foreach (var slot in _loadedSlots)
            slots.Add(slot);
        foreach (var slot in _loadedReplays.Keys)
            slots.Add(slot);
        foreach (var slot in _demoTracerOwnedSlots)
            slots.Add(slot);
        foreach (var slot in _retainedBotHiderPresentation.Keys)
            slots.Add(slot);
        foreach (var slot in NativeReplaySlots())
        {
            var state = BotControllerNative.GetReplayState(slot);
            if (state.Playing || state.Total > 0)
                slots.Add(slot);
        }

        var candidates = new List<DtrKickCandidate>();
        foreach (var slot in slots)
        {
            if (slot is < 0 or >= MaxPlayerSlots)
                continue;
            if (!snapshot.TryGetSlot(slot, out var controller) ||
                controller is not { IsValid: true } ||
                !IsReplaySlotStillSafe(slot, snapshot))
            {
                continue;
            }

            _loadedReplays.TryGetValue(slot, out var replay);
            _retainedBotHiderPresentation.TryGetValue(slot, out var retained);
            var replayPlayerName = !string.IsNullOrWhiteSpace(replay.PlayerName)
                ? replay.PlayerName
                : retained.PlayerName;
            var replaySteamId = replay.SteamId != 0
                ? replay.SteamId
                : retained.SteamId;
            candidates.Add(new DtrKickCandidate(
                slot,
                controller.UserId,
                controller.Team,
                controller.PlayerName ?? string.Empty,
                replayPlayerName ?? string.Empty,
                replaySteamId,
                replay.RetentionRank > 0
                    ? replay.RetentionRank
                    : retained.RetentionRank > 0
                        ? retained.RetentionRank
                        : ReplayRetentionPriorityParser.MaxPlayersPerTeam));
        }

        return candidates;
    }

    private void KickReplayCandidate(CommandInfo command, DtrKickCandidate candidate)
    {
        if (!candidate.UserId.HasValue)
        {
            command.ReplyToCommand($"[DTR ERR] cannot kick slot {candidate.Slot}: missing userid");
            return;
        }

        var slot = candidate.Slot;
        var userId = candidate.UserId.Value;
        StopVoiceTestPlayback("dtr_kick", printSummary: false);
        if (!TryReleaseAndKickReplayCandidate(candidate, "dtr_kick", out var stopped, out var unloaded))
        {
            command.ReplyToCommand($"[DTR ERR] cannot kick slot {candidate.Slot}: missing userid");
            return;
        }

        command.ReplyToCommand(
            $"[DTR OK] kicked slot={slot} userid={userId.ToString(CultureInfo.InvariantCulture)} stopped={FormatOnOff(stopped)} unloaded={FormatOnOff(unloaded)}");
    }

    private bool TryReleaseAndKickReplayCandidate(
        DtrKickCandidate candidate,
        string reason,
        out bool stopped,
        out bool unloaded)
    {
        stopped = false;
        unloaded = false;
        if (!candidate.UserId.HasValue)
            return false;

        stopped = BotControllerNative.StopReplay(candidate.Slot);
        unloaded = BotControllerNative.UnloadReplay(candidate.Slot);
        ReleaseReplaySlot(candidate.Slot, reason);
        _loadedSlots.Remove(candidate.Slot);
        ForgetRetainedBotHiderPresentation(candidate.Slot);
        ForgetLoadedReplayMetadata(candidate.Slot);
        Server.ExecuteCommand($"kickid {candidate.UserId.Value.ToString(CultureInfo.InvariantCulture)}");
        return true;
    }

    private static string CommandArgumentsFrom(CommandInfo command, int startArg)
    {
        var parts = new List<string>();
        for (var i = startArg; i < command.ArgCount; i++)
            parts.Add(command.GetArg(i));
        return string.Join(' ', parts).Trim();
    }

    private static string FormatKickCandidate(DtrKickCandidate candidate)
    {
        var userId = candidate.UserId.HasValue
            ? candidate.UserId.Value.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        var steamId = candidate.SteamId == 0
            ? "unknown"
            : candidate.SteamId.ToString(CultureInfo.InvariantCulture);
        return $"userid={userId} sid={steamId} keep={candidate.RetentionRank} live=\"{EscapeConsoleString(candidate.LiveName)}\" loaded=\"{EscapeConsoleString(candidate.LoadedName)}\"";
    }

    [ConsoleCommand("dtr_stop_all", "dtr_stop_all")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StopAllCommand(CCSPlayerController? player, CommandInfo command)
    {
        StopAllState("manual_stop_all");
        command.ReplyToCommand("[DTR OK] all DemoTracer replay state stopped");
    }

    [ConsoleCommand("dtr_unload", "dtr_unload <slot>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void UnloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var hadRetainedPresentation = _retainedBotHiderPresentation.ContainsKey(slot);
        var ok = BotControllerNative.UnloadReplay(slot);
        if (ok || hadRetainedPresentation)
        {
            StopVoiceTestPlayback("unload", printSummary: false);
            _loadedSlots.Remove(slot);
            ReleaseReplaySlot(slot, "unload");
            ForgetRetainedBotHiderPresentation(slot);
            ForgetLoadedReplayMetadata(slot);
        }

        if (!ok && !hadRetainedPresentation)
        {
            command.ReplyToCommand(
                $"dtr: failed to unload slot {slot}: {BotControllerNative.LastLoadError}");
        }
        else
        {
            command.ReplyToCommand(ok
                ? $"dtr: unloaded slot {slot}"
                : $"dtr: cleared retained BotHider presentation for slot {slot}");
            if (ok && !string.IsNullOrWhiteSpace(BotControllerNative.LastLoadError))
                command.ReplyToCommand($"[DTR WARN] {BotControllerNative.LastLoadError}");
        }
    }

    private static TickPlayerSnapshot BuildTickPlayerSnapshot()
    {
        var controllers = FindPlayerControllers();
        return new TickPlayerSnapshot(controllers, FindTeamPlayers(controllers));
    }

    private List<CCSPlayerController> FindReplayTargets()
    {
        var players = FindTeamPlayers();
        var targets = players
            .Where(IsReplayTargetBot)
            .OrderBy(player => player.IsBot ? 0 : 1)
            .ThenBy(player => player.Slot)
            .ToList();
        return targets;
    }

    private bool IsReplayTargetBot(CCSPlayerController player)
    {
        return IsReplayTargetBot(player, null);
    }

    private bool IsReplayTargetBot(
        CCSPlayerController player,
        IReadOnlyList<CCSPlayerController>? playerControllers)
    {
        if (!IsReplayControllerSafe(player) || IsReplayPawnTakenByController(player, playerControllers))
            return false;
        return player.IsBot || _botHiderBridge.IsManagedBot(player.Slot);
    }

    private bool IsReplaySlotStillSafe(
        int slot,
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true } && IsReplayTargetBot(player, playerControllers);
    }

    private bool IsReplaySlotStillSafe(int slot, TickPlayerSnapshot playerSnapshot)
    {
        return playerSnapshot.TryGetSlot(slot, out var player) &&
               player is { IsValid: true } &&
               IsReplayTargetBot(player, playerSnapshot.Controllers);
    }

    private static bool IsReplayControllerSafe(CCSPlayerController player)
    {
        return TryGetControllingBotState(player, out var controllingBot) && !controllingBot;
    }

    private static bool TryGetControllingBotState(CCSPlayerController player, out bool controllingBot)
    {
        controllingBot = false;
        if (player is not { IsValid: true })
            return false;

        try
        {
            controllingBot = player.ControllingBot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReplayPawnTakenByController(
        CCSPlayerController replayTarget,
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        if (replayTarget.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
            return true;

        var replayPawnIndex = replayPawn.Value.Index;
        var controllers = playerControllers ?? FindPlayerControllers();
        foreach (var controller in controllers)
        {
            if (controller is not { IsValid: true } || controller.Slot == replayTarget.Slot)
                continue;
            if (!TryGetControllingBotState(controller, out var controllingBot) || !controllingBot)
                continue;

            if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } controlledPawn &&
                controlledPawn.Value.Index == replayPawnIndex)
                return true;

            if (controller.OriginalControllerOfCurrentPawn is { IsValid: true, Value.IsValid: true } original &&
                original.Value.Slot == replayTarget.Slot)
                return true;
        }

        return false;
    }

    private static List<CCSPlayerController> FindPlayerControllers()
    {
        return Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid)
            .ToList();
    }

    private static List<CCSPlayerController> FindTeamPlayers(
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        return (playerControllers ?? FindPlayerControllers())
            .Where(player => player.IsValid &&
                             (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist) &&
                             player.PlayerPawn is { IsValid: true, Value.IsValid: true })
            .OrderBy(player => player.Team)
            .ThenBy(player => player.Slot)
            .ToList();
    }
}
