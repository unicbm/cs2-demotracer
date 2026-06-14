// CSS wrapper for BotLocker.dll. Always check IsCompatible() before use.
// Main-thread only.

using System.Runtime.InteropServices;

namespace BotLockerApi
{
    // Lock category. Per-slot lock states:
    //   All    - freezes both CCSBot::Update and CCSBot::Upkeep
    //   Aim    - freezes CCSBot::Upkeep only
    //   Weapon - locks the bot's weapon to a specific engine slot
    //   Jump   - blocks CCSBot::Jump only
    public enum LockKind
    {
        All    = 0,
        Aim    = 1,
        Weapon = 2,
        Jump   = 3,
    }

    // Engine weapon slots.
    public enum LockTarget
    {
        None  = 0,
        Slot1 = 1,
        Slot2 = 2,
        Slot3 = 3,
        Slot4 = 4,
        Slot5 = 5,
    }

    public static class BotLocker
    {
        private const int ExpectedAbiVersion = 10;

        [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotLocker_Lock(int slot, int kind, int arg);

        [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotLocker_Unlock(int slot, int kind);

        [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotLocker_UnlockAll(int kind);

        [DllImport("BotLocker", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotLocker_IsLocked(int slot, int kind);

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

        public static bool IsCompatible() => BotLocker_GetVersion() == ExpectedAbiVersion;

        public static int AbiVersion => BotLocker_GetVersion();

        // ---- All / Aim / Jump ----
        public static bool Lock(int slot, LockKind kind)
            => BotLocker_Lock(slot, (int)kind, 0) == 0;

        // ---- Weapon: arg is the engine slot to lock onto ----
        public static bool Lock(int slot, LockTarget target)
            => BotLocker_Lock(slot, (int)LockKind.Weapon, (int)target) == 0;

        public static bool Unlock(int slot, LockKind kind)
            => BotLocker_Unlock(slot, (int)kind) == 0;

        public static bool UnlockAll(LockKind kind)
            => BotLocker_UnlockAll((int)kind) == 0;

        // For All/Aim/Jump returns true if locked; for Weapon use GetWeaponLock.
        public static bool IsLocked(int slot, LockKind kind)
            => BotLocker_IsLocked(slot, (int)kind) != 0;

        // Weapon-only query: returns the locked weapon slot, or None.
        public static LockTarget GetWeaponLock(int slot)
            => (LockTarget)BotLocker_IsLocked(slot, (int)LockKind.Weapon);

        public static bool LoadReplayFromFile(int slot, string path)
            => BotLocker_LoadReplayFromFile(slot, path) == 0;

        public static bool UnloadReplay(int slot)
            => BotLocker_UnloadReplay(slot) == 0;

        public static bool StartReplay(int slot, bool loop)
            => BotLocker_StartReplay(slot, loop ? 1 : 0) == 0;

        public static bool StopReplay(int slot)
            => BotLocker_StopReplay(slot) == 0;

        public static (int Cursor, int Total, bool Playing) GetReplayState(int slot)
        {
            BotLocker_GetReplayState(slot, out var cursor, out var total, out var playing);
            return (cursor, total, playing != 0);
        }
    }
}
