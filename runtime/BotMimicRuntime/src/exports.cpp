// C-ABI exports for CounterStrikeSharp P/Invoke. quiet=true on all entries.

#include "dispatch.h"
#include "Cs2RecFile.h"
#include "InputInjector.h"
#include "MotionRecorder.h"

#include <cstdint>
#include <vector>

namespace
{
    constexpr int CS2BM_ABI = 10;
}

extern "C" __declspec(dllexport) int BotLocker_Lock(int slot, int kind, int arg)
{
    return BotLocker::Dispatch::Lock(slot,
                                     static_cast<BotLocker::LockKind>(kind), arg, /*quiet=*/true);
}

extern "C" __declspec(dllexport) int BotLocker_Unlock(int slot, int kind)
{
    return BotLocker::Dispatch::Unlock(slot,
                                       static_cast<BotLocker::LockKind>(kind), /*quiet=*/true);
}

extern "C" __declspec(dllexport) int BotLocker_UnlockAll(int kind)
{
    return BotLocker::Dispatch::UnlockAll(
        static_cast<BotLocker::LockKind>(kind), /*quiet=*/true);
}

extern "C" __declspec(dllexport) int BotLocker_IsLocked(int slot, int kind)
{
    return BotLocker::Dispatch::IsLocked(slot,
                                         static_cast<BotLocker::LockKind>(kind));
}

// Set per-slot injected input. Engine pmove runs with these values until cleared.
extern "C" __declspec(dllexport) int BotLocker_InjectUserCmd(int slot,
                                                             uint64_t buttons,
                                                             float forwardMove,
                                                             float sideMove,
                                                             float upMove,
                                                             float pitch,
                                                             float yaw)
{
    BotLocker::InjectedInput in{buttons, forwardMove, sideMove, upMove, pitch, yaw};
    return BotLocker::InputInjector::SetInput(slot, in) ? 0 : -1;
}

// Stop injecting for one slot. Engine resumes its own UserCmd.
extern "C" __declspec(dllexport) int BotLocker_ClearInjection(int slot)
{
    return BotLocker::InputInjector::ClearInput(slot) ? 0 : -1;
}

// Stop injecting for every slot at once.
extern "C" __declspec(dllexport) int BotLocker_ClearAllInjections()
{
    BotLocker::InputInjector::ClearAll();
    return 0;
}

extern "C" __declspec(dllexport) int BotLocker_GetVersion()
{
    return CS2BM_ABI;
}

// ---- Motion recording & replay ----

// Begin/stop recording a human slot's per-tick movement. 0 ok / -1 fail.
extern "C" __declspec(dllexport) int BotLocker_StartRecord(int slot)
{
    return BotLocker::MotionRecorder::StartRecord(slot) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotLocker_StopRecord(int slot)
{
    return BotLocker::MotionRecorder::StopRecord(slot) ? 0 : -1;
}

// Recorded tick / subtick counts for a slot. <0 on bad slot.
extern "C" __declspec(dllexport) int BotLocker_GetRecordedTickCount(int slot)
{
    return BotLocker::MotionRecorder::RecordedTickCount(slot);
}

extern "C" __declspec(dllexport) int BotLocker_GetRecordedSubtickCount(int slot)
{
    return BotLocker::MotionRecorder::RecordedSubtickCount(slot);
}

// Copy recorded ticks / subticks into caller buffers. Returns count written.
extern "C" __declspec(dllexport) int BotLocker_CopyRecordedTicks(int slot, BotLocker::ReplayTick *out, int maxTicks)
{
    return BotLocker::MotionRecorder::CopyTicks(slot, out, maxTicks);
}

extern "C" __declspec(dllexport) int BotLocker_CopyRecordedSubticks(int slot, BotLocker::SubtickMove *out, int maxSubticks)
{
    return BotLocker::MotionRecorder::CopySubticks(slot, out, maxSubticks);
}

// Load parallel tick + subtick arrays into a slot's replay buffer. 0 ok.
extern "C" __declspec(dllexport) int BotLocker_LoadReplay(int slot,
                                                          const BotLocker::ReplayTick *ticks, int tickCount,
                                                          const BotLocker::SubtickMove *subs, int subCount)
{
    return BotLocker::MotionRecorder::LoadReplay(slot, ticks, tickCount,
                                                 subs, subCount)
               ? 0
               : -1;
}

extern "C" __declspec(dllexport) int BotLocker_LoadReplayFromFile(int slot, const char *path)
{
    return BotLocker::Cs2RecFile::LoadFromFile(slot, path) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotLocker_UnloadReplay(int slot)
{
    BotLocker::MotionRecorder::StopReplay(slot);
    BotLocker::ReplayTick dummy{};
    BotLocker::SubtickMove dummySubtick{};
    return BotLocker::MotionRecorder::LoadReplay(slot, &dummy, 0, &dummySubtick, 0) ? 0 : -1;
}

// Move a slot's just-recorded buffers into another slot's replay buffer
extern "C" __declspec(dllexport) int BotLocker_TransferRecordingToReplay(int srcSlot, int dstSlot)
{
    int nt = BotLocker::MotionRecorder::RecordedTickCount(srcSlot);
    if (nt <= 0)
        return -1;
    int ns = BotLocker::MotionRecorder::RecordedSubtickCount(srcSlot);
    if (ns < 0)
        ns = 0;
    std::vector<BotLocker::ReplayTick> ticks(nt);
    std::vector<BotLocker::SubtickMove> subs(ns > 0 ? ns : 1);
    int gotT = BotLocker::MotionRecorder::CopyTicks(srcSlot, ticks.data(), nt);
    int gotS = ns > 0
                   ? BotLocker::MotionRecorder::CopySubticks(srcSlot, subs.data(), ns)
                   : 0;
    if (gotT <= 0)
        return -1;
    return BotLocker::MotionRecorder::LoadReplay(
               dstSlot, ticks.data(), gotT, subs.data(), gotS)
               ? 0
               : -1;
}

extern "C" __declspec(dllexport) int BotLocker_StartReplay(int slot, int loop)
{
    return BotLocker::MotionRecorder::StartReplay(slot, loop != 0) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotLocker_StopReplay(int slot)
{
    return BotLocker::MotionRecorder::StopReplay(slot) ? 0 : -1;
}

// Current replay tick index, or <0 if the slot is not replaying.
extern "C" __declspec(dllexport) int BotLocker_GetReplayCursor(int slot)
{
    return BotLocker::MotionRecorder::ReplayCursor(slot);
}

// Total ticks loaded in a slot's replay buffer.
extern "C" __declspec(dllexport) int BotLocker_GetReplayTotal(int slot)
{
    return BotLocker::MotionRecorder::ReplayTotal(slot);
}

extern "C" __declspec(dllexport) int BotLocker_GetReplayState(int slot, int *cursor, int *total, int *playing)
{
    if (cursor)
        *cursor = BotLocker::MotionRecorder::ReplayCursor(slot);
    if (total)
        *total = BotLocker::MotionRecorder::ReplayTotal(slot);
    if (playing)
        *playing = BotLocker::MotionRecorder::IsReplaying(slot) ? 1 : 0;
    return 0;
}

// Copy the tick currently being replayed (for C# to drive weapon/fire).
// Returns 0 on success, -1 if the slot isn't replaying.
extern "C" __declspec(dllexport) int BotLocker_GetReplayTick(int slot, BotLocker::ReplayTick *out)
{
    if (!out)
        return -1;
    return BotLocker::MotionRecorder::CurrentReplayTick(slot, *out) ? 0 : -1;
}

// Switch a bot to the weapon with this def index
// Returns 0 ok / -1 not found or bot not ready.
extern "C" __declspec(dllexport) int BotLocker_SwitchBotWeapon(int slot, int defIndex)
{
    return BotLocker::MotionRecorder::SwitchBotWeaponByDef(slot, defIndex) ? 0 : -1;
}
