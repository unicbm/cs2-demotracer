// C-ABI exports for CounterStrikeSharp P/Invoke. quiet=true on all entries.

#include "dispatch.h"
#include "MotionRecorder.h"
#include "InputInjector.h"
#include "BuyControllerState.h"

#include <cstdint>
#include <string>
#include <vector>

extern "C" __declspec(dllexport) int BotController_Lock(int slot, int kind, int arg)
{
    return BotController::Dispatch::Lock(slot,
                                         static_cast<BotController::LockKind>(kind), arg, /*quiet=*/true);
}

extern "C" __declspec(dllexport) int BotController_Unlock(int slot, int kind)
{
    return BotController::Dispatch::Unlock(slot,
                                           static_cast<BotController::LockKind>(kind), /*quiet=*/true);
}

extern "C" __declspec(dllexport) int BotController_UnlockAll(int kind)
{
    return BotController::Dispatch::UnlockAll(
        static_cast<BotController::LockKind>(kind), /*quiet=*/true);
}

extern "C" __declspec(dllexport) int BotController_IsLocked(int slot, int kind)
{
    return BotController::Dispatch::IsLocked(slot,
                                             static_cast<BotController::LockKind>(kind));
}

extern "C" __declspec(dllexport) int BotController_GetVersion()
{
    return 13;
}

extern "C" __declspec(dllexport) int BotController_SetControllerControllingBotOffset(int offset)
{
    return BotController::InputInjector::SetControllerControllingBotOffset(offset) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_SetReplayPovMask(uint64_t mask)
{
    BotController::MotionRecorder::SetReplayPovMask(mask);
    return 0;
}

// ---- Bot buy plans ----

static std::vector<std::string> SplitAliases(const char *aliases)
{
    std::vector<std::string> out;
    if (!aliases)
        return out;

    std::string cur;
    for (const char *p = aliases; *p; ++p)
    {
        char c = *p;
        if (c == ' ' || c == ',' || c == '\t')
        {
            if (!cur.empty())
            {
                out.push_back(cur);
                cur.clear();
            }
        }
        else
        {
            cur.push_back(c);
        }
    }
    if (!cur.empty())
        out.push_back(cur);
    return out;
}

extern "C" __declspec(dllexport) int BotController_SetBuyPlan(int slot, const char *aliases)
{
    if (slot < 0 || slot >= BotController::BuyControllerState::kMaxSlots)
        return -2;
    BotController::BuyControllerState::Set(slot, SplitAliases(aliases), false);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_SetBuySkip(int slot)
{
    if (slot < 0 || slot >= BotController::BuyControllerState::kMaxSlots)
        return -2;
    BotController::BuyControllerState::Set(slot, {}, true);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_ClearBuyPlan(int slot)
{
    if (slot < 0 || slot >= BotController::BuyControllerState::kMaxSlots)
        return -2;
    BotController::BuyControllerState::Clear(slot);
    return 0;
}

extern "C" __declspec(dllexport) int BotController_ClearAllBuyPlans()
{
    BotController::BuyControllerState::ClearAll();
    return 0;
}

extern "C" __declspec(dllexport) int BotController_GetBuyPlanItemCount(int slot)
{
    return BotController::BuyControllerState::ItemCount(slot);
}

// ---- Motion recording & replay ----

// Begin/stop recording a human slot's per-tick movement. 0 ok / -1 fail.
extern "C" __declspec(dllexport) int BotController_StartRecord(int slot)
{
    return BotController::MotionRecorder::StartRecord(slot) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_StopRecord(int slot)
{
    return BotController::MotionRecorder::StopRecord(slot) ? 0 : -1;
}

// Recorded tick / subtick counts for a slot. <0 on bad slot.
extern "C" __declspec(dllexport) int BotController_GetRecordedTickCount(int slot)
{
    return BotController::MotionRecorder::RecordedTickCount(slot);
}

extern "C" __declspec(dllexport) int BotController_GetRecordedSubtickCount(int slot)
{
    return BotController::MotionRecorder::RecordedSubtickCount(slot);
}

// Copy recorded ticks / subticks into caller buffers. Returns count written.
extern "C" __declspec(dllexport) int BotController_CopyRecordedTicks(int slot, BotController::ReplayTick *out, int maxTicks)
{
    return BotController::MotionRecorder::CopyTicks(slot, out, maxTicks);
}

extern "C" __declspec(dllexport) int BotController_CopyRecordedSubticks(int slot, BotController::SubtickMove *out, int maxSubticks)
{
    return BotController::MotionRecorder::CopySubticks(slot, out, maxSubticks);
}

// Load parallel tick + subtick arrays into a slot's replay buffer. 0 ok.
extern "C" __declspec(dllexport) int BotController_LoadReplay(int slot,
                                                          const BotController::ReplayTick *ticks, int tickCount,
                                                          const BotController::SubtickMove *subs, int subCount)
{
    return BotController::MotionRecorder::LoadReplay(slot, ticks, tickCount,
                                                     subs, subCount)
               ? 0
               : -1;
}

// Move a slot's just-recorded buffers into another slot's replay buffer
extern "C" __declspec(dllexport) int BotController_TransferRecordingToReplay(int srcSlot, int dstSlot)
{
    int nt = BotController::MotionRecorder::RecordedTickCount(srcSlot);
    if (nt <= 0)
        return -1;
    int ns = BotController::MotionRecorder::RecordedSubtickCount(srcSlot);
    if (ns < 0)
        ns = 0;
    std::vector<BotController::ReplayTick> ticks(nt);
    std::vector<BotController::SubtickMove> subs(ns > 0 ? ns : 1);
    int gotT = BotController::MotionRecorder::CopyTicks(srcSlot, ticks.data(), nt);
    int gotS = ns > 0
                   ? BotController::MotionRecorder::CopySubticks(srcSlot, subs.data(), ns)
                   : 0;
    if (gotT <= 0)
        return -1;
    return BotController::MotionRecorder::LoadReplay(
               dstSlot, ticks.data(), gotT, subs.data(), gotS)
               ? 0
               : -1;
}

extern "C" __declspec(dllexport) int BotController_StartReplay(int slot, int loop)
{
    return BotController::MotionRecorder::StartReplay(slot, loop != 0) ? 0 : -1;
}

extern "C" __declspec(dllexport) int BotController_StopReplay(int slot)
{
    return BotController::MotionRecorder::StopReplay(slot) ? 0 : -1;
}

// Current replay tick index, or <0 if the slot is not replaying.
extern "C" __declspec(dllexport) int BotController_GetReplayCursor(int slot)
{
    return BotController::MotionRecorder::ReplayCursor(slot);
}

// Total ticks loaded in a slot's replay buffer.
extern "C" __declspec(dllexport) int BotController_GetReplayTotal(int slot)
{
    return BotController::MotionRecorder::ReplayTotal(slot);
}

// Combined replay state for CSS hot paths. Returns 0 on success.
extern "C" __declspec(dllexport) int BotController_GetReplaySlotState(
    int slot,
    BotController::MotionRecorder::ReplaySlotState *out)
{
    if (!out)
        return -1;
    return BotController::MotionRecorder::GetReplaySlotState(slot, *out) ? 0 : -1;
}

// Copy the tick currently being replayed (for C# to drive weapon/fire).
// Returns 0 on success, -1 if the slot isn't replaying.
extern "C" __declspec(dllexport) int BotController_GetReplayTick(int slot, BotController::ReplayTick *out)
{
    if (!out)
        return -1;
    return BotController::MotionRecorder::CurrentReplayTick(slot, *out) ? 0 : -1;
}

// Switch a bot to the weapon with this def index
// Returns 0 ok / -1 not found or bot not ready.
extern "C" __declspec(dllexport) int BotController_SwitchBotWeapon(int slot, int defIndex)
{
    return BotController::MotionRecorder::SwitchBotWeaponByDef(slot, defIndex) ? 0 : -1;
}

// Def index of the bot's current active weapon (same normalization as the
// recorded WeaponDefIndex). <0 if unresolved. For C# to reconcile replay.
extern "C" __declspec(dllexport) int BotController_GetBotActiveWeaponDef(int slot)
{
    return BotController::MotionRecorder::BotActiveWeaponDef(slot);
}
