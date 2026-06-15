using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cs2DemoBotMimic;

public sealed class Cs2DemoBotMimicPlugin : BasePlugin
{
    public override string ModuleName => "CS2 Demo BotMimic";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "unicbm";
    public override string ModuleDescription => "Loads .cs2rec files into the BotMimic Metamod runtime.";

    private static readonly byte[] RecMagic =
    [
        (byte)'C', (byte)'S', (byte)'2', (byte)'B',
        (byte)'M', (byte)'R', (byte)'E', (byte)'C'
    ];

    private readonly List<int> _loadedSlots = new();
    private readonly Dictionary<int, LoadedReplay> _loadedReplays = new();
    private readonly Dictionary<int, int> _lastEnsuredWeaponDef = new();
    private readonly Dictionary<int, int> _lastReplayWeaponDef = new();
    private readonly Dictionary<int, int> _lastLockedWeaponTarget = new();
    private readonly Dictionary<int, PendingWeaponAlign> _pendingWeaponAlign = new();
    private readonly HashSet<int> _rebuiltInventorySlots = new();
    private readonly HashSet<int> _lastPlayingSlots = new();
    private readonly Dictionary<int, float> _replayStartedAt = new();
    private readonly BotHiderMemoryProbe _botHiderProbe = new();

    private bool _armed;
    private bool _armedLoop;
    private string _armedLabel = string.Empty;

    private bool _sequenceActive;
    private string _sequenceManifestPath = string.Empty;
    private int[] _sequenceRounds = [];
    private int _sequenceIndex;
    private bool _sequencePrepared;
    private int _sequencePreparedRound = -1;
    private bool _poolActive;
    private string _poolManifestPath = string.Empty;
    private RoundPoolManifest? _poolManifest;
    private int _poolRoundIndex;
    private readonly HashSet<string> _poolUsedCandidates = new();
    private bool _weaponAlignEnabled = true;
    private bool _weaponAlignFrameQueued;
    private HandoffMode _handoffMode = HandoffMode.DeathOrContact;
    private bool _handoffAllSlots = true;
    private bool _partialReplayEnabled = true;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnTick>(OnTick);
        Server.PrintToConsole("cs2bm: CSS control plugin loaded");
    }

    public override void Unload(bool hotReload)
    {
        _botHiderProbe.Dispose();
    }

    [ConsoleCommand("cs2bm_run_manifest", "cs2bm_run_manifest <manifest.json> [start-round]")]
    public void RunManifestCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: cs2bm_run_manifest <manifest.json> [start-round]");
            return;
        }

        var manifestPath = command.GetArg(1);
        if (!TryReadManifest(manifestPath, out var manifest, out var readError))
        {
            command.ReplyToCommand($"cs2bm: failed to read manifest: {readError}");
            return;
        }

        var rounds = manifest.Files
            .Select(file => file.Round)
            .Distinct()
            .Order()
            .ToArray();

        if (rounds.Length == 0)
        {
            command.ReplyToCommand("cs2bm: manifest has no playable rounds");
            return;
        }

        var startRound = rounds[0];
        if (command.ArgCount >= 3 &&
            (!int.TryParse(command.GetArg(2), out startRound) || !rounds.Contains(startRound)))
        {
            command.ReplyToCommand("cs2bm: start round is not present in manifest");
            return;
        }

        StopAndUnloadLoaded();
        _sequenceManifestPath = manifestPath;
        _sequenceRounds = rounds;
        _sequenceIndex = Array.IndexOf(rounds, startRound);
        _sequenceActive = _sequenceIndex >= 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _armed = false;
        _poolActive = false;

        command.ReplyToCommand(
            $"cs2bm: sequence armed, {rounds.Length - _sequenceIndex} rounds from round {startRound}; next round_start prepares bots, round_freeze_end starts playback");
    }

    [ConsoleCommand("cs2bm_stop_sequence", "cs2bm_stop_sequence")]
    public void StopSequenceCommand(CCSPlayerController? player, CommandInfo command)
    {
        _sequenceActive = false;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        command.ReplyToCommand("cs2bm: sequence stopped");
    }

    [ConsoleCommand("cs2bm_run_pool", "cs2bm_run_pool <pool_manifest.json> [start-round]")]
    public void RunPoolCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: cs2bm_run_pool <pool_manifest.json> [start-round]");
            return;
        }

        var poolPath = command.GetArg(1);
        if (!TryReadPoolManifest(poolPath, out var pool, out var readError))
        {
            command.ReplyToCommand($"cs2bm: failed to read pool manifest: {readError}");
            return;
        }

        var startRound = 0;
        if (command.ArgCount >= 3 &&
            (!int.TryParse(command.GetArg(2), out startRound) || startRound < 0))
        {
            command.ReplyToCommand("cs2bm: start round must be a non-negative integer");
            return;
        }

        StopAndUnloadLoaded();
        _sequenceActive = false;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _poolManifestPath = poolPath;
        _poolManifest = pool;
        _poolRoundIndex = startRound;
        _poolUsedCandidates.Clear();
        _poolActive = pool.Candidates.Count > 0;

        command.ReplyToCommand(
            _poolActive
                ? $"cs2bm: pool armed, candidates={pool.Candidates.Count}, next round={_poolRoundIndex}; round_freeze_end selects by economy"
                : "cs2bm: pool manifest has no candidates");
    }

    [ConsoleCommand("cs2bm_stop_pool", "cs2bm_stop_pool")]
    public void StopPoolCommand(CCSPlayerController? player, CommandInfo command)
    {
        _poolActive = false;
        _poolManifest = null;
        _poolManifestPath = string.Empty;
        _poolRoundIndex = 0;
        _poolUsedCandidates.Clear();
        command.ReplyToCommand("cs2bm: pool stopped");
    }

    [ConsoleCommand("cs2bm_weapon_align", "cs2bm_weapon_align <0|1>")]
    public void WeaponAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _weaponAlignEnabled = command.GetArg(1) != "0";
        if (!_weaponAlignEnabled)
        {
            _pendingWeaponAlign.Clear();
            _rebuiltInventorySlots.Clear();
            _lastReplayWeaponDef.Clear();
            _lastLockedWeaponTarget.Clear();
            foreach (var slot in _loadedSlots)
                BotMimicNative.UnlockWeaponSlot(slot);
        }

        command.ReplyToCommand($"cs2bm: weapon_align={_weaponAlignEnabled}");
    }

    [ConsoleCommand("cs2bm_handoff", "cs2bm_handoff <off|death|contact|death_or_contact> [all|slot]")]
    public void HandoffCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            if (!TryParseHandoffMode(command.GetArg(1), out var mode))
            {
                command.ReplyToCommand("usage: cs2bm_handoff <off|death|contact|death_or_contact> [all|slot]");
                return;
            }
            _handoffMode = mode;
        }

        if (command.ArgCount >= 3)
        {
            var scope = command.GetArg(2);
            if (scope.Equals("slot", StringComparison.OrdinalIgnoreCase))
                _handoffAllSlots = false;
            else if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
                _handoffAllSlots = true;
            else
            {
                command.ReplyToCommand("usage: cs2bm_handoff <off|death|contact|death_or_contact> [all|slot]");
                return;
            }
        }

        command.ReplyToCommand(
            $"cs2bm: handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")}");
    }

    [ConsoleCommand("cs2bm_partial", "cs2bm_partial <0|1>")]
    public void PartialCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _partialReplayEnabled = command.GetArg(1) != "0";

        command.ReplyToCommand($"cs2bm: partial_replay={_partialReplayEnabled}");
    }


    [ConsoleCommand("cs2bm_load", "cs2bm_load <slot> <absolute-or-game-path.cs2rec>")]
    public void LoadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseSlot(command, out var slot) || command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: cs2bm_load <slot> <path.cs2rec>");
            return;
        }

        var path = command.GetArg(2);
        var ok = BotMimicNative.LoadReplayFromFile(slot, path);
        if (ok)
        {
            RememberLoadedSlot(slot);
            TrackLoadedReplay(slot, path, $"slot{slot}");
        }

        command.ReplyToCommand(ok
            ? $"cs2bm: loaded slot {slot}: {path}"
            : $"cs2bm: failed to load slot {slot}: {path}");
    }

    [ConsoleCommand("cs2bm_load_round", "cs2bm_load_round <manifest.json> <round>")]
    public void LoadRoundCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseRoundArgs(command, out var manifestPath, out var round))
            return;

        var result = LoadRound(manifestPath, round);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("cs2bm_arm_round", "cs2bm_arm_round <manifest.json> <round> [loop:0|1]")]
    public void ArmRoundCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseRoundArgs(command, out var manifestPath, out var round))
            return;

        var loop = command.ArgCount >= 4 && command.GetArg(3) != "0";
        var result = LoadRound(manifestPath, round);
        if (!result.Ok)
        {
            command.ReplyToCommand(result.Message);
            return;
        }

        _sequenceActive = false;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _poolActive = false;
        _armed = true;
        _armedLoop = loop;
        _armedLabel = $"round={round} manifest={manifestPath}";
        PreloadLoadedReplays();
        command.ReplyToCommand($"cs2bm: armed {_loadedSlots.Count} slots, will start on round_freeze_end, loop={loop}");
    }

    [ConsoleCommand("cs2bm_play_loaded", "cs2bm_play_loaded [loop:0|1]")]
    public void PlayLoadedCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        var loop = command.ArgCount >= 2 && command.GetArg(1) != "0";
        command.ReplyToCommand(PlayLoaded(loop));
    }

    [ConsoleCommand("cs2bm_play", "cs2bm_play <slot> [loop:0|1]")]
    public void PlayCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var loop = command.ArgCount >= 3 && command.GetArg(2) != "0";
        if (_loadedReplays.TryGetValue(slot, out var replay))
            PreloadReplayWeaponsForSlot(slot, replay);
        _lastEnsuredWeaponDef.Remove(slot);

        var ok = BotMimicNative.StartReplay(slot, loop);
        if (ok)
            MarkReplayStarted(slot);
        command.ReplyToCommand(ok
            ? $"cs2bm: playing slot {slot}, loop={loop}"
            : $"cs2bm: failed to play slot {slot}");
    }

    [ConsoleCommand("cs2bm_stop", "cs2bm_stop <slot>")]
    public void StopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var ok = BotMimicNative.StopReplay(slot);
        ReleaseReplaySlot(slot, "manual_stop");
        command.ReplyToCommand(ok
            ? $"cs2bm: stopped slot {slot}"
            : $"cs2bm: failed to stop slot {slot}");
    }

    [ConsoleCommand("cs2bm_stop_all", "cs2bm_stop_all")]
    public void StopAllCommand(CCSPlayerController? player, CommandInfo command)
    {
        foreach (var slot in _loadedSlots.ToArray())
        {
            BotMimicNative.StopReplay(slot);
            ReleaseReplaySlot(slot, "manual_stop_all");
        }

        _armed = false;
        _sequenceActive = false;
        _poolActive = false;
        _poolManifest = null;
        _poolManifestPath = string.Empty;
        _poolUsedCandidates.Clear();
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _pendingWeaponAlign.Clear();
        _rebuiltInventorySlots.Clear();
        _lastPlayingSlots.Clear();
        _replayStartedAt.Clear();
        command.ReplyToCommand($"cs2bm: stopped {_loadedSlots.Count} loaded slots");
    }

    [ConsoleCommand("cs2bm_unload", "cs2bm_unload <slot>")]
    public void UnloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var ok = BotMimicNative.UnloadReplay(slot);
        if (ok)
        {
            _loadedSlots.Remove(slot);
            _loadedReplays.Remove(slot);
            _lastEnsuredWeaponDef.Remove(slot);
            _lastReplayWeaponDef.Remove(slot);
            _lastLockedWeaponTarget.Remove(slot);
            _pendingWeaponAlign.Remove(slot);
            _rebuiltInventorySlots.Remove(slot);
            ReleaseReplaySlot(slot, "unload");
        }

        command.ReplyToCommand(ok
            ? $"cs2bm: unloaded slot {slot}"
            : $"cs2bm: failed to unload slot {slot}");
    }

    [ConsoleCommand("cs2bm_bots", "cs2bm_bots")]
    public void BotsCommand(CCSPlayerController? player, CommandInfo command)
    {
        var players = FindTeamPlayers();
        var strictBots = players.Count(candidate => candidate.IsBot);
        var managedBots = players.Count(candidate => _botHiderProbe.IsManagedBot(candidate.Slot));
        var candidates = players.Count(IsReplayTargetBot);
        command.ReplyToCommand(
            $"cs2bm: strict IsBot={strictBots}, BotHider managed={managedBots}, safe replay candidates={candidates}");
        foreach (var bot in players)
        {
            var managed = _botHiderProbe.IsManagedBot(bot.Slot);
            command.ReplyToCommand(
                $"slot={bot.Slot} team={bot.Team} isBot={bot.IsBot} managed={managed} candidate={IsReplayTargetBot(bot)} name={bot.PlayerName}");
        }
    }

    [ConsoleCommand("cs2bm_status", "cs2bm_status <slot>")]
    public void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var state = BotMimicNative.GetReplayState(slot);
        var sequence = _sequenceActive && _sequenceIndex < _sequenceRounds.Length
            ? $" sequence_next={_sequenceRounds[_sequenceIndex]}"
            : string.Empty;
        var pool = _poolActive
            ? $" pool_next={_poolRoundIndex}"
            : string.Empty;
        command.ReplyToCommand(
            $"cs2bm: abi={BotMimicNative.AbiVersion} slot={slot} playing={state.Playing} cursor={state.Cursor} total={state.Total} handoff={FormatHandoffMode(_handoffMode)} partial={_partialReplayEnabled}{sequence}{pool}");
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_sequenceActive)
            PrepareNextSequenceRound("round_start");

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (_sequenceActive)
        {
            StartPreparedSequenceRound();
            return HookResult.Continue;
        }

        if (_poolActive)
        {
            StartNextPoolRound();
            return HookResult.Continue;
        }

        if (!_armed)
            return HookResult.Continue;

        var message = StartLoaded(_armedLoop);
        Server.PrintToConsole($"cs2bm: auto-start {_armedLabel}: {message}");
        _armed = false;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (HandoffIncludesDeath(_handoffMode) && HasActiveReplaySlots())
        {
            var triggerSlot = GetDeathHandoffSlot(@event);
            HandoffActiveReplays("player_death", triggerSlot);
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        if (_loadedSlots.Count == 0)
            return;

        foreach (var slot in _loadedSlots.ToArray())
        {
            var state = BotMimicNative.GetReplayState(slot);
            if (!state.Playing)
            {
                if (_lastPlayingSlots.Contains(slot))
                    ReleaseReplaySlot(slot, "replay_finished");
                continue;
            }

            if (!_lastPlayingSlots.Contains(slot))
                MarkReplayStarted(slot);

            if (HandoffIncludesContact(_handoffMode) && ReplayHasPassedHandoffGrace(slot) &&
                ReplayBotSeesEnemy(slot))
            {
                HandoffActiveReplays($"enemy_contact_slot{slot}", slot);
                continue;
            }

            if (!_weaponAlignEnabled)
                continue;
            if (!BotMimicNative.TryGetReplayTick(slot, out var tick))
                continue;

            ApplyReplayWeaponPreset(slot, tick.WeaponDefIndex, allowSlotReplacement: true, force: false);
        }
    }

    private bool PrepareNextSequenceRound(string reason)
    {
        if (_sequenceIndex < 0 || _sequenceIndex >= _sequenceRounds.Length)
        {
            _sequenceActive = false;
            Server.PrintToConsole("cs2bm: sequence complete");
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
            Server.PrintToConsole($"cs2bm: sequence stopped while preparing round {round}: {load.Message}");
            return false;
        }

        PreloadLoadedReplays();
        _sequencePrepared = true;
        _sequencePreparedRound = round;
        Server.PrintToConsole($"cs2bm: prepared sequence round {round} on {reason}: {load.Message}");
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
        Server.PrintToConsole($"cs2bm: sequence round {round} start on round_freeze_end: {play}");

        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _sequenceIndex++;
        if (_sequenceIndex >= _sequenceRounds.Length)
            _sequenceActive = false;
    }

    private void StartNextPoolRound()
    {
        var pool = _poolManifest;
        if (pool == null || pool.Candidates.Count == 0)
        {
            _poolActive = false;
            Server.PrintToConsole("cs2bm: pool stopped, no candidates");
            return;
        }

        if (!TryChoosePoolCandidate(pool, _poolRoundIndex, out var candidate, out var reason) ||
            candidate == null)
        {
            Server.PrintToConsole($"cs2bm: pool skipped round {_poolRoundIndex}: {reason}");
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
                $"cs2bm: pool failed round {_poolRoundIndex}: {load.Message}; candidate={candidate.DemoStem} r{candidate.SourceRound}");
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
            $"cs2bm: pool round {_poolRoundIndex} -> {candidate.DemoStem} r{candidate.SourceRound} ({reason}); {load.Message}; {play}");
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

    private static bool CheckAbi(CommandInfo command)
    {
        if (BotMimicNative.IsCompatible)
            return true;

        command.ReplyToCommand(
            $"cs2bm: ABI mismatch, runtime={BotMimicNative.AbiVersion}, expected={BotMimicNative.ExpectedAbiVersion}");
        return false;
    }

    private static bool TryParseRoundArgs(CommandInfo command, out string manifestPath, out int round)
    {
        manifestPath = string.Empty;
        round = 0;
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: command <manifest.json> <round>");
            return false;
        }

        manifestPath = command.GetArg(1);
        if (int.TryParse(command.GetArg(2), out round) && round >= 0)
            return true;

        command.ReplyToCommand("cs2bm: round must be a non-negative integer");
        return false;
    }

    private static bool TryParseSlot(CommandInfo command, out int slot)
    {
        slot = 0;
        if (command.ArgCount >= 2 && int.TryParse(command.GetArg(1), out slot) && slot >= 0)
            return true;

        command.ReplyToCommand("usage: command <slot> ...");
        return false;
    }

    private LoadRoundResult LoadRound(string manifestPath, int round)
    {
        try
        {
            if (!TryReadManifest(manifestPath, out var manifest, out var readError))
                return LoadRoundResult.Fail($"cs2bm: failed to read manifest: {readError}");

            var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
            var roundFiles = manifest.Files
                .Where(file => file.Round == round)
                .OrderBy(file => file.Side, StringComparer.Ordinal)
                .ThenBy(file => file.SteamId)
                .ToList();
            if (roundFiles.Count == 0)
                return LoadRoundResult.Fail($"cs2bm: manifest has no files for round {round}");

            var allTFiles = roundFiles.Where(file => file.Side.Equals("t", StringComparison.OrdinalIgnoreCase)).ToList();
            var allCtFiles = roundFiles.Where(file => file.Side.Equals("ct", StringComparison.OrdinalIgnoreCase)).ToList();
            var targets = FindReplayTargets();
            var tBots = targets.Where(bot => bot.Team == CsTeam.Terrorist).OrderBy(bot => bot.Slot).ToList();
            var ctBots = targets.Where(bot => bot.Team == CsTeam.CounterTerrorist).OrderBy(bot => bot.Slot).ToList();

            if (!_partialReplayEnabled && (tBots.Count < allTFiles.Count || ctBots.Count < allCtFiles.Count))
            {
                return LoadRoundResult.Fail(
                    $"cs2bm: not enough bots, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            var tCount = Math.Min(allTFiles.Count, tBots.Count);
            var ctCount = Math.Min(allCtFiles.Count, ctBots.Count);
            if (tCount == 0 && ctCount == 0)
            {
                return LoadRoundResult.Fail(
                    $"cs2bm: no safe bot targets, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            var tFiles = allTFiles.Take(tCount).ToList();
            var ctFiles = allCtFiles.Take(ctCount).ToList();
            var skippedT = allTFiles.Count - tFiles.Count;
            var skippedCt = allCtFiles.Count - ctFiles.Count;

            StopAndUnloadLoaded();
            var loaded = new List<string>();
            if (!LoadSide(tFiles, tBots, manifestDir, loaded))
                return LoadRoundResult.Fail($"cs2bm: failed while loading round {round}");
            if (!LoadSide(ctFiles, ctBots, manifestDir, loaded))
                return LoadRoundResult.Fail($"cs2bm: failed while loading round {round}");

            var partial = skippedT > 0 || skippedCt > 0
                ? $" partial replay skipped T={skippedT}/CT={skippedCt}"
                : string.Empty;
            return LoadRoundResult.Success($"cs2bm: loaded {loaded.Count} replays for round {round}{partial}: {string.Join(", ", loaded)}");
        }
        catch (Exception ex)
        {
            return LoadRoundResult.Fail($"cs2bm: load round failed: {ex.Message}");
        }
    }

    private bool LoadSide(
        IReadOnlyList<ManifestFile> files,
        IReadOnlyList<CCSPlayerController> bots,
        string manifestDir,
        List<string> loaded)
    {
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var bot = bots[i];
            var slot = bot.Slot;
            var recPath = Path.IsPathRooted(file.Path)
                ? file.Path
                : Path.GetFullPath(Path.Combine(manifestDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));

            if (!BotMimicNative.LoadReplayFromFile(slot, recPath))
                return false;

            RememberLoadedSlot(slot);
            TrackLoadedReplay(
                slot,
                recPath,
                file.PlayerName,
                file.FirstWeaponDefIndex ?? -1,
                file.PreloadWeaponDefIndices);
            loaded.Add($"{file.Side}:slot{slot}:{file.PlayerName}");
        }
        return true;
    }

    private string PlayLoaded(bool loop)
    {
        PreloadLoadedReplays();
        return StartLoaded(loop);
    }

    private void PreloadLoadedReplays()
    {
        if (!_weaponAlignEnabled)
            return;

        foreach (var slot in _loadedSlots)
        {
            if (_loadedReplays.TryGetValue(slot, out var replay))
                PreloadReplayWeaponsForSlot(slot, replay);
        }
    }

    private string StartLoaded(bool loop)
    {
        var ok = 0;
        foreach (var slot in _loadedSlots)
        {
            _lastEnsuredWeaponDef.Remove(slot);
            _lastReplayWeaponDef.Remove(slot);
            _lastLockedWeaponTarget.Remove(slot);

            if (BotMimicNative.StartReplay(slot, loop))
            {
                MarkReplayStarted(slot);
                ok++;
            }
        }
        return $"cs2bm: started {ok}/{_loadedSlots.Count} loaded slots, loop={loop}";
    }

    private void StopAndUnloadLoaded()
    {
        foreach (var slot in _loadedSlots.ToArray())
        {
            BotMimicNative.StopReplay(slot);
            ReleaseReplaySlot(slot, "unload_all");
            BotMimicNative.UnloadReplay(slot);
        }
        _loadedSlots.Clear();
        _loadedReplays.Clear();
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _pendingWeaponAlign.Clear();
        _rebuiltInventorySlots.Clear();
        _lastPlayingSlots.Clear();
        _replayStartedAt.Clear();
        _armed = false;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
    }

    private void MarkReplayStarted(int slot)
    {
        _lastPlayingSlots.Add(slot);
        _replayStartedAt[slot] = Server.CurrentTime;
    }

    private void ReleaseReplaySlot(int slot, string reason)
    {
        _lastPlayingSlots.Remove(slot);
        _replayStartedAt.Remove(slot);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _pendingWeaponAlign.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
        BotMimicNative.UnlockWeaponSlot(slot);
        ResetBotBrainForHandoff(slot);
        Server.PrintToConsole($"cs2bm: released slot={slot} reason={reason}");
    }

    private bool HasActiveReplaySlots()
    {
        foreach (var slot in _loadedSlots)
        {
            if (BotMimicNative.GetReplayState(slot).Playing)
                return true;
        }
        return false;
    }

    private void HandoffActiveReplays(string reason, int triggerSlot = -1)
    {
        var stopped = 0;
        var slots = (!_handoffAllSlots && triggerSlot >= 0)
            ? [triggerSlot]
            : _loadedSlots.ToArray();
        foreach (var slot in slots)
        {
            if (!BotMimicNative.GetReplayState(slot).Playing)
                continue;

            BotMimicNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason);
            stopped++;

            if (!_handoffAllSlots)
                break;
        }

        if (stopped > 0)
            Server.PrintToConsole($"cs2bm: handoff stopped {stopped} replay slot(s), reason={reason}");
    }

    private static int GetDeathHandoffSlot(EventPlayerDeath @event)
    {
        if (@event.Userid is { IsValid: true } victim)
            return victim.Slot;
        if (@event.Attacker is { IsValid: true } attacker)
            return attacker.Slot;
        return -1;
    }

    private bool ReplayHasPassedHandoffGrace(int slot)
    {
        return !_replayStartedAt.TryGetValue(slot, out var startedAt) ||
               Server.CurrentTime - startedAt >= 0.5f;
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

    private void RememberLoadedSlot(int slot)
    {
        if (!_loadedSlots.Contains(slot))
            _loadedSlots.Add(slot);
    }

    private void TrackLoadedReplay(
        int slot,
        string path,
        string playerName,
        int manifestFirstWeaponDefIndex = -1,
        IReadOnlyList<int>? manifestPreloadWeaponDefIndices = null)
    {
        TryReadWeaponPlan(path, out var scannedFirstDef, out var scannedPreloadDefs);
        var firstDef = NormalizeWeaponDefIndex(manifestFirstWeaponDefIndex);
        if (!IsKnownWeaponDefIndex(firstDef))
            firstDef = scannedFirstDef;

        var preloadDefs = NormalizePreloadWeaponDefs(
            manifestPreloadWeaponDefIndices is { Count: > 0 }
                ? manifestPreloadWeaponDefIndices
                : scannedPreloadDefs);
        _loadedReplays[slot] = new LoadedReplay(path, playerName, firstDef, preloadDefs);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
    }

    private void PreloadReplayWeaponsForSlot(int slot, LoadedReplay replay)
    {
        if (!_rebuiltInventorySlots.Contains(slot))
        {
            foreach (var def in replay.PreloadWeaponDefIndices)
                EnsureReplayWeaponForSlot(
                    slot,
                    def,
                    forceSwitch: false,
                    allowGive: true,
                    replaceConflictingSlot: false);
            _rebuiltInventorySlots.Add(slot);
        }

        ApplyReplayWeaponPreset(
            slot,
            ChooseStartWeaponDef(replay),
            allowSlotReplacement: true,
            force: true);
    }

    private void ApplyReplayWeaponPreset(
        int slot,
        int weaponDefIndex,
        bool allowSlotReplacement,
        bool force)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (!IsKnownWeaponDefIndex(normalized))
            return;

        if (!force &&
            _lastReplayWeaponDef.TryGetValue(slot, out var lastDef) &&
            lastDef == normalized)
            return;

        var target = GetReplayLockTarget(normalized);
        if (target > 0 &&
            (force ||
             !_lastLockedWeaponTarget.TryGetValue(slot, out var lastTarget) ||
             lastTarget != target))
        {
            if (BotMimicNative.LockWeaponSlot(slot, target))
                _lastLockedWeaponTarget[slot] = target;
        }

        if (allowSlotReplacement && IsSlotReplaceableWeaponDef(normalized))
        {
            EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: true,
                replaceConflictingSlot: false);
        }

        BotMimicNative.SwitchBotWeapon(slot, normalized);
        _lastReplayWeaponDef[slot] = normalized;
    }

    private static int ChooseStartWeaponDef(LoadedReplay replay)
    {
        var first = NormalizeWeaponDefIndex(replay.FirstWeaponDefIndex);
        if (IsKnownWeaponDefIndex(first) && GetReplayLockTarget(first) != 5)
            return first;

        foreach (var def in replay.PreloadWeaponDefIndices)
        {
            var normalized = NormalizeWeaponDefIndex(def);
            if (IsKnownWeaponDefIndex(normalized))
                return normalized;
        }

        return first;
    }

    private void QueueReplayWeaponAlign(int slot, int weaponDefIndex, bool forceSwitch)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
            return;
        if (_lastEnsuredWeaponDef.TryGetValue(slot, out var last) && last == normalized)
            return;

        _pendingWeaponAlign[slot] = new PendingWeaponAlign(normalized, forceSwitch);
        if (_weaponAlignFrameQueued)
            return;

        _weaponAlignFrameQueued = true;
        Server.NextFrame(ProcessPendingWeaponAlign);
    }

    private void ProcessPendingWeaponAlign()
    {
        _weaponAlignFrameQueued = false;
        if (!_weaponAlignEnabled || _pendingWeaponAlign.Count == 0)
        {
            _pendingWeaponAlign.Clear();
            return;
        }

        var pending = _pendingWeaponAlign.ToArray();
        _pendingWeaponAlign.Clear();
        foreach (var (slot, request) in pending)
            EnsureReplayWeaponForSlot(
                slot,
                request.WeaponDefIndex,
                request.ForceSwitch,
                allowGive: false,
                replaceConflictingSlot: false);
    }

    private void EnsureReplayWeaponForSlot(
        int slot,
        int weaponDefIndex,
        bool forceSwitch,
        bool allowGive,
        bool replaceConflictingSlot)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
            return;
        if (_lastEnsuredWeaponDef.TryGetValue(slot, out var last) && last == normalized && !forceSwitch)
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                replaceConflictingSlot,
                out var className))
        {
            _lastEnsuredWeaponDef[slot] = normalized;
            return;
        }

        _lastEnsuredWeaponDef[slot] = normalized;
        if (forceSwitch)
            BotMimicNative.SwitchBotWeapon(slot, normalized);

        Server.PrintToConsole($"cs2bm: aligned slot={slot} def={normalized} item={className}");
    }

    private static bool TryEnsureReplayWeapon(
        CCSPlayerController player,
        int weaponDefIndex,
        bool allowGive,
        bool replaceConflictingSlot,
        out string className)
    {
        className = string.Empty;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out className))
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true })
            return false;

        if (HasReplayWeapon(pawn, className))
            return true;

        var slot = GetReplayWeaponSlot(className);
        if (!allowGive)
            return false;
        if (slot is ReplayWeaponSlot.Other or ReplayWeaponSlot.Knife or
            ReplayWeaponSlot.C4 or ReplayWeaponSlot.Taser)
            return false;

        if (!replaceConflictingSlot && HasConflictingWeaponInSlot(pawn, slot, className))
            return false;

        // CS2 can leave invalid networked weapon entities when items are
        // removed from C# during playback. Do not delete conflicting slot
        // weapons here; runtime-level replacement needs a safer engine path.
        _ = replaceConflictingSlot;

        if (HasReplayWeapon(pawn, className))
            return true;

        try
        {
            player.GiveNamedItem(className);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"cs2bm: failed to give slot={player.Slot} item={className}: {ex.Message}");
            return false;
        }

        return HasReplayWeapon(pawn, className) || slot == ReplayWeaponSlot.Utility;
    }

    private static bool HasReplayWeapon(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, className))
                return true;
        }
        return false;
    }

    private static bool HasConflictingWeaponInSlot(
        CCSPlayerPawn pawn,
        ReplayWeaponSlot slot,
        string expectedClassName)
    {
        if (slot is not (ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary))
            return false;
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, expectedClassName))
                continue;
            if (GetReplayWeaponSlot(weapon.DesignerName) == slot)
                return true;
        }

        return false;
    }

    private static bool WeaponClassMatches(string actual, string expected)
    {
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return true;
        if (expected == "weapon_knife")
        {
            return actual.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
                   || actual.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static ReplayWeaponSlot GetReplayWeaponSlot(string className)
    {
        return className switch
        {
            "weapon_ak47" or "weapon_aug" or "weapon_awp" or "weapon_famas" or
            "weapon_g3sg1" or "weapon_galilar" or "weapon_m249" or "weapon_m4a1" or
            "weapon_m4a1_silencer" or "weapon_mac10" or "weapon_p90" or
            "weapon_mp5sd" or "weapon_mp7" or "weapon_mp9" or "weapon_ump45" or
            "weapon_xm1014" or "weapon_bizon" or "weapon_mag7" or "weapon_negev" or
            "weapon_sawedoff" or "weapon_nova" or "weapon_scar20" or "weapon_sg556" or
            "weapon_ssg08" => ReplayWeaponSlot.Primary,

            "weapon_deagle" or "weapon_elite" or "weapon_fiveseven" or "weapon_glock" or
            "weapon_hkp2000" or "weapon_p250" or "weapon_tec9" or "weapon_usp_silencer" or
            "weapon_cz75a" or "weapon_revolver" => ReplayWeaponSlot.Secondary,

            "weapon_flashbang" or "weapon_hegrenade" or "weapon_smokegrenade" or
            "weapon_molotov" or "weapon_decoy" or "weapon_incgrenade" => ReplayWeaponSlot.Utility,

            "weapon_c4" => ReplayWeaponSlot.C4,
            "weapon_taser" => ReplayWeaponSlot.Taser,
            "weapon_knife" => ReplayWeaponSlot.Knife,
            _ => ReplayWeaponSlot.Other
        };
    }

    private static int GetReplayLockTarget(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return 0;
        return GetReplayWeaponSlot(className) switch
        {
            ReplayWeaponSlot.Primary => 1,
            ReplayWeaponSlot.Secondary => 2,
            ReplayWeaponSlot.Knife or ReplayWeaponSlot.Taser => 3,
            ReplayWeaponSlot.Utility => 4,
            ReplayWeaponSlot.C4 => 5,
            _ => 0
        };
    }

    private static bool IsSlotReplaceableWeaponDef(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return false;
        return GetReplayWeaponSlot(className) is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary;
    }

    private static int NormalizeWeaponDefIndex(int weaponDefIndex)
    {
        if (weaponDefIndex == 42 || weaponDefIndex == 59 ||
            weaponDefIndex is >= 500 and < 600)
            return 42;
        return weaponDefIndex;
    }

    private static int[] NormalizePreloadWeaponDefs(IEnumerable<int> weaponDefIndices)
    {
        var seen = new HashSet<int>();
        var outDefs = new List<int>();
        foreach (var rawDef in weaponDefIndices)
        {
            var def = NormalizeWeaponDefIndex(rawDef);
            if (IsPreloadWeaponDefIndex(def) && seen.Add(def))
                outDefs.Add(def);
        }
        return outDefs.ToArray();
    }

    private static bool IsKnownWeaponDefIndex(int weaponDefIndex)
        => TryGetWeaponClassByDefIndex(weaponDefIndex, out _);

    private static bool IsPreloadWeaponDefIndex(int weaponDefIndex)
    {
        if (!IsKnownWeaponDefIndex(weaponDefIndex))
            return false;
        var slot = GetReplayWeaponSlot(TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            ? className
            : string.Empty);
        return slot is not ReplayWeaponSlot.Other
            and not ReplayWeaponSlot.Knife
            and not ReplayWeaponSlot.C4
            and not ReplayWeaponSlot.Taser;
    }

    private static bool TryGetWeaponClassByDefIndex(int weaponDefIndex, out string className)
    {
        className = NormalizeWeaponDefIndex(weaponDefIndex) switch
        {
            1 => "weapon_deagle",
            2 => "weapon_elite",
            3 => "weapon_fiveseven",
            4 => "weapon_glock",
            7 => "weapon_ak47",
            8 => "weapon_aug",
            9 => "weapon_awp",
            10 => "weapon_famas",
            11 => "weapon_g3sg1",
            13 => "weapon_galilar",
            14 => "weapon_m249",
            16 => "weapon_m4a1",
            17 => "weapon_mac10",
            19 => "weapon_p90",
            23 => "weapon_mp5sd",
            24 => "weapon_ump45",
            25 => "weapon_xm1014",
            26 => "weapon_bizon",
            27 => "weapon_mag7",
            28 => "weapon_negev",
            29 => "weapon_sawedoff",
            30 => "weapon_tec9",
            31 => "weapon_taser",
            32 => "weapon_hkp2000",
            33 => "weapon_mp7",
            34 => "weapon_mp9",
            35 => "weapon_nova",
            36 => "weapon_p250",
            38 => "weapon_scar20",
            39 => "weapon_sg556",
            40 => "weapon_ssg08",
            42 => "weapon_knife",
            43 => "weapon_flashbang",
            44 => "weapon_hegrenade",
            45 => "weapon_smokegrenade",
            46 => "weapon_molotov",
            47 => "weapon_decoy",
            48 => "weapon_incgrenade",
            49 => "weapon_c4",
            60 => "weapon_m4a1_silencer",
            61 => "weapon_usp_silencer",
            63 => "weapon_cz75a",
            64 => "weapon_revolver",
            _ => string.Empty
        };
        return className.Length > 0;
    }

    private static uint WeaponClassValue(string className)
    {
        return className.ToLowerInvariant() switch
        {
            "weapon_deagle" => 700,
            "weapon_elite" => 300,
            "weapon_fiveseven" => 500,
            "weapon_glock" => 200,
            "weapon_ak47" => 2700,
            "weapon_aug" => 3300,
            "weapon_awp" => 4750,
            "weapon_famas" => 2050,
            "weapon_g3sg1" => 5000,
            "weapon_galilar" => 1800,
            "weapon_m249" => 5200,
            "weapon_m4a1" => 3100,
            "weapon_mac10" => 1050,
            "weapon_p90" => 2350,
            "weapon_mp5sd" => 1500,
            "weapon_ump45" => 1200,
            "weapon_xm1014" => 2000,
            "weapon_bizon" => 1400,
            "weapon_mag7" => 1300,
            "weapon_negev" => 1700,
            "weapon_sawedoff" => 1100,
            "weapon_tec9" => 500,
            "weapon_taser" => 200,
            "weapon_hkp2000" => 200,
            "weapon_mp7" => 1500,
            "weapon_mp9" => 1250,
            "weapon_nova" => 1050,
            "weapon_p250" => 300,
            "weapon_scar20" => 5000,
            "weapon_sg556" => 3000,
            "weapon_ssg08" => 1700,
            "weapon_flashbang" => 200,
            "weapon_hegrenade" => 300,
            "weapon_smokegrenade" => 300,
            "weapon_molotov" => 400,
            "weapon_decoy" => 50,
            "weapon_incgrenade" => 600,
            "weapon_m4a1_silencer" => 2900,
            "weapon_usp_silencer" => 200,
            "weapon_cz75a" => 500,
            "weapon_revolver" => 600,
            _ => 0
        };
    }

    private static bool TryReadWeaponPlan(
        string path,
        out int firstWeaponDefIndex,
        out int[] preloadWeaponDefIndices)
    {
        firstWeaponDefIndex = -1;
        preloadWeaponDefIndices = [];
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadBytes(RecMagic.Length);
            if (!magic.SequenceEqual(RecMagic))
                return false;

            var version = reader.ReadUInt32();
            if (version != 1)
                return false;

            _ = reader.ReadSingle(); // tickrate
            _ = reader.ReadUInt32(); // round
            _ = reader.ReadByte();   // side
            _ = reader.ReadUInt32(); // flags
            _ = reader.ReadUInt64(); // steamid
            var tickCount = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // subticks
            if (tickCount == 0)
                return false;

            SkipRecString(reader);
            SkipRecString(reader);
            var preload = new List<int>();
            for (var i = 0; i < tickCount; i++)
            {
                stream.Seek(52 + 52, SeekOrigin.Current);
                var def = NormalizeWeaponDefIndex(reader.ReadInt32());
                if (IsKnownWeaponDefIndex(def) && firstWeaponDefIndex < 0)
                    firstWeaponDefIndex = def;
                if (IsPreloadWeaponDefIndex(def))
                    preload.Add(def);
                _ = reader.ReadUInt32(); // num_subtick
            }
            preloadWeaponDefIndices = NormalizePreloadWeaponDefs(preload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SkipRecString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        if (len > 0)
            reader.BaseStream.Seek(len, SeekOrigin.Current);
    }

    private List<CCSPlayerController> FindReplayTargets()
    {
        var players = FindTeamPlayers();
        return players.Where(IsReplayTargetBot).ToList();
    }

    private bool IsReplayTargetBot(CCSPlayerController player)
    {
        return player.IsBot || _botHiderProbe.IsManagedBot(player.Slot);
    }

    private static List<CCSPlayerController> FindTeamPlayers()
    {
        return Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid &&
                             (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist) &&
                             player.PlayerPawn is { IsValid: true, Value.IsValid: true })
            .OrderBy(player => player.Team)
            .ThenBy(player => player.Slot)
            .ToList();
    }

    private static bool ReplayBotSeesEnemy(int slot)
    {
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot is not { IsValid: true } || !bot.PawnIsAlive)
            return false;

        foreach (var enemy in FindTeamPlayers())
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !enemy.PawnIsAlive)
                continue;

            if (PlayerSeesTarget(bot, enemy))
                return true;
        }

        return false;
    }

    private static bool PlayerSeesTarget(CCSPlayerController observer, CCSPlayerController target)
    {
        if (observer.Slot < 0)
            return false;
        if (target.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        try
        {
            var spotted = target.PlayerPawn.Value.EntitySpottedState;
            if (spotted == null)
                return false;

            var mask = spotted.SpottedByMask;
            var word = observer.Slot / 32;
            var bit = observer.Slot % 32;
            return word >= 0 && word < mask.Length && (mask[word] & (1u << bit)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseHandoffMode(string value, out HandoffMode mode)
    {
        mode = value.ToLowerInvariant() switch
        {
            "0" or "off" or "none" => HandoffMode.Off,
            "death" or "kill" => HandoffMode.Death,
            "contact" or "see" or "sight" => HandoffMode.Contact,
            "1" or "death_or_contact" or "contact_or_death" or "auto" => HandoffMode.DeathOrContact,
            _ => HandoffMode.Off
        };
        return value.ToLowerInvariant() is "0" or "off" or "none" or
            "death" or "kill" or
            "contact" or "see" or "sight" or
            "1" or "death_or_contact" or "contact_or_death" or "auto";
    }

    private static bool HandoffIncludesDeath(HandoffMode mode)
        => mode is HandoffMode.Death or HandoffMode.DeathOrContact;

    private static bool HandoffIncludesContact(HandoffMode mode)
        => mode is HandoffMode.Contact or HandoffMode.DeathOrContact;

    private static string FormatHandoffMode(HandoffMode mode)
        => mode switch
        {
            HandoffMode.Off => "off",
            HandoffMode.Death => "death",
            HandoffMode.Contact => "contact",
            HandoffMode.DeathOrContact => "death_or_contact",
            _ => "off"
        };

    private static bool TryReadManifest(
        string manifestPath,
        out ConversionManifest manifest,
        out string error)
    {
        manifest = new ConversionManifest();
        error = string.Empty;

        try
        {
            manifest = ReadManifest(manifestPath);
            return true;
        }
        catch (FileNotFoundException)
        {
            error = $"file does not exist: {manifestPath}";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = $"directory does not exist: {manifestPath}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadPoolManifest(
        string manifestPath,
        out RoundPoolManifest manifest,
        out string error)
    {
        manifest = new RoundPoolManifest();
        error = string.Empty;

        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<RoundPoolManifest>(
                           json,
                           new JsonSerializerOptions
                           {
                               PropertyNameCaseInsensitive = true
                           })
                       ?? throw new InvalidOperationException("pool manifest JSON is empty");
            return true;
        }
        catch (FileNotFoundException)
        {
            error = $"file does not exist: {manifestPath}";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = $"directory does not exist: {manifestPath}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ConversionManifest ReadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<ConversionManifest>(
                   json,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   })
               ?? throw new InvalidOperationException("manifest JSON is empty");
    }

    private readonly record struct LoadRoundResult(bool Ok, string Message)
    {
        public static LoadRoundResult Success(string message) => new(true, message);
        public static LoadRoundResult Fail(string message) => new(false, message);
    }

    private readonly record struct LoadedReplay(
        string Path,
        string PlayerName,
        int FirstWeaponDefIndex,
        int[] PreloadWeaponDefIndices);

    private readonly record struct PendingWeaponAlign(int WeaponDefIndex, bool ForceSwitch);

    private readonly record struct TeamEconomySnapshot(uint EquipmentValue, string Class);

    private enum ReplayWeaponSlot
    {
        Other,
        Primary,
        Secondary,
        Utility,
        C4,
        Taser,
        Knife
    }

    private enum HandoffMode
    {
        Off,
        Death,
        Contact,
        DeathOrContact
    }

    private sealed class BotHiderMemoryProbe : IDisposable
    {
        private const string MappingName = "CS2BotHider_Slots";
        private const string PosixMappingPath = "/dev/shm/CS2BotHider_Slots";
        private const uint Magic = 0x44494842;
        private const int MaxSlots = 64;
        private const int TotalSize = 16384;
        private const int OffMagic = 0;
        private const int OffSlotState = 16;

        private MemoryMappedFile? _memory;
        private MemoryMappedViewAccessor? _view;

        public bool IsManagedBot(int slot)
        {
            if (slot < 0 || slot >= MaxSlots)
                return false;
            if (!TryConnect())
                return false;

            return _view!.ReadByte(OffSlotState + slot) != 0;
        }

        private bool TryConnect()
        {
            if (_view != null)
                return true;

            try
            {
                _memory = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read)
                    : MemoryMappedFile.CreateFromFile(
                        PosixMappingPath,
                        FileMode.Open,
                        null,
                        TotalSize,
                        MemoryMappedFileAccess.Read);
                _view = _memory.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.Read);
                if (_view.ReadUInt32(OffMagic) == Magic)
                    return true;
            }
            catch
            {
            }

            Dispose();
            return false;
        }

        public void Dispose()
        {
            _view?.Dispose();
            _memory?.Dispose();
            _view = null;
            _memory = null;
        }
    }

    private sealed class ConversionManifest
    {
        [JsonPropertyName("files")]
        public List<ManifestFile> Files { get; set; } = new();
    }

    private sealed class RoundPoolManifest
    {
        [JsonPropertyName("format_version")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("abi")]
        public int Abi { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; } = string.Empty;

        [JsonPropertyName("candidates")]
        public List<RoundPoolCandidate> Candidates { get; set; } = new();
    }

    private sealed class RoundPoolCandidate
    {
        [JsonPropertyName("manifest")]
        public string Manifest { get; set; } = string.Empty;

        [JsonPropertyName("demo_stem")]
        public string DemoStem { get; set; } = string.Empty;

        [JsonPropertyName("demo_path")]
        public string DemoPath { get; set; } = string.Empty;

        [JsonPropertyName("source_round")]
        public int SourceRound { get; set; }

        [JsonPropertyName("pistol_round")]
        public bool PistolRound { get; set; }

        [JsonPropertyName("t_economy")]
        public PoolTeamEconomy TEconomy { get; set; } = new();

        [JsonPropertyName("ct_economy")]
        public PoolTeamEconomy CtEconomy { get; set; } = new();

        [JsonPropertyName("duration_seconds")]
        public float DurationSeconds { get; set; }

        [JsonPropertyName("cut_reason")]
        public string? CutReason { get; set; }

        [JsonPropertyName("files")]
        public int Files { get; set; }
    }

    private sealed class PoolTeamEconomy
    {
        [JsonPropertyName("side")]
        public string Side { get; set; } = string.Empty;

        [JsonPropertyName("players")]
        public int Players { get; set; }

        [JsonPropertyName("round_start_equipment_value")]
        public uint RoundStartEquipmentValue { get; set; }

        [JsonPropertyName("equipment_value_total")]
        public uint EquipmentValueTotal { get; set; }

        [JsonPropertyName("money_saved_total")]
        public uint MoneySavedTotal { get; set; }

        [JsonPropertyName("cash_spent_this_round")]
        public uint CashSpentThisRound { get; set; }

        [JsonPropertyName("class")]
        public string Class { get; set; } = "unknown";

        public uint BestEquipmentValue => Math.Max(RoundStartEquipmentValue, EquipmentValueTotal);
    }

    private sealed class ManifestFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("round")]
        public int Round { get; set; }

        [JsonPropertyName("side")]
        public string Side { get; set; } = string.Empty;

        [JsonPropertyName("steam_id")]
        public ulong SteamId { get; set; }

        [JsonPropertyName("player_name")]
        public string PlayerName { get; set; } = string.Empty;

        [JsonPropertyName("first_weapon_def_index")]
        public int? FirstWeaponDefIndex { get; set; }

        [JsonPropertyName("preload_weapon_def_indices")]
        public int[]? PreloadWeaponDefIndices { get; set; }
    }
}
