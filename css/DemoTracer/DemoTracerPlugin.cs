using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using DemoTracerApi;
using System.Globalization;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin : BasePlugin
{
    public override string ModuleName => "CS2 DemoTracer";
    public override string ModuleVersion => "0.1.4";
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
    private const int MaxManifestAbiVersion = 17;
    private const int MaxPlayerSlots = BotControllerNative.MaxSlots;
    private const int ReplayStartHealth = 100;
    private const string FreezeTimeConVarName = "mp_freezetime";
    private const string CosmeticRiskNotice = "[DTR WARN] cosmetic alignment consumes opt-in manifest cosmetics evidence and may carry Valve GSLT/server-guideline risk outside local/private replay validation.";

    private readonly List<int> _loadedSlots = new();
    private readonly Dictionary<int, LoadedReplay> _loadedReplays = new();
    private readonly Dictionary<int, int> _lastEnsuredWeaponDef = new();
    private readonly Dictionary<int, int> _lastReplayWeaponDef = new();
    private readonly Dictionary<int, int> _lastLockedWeaponTarget = new();
    private readonly Dictionary<int, PendingWeaponAlign> _pendingWeaponAlign = new();
    private readonly Dictionary<int, int> _projectileAlignNextBySlot = new();
    private readonly Dictionary<int, int> _replayHifiEventNextBySlot = new();
    private readonly Dictionary<uint, PendingProjectileAlign> _pendingProjectileAlign = new();
    private readonly List<TrackedDroppedReplayItem> _trackedDroppedReplayItems = new();
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
    private readonly HashSet<int> _cosmeticSyncedSlots = new();
    private readonly HashSet<int> _scoreboardSyncedSlots = new();
    private readonly Dictionary<int, string?> _viewerOriginalCrosshairCodes = new();
    private readonly Dictionary<int, string> _viewerAppliedCrosshairCodes = new();
    private readonly BotHiderMemoryProbe _botHiderProbe = new();
    private readonly RayTraceLosProbe _rayTraceLosProbe = new();
    private bool _safeC4Aligned;
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
    private ReplayRoundScoreboard? _loadedRoundScoreboard;
    private bool _weaponAlignEnabled = true;
    private bool _projectileAlignEnabled = true;
    private bool _cosmeticAlignEnabled;
    private bool _stickerAlignEnabled;
    private bool _crosshairAlignEnabled = true;
    private bool _scoreboardAlignEnabled;
    private bool _weaponAlignFrameQueued;
    private int _cosmeticAppliedCount;
    private int _cosmeticSkippedCount;
    private int _stickerAppliedCount;
    private int _stickerSkippedCount;
    private int _scoreboardAppliedCount;
    private int _scoreboardSkippedCount;
    private HandoffMode _handoffMode = HandoffMode.DeathOrContact;
    private bool _handoffAllSlots;
    private bool _handoffThreat360Enabled = true;
    private float _handoffThreat360Range = HandoffThreat360DefaultRange;
    private bool _handoffThreat360LosEnabled = true;
    private bool _partialReplayEnabled = true;
    private ReplayIdentityMode _replayIdentityMode = ReplayIdentityMode.Full;
    private int _nextNadeStartToken;
    private NadeCycleState? _nadeCycle;
    private int _nextNadeCycleToken;

    public override void Load(bool hotReload)
    {
        LoadCosmeticLegacyPaints();
        HookCosmeticGiveNamedItem();
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        Capabilities.RegisterPluginCapability(ApiCapability, () => (IDemoTracerApi)_apiFacade);
        ConfigureNativeSafetyOffsets();
        Server.PrintToConsole("dtr: CSS control plugin loaded");
    }

    public override void Unload(bool hotReload)
    {
        UnhookCosmeticGiveNamedItem();
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

    [ConsoleCommand("dtr_cosmetic_align", "dtr_cosmetic_align <0|1>")]
    public void CosmeticAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _cosmeticAlignEnabled = ParseOnOff(command.GetArg(1), _cosmeticAlignEnabled);
        if (!_cosmeticAlignEnabled)
        {
            ResetCosmeticAlignState();
            ResetStickerAlignState();
        }

        command.ReplyToCommand($"dtr: cosmetic_align={_cosmeticAlignEnabled}");
        if (_cosmeticAlignEnabled)
            command.ReplyToCommand(CosmeticRiskNotice);
    }

    [ConsoleCommand("dtr_sticker_align", "dtr_sticker_align <0|1>")]
    public void StickerAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _stickerAlignEnabled = ParseOnOff(command.GetArg(1), _stickerAlignEnabled);
        if (!_stickerAlignEnabled)
            ResetStickerAlignState();

        command.ReplyToCommand($"dtr: sticker_align={_stickerAlignEnabled}");
        if (_stickerAlignEnabled)
            command.ReplyToCommand(CosmeticRiskNotice);
    }

    [ConsoleCommand("dtr_crosshair_align", "dtr_crosshair_align <0|1>")]
    public void CrosshairAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _crosshairAlignEnabled = ParseOnOff(command.GetArg(1), _crosshairAlignEnabled);
        if (!_crosshairAlignEnabled)
            ResetCrosshairAlignState();

        command.ReplyToCommand($"dtr: crosshair_align={_crosshairAlignEnabled}");
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
            command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles|cosmetics|stickers|crosshair|scoreboard> <off|on>");
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
            case "cosmetics":
            case "cosmetic":
            case "skins":
            case "skin":
                _cosmeticAlignEnabled = enabled;
                if (!_cosmeticAlignEnabled)
                {
                    ResetCosmeticAlignState();
                    ResetStickerAlignState();
                }
                command.ReplyToCommand($"[DTR OK] align cosmetics={FormatOnOff(_cosmeticAlignEnabled)}");
                if (_cosmeticAlignEnabled)
                    command.ReplyToCommand(CosmeticRiskNotice);
                return;
            case "stickers":
            case "sticker":
                _stickerAlignEnabled = enabled;
                if (!_stickerAlignEnabled)
                    ResetStickerAlignState();
                command.ReplyToCommand($"[DTR OK] align stickers={FormatOnOff(_stickerAlignEnabled)}");
                if (_stickerAlignEnabled)
                    command.ReplyToCommand(CosmeticRiskNotice);
                return;
            case "crosshair":
            case "crosshairs":
            case "view":
                _crosshairAlignEnabled = enabled;
                if (!_crosshairAlignEnabled)
                    ResetCrosshairAlignState();
                command.ReplyToCommand($"[DTR OK] align crosshair={FormatOnOff(_crosshairAlignEnabled)}");
                return;
            case "scoreboard":
            case "scoreboards":
            case "scores":
            case "stats":
                _scoreboardAlignEnabled = enabled;
                if (!_scoreboardAlignEnabled)
                    ResetScoreboardAlignState();
                command.ReplyToCommand($"[DTR OK] align scoreboard={FormatOnOff(_scoreboardAlignEnabled)}");
                return;
            default:
                command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles|cosmetics|stickers|crosshair|scoreboard> <off|on>");
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
            command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles|cosmetics|stickers|crosshair|scoreboard> <off|on>");
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
                $"[DTR OK] status plan={plan} loaded_slots={_loadedSlots.Count} settings identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} cosmetics={FormatOnOff(_cosmeticAlignEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} crosshair={FormatOnOff(_crosshairAlignEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} allow_partial={FormatOnOff(_partialReplayEnabled)} mp_freezetime={(float.IsFinite(freezeTime) ? freezeTime.ToString("F2", CultureInfo.InvariantCulture) : "unknown")} {(string.IsNullOrEmpty(freezeReason) ? "" : freezeReason)} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatScoreboardStatusCounts()}");
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
            $"dtr: abi={BotControllerNative.AbiVersion} slot={slot} playing={state.Playing} cursor={state.Cursor} total={state.Total} handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")} handoff_360={_handoffThreat360Enabled}:{_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={_handoffThreat360LosEnabled}:{_rayTraceLosProbe.ProbeStatus} partial={_partialReplayEnabled} identity={ReplayIdentityModeName()} projectile_align={_projectileAlignEnabled} cosmetic_align={_cosmeticAlignEnabled} sticker_align={_stickerAlignEnabled} crosshair_align={_crosshairAlignEnabled} scoreboard_align={_scoreboardAlignEnabled}{sequence}{pool}");
    }

    [ConsoleCommand("dtr_runtime", "dtr_runtime")]
    public void RuntimeCommand(CCSPlayerController? player, CommandInfo command)
    {
        command.ReplyToCommand(
            $"[DTR OK] DemoTracer {BotControllerNative.RuntimeSummary}");
    }

    [ConsoleCommand("dtr_doctor", "dtr_doctor [manifest.json|pool_manifest.json]")]
    public void DoctorCommand(CCSPlayerController? player, CommandInfo command)
    {
        TryReadFreezeTimeConVar(out var freezeTime, out var freezeReason);
        var players = FindTeamPlayers();
        var tPlayers = players.Count(candidate => candidate.Team == CsTeam.Terrorist);
        var ctPlayers = players.Count(candidate => candidate.Team == CsTeam.CounterTerrorist);
        var strictBots = players.Count(candidate => candidate.IsBot);
        var managedBots = players.Count(candidate => _botHiderProbe.IsManagedBot(candidate.Slot));
        var replayTargets = FindReplayTargets();
        var loadedPlaying = _loadedSlots.Count(slot => BotControllerNative.GetReplayState(slot).Playing);

        command.ReplyToCommand(
            $"[DTR DOCTOR] runtime {BotControllerNative.RuntimeSummary}");
        command.ReplyToCommand(
            $"[DTR DOCTOR] server map={CurrentMapName()} time={Server.CurrentTime.ToString("F2", CultureInfo.InvariantCulture)} mp_freezetime={(float.IsFinite(freezeTime) ? freezeTime.ToString("F2", CultureInfo.InvariantCulture) : "unknown")} {(string.IsNullOrEmpty(freezeReason) ? "" : freezeReason)}");
        command.ReplyToCommand(
            $"[DTR DOCTOR] bots players T={tPlayers}/CT={ctPlayers} strict_bots={strictBots} bot_hider_managed={managedBots} safe_replay_targets={replayTargets.Count}");
        command.ReplyToCommand(
            $"[DTR DOCTOR] replay loaded={_loadedSlots.Count} playing={loadedPlaying} identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} cosmetics={FormatOnOff(_cosmeticAlignEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} crosshair={FormatOnOff(_crosshairAlignEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} partial={FormatOnOff(_partialReplayEnabled)} raytrace={_rayTraceLosProbe.ProbeStatus} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatScoreboardStatusCounts()}");

        if (command.ArgCount >= 2)
            ReplyDoctorManifest(command, command.GetArg(1));
    }

    private static void ReplyDoctorManifest(CommandInfo command, string manifestPath)
    {
        if (TryReadManifest(manifestPath, out var manifest, out var readError))
        {
            var rounds = manifest.Files
                .Select(file => file.Round)
                .Distinct()
                .Order()
                .ToArray();
            command.ReplyToCommand(
                $"[DTR DOCTOR] manifest type=round path=\"{manifestPath}\" map={manifest.Map} abi={manifest.Abi} dtr_format={manifest.EffectiveDtrFormatVersion} files={manifest.Files.Count} avatar_overrides={manifest.AvatarOverrides.Count} rounds={FormatRoundList(rounds)}");
            return;
        }

        if (TryReadPoolManifest(manifestPath, out var pool, out var poolError))
        {
            command.ReplyToCommand(
                $"[DTR DOCTOR] manifest type=pool path=\"{manifestPath}\" map={pool.Map} abi={pool.Abi} format={pool.FormatVersion} candidates={pool.Candidates.Count}");
            return;
        }

        command.ReplyToCommand(
            $"[DTR DOCTOR] manifest path=\"{manifestPath}\" read_failed round=\"{readError}\" pool=\"{poolError}\"");
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (StopReplayStateForRoundBoundary("round_start"))
            Server.PrintToConsole("[DTR WARN] round_start stopped stale DTR replay state");

        if ((_sequenceActive || _armed) && IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            StopAllState("warmup_block");
            return HookResult.Continue;
        }

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

        if ((_sequenceActive || _poolActive || _armed) && IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            StopAllState("warmup_block");
            return HookResult.Continue;
        }

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
            var message = StartLoaded(loop, ReplayStartAnchor.Live, null);
            Server.PrintToConsole($"dtr: auto-start {label}: {message}");
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } victim &&
            IsReplaySlotPlaying(victim.Slot))
        {
            BotControllerNative.StopReplay(victim.Slot);
            ReleaseReplaySlot(victim.Slot, "replay_target_death");
            if (IsNadeCycleSlot(victim.Slot))
                StopNadeCycle("replay_target_death", stopCurrent: false);
        }

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
            RestoreAllReplayViewerCrosshairs();
            return;
        }

        var playerSnapshot = BuildTickPlayerSnapshot();
        UpdateReplayPovMask(playerSnapshot);
        UpdateReplayViewerCrosshairs(playerSnapshot);

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
            if (playerSnapshot.TryGetSlot(slot, out var replayPlayer) &&
                replayPlayer is { IsValid: true, PawnIsAlive: false })
            {
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "dead_replay_target");
                if (IsNadeCycleSlot(slot))
                    StopNadeCycle("dead_replay_target", stopCurrent: false);
                continue;
            }

            if (!_lastPlayingSlots.Contains(slot))
                MarkReplayStarted(slot);

            var hasLoadedReplay = _loadedReplays.TryGetValue(slot, out var replay);
            if (hasLoadedReplay)
                ProcessReplayHifiEvents(slot, replay, state.Cursor);

            if (HandoffIncludesContact(_handoffMode) && ReplayHasPassedHandoffGrace(slot) &&
                ReplayBotHasContact(slot, playerSnapshot, out var contactReason, out _))
            {
                HandoffActiveReplays($"enemy_contact_{contactReason}_slot{slot}", slot);
                continue;
            }

            if (!_weaponAlignEnabled)
                continue;

            if (hasLoadedReplay && replay.UtilityOnly)
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

            ApplyReplayWeaponPreset(slot, weaponDefIndex, allowSlotReplacement: false, force: false);
        }
    }

    private void ProcessReplayHifiEvents(int slot, LoadedReplay replay, int cursor)
    {
        if (cursor < 0 || replay.HifiEvents.Length == 0)
            return;

        var next = _replayHifiEventNextBySlot.GetValueOrDefault(slot);
        while (next < replay.HifiEvents.Length && replay.HifiEvents[next].TickIndex <= (uint)cursor)
        {
            ExecuteReplayHifiEvent(slot, replay, replay.HifiEvents[next]);
            next++;
        }
        _replayHifiEventNextBySlot[slot] = next;
    }

    private void ExecuteReplayHifiEvent(int slot, LoadedReplay replay, ReplayHifiEvent replayEvent)
    {
        var kind = replayEvent.Kind.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "item_drop":
                // Live replay ticks must not mutate inventory/entities. Keep item events
                // as metadata until replay-safe transfer machinery exists.
                break;

            case "bomb_drop":
                // C4 is a unique objective entity. Mid-replay DropActiveWeapon on C4 can
                // leave CS2 in an invalid bomb state, so runtime C4 transfer stays record-only.
                break;

            case "item_pickup":
                // Record-only for stability. Unpaired pickups may represent world state we cannot
                // safely prove yet.
                break;

            case "item_transfer":
                if (!ReplayEventBelongsToSlot(replayEvent.TargetSteamId, replay.SteamId))
                    return;
                QueueReplayUtilityTransferGrant(slot, replayEvent);
                break;

            case "bomb_pickup":
                // Safe C4 ownership is aligned before replay start. Do not clone or move C4
                // during live replay ticks.
                break;
        }
    }

    private static bool ReplayEventBelongsToSlot(ulong? eventSteamId, ulong replaySteamId)
        => !eventSteamId.HasValue || replaySteamId == 0 || eventSteamId.Value == replaySteamId;

    private static bool ShouldExecuteReplayEquipmentEvent(int weaponDefIndex, bool isBomb)
        => isBomb || IsUtilityWeaponDefIndex(weaponDefIndex);

    private void QueueReplayUtilityTransferGrant(int slot, ReplayHifiEvent replayEvent)
    {
        var weaponDefIndex = ReplayEventWeaponDefIndex(replayEvent);
        if (!IsUtilityWeaponDefIndex(weaponDefIndex) ||
            !TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return;

        var targetCount = Math.Max(1, replayEvent.TargetCountAfter ?? 1);
        Server.NextFrame(() => EnsureReplayUtilityTransferGrant(slot, className, targetCount, replayEvent.Tick));
    }

    private void EnsureReplayUtilityTransferGrant(int slot, string className, int targetCount, int sourceTick)
    {
        if (!IsReplaySlotStillSafe(slot))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

        var currentCount = CountCurrentReplayItems(player, className);
        if (currentCount >= targetCount)
            return;

        var missing = targetCount - currentCount;
        for (var i = 0; i < missing; i++)
        {
            if (!TryGiveNamedItem(player, className))
            {
                Server.PrintToConsole(
                    $"dtr: hifi transfer grant failed slot={slot} item={className} tick={sourceTick}");
                return;
            }
        }

        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
    }

    private void DropReplayItemToWorld(int slot, ReplayHifiEvent replayEvent, bool isBomb)
    {
        var weaponDefIndex = isBomb ? 49 : ReplayEventWeaponDefIndex(replayEvent);
        if (weaponDefIndex < 0 ||
            !ShouldExecuteReplayEquipmentEvent(weaponDefIndex, isBomb) ||
            !TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        var pawn = player.PlayerPawn.Value;
        var weapon = GetReplayWeaponsByClass(pawn, className).FirstOrDefault();
        if (weapon == null)
        {
            Server.PrintToConsole($"dtr: hifi drop skipped slot={slot} item={className} tick={replayEvent.Tick}");
            return;
        }
        if (!TrySelectWeapon(player, pawn, weapon))
            return;

        try
        {
            player.DropActiveWeapon();
            _trackedDroppedReplayItems.Add(new TrackedDroppedReplayItem(slot, weaponDefIndex, weapon.Handle));
            _lastEnsuredWeaponDef.Remove(slot);
            _lastReplayWeaponDef.Remove(slot);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: hifi drop failed slot={slot} item={className} tick={replayEvent.Tick}: {ex.Message}");
        }
    }

    private void EnsureReplayEventItem(int slot, ReplayHifiEvent replayEvent, bool isBomb)
    {
        var weaponDefIndex = isBomb ? 49 : ReplayEventWeaponDefIndex(replayEvent);
        if (weaponDefIndex < 0 ||
            !ShouldExecuteReplayEquipmentEvent(weaponDefIndex, isBomb) ||
            !TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return;

        var targetCount = Math.Max(1, replayEvent.TargetCountAfter ?? 1);
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

        var currentCount = CountCurrentReplayItems(player, className);
        if (currentCount >= targetCount)
            return;

        if (isBomb)
        {
            Server.PrintToConsole(
                $"dtr: hifi bomb pickup skipped slot={slot} tick={replayEvent.Tick}: refusing to clone C4");
            return;
        }

        var missing = targetCount - currentCount;
        for (var i = 0; i < missing; i++)
        {
            if (!TryGiveNamedItem(player, className))
            {
                Server.PrintToConsole($"dtr: hifi pickup fallback failed slot={slot} item={className} tick={replayEvent.Tick}");
                return;
            }
            KillOneTrackedDroppedReplayItem(weaponDefIndex);
        }

        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
    }

    private static int ReplayEventWeaponDefIndex(ReplayHifiEvent replayEvent)
    {
        if (replayEvent.WeaponDefIndex.HasValue)
            return NormalizeWeaponDefIndex(replayEvent.WeaponDefIndex.Value);
        if (string.IsNullOrWhiteSpace(replayEvent.ItemName))
            return -1;

        var itemName = NormalizeReplayEventItemName(replayEvent.ItemName);
        return NormalizeWeaponDefIndex(WeaponDefIndex(itemName));
    }

    private static string NormalizeReplayEventItemName(string itemName)
    {
        var normalized = itemName.Trim().ToLowerInvariant() switch
        {
            "decoy_grenade" or "weapon_decoy_grenade" => "weapon_decoy",
            "c4" or "weapon_c4_explosive" => "weapon_c4",
            var value => value
        };
        return normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"weapon_{normalized}";
    }

    private static int CountCurrentReplayItems(CCSPlayerController player, string className)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
            return 0;

        var count = 0;
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, className))
                count++;
        }
        return count;
    }

    private static IEnumerable<CBasePlayerWeapon> GetReplayWeaponsByClass(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
            yield break;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, className))
                yield return weapon;
        }
    }

    private void KillOneTrackedDroppedReplayItem(int weaponDefIndex)
    {
        var index = _trackedDroppedReplayItems.FindIndex(item => item.WeaponDefIndex == weaponDefIndex);
        if (index < 0)
            return;

        var item = _trackedDroppedReplayItems[index];
        _trackedDroppedReplayItems.RemoveAt(index);
        KillTrackedDroppedReplayItem(item, "hifi_pickup_fallback");
    }

    private void KillTrackedReplayDropsForSlot(int slot, string reason)
    {
        for (var i = _trackedDroppedReplayItems.Count - 1; i >= 0; i--)
        {
            var item = _trackedDroppedReplayItems[i];
            if (item.SourceSlot != slot)
                continue;
            _trackedDroppedReplayItems.RemoveAt(i);
            KillTrackedDroppedReplayItem(item, reason);
        }
    }

    private static void KillTrackedDroppedReplayItem(TrackedDroppedReplayItem item, string reason)
    {
        try
        {
            var weapon = new CBasePlayerWeapon(item.Handle);
            if (weapon.IsValid)
                weapon.AcceptInput("Kill");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to kill tracked hifi drop slot={item.SourceSlot} def={item.WeaponDefIndex} reason={reason}: {ex.Message}");
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
        TryApplySpawnedReplayWeaponCosmetic(entity);

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

    private LoadRoundResult RunNadeClip(string manifestPath, string clipId, int slot, bool loop, bool quiet = false)
    {
        try
        {
            if (!TryFindNadeClip(manifestPath, clipId, out var manifest, out var clip, out var readError))
                return LoadRoundResult.Fail($"dtr: failed to read nade manifest: {readError}");
            if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
            {
                return LoadRoundResult.Fail(
                    $"dtr: map mismatch, server=\"{currentMap}\" nade_manifest=\"{manifest.Map}\" path=\"{manifestPath}\"");
            }

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
            ValidateNadeClipFields(manifestPath, clip, string.IsNullOrWhiteSpace(clip.ClipId) ? "<direct>" : clip.ClipId);
            if (!IsReplaySlotStillSafe(slot))
                return LoadRoundResult.Fail($"dtr: refused to run nade on slot {slot}: not a safe bot target");

            var recPath = ResolveNadeClipPath(manifestPath, clip.Path);
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
            if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
            {
                return LoadRoundResult.Fail(
                    $"dtr: map mismatch, server=\"{currentMap}\" nade_manifest=\"{manifest.Map}\" path=\"{manifestPath}\"");
            }
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
            if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
            {
                return LoadRoundResult.Fail(
                    $"dtr: map mismatch, server=\"{currentMap}\" manifest=\"{manifest.Map}\" path=\"{manifestPath}\"");
            }

            var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
            var avatarOverrides = BuildAvatarOverrideMap(manifest.AvatarOverrides);
            var roundFiles = manifest.Files
                .Where(file => file.Round == round)
                .OrderBy(file => file.Side, StringComparer.Ordinal)
                .ThenBy(file => file.SteamId)
                .ToList();
            if (roundFiles.Count == 0)
                return LoadRoundResult.Fail($"dtr: manifest has no files for round {round}");
            var roundScoreboard = manifest.Rounds.FirstOrDefault(item => item.Round == round)?.Scoreboard;

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
            _loadedRoundScoreboard = roundScoreboard;
            var loaded = new List<string>();
            if (!LoadSide(tAssignments, manifestDir, avatarOverrides, loaded, out var loadError))
                return FailLoadRoundAfterPartialLoad(round, loadError);
            if (!LoadSide(ctAssignments, manifestDir, avatarOverrides, loaded, out loadError))
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
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides,
        List<string> loaded,
        out string error)
    {
        error = string.Empty;
        foreach (var assignment in assignments)
        {
            var file = assignment.File;
            var bot = assignment.Bot;
            var slot = bot.Slot;
            if (!IsReplaySlotStillSafe(slot))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} target is no longer a safe bot";
                return false;
            }

            if (!TryResolveChildPathUnderRoot(manifestDir, file.Path, out var recPath, out var pathError))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} {pathError}";
                return false;
            }

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
                file.Cosmetics,
                file.View,
                file.Scoreboard,
                replayMetadata: replayMetadata);
            BotControllerNative.SetBuySkip(slot);
            TryApplyReplayIdentity(slot, file, manifestDir, avatarOverrides);
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

    private static Dictionary<ulong, ManifestAvatarOverride> BuildAvatarOverrideMap(
        IReadOnlyList<ManifestAvatarOverride> avatarOverrides)
    {
        var map = new Dictionary<ulong, ManifestAvatarOverride>();
        foreach (var avatar in avatarOverrides)
        {
            if (avatar.SteamId != 0)
                map.TryAdd(avatar.SteamId, avatar);
        }
        return map;
    }

    private void TryApplyReplayIdentity(
        int slot,
        ManifestFile file,
        string manifestDir,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides)
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
        {
            TryApplyReplayAvatarOverride(slot, file, manifestDir, avatarOverrides);
            Server.ExecuteCommand($"bh_setsid {slot} {file.SteamId}");
        }
        Server.PrintToConsole(
            _replayIdentityMode == ReplayIdentityMode.Full
                ? $"dtr: replay identity queued slot={slot} player={file.PlayerName} sid={file.SteamId}"
                : $"dtr: replay identity queued slot={slot} player={file.PlayerName}");
    }

    private void TryApplyReplayAvatarOverride(
        int slot,
        ManifestFile file,
        string manifestDir,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides)
    {
        if (file.SteamId == 0 ||
            !avatarOverrides.TryGetValue(file.SteamId, out var avatar))
        {
            return;
        }

        var format = avatar.Format.Trim();
        var pngFormat =
            (format.Length == 0 && avatar.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ||
            format.Equals("png", StringComparison.OrdinalIgnoreCase);
        if (!pngFormat)
        {
            Server.PrintToConsole(
                $"dtr: replay avatar skipped slot={slot} player={file.PlayerName} sid={file.SteamId}: unsupported format={avatar.Format}");
            return;
        }

        if (!TryResolveChildPathUnderRoot(manifestDir, avatar.Path, out var avatarPath, out var pathError))
        {
            Server.PrintToConsole(
                $"dtr: replay avatar skipped slot={slot} player={file.PlayerName} sid={file.SteamId}: {pathError}");
            return;
        }

        if (!File.Exists(avatarPath))
        {
            Server.PrintToConsole(
                $"dtr: replay avatar skipped slot={slot} player={file.PlayerName} sid={file.SteamId}: missing {avatar.Path}");
            return;
        }

        Server.ExecuteCommand(
            $"bc_avatar_override_probe {file.SteamId} \"{EscapeConsoleString(avatarPath)}\"");
        Server.PrintToConsole(
            $"dtr: replay avatar queued slot={slot} player={file.PlayerName} sid={file.SteamId} path={avatar.Path}");
    }

    private string PlayLoaded(bool loop)
    {
        PreloadLoadedReplays();
        return StartLoaded(loop);
    }

    private void PreloadLoadedReplays()
    {
        if (_weaponAlignEnabled)
        {
            foreach (var slot in _loadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_loadedReplays.TryGetValue(slot, out var replay))
                {
                    if (replay.UtilityOnly)
                    {
                        PrepareNadeClipWeapon(slot, replay.UtilityWeaponDefIndex, out _);
                        continue;
                    }
                    ApplyReplayLoadoutForSlot(slot, replay);
                    PreloadReplayWeaponsForSlot(slot, replay);
                }
            }
        }

        if (_cosmeticAlignEnabled && _weaponAlignEnabled)
        {
            foreach (var slot in _loadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_loadedReplays.TryGetValue(slot, out var replay) && !_cosmeticSyncedSlots.Contains(slot))
                    ApplyLoadedReplayCosmeticsForSlot(slot, replay);
            }
        }

        ApplyLoadedReplayScoreboards();
        AlignSafeC4OwnerForLoadedReplays();
    }

    private void AlignSafeC4OwnerForLoadedReplays()
    {
        if (_safeC4Aligned)
            return;

        var plantedOwner = FindLoadedC4Owner(IsBombPlantedEvent);
        var initialOwner = FindLoadedC4Owner(IsBombInitialOwnerEvent);
        var targetOwner = plantedOwner ?? initialOwner;

        if (!targetOwner.HasValue)
            return;

        var targetSlot = targetOwner.Value.Slot;
        var targetSteamId = targetOwner.Value.SteamId;
        if (targetSlot < 0 || !IsReplaySlotStillSafe(targetSlot))
            return;

        foreach (var slot in _loadedSlots.ToArray())
        {
            if (slot == targetSlot || !_loadedReplays.TryGetValue(slot, out var replay) || replay.UtilityOnly)
                continue;
            RemoveC4FromReplaySlot(slot, "safe_c4_owner_align");
        }

        var player = Utilities.GetPlayerFromSlot(targetSlot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;
        if (CountCurrentReplayItems(player, "weapon_c4") <= 0 &&
            !TryGiveNamedItem(player, "weapon_c4"))
        {
            Server.PrintToConsole(
                $"dtr: C4 safe owner align failed slot={targetSlot} steam_id={targetSteamId}");
            return;
        }

        _safeC4Aligned = true;
        if (plantedOwner.HasValue &&
            initialOwner.HasValue &&
            plantedOwner.Value.SteamId != initialOwner.Value.SteamId)
        {
            Server.PrintToConsole(
                "dtr: C4 safe owner collapsed to planter " +
                $"slot={targetSlot} steam_id={targetSteamId} initial_steam_id={initialOwner.Value.SteamId}");
            return;
        }

        var source = plantedOwner.HasValue ? "bomb_planted" : "bomb_initial_owner";
        Server.PrintToConsole(
            $"dtr: C4 safe owner aligned slot={targetSlot} steam_id={targetSteamId} source={source}");
    }

    private static bool IsBombInitialOwnerEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_initial_owner", StringComparison.OrdinalIgnoreCase);

    private static bool IsBombPlantedEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_planted", StringComparison.OrdinalIgnoreCase);

    private (int Slot, ulong SteamId)? FindLoadedC4Owner(Func<ReplayHifiEvent, bool> predicate)
    {
        foreach (var slot in _loadedSlots)
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay) || replay.UtilityOnly)
                continue;

            var replayEvent = replay.HifiEvents.FirstOrDefault(predicate);
            if (replayEvent is null)
                continue;

            var steamId = replayEvent.ActorSteamId.GetValueOrDefault(replay.SteamId);
            return (slot, steamId);
        }

        return null;
    }

    private void RemoveC4FromReplaySlot(int slot, string reason)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        var pawn = player.PlayerPawn.Value;
        foreach (var weapon in GetReplayWeaponsByClass(pawn, "weapon_c4").ToArray())
            DropAndKillReplayWeapon(player, pawn, weapon, reason);
    }

    private string StartLoaded(bool loop)
        => StartLoaded(loop, ReplayStartAnchor.Live, null);

    private string StartLoaded(
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        var respawned = RespawnDeadLoadedReplayBots();
        if (respawned > 0)
        {
            Server.NextFrame(() =>
            {
                PreloadLoadedReplays();
                Server.PrintToConsole(
                    $"dtr: queued start after respawn: {StartLoadedReady(loop, anchor, freezeTimeSeconds)}");
            });
            return $"dtr: respawned {respawned} replay bot(s), start queued";
        }

        return StartLoadedReady(loop, anchor, freezeTimeSeconds);
    }

    private string StartLoadedReady(
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        if (IsWarmupPeriod())
            return "[DTR ERR] 热身阶段无法进行回放";

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

    private bool StartReplayForSlot(
        int slot,
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        if (IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            return false;
        }

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

    private static bool IsWarmupPeriod()
    {
        try
        {
            var proxy = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(entity => entity is { IsValid: true });
            return proxy is { IsValid: true } &&
                   proxy.GameRules != null &&
                   proxy.GameRules.WarmupPeriod;
        }
        catch
        {
            return false;
        }
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
        => StopAndUnloadLoaded(clearArmedPlan: true);

    private void StopAndUnloadLoaded(bool clearArmedPlan)
    {
        var trackedSlots = _loadedSlots.ToHashSet();
        StopNadeCycle("unload_all", stopCurrent: false);
        foreach (var slot in _loadedSlots.ToArray())
        {
            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, "unload_all");
            BotControllerNative.UnloadReplay(slot);
        }
        StopUntrackedNativeReplaySlots(trackedSlots, "unload_all");
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
        ResetCosmeticAlignState(resetCounters: true);
        ResetStickerAlignState(resetCounters: true);
        ResetCrosshairAlignState(resetCounters: true);
        ResetScoreboardAlignState(resetCounters: true);
        _loadedRoundScoreboard = null;
        _lastPlayingSlots.Clear();
        _quietReplaySlots.Clear();
        _replayStartedAt.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        _pendingThreat360.Clear();
        _safeC4Aligned = false;
        if (clearArmedPlan)
        {
            _armed = false;
            _armedPrepared = false;
            _armedManifestPath = string.Empty;
            _armedSourceRound = -1;
        }
        else
        {
            _armedPrepared = false;
        }
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        SetReplayPovMask(0);
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
        _cosmeticSyncedSlots.Clear();
        RestoreAllReplayViewerCrosshairs();
        _lastPlayingSlots.Clear();
        _quietReplaySlots.Clear();
        _replayStartedAt.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        _pendingThreat360.Clear();
        _safeC4Aligned = false;
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

    private bool StopReplayStateForRoundBoundary(string reason)
    {
        if (_loadedSlots.Count == 0 &&
            _lastPlayingSlots.Count == 0 &&
            _queuedNadeStartTokens.Count == 0 &&
            !HasAnyNativeActiveReplaySlot())
            return false;

        StopAndUnloadLoaded(clearArmedPlan: false);
        return true;
    }

    private static IEnumerable<int> NativeReplaySlots()
    {
        for (var slot = 0; slot < MaxPlayerSlots; slot++)
            yield return slot;
    }

    private void StopUntrackedNativeReplaySlots(IReadOnlySet<int> trackedSlots, string reason)
    {
        foreach (var slot in NativeReplaySlots())
        {
            if (trackedSlots.Contains(slot) || !BotControllerNative.GetReplayState(slot).Playing)
                continue;

            BotControllerNative.StopReplay(slot);
            BotControllerNative.UnloadReplay(slot);
            BotControllerNative.ClearBuyPlan(slot);
            BotControllerNative.UnlockWeaponSlot(slot);
            ClearReplayPovSlot(slot);
            Server.PrintToConsole($"dtr: stopped native replay slot={slot} reason={reason}");
        }
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

    private static void IssueRestartIfRequested(bool restart, Action<string> reply)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        reply("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_start.");
    }

    private void MarkReplayStarted(int slot)
    {
        _lastPlayingSlots.Add(slot);
        _replayStartedAt[slot] = Server.CurrentTime;
        _projectileAlignNextBySlot[slot] = 0;
        _replayHifiEventNextBySlot[slot] = 0;
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
        _replayHifiEventNextBySlot.Remove(slot);
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
        KillTrackedReplayDropsForSlot(slot, reason);
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

    private bool HasAnyNativeActiveReplaySlot()
    {
        if (HasActiveReplaySlots())
            return true;

        foreach (var slot in NativeReplaySlots())
        {
            if (BotControllerNative.GetReplayState(slot).Playing)
                return true;
        }
        return false;
    }

    private bool CheckReplayStartGates(Action<string> reply, bool stopCurrentForOverride)
    {
        if (IsWarmupPeriod())
        {
            reply("[DTR ERR] 热身阶段无法进行回放");
            return false;
        }

        if (!stopCurrentForOverride || !HasAnyNativeActiveReplaySlot())
            return true;

        reply("[DTR WARN] 会STOP当前所有DTR并override");
        StopAndUnloadLoaded();
        StopSequenceState();
        StopPoolState();
        return true;
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
        _replayHifiEventNextBySlot.Remove(slot);
        _queuedNadeStartTokens.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
        _cosmeticSyncedSlots.Remove(slot);
        _scoreboardSyncedSlots.Remove(slot);
        KillTrackedReplayDropsForSlot(slot, "forget_replay");
    }

    private void TrackLoadedReplay(
        int slot,
        string path,
        string playerName,
        ulong steamId = 0,
        int manifestFirstWeaponDefIndex = -1,
        IReadOnlyList<int>? manifestPreloadWeaponDefIndices = null,
        ReplayLoadoutSnapshot? loadout = null,
        ReplayCosmetics? cosmetics = null,
        ReplayView? view = null,
        ReplayPlayerScoreboard? scoreboard = null,
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
        var hifiEvents = (metadata.HighFidelity?.Events ?? [])
            .OrderBy(replayEvent => replayEvent.TickIndex)
            .ThenBy(replayEvent => replayEvent.Tick)
            .ToArray();
        var inventorySnapshots = (metadata.HighFidelity?.InventorySnapshots ?? [])
            .OrderBy(snapshot => snapshot.TickIndex)
            .ThenBy(snapshot => snapshot.Tick)
            .ToArray();
        var normalizedCosmetics = NormalizeReplayCosmetics(cosmetics);
        var normalizedView = NormalizeReplayView(view);
        var normalizedScoreboard = NormalizeReplayScoreboard(scoreboard);
        _loadedReplays[slot] = new LoadedReplay(
            path,
            playerName,
            steamId,
            firstDef,
            preloadDefs,
            hasLoadout,
            NormalizeReplayLoadout(loadout ?? new ReplayLoadoutSnapshot()),
            normalizedCosmetics,
            normalizedView,
            normalizedScoreboard,
            metadata.Projectiles ?? [],
            hifiEvents,
            inventorySnapshots,
            metadata.TickCount,
            utilityOnly,
            NormalizeWeaponDefIndex(utilityWeaponDefIndex),
            metadata.TickRate,
            metadata.PlayStartTickIndex);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _projectileAlignNextBySlot[slot] = 0;
        _replayHifiEventNextBySlot[slot] = 0;
        _rebuiltInventorySlots.Remove(slot);
        _loadoutSyncedSlots.Remove(slot);
        _cosmeticSyncedSlots.Remove(slot);
        _scoreboardSyncedSlots.Remove(slot);
        _safeC4Aligned = false;
    }

    private static ReplayFileMetadata ReadReplayMetadataOrEmpty(string path)
        => BotControllerNative.TryReadReplayMetadata(path, out var metadata)
            ? metadata
            : ReplayFileMetadata.Empty;

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

    private void CompleteWeaponSlotReplacement(
        CCSPlayerController player,
        string targetItem,
        string fallbackItem,
        ReplayWeaponSlot slot)
    {
        if (player is not { IsValid: true, PawnIsAlive: true })
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
        if (player is not { IsValid: true, PawnIsAlive: true })
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
        if (player is not { IsValid: true, PawnIsAlive: true })
            return false;

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
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

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
                replaceConflictingSlot: true);
            if (!ensured)
            {
                _lastReplayWeaponDef.Remove(slot);
                return;
            }
        }

        if (BotControllerNative.SwitchBotWeapon(slot, normalized))
        {
            _lastReplayWeaponDef[slot] = normalized;
            ApplyReplayWeaponCosmeticForSlot(slot, normalized);
        }
        else if (!allowSlotReplacement)
        {
            _lastReplayWeaponDef[slot] = normalized;
        }
        else
        {
            _lastReplayWeaponDef.Remove(slot);
        }
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
        if (player is not { IsValid: true, PawnIsAlive: true } ||
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
        ApplyReplayWeaponCosmeticForSlot(slot, normalized);
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
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true })
            return false;

        if (HasReplayWeapon(pawn, className))
            return true;

        var slot = GetReplayWeaponSlot(className);
        if (!allowGive)
            return false;
        if (slot is ReplayWeaponSlot.Other or ReplayWeaponSlot.Knife or
            ReplayWeaponSlot.C4 or ReplayWeaponSlot.Taser)
            return false;

        if (HasConflictingWeaponInSlot(pawn, slot, className))
        {
            if (!replaceConflictingSlot)
                return false;

            var targetClassName = className;
            var conflictingWeapons = GetWeaponsInReplaySlot(pawn, slot)
                .Where(weapon => !WeaponClassMatches(weapon.DesignerName, targetClassName))
                .ToList();
            foreach (var weapon in conflictingWeapons)
            {
                if (!DropAndKillReplayWeapon(player, pawn, weapon, "replace_replay_slot"))
                    return false;
            }
        }

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


    private enum ReplayStartAnchor
    {
        Live,
        FreezePreroll,
    }

    private enum ReplayIdentityMode
    {
        Off,
        Name,
        Full,
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
        ReplayCosmetics Cosmetics,
        ReplayView View,
        ReplayPlayerScoreboard Scoreboard,
        ReplayProjectileEvent[] Projectiles,
        ReplayHifiEvent[] HifiEvents,
        ReplayInventorySnapshot[] InventorySnapshots,
        int TickCount,
        bool UtilityOnly,
        int UtilityWeaponDefIndex,
        float TickRate,
        uint PlayStartTickIndex);

    private readonly record struct ReplayAssignment(ManifestFile File, CCSPlayerController Bot);

    private readonly record struct PendingWeaponAlign(int WeaponDefIndex, bool ForceSwitch);

    private readonly record struct PendingBulletHit(int AttackerSlot, float Time);

    private readonly record struct PendingBulletDamage(int AttackerSlot, int Damage, float Time);

    private readonly record struct PendingThreat360(int EnemySlot, float FirstSeenAt);

    private readonly record struct TrackedDroppedReplayItem(int SourceSlot, int WeaponDefIndex, IntPtr Handle);

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

    private static string CurrentMapName()
    {
        try
        {
            return Server.MapName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool CheckManifestMap(CommandInfo command, string manifestMap, string manifestPath)
    {
        if (CurrentMapMatchesManifest(manifestMap, out var currentMap))
            return true;

        command.ReplyToCommand(
            $"[DTR ERR] map mismatch: server=\"{currentMap}\" manifest=\"{manifestMap}\" path=\"{manifestPath}\"");
        return false;
    }

    private static bool CurrentMapMatchesManifest(string manifestMap, out string currentMap)
    {
        currentMap = CurrentMapName();
        if (string.IsNullOrWhiteSpace(manifestMap) ||
            string.IsNullOrWhiteSpace(currentMap) ||
            currentMap.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NormalizeMapName(currentMap).Equals(NormalizeMapName(manifestMap), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMapName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("de_", StringComparison.Ordinal)
            ? normalized[3..]
            : normalized;
    }

    private static string FormatRoundList(IReadOnlyList<int> rounds)
    {
        if (rounds.Count == 0)
            return "none";
        if (rounds.Count <= 16)
            return string.Join(",", rounds);
        return $"{string.Join(",", rounds.Take(16))},... ({rounds.Count})";
    }

    private static bool CheckAbi(CommandInfo command)
    {
        if (BotControllerNative.IsCompatible)
            return true;

        command.ReplyToCommand(
            $"dtr: ABI mismatch; {BotControllerNative.RuntimeSummary}");
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
        if (command.ArgCount > argIndex &&
            int.TryParse(command.GetArg(argIndex), out slot) &&
            slot is >= 0 and < MaxPlayerSlots)
            return true;

        command.ReplyToCommand($"dtr: slot must be an integer from 0 to {MaxPlayerSlots - 1}");
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
}
