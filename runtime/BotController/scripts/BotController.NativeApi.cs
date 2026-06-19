// P/Invoke wrapper for BotController.dll (ABI 12). Check IsCompatible() before use.
// Main-thread only.

using System.Runtime.InteropServices;

namespace BotControllerApi
{
    // Lock category, mirrors BotController::LockKind.
    //   All    - freezes both CCSBot::Update and CCSBot::Upkeep
    //   Aim    - freezes CCSBot::Upkeep only
    //   Weapon - locks the bot's weapon to a specific engine slot
    //   Jump   - blocks CCSBot::Jump only
    public enum LockKind
    {
        All = 0,
        Aim = 1,
        Weapon = 2,
        Jump = 3,
    }

    // Engine weapon slots, mirrors BotController::LockTarget.
    public enum LockTarget
    {
        None = 0,
        Slot1 = 1,
        Slot2 = 2,
        Slot3 = 3,
        Slot4 = 4,
        Slot5 = 5,
    }

    /** One boundary of a movement tick. Captured pre (before mover) and post (after) */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MovementSnapshot
    {
        public float OriginX, OriginY, OriginZ;
        public float VelX, VelY, VelZ;
        public float Pitch, Yaw, Roll;
        public uint EntityFlags;
        public byte MoveType;
        public byte Pad0, Pad1, Pad2;
        public ulong Buttons;        // states[0] (pressed)
        public ulong Buttons1;       // states[1]
        public ulong Buttons2;       // states[2]
        public float DuckAmount;     // m_flDuckAmount (0=stand, 1=full crouch)
        public float DuckSpeed;      // m_flDuckSpeed
        public float LadderNormalX;  // m_vecLadderNormal
        public float LadderNormalY;
        public float LadderNormalZ;
        public byte Ducked;         // m_bDucked
        public byte Ducking;        // m_bDucking
        public byte DesiresDuck;    // m_bDesiresDuck
        public byte ActualMoveType; // m_nActualMoveType
    }

    /** One recorded server tick. Must match C++ ReplayTick byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayTick
    {
        public MovementSnapshot Pre;
        public MovementSnapshot Post;
        public int WeaponDefIndex;
        public uint NumSubtick;
    }

    /** One subtick input step. Must match C++ SubtickMove byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SubtickMove
    {
        public float When;
        public uint Button;
        public float Pressed;
        public float AnalogForward;
        public float AnalogLeft;
        public float PitchDelta;
        public float YawDelta;
    }

    // Thin static binding over the native exports. No orchestration here.
    public static class BotController
    {
        private const int ExpectedAbiVersion = 12;

        // Sentinel weapon def meaning "any knife"
        public const int KnifeDef = 9001;

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_Lock(int slot, int kind, int arg);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_Unlock(int slot, int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_UnlockAll(int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_IsLocked(int slot, int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetVersion();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetReplayPovMask(ulong mask);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StartRecord(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StopRecord(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetRecordedTickCount(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetRecordedSubtickCount(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CopyRecordedTicks(
            int slot, [Out] ReplayTick[] ticks, int maxTicks);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CopyRecordedSubticks(
            int slot, [Out] SubtickMove[] subs, int maxSubticks);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_LoadReplay(
            int slot, [In] ReplayTick[] ticks, int tickCount,
            [In] SubtickMove[] subs, int subCount);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_TransferRecordingToReplay(int srcSlot, int dstSlot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StartReplay(int slot, int loop);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StopReplay(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayCursor(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayTotal(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayTick(int slot, out ReplayTick tick);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SwitchBotWeapon(int slot, int defIndex);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetBotActiveWeaponDef(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetBuyPlan(int slot,
            [MarshalAs(UnmanagedType.LPStr)] string aliases);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetBuySkip(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_ClearBuyPlan(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_ClearAllBuyPlans();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetBuyPlanItemCount(int slot);

        // Native ABI must match what this wrapper expects.
        public static bool IsCompatible() => BotController_GetVersion() == ExpectedAbiVersion;

        // ---- locks ----

        // All / Aim / Jump
        public static bool Lock(int slot, LockKind kind)
            => BotController_Lock(slot, (int)kind, 0) == 0;

        // Weapon: arg is the engine slot to lock onto
        public static bool Lock(int slot, LockTarget target)
            => BotController_Lock(slot, (int)LockKind.Weapon, (int)target) == 0;

        public static bool Unlock(int slot, LockKind kind)
            => BotController_Unlock(slot, (int)kind) == 0;

        public static bool UnlockAll(LockKind kind)
            => BotController_UnlockAll((int)kind) == 0;

        // For All/Aim/Jump returns true if locked; for Weapon use GetWeaponLock.
        public static bool IsLocked(int slot, LockKind kind)
            => BotController_IsLocked(slot, (int)kind) != 0;

        // Weapon-only query: returns the locked weapon slot, or None.
        public static LockTarget GetWeaponLock(int slot)
            => (LockTarget)BotController_IsLocked(slot, (int)LockKind.Weapon);

        // ---- recording ----

        public static bool StartRecord(int slot) => BotController_StartRecord(slot) == 0;

        public static bool StopRecord(int slot) => BotController_StopRecord(slot) == 0;

        public static int RecordedTickCount(int slot) => BotController_GetRecordedTickCount(slot);

        // Pull a slot's recorded ticks + subticks out of native memory.
        public static (ReplayTick[] ticks, SubtickMove[] subs) GetRecordedMotion(int slot)
        {
            int nt = BotController_GetRecordedTickCount(slot);
            if (nt <= 0) return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>());

            var ticks = new ReplayTick[nt];
            int gotT = BotController_CopyRecordedTicks(slot, ticks, nt);
            if (gotT <= 0) return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>());
            if (gotT != nt) Array.Resize(ref ticks, gotT);

            int ns = BotController_GetRecordedSubtickCount(slot);
            SubtickMove[] subs;
            if (ns <= 0)
                subs = Array.Empty<SubtickMove>();
            else
            {
                subs = new SubtickMove[ns];
                int gotS = BotController_CopyRecordedSubticks(slot, subs, ns);
                if (gotS <= 0) subs = Array.Empty<SubtickMove>();
                else if (gotS != ns) Array.Resize(ref subs, gotS);
            }
            return (ticks, subs);
        }

        // ---- replay ----

        // Load ticks + subticks into a slot's replay buffer (native copies in).
        public static bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subs)
            => ticks is { Length: > 0 }
               && BotController_LoadReplay(slot, ticks, ticks.Length,
                                       subs ?? Array.Empty<SubtickMove>(),
                                       subs?.Length ?? 0) == 0;

        // Move a slot's just-recorded buffers straight into another slot's
        // replay buffer, no managed round-trip.
        public static bool TransferRecordingToReplay(int srcSlot, int dstSlot)
            => BotController_TransferRecordingToReplay(srcSlot, dstSlot) == 0;

        public static bool StartReplay(int slot, bool loop = false)
            => BotController_StartReplay(slot, loop ? 1 : 0) == 0;

        public static bool StopReplay(int slot) => BotController_StopReplay(slot) == 0;

        public static int ReplayCursor(int slot) => BotController_GetReplayCursor(slot);

        public static int ReplayTotal(int slot) => BotController_GetReplayTotal(slot);

        public static bool IsReplaying(int slot) => BotController_GetReplayCursor(slot) >= 0;

        // Bit n means replay slot n is currently watched in first-person.
        public static bool SetReplayPovMask(ulong mask)
            => BotController_SetReplayPovMask(mask) == 0;

        // The tick currently being replayed on this slot, for driving weapon/fire
        // C#-side. Returns false if the slot isn't replaying.
        public static bool TryGetReplayTick(int slot, out ReplayTick tick)
            => BotController_GetReplayTick(slot, out tick) == 0;

        // Switch a bot to the weapon with this def index.
        public static bool SwitchBotWeapon(int slot, int defIndex)
            => BotController_SwitchBotWeapon(slot, defIndex) == 0;

        // Def index of the bot's current active weapon, same normalization as the
        // recorded WeaponDefIndex. <0 if unresolved.
        public static int BotActiveWeaponDef(int slot)
            => BotController_GetBotActiveWeaponDef(slot);

        // ---- buy plans ----

        // Force a bot's per-round buy.
        public static bool SetBuyPlan(int slot, string aliases)
            => BotController_SetBuyPlan(slot, aliases ?? "") == 0;

        // Force a bot to buy nothing each round.
        public static bool SetBuySkip(int slot)
            => BotController_SetBuySkip(slot) == 0;

        // Remove a bot's buy plan (back to vanilla AI buying).
        public static bool ClearBuyPlan(int slot)
            => BotController_ClearBuyPlan(slot) == 0;

        public static bool ClearAllBuyPlans()
            => BotController_ClearAllBuyPlans() == 0;

        // Plan item count: -1 none, 0 skip/empty, >0 alias count.
        public static int BuyPlanItemCount(int slot)
            => BotController_GetBuyPlanItemCount(slot);
    }
}
