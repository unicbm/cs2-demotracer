using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

internal static class DtrReplayReader
{
    private const byte RecCodecBrotli = 1;
    private const byte SectionCodecNone = 0;
    private const int TickMetadataByteSize = 8;
    private const int ProjectileEventByteSize = 48;
    private const uint SectionSnapshots = 1;
    private const uint SectionTickMetadata = 2;
    private const uint SectionProjectiles = 3;
    private const uint SectionHighFidelityJson = 4;
    private const uint SectionSubticks = 5;
    private const uint SectionCommandFrames = 6;
    private const uint SectionMovementExtras = 7;
    private const uint SectionVersionV1 = 1;

    private static readonly byte[] RecMagic =
    [
        (byte)'C', (byte)'S', (byte)'D', (byte)'T',
        (byte)'R', (byte)'R', (byte)'E', (byte)'C'
    ];

    private static readonly JsonSerializerOptions HifiJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static DtrReplayFile Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".dtr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("expected .dtr replay file");

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(RecMagic.Length);
        if (!magic.SequenceEqual(RecMagic))
            throw new InvalidDataException("bad .dtr magic");

        var version = reader.ReadUInt32();
        if (version is < BotControllerNative.MinRecFormatVersion or > BotControllerNative.RecFormatVersion)
            throw new InvalidDataException(
                $"unsupported .dtr version {version}; expected {BotControllerNative.MinRecFormatVersion}..{BotControllerNative.RecFormatVersion}");

        var tickRate = reader.ReadSingle();
        _ = reader.ReadUInt32(); // round
        _ = reader.ReadByte();   // side
        _ = reader.ReadUInt32(); // flags
        _ = reader.ReadUInt64(); // steam_id
        var tickCount = CheckedCount(reader.ReadUInt32(), "tick_count");
        var subtickCount = CheckedCount(reader.ReadUInt32(), "subtick_count");
        var projectileCount = version >= 4
            ? CheckedCount(reader.ReadUInt32(), "projectile_count")
            : 0;
        var playStartTickIndex = version >= 5
            ? CheckedCount(reader.ReadUInt32(), "play_start_tick_index")
            : 0;
        var metadataJsonLength = version >= 6
            ? CheckedCount(reader.ReadUInt32(), "metadata_json_len")
            : 0;
        ValidatePlayStartTickIndex(tickCount, playStartTickIndex);
        _ = ReadRecString(reader); // map
        _ = ReadRecString(reader); // player name

        if (version >= 7)
            return ReadV7Sections(
                reader,
                version,
                tickRate,
                tickCount,
                subtickCount,
                projectileCount,
                playStartTickIndex,
                metadataJsonLength);

        return ReadLegacyBody(
            reader,
            version,
            tickRate,
            tickCount,
            subtickCount,
            projectileCount,
            playStartTickIndex,
            metadataJsonLength);
    }

    private static DtrReplayFile ReadLegacyBody(
        BinaryReader reader,
        uint version,
        float tickRate,
        int tickCount,
        int subtickCount,
        int projectileCount,
        int playStartTickIndex,
        int metadataJsonLength)
    {
        var codec = reader.ReadByte();
        if (codec != RecCodecBrotli)
            throw new InvalidDataException($"unsupported .dtr codec {codec}");

        var bodyUncompressedLength = CheckedLength(reader.ReadUInt64(), "body_uncompressed_len");
        var bodyCompressedLength = CheckedLength(reader.ReadUInt64(), "body_compressed_len");
        var expectedBodyLength = ExpectedBodyLength(tickCount, subtickCount, projectileCount, metadataJsonLength);
        if (bodyUncompressedLength != expectedBodyLength)
            throw new InvalidDataException($"body length {bodyUncompressedLength} != expected {expectedBodyLength}");

        var compressed = reader.ReadBytes(bodyCompressedLength);
        if (compressed.Length != bodyCompressedLength)
            throw new EndOfStreamException("truncated compressed .dtr body");

        var body = DecompressBrotli(compressed, bodyUncompressedLength);
        using var bodyStream = new MemoryStream(body, writable: false);
        using var bodyReader = new BinaryReader(bodyStream);

        var snapshotCount = tickCount == 0 ? 0 : tickCount + 1;
        var snapshots = new NativeMovementSnapshot[snapshotCount];
        for (var i = 0; i < snapshotCount; i++)
            snapshots[i] = ReadCurrentSnapshot(bodyReader);

        var ticks = new NativeReplayTick[tickCount];
        long expectedSubticks = 0;
        for (var i = 0; i < tickCount; i++)
        {
            ticks[i] = new NativeReplayTick
            {
                Pre = snapshots[i],
                Post = snapshots[i + 1],
                WeaponDefIndex = bodyReader.ReadInt32(),
                NumSubtick = bodyReader.ReadUInt32()
            };
            expectedSubticks += ticks[i].NumSubtick;
        }

        if (expectedSubticks != subtickCount)
            throw new InvalidDataException($"tick subtick sum {expectedSubticks} != header subtick count {subtickCount}");

        var projectiles = new ReplayProjectileEvent[projectileCount];
        for (var i = 0; i < projectileCount; i++)
            projectiles[i] = ReadProjectileEvent(bodyReader);

        var highFidelity = ReplayHighFidelityMetadata.Empty;
        if (metadataJsonLength > 0)
        {
            var metadataJson = bodyReader.ReadBytes(metadataJsonLength);
            if (metadataJson.Length != metadataJsonLength)
                throw new EndOfStreamException("truncated high_fidelity metadata in .dtr");
            highFidelity = ReadHighFidelityMetadata(metadataJson);
        }

        var subticks = new NativeSubtickMove[subtickCount];
        for (var i = 0; i < subtickCount; i++)
        {
            subticks[i] = new NativeSubtickMove
            {
                When = bodyReader.ReadSingle(),
                Button = bodyReader.ReadUInt32(),
                Pressed = bodyReader.ReadSingle(),
                AnalogForward = bodyReader.ReadSingle(),
                AnalogLeft = bodyReader.ReadSingle(),
                PitchDelta = bodyReader.ReadSingle(),
                YawDelta = bodyReader.ReadSingle()
            };
        }

        if (bodyStream.Position != bodyStream.Length)
            throw new InvalidDataException("trailing bytes in .dtr body");

        return new DtrReplayFile(
            version,
            ticks,
            MergeProjectileMetadata(projectiles, highFidelity),
            highFidelity,
            subticks,
            [],
            [],
            tickRate,
            (uint)playStartTickIndex);
    }

    private static DtrReplayFile ReadV7Sections(
        BinaryReader reader,
        uint version,
        float tickRate,
        int tickCount,
        int subtickCount,
        int projectileCount,
        int playStartTickIndex,
        int metadataJsonLength)
    {
        var sectionCount = CheckedCount(reader.ReadUInt32(), "section_count");
        var snapshotCount = tickCount == 0 ? 0 : checked(tickCount + 1);
        NativeMovementSnapshot[]? snapshots = null;
        TickMetadata[]? tickMetadata = null;
        ReplayProjectileEvent[]? projectiles = null;
        ReplayHighFidelityMetadata highFidelity = ReplayHighFidelityMetadata.Empty;
        NativeSubtickMove[]? subticks = null;
        NativeReplayCommandFrame[]? commandFrames = null;
        NativeReplayMovementExtra[]? movementExtras = null;
        var seenHighFidelity = false;

        for (var i = 0; i < sectionCount; i++)
        {
            var header = ReadSectionHeader(reader);
            if (!IsKnownSection(header.SectionId))
            {
                SkipExact(reader, header.CompressedLength);
                continue;
            }

            var compressed = ReadExact(reader, header.CompressedLength, "v7 section payload");
            var body = DecodeSectionBody(compressed, header.Codec, header.UncompressedLength);

            switch (header.SectionId)
            {
                case SectionSnapshots:
                    RejectDuplicate(snapshots is not null, "snapshots");
                    RequireSectionShape(
                        header,
                        "snapshots",
                        snapshotCount,
                        checked(snapshotCount * BotControllerNative.MovementSnapshotByteSize));
                    snapshots = ReadSnapshotsFromSection(body, snapshotCount);
                    break;
                case SectionTickMetadata:
                    RejectDuplicate(tickMetadata is not null, "tick metadata");
                    RequireSectionShape(
                        header,
                        "tick metadata",
                        tickCount,
                        checked(tickCount * TickMetadataByteSize));
                    tickMetadata = ReadTickMetadataFromSection(body, tickCount);
                    break;
                case SectionSubticks:
                    RejectDuplicate(subticks is not null, "subticks");
                    RequireSectionShape(
                        header,
                        "subticks",
                        subtickCount,
                        checked(subtickCount * BotControllerNative.SubtickMoveByteSize));
                    subticks = ReadSubticksFromSection(body, subtickCount);
                    break;
                case SectionProjectiles:
                    RejectDuplicate(projectiles is not null, "projectiles");
                    RequireSectionShape(
                        header,
                        "projectiles",
                        projectileCount,
                        checked(projectileCount * ProjectileEventByteSize));
                    projectiles = ReadProjectilesFromSection(body, projectileCount);
                    break;
                case SectionHighFidelityJson:
                    RejectDuplicate(seenHighFidelity, "high fidelity metadata");
                    RequireSectionShape(
                        header,
                        "high fidelity metadata",
                        metadataJsonLength == 0 ? 0 : 1,
                        metadataJsonLength);
                    highFidelity = metadataJsonLength == 0
                        ? ReplayHighFidelityMetadata.Empty
                        : ReadHighFidelityMetadata(body);
                    seenHighFidelity = true;
                    break;
                case SectionCommandFrames:
                    RejectDuplicate(commandFrames is not null, "command frames");
                    RequireSectionShape(
                        header,
                        "command frames",
                        tickCount,
                        checked(tickCount * BotControllerNative.ReplayCommandFrameByteSize));
                    commandFrames = ReadCommandFramesFromSection(body, tickCount);
                    break;
                case SectionMovementExtras:
                    RejectDuplicate(movementExtras is not null, "movement extras");
                    RequireSectionShape(
                        header,
                        "movement extras",
                        tickCount,
                        checked(tickCount * BotControllerNative.ReplayMovementExtraByteSize));
                    movementExtras = ReadMovementExtrasFromSection(body, tickCount);
                    break;
            }
        }

        if (snapshots is null)
            throw new InvalidDataException("missing required v7 section snapshots");
        if (tickMetadata is null)
            throw new InvalidDataException("missing required v7 section tick metadata");
        if (subticks is null)
            throw new InvalidDataException("missing required v7 section subticks");
        if (projectileCount > 0 && projectiles is null)
            throw new InvalidDataException("missing required v7 section projectiles");
        if (metadataJsonLength > 0 && !seenHighFidelity)
            throw new InvalidDataException("missing required v7 section high fidelity metadata");

        var ticks = new NativeReplayTick[tickCount];
        long expectedSubticks = 0;
        for (var i = 0; i < tickCount; i++)
        {
            ticks[i] = new NativeReplayTick
            {
                Pre = snapshots[i],
                Post = snapshots[i + 1],
                WeaponDefIndex = tickMetadata[i].WeaponDefIndex,
                NumSubtick = tickMetadata[i].NumSubtick
            };
            expectedSubticks += tickMetadata[i].NumSubtick;
        }

        if (expectedSubticks != subtickCount)
            throw new InvalidDataException($"tick subtick sum {expectedSubticks} != header subtick count {subtickCount}");

        return new DtrReplayFile(
            version,
            ticks,
            MergeProjectileMetadata(projectiles ?? [], highFidelity),
            highFidelity,
            subticks,
            commandFrames ?? [],
            movementExtras ?? [],
            tickRate,
            (uint)playStartTickIndex);
    }

    private static void ValidatePlayStartTickIndex(int tickCount, int playStartTickIndex)
    {
        if (tickCount == 0)
        {
            if (playStartTickIndex == 0)
                return;
            throw new InvalidDataException(
                $"play_start_tick_index {playStartTickIndex} requires at least one tick");
        }
        if (playStartTickIndex >= tickCount)
            throw new InvalidDataException(
                $"play_start_tick_index {playStartTickIndex} out of range for {tickCount} ticks");
    }

    private static int CheckedCount(uint value, string fieldName)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"{fieldName} too large: {value}");
        return (int)value;
    }

    private static int CheckedLength(ulong value, string fieldName)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"{fieldName} too large: {value}");
        return (int)value;
    }

    private static DtrSectionHeader ReadSectionHeader(BinaryReader reader)
    {
        var sectionId = reader.ReadUInt32();
        var sectionVersion = reader.ReadUInt32();
        var codec = reader.ReadByte();
        _ = reader.ReadByte(); // pad
        _ = reader.ReadUInt16(); // pad
        _ = reader.ReadUInt32(); // flags
        var elementCount = CheckedCount(reader.ReadUInt32(), "section_element_count");
        var uncompressedLength = CheckedLength(reader.ReadUInt64(), "section_uncompressed_len");
        var compressedLength = CheckedLength(reader.ReadUInt64(), "section_compressed_len");
        return new DtrSectionHeader(
            sectionId,
            sectionVersion,
            codec,
            elementCount,
            uncompressedLength,
            compressedLength);
    }

    private static bool IsKnownSection(uint sectionId)
        => sectionId is SectionSnapshots
            or SectionTickMetadata
            or SectionProjectiles
            or SectionHighFidelityJson
            or SectionSubticks
            or SectionCommandFrames
            or SectionMovementExtras;

    private static void RequireSectionShape(
        DtrSectionHeader header,
        string name,
        int expectedElementCount,
        int expectedUncompressedLength)
    {
        if (header.SectionVersion != SectionVersionV1)
            throw new InvalidDataException($"unsupported {name} section version {header.SectionVersion}");
        if (header.ElementCount != expectedElementCount)
            throw new InvalidDataException(
                $"{name} section count {header.ElementCount} != expected {expectedElementCount}");
        if (header.UncompressedLength != expectedUncompressedLength)
            throw new InvalidDataException(
                $"{name} section length {header.UncompressedLength} != expected {expectedUncompressedLength}");
    }

    private static void RejectDuplicate(bool seen, string name)
    {
        if (seen)
            throw new InvalidDataException($"duplicate v7 section {name}");
    }

    private static byte[] DecodeSectionBody(byte[] compressed, byte codec, int expectedLength)
    {
        return codec switch
        {
            SectionCodecNone => RequireExactLength(compressed, expectedLength, "uncompressed v7 section"),
            RecCodecBrotli => DecompressBrotli(compressed, expectedLength),
            _ => throw new InvalidDataException($"unsupported v7 section codec {codec}")
        };
    }

    private static byte[] RequireExactLength(byte[] bytes, int expectedLength, string name)
    {
        if (bytes.Length != expectedLength)
            throw new InvalidDataException($"{name} length {bytes.Length} != expected {expectedLength}");
        return bytes;
    }

    private static byte[] ReadExact(BinaryReader reader, int length, string name)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException($"truncated {name}");
        return bytes;
    }

    private static void SkipExact(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("truncated skipped v7 section");
    }

    private static int ExpectedBodyLength(int tickCount, int subtickCount, int projectileCount, int metadataJsonLength)
    {
        var snapshotCount = tickCount == 0 ? 0 : checked(tickCount + 1);
        return checked(
            snapshotCount * BotControllerNative.MovementSnapshotByteSize +
            tickCount * TickMetadataByteSize +
            projectileCount * ProjectileEventByteSize +
            metadataJsonLength +
            subtickCount * BotControllerNative.SubtickMoveByteSize);
    }

    private static byte[] DecompressBrotli(byte[] compressed, int expectedLength)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        var output = GC.AllocateUninitializedArray<byte>(expectedLength);
        var totalRead = 0;
        while (totalRead < output.Length)
        {
            var read = brotli.Read(output, totalRead, output.Length - totalRead);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"decompressed body length {totalRead} != expected {expectedLength}");
            }
            totalRead += read;
        }

        if (brotli.ReadByte() != -1)
            throw new InvalidDataException($"decompressed body exceeds expected length {expectedLength}");
        return output;
    }

    private static NativeMovementSnapshot ReadCurrentSnapshot(BinaryReader reader)
    {
        return new NativeMovementSnapshot
        {
            OriginX = reader.ReadSingle(),
            OriginY = reader.ReadSingle(),
            OriginZ = reader.ReadSingle(),
            VelX = reader.ReadSingle(),
            VelY = reader.ReadSingle(),
            VelZ = reader.ReadSingle(),
            Pitch = reader.ReadSingle(),
            Yaw = reader.ReadSingle(),
            Roll = reader.ReadSingle(),
            EntityFlags = reader.ReadUInt32(),
            MoveType = reader.ReadByte(),
            Pad0 = reader.ReadByte(),
            Pad1 = reader.ReadByte(),
            Pad2 = reader.ReadByte(),
            Buttons = reader.ReadUInt64(),
            Buttons1 = reader.ReadUInt64(),
            Buttons2 = reader.ReadUInt64(),
            DuckAmount = reader.ReadSingle(),
            DuckSpeed = reader.ReadSingle(),
            LadderNormalX = reader.ReadSingle(),
            LadderNormalY = reader.ReadSingle(),
            LadderNormalZ = reader.ReadSingle(),
            Ducked = reader.ReadByte(),
            Ducking = reader.ReadByte(),
            DesiresDuck = reader.ReadByte(),
            ActualMoveType = reader.ReadByte()
        };
    }

    private static NativeMovementSnapshot[] ReadSnapshotsFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var snapshots = new NativeMovementSnapshot[count];
        for (var i = 0; i < count; i++)
            snapshots[i] = ReadCurrentSnapshot(reader);
        RequireConsumed(stream, "snapshots");
        return snapshots;
    }

    private static TickMetadata[] ReadTickMetadataFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var metadata = new TickMetadata[count];
        for (var i = 0; i < count; i++)
        {
            metadata[i] = new TickMetadata(
                reader.ReadInt32(),
                reader.ReadUInt32());
        }
        RequireConsumed(stream, "tick metadata");
        return metadata;
    }

    private static ReplayProjectileEvent[] ReadProjectilesFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var projectiles = new ReplayProjectileEvent[count];
        for (var i = 0; i < count; i++)
            projectiles[i] = ReadProjectileEvent(reader);
        RequireConsumed(stream, "projectiles");
        return projectiles;
    }

    private static NativeSubtickMove[] ReadSubticksFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var subticks = new NativeSubtickMove[count];
        for (var i = 0; i < count; i++)
            subticks[i] = ReadSubtickMove(reader);
        RequireConsumed(stream, "subticks");
        return subticks;
    }

    private static NativeSubtickMove ReadSubtickMove(BinaryReader reader)
    {
        return new NativeSubtickMove
        {
            When = reader.ReadSingle(),
            Button = reader.ReadUInt32(),
            Pressed = reader.ReadSingle(),
            AnalogForward = reader.ReadSingle(),
            AnalogLeft = reader.ReadSingle(),
            PitchDelta = reader.ReadSingle(),
            YawDelta = reader.ReadSingle()
        };
    }

    private static NativeReplayCommandFrame[] ReadCommandFramesFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var frames = new NativeReplayCommandFrame[count];
        for (var i = 0; i < count; i++)
        {
            frames[i] = new NativeReplayCommandFrame
            {
                ForwardMove = reader.ReadSingle(),
                LeftMove = reader.ReadSingle(),
                UpMove = reader.ReadSingle(),
                Pitch = reader.ReadSingle(),
                Yaw = reader.ReadSingle(),
                Roll = reader.ReadSingle(),
                Buttons = reader.ReadUInt64(),
                Buttons1 = reader.ReadUInt64(),
                Buttons2 = reader.ReadUInt64(),
                MouseDx = reader.ReadInt32(),
                MouseDy = reader.ReadInt32(),
                WeaponSelect = reader.ReadInt32(),
                Fields = reader.ReadUInt32(),
                LeftHandDesired = reader.ReadByte(),
                Pad0 = reader.ReadByte(),
                Pad1 = reader.ReadByte(),
                Pad2 = reader.ReadByte()
            };
        }
        RequireConsumed(stream, "command frames");
        return frames;
    }

    private static NativeReplayMovementExtra[] ReadMovementExtrasFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var extras = new NativeReplayMovementExtra[count];
        for (var i = 0; i < count; i++)
        {
            extras[i] = new NativeReplayMovementExtra
            {
                Fields = reader.ReadUInt32(),
                JumpPressedTime = reader.ReadSingle(),
                LastDuckTime = reader.ReadSingle(),
                LastActualJumpPressTick = reader.ReadInt32(),
                LastActualJumpPressFrac = reader.ReadSingle(),
                LastUsableJumpPressTick = reader.ReadInt32(),
                LastUsableJumpPressFrac = reader.ReadSingle(),
                LastLandedTick = reader.ReadInt32(),
                LastLandedFrac = reader.ReadSingle(),
                LastLandedVelocityX = reader.ReadSingle(),
                LastLandedVelocityY = reader.ReadSingle(),
                LastLandedVelocityZ = reader.ReadSingle()
            };
        }
        RequireConsumed(stream, "movement extras");
        return extras;
    }

    private static void RequireConsumed(Stream stream, string name)
    {
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"trailing bytes in v7 {name} section");
    }

    private static ReplayProjectileEvent ReadProjectileEvent(BinaryReader reader)
    {
        var tickIndex = reader.ReadUInt32();
        var weaponDefIndex = reader.ReadInt32();
        var kind = (ReplayProjectileKind)reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        var initialPosition = new ReplayVector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        var initialVelocity = new ReplayVector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        var detonationPosition = new ReplayVector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        return new ReplayProjectileEvent(
            tickIndex,
            kind,
            weaponDefIndex,
            initialPosition,
            initialVelocity,
            detonationPosition,
            new ReplayVector3(0.0f, 0.0f, 0.0f),
            -1,
            string.Empty,
            0.0f);
    }

    private static ReplayHighFidelityMetadata ReadHighFidelityMetadata(byte[] metadataJson)
    {
        var metadata = JsonSerializer.Deserialize<ReplayHighFidelityMetadata>(metadataJson, HifiJsonOptions)
            ?? ReplayHighFidelityMetadata.Empty;
        metadata.Events ??= [];
        metadata.InventorySnapshots ??= [];
        metadata.Projectiles ??= [];
        return metadata;
    }

    private static ReplayProjectileEvent[] MergeProjectileMetadata(
        ReplayProjectileEvent[] projectiles,
        ReplayHighFidelityMetadata highFidelity)
    {
        var metadata = highFidelity.Projectiles ?? [];
        if (projectiles.Length == 0 || metadata.Length == 0)
            return projectiles;

        var used = new bool[metadata.Length];
        var merged = new ReplayProjectileEvent[projectiles.Length];
        for (var i = 0; i < projectiles.Length; i++)
        {
            var projectile = projectiles[i];
            var match = -1;
            for (var j = 0; j < metadata.Length; j++)
            {
                if (used[j])
                    continue;
                if (!ProjectileMetadataMatches(projectile, metadata[j]))
                    continue;
                match = j;
                break;
            }

            if (match < 0)
            {
                merged[i] = projectile;
                continue;
            }

            used[match] = true;
            var item = metadata[match];
            merged[i] = projectile with
            {
                EffectPosition = ReadMetadataVector(item.EffectPosition),
                EffectTickIndex = ReadMetadataTickIndex(item.EffectTickIndex),
                EffectSource = item.EffectSource ?? string.Empty,
                EffectConfidence = item.EffectConfidence
            };
        }

        return merged;
    }

    private static bool ProjectileMetadataMatches(
        ReplayProjectileEvent projectile,
        ReplayProjectileMetadata metadata)
    {
        return metadata.TickIndex == projectile.TickIndex &&
               ProjectileKindFromString(metadata.Kind) == projectile.Kind &&
               metadata.WeaponDefIndex == projectile.WeaponDefIndex;
    }

    private static ReplayProjectileKind ProjectileKindFromString(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "smoke" => ReplayProjectileKind.Smoke,
            "flash" => ReplayProjectileKind.Flash,
            "he" => ReplayProjectileKind.He,
            "molotov" or "incgrenade" or "incendiary" => ReplayProjectileKind.Molotov,
            "decoy" => ReplayProjectileKind.Decoy,
            _ => ReplayProjectileKind.Unknown
        };
    }

    private static ReplayVector3 ReadMetadataVector(float[]? values)
    {
        return values is { Length: >= 3 }
            ? new ReplayVector3(values[0], values[1], values[2])
            : new ReplayVector3(0.0f, 0.0f, 0.0f);
    }

    private static int ReadMetadataTickIndex(uint? value)
        => value.HasValue && value.Value <= int.MaxValue ? (int)value.Value : -1;

    private static string ReadRecString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len)
            throw new EndOfStreamException("truncated string in .dtr");
        return Encoding.UTF8.GetString(bytes);
    }
}

internal readonly record struct DtrSectionHeader(
    uint SectionId,
    uint SectionVersion,
    byte Codec,
    int ElementCount,
    int UncompressedLength,
    int CompressedLength);

internal readonly record struct TickMetadata(int WeaponDefIndex, uint NumSubtick);

internal readonly record struct DtrReplayFile(
    uint Version,
    NativeReplayTick[] Ticks,
    ReplayProjectileEvent[] Projectiles,
    ReplayHighFidelityMetadata HighFidelity,
    NativeSubtickMove[] Subticks,
    NativeReplayCommandFrame[] CommandFrames,
    NativeReplayMovementExtra[] MovementExtras,
    float TickRate,
    uint PlayStartTickIndex);
