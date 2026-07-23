// Detour for BuyState::OnUpdate.

#include "BuyController.h"
#include "BuyControllerState.h"
#include "ccsbot_slot.h"
#include "dispatch.h"
#include "hook.h"
#include "platform.h"
#include "version_targets.h"

#include <convar.h>
#include <eiface.h>
#include <playerslot.h>
#include <tier0/dbg.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <string>

namespace tg = BotController::targets;

using BuyUpdate_t = void(BC_FASTCALL *)(void *self, void *me);

namespace BotController
{
    namespace BuyControllerHooks
    {
        static BuyUpdate_t g_origOnUpdate = nullptr;
        static void *g_addrOnUpdate = nullptr;
        static Hook g_hookOnUpdate;
        static bool g_installed = false;
        static std::string g_status = "not_attempted";

        static std::array<std::atomic<uint8_t>, 64> g_lastInitDelay{};

        static void IssueBuy(int slot, const char *alias)
        {
            if (!Dispatch::g_pGameClients || slot < 0 || slot >= 64)
                return;

            char line[128];
            std::snprintf(line, sizeof(line), "buy %s", alias);
            CCommand cmd;
            if (!cmd.Tokenize(line))
                return;

            Dispatch::g_pGameClients->ClientCommand(CPlayerSlot(slot), cmd);
        }

        static void ApplyPlan(void *self, int slot)
        {
            BuyPlan plan;
            if (!BuyControllerState::Copy(slot, plan))
                return;

            if (!plan.skip)
            {
                for (const auto &alias : plan.items)
                    IssueBuy(slot, alias.c_str());
            }

            const uint8_t done = 1;
            WriteField(self, tg::kBuy_DoneBuying, done);

            char dbg[128];
            std::snprintf(dbg, sizeof(dbg), "[BC][buy] slot=%d skip=%d items=%d\n",
                          slot, plan.skip ? 1 : 0, static_cast<int>(plan.items.size()));
            DebugOut(dbg);
        }

        static void BC_FASTCALL HookedOnUpdate(void *self, void *me)
        {
            int slot = CCSBotToSlot(me);
            if (slot < 0 || slot >= 64 || !BuyControllerState::HasPlan(slot))
                return g_origOnUpdate(self, me);

            uint8_t init = 0;
            if (!SafeRead(self, tg::kBuy_InitialDelay, init))
                return g_origOnUpdate(self, me);
            if (init &&
                !g_lastInitDelay[slot].load(std::memory_order_relaxed))
                ApplyPlan(self, slot);
            g_lastInitDelay[slot].store(init, std::memory_order_relaxed);

            g_origOnUpdate(self, me);
        }

        void ResetInitialDelayLatch(int slot)
        {
            if (slot >= 0 && slot < static_cast<int>(g_lastInitDelay.size()))
                g_lastInitDelay[slot].store(0, std::memory_order_relaxed);
        }

        void ResetAllInitialDelayLatches()
        {
            for (auto &value : g_lastInitDelay)
                value.store(0, std::memory_order_relaxed);
        }

        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen)
        {
            g_addrOnUpdate = Sig::ResolveSig(gd, serverModule, "BuyState::OnUpdate",
                                             errorOut, errorOutLen);
            if (!g_addrOnUpdate)
            {
                g_status = "failed: OnUpdate sig";
                return false;
            }

            if (!g_hookOnUpdate.Create(g_addrOnUpdate,
                                       reinterpret_cast<void *>(&HookedOnUpdate),
                                       reinterpret_cast<void **>(&g_origOnUpdate)) ||
                !g_hookOnUpdate.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook BuyState::OnUpdate failed");
                g_hookOnUpdate.Remove();
                g_origOnUpdate = nullptr;
                g_status = "failed: hook OnUpdate";
                return false;
            }

            g_installed = true;
            g_status = "ok";

            char dbg[160];
            std::snprintf(dbg, sizeof(dbg), "[BuyController] OnUpdate@%p\n", g_addrOnUpdate);
            DebugOut(dbg);
            return true;
        }

        void Remove()
        {
            if (!g_installed)
                return;
            g_hookOnUpdate.Remove();
            g_origOnUpdate = nullptr;
            g_installed = false;
            g_status = "not_attempted";
            ResetAllInitialDelayLatches();
        }

        const char *Status() { return g_status.c_str(); }
        void *OnUpdateAddress() { return g_addrOnUpdate; }
    }
}
