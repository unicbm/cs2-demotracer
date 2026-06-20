using System.IO.Compression;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
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
        if (manifest.FormatVersion != RoundPoolManifestFormatVersion)
        {
            throw new InvalidDataException(
                $"pool manifest format_version {manifest.FormatVersion} unsupported; expected {RoundPoolManifestFormatVersion}");
        }

        ValidateManifestAbi(manifest.Abi);
        for (var i = 0; i < manifest.Candidates.Count; i++)
            ValidateRoundPoolCandidate(manifest.Candidates[i], i);
    }

    private static void ValidateRoundPoolCandidate(RoundPoolCandidate? candidate, int index)
    {
        if (candidate == null)
            throw new InvalidDataException($"pool candidate {index} is null");
        if (string.IsNullOrWhiteSpace(candidate.Manifest))
            throw new InvalidDataException($"pool candidate {index} manifest is required");
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
        return JsonSerializer.Deserialize<ConversionManifest>(
                   json,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   })
               ?? throw new InvalidOperationException("manifest JSON is empty");
    }
}
