using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoTracer;

public sealed class DemoTracerPlugin : BasePlugin
{
    public override string ModuleName => "CS2 DemoTracer";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "unicbm";
    public override string ModuleDescription => "Trace CS2 demos into bot-executable route replays.";

    private static readonly byte[] RecMagic =
    [
        (byte)'C', (byte)'S', (byte)'D', (byte)'T',
        (byte)'R', (byte)'R', (byte)'E', (byte)'C'
    ];
    private const float HandoffGraceSeconds = 0.25f;
    private const float BulletHandoffMatchSeconds = 0.25f;
    private const int BulletHandoffMinDamage = 10;
    private const int ProjectileAlignMatchAttempts = 8;
    private const int ProjectileAlignPostMatchWrites = 1;
    private const int MaxPlayerSlots = 64;
    private static readonly string[] UtilityTraceColumns =
    [
        "kind",
        "time",
        "slot",
        "player",
        "steam_id",
        "replay_cursor",
        "replay_total",
        "weapon_def",
        "live_weapon",
        "live_x",
        "live_y",
        "live_z",
        "live_vx",
        "live_vy",
        "live_vz",
        "live_pitch",
        "live_yaw",
        "replay_pre_x",
        "replay_pre_y",
        "replay_pre_z",
        "replay_pre_vx",
        "replay_pre_vy",
        "replay_pre_vz",
        "replay_pre_pitch",
        "replay_pre_yaw",
        "replay_buttons",
        "replay_buttons1",
        "replay_buttons2",
        "replay_post_x",
        "replay_post_y",
        "replay_post_z",
        "replay_post_vx",
        "replay_post_vy",
        "replay_post_vz",
        "stash_set",
        "stash_time",
        "stash_x",
        "stash_y",
        "stash_z",
        "stash_vx",
        "stash_vy",
        "stash_vz",
        "stash_pitch",
        "stash_yaw",
        "projectile_index",
        "projectile_name",
        "projectile_x",
        "projectile_y",
        "projectile_z",
        "projectile_abs_vx",
        "projectile_abs_vy",
        "projectile_abs_vz",
        "projectile_est_vx",
        "projectile_est_vy",
        "projectile_est_vz",
        "projectile_init_x",
        "projectile_init_y",
        "projectile_init_z",
        "projectile_init_vx",
        "projectile_init_vy",
        "projectile_init_vz",
        "projectile_smoke_det_x",
        "projectile_smoke_det_y",
        "projectile_smoke_det_z",
        "projectile_bounces",
        "projectile_is_live",
        "event_entity",
        "event_x",
        "event_y",
        "event_z",
        "message"
    ];

    private readonly List<int> _loadedSlots = new();
    private readonly Dictionary<int, LoadedReplay> _loadedReplays = new();
    private readonly Dictionary<int, int> _lastEnsuredWeaponDef = new();
    private readonly Dictionary<int, int> _lastReplayWeaponDef = new();
    private readonly Dictionary<int, int> _lastLockedWeaponTarget = new();
    private readonly Dictionary<int, PendingWeaponAlign> _pendingWeaponAlign = new();
    private readonly Dictionary<int, int> _projectileAlignNextBySlot = new();
    private readonly Dictionary<uint, PendingProjectileAlign> _pendingProjectileAlign = new();
    private readonly HashSet<int> _rebuiltInventorySlots = new();
    private readonly HashSet<int> _loadoutSyncedSlots = new();
    private readonly HashSet<int> _lastPlayingSlots = new();
    private readonly Dictionary<int, float> _replayStartedAt = new();
    private readonly Dictionary<int, PendingBulletHit> _pendingBulletHits = new();
    private readonly Dictionary<int, PendingBulletDamage> _pendingBulletDamages = new();
    private readonly Dictionary<uint, UtilityProjectileTrace> _utilityTraceProjectiles = new();
    private readonly BotHiderMemoryProbe _botHiderProbe = new();
    private StreamWriter? _utilityTraceWriter;
    private string _utilityTracePath = string.Empty;
    private bool _utilityTraceEnabled;

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
    private bool _projectileAlignEnabled = true;
    private bool _weaponAlignFrameQueued;
    private HandoffMode _handoffMode = HandoffMode.DeathOrContact;
    private bool _handoffAllSlots;
    private bool _partialReplayEnabled = true;
    private bool _replayIdentityEnabled;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        ConfigureNativeSafetyOffsets();
        Server.PrintToConsole("dtr: CSS control plugin loaded");
    }

    public override void Unload(bool hotReload)
    {
        StopAndUnloadLoaded();
        StopUtilityTrace();
        BotControllerNative.ClearAllBuyPlans();
        _botHiderProbe.Dispose();
    }

    private static void ConfigureNativeSafetyOffsets()
    {
        try
        {
            var offset = Schema.GetSchemaOffset("CCSPlayerController", "m_bControllingBot");
            var ok = BotControllerNative.SetControllerControllingBotOffset(offset);
            Server.PrintToConsole(ok
                ? $"dtr: native takeover guard enabled, ControllingBot offset=0x{offset:X}"
                : "dtr: native takeover guard unavailable; CSS safety checks remain active");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: native takeover guard unavailable: {ex.Message}");
        }
    }

    [ConsoleCommand("dtr_run_manifest", "dtr_run_manifest <manifest.json> [start-round]")]
    public void RunManifestCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_run_manifest <manifest.json> [start-round]");
            return;
        }

        var manifestPath = command.GetArg(1);
        if (!TryReadManifest(manifestPath, out var manifest, out var readError))
        {
            command.ReplyToCommand($"dtr: failed to read manifest: {readError}");
            return;
        }

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
        if (command.ArgCount >= 3 &&
            (!int.TryParse(command.GetArg(2), out startRound) || !rounds.Contains(startRound)))
        {
            command.ReplyToCommand("dtr: start round is not present in manifest");
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
            $"dtr: sequence armed, {rounds.Length - _sequenceIndex} rounds from round {startRound}; next round_start prepares bots, round_freeze_end starts playback");
    }

    [ConsoleCommand("dtr_stop_sequence", "dtr_stop_sequence")]
    public void StopSequenceCommand(CCSPlayerController? player, CommandInfo command)
    {
        _sequenceActive = false;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        command.ReplyToCommand("dtr: sequence stopped");
    }

    [ConsoleCommand("dtr_run_pool", "dtr_run_pool <pool_manifest.json> [start-round]")]
    public void RunPoolCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_run_pool <pool_manifest.json> [start-round]");
            return;
        }

        var poolPath = command.GetArg(1);
        if (!TryReadPoolManifest(poolPath, out var pool, out var readError))
        {
            command.ReplyToCommand($"dtr: failed to read pool manifest: {readError}");
            return;
        }

        var startRound = 0;
        if (command.ArgCount >= 3 &&
            (!int.TryParse(command.GetArg(2), out startRound) || startRound < 0))
        {
            command.ReplyToCommand("dtr: start round must be a non-negative integer");
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
                ? $"dtr: pool armed, candidates={pool.Candidates.Count}, next round={_poolRoundIndex}; round_freeze_end selects by economy"
                : "dtr: pool manifest has no candidates");
    }

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

    [ConsoleCommand("dtr_weapon_align", "dtr_weapon_align <0|1>")]
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
                BotControllerNative.UnlockWeaponSlot(slot);
        }

        command.ReplyToCommand($"dtr: weapon_align={_weaponAlignEnabled}");
    }

    [ConsoleCommand("dtr_projectile_align", "dtr_projectile_align <0|1>")]
    public void ProjectileAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _projectileAlignEnabled = command.GetArg(1) != "0";
        if (!_projectileAlignEnabled)
        {
            _projectileAlignNextBySlot.Clear();
            _pendingProjectileAlign.Clear();
        }

        command.ReplyToCommand($"dtr: projectile_align={_projectileAlignEnabled}");
    }

    [ConsoleCommand("dtr_handoff", "dtr_handoff <off|death|contact|death_or_contact> [all|slot]")]
    public void HandoffCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            if (!TryParseHandoffMode(command.GetArg(1), out var mode))
            {
                command.ReplyToCommand("usage: dtr_handoff <off|death|contact|death_or_contact> [all|slot]");
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
                command.ReplyToCommand("usage: dtr_handoff <off|death|contact|death_or_contact> [all|slot]");
                return;
            }
        }

        command.ReplyToCommand(
            $"dtr: handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")}");
    }

    private static string EscapeConsoleString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    [ConsoleCommand("dtr_partial", "dtr_partial <0|1>")]
    public void PartialCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _partialReplayEnabled = command.GetArg(1) != "0";

        command.ReplyToCommand($"dtr: partial_replay={_partialReplayEnabled}");
    }

    [ConsoleCommand("dtr_replay_identity", "dtr_replay_identity <0|1>")]
    public void ReplayIdentityCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _replayIdentityEnabled = command.GetArg(1) != "0";

        command.ReplyToCommand($"dtr: replay_identity={ReplayIdentityModeName()}");
    }

    [ConsoleCommand("dtr_load", "dtr_load <slot> <absolute-or-game-path.dtr>")]
    public void LoadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseSlot(command, out var slot) || command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_load <slot> <path.dtr>");
            return;
        }

        var path = command.GetArg(2);
        var ok = BotControllerNative.LoadReplayFromFile(slot, path);
        if (ok)
        {
            RememberLoadedSlot(slot);
            TrackLoadedReplay(slot, path, $"slot{slot}");
        }

        command.ReplyToCommand(ok
            ? $"dtr: loaded slot {slot}: {path}"
            : $"dtr: failed to load slot {slot}: {path} ({BotControllerNative.LastLoadError})");
    }

    [ConsoleCommand("dtr_load_round", "dtr_load_round <manifest.json> <round>")]
    public void LoadRoundCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseRoundArgs(command, out var manifestPath, out var round))
            return;

        var result = LoadRound(manifestPath, round);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("dtr_arm_round", "dtr_arm_round <manifest.json> <round> [loop:0|1]")]
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
        command.ReplyToCommand($"dtr: armed {_loadedSlots.Count} slots, will start on round_freeze_end, loop={loop}");
    }

    [ConsoleCommand("dtr_play_loaded", "dtr_play_loaded [loop:0|1]")]
    public void PlayLoadedCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        var loop = command.ArgCount >= 2 && command.GetArg(1) != "0";
        command.ReplyToCommand(PlayLoaded(loop));
    }

    [ConsoleCommand("dtr_play", "dtr_play <slot> [loop:0|1]")]
    public void PlayCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var loop = command.ArgCount >= 3 && command.GetArg(2) != "0";
        if (_loadedReplays.TryGetValue(slot, out var replay))
            PreloadReplayWeaponsForSlot(slot, replay);
        _lastEnsuredWeaponDef.Remove(slot);

        if (!IsReplaySlotStillSafe(slot))
        {
            command.ReplyToCommand($"dtr: refused to play slot {slot}: not a safe bot target");
            return;
        }

        var ok = BotControllerNative.StartReplay(slot, loop);
        if (ok)
            MarkReplayStarted(slot);
        var state = ok ? default : BotControllerNative.GetReplayState(slot);
        command.ReplyToCommand(ok
            ? $"dtr: playing slot {slot}, loop={loop}"
            : $"dtr: failed to play slot {slot} (cursor={state.Cursor}, total={state.Total})");
    }

    [ConsoleCommand("dtr_stop", "dtr_stop <slot>")]
    public void StopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var ok = BotControllerNative.StopReplay(slot);
        ReleaseReplaySlot(slot, "manual_stop");
        command.ReplyToCommand(ok
            ? $"dtr: stopped slot {slot}"
            : $"dtr: failed to stop slot {slot}");
    }

    [ConsoleCommand("dtr_stop_all", "dtr_stop_all")]
    public void StopAllCommand(CCSPlayerController? player, CommandInfo command)
    {
        foreach (var slot in _loadedSlots.ToArray())
        {
            BotControllerNative.StopReplay(slot);
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
        _projectileAlignNextBySlot.Clear();
        _pendingProjectileAlign.Clear();
        _rebuiltInventorySlots.Clear();
        _lastPlayingSlots.Clear();
        _replayStartedAt.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        command.ReplyToCommand($"dtr: stopped {_loadedSlots.Count} loaded slots");
    }

    [ConsoleCommand("dtr_unload", "dtr_unload <slot>")]
    public void UnloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var ok = BotControllerNative.UnloadReplay(slot);
        if (ok)
        {
            _loadedSlots.Remove(slot);
            _loadedReplays.Remove(slot);
            _lastEnsuredWeaponDef.Remove(slot);
            _lastReplayWeaponDef.Remove(slot);
            _lastLockedWeaponTarget.Remove(slot);
            _pendingWeaponAlign.Remove(slot);
            _rebuiltInventorySlots.Remove(slot);
            _pendingBulletHits.Remove(slot);
            _pendingBulletDamages.Remove(slot);
            ReleaseReplaySlot(slot, "unload");
        }

        command.ReplyToCommand(ok
            ? $"dtr: unloaded slot {slot}"
            : $"dtr: failed to unload slot {slot}");
    }

    [ConsoleCommand("dtr_bots", "dtr_bots")]
    public void BotsCommand(CCSPlayerController? player, CommandInfo command)
    {
        var players = FindTeamPlayers();
        var strictBots = players.Count(candidate => candidate.IsBot);
        var managedBots = players.Count(candidate => _botHiderProbe.IsManagedBot(candidate.Slot));
        var candidates = players.Count(IsReplayTargetBot);
        command.ReplyToCommand(
            $"dtr: strict IsBot={strictBots}, BotHider managed={managedBots}, safe replay candidates={candidates}");
        foreach (var bot in players)
        {
            var managed = _botHiderProbe.IsManagedBot(bot.Slot);
            var controllingBot = TryGetControllingBotState(bot, out var isControllingBot)
                ? (isControllingBot ? "1" : "0")
                : "unknown";
            command.ReplyToCommand(
                $"slot={bot.Slot} team={bot.Team} isBot={bot.IsBot} managed={managed} controllingBot={controllingBot} candidate={IsReplayTargetBot(bot)} name={bot.PlayerName}");
        }
    }

    [ConsoleCommand("dtr_status", "dtr_status <slot>")]
    public void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var state = BotControllerNative.GetReplayState(slot);
        var sequence = _sequenceActive && _sequenceIndex < _sequenceRounds.Length
            ? $" sequence_next={_sequenceRounds[_sequenceIndex]}"
            : string.Empty;
        var pool = _poolActive
            ? $" pool_next={_poolRoundIndex}"
            : string.Empty;
        command.ReplyToCommand(
            $"dtr: abi={BotControllerNative.AbiVersion} slot={slot} playing={state.Playing} cursor={state.Cursor} total={state.Total} handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")} partial={_partialReplayEnabled} identity={ReplayIdentityModeName()} projectile_align={_projectileAlignEnabled}{sequence}{pool}");
    }

    [ConsoleCommand("dtr_util_trace", "dtr_util_trace <0|1> [path]")]
    public void UtilityTraceCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand(
                _utilityTraceEnabled
                    ? $"dtr: utility trace on path=\"{_utilityTracePath}\""
                    : "usage: dtr_util_trace <0|1> [path]");
            return;
        }

        if (command.GetArg(1) == "0")
        {
            var path = _utilityTracePath;
            StopUtilityTrace();
            command.ReplyToCommand(string.IsNullOrEmpty(path)
                ? "dtr: utility trace off"
                : $"dtr: utility trace off path=\"{path}\"");
            return;
        }

        var requestedPath = command.ArgCount >= 3 ? command.GetArg(2) : string.Empty;
        if (!StartUtilityTrace(requestedPath, out var message))
        {
            command.ReplyToCommand($"dtr: utility trace failed: {message}");
            return;
        }

        command.ReplyToCommand($"dtr: utility trace on path=\"{message}\"");
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
        Server.PrintToConsole($"dtr: auto-start {_armedLabel}: {message}");
        _armed = false;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (HandoffIncludesDeath(_handoffMode) && HasActiveReplaySlots())
        {
            var triggerSlot = GetDeathHandoffSlot(@event);
            if (triggerSlot >= 0)
                HandoffActiveReplays($"player_death_slot{triggerSlot}", triggerSlot);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnGrenadeThrown(EventGrenadeThrown @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled)
            TraceGrenadeThrown(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled)
            TraceSmokeDetonate(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled)
            TraceSmokeExpired(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBulletDamage(EventBulletDamage @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Victim, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_pendingBulletDamages.TryGetValue(victimSlot, out var damage) &&
            damage.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(damage.Time))
        {
            _pendingBulletDamages.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage.Damage);
        }
        else
        {
            _pendingBulletHits[victimSlot] = new PendingBulletHit(attackerSlot, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Userid, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        var damage = Math.Max(0, @event.DmgHealth) + Math.Max(0, @event.DmgArmor);
        if (damage < BulletHandoffMinDamage)
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_pendingBulletHits.TryGetValue(victimSlot, out var hit) &&
            hit.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(hit.Time))
        {
            _pendingBulletHits.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage);
        }
        else
        {
            _pendingBulletDamages[victimSlot] = new PendingBulletDamage(attackerSlot, damage, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        ProcessPendingProjectileAlign();

        if (_utilityTraceEnabled)
            TraceUtilityTick();

        if (_loadedSlots.Count == 0)
            return;

        foreach (var slot in _loadedSlots.ToArray())
        {
            var state = BotControllerNative.GetReplayState(slot);
            if (!state.Playing)
            {
                if (_lastPlayingSlots.Contains(slot))
                    ReleaseReplaySlot(slot, "replay_finished");
                continue;
            }

            if (!IsReplaySlotStillSafe(slot))
            {
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "unsafe_replay_target");
                continue;
            }

            if (!_lastPlayingSlots.Contains(slot))
                MarkReplayStarted(slot);

            if (HandoffIncludesContact(_handoffMode) && ReplayHasPassedHandoffGrace(slot) &&
                ReplayBotSeesEnemy(slot, out var contactReason))
            {
                HandoffActiveReplays($"enemy_contact_{contactReason}_slot{slot}", slot);
                continue;
            }

            if (!_weaponAlignEnabled)
                continue;
            var hasReplayTick = BotControllerNative.TryGetReplayTick(slot, out var tick);
            if (!hasReplayTick)
                continue;

            ApplyReplayWeaponPreset(slot, tick.WeaponDefIndex, allowSlotReplacement: true, force: false);
        }
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (!IsSmokeProjectile(entity))
            return;

        try
        {
            var projectile = new CSmokeGrenadeProjectile(entity.Handle);
            if (!projectile.IsValid)
                return;
            TrackProjectileAlignCandidate(projectile);
            if (_utilityTraceEnabled)
            {
                _utilityTraceProjectiles[projectile.Index] =
                    new UtilityProjectileTrace(projectile.Index, entity.Handle, projectile.DesignerName);
                TraceProjectileEvent("projectile_spawned", projectile, null);
            }
        }
        catch (Exception ex)
        {
            if (_utilityTraceEnabled)
                TraceUtilityMessage("projectile_spawn_failed", ex.Message);
        }
    }

    private void TrackProjectileAlignCandidate(CSmokeGrenadeProjectile projectile)
    {
        if (!_projectileAlignEnabled)
            return;

        var pending = new PendingProjectileAlign(projectile.Index, projectile.Handle)
        {
            MatchAttemptsRemaining = ProjectileAlignMatchAttempts,
            WritesRemaining = 0
        };
        _pendingProjectileAlign[projectile.Index] = pending;

        TryResolveAndApplyProjectileAlign(projectile, pending);
    }

    private void ProcessPendingProjectileAlign()
    {
        if (_pendingProjectileAlign.Count == 0)
            return;

        foreach (var entry in _pendingProjectileAlign.ToArray().OrderBy(item => item.Key))
        {
            var pending = entry.Value;
            try
            {
                var projectile = new CSmokeGrenadeProjectile(pending.Handle);
                if (!projectile.IsValid)
                {
                    _pendingProjectileAlign.Remove(entry.Key);
                    continue;
                }

                if (!pending.Matched)
                {
                    if (TryResolveAndApplyProjectileAlign(projectile, pending))
                        continue;

                    pending.MatchAttemptsRemaining--;
                    if (pending.MatchAttemptsRemaining <= 0)
                        _pendingProjectileAlign.Remove(entry.Key);
                    else
                        _pendingProjectileAlign[entry.Key] = pending;
                    continue;
                }

                ApplyProjectileAlign(projectile, pending.Align);
                pending.WritesRemaining--;
                if (pending.WritesRemaining <= 0)
                    _pendingProjectileAlign.Remove(entry.Key);
                else
                    _pendingProjectileAlign[entry.Key] = pending;
            }
            catch (Exception ex)
            {
                _pendingProjectileAlign.Remove(entry.Key);
                if (_utilityTraceEnabled)
                    TraceUtilityMessage("projectile_align_failed", $"index={entry.Key} {ex.Message}");
            }
        }
    }

    private bool TryResolveAndApplyProjectileAlign(
        CSmokeGrenadeProjectile projectile,
        PendingProjectileAlign pending)
    {
        if (!_projectileAlignEnabled || !TryResolveProjectileAlign(projectile, out var slot, out var eventIndex, out var align))
            return false;

        ApplyProjectileAlign(projectile, align);
        _projectileAlignNextBySlot[slot] = eventIndex + 1;
        pending.Matched = true;
        pending.Slot = slot;
        pending.EventIndex = eventIndex;
        pending.Align = align;
        pending.WritesRemaining = ProjectileAlignPostMatchWrites;
        _pendingProjectileAlign[pending.Index] = pending;

        if (_utilityTraceEnabled)
        {
            TraceUtilityMessage(
                "projectile_align",
                $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} init_vel=({align.InitialVelocity.X:F3},{align.InitialVelocity.Y:F3},{align.InitialVelocity.Z:F3})");
        }
        return true;
    }

    private bool TryResolveProjectileAlign(
        CSmokeGrenadeProjectile projectile,
        out int slot,
        out int eventIndex,
        out ReplayProjectileEvent align)
    {
        slot = -1;
        eventIndex = -1;
        align = default;

        if (!TryGetProjectileThrowerSlot(projectile, out slot))
            return false;
        if (!_loadedReplays.TryGetValue(slot, out var replay) || replay.Projectiles.Length == 0)
            return false;

        var state = BotControllerNative.GetReplayState(slot);
        if (!state.Playing)
            return false;

        var next = _projectileAlignNextBySlot.TryGetValue(slot, out var value) ? value : 0;
        eventIndex = FindProjectileAlignEvent(replay.Projectiles, next, state.Cursor, ReplayProjectileKind.Smoke);
        if (eventIndex < 0)
            return false;

        align = replay.Projectiles[eventIndex];
        return true;
    }

    private static void ApplyProjectileAlign(CSmokeGrenadeProjectile projectile, ReplayProjectileEvent align)
    {
        SetVector(projectile.InitialPosition, align.InitialPosition);
        SetVector(projectile.InitialVelocity, align.InitialVelocity);
        SetVector(projectile.AbsOrigin, align.InitialPosition);
        SetVector(projectile.AbsVelocity, align.InitialVelocity);
    }

    private static int FindProjectileAlignEvent(
        IReadOnlyList<ReplayProjectileEvent> events,
        int start,
        int cursor,
        ReplayProjectileKind kind)
    {
        const int MaxCursorDistance = 96;
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = Math.Max(start, 0); i < events.Count; i++)
        {
            var candidate = events[i];
            if (candidate.Kind != kind)
                continue;

            var diff = Math.Abs((int)candidate.TickIndex - cursor);
            if (diff < bestDistance)
            {
                best = i;
                bestDistance = diff;
            }
            if ((int)candidate.TickIndex > cursor + MaxCursorDistance)
                break;
        }

        return bestDistance <= MaxCursorDistance ? best : -1;
    }

    private static bool TryGetProjectileThrowerSlot(CSmokeGrenadeProjectile projectile, out int slot)
    {
        slot = -1;
        var thrower = projectile.Thrower.Value;
        if (thrower is not { IsValid: true })
            return false;

        foreach (var player in FindTeamPlayers())
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn is { IsValid: true } && pawn.Handle == thrower.Handle)
            {
                slot = player.Slot;
                return true;
            }
        }

        return false;
    }

    private static void SetVector(Vector? vector, ReplayVector3 value)
    {
        if (vector == null)
            return;
        vector.X = value.X;
        vector.Y = value.Y;
        vector.Z = value.Z;
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        _pendingProjectileAlign.Remove(entity.Index);

        if (!_utilityTraceEnabled)
            return;

        var index = entity.Index;
        if (!_utilityTraceProjectiles.TryGetValue(index, out var tracked))
            return;

        try
        {
            var projectile = new CSmokeGrenadeProjectile(tracked.Handle);
            TraceProjectileEvent("projectile_deleted", projectile, tracked);
        }
        catch
        {
            TraceWrite(RowFields(
                ("kind", "projectile_deleted"),
                ("time", TimeField()),
                ("projectile_index", index),
                ("projectile_name", tracked.DesignerName)
            ));
        }
        _utilityTraceProjectiles.Remove(index);
    }

    private bool StartUtilityTrace(string requestedPath, out string message)
    {
        StopUtilityTrace();
        _utilityTraceProjectiles.Clear();

        try
        {
            var path = string.IsNullOrWhiteSpace(requestedPath)
                ? DefaultUtilityTracePath()
                : requestedPath;
            path = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            _utilityTraceWriter = new StreamWriter(path, append: false);
            _utilityTraceWriter.WriteLine(Row(UtilityTraceColumns));
            _utilityTraceWriter.Flush();
            _utilityTracePath = path;
            _utilityTraceEnabled = true;
            message = path;
            return true;
        }
        catch (Exception ex)
        {
            StopUtilityTrace();
            message = ex.Message;
            return false;
        }
    }

    private void StopUtilityTrace()
    {
        _utilityTraceEnabled = false;
        _utilityTraceProjectiles.Clear();
        _utilityTraceWriter?.Flush();
        _utilityTraceWriter?.Dispose();
        _utilityTraceWriter = null;
    }

    private string DefaultUtilityTracePath()
    {
        var dir = Path.GetDirectoryName(ModulePath);
        if (string.IsNullOrWhiteSpace(dir))
            dir = AppContext.BaseDirectory;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"dtr_util_trace_{stamp}.csv");
    }

    private void TraceUtilityTick()
    {
        foreach (var slot in _loadedSlots.ToArray())
        {
            try
            {
                TraceReplaySlotTick(slot);
            }
            catch (Exception ex)
            {
                TraceUtilityMessage("slot_tick_failed", $"slot={slot} {ex.Message}");
            }
        }

        foreach (var tracked in _utilityTraceProjectiles.Values.ToArray())
        {
            try
            {
                var projectile = new CSmokeGrenadeProjectile(tracked.Handle);
                if (!projectile.IsValid)
                {
                    _utilityTraceProjectiles.Remove(tracked.Index);
                    continue;
                }
                TraceProjectileEvent("projectile_tick", projectile, tracked);
            }
            catch (Exception ex)
            {
                _utilityTraceProjectiles.Remove(tracked.Index);
                TraceUtilityMessage("projectile_tick_failed", $"index={tracked.Index} {ex.Message}");
            }
        }

        _utilityTraceWriter?.Flush();
    }

    private void TraceReplaySlotTick(int slot)
    {
        var state = BotControllerNative.GetReplayState(slot);
        var hasTick = BotControllerNative.TryGetReplayTick(slot, out var tick);
        var bot = Utilities.GetPlayerFromSlot(slot);
        var pawn = bot?.PlayerPawn.Value;
        var liveOrigin = SafeVector(() => pawn?.AbsOrigin);
        var liveVelocity = SafeVector(() => pawn?.AbsVelocity);
        var liveAngles = SafeQAngle(() => pawn?.EyeAngles);
        var stashPosition = SafeVector(() => pawn?.StashedGrenadeThrowPosition);
        var stashVelocity = SafeVector(() => pawn?.StashedVelocity);
        var stashAngles = SafeQAngle(() => pawn?.StashedShootAngles);
        var stashSet = SafeObject(() => pawn?.GrenadeParametersStashed.ToString());
        var stashTime = SafeObject(() => pawn?.GrenadeParameterStashTime);

        TraceWrite(RowFields(
            ("kind", "slot_tick"),
            ("time", TimeField()),
            ("slot", slot),
            ("player", bot?.PlayerName ?? ""),
            ("steam_id", bot?.SteamID ?? 0UL),
            ("replay_cursor", state.Cursor),
            ("replay_total", state.Total),
            ("weapon_def", hasTick ? tick.WeaponDefIndex : null),
            ("live_weapon", ActiveWeaponName(pawn)),
            ("live_x", liveOrigin.X),
            ("live_y", liveOrigin.Y),
            ("live_z", liveOrigin.Z),
            ("live_vx", liveVelocity.X),
            ("live_vy", liveVelocity.Y),
            ("live_vz", liveVelocity.Z),
            ("live_pitch", liveAngles.X),
            ("live_yaw", liveAngles.Y),
            ("replay_pre_x", hasTick ? tick.Pre.OriginX : null),
            ("replay_pre_y", hasTick ? tick.Pre.OriginY : null),
            ("replay_pre_z", hasTick ? tick.Pre.OriginZ : null),
            ("replay_pre_vx", hasTick ? tick.Pre.VelX : null),
            ("replay_pre_vy", hasTick ? tick.Pre.VelY : null),
            ("replay_pre_vz", hasTick ? tick.Pre.VelZ : null),
            ("replay_pre_pitch", hasTick ? tick.Pre.Pitch : null),
            ("replay_pre_yaw", hasTick ? tick.Pre.Yaw : null),
            ("replay_buttons", hasTick ? Hex(tick.Pre.Buttons) : null),
            ("replay_buttons1", hasTick ? Hex(tick.Pre.Buttons1) : null),
            ("replay_buttons2", hasTick ? Hex(tick.Pre.Buttons2) : null),
            ("replay_post_x", hasTick ? tick.Post.OriginX : null),
            ("replay_post_y", hasTick ? tick.Post.OriginY : null),
            ("replay_post_z", hasTick ? tick.Post.OriginZ : null),
            ("replay_post_vx", hasTick ? tick.Post.VelX : null),
            ("replay_post_vy", hasTick ? tick.Post.VelY : null),
            ("replay_post_vz", hasTick ? tick.Post.VelZ : null),
            ("stash_set", stashSet),
            ("stash_time", stashTime),
            ("stash_x", stashPosition.X),
            ("stash_y", stashPosition.Y),
            ("stash_z", stashPosition.Z),
            ("stash_vx", stashVelocity.X),
            ("stash_vy", stashVelocity.Y),
            ("stash_vz", stashVelocity.Z),
            ("stash_pitch", stashAngles.X),
            ("stash_yaw", stashAngles.Y)
        ));
    }

    private void TraceGrenadeThrown(EventGrenadeThrown @event)
    {
        var slot = @event.Userid?.Slot ?? -1;
        var pawn = @event.Userid?.PlayerPawn.Value;
        var stashPosition = SafeVector(() => pawn?.StashedGrenadeThrowPosition);
        var stashVelocity = SafeVector(() => pawn?.StashedVelocity);
        var stashAngles = SafeQAngle(() => pawn?.StashedShootAngles);
        TraceWrite(RowFields(
            ("kind", "grenade_thrown"),
            ("time", TimeField()),
            ("slot", slot),
            ("player", @event.Userid?.PlayerName ?? ""),
            ("steam_id", @event.Userid?.SteamID ?? 0UL),
            ("live_weapon", @event.Weapon),
            ("stash_set", SafeObject(() => pawn?.GrenadeParametersStashed.ToString())),
            ("stash_time", SafeObject(() => pawn?.GrenadeParameterStashTime)),
            ("stash_x", stashPosition.X),
            ("stash_y", stashPosition.Y),
            ("stash_z", stashPosition.Z),
            ("stash_vx", stashVelocity.X),
            ("stash_vy", stashVelocity.Y),
            ("stash_vz", stashVelocity.Z),
            ("stash_pitch", stashAngles.X),
            ("stash_yaw", stashAngles.Y)
        ));
    }

    private void TraceSmokeDetonate(EventSmokegrenadeDetonate @event)
    {
        TraceWrite(RowFields(
            ("kind", "smoke_detonate"),
            ("time", TimeField()),
            ("slot", @event.Userid?.Slot ?? -1),
            ("player", @event.Userid?.PlayerName ?? ""),
            ("steam_id", @event.Userid?.SteamID ?? 0UL),
            ("event_entity", @event.Entityid),
            ("event_x", @event.X),
            ("event_y", @event.Y),
            ("event_z", @event.Z)
        ));
    }

    private void TraceSmokeExpired(EventSmokegrenadeExpired @event)
    {
        TraceWrite(RowFields(
            ("kind", "smoke_expired"),
            ("time", TimeField()),
            ("slot", @event.Userid?.Slot ?? -1),
            ("player", @event.Userid?.PlayerName ?? ""),
            ("steam_id", @event.Userid?.SteamID ?? 0UL),
            ("event_entity", @event.Entityid),
            ("event_x", @event.X),
            ("event_y", @event.Y),
            ("event_z", @event.Z)
        ));
    }

    private void TraceProjectileEvent(
        string kind,
        CSmokeGrenadeProjectile projectile,
        UtilityProjectileTrace? tracked)
    {
        var time = Server.CurrentTime;
        var origin = SafeVector(() => projectile.AbsOrigin);
        var absVelocity = SafeVector(() => projectile.AbsVelocity);
        var initialPosition = SafeVector(() => projectile.InitialPosition);
        var initialVelocity = SafeVector(() => projectile.InitialVelocity);
        var smokeDetonationPosition = SafeVector(() => projectile.SmokeDetonationPos);
        var estimate = tracked?.EstimateVelocity(origin, time) ?? TraceVector.Empty;
        tracked?.Update(origin, time);

        TraceWrite(RowFields(
            ("kind", kind),
            ("time", TimeField()),
            ("projectile_index", projectile.Index),
            ("projectile_name", projectile.DesignerName),
            ("projectile_x", origin.X),
            ("projectile_y", origin.Y),
            ("projectile_z", origin.Z),
            ("projectile_abs_vx", absVelocity.X),
            ("projectile_abs_vy", absVelocity.Y),
            ("projectile_abs_vz", absVelocity.Z),
            ("projectile_est_vx", estimate.X),
            ("projectile_est_vy", estimate.Y),
            ("projectile_est_vz", estimate.Z),
            ("projectile_init_x", initialPosition.X),
            ("projectile_init_y", initialPosition.Y),
            ("projectile_init_z", initialPosition.Z),
            ("projectile_init_vx", initialVelocity.X),
            ("projectile_init_vy", initialVelocity.Y),
            ("projectile_init_vz", initialVelocity.Z),
            ("projectile_smoke_det_x", smokeDetonationPosition.X),
            ("projectile_smoke_det_y", smokeDetonationPosition.Y),
            ("projectile_smoke_det_z", smokeDetonationPosition.Z),
            ("projectile_bounces", SafeObject(() => projectile.Bounces)),
            ("projectile_is_live", SafeObject(() => projectile.IsLive))
        ));
    }

    private void TraceUtilityMessage(string kind, string message)
    {
        TraceWrite(RowFields(
            ("kind", kind),
            ("time", TimeField()),
            ("message", message)
        ));
    }

    private void TraceWrite(string line)
    {
        if (!_utilityTraceEnabled || _utilityTraceWriter == null)
            return;
        try
        {
            _utilityTraceWriter.WriteLine(line);
        }
        catch (Exception ex)
        {
            _utilityTraceEnabled = false;
            Server.PrintToConsole($"dtr: utility trace disabled after write failure: {ex.Message}");
        }
    }

    private static bool IsSmokeProjectile(CEntityInstance entity)
    {
        if (!entity.IsValid)
            return false;
        return entity.DesignerName.Contains("smokegrenade_projectile", StringComparison.OrdinalIgnoreCase);
    }

    private static string ActiveWeaponName(CCSPlayerPawn? pawn)
    {
        try
        {
            var weapon = pawn?.WeaponServices?.ActiveWeapon.Value;
            return weapon is { IsValid: true } ? weapon.DesignerName : "";
        }
        catch
        {
            return "";
        }
    }

    private static object? SafeObject(Func<object?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static TraceVector SafeVector(Func<Vector?> read)
    {
        try
        {
            var value = read();
            return value == null
                ? TraceVector.Empty
                : new TraceVector(value.X, value.Y, value.Z);
        }
        catch
        {
            return TraceVector.Empty;
        }
    }

    private static TraceVector SafeQAngle(Func<QAngle?> read)
    {
        try
        {
            var value = read();
            return value == null
                ? TraceVector.Empty
                : new TraceVector(value.X, value.Y, value.Z);
        }
        catch
        {
            return TraceVector.Empty;
        }
    }

    private static string TimeField()
        => F(Server.CurrentTime);

    private static string Hex(ulong value)
        => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private static string Row(params object?[] fields)
    {
        var output = new string[UtilityTraceColumns.Length];
        for (var i = 0; i < output.Length; i++)
            output[i] = CsvField(i < fields.Length ? fields[i] : null);
        return string.Join(",", output);
    }

    private static string RowFields(params (string Column, object? Value)[] fields)
    {
        var output = new object?[UtilityTraceColumns.Length];
        foreach (var (column, value) in fields)
        {
            var index = Array.IndexOf(UtilityTraceColumns, column);
            if (index >= 0)
                output[index] = value;
        }
        return Row(output);
    }

    private static string CsvField(object? value)
    {
        var text = value switch
        {
            null => "",
            string s => s,
            float f => F(f),
            double d => d.ToString("0.#####", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        if (text.Contains('"'))
            text = text.Replace("\"", "\"\"");
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text}\""
            : text;
    }

    private static string F(float value)
        => value.ToString("0.#####", CultureInfo.InvariantCulture);

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
        Server.PrintToConsole($"dtr: prepared sequence round {round} on {reason}: {load.Message}");
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
            _sequenceActive = false;
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

    private static bool CheckAbi(CommandInfo command)
    {
        if (BotControllerNative.IsCompatible)
            return true;

        command.ReplyToCommand(
            $"dtr: ABI mismatch, runtime={BotControllerNative.AbiVersion}, expected={BotControllerNative.ExpectedAbiVersion}");
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

        command.ReplyToCommand("dtr: round must be a non-negative integer");
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
                return LoadRoundResult.Fail($"dtr: failed to read manifest: {readError}");

            var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
            var roundFiles = manifest.Files
                .Where(file => file.Round == round)
                .OrderBy(file => file.Side, StringComparer.Ordinal)
                .ThenBy(file => file.SteamId)
                .ToList();
            if (roundFiles.Count == 0)
                return LoadRoundResult.Fail($"dtr: manifest has no files for round {round}");

            var allTFiles = roundFiles.Where(file => file.Side.Equals("t", StringComparison.OrdinalIgnoreCase)).ToList();
            var allCtFiles = roundFiles.Where(file => file.Side.Equals("ct", StringComparison.OrdinalIgnoreCase)).ToList();
            var targets = FindReplayTargets();
            var tBots = targets.Where(bot => bot.Team == CsTeam.Terrorist).OrderBy(bot => bot.Slot).ToList();
            var ctBots = targets.Where(bot => bot.Team == CsTeam.CounterTerrorist).OrderBy(bot => bot.Slot).ToList();

            if (!_partialReplayEnabled && (tBots.Count < allTFiles.Count || ctBots.Count < allCtFiles.Count))
            {
                return LoadRoundResult.Fail(
                    $"dtr: not enough bots, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            var tAssignments = BuildReplayAssignments(allTFiles, tBots);
            var ctAssignments = BuildReplayAssignments(allCtFiles, ctBots);
            if (tAssignments.Count == 0 && ctAssignments.Count == 0)
            {
                return LoadRoundResult.Fail(
                    $"dtr: no safe bot targets, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            var skippedT = allTFiles.Count - tAssignments.Count;
            var skippedCt = allCtFiles.Count - ctAssignments.Count;

            StopAndUnloadLoaded();
            var loaded = new List<string>();
            if (!LoadSide(tAssignments, manifestDir, loaded, out var loadError))
                return LoadRoundResult.Fail($"dtr: failed while loading round {round}: {loadError}");
            if (!LoadSide(ctAssignments, manifestDir, loaded, out loadError))
                return LoadRoundResult.Fail($"dtr: failed while loading round {round}: {loadError}");

            var partial = skippedT > 0 || skippedCt > 0
                ? $" partial replay skipped T={skippedT}/CT={skippedCt}"
                : string.Empty;
            return LoadRoundResult.Success($"dtr: loaded {loaded.Count} replays for round {round}{partial}: {string.Join(", ", loaded)}");
        }
        catch (Exception ex)
        {
            return LoadRoundResult.Fail($"dtr: load round failed: {ex.Message}");
        }
    }

    private bool LoadSide(
        IReadOnlyList<ReplayAssignment> assignments,
        string manifestDir,
        List<string> loaded,
        out string error)
    {
        error = string.Empty;
        foreach (var assignment in assignments)
        {
            var file = assignment.File;
            var bot = assignment.Bot;
            var slot = bot.Slot;
            var recPath = Path.IsPathRooted(file.Path)
                ? file.Path
                : Path.GetFullPath(Path.Combine(manifestDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));

            if (!BotControllerNative.LoadReplayFromFile(slot, recPath))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} {recPath} ({BotControllerNative.LastLoadError})";
                return false;
            }

            RememberLoadedSlot(slot);
            TrackLoadedReplay(
                slot,
                recPath,
                file.PlayerName,
                file.SteamId,
                file.FirstWeaponDefIndex ?? -1,
                file.PreloadWeaponDefIndices,
                file.Loadout);
            BotControllerNative.SetBuySkip(slot);
            TryApplyReplayIdentity(slot, file);
            loaded.Add($"{file.Side}:slot{slot}:{file.PlayerName}");
        }
        return true;
    }

    private static List<ReplayAssignment> BuildReplayAssignments(
        IReadOnlyList<ManifestFile> files,
        IReadOnlyList<CCSPlayerController> bots)
    {
        var count = Math.Min(files.Count, bots.Count);
        var assignments = new List<ReplayAssignment>(count);
        for (var i = 0; i < count; i++)
            assignments.Add(new ReplayAssignment(files[i], bots[i]));
        return assignments;
    }

    private void TryApplyReplayIdentity(int slot, ManifestFile file)
    {
        if (!_replayIdentityEnabled)
            return;

        if (file.SteamId == 0)
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: missing steam_id");
            return;
        }

        if (!_botHiderProbe.IsAvailable())
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: BotHider unavailable");
            return;
        }

        if (!_botHiderProbe.IsManagedBot(slot))
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: not a BotHider managed bot");
            return;
        }

        if (!string.IsNullOrWhiteSpace(file.PlayerName))
            Server.ExecuteCommand($"bh_setname {slot} \"{EscapeConsoleString(file.PlayerName)}\"");
        Server.ExecuteCommand($"bh_setsid {slot} {file.SteamId}");
        Server.PrintToConsole(
            $"dtr: replay identity queued slot={slot} player={file.PlayerName} sid={file.SteamId}");
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
            if (!IsReplaySlotStillSafe(slot))
                continue;
            if (_loadedReplays.TryGetValue(slot, out var replay))
            {
                ApplyReplayLoadoutForSlot(slot, replay);
                PreloadReplayWeaponsForSlot(slot, replay);
            }
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

            if (!IsReplaySlotStillSafe(slot))
            {
                ReleaseReplaySlot(slot, "unsafe_start_target");
                continue;
            }

            if (BotControllerNative.StartReplay(slot, loop))
            {
                MarkReplayStarted(slot);
                ok++;
            }
        }
        return $"dtr: started {ok}/{_loadedSlots.Count} loaded slots, loop={loop}";
    }

    private void StopAndUnloadLoaded()
    {
        foreach (var slot in _loadedSlots.ToArray())
        {
            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, "unload_all");
            BotControllerNative.UnloadReplay(slot);
        }
        _loadedSlots.Clear();
        _loadedReplays.Clear();
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _pendingWeaponAlign.Clear();
        _projectileAlignNextBySlot.Clear();
        _pendingProjectileAlign.Clear();
        _rebuiltInventorySlots.Clear();
        _loadoutSyncedSlots.Clear();
        _lastPlayingSlots.Clear();
        _replayStartedAt.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        _armed = false;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
    }

    private void MarkReplayStarted(int slot)
    {
        _lastPlayingSlots.Add(slot);
        _replayStartedAt[slot] = Server.CurrentTime;
        _projectileAlignNextBySlot[slot] = 0;
    }

    private void ReleaseReplaySlot(int slot, string reason)
    {
        _lastPlayingSlots.Remove(slot);
        _replayStartedAt.Remove(slot);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _pendingWeaponAlign.Remove(slot);
        _projectileAlignNextBySlot.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
        _loadoutSyncedSlots.Remove(slot);
        _pendingBulletHits.Remove(slot);
        _pendingBulletDamages.Remove(slot);
        BotControllerNative.ClearBuyPlan(slot);
        BotControllerNative.UnlockReplayControl(slot);
        BotControllerNative.UnlockWeaponSlot(slot);
        ResetBotBrainForHandoff(slot);
        Server.PrintToConsole($"dtr: released slot={slot} reason={reason}");
    }

    private bool HasActiveReplaySlots()
    {
        foreach (var slot in _loadedSlots)
        {
            if (BotControllerNative.GetReplayState(slot).Playing)
                return true;
        }
        return false;
    }

    private void HandoffActiveReplays(string reason, int triggerSlot = -1, int lookAtSlot = -1)
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
            if (lookAtSlot >= 0 && slot == triggerSlot)
                AimSlotAtSlot(slot, lookAtSlot);
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
            victimSlot,
            attackerSlot);
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

    private static void AimSlotAtSlot(int slot, int targetSlot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        var target = Utilities.GetPlayerFromSlot(targetSlot);
        if (player is not { IsValid: true } ||
            target is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true } ||
            target.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        try
        {
            var from = EyePosition(player.PlayerPawn.Value);
            var to = EyePosition(target.PlayerPawn.Value);
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var dz = to.Z - from.Z;
            var horizontal = MathF.Sqrt(dx * dx + dy * dy);
            if (horizontal < 0.001f)
                return;

            var pitch = -RadiansToDegrees(MathF.Atan2(dz, horizontal));
            var yaw = NormalizeYaw(RadiansToDegrees(MathF.Atan2(dy, dx)));
            player.PlayerPawn.Value.Teleport(
                (System.Numerics.Vector3?)null,
                new System.Numerics.Vector3(pitch, yaw, 0.0f),
                (System.Numerics.Vector3?)null);
        }
        catch
        {
        }
    }

    private static (float X, float Y, float Z) EyePosition(CCSPlayerPawn pawn)
    {
        var origin = pawn.AbsOrigin;
        var viewOffset = pawn.ViewOffset;
        if (origin == null || viewOffset == null)
            return (0.0f, 0.0f, 0.0f);

        return (
            origin.X + viewOffset.X,
            origin.Y + viewOffset.Y,
            origin.Z + viewOffset.Z);
    }

    private static float RadiansToDegrees(float radians)
        => radians * 180.0f / MathF.PI;

    private static float NormalizeYaw(float yaw)
    {
        while (yaw > 180.0f)
            yaw -= 360.0f;
        while (yaw < -180.0f)
            yaw += 360.0f;
        return yaw;
    }

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

    private void RememberLoadedSlot(int slot)
    {
        if (!_loadedSlots.Contains(slot))
            _loadedSlots.Add(slot);
    }

    private void ForgetLoadedReplayMetadata(int slot)
    {
        _loadedReplays.Remove(slot);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _pendingWeaponAlign.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
    }

    private void TrackLoadedReplay(
        int slot,
        string path,
        string playerName,
        ulong steamId = 0,
        int manifestFirstWeaponDefIndex = -1,
        IReadOnlyList<int>? manifestPreloadWeaponDefIndices = null,
        ReplayLoadoutSnapshot? loadout = null)
    {
        TryReadWeaponPlan(path, out var scannedFirstDef, out var scannedPreloadDefs);
        _ = BotControllerNative.TryReadReplayProjectiles(path, out var projectiles);
        var firstDef = NormalizeWeaponDefIndex(manifestFirstWeaponDefIndex);
        if (!IsKnownWeaponDefIndex(firstDef))
            firstDef = scannedFirstDef;

        var preloadDefs = NormalizePreloadWeaponDefs(
            manifestPreloadWeaponDefIndices is { Count: > 0 }
                ? manifestPreloadWeaponDefIndices
                : scannedPreloadDefs);
        var hasLoadout = loadout != null;
        _loadedReplays[slot] = new LoadedReplay(
            path,
            playerName,
            steamId,
            firstDef,
            preloadDefs,
            hasLoadout,
            NormalizeReplayLoadout(loadout ?? new ReplayLoadoutSnapshot()),
            projectiles.ToArray());
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _projectileAlignNextBySlot[slot] = 0;
        _rebuiltInventorySlots.Remove(slot);
        _loadoutSyncedSlots.Remove(slot);
    }

    private static ReplayLoadoutSnapshot NormalizeReplayLoadout(ReplayLoadoutSnapshot loadout)
    {
        return new ReplayLoadoutSnapshot
        {
            WeaponDefIndices = loadout.WeaponDefIndices?
                .Select(NormalizeWeaponDefIndex)
                .Where(IsLoadoutWeaponDefIndex)
                .ToArray() ?? Array.Empty<int>(),
            ArmorValue = Math.Min(loadout.ArmorValue, 100),
            HasHelmet = loadout.HasHelmet,
            HasDefuser = loadout.HasDefuser
        };
    }

    private void ApplyReplayLoadoutForSlot(int slot, LoadedReplay replay)
    {
        if (!_weaponAlignEnabled || !replay.HasLoadout || _loadoutSyncedSlots.Contains(slot))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true } || pawn is not { IsValid: true })
            return;

        ApplyReplayArmorAndKit(player, pawn, replay.Loadout);

        var targetItems = BuildLoadoutItemCounts(replay.Loadout);
        var deferredWeaponSync = false;
        deferredWeaponSync |= SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Primary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Primary);
        deferredWeaponSync |= SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Secondary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Secondary);
        GiveMissingLoadoutItems(
            player,
            targetItems,
            itemName => GetReplayWeaponSlot(itemName) is not ReplayWeaponSlot.Primary
                and not ReplayWeaponSlot.Secondary
                and not ReplayWeaponSlot.Knife
                and not ReplayWeaponSlot.C4);

        if (deferredWeaponSync)
            Server.NextFrame(() => Server.NextFrame(() => ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(replay), true, true)));
        else
            ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(replay), true, true);

        _loadoutSyncedSlots.Add(slot);
    }

    private static void ApplyReplayArmorAndKit(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayLoadoutSnapshot loadout)
    {
        pawn.ArmorValue = (int)loadout.ArmorValue;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");

        if (pawn.ItemServices == null || pawn.ItemServices.Handle == IntPtr.Zero)
            return;

        var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
        itemServices.HasHelmet = loadout.HasHelmet;
        itemServices.HasDefuser = player.Team == CsTeam.CounterTerrorist && loadout.HasDefuser;
    }

    private static Dictionary<string, int> BuildLoadoutItemCounts(ReplayLoadoutSnapshot loadout)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in loadout.WeaponDefIndices ?? Array.Empty<int>())
        {
            if (!TryGetWeaponClassByDefIndex(def, out var className))
                continue;
            if (GetReplayWeaponSlot(className) is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4)
                continue;
            counts[className] = counts.GetValueOrDefault(className) + 1;
        }
        return counts;
    }

    private bool SyncTargetWeaponSlot(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        ReplayWeaponSlot slot,
        Func<string, bool> predicate)
    {
        var targetItem = BestTargetSlotItem(targetItems, predicate);
        if (targetItem == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            TryGiveNamedItem(player, targetItem);
            return false;
        }

        if (HasReplayWeapon(pawn, targetItem))
            return false;

        var currentSlotWeapons = GetWeaponsInReplaySlot(pawn, slot).ToList();
        if (currentSlotWeapons.Count == 0)
        {
            TryGiveNamedItem(player, targetItem);
            return false;
        }

        var fallbackItem = currentSlotWeapons
            .Select(weapon => NormalizeWeaponClassName(weapon.DesignerName))
            .FirstOrDefault(itemName => !WeaponClassMatches(itemName, targetItem));
        var weaponToDrop = currentSlotWeapons
            .FirstOrDefault(weapon => !WeaponClassMatches(
                NormalizeWeaponClassName(weapon.DesignerName),
                targetItem));
        if (fallbackItem == null || weaponToDrop == null)
            return false;

        if (!TrySelectWeapon(player, pawn, weaponToDrop))
            return false;

        try
        {
            player.DropActiveWeapon();
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to drop slot={player.Slot} item={weaponToDrop.DesignerName}: {ex.Message}");
            return false;
        }

        _lastEnsuredWeaponDef.Remove(player.Slot);
        _lastReplayWeaponDef.Remove(player.Slot);
        Server.NextFrame(() => CompleteWeaponSlotReplacement(player, targetItem, fallbackItem, slot));
        return true;
    }

    private static string? BestTargetSlotItem(
        Dictionary<string, int> targetItems,
        Func<string, bool> predicate)
    {
        return targetItems.Keys
            .Where(predicate)
            .OrderByDescending(WeaponClassValue)
            .ThenBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private void CompleteWeaponSlotReplacement(
        CCSPlayerController player,
        string targetItem,
        string fallbackItem,
        ReplayWeaponSlot slot)
    {
        if (!player.IsValid)
            return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
            return;

        if (HasReplayWeapon(pawn, targetItem) || GetWeaponsInReplaySlot(pawn, slot).Any())
            return;

        TryGiveNamedItem(player, targetItem);
        Server.NextFrame(() => RestoreFallbackWeaponIfNeeded(player, targetItem, fallbackItem, slot));
    }

    private static void RestoreFallbackWeaponIfNeeded(
        CCSPlayerController player,
        string targetItem,
        string fallbackItem,
        ReplayWeaponSlot slot)
    {
        if (!player.IsValid)
            return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
            return;

        if (HasReplayWeapon(pawn, targetItem) || GetWeaponsInReplaySlot(pawn, slot).Any())
            return;

        TryGiveNamedItem(player, fallbackItem);
    }

    private static void GiveMissingLoadoutItems(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        Func<string, bool> predicate)
    {
        var currentItems = CountCurrentLoadoutItems(player);
        foreach (var (itemName, targetCount) in targetItems.Where(pair => predicate(pair.Key)).ToList())
        {
            var missingCount = Math.Max(0, targetCount - currentItems.GetValueOrDefault(itemName));
            for (var i = 0; i < missingCount; i++)
                TryGiveNamedItem(player, itemName);
        }
    }

    private static Dictionary<string, int> CountCurrentLoadoutItems(CCSPlayerController player)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
            return counts;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            var itemName = NormalizeWeaponClassName(weapon.DesignerName);
            if (GetReplayWeaponSlot(itemName) is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Other)
                continue;
            counts[itemName] = counts.GetValueOrDefault(itemName) + 1;
        }
        return counts;
    }

    private static bool TryGiveNamedItem(CCSPlayerController player, string itemName)
    {
        try
        {
            player.GiveNamedItem(itemName);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to give slot={player.Slot} item={itemName}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySelectWeapon(CCSPlayerController player, CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        var defIndex = WeaponDefIndex(weapon.DesignerName);
        if (player.Slot >= 0 && defIndex >= 0)
            BotControllerNative.SwitchBotWeapon(player.Slot, defIndex);

        var weaponServices = pawn.WeaponServices;
        if (weaponServices == null)
            return false;

        weaponServices.ActiveWeapon.Raw = weapon.EntityHandle.Raw;
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");

        if (player.UserId != null)
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {weapon.DesignerName}");

        return true;
    }

    private static IEnumerable<CBasePlayerWeapon> GetWeaponsInReplaySlot(CCSPlayerPawn pawn, ReplayWeaponSlot slot)
    {
        if (pawn.WeaponServices == null)
            yield break;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            if (GetReplayWeaponSlot(NormalizeWeaponClassName(weapon.DesignerName)) == slot)
                yield return weapon;
        }
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
        if (target <= 0)
        {
            if (_lastLockedWeaponTarget.Remove(slot))
                BotControllerNative.UnlockWeaponSlot(slot);
        }
        else if (force ||
                 !_lastLockedWeaponTarget.TryGetValue(slot, out var lastTarget) ||
                 lastTarget != target)
        {
            if (BotControllerNative.LockWeaponSlot(slot, target))
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

        BotControllerNative.SwitchBotWeapon(slot, normalized);
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
            BotControllerNative.SwitchBotWeapon(slot, normalized);

        Server.PrintToConsole($"dtr: aligned slot={slot} def={normalized} item={className}");
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
                $"dtr: failed to give slot={player.Slot} item={className}: {ex.Message}");
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
        actual = NormalizeWeaponClassName(actual);
        expected = NormalizeWeaponClassName(expected);
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return true;
        if (expected == "weapon_knife")
        {
            return actual.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
                   || actual.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string NormalizeWeaponClassName(string className)
    {
        return className switch
        {
            "weapon_decoy_grenade" => "weapon_decoy",
            "weapon_c4_explosive" => "weapon_c4",
            _ => className
        };
    }

    private static ReplayWeaponSlot GetReplayWeaponSlot(string className)
    {
        className = NormalizeWeaponClassName(className);
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

    private static bool IsLoadoutWeaponDefIndex(int weaponDefIndex)
    {
        if (!IsKnownWeaponDefIndex(weaponDefIndex))
            return false;
        var slot = GetReplayWeaponSlot(TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            ? className
            : string.Empty);
        return slot is not ReplayWeaponSlot.Other
            and not ReplayWeaponSlot.Knife
            and not ReplayWeaponSlot.C4;
    }

    private static int WeaponDefIndex(string className)
    {
        return NormalizeWeaponClassName(className).ToLowerInvariant() switch
        {
            "weapon_deagle" => 1,
            "weapon_elite" => 2,
            "weapon_fiveseven" => 3,
            "weapon_glock" => 4,
            "weapon_ak47" => 7,
            "weapon_aug" => 8,
            "weapon_awp" => 9,
            "weapon_famas" => 10,
            "weapon_g3sg1" => 11,
            "weapon_galilar" => 13,
            "weapon_m249" => 14,
            "weapon_m4a1" => 16,
            "weapon_mac10" => 17,
            "weapon_p90" => 19,
            "weapon_mp5sd" => 23,
            "weapon_ump45" => 24,
            "weapon_xm1014" => 25,
            "weapon_bizon" => 26,
            "weapon_mag7" => 27,
            "weapon_negev" => 28,
            "weapon_sawedoff" => 29,
            "weapon_tec9" => 30,
            "weapon_taser" => 31,
            "weapon_hkp2000" => 32,
            "weapon_mp7" => 33,
            "weapon_mp9" => 34,
            "weapon_nova" => 35,
            "weapon_p250" => 36,
            "weapon_scar20" => 38,
            "weapon_sg556" => 39,
            "weapon_ssg08" => 40,
            "weapon_knife" => 42,
            "weapon_flashbang" => 43,
            "weapon_hegrenade" => 44,
            "weapon_smokegrenade" => 45,
            "weapon_molotov" => 46,
            "weapon_decoy" => 47,
            "weapon_incgrenade" => 48,
            "weapon_c4" => 49,
            "weapon_m4a1_silencer" => 60,
            "weapon_usp_silencer" => 61,
            "weapon_cz75a" => 63,
            "weapon_revolver" => 64,
            _ => -1
        };
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
            if (version is < BotControllerNative.MinRecFormatVersion or > BotControllerNative.RecFormatVersion)
                return false;

            _ = reader.ReadSingle(); // tickrate
            _ = reader.ReadUInt32(); // round
            _ = reader.ReadByte();   // side
            _ = reader.ReadUInt32(); // flags
            _ = reader.ReadUInt64(); // steamid
            var tickCount = CheckedRecCount(reader.ReadUInt32());
            var subtickCount = CheckedRecCount(reader.ReadUInt32());
            var projectileCount = version >= 4
                ? CheckedRecCount(reader.ReadUInt32())
                : 0;
            if (tickCount == 0)
                return false;

            SkipRecString(reader);
            SkipRecString(reader);

            var codec = reader.ReadByte();
            if (codec != 1)
                return false;
            var bodyUncompressedLength = CheckedRecLength(reader.ReadUInt64());
            var bodyCompressedLength = CheckedRecLength(reader.ReadUInt64());
            var expectedBodyLength = ExpectedRecBodyLength(tickCount, subtickCount, projectileCount);
            if (bodyUncompressedLength != expectedBodyLength)
                return false;

            var compressed = reader.ReadBytes(bodyCompressedLength);
            if (compressed.Length != bodyCompressedLength)
                return false;
            var body = DecompressRecBody(compressed, bodyUncompressedLength);
            using var bodyStream = new MemoryStream(body, writable: false);
            using var bodyReader = new BinaryReader(bodyStream);
            bodyStream.Seek((long)(tickCount + 1) * BotControllerNative.MovementSnapshotByteSize, SeekOrigin.Begin);

            var preload = new List<int>();
            for (var i = 0; i < tickCount; i++)
            {
                var def = NormalizeWeaponDefIndex(bodyReader.ReadInt32());
                if (IsKnownWeaponDefIndex(def) && firstWeaponDefIndex < 0)
                    firstWeaponDefIndex = def;
                if (IsPreloadWeaponDefIndex(def))
                    preload.Add(def);
                _ = bodyReader.ReadUInt32(); // num_subtick
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

    private static int CheckedRecCount(uint value)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"count too large: {value}");
        return (int)value;
    }

    private static int CheckedRecLength(ulong value)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"body length too large: {value}");
        return (int)value;
    }

    private static int ExpectedRecBodyLength(int tickCount, int subtickCount, int projectileCount)
    {
        var snapshotCount = tickCount == 0 ? 0 : checked(tickCount + 1);
        return checked(
            snapshotCount * BotControllerNative.MovementSnapshotByteSize +
            tickCount * 8 +
            projectileCount * 48 +
            subtickCount * 28);
    }

    private static byte[] DecompressRecBody(byte[] compressed, int expectedLength)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        brotli.CopyTo(output);
        if (output.Length != expectedLength)
            throw new InvalidDataException($"decompressed body length {output.Length} != expected {expectedLength}");
        return output.ToArray();
    }

    private List<CCSPlayerController> FindReplayTargets()
    {
        var players = FindTeamPlayers();
        return players.Where(IsReplayTargetBot).ToList();
    }

    private bool IsReplayTargetBot(CCSPlayerController player)
    {
        if (!IsReplayControllerSafe(player) || IsReplayPawnTakenByController(player))
            return false;
        return player.IsBot || _botHiderProbe.IsManagedBot(player.Slot);
    }

    private bool IsReplaySlotStillSafe(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true } && IsReplayTargetBot(player);
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

    private static bool IsReplayPawnTakenByController(CCSPlayerController replayTarget)
    {
        if (replayTarget.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
            return true;

        var replayPawnIndex = replayPawn.Value.Index;
        foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
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

    private static bool ReplayBotSeesEnemy(int slot, out string contactReason)
    {
        contactReason = string.Empty;
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot is not { IsValid: true } || !bot.PawnIsAlive)
            return false;

        foreach (var enemy in FindTeamPlayers())
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !enemy.PawnIsAlive)
                continue;

            if (PlayerSeesTarget(bot, enemy, out contactReason))
                return true;
        }

        return false;
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

    private string ReplayIdentityModeName()
        => _replayIdentityEnabled ? "bothider" : "off";

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
        ulong SteamId,
        int FirstWeaponDefIndex,
        int[] PreloadWeaponDefIndices,
        bool HasLoadout,
        ReplayLoadoutSnapshot Loadout,
        ReplayProjectileEvent[] Projectiles);

    private readonly record struct ReplayAssignment(ManifestFile File, CCSPlayerController Bot);

    private readonly record struct PendingWeaponAlign(int WeaponDefIndex, bool ForceSwitch);

    private readonly record struct PendingBulletHit(int AttackerSlot, float Time);

    private readonly record struct PendingBulletDamage(int AttackerSlot, int Damage, float Time);

    private readonly record struct TeamEconomySnapshot(uint EquipmentValue, string Class);

    private sealed class PendingProjectileAlign(uint index, IntPtr handle)
    {
        public uint Index { get; } = index;
        public IntPtr Handle { get; } = handle;
        public ReplayProjectileEvent Align { get; set; }
        public int Slot { get; set; } = -1;
        public int EventIndex { get; set; } = -1;
        public int MatchAttemptsRemaining { get; set; }
        public int WritesRemaining { get; set; }
        public bool Matched { get; set; }
    }

    private readonly record struct TraceVector(float? X, float? Y, float? Z)
    {
        public static TraceVector Empty => new(null, null, null);
    }

    private sealed class UtilityProjectileTrace(uint index, IntPtr handle, string designerName)
    {
        private bool _hasLastPosition;
        private TraceVector _lastPosition = TraceVector.Empty;
        private float _lastTime;

        public uint Index { get; } = index;
        public IntPtr Handle { get; } = handle;
        public string DesignerName { get; } = designerName;

        public TraceVector EstimateVelocity(TraceVector position, float time)
        {
            if (!_hasLastPosition ||
                !position.X.HasValue ||
                !position.Y.HasValue ||
                !position.Z.HasValue ||
                !_lastPosition.X.HasValue ||
                !_lastPosition.Y.HasValue ||
                !_lastPosition.Z.HasValue)
            {
                return TraceVector.Empty;
            }

            var dt = time - _lastTime;
            if (dt <= 0.0f)
                return TraceVector.Empty;

            return new TraceVector(
                (position.X.Value - _lastPosition.X.Value) / dt,
                (position.Y.Value - _lastPosition.Y.Value) / dt,
                (position.Z.Value - _lastPosition.Z.Value) / dt);
        }

        public void Update(TraceVector position, float time)
        {
            _lastPosition = position;
            _lastTime = time;
            _hasLastPosition = position.X.HasValue && position.Y.HasValue && position.Z.HasValue;
        }
    }

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

        public bool IsAvailable()
            => TryConnect();

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

        [JsonPropertyName("loadout")]
        public ReplayLoadoutSnapshot? Loadout { get; set; }
    }

    private sealed class ReplayLoadoutSnapshot
    {
        [JsonPropertyName("weapon_def_indices")]
        public int[]? WeaponDefIndices { get; set; }

        [JsonPropertyName("armor_value")]
        public uint ArmorValue { get; set; }

        [JsonPropertyName("has_helmet")]
        public bool HasHelmet { get; set; }

        [JsonPropertyName("has_defuser")]
        public bool HasDefuser { get; set; }
    }
}
