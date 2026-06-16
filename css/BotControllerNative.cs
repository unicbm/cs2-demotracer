using System.Runtime.InteropServices;

namespace Cs2DemoBotMimic;

internal static class BotControllerNative
{
    public const int ExpectedAbiVersion = 10;
    public const uint RecFormatVersion = 2;
    public const uint LegacyRecFormatVersion = 1;
    public const int MovementSnapshotByteSize = 92;
    public const int ReplayTickByteSize = 192;

    private const int LockKindAll = 0;
    private const int LockKindAim = 1;
    private const int LockKindWeapon = 2;
    private const int LockKindJump = 3;

    private static readonly byte[] RecMagic =
    [
        (byte)'C', (byte)'S', (byte)'2', (byte)'B',
        (byte)'M', (byte)'R', (byte)'E', (byte)'C'
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
    private static extern int BotController_LoadReplay(
        int slot,
        [In] NativeReplayTick[] ticks,
        int tickCount,
        [In] NativeSubtickMove[] subs,
        int subCount);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StartReplay(int slot, int loop);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_StopReplay(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayCursor(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayTotal(int slot);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetReplayTick(int slot, out NativeReplayTick tick);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_SwitchBotWeapon(int slot, int defIndex);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_GetBotActiveWeaponDef(int slot);

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

    public static bool LoadReplayFromFile(int slot, string path)
    {
        try
        {
            EnsureNativeLayout();
            var replay = ReadReplayFile(path);
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

    public static bool UnloadReplay(int slot)
    {
        StopReplay(slot);
        LastLoadError = string.Empty;
        return true;
    }

    public static bool StartReplay(int slot, bool loop)
    {
        if (BotController_Lock(slot, LockKindAll, 0) != 0)
            return false;

        var ok = BotController_StartReplay(slot, loop ? 1 : 0) == 0;
        if (!ok)
            BotController_Unlock(slot, LockKindAll);
        return ok;
    }

    public static bool StopReplay(int slot)
    {
        var ok = BotController_StopReplay(slot) == 0;
        BotController_Unlock(slot, LockKindAll);
        return ok;
    }

    public static ReplayState GetReplayState(int slot)
    {
        var cursor = BotController_GetReplayCursor(slot);
        var total = BotController_GetReplayTotal(slot);
        return new ReplayState(cursor, total, cursor >= 0);
    }

    public static bool TryGetReplayTick(int slot, out NativeReplayTick tick)
        => BotController_GetReplayTick(slot, out tick) == 0;

    public static bool SwitchBotWeapon(int slot, int defIndex)
        => BotController_SwitchBotWeapon(slot, defIndex) == 0;

    public static int BotActiveWeaponDef(int slot)
        => BotController_GetBotActiveWeaponDef(slot);

    public static bool LockWeaponSlot(int slot, int target)
        => target is >= 1 and <= 5 && BotController_Lock(slot, LockKindWeapon, target) == 0;

    public static bool UnlockWeaponSlot(int slot)
        => BotController_Unlock(slot, LockKindWeapon) == 0;

    public static void UnlockReplayControl(int slot)
    {
        BotController_Unlock(slot, LockKindAll);
        BotController_Unlock(slot, LockKindAim);
        BotController_Unlock(slot, LockKindJump);
    }

    private static ReplayFile ReadReplayFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(RecMagic.Length);
        if (!magic.SequenceEqual(RecMagic))
            throw new InvalidDataException("bad .cs2rec magic");

        var version = reader.ReadUInt32();
        if (version is not (LegacyRecFormatVersion or RecFormatVersion))
            throw new InvalidDataException(
                $"unsupported .cs2rec version {version}; expected {LegacyRecFormatVersion} or {RecFormatVersion}");

        _ = reader.ReadSingle(); // tick_rate
        _ = reader.ReadUInt32(); // round
        _ = reader.ReadByte();   // side
        _ = reader.ReadUInt32(); // flags
        _ = reader.ReadUInt64(); // steam_id
        var tickCount = CheckedCount(reader.ReadUInt32(), "tick_count");
        var subtickCount = CheckedCount(reader.ReadUInt32(), "subtick_count");
        _ = ReadRecString(reader); // map
        _ = ReadRecString(reader); // player name

        var ticks = new NativeReplayTick[tickCount];
        long expectedSubticks = 0;
        for (var i = 0; i < tickCount; i++)
        {
            ticks[i] = new NativeReplayTick
            {
                Pre = ReadSnapshot(reader, version),
                Post = ReadSnapshot(reader, version),
                WeaponDefIndex = reader.ReadInt32(),
                NumSubtick = reader.ReadUInt32()
            };
            expectedSubticks += ticks[i].NumSubtick;
        }

        if (expectedSubticks != subtickCount)
            throw new InvalidDataException($"tick subtick sum {expectedSubticks} != header subtick count {subtickCount}");

        var subticks = new NativeSubtickMove[subtickCount];
        for (var i = 0; i < subtickCount; i++)
        {
            subticks[i] = new NativeSubtickMove
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

        return new ReplayFile(ticks, subticks);
    }

    private static int CheckedCount(uint value, string fieldName)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"{fieldName} too large: {value}");
        return (int)value;
    }

    private static NativeMovementSnapshot ReadSnapshot(BinaryReader reader, uint version)
        => version == LegacyRecFormatVersion
            ? ReadLegacySnapshot(reader)
            : ReadCurrentSnapshot(reader);

    private static NativeMovementSnapshot ReadLegacySnapshot(BinaryReader reader)
    {
        const uint FlDucking = 1 << 1;
        const ulong InDuck = 1UL << 2;

        var snapshot = new NativeMovementSnapshot
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
            Buttons = reader.ReadUInt64()
        };

        var ducking = (snapshot.EntityFlags & FlDucking) != 0 || (snapshot.Buttons & InDuck) != 0;
        var duckByte = (byte)(ducking ? 1 : 0);
        snapshot.DuckAmount = ducking ? 1.0f : 0.0f;
        snapshot.DuckSpeed = ducking ? 8.0f : 0.0f;
        snapshot.Ducked = duckByte;
        snapshot.Ducking = duckByte;
        snapshot.DesiresDuck = duckByte;
        snapshot.ActualMoveType = snapshot.MoveType;
        return snapshot;
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

    private static string ReadRecString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len)
            throw new EndOfStreamException("truncated string in .cs2rec");
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

    private readonly record struct ReplayFile(NativeReplayTick[] Ticks, NativeSubtickMove[] Subticks);
}

internal readonly record struct ReplayState(int Cursor, int Total, bool Playing);

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
