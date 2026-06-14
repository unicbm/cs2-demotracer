using System.Runtime.InteropServices;

namespace Cs2DemoBotMimic;

internal static class BotMimicNative
{
    public const int ExpectedAbiVersion = 10;

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_GetVersion();

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int BotLocker_LoadReplayFromFile(int slot, string path);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_UnloadReplay(int slot);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_StartReplay(int slot, int loop);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_StopReplay(int slot);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_GetReplayState(int slot, out int cursor, out int total, out int playing);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_GetReplayTick(int slot, out NativeReplayTick tick);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_SwitchBotWeapon(int slot, int defIndex);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_Lock(int slot, int kind, int arg);

    [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotLocker_Unlock(int slot, int kind);

    public static int AbiVersion => BotLocker_GetVersion();

    public static bool IsCompatible => AbiVersion == ExpectedAbiVersion;

    public static bool LoadReplayFromFile(int slot, string path)
        => BotLocker_LoadReplayFromFile(slot, path) == 0;

    public static bool UnloadReplay(int slot)
        => BotLocker_UnloadReplay(slot) == 0;

    public static bool StartReplay(int slot, bool loop)
        => BotLocker_StartReplay(slot, loop ? 1 : 0) == 0;

    public static bool StopReplay(int slot)
        => BotLocker_StopReplay(slot) == 0;

    public static ReplayState GetReplayState(int slot)
    {
        BotLocker_GetReplayState(slot, out var cursor, out var total, out var playing);
        return new ReplayState(cursor, total, playing != 0);
    }

    public static bool TryGetReplayTick(int slot, out NativeReplayTick tick)
        => BotLocker_GetReplayTick(slot, out tick) == 0;

    public static bool SwitchBotWeapon(int slot, int defIndex)
        => BotLocker_SwitchBotWeapon(slot, defIndex) == 0;

    public static bool LockWeaponSlot(int slot, int target)
        => BotLocker_Lock(slot, 2, target) == 0;

    public static bool UnlockWeaponSlot(int slot)
        => BotLocker_Unlock(slot, 2) == 0;
}

internal readonly record struct ReplayState(int Cursor, int Total, bool Playing);

[StructLayout(LayoutKind.Explicit, Pack = 4, Size = 52)]
internal struct NativeMovementSnapshot
{
    [FieldOffset(0)] public float OriginX;
    [FieldOffset(4)] public float OriginY;
    [FieldOffset(8)] public float OriginZ;
    [FieldOffset(12)] public float VelX;
    [FieldOffset(16)] public float VelY;
    [FieldOffset(20)] public float VelZ;
    [FieldOffset(24)] public float Pitch;
    [FieldOffset(28)] public float Yaw;
    [FieldOffset(32)] public float Roll;
    [FieldOffset(36)] public uint EntityFlags;
    [FieldOffset(40)] public byte MoveType;
    [FieldOffset(44)] public ulong Buttons;
}

[StructLayout(LayoutKind.Explicit, Pack = 4, Size = 112)]
internal struct NativeReplayTick
{
    [FieldOffset(0)] public NativeMovementSnapshot Pre;
    [FieldOffset(52)] public NativeMovementSnapshot Post;
    [FieldOffset(104)] public int WeaponDefIndex;
    [FieldOffset(108)] public uint NumSubtick;
}
