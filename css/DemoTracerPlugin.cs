using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using DemoTracerApi;
using System.Globalization;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoTracer;

public sealed class DemoTracerPlugin : BasePlugin
{
    public override string ModuleName => "CS2 DemoTracer";
    public override string ModuleVersion => "0.1.3";
    public override string ModuleAuthor => "unicbm";
    public override string ModuleDescription => "Trace CS2 demos into bot-executable route replays.";

    public DemoTracerPlugin()
    {
        _apiFacade = new DemoTracerApiFacade(this);
    }

    private static readonly PluginCapability<IDemoTracerApi> ApiCapability = new("demotracer:api");
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly object NadeManifestCacheLock = new();
    private static readonly Dictionary<string, CachedNadeManifest> NadeManifestCache = new(StringComparer.OrdinalIgnoreCase);
    private const float HandoffGraceSeconds = 0.25f;
    private const float BulletHandoffMatchSeconds = 0.25f;
    private const int BulletHandoffMinDamage = 10;
    private const float HandoffThreat360DefaultRange = 420.0f;
    private const float HandoffThreat360MinRange = 150.0f;
    private const float HandoffThreat360MaxRange = 800.0f;
    private const float HandoffThreat360ImmediateRange = 240.0f;
    private const float HandoffThreat360HoldSeconds = 0.08f;
    private const float HandoffThreat360MaxVerticalDelta = 128.0f;
    private const float HandoffThreat360ChestZScale = 0.62f;
    private const int ProjectileAlignMatchAttempts = 8;
    private const int ProjectileAlignPostMatchWrites = 1;
    private const float NadeClipStartSettleSeconds = 0.12f;
    private const int NadeClipStartReadyRetries = 6;
    private const float NadeCycleDefaultGapSeconds = 1.5f;
    private const float NadeCycleMaxGapSeconds = 30.0f;
    private const int MinManifestAbiVersion = 12;
    private const int MaxPlayerSlots = 64;
    private const int ReplayStartHealth = 100;
    private const string FreezeTimeConVarName = "mp_freezetime";
    private enum ReplayStartAnchor
    {
        Live,
        RecordingStart,
        FreezePreroll,
    }
    private enum ReplayIdentityMode
    {
        Off,
        Name,
        Full,
    }

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
    private readonly Dictionary<int, int> _queuedNadeStartTokens = new();
    private readonly HashSet<int> _rebuiltInventorySlots = new();
    private readonly HashSet<int> _loadoutSyncedSlots = new();
    private readonly HashSet<int> _lastPlayingSlots = new();
    private readonly HashSet<int> _quietReplaySlots = new();
    private readonly Dictionary<int, float> _replayStartedAt = new();
    private readonly Dictionary<int, PendingBulletHit> _pendingBulletHits = new();
    private readonly Dictionary<int, PendingBulletDamage> _pendingBulletDamages = new();
    private readonly Dictionary<int, PendingThreat360> _pendingThreat360 = new();
    private readonly Dictionary<uint, UtilityProjectileTrace> _utilityTraceProjectiles = new();
    private readonly BotHiderMemoryProbe _botHiderProbe = new();
    private readonly RayTraceLosProbe _rayTraceLosProbe = new();
    private readonly DemoTracerApiFacade _apiFacade;
    private StreamWriter? _utilityTraceWriter;
    private string _utilityTracePath = string.Empty;
    private bool _utilityTraceEnabled;
    private ulong _lastReplayPovMask = ulong.MaxValue;

    private bool _armed;
    private bool _armedLoop;
    private string _armedLabel = string.Empty;
    private string _armedManifestPath = string.Empty;
    private int _armedSourceRound = -1;
    private bool _armedPrepared;
    private int _freezePrerollToken;
    private bool _freezePrerollStarted;

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
    private bool _handoffThreat360Enabled = true;
    private float _handoffThreat360Range = HandoffThreat360DefaultRange;
    private bool _handoffThreat360LosEnabled = true;
    private bool _partialReplayEnabled = true;
    private ReplayIdentityMode _replayIdentityMode = ReplayIdentityMode.Off;
    private int _nextNadeStartToken;
    private NadeCycleState? _nadeCycle;
    private int _nextNadeCycleToken;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        Capabilities.RegisterPluginCapability(ApiCapability, () => (IDemoTracerApi)_apiFacade);
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

    [ConsoleCommand("dtr_go", "dtr_go <seq|round|pool> ...")]
    public void GoCommand(CCSPlayerController? player, CommandInfo command)
        => DispatchPlanCommand(command, "dtr_go", restart: true);

    [ConsoleCommand("dtr_arm", "dtr_arm <seq|round|pool> ...")]
    public void ArmCommand(CCSPlayerController? player, CommandInfo command)
        => DispatchPlanCommand(command, "dtr_arm", restart: false);

    [ConsoleCommand("dtr_seq_restart", "dtr_seq_restart <manifest.json> [from_source_round]")]
    public void SequenceRestartCommand(CCSPlayerController? player, CommandInfo command)
        => RunManifestSequence(command, "dtr_seq_restart", restart: true);

    [ConsoleCommand("dtr_round_restart", "dtr_round_restart <manifest.json> <source_round>")]
    public void RoundRestartCommand(CCSPlayerController? player, CommandInfo command)
        => ArmSingleRound(command, "dtr_round_restart", restart: true);

    [ConsoleCommand("dtr_pool_restart", "dtr_pool_restart <pool_manifest.json> [server_round]")]
    public void PoolRestartCommand(CCSPlayerController? player, CommandInfo command)
        => RunPoolPlan(command, "dtr_pool_restart", restart: true);

    [ConsoleCommand("dtr_run_manifest", "dtr_run_manifest <manifest.json> [from_source_round]")]
    public void RunManifestCommand(CCSPlayerController? player, CommandInfo command)
        => RunManifestSequence(command, "dtr_run_manifest", restart: false);

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
        if (command.ArgCount > argOffset + 1 &&
            (!int.TryParse(command.GetArg(argOffset + 1), out startRound) || !rounds.Contains(startRound)))
        {
            command.ReplyToCommand($"[DTR ERR] from_source_round={command.GetArg(argOffset + 1)} was not found in manifest.");
            command.ReplyToCommand($"[DTR HINT] Available source rounds: {string.Join(", ", rounds)}.");
            return;
        }

        StopAndUnloadLoaded();
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

        command.ReplyToCommand(
            restart
                ? $"[DTR OK] Planned SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; restart=now."
                : $"[DTR OK] Armed SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; waiting for next round_start.");
        command.ReplyToCommand(
            $"[DTR OK] Sequence has {rounds.Length - _sequenceIndex} round(s) remaining from source_round={startRound}.");
        IssueRestartIfRequested(command, restart);
    }

    [ConsoleCommand("dtr_stop_sequence", "dtr_stop_sequence")]
    public void StopSequenceCommand(CCSPlayerController? player, CommandInfo command)
    {
        _sequenceActive = false;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        InvalidateFreezePreroll();
        command.ReplyToCommand("dtr: sequence stopped");
    }

    [ConsoleCommand("dtr_run_pool", "dtr_run_pool <pool_manifest.json> [server_round]")]
    public void RunPoolCommand(CCSPlayerController? player, CommandInfo command)
        => RunPoolPlan(command, "dtr_run_pool", restart: false);

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
            _weaponAlignEnabled = ParseOnOff(command.GetArg(1), _weaponAlignEnabled);
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
            _projectileAlignEnabled = ParseOnOff(command.GetArg(1), _projectileAlignEnabled);
        if (!_projectileAlignEnabled)
        {
            _projectileAlignNextBySlot.Clear();
            _pendingProjectileAlign.Clear();
        }

        command.ReplyToCommand($"dtr: projectile_align={_projectileAlignEnabled}");
    }

    [ConsoleCommand("dtr_handoff", "dtr_handoff <off|death|contact|death_or_contact> [all|slot]")]
    public void HandoffCommand(CCSPlayerController? player, CommandInfo command)
        => SetHandoffMode(command, argOffset: 1);

    private void SetHandoffMode(CommandInfo command, int argOffset)
    {
        if (command.ArgCount > argOffset)
        {
            if (!TryParseHandoffMode(command.GetArg(argOffset), out var mode))
            {
                command.ReplyToCommand("usage: dtr_handoff <off|death|contact|death_or_contact> [all|slot]");
                return;
            }
            _handoffMode = mode;
        }

        if (command.ArgCount > argOffset + 1)
        {
            var scope = command.GetArg(argOffset + 1);
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
            $"[DTR OK] handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")}");
    }

    [ConsoleCommand("dtr_handoff_360", "dtr_handoff_360 [0|1] [range] [los|nolos]")]
    public void Handoff360Command(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            var enabled = command.GetArg(1);
            if (enabled is "0" or "off" or "false")
            {
                _handoffThreat360Enabled = false;
                _pendingThreat360.Clear();
            }
            else if (enabled is "1" or "on" or "true")
            {
                _handoffThreat360Enabled = true;
            }
            else
            {
                command.ReplyToCommand("usage: dtr_handoff_360 [0|1] [range] [los|nolos]");
                return;
            }
        }

        if (command.ArgCount >= 3)
        {
            if (!float.TryParse(command.GetArg(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var range))
            {
                command.ReplyToCommand("usage: dtr_handoff_360 [0|1] [range] [los|nolos]");
                return;
            }
            _handoffThreat360Range = Math.Clamp(range, HandoffThreat360MinRange, HandoffThreat360MaxRange);
            _pendingThreat360.Clear();
        }

        if (command.ArgCount >= 4)
        {
            var los = command.GetArg(3);
            if (los.Equals("los", StringComparison.OrdinalIgnoreCase) ||
                los.Equals("ray", StringComparison.OrdinalIgnoreCase) ||
                los.Equals("raytrace", StringComparison.OrdinalIgnoreCase) ||
                los is "1" or "on" or "true")
            {
                _handoffThreat360LosEnabled = true;
            }
            else if (los.Equals("nolos", StringComparison.OrdinalIgnoreCase) ||
                     los.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                     los is "0" or "false")
            {
                _handoffThreat360LosEnabled = false;
            }
            else
            {
                command.ReplyToCommand("usage: dtr_handoff_360 [0|1] [range] [los|nolos]");
                return;
            }
            _pendingThreat360.Clear();
        }

        command.ReplyToCommand(
            $"dtr: handoff_360={_handoffThreat360Enabled} range={_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={_handoffThreat360LosEnabled} raytrace={_rayTraceLosProbe.ProbeStatus}");
    }

    private static string EscapeConsoleString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool ParseOnOff(string value, bool fallback)
        => value.ToLowerInvariant() switch
        {
            "1" or "on" or "true" or "yes" or "full" or "name" => true,
            "0" or "off" or "false" or "no" => false,
            _ => fallback,
        };

    private static string FormatOnOff(bool value)
        => value ? "on" : "off";

    private void SetIdentityMode(CommandInfo command)
    {
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_set identity <off|name|full>");
            return;
        }

        switch (command.GetArg(2).ToLowerInvariant())
        {
            case "off":
            case "0":
            case "false":
                _replayIdentityMode = ReplayIdentityMode.Off;
                break;
            case "name":
                _replayIdentityMode = ReplayIdentityMode.Name;
                break;
            case "full":
            case "1":
            case "on":
            case "true":
                _replayIdentityMode = ReplayIdentityMode.Full;
                break;
            default:
                command.ReplyToCommand("usage: dtr_set identity <off|name|full>");
                return;
        }

        command.ReplyToCommand($"[DTR OK] identity={ReplayIdentityModeName()}");
    }

    private void SetAlignMode(CommandInfo command)
    {
        if (command.ArgCount < 4)
        {
            command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles> <off|on>");
            return;
        }

        var enabled = ParseOnOff(command.GetArg(3), false);
        switch (command.GetArg(2).ToLowerInvariant())
        {
            case "weapons":
            case "weapon":
            case "loadout":
            case "active_weapon":
            case "active-weapon":
            case "slot_lock":
            case "slot-lock":
                _weaponAlignEnabled = enabled;
                if (!_weaponAlignEnabled)
                {
                    _pendingWeaponAlign.Clear();
                    _rebuiltInventorySlots.Clear();
                    _lastReplayWeaponDef.Clear();
                    _lastLockedWeaponTarget.Clear();
                    foreach (var slot in _loadedSlots)
                        BotControllerNative.UnlockWeaponSlot(slot);
                }
                command.ReplyToCommand($"[DTR OK] align weapons={FormatOnOff(_weaponAlignEnabled)}");
                if (command.GetArg(2).Equals("loadout", StringComparison.OrdinalIgnoreCase) ||
                    command.GetArg(2).Equals("active_weapon", StringComparison.OrdinalIgnoreCase) ||
                    command.GetArg(2).Equals("slot_lock", StringComparison.OrdinalIgnoreCase) ||
                    command.GetArg(2).Equals("active-weapon", StringComparison.OrdinalIgnoreCase) ||
                    command.GetArg(2).Equals("slot-lock", StringComparison.OrdinalIgnoreCase))
                {
                    command.ReplyToCommand("[DTR WARN] loadout/active_weapon/slot_lock currently share the weapons align implementation.");
                }
                return;
            case "projectiles":
            case "projectile":
                _projectileAlignEnabled = enabled;
                if (!_projectileAlignEnabled)
                {
                    _projectileAlignNextBySlot.Clear();
                    _pendingProjectileAlign.Clear();
                }
                command.ReplyToCommand($"[DTR OK] align projectiles={FormatOnOff(_projectileAlignEnabled)}");
                return;
            default:
                command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles> <off|on>");
                return;
        }
    }

    [ConsoleCommand("dtr_partial", "dtr_partial <0|1>")]
    public void PartialCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _partialReplayEnabled = ParseOnOff(command.GetArg(1), _partialReplayEnabled);

        command.ReplyToCommand($"dtr: partial_replay={_partialReplayEnabled}");
    }

    [ConsoleCommand("dtr_replay_identity", "dtr_replay_identity <0|1>")]
    public void ReplayIdentityCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _replayIdentityMode = ParseOnOff(command.GetArg(1), false)
                ? ReplayIdentityMode.Full
                : ReplayIdentityMode.Off;

        command.ReplyToCommand($"dtr: replay_identity={ReplayIdentityModeName()}");
    }

    [ConsoleCommand("dtr_set", "dtr_set <identity|align|handoff|allow_partial> ...")]
    public void SetCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_set identity <off|name|full>");
            command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles> <off|on>");
            command.ReplyToCommand("usage: dtr_set handoff <off|death|contact|death_or_contact> [slot|all]");
            command.ReplyToCommand("usage: dtr_set allow_partial <off|on>");
            return;
        }

        switch (command.GetArg(1).ToLowerInvariant())
        {
            case "identity":
                SetIdentityMode(command);
                return;
            case "align":
                SetAlignMode(command);
                return;
            case "handoff":
                SetHandoffMode(command, argOffset: 2);
                return;
            case "allow_partial":
            case "partial":
                if (command.ArgCount < 3)
                {
                    command.ReplyToCommand("usage: dtr_set allow_partial <off|on>");
                    return;
                }
                _partialReplayEnabled = ParseOnOff(command.GetArg(2), _partialReplayEnabled);
                command.ReplyToCommand($"[DTR OK] allow_partial={FormatOnOff(_partialReplayEnabled)}");
                return;
            default:
                command.ReplyToCommand("[DTR ERR] unknown setting namespace. Use identity, align, handoff, or allow_partial.");
                return;
        }
    }

    [ConsoleCommand("dtr_load", "dtr_load <round|slot> ...")]
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
    public void LoadRoundCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseRoundArgs(command, "dtr_load_round", out var manifestPath, out var round))
            return;

        var result = LoadRound(manifestPath, round);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("dtr_list_nades", "dtr_list_nades <nade_manifest.json> [kind]")]
    public void ListNadesCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_list_nades <nade_manifest.json> [kind]");
            return;
        }

        var manifestPath = command.GetArg(1);
        if (!TryReadNadeManifest(manifestPath, out var manifest, out var error))
        {
            command.ReplyToCommand($"dtr: failed to read nade manifest: {error}");
            return;
        }

        var kindFilter = command.ArgCount >= 3 ? command.GetArg(2) : string.Empty;
        var clips = manifest.Clips
            .Where(clip => NadeKindMatchesFilter(clip, kindFilter))
            .OrderBy(clip => clip.Side, StringComparer.Ordinal)
            .ThenBy(clip => clip.Phase, StringComparer.Ordinal)
            .ThenBy(clip => clip.Kind, StringComparer.Ordinal)
            .ThenBy(clip => clip.Round)
            .ThenBy(clip => clip.ThrowTick)
            .ToList();

        command.ReplyToCommand($"dtr: nade clips {clips.Count}/{manifest.Clips.Count} map={manifest.Map}");
        foreach (var clip in clips.Take(40))
        {
            command.ReplyToCommand(
                $"dtr: {clip.ClipId} {clip.Side}/{clip.Phase}/{clip.Kind} round={clip.Round} player={clip.PlayerName} tick={clip.ThrowTick}");
        }
        if (clips.Count > 40)
            command.ReplyToCommand($"dtr: ... {clips.Count - 40} more clips");
    }

    [ConsoleCommand("dtr_run_nade", "dtr_run_nade <nade_manifest.json> <clip_id> <slot> [loop:0|1]")]
    public void RunNadeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 4)
        {
            command.ReplyToCommand("usage: dtr_run_nade <nade_manifest.json> <clip_id> <slot> [loop:0|1]");
            return;
        }
        if (!int.TryParse(command.GetArg(3), out var slot) || slot < 0)
        {
            command.ReplyToCommand("dtr: slot must be a non-negative integer");
            return;
        }

        var manifestPath = command.GetArg(1);
        var clipId = command.GetArg(2);
        var loop = command.ArgCount >= 5 && command.GetArg(4) != "0";
        var result = RunNadeClip(manifestPath, clipId, slot, loop);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("dtr_cycle_smokes", "dtr_cycle_smokes <nade_manifest.json> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]")]
    public void CycleSmokesCommand(CCSPlayerController? player, CommandInfo command)
    {
        RunNadeCycleCommand(command, "smoke", "dtr_cycle_smokes");
    }

    [ConsoleCommand("dtr_cycle_flashes", "dtr_cycle_flashes <nade_manifest.json> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]")]
    public void CycleFlashesCommand(CCSPlayerController? player, CommandInfo command)
    {
        RunNadeCycleCommand(command, "flash", "dtr_cycle_flashes");
    }

    [ConsoleCommand("dtr_cycle_he", "dtr_cycle_he <nade_manifest.json> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]")]
    public void CycleHeCommand(CCSPlayerController? player, CommandInfo command)
    {
        RunNadeCycleCommand(command, "he", "dtr_cycle_he");
    }

    [ConsoleCommand("dtr_cycle_fire", "dtr_cycle_fire <nade_manifest.json> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]")]
    public void CycleFireCommand(CCSPlayerController? player, CommandInfo command)
    {
        RunNadeCycleCommand(command, "molotov", "dtr_cycle_fire");
    }

    [ConsoleCommand("dtr_cycle_random_nades", "dtr_cycle_random_nades <nade_manifest.json> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]")]
    public void CycleRandomNadesCommand(CCSPlayerController? player, CommandInfo command)
    {
        RunNadeCycleCommand(command, "random", "dtr_cycle_random_nades");
    }

    private void RunNadeCycleCommand(CommandInfo command, string kindFilter, string commandName)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand($"usage: {commandName} <nade_manifest.json> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]");
            return;
        }
        if (!int.TryParse(command.GetArg(2), out var slot) || slot < 0)
        {
            command.ReplyToCommand("dtr: slot must be a non-negative integer");
            return;
        }
        if (!TryParseNadeCycleArgs(command, 3, commandName, out var sideFilter, out var phaseFilter, out var gapSeconds, out var parseError))
        {
            command.ReplyToCommand(parseError);
            return;
        }

        var result = StartNadeCycle(command.GetArg(1), slot, kindFilter, sideFilter, phaseFilter, gapSeconds);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("dtr_stop_nade_cycle", "dtr_stop_nade_cycle")]
    public void StopNadeCycleCommand(CCSPlayerController? player, CommandInfo command)
    {
        var stopped = StopNadeCycle("manual_stop_nade_cycle", stopCurrent: true);
        command.ReplyToCommand(stopped ? "dtr: nade cycle stopped" : "dtr: no active nade cycle");
    }

    [ConsoleCommand("dtr_arm_round", "dtr_arm_round <manifest.json> <source_round> [loop:0|1]")]
    public void ArmRoundCommand(CCSPlayerController? player, CommandInfo command)
        => ArmSingleRound(command, "dtr_arm_round", restart: false);

    private void ArmSingleRound(
        CommandInfo command,
        string commandName,
        bool restart,
        int argOffset = 1)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseRoundArgs(command, commandName, out var manifestPath, out var round, argOffset))
            return;

        if (!ManifestContainsSourceRound(manifestPath, round, out var validateError))
        {
            command.ReplyToCommand(validateError);
            return;
        }

        var loop = command.ArgCount > argOffset + 2 && command.GetArg(argOffset + 2) != "0";
        StopAndUnloadLoaded();
        _sequenceActive = false;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _poolActive = false;
        InvalidateFreezePreroll();
        _armed = true;
        _armedLoop = loop;
        _armedPrepared = false;
        _armedManifestPath = manifestPath;
        _armedSourceRound = round;
        _armedLabel = $"source_round={round} manifest={manifestPath}";
        command.ReplyToCommand(
            restart
                ? $"[DTR OK] Planned SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; restart=now."
                : $"[DTR OK] Armed SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; waiting for next round_start.");
        command.ReplyToCommand("[DTR OK] This plan will not advance to later manifest rounds.");
        IssueRestartIfRequested(command, restart);
    }

    [ConsoleCommand("dtr_play_loaded", "dtr_play_loaded [loop:0|1]")]
    public void PlayLoadedCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        var loop = command.ArgCount >= 2 && command.GetArg(1) != "0";
        command.ReplyToCommand(PlayLoaded(loop));
    }

    [ConsoleCommand("dtr_play", "dtr_play <loaded|slot> ...")]
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

    [ConsoleCommand("dtr_stop_all", "dtr_stop_all")]
    public void StopAllCommand(CCSPlayerController? player, CommandInfo command)
    {
        StopAllState("manual_stop_all");
        command.ReplyToCommand("[DTR OK] all DemoTracer replay state stopped");
    }

    [ConsoleCommand("dtr_unload", "dtr_unload <slot>")]
    public void UnloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var ok = BotControllerNative.UnloadReplay(slot);
        if (ok)
        {
            if (IsNadeCycleSlot(slot))
                StopNadeCycle("manual_unload", stopCurrent: false);
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

    [ConsoleCommand("dtr_status", "dtr_status [slot <slot>|<slot>]")]
    public void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            TryReadFreezeTimeConVar(out var freezeTime, out var freezeReason);
            var plan = _sequenceActive
                ? _sequenceIndex < _sequenceRounds.Length
                    ? $"sequence from_source_round={_sequenceRounds[_sequenceIndex]} prepared={_sequencePrepared}:{_sequencePreparedRound}"
                    : "sequence complete"
                : _armed
                    ? $"single source_round={_armedSourceRound} prepared={_armedPrepared}"
                    : _poolActive
                        ? $"pool server_round={_poolRoundIndex} candidates={_poolManifest?.Candidates.Count ?? 0}"
                        : "none";
            command.ReplyToCommand(
                $"[DTR OK] status plan={plan} loaded_slots={_loadedSlots.Count} settings identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} allow_partial={FormatOnOff(_partialReplayEnabled)} mp_freezetime={(float.IsFinite(freezeTime) ? freezeTime.ToString("F2", CultureInfo.InvariantCulture) : "unknown")} {(string.IsNullOrEmpty(freezeReason) ? "" : freezeReason)}");
            return;
        }

        var slotArg = command.GetArg(1).Equals("slot", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (!TryParseSlotAt(command, slotArg, out var slot))
            return;
        var state = BotControllerNative.GetReplayState(slot);
        var sequence = _sequenceActive && _sequenceIndex < _sequenceRounds.Length
            ? $" sequence_next={_sequenceRounds[_sequenceIndex]}"
            : string.Empty;
        var pool = _poolActive
            ? $" pool_next={_poolRoundIndex}"
            : string.Empty;
        command.ReplyToCommand(
            $"dtr: abi={BotControllerNative.AbiVersion} slot={slot} playing={state.Playing} cursor={state.Cursor} total={state.Total} handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")} handoff_360={_handoffThreat360Enabled}:{_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={_handoffThreat360LosEnabled}:{_rayTraceLosProbe.ProbeStatus} partial={_partialReplayEnabled} identity={ReplayIdentityModeName()} projectile_align={_projectileAlignEnabled}{sequence}{pool}");
    }

    [ConsoleCommand("dtr_runtime", "dtr_runtime")]
    public void RuntimeCommand(CCSPlayerController? player, CommandInfo command)
    {
        command.ReplyToCommand(
            $"[DTR OK] DemoTracer expected_abi={BotControllerNative.ExpectedAbiVersion} runtime_abi={BotControllerNative.AbiVersion} compatible={BotControllerNative.IsCompatible}");
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
        {
            if (PrepareNextSequenceRound("round_start"))
                ScheduleFreezePrerollStart($"sequence round {_sequencePreparedRound}");
        }
        else if (_armed)
        {
            if (PrepareArmedRound("round_start"))
                ScheduleFreezePrerollStart(_armedLabel);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        InvalidateFreezePreroll();

        if (_sequenceActive)
        {
            Server.NextFrame(StartPreparedSequenceRound);
            return HookResult.Continue;
        }

        if (_poolActive)
        {
            Server.NextFrame(StartNextPoolRound);
            return HookResult.Continue;
        }

        if (!_armed)
            return HookResult.Continue;
        if (!_armedPrepared)
        {
            Server.PrintToConsole($"[DTR WARN] armed round is waiting for the next full round_start: {_armedLabel}");
            return HookResult.Continue;
        }

        var loop = _armedLoop;
        var label = _armedLabel;
        _armed = false;
        _armedPrepared = false;
        Server.NextFrame(() =>
        {
            var message = StartLoaded(loop);
            Server.PrintToConsole($"dtr: auto-start {label}: {message}");
        });
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
        if (_utilityTraceEnabled && _nadeCycle == null)
            TraceGrenadeThrown(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled && _nadeCycle == null)
            TraceSmokeDetonate(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled && _nadeCycle == null)
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

        if (_utilityTraceEnabled && _nadeCycle == null)
            TraceUtilityTick();

        if (_loadedSlots.Count == 0)
        {
            SetReplayPovMask(0);
            return;
        }

        var playerSnapshot = BuildTickPlayerSnapshot();
        UpdateReplayPovMask(playerSnapshot);

        foreach (var slot in _loadedSlots.ToArray())
        {
            var state = BotControllerNative.GetReplayState(slot);
            if (!state.Playing)
            {
                if (_lastPlayingSlots.Contains(slot))
                {
                    ReleaseReplaySlot(slot, "replay_finished");
                    if (IsNadeCycleSlot(slot))
                        QueueNextNadeCycleClip("replay_finished");
                }
                continue;
            }

            if (!IsReplaySlotStillSafe(slot, playerSnapshot))
            {
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "unsafe_replay_target");
                if (IsNadeCycleSlot(slot))
                    StopNadeCycle("unsafe_replay_target", stopCurrent: false);
                continue;
            }

            if (!_lastPlayingSlots.Contains(slot))
                MarkReplayStarted(slot);

            if (HandoffIncludesContact(_handoffMode) && ReplayHasPassedHandoffGrace(slot) &&
                ReplayBotHasContact(slot, playerSnapshot, out var contactReason, out _))
            {
                HandoffActiveReplays($"enemy_contact_{contactReason}_slot{slot}", slot);
                continue;
            }

            if (!_weaponAlignEnabled)
                continue;

            if (_loadedReplays.TryGetValue(slot, out var replay) && replay.UtilityOnly)
            {
                ApplyReplayWeaponPreset(slot, replay.UtilityWeaponDefIndex, allowSlotReplacement: false, force: true);
                continue;
            }

            var weaponDefIndex = NormalizeWeaponDefIndex(state.WeaponDefIndex);
            if (weaponDefIndex < 0)
            {
                _lastReplayWeaponDef.Remove(slot);
                continue;
            }
            if (_lastReplayWeaponDef.TryGetValue(slot, out var lastDef) &&
                lastDef == weaponDefIndex)
                continue;

            ApplyReplayWeaponPreset(slot, weaponDefIndex, allowSlotReplacement: true, force: false);
        }
    }

    private void UpdateReplayPovMask(TickPlayerSnapshot playerSnapshot)
    {
        SetReplayPovMask(BuildReplayPovMask(playerSnapshot));
    }

    private ulong BuildReplayPovMask(TickPlayerSnapshot playerSnapshot)
    {
        if (_loadedSlots.Count == 0 || _lastPlayingSlots.Count == 0)
            return 0;

        var replayPawnSlots = new Dictionary<uint, int>();
        foreach (var slot in _loadedSlots)
        {
            if (slot is < 0 or >= MaxPlayerSlots || !_lastPlayingSlots.Contains(slot))
                continue;

            if (!playerSnapshot.TryGetSlot(slot, out var replayController) ||
                replayController is not { IsValid: true })
                continue;
            if (replayController.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
                continue;

            replayPawnSlots[replayPawn.Value.Index] = slot;
        }

        if (replayPawnSlots.Count == 0)
            return 0;

        ulong mask = 0;
        foreach (var controller in playerSnapshot.Controllers)
        {
            if (controller is not { IsValid: true })
                continue;
            if (controller.IsBot || _botHiderProbe.IsManagedBot(controller.Slot))
                continue;
            if (!TryGetInEyeObserverTargetIndex(controller, out var targetIndex))
                continue;
            if (replayPawnSlots.TryGetValue(targetIndex, out var targetSlot))
                mask |= 1UL << targetSlot;
        }

        return mask;
    }

    private static bool TryGetInEyeObserverTargetIndex(CCSPlayerController controller, out uint targetIndex)
    {
        targetIndex = 0;
        try
        {
            CPlayer_ObserverServices? observerServices = null;
            if (controller.ObserverPawn is { IsValid: true, Value.IsValid: true } observerPawn)
                observerServices = observerPawn.Value.ObserverServices;
            else if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } playerPawn)
                observerServices = playerPawn.Value.ObserverServices;

            if (observerServices == null ||
                observerServices.ObserverMode != (byte)ObserverMode_t.OBS_MODE_IN_EYE)
                return false;
            if (observerServices.ObserverTarget is not { IsValid: true, Value.IsValid: true } target)
                return false;

            targetIndex = target.Value.Index;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetReplayPovMask(ulong mask)
    {
        if (mask == _lastReplayPovMask)
            return;

        _ = BotControllerNative.SetReplayPovMask(mask);
        _lastReplayPovMask = mask;
    }

    private void ClearReplayPovSlot(int slot)
    {
        if (slot is < 0 or >= MaxPlayerSlots || _lastReplayPovMask == ulong.MaxValue)
            return;

        SetReplayPovMask(_lastReplayPovMask & ~(1UL << slot));
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (!TryGetProjectileKind(entity, out var kind, out var weaponDefIndex))
            return;

        try
        {
            var projectile = new CBaseCSGrenadeProjectile(entity.Handle);
            if (!projectile.IsValid)
                return;
            TrackProjectileAlignCandidate(projectile, kind, weaponDefIndex);
            if (_utilityTraceEnabled && _nadeCycle == null)
            {
                _utilityTraceProjectiles[projectile.Index] =
                    new UtilityProjectileTrace(projectile.Index, entity.Handle, projectile.DesignerName);
                TraceProjectileEvent("projectile_spawned", projectile, null);
            }
        }
        catch (Exception ex)
        {
            if (_utilityTraceEnabled && _nadeCycle == null)
                TraceUtilityMessage("projectile_spawn_failed", ex.Message);
        }
    }

    private void TrackProjectileAlignCandidate(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        if (!_projectileAlignEnabled)
            return;
        if (kind == ReplayProjectileKind.Molotov)
        {
            if (_utilityTraceEnabled && _nadeCycle == null)
                TraceUtilityMessage("projectile_align_skipped", $"projectile={projectile.Index} kind=molotov");
            return;
        }

        var pending = new PendingProjectileAlign(projectile.Index, projectile.Handle, kind, weaponDefIndex)
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
                var projectile = new CBaseCSGrenadeProjectile(pending.Handle);
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
                if (_utilityTraceEnabled && _nadeCycle == null)
                    TraceUtilityMessage("projectile_align_failed", $"index={entry.Key} {ex.Message}");
            }
        }
    }

    private bool TryResolveAndApplyProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileAlign pending)
    {
        if (!_projectileAlignEnabled ||
            !TryResolveProjectileAlign(
                projectile,
                pending.Kind,
                pending.WeaponDefIndex,
                out var slot,
                out var eventIndex,
                out var align))
            return false;

        ApplyProjectileAlign(projectile, align);
        _projectileAlignNextBySlot[slot] = eventIndex + 1;
        pending.Matched = true;
        pending.Slot = slot;
        pending.EventIndex = eventIndex;
        pending.Align = align;
        pending.WritesRemaining = ProjectileAlignPostMatchWrites;
        _pendingProjectileAlign[pending.Index] = pending;

        if (_utilityTraceEnabled && _nadeCycle == null)
        {
            TraceUtilityMessage(
                "projectile_align",
                $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} init_vel=({align.InitialVelocity.X:F3},{align.InitialVelocity.Y:F3},{align.InitialVelocity.Z:F3})");
        }
        return true;
    }

    private bool TryResolveProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex,
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
        eventIndex = FindProjectileAlignEvent(replay.Projectiles, next, state.Cursor, kind, weaponDefIndex);
        if (eventIndex < 0)
            return false;

        align = replay.Projectiles[eventIndex];
        return true;
    }

    private static void ApplyProjectileAlign(CBaseCSGrenadeProjectile projectile, ReplayProjectileEvent align)
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
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        const int MaxCursorDistance = 96;
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = Math.Max(start, 0); i < events.Count; i++)
        {
            var candidate = events[i];
            if (candidate.Kind != kind)
                continue;
            if (!ProjectileWeaponDefMatches(kind, weaponDefIndex, candidate.WeaponDefIndex))
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

    private static bool ProjectileWeaponDefMatches(
        ReplayProjectileKind kind,
        int liveWeaponDefIndex,
        int replayWeaponDefIndex)
    {
        if (liveWeaponDefIndex <= 0 || replayWeaponDefIndex <= 0)
            return true;
        if (liveWeaponDefIndex == replayWeaponDefIndex)
            return true;

        // CS2 commonly exposes incendiary projectiles under the same molotov
        // projectile class. Treat 46/48 as the same projectile kind for align,
        // while still preparing the bot with the exact replay weapon def.
        return kind == ReplayProjectileKind.Molotov &&
               liveWeaponDefIndex is 46 or 48 &&
               replayWeaponDefIndex is 46 or 48;
    }

    private static bool TryGetProjectileThrowerSlot(CBaseCSGrenadeProjectile projectile, out int slot)
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
            var projectile = new CBaseCSGrenadeProjectile(tracked.Handle);
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
                var projectile = new CBaseCSGrenadeProjectile(tracked.Handle);
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
        CBaseCSGrenadeProjectile projectile,
        UtilityProjectileTrace? tracked)
    {
        var time = Server.CurrentTime;
        var projectileName = SafeObject(() => projectile.DesignerName)?.ToString() ?? "";
        var origin = SafeVector(() => projectile.AbsOrigin);
        var absVelocity = SafeVector(() => projectile.AbsVelocity);
        var initialPosition = SafeVector(() => projectile.InitialPosition);
        var initialVelocity = SafeVector(() => projectile.InitialVelocity);
        var smokeDetonationPosition = IsSmokeProjectileName(projectileName)
            ? SafeVector(() => new CSmokeGrenadeProjectile(projectile.Handle).SmokeDetonationPos)
            : TraceVector.Empty;
        var estimate = tracked?.EstimateVelocity(origin, time) ?? TraceVector.Empty;
        tracked?.Update(origin, time);

        TraceWrite(RowFields(
            ("kind", kind),
            ("time", TimeField()),
            ("projectile_index", projectile.Index),
            ("projectile_name", projectileName),
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

    private void TraceNadeStage(string kind, int slot, NadeClip clip, string message)
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
        var activeWeapon = ActiveWeaponName(pawn);
        var activeDef = SafeObject(() => BotControllerNative.BotActiveWeaponDef(slot));
        var clipSummary = string.IsNullOrWhiteSpace(clip.ClipId)
            ? "clip=<unknown>"
            : $"clip={clip.ClipId} side={clip.Side} phase={clip.Phase} kind={clip.Kind} round={clip.Round} throw_tick={clip.ThrowTick} def={clip.WeaponDefIndex} first_def={clip.FirstWeaponDefIndex}";
        var fullMessage =
            $"{clipSummary} active_def={activeDef} playing={state.Playing} cursor={state.Cursor}/{state.Total} {message}";

        if (IsNadeCycleSlot(slot) || IsQuietReplaySlot(slot))
            return;

        Server.PrintToConsole($"dtr: nade_trace {kind} slot={slot} {fullMessage}");
        TraceWrite(RowFields(
            ("kind", kind),
            ("time", TimeField()),
            ("slot", slot),
            ("player", bot?.PlayerName ?? clip.PlayerName),
            ("steam_id", bot?.SteamID ?? clip.SteamId),
            ("replay_cursor", state.Cursor),
            ("replay_total", state.Total),
            ("weapon_def", hasTick ? tick.WeaponDefIndex : clip.WeaponDefIndex),
            ("live_weapon", activeWeapon),
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
            ("stash_set", SafeObject(() => pawn?.GrenadeParametersStashed.ToString())),
            ("stash_time", SafeObject(() => pawn?.GrenadeParameterStashTime)),
            ("stash_x", stashPosition.X),
            ("stash_y", stashPosition.Y),
            ("stash_z", stashPosition.Z),
            ("stash_vx", stashVelocity.X),
            ("stash_vy", stashVelocity.Y),
            ("stash_vz", stashVelocity.Z),
            ("stash_pitch", stashAngles.X),
            ("stash_yaw", stashAngles.Y),
            ("message", fullMessage)
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

    private static bool TryGetProjectileKind(
        CEntityInstance entity,
        out ReplayProjectileKind kind,
        out int weaponDefIndex)
    {
        kind = ReplayProjectileKind.Unknown;
        weaponDefIndex = -1;
        if (!entity.IsValid || string.IsNullOrEmpty(entity.DesignerName))
            return false;

        var name = entity.DesignerName;
        if (IsSmokeProjectileName(name))
        {
            kind = ReplayProjectileKind.Smoke;
            weaponDefIndex = 45;
            return true;
        }
        if (name.Contains("flashbang_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Flash;
            weaponDefIndex = 43;
            return true;
        }
        if (name.Contains("hegrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("he_grenade_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.He;
            weaponDefIndex = 44;
            return true;
        }
        if (name.Contains("incgrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("incendiarygrenade_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Molotov;
            weaponDefIndex = 48;
            return true;
        }
        if (name.Contains("molotov_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Molotov;
            weaponDefIndex = 46;
            return true;
        }
        if (name.Contains("decoy_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Decoy;
            weaponDefIndex = 47;
            return true;
        }

        return false;
    }

    private static bool IsSmokeProjectileName(string name)
        => name.Contains("smokegrenade_projectile", StringComparison.OrdinalIgnoreCase);

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

    private bool PrepareArmedRound(string reason)
    {
        if (!_armed)
            return false;
        if (_armedPrepared)
            return true;
        if (string.IsNullOrWhiteSpace(_armedManifestPath) || _armedSourceRound < 0)
        {
            _armed = false;
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

    private static bool TryParseRoundArgs(
        CommandInfo command,
        string commandName,
        out string manifestPath,
        out int round,
        int argOffset = 1)
    {
        manifestPath = string.Empty;
        round = 0;
        if (command.ArgCount <= argOffset + 1)
        {
            command.ReplyToCommand($"usage: {commandName} <manifest.json> <source_round>");
            return false;
        }

        manifestPath = command.GetArg(argOffset);
        if (int.TryParse(command.GetArg(argOffset + 1), out round) && round >= 0)
            return true;

        command.ReplyToCommand("dtr: source_round must be a non-negative integer");
        return false;
    }

    private static bool TryParseSlot(CommandInfo command, out int slot)
        => TryParseSlotAt(command, 1, out slot);

    private static bool TryParseSlotAt(CommandInfo command, int argIndex, out int slot)
    {
        slot = 0;
        if (command.ArgCount > argIndex && int.TryParse(command.GetArg(argIndex), out slot) && slot >= 0)
            return true;

        command.ReplyToCommand("usage: command <slot> ...");
        return false;
    }

    private LoadRoundResult RunNadeClip(string manifestPath, string clipId, int slot, bool loop, bool quiet = false)
    {
        try
        {
            if (!TryFindNadeClip(manifestPath, clipId, out _, out var clip, out var readError))
                return LoadRoundResult.Fail($"dtr: failed to read nade manifest: {readError}");

            if (clip == null)
                return LoadRoundResult.Fail($"dtr: nade clip not found: {clipId}");

            return RunNadeClip(manifestPath, clip, slot, loop, quiet);
        }
        catch (Exception ex)
        {
            return LoadRoundResult.Fail($"dtr: failed to run nade clip: {ex.Message}");
        }
    }

    private LoadRoundResult RunNadeClip(string manifestPath, NadeClip clip, int slot, bool loop, bool quiet = false)
    {
        try
        {
            if (!IsReplaySlotStillSafe(slot))
                return LoadRoundResult.Fail($"dtr: refused to run nade on slot {slot}: not a safe bot target");

            var recPath = ResolveManifestPath(manifestPath, clip.Path);
            if (!File.Exists(recPath))
                return LoadRoundResult.Fail($"dtr: nade clip file missing: {recPath}");
            var utilityWeaponDefIndex = ChooseNadeClipUtilityWeaponDefIndex(clip);
            if (!IsUtilityWeaponDefIndex(utilityWeaponDefIndex))
            {
                return LoadRoundResult.Fail(
                    $"dtr: nade clip {clip.ClipId} has no valid utility weapon def (manifest={clip.WeaponDefIndex}, first={clip.FirstWeaponDefIndex})");
            }

            if (quiet)
                _quietReplaySlots.Add(slot);

            TraceNadeStage(
                "nade_command",
                slot,
                clip,
                $"loop={loop} target_def={utilityWeaponDefIndex} path=\"{recPath}\"");

            if (_loadedSlots.Contains(slot) || BotControllerNative.GetReplayState(slot).Total > 0)
            {
                TraceNadeStage("nade_reload", slot, clip, "stopping previous replay before loading clip");
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "nade_reload");
                BotControllerNative.UnloadReplay(slot);
                _loadedSlots.Remove(slot);
                ForgetLoadedReplayMetadata(slot);
                if (quiet)
                    _quietReplaySlots.Add(slot);
            }

            if (!BotControllerNative.LoadReplayFromFile(slot, recPath, out var replayMetadata))
            {
                _quietReplaySlots.Remove(slot);
                return LoadRoundResult.Fail(
                    $"dtr: failed to load nade clip {clip.ClipId} on slot {slot}: {BotControllerNative.LastLoadError}");
            }

            RememberLoadedSlot(slot);
            TrackLoadedReplay(
                slot,
                recPath,
                clip.PlayerName,
                clip.SteamId,
                utilityWeaponDefIndex,
                new[] { utilityWeaponDefIndex },
                null,
                utilityOnly: true,
                utilityWeaponDefIndex: utilityWeaponDefIndex,
                replayMetadata: replayMetadata);
            TraceNadeStage("nade_loaded", slot, clip, "clip loaded and metadata tracked");
            if (!PrepareNadeClipWeapon(slot, utilityWeaponDefIndex, out var weaponError))
            {
                TraceNadeStage("nade_prepare_failed", slot, clip, weaponError);
                if (quiet)
                    _quietReplaySlots.Remove(slot);
                return LoadRoundResult.Fail($"dtr: failed to prepare nade weapon: {weaponError}");
            }
            TraceNadeStage("nade_prepare_ok", slot, clip, $"initial prepare completed target_def={utilityWeaponDefIndex}");

            _pendingProjectileAlign.Clear();
            var token = ++_nextNadeStartToken;
            _queuedNadeStartTokens[slot] = token;
            var settleUntilTime = Server.CurrentTime + NadeClipStartSettleSeconds;
            TraceNadeStage(
                "nade_queued",
                slot,
                clip,
                $"token={token} target_def={utilityWeaponDefIndex} settle_seconds={F(NadeClipStartSettleSeconds)} ready_retries={NadeClipStartReadyRetries}");
            QueueNadeClipStart(
                slot,
                token,
                clip,
                utilityWeaponDefIndex,
                loop,
                settleUntilTime,
                NadeClipStartReadyRetries);
            return LoadRoundResult.Success(
                $"dtr: queued nade {clip.ClipId} slot={slot} {clip.Side}/{clip.Phase}/{clip.Kind} round={clip.Round} player={clip.PlayerName} loop={loop} start_delay_seconds={F(NadeClipStartSettleSeconds)}");
        }
        catch (Exception ex)
        {
            return LoadRoundResult.Fail($"dtr: failed to run nade clip: {ex.Message}");
        }
    }

    private void QueueNadeClipStart(
        int slot,
        int token,
        NadeClip clip,
        int utilityWeaponDefIndex,
        bool loop,
        float settleUntilTime,
        int readyRetriesRemaining)
    {
        if (!IsQueuedNadeStartCurrent(slot, token))
            return;

        var settleRemaining = settleUntilTime - Server.CurrentTime;
        if (settleRemaining > 0.0f)
        {
            TraceNadeStage(
                "nade_wait_settle",
                slot,
                clip,
                $"token={token} settle_remaining={F(settleRemaining)} ready_retries={readyRetriesRemaining}");
            Server.NextFrame(() =>
                QueueNadeClipStart(
                    slot,
                    token,
                    clip,
                    utilityWeaponDefIndex,
                    loop,
                    settleUntilTime,
                    readyRetriesRemaining));
            return;
        }

        if (!IsReplaySlotStillSafe(slot))
        {
            FailQueuedNadeStart(slot, token, clip.ClipId, $"slot {slot} is not a safe bot target");
            return;
        }

        if (!PrepareNadeClipWeapon(slot, utilityWeaponDefIndex, false, out var weaponError))
        {
            TraceNadeStage(
                "nade_wait_ready",
                slot,
                clip,
                $"token={token} target_def={utilityWeaponDefIndex} retries_remaining={readyRetriesRemaining} {weaponError}");
            if (readyRetriesRemaining > 0)
            {
                Server.NextFrame(() =>
                    QueueNadeClipStart(
                        slot,
                        token,
                    clip,
                    utilityWeaponDefIndex,
                    loop,
                    Server.CurrentTime,
                    readyRetriesRemaining - 1));
                return;
            }

            FailQueuedNadeStart(slot, token, clip.ClipId, $"weapon not ready: {weaponError}");
            return;
        }

        TraceNadeStage("nade_ready", slot, clip, $"token={token} target_def={utilityWeaponDefIndex} starting next frame");
        Server.NextFrame(() => StartQueuedNadeClip(slot, token, clip, loop));
    }

    private void StartQueuedNadeClip(int slot, int token, NadeClip clip, bool loop)
    {
        if (!IsQueuedNadeStartCurrent(slot, token))
            return;

        if (!IsReplaySlotStillSafe(slot))
        {
            FailQueuedNadeStart(slot, token, clip.ClipId, $"slot {slot} is not a safe bot target");
            return;
        }

        if (!StartReplayForSlot(slot, loop))
        {
            var state = BotControllerNative.GetReplayState(slot);
            FailQueuedNadeStart(
                slot,
                token,
                clip.ClipId,
                $"native start failed (cursor={state.Cursor}, total={state.Total})");
            return;
        }

        _queuedNadeStartTokens.Remove(slot);
        MarkReplayStarted(slot);
        TraceNadeStage("nade_start_ok", slot, clip, $"loop={loop}");
        if (!IsQuietReplaySlot(slot))
        {
            Server.PrintToConsole(
                $"dtr: playing nade {clip.ClipId} slot={slot} {clip.Side}/{clip.Phase}/{clip.Kind} round={clip.Round} player={clip.PlayerName} loop={loop}");
        }
    }

    private bool IsQueuedNadeStartCurrent(int slot, int token)
        => _queuedNadeStartTokens.TryGetValue(slot, out var current) && current == token;

    private void FailQueuedNadeStart(int slot, int token, string clipId, string reason)
    {
        if (!IsQueuedNadeStartCurrent(slot, token))
            return;

        var quiet = IsQuietReplaySlot(slot);
        _queuedNadeStartTokens.Remove(slot);
        BotControllerNative.StopReplay(slot);
        ReleaseReplaySlot(slot, "nade_start_failed");
        BotControllerNative.UnloadReplay(slot);
        _loadedSlots.Remove(slot);
        ForgetLoadedReplayMetadata(slot);
        if (!quiet)
            TraceNadeStage("nade_start_failed", slot, new NadeClip { ClipId = clipId }, reason);
        if (!quiet)
            Server.PrintToConsole($"dtr: failed to play nade {clipId} on slot {slot}: {reason}");
        if (IsNadeCycleSlot(slot))
            QueueNextNadeCycleClip("nade_start_failed");
    }

    private LoadRoundResult StartNadeCycle(
        string manifestPath,
        int slot,
        string kindFilter,
        string sideFilter,
        string phaseFilter,
        float gapSeconds)
    {
        try
        {
            if (!TryReadNadeManifest(manifestPath, out var manifest, out var readError))
                return LoadRoundResult.Fail($"dtr: failed to read nade manifest: {readError}");
            if (!IsReplaySlotStillSafe(slot))
                return LoadRoundResult.Fail($"dtr: refused to cycle {kindFilter}s on slot {slot}: not a safe bot target");

            var clips = manifest.Clips
                .Where(clip => NadeCycleKindMatches(clip, kindFilter))
                .Where(clip => NadeCycleSideMatches(clip, sideFilter))
                .Where(clip => NadeCyclePhaseMatches(clip, phaseFilter))
                .OrderBy(clip => clip.Side, StringComparer.Ordinal)
                .ThenBy(clip => clip.Phase, StringComparer.Ordinal)
                .ThenBy(clip => clip.Round)
                .ThenBy(clip => clip.ThrowTick)
                .ThenBy(clip => clip.ClipId, StringComparer.Ordinal)
                .ToList();
            if (NadeCycleIsRandom(kindFilter))
                clips = clips.OrderBy(_ => Guid.NewGuid()).ToList();
            if (clips.Count == 0)
            {
                return LoadRoundResult.Fail(
                    $"dtr: {kindFilter} cycle has no clips for side={sideFilter} phase={phaseFilter}");
            }

            var disabledTrace = _utilityTraceEnabled;
            if (disabledTrace)
                StopUtilityTrace();
            StopNadeCycle($"new_{kindFilter}_cycle", stopCurrent: true);
            var token = ++_nextNadeCycleToken;
            _nadeCycle = new NadeCycleState(
                token,
                Path.GetFullPath(manifestPath),
                clips,
                slot,
                kindFilter,
                sideFilter,
                phaseFilter,
                gapSeconds);
            Server.PrintToConsole(
                $"dtr: {kindFilter} cycle start clips={clips.Count} slot={slot} side={sideFilter} phase={phaseFilter} gap={F(gapSeconds)}s trace={(disabledTrace ? "disabled" : "off")}");
            StartCurrentNadeCycleClip("cycle_start");
            return LoadRoundResult.Success(
                $"dtr: {kindFilter} cycle queued clips={clips.Count} slot={slot} side={sideFilter} phase={phaseFilter} gap={F(gapSeconds)}s");
        }
        catch (Exception ex)
        {
            return LoadRoundResult.Fail($"dtr: failed to start {kindFilter} cycle: {ex.Message}");
        }
    }

    private void StartCurrentNadeCycleClip(string reason)
    {
        var cycle = _nadeCycle;
        if (cycle == null || !IsNadeCycleCurrent(cycle.Token))
            return;
        if (cycle.Index >= cycle.Clips.Count)
        {
            CompleteNadeCycle("complete");
            return;
        }
        if (!IsReplaySlotStillSafe(cycle.Slot))
        {
            StopNadeCycle("unsafe_cycle_target", stopCurrent: false);
            return;
        }

        var clip = cycle.Clips[cycle.Index];
        var displayIndex = cycle.Index + 1;
        cycle.Index++;
        cycle.Waiting = false;
        Server.PrintToConsole(
            $"dtr: {cycle.KindFilter} cycle {displayIndex}/{cycle.Clips.Count} slot={cycle.Slot} kind={clip.Kind} clip={clip.ClipId} {clip.Side}/{clip.Phase} round={clip.Round} player={clip.PlayerName} tick={clip.ThrowTick} reason={reason}");
        var result = RunNadeClip(cycle.ManifestPath, clip, cycle.Slot, loop: false);
        if (!result.Ok)
        {
            Server.PrintToConsole($"dtr: {cycle.KindFilter} cycle clip failed: {result.Message}");
            QueueNextNadeCycleClip("clip_load_failed");
        }
    }

    private void QueueNextNadeCycleClip(string reason)
    {
        var cycle = _nadeCycle;
        if (cycle == null || !IsNadeCycleCurrent(cycle.Token))
            return;
        if (cycle.Waiting)
            return;
        if (cycle.Index >= cycle.Clips.Count)
        {
            CompleteNadeCycle(reason);
            return;
        }

        cycle.Waiting = true;
        var startTime = Server.CurrentTime + cycle.GapSeconds;
        Server.PrintToConsole(
            $"dtr: {cycle.KindFilter} cycle wait gap={F(cycle.GapSeconds)}s next={cycle.Index + 1}/{cycle.Clips.Count} reason={reason}");
        Server.NextFrame(() => ContinueNadeCycleAfterGap(cycle.Token, startTime));
    }

    private void ContinueNadeCycleAfterGap(int token, float startTime)
    {
        var cycle = _nadeCycle;
        if (cycle == null || cycle.Token != token)
            return;
        if (Server.CurrentTime < startTime)
        {
            Server.NextFrame(() => ContinueNadeCycleAfterGap(token, startTime));
            return;
        }

        StartCurrentNadeCycleClip("gap_elapsed");
    }

    private bool StopNadeCycle(string reason, bool stopCurrent)
    {
        var cycle = _nadeCycle;
        if (cycle == null)
            return false;

        _nadeCycle = null;
        _nextNadeCycleToken++;
        if (stopCurrent)
        {
            BotControllerNative.StopReplay(cycle.Slot);
            ReleaseReplaySlot(cycle.Slot, reason);
            BotControllerNative.UnloadReplay(cycle.Slot);
            _loadedSlots.Remove(cycle.Slot);
            ForgetLoadedReplayMetadata(cycle.Slot);
        }
        Server.PrintToConsole(
            $"dtr: {cycle.KindFilter} cycle stopped reason={reason} played={cycle.Index}/{cycle.Clips.Count} slot={cycle.Slot}");
        return true;
    }

    private void CompleteNadeCycle(string reason)
    {
        var cycle = _nadeCycle;
        if (cycle == null)
            return;
        _nadeCycle = null;
        _nextNadeCycleToken++;
        Server.PrintToConsole(
            $"dtr: {cycle.KindFilter} cycle complete reason={reason} played={cycle.Index}/{cycle.Clips.Count} slot={cycle.Slot}");
    }

    private bool IsNadeCycleCurrent(int token)
        => _nadeCycle is { } cycle && cycle.Token == token;

    private bool IsNadeCycleSlot(int slot)
        => _nadeCycle is { } cycle && cycle.Slot == slot;

    private LoadRoundResult LoadRound(string manifestPath, int round)
    {
        var replayStateReplaced = false;
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
            replayStateReplaced = true;
            var loaded = new List<string>();
            if (!LoadSide(tAssignments, manifestDir, loaded, out var loadError))
                return FailLoadRoundAfterPartialLoad(round, loadError);
            if (!LoadSide(ctAssignments, manifestDir, loaded, out loadError))
                return FailLoadRoundAfterPartialLoad(round, loadError);

            var partial = skippedT > 0 || skippedCt > 0
                ? $" partial replay skipped T={skippedT}/CT={skippedCt}"
                : string.Empty;
            return LoadRoundResult.Success($"dtr: loaded {loaded.Count} replays for round {round}{partial}: {string.Join(", ", loaded)}");
        }
        catch (Exception ex)
        {
            if (replayStateReplaced)
                StopAndUnloadLoaded();
            return LoadRoundResult.Fail($"dtr: load round failed: {ex.Message}");
        }
    }

    private LoadRoundResult FailLoadRoundAfterPartialLoad(int round, string error)
    {
        StopAndUnloadLoaded();
        return LoadRoundResult.Fail($"dtr: failed while loading round {round}: {error}");
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

            if (!BotControllerNative.LoadReplayFromFile(slot, recPath, out var replayMetadata))
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
                file.Loadout,
                replayMetadata: replayMetadata);
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
        if (_replayIdentityMode == ReplayIdentityMode.Off)
            return;

        if (_replayIdentityMode == ReplayIdentityMode.Full && file.SteamId == 0)
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
        if (_replayIdentityMode == ReplayIdentityMode.Full)
            Server.ExecuteCommand($"bh_setsid {slot} {file.SteamId}");
        Server.PrintToConsole(
            _replayIdentityMode == ReplayIdentityMode.Full
                ? $"dtr: replay identity queued slot={slot} player={file.PlayerName} sid={file.SteamId}"
                : $"dtr: replay identity queued slot={slot} player={file.PlayerName}");
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
                if (replay.UtilityOnly)
                {
                    if (_weaponAlignEnabled)
                        PrepareNadeClipWeapon(slot, replay.UtilityWeaponDefIndex, out _);
                    continue;
                }
                if (_weaponAlignEnabled)
                {
                    ApplyReplayLoadoutForSlot(slot, replay);
                    PreloadReplayWeaponsForSlot(slot, replay);
                }
            }
        }
    }

    private string StartLoaded(bool loop)
        => StartLoaded(loop, ReplayStartAnchor.Live, null);

    private string StartLoaded(bool loop, ReplayStartAnchor anchor, float? freezeTimeSeconds)
    {
        var respawned = RespawnDeadLoadedReplayBots();
        if (respawned > 0)
        {
            Server.NextFrame(() =>
            {
                PreloadLoadedReplays();
                Server.PrintToConsole($"dtr: queued start after respawn: {StartLoadedReady(loop, anchor, freezeTimeSeconds)}");
            });
            return $"dtr: respawned {respawned} replay bot(s), start queued";
        }

        return StartLoadedReady(loop, anchor, freezeTimeSeconds);
    }

    private string StartLoadedReady(bool loop, ReplayStartAnchor anchor, float? freezeTimeSeconds)
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

            if (_loadedReplays.TryGetValue(slot, out var replay) && !replay.UtilityOnly)
            {
                if (!ResetReplayPawnRoundStartHealth(slot))
                {
                    ReleaseReplaySlot(slot, "dead_start_target");
                    continue;
                }
            }

            if (StartReplayForSlot(slot, loop, anchor, freezeTimeSeconds))
            {
                MarkReplayStarted(slot);
                ok++;
            }
        }
        return $"dtr: started {ok}/{_loadedSlots.Count} loaded slots, loop={loop}";
    }

    private bool StartReplayForSlot(int slot, bool loop)
        => StartReplayForSlot(slot, loop, ReplayStartAnchor.Live, null);

    private bool StartReplayForSlot(int slot, bool loop, ReplayStartAnchor anchor, float? freezeTimeSeconds)
    {
        var startIndex = 0u;
        if (_loadedReplays.TryGetValue(slot, out var replay))
        {
            if (anchor == ReplayStartAnchor.FreezePreroll)
            {
                if (replay.PlayStartTickIndex == 0)
                    return false;

                startIndex = FreezePrerollStartIndex(replay, freezeTimeSeconds ?? 0.0f);
                return startIndex < replay.PlayStartTickIndex &&
                       BotControllerNative.StartReplayUntil(
                           slot,
                           loop,
                           startIndex,
                           replay.PlayStartTickIndex);
            }

            startIndex = anchor switch
            {
                ReplayStartAnchor.Live => replay.PlayStartTickIndex,
                _ => 0,
            };
        }
        return BotControllerNative.StartReplayAt(slot, loop, startIndex);
    }

    private void ScheduleFreezePrerollStart(string label)
    {
        if (!TryGetFreezePrerollSchedule(out var freezeTimeSeconds, out var delaySeconds, out var reason))
        {
            Server.PrintToConsole($"dtr: freeze pre-roll skipped for {label}: {reason}");
            return;
        }

        var token = ++_freezePrerollToken;
        _freezePrerollStarted = false;
        void Start()
        {
            if (token != _freezePrerollToken || _freezePrerollStarted)
                return;
            _freezePrerollStarted = true;
            PreloadLoadedReplays();
            var message = StartLoaded(loop: false, ReplayStartAnchor.FreezePreroll, freezeTimeSeconds);
            Server.PrintToConsole(
                $"dtr: freeze pre-roll start {label}: mp_freezetime={freezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}s delay={delaySeconds.ToString("F2", CultureInfo.InvariantCulture)}s; {message}");
        }

        if (delaySeconds <= 0.01f)
        {
            Server.NextFrame(Start);
        }
        else
        {
            AddTimer(delaySeconds, Start);
            Server.PrintToConsole(
                $"dtr: freeze pre-roll scheduled {label}: mp_freezetime={freezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}s delay={delaySeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
        }
    }

    private void InvalidateFreezePreroll()
    {
        _freezePrerollToken++;
        _freezePrerollStarted = false;
    }

    private bool TryGetFreezePrerollSchedule(
        out float freezeTimeSeconds,
        out float delaySeconds,
        out string reason)
    {
        freezeTimeSeconds = 0.0f;
        delaySeconds = 0.0f;
        if (!TryReadFreezeTimeConVar(out freezeTimeSeconds, out reason))
            return false;
        if (freezeTimeSeconds <= 0.0f)
        {
            reason = $"{FreezeTimeConVarName} is {freezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}";
            return false;
        }

        var maxRecordedPrerollSeconds = 0.0f;
        foreach (var replay in _loadedReplays.Values)
        {
            if (replay.UtilityOnly || replay.PlayStartTickIndex == 0 || replay.TickRate <= 0.0f)
                continue;
            maxRecordedPrerollSeconds = Math.Max(
                maxRecordedPrerollSeconds,
                replay.PlayStartTickIndex / replay.TickRate);
        }

        if (maxRecordedPrerollSeconds <= 0.0f)
        {
            reason = "loaded replays have no recorded freeze pre-roll";
            return false;
        }

        var playbackPrerollSeconds = Math.Min(freezeTimeSeconds, maxRecordedPrerollSeconds);
        var scheduleWindowSeconds = freezeTimeSeconds;
        if (TryReadFreezePhaseRemaining(out var phaseRemainingSeconds, out _) &&
            phaseRemainingSeconds > 0.0f)
        {
            scheduleWindowSeconds = phaseRemainingSeconds;
        }

        delaySeconds = Math.Max(0.0f, scheduleWindowSeconds - playbackPrerollSeconds);
        reason = string.Empty;
        return true;
    }

    private static bool TryReadFreezePhaseRemaining(out float seconds, out string reason)
    {
        seconds = 0.0f;
        try
        {
            var proxy = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(entity => entity is { IsValid: true });
            if (proxy is not { IsValid: true })
            {
                reason = "cs_gamerules entity was not found";
                return false;
            }

            var rules = proxy.GameRules;
            if (rules == null)
            {
                reason = "cs_gamerules has no rules object";
                return false;
            }

            if (!rules.FreezePeriod)
            {
                reason = "game rules are not in freeze period";
                return false;
            }

            var phaseTime = rules.TimeUntilNextPhaseStarts;
            if (!float.IsFinite(phaseTime))
            {
                reason = "game rules phase end time is invalid";
                return false;
            }

            seconds = phaseTime > Server.CurrentTime
                ? phaseTime - Server.CurrentTime
                : phaseTime;
            if (seconds > 0.0f && float.IsFinite(seconds))
            {
                reason = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = $"failed to read game rules freeze phase: {ex.Message}";
            return false;
        }

        reason = "game rules freeze phase has no remaining time";
        return false;
    }

    private static bool TryReadFreezeTimeConVar(out float seconds, out string reason)
    {
        seconds = 0.0f;
        var conVar = ConVar.Find(FreezeTimeConVarName);
        if (conVar == null)
        {
            reason = $"server ConVar {FreezeTimeConVarName} was not found";
            return false;
        }

        try
        {
            seconds = conVar.GetPrimitiveValue<float>();
        }
        catch
        {
            try
            {
                seconds = conVar.GetPrimitiveValue<int>();
            }
            catch (Exception ex)
            {
                reason = $"failed to read {FreezeTimeConVarName}: {ex.Message}";
                return false;
            }
        }

        if (float.IsFinite(seconds) && seconds >= 0.0f)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{FreezeTimeConVarName} has invalid value {seconds.ToString(CultureInfo.InvariantCulture)}";
        return false;
    }

    private static uint FreezePrerollStartIndex(LoadedReplay replay, float freezeTimeSeconds)
    {
        if (replay.PlayStartTickIndex == 0 || replay.TickRate <= 0.0f || freezeTimeSeconds <= 0.0f)
            return replay.PlayStartTickIndex;

        var serverFreezeTicks = (uint)Math.Round(freezeTimeSeconds * replay.TickRate);
        return serverFreezeTicks >= replay.PlayStartTickIndex
            ? 0
            : replay.PlayStartTickIndex - serverFreezeTicks;
    }

    private int RespawnDeadLoadedReplayBots()
    {
        var respawned = 0;
        foreach (var slot in _loadedSlots)
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay) || replay.UtilityOnly)
                continue;

            if (!IsReplaySlotStillSafe(slot))
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || player.PawnIsAlive)
                continue;

            try
            {
                player.Respawn();
                _loadoutSyncedSlots.Remove(slot);
                _rebuiltInventorySlots.Remove(slot);
                _lastEnsuredWeaponDef.Remove(slot);
                _lastReplayWeaponDef.Remove(slot);
                _lastLockedWeaponTarget.Remove(slot);
                respawned++;
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"dtr: failed to respawn replay bot slot={slot}: {ex.Message}");
            }
        }

        return respawned;
    }

    private void StopAndUnloadLoaded()
    {
        StopNadeCycle("unload_all", stopCurrent: false);
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
        _queuedNadeStartTokens.Clear();
        _rebuiltInventorySlots.Clear();
        _loadoutSyncedSlots.Clear();
        _lastPlayingSlots.Clear();
        _quietReplaySlots.Clear();
        _replayStartedAt.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        _pendingThreat360.Clear();
        _armed = false;
        _armedPrepared = false;
        _armedManifestPath = string.Empty;
        _armedSourceRound = -1;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        SetReplayPovMask(0);
    }

    private void StopSequenceState()
    {
        _sequenceActive = false;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        InvalidateFreezePreroll();
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

    private void StopLoadedReplaySlots(string reason)
    {
        StopNadeCycle(reason, stopCurrent: false);
        foreach (var slot in _loadedSlots.ToArray())
        {
            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason);
        }
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _pendingWeaponAlign.Clear();
        _projectileAlignNextBySlot.Clear();
        _pendingProjectileAlign.Clear();
        _queuedNadeStartTokens.Clear();
        _rebuiltInventorySlots.Clear();
        _lastPlayingSlots.Clear();
        _quietReplaySlots.Clear();
        _replayStartedAt.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        _pendingThreat360.Clear();
        SetReplayPovMask(0);
    }

    private void StopAllState(string reason)
    {
        StopLoadedReplaySlots(reason);
        _armed = false;
        _armedPrepared = false;
        _armedManifestPath = string.Empty;
        _armedSourceRound = -1;
        StopSequenceState();
        StopPoolState();
    }

    private void StopOneSlot(CommandInfo command, int slot, string reason)
    {
        var ok = BotControllerNative.StopReplay(slot);
        ReleaseReplaySlot(slot, reason);
        if (IsNadeCycleSlot(slot))
            StopNadeCycle(reason, stopCurrent: false);
        command.ReplyToCommand(ok
            ? $"[DTR OK] stopped slot {slot}"
            : $"[DTR ERR] failed to stop slot {slot}");
    }

    private static void IssueRestartIfRequested(CommandInfo command, bool restart)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        command.ReplyToCommand("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_start.");
    }

    private void MarkReplayStarted(int slot)
    {
        _lastPlayingSlots.Add(slot);
        _replayStartedAt[slot] = Server.CurrentTime;
        _projectileAlignNextBySlot[slot] = 0;
    }

    private void ReleaseReplaySlot(int slot, string reason)
    {
        if (_loadedReplays.TryGetValue(slot, out var releasedReplay) && releasedReplay.UtilityOnly)
            _pendingProjectileAlign.Clear();
        _lastPlayingSlots.Remove(slot);
        _replayStartedAt.Remove(slot);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _pendingWeaponAlign.Remove(slot);
        _projectileAlignNextBySlot.Remove(slot);
        _queuedNadeStartTokens.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
        _loadoutSyncedSlots.Remove(slot);
        _pendingBulletHits.Remove(slot);
        _pendingBulletDamages.Remove(slot);
        _pendingThreat360.Remove(slot);
        BotControllerNative.ClearBuyPlan(slot);
        BotControllerNative.UnlockReplayControl(slot);
        BotControllerNative.UnlockWeaponSlot(slot);
        ResetBotBrainForHandoff(slot);
        ClearReplayPovSlot(slot);
        var quiet = _quietReplaySlots.Remove(slot);
        if (!quiet)
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

    private bool IsQuietReplaySlot(int slot)
        => _quietReplaySlots.Contains(slot);

    private bool IsReplaySlotBusy(int slot)
    {
        if (slot < 0)
            return false;
        var state = BotControllerNative.GetReplayState(slot);
        return state.Playing || _queuedNadeStartTokens.ContainsKey(slot);
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
        _queuedNadeStartTokens.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
    }

    private void TrackLoadedReplay(
        int slot,
        string path,
        string playerName,
        ulong steamId = 0,
        int manifestFirstWeaponDefIndex = -1,
        IReadOnlyList<int>? manifestPreloadWeaponDefIndices = null,
        ReplayLoadoutSnapshot? loadout = null,
        bool utilityOnly = false,
        int utilityWeaponDefIndex = -1,
        ReplayFileMetadata? replayMetadata = null)
    {
        var metadata = replayMetadata ?? ReadReplayMetadataOrEmpty(path);
        TryBuildWeaponPlan(metadata.WeaponDefIndices ?? [], out var scannedFirstDef, out var scannedPreloadDefs);
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
            metadata.Projectiles ?? [],
            utilityOnly,
            NormalizeWeaponDefIndex(utilityWeaponDefIndex),
            metadata.TickRate,
            metadata.PlayStartTickIndex);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _projectileAlignNextBySlot[slot] = 0;
        _rebuiltInventorySlots.Remove(slot);
        _loadoutSyncedSlots.Remove(slot);
    }

    private static ReplayFileMetadata ReadReplayMetadataOrEmpty(string path)
        => BotControllerNative.TryReadReplayMetadata(path, out var metadata)
            ? metadata
            : ReplayFileMetadata.Empty;

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
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
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

    private bool PrepareNadeClipWeapon(int slot, int weaponDefIndex, out string error)
        => PrepareNadeClipWeapon(slot, weaponDefIndex, allowGive: true, out error);

    private bool PrepareNadeClipWeapon(int slot, int weaponDefIndex, bool allowGive, out string error)
    {
        error = string.Empty;
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (!IsUtilityWeaponDefIndex(normalized))
        {
            error = $"weapon def {weaponDefIndex} is not a grenade";
            return false;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
        {
            error = $"slot {slot} is not a valid bot";
            return false;
        }

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                replaceConflictingSlot: false,
                out var className))
        {
            error = allowGive ? $"could not ensure {normalized}" : $"weapon {normalized} is not present yet";
            return false;
        }

        BotControllerNative.SwitchBotWeapon(slot, normalized);
        _lastEnsuredWeaponDef[slot] = normalized;
        _lastReplayWeaponDef[slot] = normalized;
        if (!IsQuietReplaySlot(slot))
            Server.PrintToConsole($"dtr: prepared nade slot={slot} def={normalized} item={className}");
        return true;
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

    private static bool ResetReplayPawnRoundStartHealth(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        var pawn = player.PlayerPawn.Value;
        pawn.Health = ReplayStartHealth;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        return true;
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
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            if (targetItem != null)
                TryGiveNamedItem(player, targetItem);
            return false;
        }

        if (targetItem != null && HasReplayWeapon(pawn, targetItem))
            return false;

        var currentSlotWeapons = GetWeaponsInReplaySlot(pawn, slot).ToList();
        if (targetItem == null)
        {
            var extraWeapon = currentSlotWeapons.FirstOrDefault();
            return extraWeapon != null &&
                   DropAndKillReplayWeapon(player, pawn, extraWeapon, "extra_loadout_slot");
        }

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

        if (!DropAndKillReplayWeapon(player, pawn, weaponToDrop, "replace_loadout_slot"))
            return false;

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

    private static bool DropAndKillReplayWeapon(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string reason)
    {
        var weaponName = weapon.DesignerName;
        if (!TrySelectWeapon(player, pawn, weapon))
            return false;

        try
        {
            player.DropActiveWeapon();
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to drop slot={player.Slot} item={weaponName}: {ex.Message}");
            return false;
        }

        KillDroppedWeapon(player.Slot, weapon, weaponName, reason);
        Server.NextFrame(() => KillDroppedWeapon(player.Slot, weapon, weaponName, reason));
        return true;
    }

    private static void KillDroppedWeapon(
        int slot,
        CBasePlayerWeapon weapon,
        string weaponName,
        string reason)
    {
        try
        {
            if (weapon.IsValid)
                weapon.AcceptInput("Kill");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to kill dropped weapon slot={slot} item={weaponName} reason={reason}: {ex.Message}");
        }
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
                _ = EnsureReplayWeaponForSlot(
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
            var ensured = EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: true,
                replaceConflictingSlot: false);
            if (!ensured)
            {
                _lastReplayWeaponDef.Remove(slot);
                return;
            }
        }

        if (BotControllerNative.SwitchBotWeapon(slot, normalized))
            _lastReplayWeaponDef[slot] = normalized;
        else
            _lastReplayWeaponDef.Remove(slot);
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
            _ = EnsureReplayWeaponForSlot(
                slot,
                request.WeaponDefIndex,
                request.ForceSwitch,
                allowGive: false,
                replaceConflictingSlot: false);
    }

    private bool EnsureReplayWeaponForSlot(
        int slot,
        int weaponDefIndex,
        bool forceSwitch,
        bool allowGive,
        bool replaceConflictingSlot)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
            return false;
        if (_lastEnsuredWeaponDef.TryGetValue(slot, out var last) && last == normalized && !forceSwitch)
            return true;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                replaceConflictingSlot,
                out var className))
            return false;

        _lastEnsuredWeaponDef[slot] = normalized;
        if (forceSwitch)
        {
            if (!BotControllerNative.SwitchBotWeapon(slot, normalized))
            {
                _lastEnsuredWeaponDef.Remove(slot);
                return false;
            }
        }

        Server.PrintToConsole($"dtr: aligned slot={slot} def={normalized} item={className}");
        return true;
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

        var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
        if (activeWeapon != null &&
            activeWeapon.IsValid &&
            WeaponClassMatches(activeWeapon.DesignerName, className))
        {
            return true;
        }

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

    private static bool IsUtilityWeaponDefIndex(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return false;
        return GetReplayWeaponSlot(className) == ReplayWeaponSlot.Utility;
    }

    private static int ChooseNadeClipUtilityWeaponDefIndex(NadeClip clip)
    {
        var first = NormalizeWeaponDefIndex(clip.FirstWeaponDefIndex);
        if (IsUtilityWeaponDefIndex(first) && NadeWeaponDefMatchesKind(clip.Kind, first))
            return first;

        var manifest = NormalizeWeaponDefIndex(clip.WeaponDefIndex);
        if (IsUtilityWeaponDefIndex(manifest))
            return manifest;

        return first;
    }

    private static bool NadeWeaponDefMatchesKind(string kind, int weaponDefIndex)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        return kind.Trim().ToLowerInvariant() switch
        {
            "flash" => normalized == 43,
            "he" => normalized == 44,
            "smoke" => normalized == 45,
            "molotov" => normalized is 46 or 48,
            "decoy" => normalized == 47,
            _ => true
        };
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

    private static bool TryBuildWeaponPlan(
        IReadOnlyList<int> weaponDefIndices,
        out int firstWeaponDefIndex,
        out int[] preloadWeaponDefIndices)
    {
        firstWeaponDefIndex = -1;
        preloadWeaponDefIndices = [];

        if (weaponDefIndices.Count == 0)
            return false;

        var preload = new List<int>();
        foreach (var value in weaponDefIndices)
        {
            var def = NormalizeWeaponDefIndex(value);
            if (IsKnownWeaponDefIndex(def) && firstWeaponDefIndex < 0)
                firstWeaponDefIndex = def;
            if (IsPreloadWeaponDefIndex(def))
                preload.Add(def);
        }
        preloadWeaponDefIndices = NormalizePreloadWeaponDefs(preload);
        return true;
    }

    private static TickPlayerSnapshot BuildTickPlayerSnapshot()
    {
        var controllers = FindPlayerControllers();
        return new TickPlayerSnapshot(controllers, FindTeamPlayers(controllers));
    }

    private sealed class TickPlayerSnapshot
    {
        private readonly Dictionary<int, CCSPlayerController> _bySlot = new();

        public TickPlayerSnapshot(
            IReadOnlyList<CCSPlayerController> controllers,
            IReadOnlyList<CCSPlayerController> teamPlayers)
        {
            Controllers = controllers;
            TeamPlayers = teamPlayers;

            foreach (var controller in controllers)
            {
                if (controller is not { IsValid: true } || controller.Slot < 0)
                    continue;
                _bySlot.TryAdd(controller.Slot, controller);
            }
        }

        public IReadOnlyList<CCSPlayerController> Controllers { get; }
        public IReadOnlyList<CCSPlayerController> TeamPlayers { get; }

        public bool TryGetSlot(int slot, out CCSPlayerController player)
        {
            if (_bySlot.TryGetValue(slot, out var value))
            {
                player = value;
                return true;
            }

            player = null!;
            return false;
        }
    }

    private List<CCSPlayerController> FindReplayTargets()
    {
        var players = FindTeamPlayers();
        return players.Where(IsReplayTargetBot).ToList();
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
        return player.IsBot || _botHiderProbe.IsManagedBot(player.Slot);
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
        => _replayIdentityMode switch
        {
            ReplayIdentityMode.Name => "name",
            ReplayIdentityMode.Full => "full",
            _ => "off",
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
            ValidateConversionManifest(manifest);
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

    private static void ValidateConversionManifest(ConversionManifest manifest)
    {
        manifest.Files ??= new List<ManifestFile>();
        ValidateManifestAbi(manifest.Abi);
        var formatVersion = manifest.EffectiveDtrFormatVersion;
        if (formatVersion == 0)
            return;

        var minVersion = (int)BotControllerNative.MinRecFormatVersion;
        var maxVersion = (int)BotControllerNative.RecFormatVersion;
        if (formatVersion < minVersion || formatVersion > maxVersion)
        {
            throw new InvalidDataException(
                $"manifest format_version {formatVersion} unsupported; expected {minVersion}..{maxVersion}");
        }
    }

    private static void ValidateManifestAbi(int abi)
    {
        if (abi == 0)
            return;

        if (abi < MinManifestAbiVersion || abi > BotControllerNative.ExpectedAbiVersion)
        {
            throw new InvalidDataException(
                $"manifest abi {abi} unsupported; expected {MinManifestAbiVersion}..{BotControllerNative.ExpectedAbiVersion}");
        }
    }

    private static bool ManifestContainsSourceRound(
        string manifestPath,
        int sourceRound,
        out string error)
    {
        error = string.Empty;
        if (!TryReadManifest(manifestPath, out var manifest, out var readError))
        {
            error = $"[DTR ERR] failed to read manifest: {readError}";
            return false;
        }

        var rounds = manifest.Files
            .Select(file => file.Round)
            .Distinct()
            .Order()
            .ToArray();
        if (rounds.Contains(sourceRound))
            return true;

        error = $"[DTR ERR] source_round={sourceRound} was not found in manifest. [DTR HINT] Available source rounds: {string.Join(", ", rounds)}.";
        return false;
    }

    private static bool TryReadNadeManifest(
        string manifestPath,
        out NadeManifest manifest,
        out string error)
    {
        manifest = new NadeManifest();
        error = string.Empty;
        try
        {
            manifest = ReadNadeManifestCached(manifestPath).Manifest;
            return true;
        }
        catch (FileNotFoundException)
        {
            error = $"file not found: {manifestPath}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryFindNadeClip(
        string manifestPath,
        string clipId,
        out NadeManifest manifest,
        out NadeClip? clip,
        out string error)
    {
        manifest = new NadeManifest();
        clip = null;
        error = string.Empty;
        try
        {
            var cached = ReadNadeManifestCached(manifestPath);
            manifest = cached.Manifest;
            cached.ClipsById.TryGetValue(clipId, out clip);
            return true;
        }
        catch (FileNotFoundException)
        {
            error = $"file not found: {manifestPath}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static CachedNadeManifest ReadNadeManifestCached(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
            throw new FileNotFoundException("nade manifest file not found", manifestPath);

        lock (NadeManifestCacheLock)
        {
            if (NadeManifestCache.TryGetValue(fullPath, out var cached) &&
                cached.LastWriteUtc == file.LastWriteTimeUtc &&
                cached.Length == file.Length)
            {
                return cached;
            }

            var manifest = JsonSerializer.Deserialize<NadeManifest>(
                               ReadMaybeBrotliText(fullPath),
                               ManifestJsonOptions)
                           ?? throw new InvalidOperationException("nade manifest JSON is empty");
            var clipsById = new Dictionary<string, NadeClip>(manifest.Clips.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var clip in manifest.Clips)
            {
                if (!string.IsNullOrWhiteSpace(clip.ClipId))
                    clipsById[clip.ClipId] = clip;
            }

            cached = new CachedNadeManifest(manifest, clipsById, file.LastWriteTimeUtc, file.Length);
            NadeManifestCache[fullPath] = cached;
            return cached;
        }
    }

    private static string ReadMaybeBrotliText(string path)
    {
        if (!path.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path);

        using var input = File.OpenRead(path);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        return reader.ReadToEnd();
    }

    private static string ResolveManifestPath(string manifestPath, string childPath)
    {
        if (Path.IsPathRooted(childPath))
            return childPath;
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        return Path.GetFullPath(Path.Combine(manifestDir, childPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool NadeKindMatchesFilter(NadeClip clip, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) ||
            filter.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            filter.Equals("*", StringComparison.OrdinalIgnoreCase))
            return true;

        var needle = filter.Trim();
        return clip.Kind.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
               clip.GrenadeType.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               clip.WeaponDefIndex.ToString(CultureInfo.InvariantCulture).Equals(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseNadeCycleArgs(
        CommandInfo command,
        int startArg,
        string commandName,
        out string sideFilter,
        out string phaseFilter,
        out float gapSeconds,
        out string error)
    {
        sideFilter = "all";
        phaseFilter = "all";
        gapSeconds = NadeCycleDefaultGapSeconds;
        error = string.Empty;

        for (var i = startArg; i < command.ArgCount; i++)
        {
            var arg = command.GetArg(i).Trim();
            var lower = arg.ToLowerInvariant();
            if (lower is "all" or "*")
            {
                continue;
            }
            if (lower is "both")
            {
                sideFilter = "all";
            }
            else if (lower is "t" or "terrorist" or "terrorists")
            {
                sideFilter = "t";
            }
            else if (lower is "ct" or "counterterrorist" or "counterterrorists")
            {
                sideFilter = "ct";
            }
            else if (lower is "combat" or "retake")
            {
                phaseFilter = lower;
            }
            else if (float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedGap) &&
                     parsedGap >= 0.0f &&
                     parsedGap <= NadeCycleMaxGapSeconds)
            {
                gapSeconds = parsedGap;
            }
            else
            {
                error = $"usage: {commandName} <nade_manifest.json> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]";
                return false;
            }
        }

        return true;
    }

    private static bool NadeCycleSideMatches(NadeClip clip, string sideFilter)
        => sideFilter.Equals("all", StringComparison.OrdinalIgnoreCase) ||
           clip.Side.Equals(sideFilter, StringComparison.OrdinalIgnoreCase);

    private static bool NadeCyclePhaseMatches(NadeClip clip, string phaseFilter)
        => phaseFilter.Equals("all", StringComparison.OrdinalIgnoreCase) ||
           clip.Phase.Equals(phaseFilter, StringComparison.OrdinalIgnoreCase);

    private static bool NadeCycleKindMatches(NadeClip clip, string kindFilter)
    {
        if (!NadeCycleIsRandom(kindFilter))
            return clip.Kind.Equals(kindFilter, StringComparison.OrdinalIgnoreCase);

        return clip.Kind.Equals("smoke", StringComparison.OrdinalIgnoreCase) ||
               clip.Kind.Equals("flash", StringComparison.OrdinalIgnoreCase) ||
               clip.Kind.Equals("he", StringComparison.OrdinalIgnoreCase) ||
               clip.Kind.Equals("molotov", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NadeCycleIsRandom(string kindFilter)
        => kindFilter.Equals("random", StringComparison.OrdinalIgnoreCase);

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
            ValidateRoundPoolManifest(manifest);
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

    private static void ValidateRoundPoolManifest(RoundPoolManifest manifest)
    {
        manifest.Candidates ??= new List<RoundPoolCandidate>();
        ValidateManifestAbi(manifest.Abi);
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

    private sealed class DemoTracerApiFacade : IDemoTracerApi
    {
        private readonly DemoTracerPlugin _plugin;

        public DemoTracerApiFacade(DemoTracerPlugin plugin)
        {
            _plugin = plugin;
        }

        public int ApiVersion => 2;

        public bool TryLoadNadeManifest(
            string manifestPath,
            out DemoTracerNadeManifest manifest,
            out string error)
        {
            manifest = new DemoTracerNadeManifest();
            if (!TryReadNadeManifest(manifestPath, out var internalManifest, out error))
                return false;

            manifest = ToApiManifest(internalManifest);
            return true;
        }

        public bool TryRunNadeClip(
            string manifestPath,
            string clipId,
            int slot,
            bool loop,
            out DemoTracerNadeRunResult result)
        {
            var run = _plugin.RunNadeClip(manifestPath, clipId, slot, loop, quiet: true);
            result = new DemoTracerNadeRunResult
            {
                Queued = run.Ok,
                Slot = slot,
                ClipId = clipId,
                Message = run.Message
            };
            return run.Ok;
        }

        public bool TryRunNadeClipDirect(
            string clipBasePath,
            DemoTracerNadeClip clip,
            int slot,
            bool loop,
            out DemoTracerNadeRunResult result)
        {
            var internalClip = FromApiClip(clip);
            var baseManifestPath = Path.Combine(
                string.IsNullOrWhiteSpace(clipBasePath) ? "." : clipBasePath,
                "__direct_nade_clip__.json");
            var run = _plugin.RunNadeClip(baseManifestPath, internalClip, slot, loop, quiet: true);
            result = new DemoTracerNadeRunResult
            {
                Queued = run.Ok,
                Slot = slot,
                ClipId = clip.ClipId,
                DurationSeconds = clip.DurationSeconds,
                Message = run.Message
            };
            return run.Ok;
        }

        public bool IsSlotBusy(int slot)
            => _plugin.IsReplaySlotBusy(slot);

        private static DemoTracerNadeManifest ToApiManifest(NadeManifest manifest)
        {
            var clips = new List<DemoTracerNadeClip>(manifest.Clips.Count);
            foreach (var clip in manifest.Clips)
                clips.Add(ToApiClip(clip));

            return new DemoTracerNadeManifest
            {
                FormatVersion = manifest.FormatVersion,
                Map = manifest.Map,
                CoordinateMode = manifest.CoordinateMode,
                TickRate = manifest.TickRate,
                Clips = clips
            };
        }

        private static DemoTracerNadeClip ToApiClip(NadeClip clip)
            => new()
            {
                ClipId = clip.ClipId,
                Path = clip.Path,
                Kind = clip.Kind,
                GrenadeType = clip.GrenadeType,
                WeaponDefIndex = clip.WeaponDefIndex,
                FirstWeaponDefIndex = clip.FirstWeaponDefIndex,
                Phase = clip.Phase,
                Round = clip.Round,
                Side = clip.Side,
                SteamId = clip.SteamId,
                PlayerName = clip.PlayerName,
                ThrowTick = clip.ThrowTick,
                StartOrigin = ToApiVector(clip.StartOrigin),
                StartYaw = clip.StartYaw,
                ProjectileInitialVelocity = ToApiVector(clip.ProjectileInitialVelocity),
                ProjectileDetonationPosition = ToApiVector(clip.ProjectileDetonationPosition),
                DurationSeconds = clip.DurationSeconds
            };

        private static NadeClip FromApiClip(DemoTracerNadeClip clip)
            => new()
            {
                ClipId = clip.ClipId,
                Path = clip.Path,
                Kind = clip.Kind,
                GrenadeType = clip.GrenadeType,
                WeaponDefIndex = clip.WeaponDefIndex,
                FirstWeaponDefIndex = clip.FirstWeaponDefIndex != 0 ? clip.FirstWeaponDefIndex : clip.WeaponDefIndex,
                Phase = clip.Phase,
                Round = clip.Round,
                Side = clip.Side,
                SteamId = clip.SteamId,
                PlayerName = clip.PlayerName,
                ThrowTick = clip.ThrowTick,
                StartOrigin = FromApiVector(clip.StartOrigin),
                StartYaw = clip.StartYaw,
                ProjectileInitialVelocity = FromApiVector(clip.ProjectileInitialVelocity),
                ProjectileDetonationPosition = FromApiVector(clip.ProjectileDetonationPosition),
                DurationSeconds = clip.DurationSeconds,
                PreloadWeaponDefIndices = clip.WeaponDefIndex != 0 ? [clip.WeaponDefIndex] : null
            };

        private static DemoTracerVector3 ToApiVector(float[]? values)
        {
            if (values == null || values.Length < 3)
                return new DemoTracerVector3();
            return new DemoTracerVector3
            {
                X = values[0],
                Y = values[1],
                Z = values[2]
            };
        }

        private static float[] FromApiVector(DemoTracerVector3? value)
            => value == null ? [0f, 0f, 0f] : [value.X, value.Y, value.Z];
    }

    private readonly record struct LoadedReplay(
        string Path,
        string PlayerName,
        ulong SteamId,
        int FirstWeaponDefIndex,
        int[] PreloadWeaponDefIndices,
        bool HasLoadout,
        ReplayLoadoutSnapshot Loadout,
        ReplayProjectileEvent[] Projectiles,
        bool UtilityOnly,
        int UtilityWeaponDefIndex,
        float TickRate,
        uint PlayStartTickIndex);

    private readonly record struct ReplayAssignment(ManifestFile File, CCSPlayerController Bot);

    private readonly record struct PendingWeaponAlign(int WeaponDefIndex, bool ForceSwitch);

    private readonly record struct PendingBulletHit(int AttackerSlot, float Time);

    private readonly record struct PendingBulletDamage(int AttackerSlot, int Damage, float Time);

    private readonly record struct PendingThreat360(int EnemySlot, float FirstSeenAt);

    private readonly record struct TeamEconomySnapshot(uint EquipmentValue, string Class);

    private sealed class NadeCycleState(
        int token,
        string manifestPath,
        List<NadeClip> clips,
        int slot,
        string kindFilter,
        string sideFilter,
        string phaseFilter,
        float gapSeconds)
    {
        public int Token { get; } = token;
        public string ManifestPath { get; } = manifestPath;
        public List<NadeClip> Clips { get; } = clips;
        public int Slot { get; } = slot;
        public string KindFilter { get; } = kindFilter;
        public string SideFilter { get; } = sideFilter;
        public string PhaseFilter { get; } = phaseFilter;
        public float GapSeconds { get; } = gapSeconds;
        public int Index { get; set; }
        public bool Waiting { get; set; }
    }

    private sealed class PendingProjectileAlign(
        uint index,
        IntPtr handle,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        public uint Index { get; } = index;
        public IntPtr Handle { get; } = handle;
        public ReplayProjectileKind Kind { get; } = kind;
        public int WeaponDefIndex { get; } = weaponDefIndex;
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

    private sealed class RayTraceLosProbe
    {
        private const string CapabilityName = "raytrace:craytraceinterface";
        private const string ApiAssemblyName = "RayTraceApi";
        private const string RayTraceInterfaceTypeName = "RayTraceAPI.CRayTraceInterface";
        private const string TraceOptionsTypeName = "RayTraceAPI.TraceOptions";
        private const string TraceResultTypeName = "RayTraceAPI.TraceResult";
        private const string InteractionLayersTypeName = "RayTraceAPI.InteractionLayers";

        private bool _initialized;
        private object? _capability;
        private object? _traceOptions;
        private MethodInfo? _getMethod;
        private MethodInfo? _traceEndShapeMethod;
        private FieldInfo? _fractionField;
        private string _status = "unresolved";
        private DateTime _nextInitAttemptAt = DateTime.MinValue;

        public string Status
        {
            get
            {
                EnsureInitialized();
                return _status;
            }
        }

        public string ProbeStatus
        {
            get
            {
                _ = TryGetRayTrace(out _);
                return _status;
            }
        }

        public bool TryIsWorldLineClear(Vector start, Vector end, out bool clear)
        {
            clear = false;
            if (!TryGetRayTrace(out var rayTrace))
                return false;

            try
            {
                var args = new object?[] { start, end, null, _traceOptions, null };
                var hit = _traceEndShapeMethod!.Invoke(rayTrace, args) is true;
                if (!hit)
                {
                    clear = true;
                    _status = "available";
                    return true;
                }

                var result = args[4];
                if (result == null)
                {
                    _status = "bad_result";
                    return false;
                }

                var fraction = Convert.ToSingle(_fractionField!.GetValue(result), CultureInfo.InvariantCulture);
                clear = fraction >= 0.999f;
                _status = "available";
                return true;
            }
            catch
            {
                _status = "invoke_error";
                return false;
            }
        }

        private bool TryGetRayTrace(out object rayTrace)
        {
            rayTrace = null!;
            EnsureInitialized();
            if (_capability == null || _getMethod == null)
                return false;

            try
            {
                var value = _getMethod.Invoke(_capability, null);
                if (value == null)
                {
                    _status = "no_provider";
                    return false;
                }

                rayTrace = value;
                return true;
            }
            catch
            {
                _status = "get_error";
                return false;
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            if (DateTime.UtcNow < _nextInitAttemptAt)
                return;
            _initialized = true;

            try
            {
                var interfaceType = ResolveRayTraceType(RayTraceInterfaceTypeName);
                var optionsType = ResolveRayTraceType(TraceOptionsTypeName);
                var resultType = ResolveRayTraceType(TraceResultTypeName);
                var layersType = ResolveRayTraceType(InteractionLayersTypeName);
                if (interfaceType == null || optionsType == null || resultType == null || layersType == null)
                {
                    _status = "api_missing";
                    RetryInitializeLater();
                    return;
                }

                var capabilityType = typeof(PluginCapability<>).MakeGenericType(interfaceType);
                _capability = Activator.CreateInstance(capabilityType, CapabilityName);
                _getMethod = capabilityType.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance);
                _traceEndShapeMethod = interfaceType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "TraceEndShape" && method.GetParameters().Length == 5);
                _fractionField = resultType.GetField("Fraction", BindingFlags.Public | BindingFlags.Instance);
                _traceOptions = Activator.CreateInstance(optionsType);
                var interactsWith = optionsType.GetField("InteractsWith", BindingFlags.Public | BindingFlags.Instance);
                if (_traceOptions != null && interactsWith != null)
                {
                    var worldOnly = Enum.Parse(layersType, "MASK_WORLD_ONLY");
                    interactsWith.SetValue(_traceOptions, Convert.ToUInt64(worldOnly, CultureInfo.InvariantCulture));
                }

                if (_capability == null ||
                    _getMethod == null ||
                    _traceEndShapeMethod == null ||
                    _fractionField == null ||
                    _traceOptions == null)
                {
                    _status = "api_incomplete";
                    return;
                }

                _status = "ready";
            }
            catch
            {
                _status = "init_error";
                RetryInitializeLater();
            }
        }

        private void RetryInitializeLater()
        {
            _initialized = false;
            _nextInitAttemptAt = DateTime.UtcNow.AddSeconds(1.0);
        }

        private static Type? ResolveRayTraceType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var existing = assembly.GetType(fullName, throwOnError: false);
                if (existing != null)
                    return existing;
            }

            try
            {
                return Assembly.Load(new AssemblyName(ApiAssemblyName)).GetType(fullName, throwOnError: false);
            }
            catch
            {
                return Type.GetType($"{fullName}, {ApiAssemblyName}", throwOnError: false);
            }
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
        [JsonPropertyName("format_version")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("dtr_format_version")]
        public int DtrFormatVersion { get; set; }

        [JsonPropertyName("abi")]
        public int Abi { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public List<ManifestFile> Files { get; set; } = new();

        public int EffectiveDtrFormatVersion => DtrFormatVersion != 0 ? DtrFormatVersion : FormatVersion;
    }

    private sealed class NadeManifest
    {
        [JsonPropertyName("format_version")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; } = string.Empty;

        [JsonPropertyName("coordinate_mode")]
        public string CoordinateMode { get; set; } = string.Empty;

        [JsonPropertyName("tickrate")]
        public float TickRate { get; set; }

        [JsonPropertyName("tick_rate")]
        public float TickRateAlt
        {
            get => TickRate;
            set => TickRate = value;
        }

        [JsonPropertyName("clips")]
        public List<NadeClip> Clips { get; set; } = new();
    }

    private sealed record CachedNadeManifest(
        NadeManifest Manifest,
        Dictionary<string, NadeClip> ClipsById,
        DateTime LastWriteUtc,
        long Length);

    private sealed class NadeClip
    {
        [JsonPropertyName("clip_id")]
        public string ClipId { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("grenade_type")]
        public string GrenadeType { get; set; } = string.Empty;

        [JsonPropertyName("weapon_def_index")]
        public int WeaponDefIndex { get; set; }

        [JsonPropertyName("phase")]
        public string Phase { get; set; } = string.Empty;

        [JsonPropertyName("round")]
        public int Round { get; set; }

        [JsonPropertyName("side")]
        public string Side { get; set; } = string.Empty;

        [JsonPropertyName("start_origin")]
        public float[]? StartOrigin { get; set; }

        [JsonPropertyName("start_yaw")]
        public float StartYaw { get; set; }

        [JsonPropertyName("projectile_initial_velocity")]
        public float[]? ProjectileInitialVelocity { get; set; }

        [JsonPropertyName("projectile_detonation_position")]
        public float[]? ProjectileDetonationPosition { get; set; }

        [JsonPropertyName("duration_seconds")]
        public float DurationSeconds { get; set; }

        [JsonPropertyName("steam_id")]
        public ulong SteamId { get; set; }

        [JsonPropertyName("player_name")]
        public string PlayerName { get; set; } = string.Empty;

        [JsonPropertyName("throw_tick")]
        public int ThrowTick { get; set; }

        [JsonPropertyName("first_weapon_def_index")]
        public int FirstWeaponDefIndex { get; set; }

        [JsonPropertyName("preload_weapon_def_indices")]
        public int[]? PreloadWeaponDefIndices { get; set; }

        [JsonPropertyName("loadout")]
        public ReplayLoadoutSnapshot? Loadout { get; set; }
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
