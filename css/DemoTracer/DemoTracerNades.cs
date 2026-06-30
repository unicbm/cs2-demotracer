using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using DemoTracerApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
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
        if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
        {
            command.ReplyToCommand(
                $"[DTR WARN] map mismatch: server=\"{currentMap}\" nade_manifest=\"{manifest.Map}\" path=\"{manifestPath}\"");
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
        if (!TryParseSlotAt(command, 3, out var slot))
            return;

        var manifestPath = command.GetArg(1);
        var clipId = command.GetArg(2);
        var loop = command.ArgCount >= 5 && command.GetArg(4) != "0";
        var result = RunNadeClipWithStartGate(
            manifestPath,
            clipId,
            slot,
            loop,
            warningSink: message => command.ReplyToCommand(message));
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
        if (!TryParseSlotAt(command, 2, out var slot))
            return;
        if (!TryParseNadeCycleArgs(command, 3, commandName, out var sideFilter, out var phaseFilter, out var gapSeconds, out var parseError))
        {
            command.ReplyToCommand(parseError);
            return;
        }

        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: true))
            return;
        var result = StartNadeCycle(command.GetArg(1), slot, kindFilter, sideFilter, phaseFilter, gapSeconds);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("dtr_stop_nade_cycle", "dtr_stop_nade_cycle")]
    public void StopNadeCycleCommand(CCSPlayerController? player, CommandInfo command)
    {
        var stopped = StopNadeCycle("manual_stop_nade_cycle", stopCurrent: true);
        command.ReplyToCommand(stopped ? "dtr: nade cycle stopped" : "dtr: no active nade cycle");
    }

    private LoadRoundResult RunNadeClipWithStartGate(
        string manifestPath,
        string clipId,
        int slot,
        bool loop,
        Action<string>? warningSink = null,
        bool quiet = false)
    {
        if (!TryCheckNadeReplayStartGates(warningSink, out var blocked))
            return blocked;

        return RunNadeClip(manifestPath, clipId, slot, loop, quiet);
    }

    private LoadRoundResult RunNadeClipWithStartGate(
        string manifestPath,
        NadeClip clip,
        int slot,
        bool loop,
        Action<string>? warningSink = null,
        bool quiet = false)
    {
        if (!TryCheckNadeReplayStartGates(warningSink, out var blocked))
            return blocked;

        return RunNadeClip(manifestPath, clip, slot, loop, quiet);
    }

    private bool TryCheckNadeReplayStartGates(Action<string>? warningSink, out LoadRoundResult blocked)
    {
        var gateMessages = new List<string>();
        if (!CheckReplayStartGates(message => gateMessages.Add(message), stopCurrentForOverride: true))
        {
            var blockMessage = gateMessages.LastOrDefault() ?? "dtr: replay start blocked";
            blocked = LoadRoundResult.Fail(blockMessage);
            return false;
        }

        foreach (var message in gateMessages)
            warningSink?.Invoke(message);

        blocked = default;
        return true;
    }

    private sealed class DemoTracerApiFacade : IDemoTracerApi
    {
        private readonly DemoTracerPlugin _plugin;

        public DemoTracerApiFacade(DemoTracerPlugin plugin)
        {
            _plugin = plugin;
        }

        public int ApiVersion => BotControllerNative.DemoTracerApiVersion;

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
            var run = _plugin.RunNadeClipWithStartGate(manifestPath, clipId, slot, loop, quiet: true);
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
            var run = _plugin.RunNadeClipWithStartGate(baseManifestPath, internalClip, slot, loop, quiet: true);
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

        public bool IsDemoTracerBot(int slot)
            => _plugin.IsDemoTracerBot(slot);

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
}
