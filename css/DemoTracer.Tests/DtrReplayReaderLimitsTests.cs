using System.IO.Compression;
using System.Text;

namespace DemoTracer.Tests;

public sealed class DtrReplayReaderLimitsTests : IDisposable
{
    private const byte CodecNone = 0;
    private const byte CodecBrotli = 1;
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"demotracer-reader-tests-{Guid.NewGuid():N}");

    [Fact]
    public void DefaultLimitsAreGenerousButFinite()
    {
        var limits = DtrReadLimits.Default;

        Assert.Equal(64L * 1024 * 1024, limits.MaxFileBytes);
        Assert.Equal(32, limits.MaxSectionCount);
        Assert.Equal(48L * 1024 * 1024, limits.MaxCompressedSectionBytes);
        Assert.Equal(64L * 1024 * 1024, limits.MaxTotalCompressedBytes);
        Assert.Equal(48L * 1024 * 1024, limits.MaxDecodedSectionBytes);
        Assert.Equal(64L * 1024 * 1024, limits.MaxTotalDecodedBytes);
        Assert.Equal(32_768, limits.MaxTickCount);
        Assert.Equal(1_179_648, limits.MaxSubtickCount);
        Assert.Equal(36, limits.MaxSubticksPerTick);
        Assert.Equal(4_096, limits.MaxProjectileCount);
        Assert.Equal(8 * 1024 * 1024, limits.MaxMetadataJsonBytes);
    }

    [Fact]
    public void RejectsFileBeforeParsingWhenItExceedsLimit()
    {
        var path = WriteFile(writer => writer.Write(new byte[32]));
        var limits = DtrReadLimits.Default with { MaxFileBytes = 16 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("file length", error.Message);
    }

    [Fact]
    public void RejectsTickCountBeforeReadingTheRestOfTheHeader()
    {
        var path = WriteFile(writer => WriteHeaderPrefix(writer, version: 7, tickCount: 2, subtickCount: 0));
        var limits = DtrReadLimits.Default with { MaxTickCount = 1 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("tick_count 2 exceeds limit 1", error.Message);
    }

    [Fact]
    public void RejectsImpossibleHeaderSubtickRatio()
    {
        var path = WriteFile(writer => WriteHeaderPrefix(writer, version: 3, tickCount: 1, subtickCount: 37));

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("exceeds 36 per tick", error.Message);
    }

    [Fact]
    public void RejectsProjectileAndMetadataCountsAtTheirLimits()
    {
        var projectilePath = WriteFile(writer =>
            WriteHeaderPrefix(writer, version: 4, tickCount: 0, subtickCount: 0, projectileCount: 2));
        var metadataPath = WriteFile(writer =>
            WriteHeaderPrefix(writer, version: 6, tickCount: 0, subtickCount: 0, metadataJsonLength: 2));

        var projectileError = Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(
                projectilePath,
                DtrReadLimits.Default with { MaxProjectileCount = 1 }));
        var metadataError = Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(
                metadataPath,
                DtrReadLimits.Default with { MaxMetadataJsonBytes = 1 }));

        Assert.Contains("projectile_count 2 exceeds limit 1", projectileError.Message);
        Assert.Contains("metadata_json_len 2 exceeds limit 1", metadataError.Message);
    }

    [Fact]
    public void RejectsTruncatedDeclaredStringBeforeReadingItsPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteHeaderPrefix(writer, version: 3, tickCount: 0, subtickCount: 0);
            writer.Write(ushort.MaxValue);
        });

        var error = Assert.Throws<EndOfStreamException>(() => DtrReplayReader.Read(path));

        Assert.Contains("string in .dtr", error.Message);
    }

    [Fact]
    public void RejectsLegacyBodyBudgetsBeforeReadingPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 6, tickCount: 0, subtickCount: 0);
            writer.Write(CodecBrotli);
            writer.Write(0UL);
            writer.Write(11UL);
        });
        var limits = DtrReadLimits.Default with { MaxCompressedSectionBytes = 10 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("body_compressed_len 11 exceeds limit 10", error.Message);
    }

    [Fact]
    public void RejectsLegacyDecodedBodyBudgetBeforeCheckingItsShape()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 6, tickCount: 0, subtickCount: 0);
            writer.Write(CodecBrotli);
            writer.Write(11UL);
            writer.Write(0UL);
        });
        var limits = DtrReadLimits.Default with { MaxDecodedSectionBytes = 10 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("body_uncompressed_len 11 exceeds limit 10", error.Message);
    }

    [Theory]
    [InlineData(3U)]
    [InlineData(5U)]
    public void KeepsLegacyVersionsReadable(uint version)
    {
        var compressed = Compress([]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version, tickCount: 0, subtickCount: 0);
            writer.Write(CodecBrotli);
            writer.Write(0UL);
            writer.Write((ulong)compressed.Length);
            writer.Write(compressed);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(version, replay.Version);
        Assert.Empty(replay.Ticks);
        Assert.Empty(replay.Subticks);
    }

    [Fact]
    public void RejectsV7SectionCountBeforeReadingSectionHeaders()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(2U);
        });
        var limits = DtrReadLimits.Default with { MaxSectionCount = 1 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("section_count 2 exceeds limit 1", error.Message);
    }

    [Fact]
    public void RejectsKnownSectionShapeBeforeMissingPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(1U);
            WriteSectionHeader(
                writer,
                sectionId: 1,
                codec: CodecBrotli,
                elementCount: 1,
                uncompressedLength: 0,
                compressedLength: 10);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("snapshots section count 1 != expected 0", error.Message);
    }

    [Fact]
    public void ReservesBytesForRemainingV7HeadersBeforeReadingPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(2U);
            WriteSectionHeader(
                writer,
                sectionId: 99,
                codec: CodecNone,
                elementCount: 0,
                uncompressedLength: 4,
                compressedLength: 4);
            writer.Write(new byte[4]);
        });

        var error = Assert.Throws<EndOfStreamException>(() => DtrReplayReader.Read(path));

        Assert.Contains("v7 section payload and remaining headers", error.Message);
    }

    [Fact]
    public void EnforcesCumulativeV7SectionBudgets()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(2U);
            WriteSection(writer, sectionId: 99, codec: CodecNone, elementCount: 0, payload: new byte[5]);
            WriteSectionHeader(
                writer,
                sectionId: 100,
                codec: CodecNone,
                elementCount: 0,
                uncompressedLength: 5,
                compressedLength: 5);
        });
        var limits = DtrReadLimits.Default with
        {
            MaxTotalCompressedBytes = 9,
            MaxTotalDecodedBytes = 9
        };

        var decodedError = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));
        var compressedError = Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(
                path,
                limits with
                {
                    MaxTotalCompressedBytes = 9,
                    MaxTotalDecodedBytes = 100
                }));

        Assert.Contains("total section decoded bytes exceeds limit 9", decodedError.Message);
        Assert.Contains("total section compressed bytes exceeds limit 9", compressedError.Message);
    }

    [Fact]
    public void SkipsLargeUnknownSectionAndReadsRequiredEmptySections()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(4U);
            WriteSection(
                writer,
                sectionId: 99,
                codec: 255,
                elementCount: int.MaxValue,
                payload: new byte[5_000],
                uncompressedLength: 0);
            WriteSection(writer, sectionId: 1, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 5, codec: CodecNone, elementCount: 0, payload: []);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Empty(replay.Ticks);
        Assert.Empty(replay.Subticks);
    }

    [Fact]
    public void RejectsBrotliOutputBeyondDeclaredSectionLength()
    {
        var compressed = Compress([42]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(1U);
            WriteSection(
                writer,
                sectionId: 1,
                codec: CodecBrotli,
                elementCount: 0,
                payload: compressed,
                uncompressedLength: 0);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("exceeds expected length 0", error.Message);
    }

    [Fact]
    public void RejectsMoreThanThirtySixSubticksInTickMetadata()
    {
        var tickMetadata = new byte[8];
        BitConverter.GetBytes(37U).CopyTo(tickMetadata, 4);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 1, subtickCount: 36);
            writer.Write(1U);
            WriteSection(
                writer,
                sectionId: 2,
                codec: CodecNone,
                elementCount: 1,
                payload: tickMetadata);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("tick subtick count 37 exceeds limit 36", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }

    private string WriteFile(Action<BinaryWriter> write)
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.dtr");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        write(writer);
        return path;
    }

    private static void WriteCompleteHeader(
        BinaryWriter writer,
        uint version,
        uint tickCount,
        uint subtickCount,
        uint projectileCount = 0,
        uint metadataJsonLength = 0)
    {
        WriteHeaderPrefix(writer, version, tickCount, subtickCount, projectileCount, metadataJsonLength);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
    }

    private static void WriteHeaderPrefix(
        BinaryWriter writer,
        uint version,
        uint tickCount,
        uint subtickCount,
        uint projectileCount = 0,
        uint metadataJsonLength = 0)
    {
        writer.Write("CSDTRREC"u8);
        writer.Write(version);
        writer.Write(128.0f);
        writer.Write(1U);
        writer.Write((byte)0);
        writer.Write(0U);
        writer.Write(0UL);
        writer.Write(tickCount);
        writer.Write(subtickCount);
        if (version >= 4)
            writer.Write(projectileCount);
        if (version >= 5)
            writer.Write(0U);
        if (version >= 6)
            writer.Write(metadataJsonLength);
    }

    private static void WriteSection(
        BinaryWriter writer,
        uint sectionId,
        byte codec,
        int elementCount,
        byte[] payload,
        int? uncompressedLength = null)
    {
        WriteSectionHeader(
            writer,
            sectionId,
            codec,
            elementCount,
            uncompressedLength ?? payload.Length,
            payload.Length);
        writer.Write(payload);
    }

    private static void WriteSectionHeader(
        BinaryWriter writer,
        uint sectionId,
        byte codec,
        int elementCount,
        int uncompressedLength,
        int compressedLength)
    {
        writer.Write(sectionId);
        writer.Write(1U);
        writer.Write(codec);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write(0U);
        writer.Write((uint)elementCount);
        writer.Write((ulong)uncompressedLength);
        writer.Write((ulong)compressedLength);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(bytes);
        return output.ToArray();
    }
}
