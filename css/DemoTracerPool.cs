using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    [ConsoleCommand("dtr_pool_restart", "dtr_pool_restart <pool_manifest.json> [server_round]")]
    public void PoolRestartCommand(CCSPlayerController? player, CommandInfo command)
        => RunPoolPlan(command, "dtr_pool_restart", restart: true);

    [ConsoleCommand("dtr_run_pool", "dtr_run_pool <pool_manifest.json> [server_round]")]
    public void RunPoolCommand(CCSPlayerController? player, CommandInfo command)
        => RunPoolPlan(command, "dtr_run_pool", restart: false);

    [ConsoleCommand("dtr_stop_pool", "dtr_stop_pool")]
    public void StopPoolCommand(CCSPlayerController? player, CommandInfo command)
    {
        _poolActive = false;
        _poolManifest = null;
        _poolManifestPath = string.Empty;
        _poolRoundIndex = 0;
        _poolUsedCandidates.Clear();
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

        StopAndUnloadLoaded();
        _sequenceActive = false;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _armed = false;
        _armedPrepared = false;
        _armedManifestPath = string.Empty;
        _armedSourceRound = -1;
        InvalidateFreezePreroll();
        _poolManifestPath = poolPath;
        _poolManifest = pool;
        _poolRoundIndex = startRound;
        _poolUsedCandidates.Clear();
        _poolActive = pool.Candidates.Count > 0;

        command.ReplyToCommand(
            _poolActive
                ? restart
                    ? $"[DTR OK] Planned POOL. pool_manifest=\"{poolPath}\"; server_round={_poolRoundIndex}; restart=now."
                    : $"[DTR OK] Armed POOL. pool_manifest=\"{poolPath}\"; server_round={_poolRoundIndex}; waiting for next round_start/freeze_end."
                : "dtr: pool manifest has no candidates");
        if (_poolActive)
        {
            command.ReplyToCommand("[DTR WARN] Pool currently selects candidates at round_freeze_end; bounded freeze pre-roll is skipped for pool rounds.");
            IssueRestartIfRequested(command, restart);
        }
    }

    private void StartNextPoolRound()
    {
        var pool = _poolManifest;
        if (pool == null || pool.Candidates.Count == 0)
        {
            _poolActive = false;
            Server.PrintToConsole("dtr: pool stopped, no candidates");
            return;
        }

        if (!TryChoosePoolCandidate(pool, _poolRoundIndex, out var candidate, out var reason) ||
            candidate == null)
        {
            Server.PrintToConsole($"dtr: pool skipped round {_poolRoundIndex}: {reason}");
            _poolRoundIndex++;
            return;
        }

        var poolDir = Path.GetDirectoryName(Path.GetFullPath(_poolManifestPath)) ?? ".";
        var manifestPath = Path.IsPathRooted(candidate.Manifest)
            ? candidate.Manifest
            : Path.GetFullPath(Path.Combine(poolDir, candidate.Manifest.Replace('/', Path.DirectorySeparatorChar)));
        var load = LoadRound(manifestPath, candidate.SourceRound);
        if (!load.Ok)
        {
            Server.PrintToConsole(
                $"dtr: pool failed round {_poolRoundIndex}: {load.Message}; candidate={candidate.DemoStem} r{candidate.SourceRound}");
            _poolRoundIndex++;
            return;
        }

        PreloadLoadedReplays();
        var play = StartLoaded(loop: false);
        var key = PoolCandidateKey(candidate);
        _poolUsedCandidates.Add(key);
        if (_poolUsedCandidates.Count > Math.Max(64, pool.Candidates.Count / 2))
            _poolUsedCandidates.Clear();

        Server.PrintToConsole(
            $"dtr: pool round {_poolRoundIndex} -> {candidate.DemoStem} r{candidate.SourceRound} ({reason}); {load.Message}; {play}");
        _poolRoundIndex++;
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

        long bestScore = long.MaxValue;
        foreach (var candidate in pool.Candidates)
        {
            if (candidate.PistolRound != pistolRound)
                continue;
            if (pistolRound && candidate.SourceRound is not 0 and not 12)
                continue;
            if (!pistolRound && candidate.SourceRound is 0 or 12)
                continue;

            var score = ScorePoolCandidate(candidate, tEconomy, ctEconomy, roundIndex);
            if (score >= bestScore)
                continue;
            bestScore = score;
            selected = candidate;
        }

        if (selected == null)
        {
            reason = pistolRound
                ? "no pistol candidates from source round 0/12"
                : "no non-pistol candidates";
            return false;
        }

        reason =
            $"target T={tEconomy.Class}:{tEconomy.EquipmentValue} CT={ctEconomy.Class}:{ctEconomy.EquipmentValue}, score={bestScore}";
        return true;
    }

    private long ScorePoolCandidate(
        RoundPoolCandidate candidate,
        TeamEconomySnapshot targetT,
        TeamEconomySnapshot targetCt,
        int roundIndex)
    {
        var score = 0L;
        score += Math.Abs((long)candidate.TEconomy.BestEquipmentValue - targetT.EquipmentValue);
        score += Math.Abs((long)candidate.CtEconomy.BestEquipmentValue - targetCt.EquipmentValue);
        score += EconomyClassPenalty(candidate.TEconomy.Class, targetT.Class);
        score += EconomyClassPenalty(candidate.CtEconomy.Class, targetCt.Class);
        score += Math.Abs(candidate.SourceRound - (roundIndex % 24)) * 25L;
        if (_poolUsedCandidates.Contains(PoolCandidateKey(candidate)))
            score += 10_000L;
        score += StableHash(PoolCandidateKey(candidate), roundIndex) % 997;
        return score;
    }

    private TeamEconomySnapshot SnapshotCurrentTeamEconomy(CsTeam team, bool pistolRound)
    {
        var bots = FindReplayTargets()
            .Where(bot => bot.Team == team && bot.PawnIsAlive)
            .ToList();
        uint equipment = 0;
        foreach (var bot in bots)
        {
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

        var economyClass = ClassifyEconomy(bots.Count, equipment, pistolRound);
        return new TeamEconomySnapshot(equipment, economyClass);
    }

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

    private static bool IsPistolRoundIndex(int round) => round is 0 or 12;

    private static string PoolCandidateKey(RoundPoolCandidate candidate)
        => $"{candidate.Manifest}|{candidate.SourceRound}";

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
        _poolActive = false;
        _poolManifest = null;
        _poolManifestPath = string.Empty;
        _poolRoundIndex = 0;
        _poolUsedCandidates.Clear();
        InvalidateFreezePreroll();
    }
}
