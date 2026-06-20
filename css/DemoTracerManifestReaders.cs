using System.IO.Compression;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
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
        for (var i = 0; i < manifest.Files.Count; i++)
            ValidateManifestFile(manifest.Files[i], i, paths);
    }

    private static void ValidateManifestFile(ManifestFile? file, int index, HashSet<string> paths)
    {
        if (file == null)
            throw new InvalidDataException($"manifest file {index} is null");
        if (string.IsNullOrWhiteSpace(file.Path))
            throw new InvalidDataException($"manifest file {index} path is required");
        if (!file.Path.EndsWith(".dtr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"manifest file {index} path must point to .dtr: {file.Path}");
        if (!paths.Add(file.Path))
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
            ValidateNadeManifest(manifest);
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

    private static string ResolveManifestPath(string manifestPath, string childPath)
    {
        if (TryResolveManifestChildPath(manifestPath, childPath, out var fullPath, out var error))
            return fullPath;
        throw new InvalidDataException(error);
    }

    private static bool TryResolveManifestChildPath(
        string manifestPath,
        string childPath,
        out string fullPath,
        out string error)
    {
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        return TryResolveChildPathUnderRoot(manifestDir, childPath, out fullPath, out error);
    }

    private static bool TryResolveChildPathUnderRoot(
        string rootDir,
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
        fullPath = Path.GetFullPath(Path.Combine(root, childPath.Replace('/', Path.DirectorySeparatorChar)));
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

    private static void ValidateNadeManifest(NadeManifest manifest)
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
            ValidateNadeClip(manifest.Clips[i], i, clipIds);
    }

    private static void ValidateNadeClip(NadeClip? clip, int index, HashSet<string> clipIds)
    {
        if (clip == null)
            throw new InvalidDataException($"nade clip {index} is null");
        if (string.IsNullOrWhiteSpace(clip.ClipId))
            throw new InvalidDataException($"nade clip {index} clip_id is required");
        if (!clipIds.Add(clip.ClipId))
            throw new InvalidDataException($"duplicate nade clip_id: {clip.ClipId}");
        if (string.IsNullOrWhiteSpace(clip.Path))
            throw new InvalidDataException($"nade clip {clip.ClipId} path is required");
    }

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
