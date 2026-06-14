// CCSBot* -> player slot via pawn (+0x18) -> m_hController (+0xB80).

#include "ccsbot_slot.h"
#include "version_targets.h"

#include <cstdint>
#include <mutex>
#include <unordered_set>

#include <tier0/dbg.h>

namespace tg = cs2bl::targets;

namespace BotLocker
{
    // Compile-time switch to turn the once-per-bot diagnostic scan back on.
    static constexpr bool kEnableHandleScan = false;

    static std::unordered_set<void *> g_scanned;
    static std::mutex g_scannedMu;

    static int EntIndexFromHandle(uint32_t h)
    {
        if (h == 0u || h == 0xFFFFFFFFu)
            return -1;
        return static_cast<int>(h & 0x7FFFu);
    }

    static void ScanPawnForControllerHandle(void *pawn)
    {
        if (!pawn)
            return;
        Msg("[BWL][scan] pawn=%p candidate handles idx 1..64, 0x008..0x1000:\n", pawn);
        for (int off = 0x8; off < 0x1000; off += 4)
        {
            uint32_t v = *reinterpret_cast<uint32_t *>(
                reinterpret_cast<char *>(pawn) + off);
            if (v == 0u || v == 0xFFFFFFFFu)
                continue;
            int idx = static_cast<int>(v & 0x7FFFu);
            uint32_t serial = (v >> 15);
            if (idx >= 1 && idx <= 64)
                Msg("[BWL][scan]   +0x%03X = 0x%08X  idx=%d  serial=%u\n",
                    off, v, idx, serial);
        }
    }

    SlotResolution ResolveSlot(void *bot)
    {
        SlotResolution out{nullptr, -1, -1};
        if (!bot)
            return out;

        void *pawn = *reinterpret_cast<void **>(
            reinterpret_cast<char *>(bot) + tg::kBot_Pawn);
        if (!pawn)
            return out;
        out.pawn = pawn;

        void *identity = *reinterpret_cast<void **>(
            reinterpret_cast<char *>(pawn) + tg::kEnt_Identity);
        if (identity)
        {
            uint32_t handle = *reinterpret_cast<uint32_t *>(
                reinterpret_cast<char *>(identity) + tg::kEntIdentity_EHandle);
            int idx = EntIndexFromHandle(handle);
            if (idx > 0)
                out.pawnEntIndex = idx;
        }

        // Read m_hController; fall back to m_hOriginalController if the
        // primary handle isn't populated for this pawn yet.
        uint32_t ctrlHandle = *reinterpret_cast<uint32_t *>(
            reinterpret_cast<char *>(pawn) + tg::kPawn_Controller);
        int ctrlIdx = EntIndexFromHandle(ctrlHandle);
        if (ctrlIdx < 1 || ctrlIdx > 64)
        {
            uint32_t origHandle = *reinterpret_cast<uint32_t *>(
                reinterpret_cast<char *>(pawn) + tg::kPawn_OriginalController);
            ctrlIdx = EntIndexFromHandle(origHandle);
        }
        if (ctrlIdx >= 1 && ctrlIdx <= 64)
            out.slot = ctrlIdx - 1;

        if (kEnableHandleScan)
        {
            std::lock_guard<std::mutex> lk(g_scannedMu);
            if (g_scanned.insert(bot).second)
                ScanPawnForControllerHandle(pawn);
        }

        return out;
    }

    int CCSBotToSlot(void *bot)
    {
        return ResolveSlot(bot).slot;
    }

    // pawn+0xB80 m_hController -> entindex -> slot.
    int ControllerSlotForPawn(void *pawn)
    {
        if (!pawn)
            return -1;
        uint32_t h = *reinterpret_cast<uint32_t *>(
            reinterpret_cast<char *>(pawn) + tg::kPawn_Controller);
        int idx = EntIndexFromHandle(h);
        if (idx < 1 || idx > 64)
            return -1;
        return idx - 1;
    }

    // CCSPlayerController*'s own identity ehandle -> entindex -> slot.
    int ControllerToSlot(void *controller)
    {
        if (!controller)
            return -1;
        void *identity = *reinterpret_cast<void **>(
            reinterpret_cast<char *>(controller) + tg::kEnt_Identity);
        if (!identity)
            return -1;
        uint32_t h = *reinterpret_cast<uint32_t *>(
            reinterpret_cast<char *>(identity) + tg::kEntIdentity_EHandle);
        int idx = EntIndexFromHandle(h);
        if (idx < 1 || idx > 64)
            return -1;
        return idx - 1;
    }
}
