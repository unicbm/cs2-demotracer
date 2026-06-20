using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace DemoTracer;

internal static class BotControllerNative
{
    public const int ExpectedAbiVersion = 15;
    public const uint RecFormatVersion = 5;
    public const uint MinRecFormatVersion = 3;
    public const int MovementSnapshotByteSize = 92;
    public const int ReplayTickByteSize = 192;
    public const int MaxSlots = 64;

    private const byte RecCodecBrotli = 1;
    private const int TickMetadataByteSize = 8;
    private const int ProjectileEventByteSize = 48;
    private const int SubtickMoveByteSize = 28;
    private const int LockKindAll = 0;
    private const int LockKindAim = 1;
    private const int LockKindWeapon = 2;
    private const int LockKindJump = 3;

    private static readonly byte[] RecMagic =
    [
        (byte)'C', (byte)'S', (byte)'D', (byte)'T',
        (byte)'R', (byte)'R', (byte)'E', (byte)'C'
    ];

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_Lock(int slot, int kind, int arg);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_Unlock(int slot, int kind);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetVersion();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetControllerControllingBotOffset(int offset);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetReplayPovMask(ulong mask);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_LoadReplay(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StartReplay(int slot, int loop);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StartReplayAt(int slot, int loop, int startIndex);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StartReplayUntil(
        int slot,
        int loop,
        int startIndex,
        int holdBeforeIndex);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StopReplay(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayCursor(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayTotal(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplaySlotState(
        int slot,
        out NativeReplaySlotState state);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayTick(int slot, out NativeReplayTick tick);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SwitchBotWeapon(int slot, int defIndex);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetBotActiveWeaponDef(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetBuyPlan(
        int slot,
        [MarshalAs(UnmanagedType.LPStr)] string aliases);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SetBuySkip(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearBuyPlan(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearAllBuyPlans();

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetBuyPlanItemCount(int slot);

    public static string LastLoadError { get; private set; } = string.Empty;

    public static int AbiVersion
    {
        get
        {
            try
            {
                return BotController_GetVersion();
            }
            catch
            {
                return -1;
            }
        }
    }

    public static bool IsCompatible => AbiVersion == ExpectedAbiVersion;

    public static bool SetControllerControllingBotOffset(int offset)
    {
        try
        {
            return BotController_SetControllerControllingBotOffset(offset) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetReplayPovMask(ulong mask)
    {
        try
        {
            return BotController_SetReplayPovMask(mask) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool LoadReplayFromFile(int slot, string path)
        => LoadReplayFromFile(slot, path, out _);

    public static bool LoadReplayFromFile(int slot, string path, out ReplayFileMetadata metadata)
    {
        metadata = ReplayFileMetadata.Empty;
        if (!ValidSlot(slot))
        {
            LastLoadError = $"slot {slot} out of range 0..{MaxSlots - 1}";
            return false;
        }

        try
        {
            EnsureNativeLayout();
            var replay = ReadReplayFile(path);
            metadata = BuildReplayMetadata(replay);
            if (replay.Ticks.Length == 0)
            {
                LastLoadError = "replay has no ticks";
                return false;
            }

            var subticks = replay.Subticks.Length == 0
                ? [new NativeSubtickMove()]
                : replay.Subticks;
            var ok = BotController_LoadReplay(
                slot,
                replay.Ticks,
                replay.Ticks.Length,
                subticks,
                replay.Subticks.Length) == 0;
            LastLoadError = ok ? string.Empty : "BotController_LoadReplay failed";
            return ok;
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return false;
        }
    }

    public static bool TryReadReplayMetadata(string path, out ReplayFileMetadata metadata)
    {
        try
        {
            var replay = ReadReplayFile(path);
            metadata = BuildReplayMetadata(replay);
            return true;
        }
        catch
        {
            metadata = ReplayFileMetadata.Empty;
            return false;
        }
    }

    public static bool UnloadReplay(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        StopReplay(slot);
        LastLoadError = string.Empty;
        return true;
    }

    public static bool StartReplay(int slot, bool loop)
        => StartReplayAt(slot, loop, 0);

    public static bool StartReplayAt(int slot, bool loop, uint startIndex)
    {
        if (!ValidSlot(slot))
            return false;
        if (BotController_Lock(slot, LockKindAll, 0) != 0)
            return false;

        var ok = startIndex == 0
            ? BotController_StartReplay(slot, loop ? 1 : 0) == 0
            : BotController_StartReplayAt(slot, loop ? 1 : 0, checked((int)startIndex)) == 0;
        if (!ok)
            BotController_Unlock(slot, LockKindAll);
        return ok;
    }

    public static bool StartReplayUntil(
        int slot,
        bool loop,
        uint startIndex,
        uint holdBeforeIndex)
    {
        if (!ValidSlot(slot))
            return false;
        if (holdBeforeIndex <= startIndex)
            return false;
        if (BotController_Lock(slot, LockKindAll, 0) != 0)
            return false;

        var ok = BotController_StartReplayUntil(
            slot,
            loop ? 1 : 0,
            checked((int)startIndex),
            checked((int)holdBeforeIndex)) == 0;
        if (!ok)
            BotController_Unlock(slot, LockKindAll);
        return ok;
    }

    public static bool StopReplay(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        var ok = BotController_StopReplay(slot) == 0;
        BotController_Unlock(slot, LockKindAll);
        return ok;
    }

    public static ReplayState GetReplayState(int slot)
    {
        if (!ValidSlot(slot))
            return ReplayState.Empty;

        try
        {
            if (BotController_GetReplaySlotState(slot, out var state) == 0)
            {
                return new ReplayState(
                    state.Cursor,
                    state.Total,
                    state.Playing != 0,
                    state.CurrentTickIndex,
                    state.WeaponDefIndex,
                    state.NumSubtick);
            }
        }
        catch
        {
        }

        var cursor = BotController_GetReplayCursor(slot);
        var total = BotController_GetReplayTotal(slot);
        return new ReplayState(cursor, total, cursor >= 0, -1, -1, 0);
    }

    public static bool TryGetReplayTick(int slot, out NativeReplayTick tick)
    {
        tick = default;
        return ValidSlot(slot) && BotController_GetReplayTick(slot, out tick) == 0;
    }

    public static bool SwitchBotWeapon(int slot, int defIndex)
        => ValidSlot(slot) && BotController_SwitchBotWeapon(slot, defIndex) == 0;

    public static int BotActiveWeaponDef(int slot)
        => ValidSlot(slot) ? BotController_GetBotActiveWeaponDef(slot) : -1;

    public static bool SetBuyPlan(int slot, string aliases)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_SetBuyPlan(slot, aliases ?? string.Empty) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetBuySkip(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_SetBuySkip(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearBuyPlan(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        try
        {
            return BotController_ClearBuyPlan(slot) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearAllBuyPlans()
    {
        try
        {
            return BotController_ClearAllBuyPlans() == 0;
        }
        catch
        {
            return false;
        }
    }

    public static int BuyPlanItemCount(int slot)
    {
        if (!ValidSlot(slot))
            return -1;
        try
        {
            return BotController_GetBuyPlanItemCount(slot);
        }
        catch
        {
            return -1;
        }
    }

    public static bool LockWeaponSlot(int slot, int target)
        => ValidSlot(slot) && target is >= 1 and <= 5 && BotController_Lock(slot, LockKindWeapon, target) == 0;

    public static bool UnlockWeaponSlot(int slot)
        => ValidSlot(slot) && BotController_Unlock(slot, LockKindWeapon) == 0;

    public static void UnlockReplayControl(int slot)
    {
        if (!ValidSlot(slot))
            return;
        BotController_Unlock(slot, LockKindAll);
        BotController_Unlock(slot, LockKindAim);
        BotController_Unlock(slot, LockKindJump);
    }

    private static bool ValidSlot(int slot)
        => slot is >= 0 and < MaxSlots;

    private static ReplayFile ReadReplayFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".dtr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("expected .dtr replay file");

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(RecMagic.Length);
        if (!magic.SequenceEqual(RecMagic))
            throw new InvalidDataException("bad .dtr magic");

        var version = reader.ReadUInt32();
        if (version is < MinRecFormatVersion or > RecFormatVersion)
            throw new InvalidDataException(
                $"unsupported .dtr version {version}; expected {MinRecFormatVersion}..{RecFormatVersion}");

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
        ValidatePlayStartTickIndex(tickCount, playStartTickIndex);
        _ = ReadRecString(reader); // map
        _ = ReadRecString(reader); // player name

        var codec = reader.ReadByte();
        if (codec != RecCodecBrotli)
            throw new InvalidDataException($"unsupported .dtr codec {codec}");

        var bodyUncompressedLength = CheckedLength(reader.ReadUInt64(), "body_uncompressed_len");
        var bodyCompressedLength = CheckedLength(reader.ReadUInt64(), "body_compressed_len");
        var expectedBodyLength = ExpectedBodyLength(tickCount, subtickCount, projectileCount);
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

        return new ReplayFile(ticks, projectiles, subticks, tickRate, (uint)playStartTickIndex);
    }

    private static ReplayFileMetadata BuildReplayMetadata(ReplayFile replay)
    {
        var weaponDefIndices = new int[replay.Ticks.Length];
        for (var i = 0; i < replay.Ticks.Length; i++)
            weaponDefIndices[i] = replay.Ticks[i].WeaponDefIndex;
        return new ReplayFileMetadata(
            replay.TickRate,
            replay.PlayStartTickIndex,
            replay.Projectiles,
            weaponDefIndices);
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

    private static int ExpectedBodyLength(int tickCount, int subtickCount, int projectileCount)
    {
        var snapshotCount = tickCount == 0 ? 0 : checked(tickCount + 1);
        return checked(
            snapshotCount * MovementSnapshotByteSize +
            tickCount * TickMetadataByteSize +
            projectileCount * ProjectileEventByteSize +
            subtickCount * SubtickMoveByteSize);
    }

    private static byte[] DecompressBrotli(byte[] compressed, int expectedLength)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        brotli.CopyTo(output);
        if (output.Length != expectedLength)
            throw new InvalidDataException($"decompressed body length {output.Length} != expected {expectedLength}");
        return output.ToArray();
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
            detonationPosition);
    }

    private static string ReadRecString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len)
            throw new EndOfStreamException("truncated string in .dtr");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static void EnsureNativeLayout()
    {
        var snapshotSize = Marshal.SizeOf<NativeMovementSnapshot>();
        if (snapshotSize != MovementSnapshotByteSize)
            throw new InvalidOperationException($"MovementSnapshot layout is {snapshotSize}, expected {MovementSnapshotByteSize}");

        var tickSize = Marshal.SizeOf<NativeReplayTick>();
        if (tickSize != ReplayTickByteSize)
            throw new InvalidOperationException($"ReplayTick layout is {tickSize}, expected {ReplayTickByteSize}");
    }

    private readonly record struct ReplayFile(
        NativeReplayTick[] Ticks,
        ReplayProjectileEvent[] Projectiles,
        NativeSubtickMove[] Subticks,
        float TickRate,
        uint PlayStartTickIndex);
}

internal readonly record struct ReplayFileMetadata(
    float TickRate,
    uint PlayStartTickIndex,
    ReplayProjectileEvent[] Projectiles,
    int[] WeaponDefIndices)
{
    public static ReplayFileMetadata Empty { get; } = new(0.0f, 0, [], []);
}

internal readonly record struct ReplayState(
    int Cursor,
    int Total,
    bool Playing,
    int CurrentTickIndex,
    int WeaponDefIndex,
    int NumSubtick)
{
    public static ReplayState Empty { get; } = new(-1, 0, false, -1, -1, 0);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplaySlotState
{
    public int Playing;
    public int Cursor;
    public int Total;
    public int CurrentTickIndex;
    public int WeaponDefIndex;
    public int NumSubtick;
}

internal enum ReplayProjectileKind : byte
{
    Unknown = 0,
    Smoke = 1,
    Flash = 2,
    He = 3,
    Molotov = 4,
    Decoy = 5
}

internal readonly record struct ReplayVector3(float X, float Y, float Z);

internal readonly record struct ReplayProjectileEvent(
    uint TickIndex,
    ReplayProjectileKind Kind,
    int WeaponDefIndex,
    ReplayVector3 InitialPosition,
    ReplayVector3 InitialVelocity,
    ReplayVector3 DetonationPosition);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeMovementSnapshot
{
    public float OriginX, OriginY, OriginZ;
    public float VelX, VelY, VelZ;
    public float Pitch, Yaw, Roll;
    public uint EntityFlags;
    public byte MoveType;
    public byte Pad0, Pad1, Pad2;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public float DuckAmount;
    public float DuckSpeed;
    public float LadderNormalX;
    public float LadderNormalY;
    public float LadderNormalZ;
    public byte Ducked;
    public byte Ducking;
    public byte DesiresDuck;
    public byte ActualMoveType;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayTick
{
    public NativeMovementSnapshot Pre;
    public NativeMovementSnapshot Post;
    public int WeaponDefIndex;
    public uint NumSubtick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeSubtickMove
{
    public float When;
    public uint Button;
    public float Pressed;
    public float AnalogForward;
    public float AnalogLeft;
    public float PitchDelta;
    public float YawDelta;
}

public sealed partial class DemoTracerPlugin
{
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
}
