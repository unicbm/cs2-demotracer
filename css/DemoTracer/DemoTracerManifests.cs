using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
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

        [JsonPropertyName("rounds")]
        public List<ManifestRound> Rounds { get; set; } = new();

        public int EffectiveDtrFormatVersion => DtrFormatVersion != 0 ? DtrFormatVersion : FormatVersion;
    }

    private sealed class ManifestRound
    {
        [JsonPropertyName("round")]
        public int Round { get; set; }

        [JsonPropertyName("bomb_planted_tick")]
        public int? BombPlantedTick { get; set; }

        [JsonPropertyName("bomb_planted_seconds_after_live")]
        public float? BombPlantedSecondsAfterLive { get; set; }

        [JsonPropertyName("duration_seconds")]
        public float DurationSeconds { get; set; }
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

    private const int NadeManifestFormatVersion = 1;
    private const int RoundPoolManifestFormatVersion = 1;

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
            ValidateConversionManifest(manifestPath, manifest);
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

    private static void ValidateConversionManifest(string manifestPath, ConversionManifest manifest)
    {
        manifest.Files ??= new List<ManifestFile>();
        ValidateManifestAbi(manifest.Abi);
        if (string.IsNullOrWhiteSpace(manifest.Map))
            throw new InvalidDataException("manifest map is required");

        var formatVersion = manifest.EffectiveDtrFormatVersion;
        if (formatVersion != 0)
        {
            var minVersion = (int)BotControllerNative.MinRecFormatVersion;
            var maxVersion = (int)BotControllerNative.RecFormatVersion;
            if (formatVersion < minVersion || formatVersion > maxVersion)
            {
                throw new InvalidDataException(
                    $"manifest format_version {formatVersion} unsupported; expected {minVersion}..{maxVersion}");
            }
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        for (var i = 0; i < manifest.Files.Count; i++)
            ValidateManifestFile(manifest.Files[i], i, manifestDir, paths);
    }

    private static void ValidateManifestFile(
        ManifestFile? file,
        int index,
        string manifestDir,
        HashSet<string> paths)
    {
        if (file == null)
            throw new InvalidDataException($"manifest file {index} is null");
        if (string.IsNullOrWhiteSpace(file.Path))
            throw new InvalidDataException($"manifest file {index} path is required");
        if (!file.Path.EndsWith(".dtr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"manifest file {index} path must point to .dtr: {file.Path}");
        if (!TryResolveChildPathUnderRoot(manifestDir, file.Path, out var fullPath, out var pathError))
            throw new InvalidDataException($"manifest file {index} {pathError}");
        if (!paths.Add(fullPath))
            throw new InvalidDataException($"duplicate manifest file path: {file.Path}");
        if (string.IsNullOrWhiteSpace(file.Side) ||
            !file.Side.Equals("t", StringComparison.OrdinalIgnoreCase) &&
            !file.Side.Equals("ct", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"manifest file {index} side must be t or ct: {file.Side}");
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

        return ManifestContainsSourceRound(manifest, sourceRound, out error);
    }

    private static bool ManifestContainsSourceRound(
        ConversionManifest manifest,
        int sourceRound,
        out string error)
    {
        error = string.Empty;
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

            var manifest = DeserializeManifestJson<NadeManifest>(
                fullPath,
                ReadMaybeBrotliText(fullPath),
                "nade manifest");
            ValidateNadeManifest(fullPath, manifest);
            var clipsById = new Dictionary<string, NadeClip>(manifest.Clips.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var clip in manifest.Clips)
            {
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
        try
        {
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli);
            return reader.ReadToEnd();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"manifest Brotli payload is invalid: {path} ({ex.Message})",
                ex);
        }
    }

    private static string ResolveNadeClipPath(string manifestPath, string childPath)
    {
        if (TryResolveNadeClipPath(manifestPath, childPath, out var fullPath, out var error))
            return fullPath;
        throw new InvalidDataException(error);
    }

    private static bool TryResolveNadeClipPath(
        string manifestPath,
        string childPath,
        out string fullPath,
        out string error)
    {
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        if (TryResolveRelativePathUnderRoot(manifestDir, manifestDir, childPath, out fullPath, out error))
            return true;

        if (TryGetNadeLibraryRoot(manifestPath, manifestDir, out var libraryRoot) &&
            TryResolveRelativePathUnderRoot(libraryRoot, manifestDir, childPath, out fullPath, out _))
        {
            error = string.Empty;
            return true;
        }

        return false;
    }

    private static bool TryResolveChildPathUnderRoot(
        string rootDir,
        string childPath,
        out string fullPath,
        out string error)
        => TryResolveRelativePathUnderRoot(rootDir, rootDir, childPath, out fullPath, out error);

    private static bool TryResolveRelativePathUnderRoot(
        string rootDir,
        string baseDir,
        string childPath,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(childPath))
        {
            error = "manifest child path is empty";
            return false;
        }
        if (Path.IsPathRooted(childPath))
        {
            error = $"manifest child path must be relative: {childPath}";
            return false;
        }

        var root = Path.GetFullPath(rootDir);
        var basePath = Path.GetFullPath(baseDir);
        fullPath = Path.GetFullPath(Path.Combine(basePath, childPath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal))
        {
            error = $"manifest child path escapes manifest directory: {childPath}";
            fullPath = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryGetNadeLibraryRoot(string manifestPath, string manifestDir, out string libraryRoot)
    {
        libraryRoot = string.Empty;
        if (!IsNadeManifestFileName(Path.GetFileName(manifestPath)))
            return false;

        var mapDir = Path.GetFullPath(manifestDir);
        var mapsDir = Path.GetDirectoryName(mapDir);
        if (mapsDir == null ||
            !string.Equals(Path.GetFileName(mapsDir), "maps", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        libraryRoot = Path.GetDirectoryName(mapsDir) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(libraryRoot);
    }

    private static bool IsNadeManifestFileName(string? name)
        => string.Equals(name, "nade_manifest.json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "nade_manifest.json.br", StringComparison.OrdinalIgnoreCase);

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
            manifest = DeserializeManifestJson<RoundPoolManifest>(
                manifestPath,
                json,
                "pool manifest");
            ValidateRoundPoolManifest(manifestPath, manifest);
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

    private static void ValidateNadeManifest(string manifestPath, NadeManifest manifest)
    {
        if (manifest.FormatVersion != NadeManifestFormatVersion)
        {
            throw new InvalidDataException(
                $"nade manifest format_version {manifest.FormatVersion} unsupported; expected {NadeManifestFormatVersion}");
        }
        if (string.IsNullOrWhiteSpace(manifest.Map))
            throw new InvalidDataException("nade manifest map is required");

        manifest.Clips ??= new List<NadeClip>();
        var clipIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < manifest.Clips.Count; i++)
            ValidateNadeClip(manifestPath, manifest.Clips[i], i, clipIds);
    }

    private static void ValidateNadeClip(
        string manifestPath,
        NadeClip? clip,
        int index,
        HashSet<string> clipIds)
    {
        if (clip == null)
            throw new InvalidDataException($"nade clip {index} is null");
        if (string.IsNullOrWhiteSpace(clip.ClipId))
            throw new InvalidDataException($"nade clip {index} clip_id is required");
        if (!clipIds.Add(clip.ClipId))
            throw new InvalidDataException($"duplicate nade clip_id: {clip.ClipId}");
        ValidateNadeClipFields(manifestPath, clip, clip.ClipId);
    }

    private static void ValidateNadeClipFields(string manifestPath, NadeClip clip, string label)
    {
        if (string.IsNullOrWhiteSpace(clip.Path))
            throw new InvalidDataException($"nade clip {label} path is required");
        if (!clip.Path.EndsWith(".dtr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"nade clip {label} path must point to .dtr: {clip.Path}");
        if (!IsManifestValueOneOf(clip.Side, "t", "ct"))
            throw new InvalidDataException($"nade clip {label} side must be t or ct: {clip.Side}");
        if (!IsManifestValueOneOf(clip.Phase, "opening", "combat", "retake"))
            throw new InvalidDataException($"nade clip {label} phase is unsupported: {clip.Phase}");
        if (!IsManifestValueOneOf(clip.Kind, "unknown", "smoke", "flash", "he", "molotov", "decoy"))
            throw new InvalidDataException($"nade clip {label} kind is unsupported: {clip.Kind}");
        if (!TryResolveNadeClipPath(manifestPath, clip.Path, out _, out var pathError))
            throw new InvalidDataException($"nade clip {label} {pathError}");
    }

    private static bool IsManifestValueOneOf(string? value, params string[] allowed)
        => !string.IsNullOrWhiteSpace(value) &&
           allowed.Any(item => value.Equals(item, StringComparison.OrdinalIgnoreCase));

    private static void ValidateRoundPoolManifest(string manifestPath, RoundPoolManifest manifest)
    {
        manifest.Candidates ??= new List<RoundPoolCandidate>();
        if (manifest.FormatVersion != RoundPoolManifestFormatVersion)
        {
            throw new InvalidDataException(
                $"pool manifest format_version {manifest.FormatVersion} unsupported; expected {RoundPoolManifestFormatVersion}");
        }
        if (string.IsNullOrWhiteSpace(manifest.Map))
            throw new InvalidDataException("pool manifest map is required");

        ValidateManifestAbi(manifest.Abi);
        var poolDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < manifest.Candidates.Count; i++)
            ValidateRoundPoolCandidate(manifest.Candidates[i], i, poolDir, candidates);
    }

    private static void ValidateRoundPoolCandidate(
        RoundPoolCandidate? candidate,
        int index,
        string poolDir,
        HashSet<string> candidates)
    {
        if (candidate == null)
            throw new InvalidDataException($"pool candidate {index} is null");
        if (string.IsNullOrWhiteSpace(candidate.Manifest))
            throw new InvalidDataException($"pool candidate {index} manifest is required");
        if (!candidate.Manifest.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"pool candidate {index} manifest must point to .json: {candidate.Manifest}");
        if (!TryResolveChildPathUnderRoot(poolDir, candidate.Manifest, out _, out var pathError))
            throw new InvalidDataException($"pool candidate {index} {pathError}");
        var candidateKey = $"{candidate.Manifest}|{candidate.SourceRound}";
        if (!candidates.Add(candidateKey))
            throw new InvalidDataException($"duplicate pool candidate manifest/source_round: {candidate.Manifest} r{candidate.SourceRound}");
        if (candidate.SourceRound < 0)
            throw new InvalidDataException($"pool candidate {index} source_round must be non-negative");
        if (candidate.Files <= 0)
            throw new InvalidDataException($"pool candidate {index} files must be positive");
        if (candidate.TEconomy == null)
            throw new InvalidDataException($"pool candidate {index} t_economy is required");
        if (candidate.CtEconomy == null)
            throw new InvalidDataException($"pool candidate {index} ct_economy is required");

        ValidatePoolTeamEconomy(candidate.TEconomy, index, "t_economy", "t");
        ValidatePoolTeamEconomy(candidate.CtEconomy, index, "ct_economy", "ct");
    }

    private static void ValidatePoolTeamEconomy(
        PoolTeamEconomy economy,
        int candidateIndex,
        string fieldName,
        string expectedSide)
    {
        if (!string.IsNullOrWhiteSpace(economy.Side) &&
            !economy.Side.Equals(expectedSide, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"pool candidate {candidateIndex} {fieldName}.side must be {expectedSide}");
        }
        if (economy.Players < 0)
            throw new InvalidDataException($"pool candidate {candidateIndex} {fieldName}.players must be non-negative");
        if (string.IsNullOrWhiteSpace(economy.Class))
            throw new InvalidDataException($"pool candidate {candidateIndex} {fieldName}.class is required");
    }

    private static ConversionManifest ReadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return DeserializeManifestJson<ConversionManifest>(
            manifestPath,
            json,
            "manifest");
    }

    private static T DeserializeManifestJson<T>(string path, string json, string manifestKind)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ManifestJsonOptions)
                   ?? throw new InvalidDataException($"{manifestKind} JSON is empty: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{manifestKind} contains invalid JSON: {path} ({ex.Message})",
                ex);
        }
    }
}
