using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Commands;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
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
