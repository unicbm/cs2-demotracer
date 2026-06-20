using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

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
}
