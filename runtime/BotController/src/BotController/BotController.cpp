// CCSBot Update/Upkeep/Jump detours

#include "BotController.h"
#include "BotControllerState.h"
#include "ccsbot_slot.h"
#include "sig_scan.h"
#include "MotionRecorder.h"
#include "version_targets.h"
#include "hook.h"
#include "platform.h"

#include <tier0/dbg.h>

#include <cstdint>
#include <cstdio>
#include <cmath>
#include <vector>

namespace tg = BotController::targets;

using Update_t = void(BC_FASTCALL *)(void *bot);
using Upkeep_t = void(BC_FASTCALL *)(void *bot);
using Jump_t = char(BC_FASTCALL *)(void *bot, char mustJump);
using UpdateLookAngles_t = void(BC_FASTCALL *)(void *bot);
using SetEyeAngles_t = void(BC_FASTCALL *)(void *pawn, float *angle);
using GetEyeAngles_t = float *(BC_FASTCALL *)(void *pawn, float *out);

namespace BotController
{
    namespace BotControllerHooks
    {
        static Update_t g_origUpdate = nullptr;
        static void *g_addrUpdate = nullptr;
        static Upkeep_t g_origUpkeep = nullptr;
        static void *g_addrUpkeep = nullptr;
        static Jump_t g_origJump = nullptr;
        static void *g_addrJump = nullptr;
        static UpdateLookAngles_t g_origUpdateLookAngles = nullptr;
        static void *g_addrUpdateLookAngles = nullptr;
        static SetEyeAngles_t g_origSetEyeAngles = nullptr;
        static void *g_addrSetEyeAngles = nullptr;
        static GetEyeAngles_t g_origGetEyeAngles = nullptr;
        static void *g_addrGetEyeAngles = nullptr;
        static thread_local bool g_replayOwnedSetEyeAngles = false;
        static bool g_installed = false;
        static std::string g_status = "not_attempted";

        static Hook g_hookUpdate;
        static Hook g_hookUpkeep;
        static Hook g_hookJump;
        static Hook g_hookUpdateLookAngles;
        static Hook g_hookSetEyeAngles;
        static Hook g_hookGetEyeAngles;

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        // Skip the Bot tick under All lock OR while replaying
        static void BC_FASTCALL HookedUpdate(void *bot)
        {
            int slot = CCSBotToSlot(bot);
            if (slot >= 0 &&
                (BotControllerState::GetAll(slot) || MotionRecorder::IsReplaying(slot)))
            {
                *(reinterpret_cast<uint8_t *>(bot) + tg::kBot_AiTickedFlag) = 1;
                return;
            }
            g_origUpdate(bot);
        }

        // Skip the per-frame view tick under replay, All, or Aim lock.
        static void BC_FASTCALL HookedUpdateLookAngles(void *bot); // fwd decl

        static void BC_FASTCALL HookedUpkeep(void *bot)
        {
            int slot = CCSBotToSlot(bot);
            if (slot >= 0 && MotionRecorder::IsReplaying(slot))
                return;
            if (slot >= 0 &&
                (BotControllerState::GetAll(slot) || BotControllerState::GetAim(slot)))
            {
                return;
            }
            g_origUpkeep(bot);
        }

        static void BC_FASTCALL HookedUpdateLookAngles(void *bot)
        {
            int slot = CCSBotToSlot(bot);
            if (slot >= 0 &&
                (MotionRecorder::IsReplaying(slot) ||
                 BotControllerState::GetAll(slot) ||
                 BotControllerState::GetAim(slot)))
            {
                return;
            }
            g_origUpdateLookAngles(bot);
        }

        // Engine eye-angle
        static void BC_FASTCALL HookedSetEyeAngles(void *pawn, float *angle)
        {
            int slot = pawn ? ControllerSlotForPawn(pawn) : -1;
            if (slot >= 0 && MotionRecorder::IsReplaying(slot) && !g_replayOwnedSetEyeAngles &&
                !MotionRecorder::ReplayViewAllowsEngineSetEyeAngles())
            {
                return;
            }
            g_origSetEyeAngles(pawn, angle);
        }

        bool ApplyReplayEyeAngles(void *pawn, float pitch, float yaw)
        {
            if (!pawn || !g_origSetEyeAngles)
                return false;

            float angle[3] = {pitch, NormalizeDeg(yaw), 0.0f};
            bool oldGuard = g_replayOwnedSetEyeAngles;
            g_replayOwnedSetEyeAngles = true;
            g_origSetEyeAngles(pawn, angle);
            g_replayOwnedSetEyeAngles = oldGuard;
            return true;
        }

        static float *BC_FASTCALL HookedGetEyeAngles(void *pawn, float *out)
        {
            int slot = pawn ? ControllerSlotForPawn(pawn) : -1;

            if (slot >= 0 && out && MotionRecorder::IsReplaying(slot))
            {
                MovementSnapshot view{};
                if (MotionRecorder::ReplaySpectatorView(slot, view))
                {
                    out[0] = view.pitch;
                    out[1] = NormalizeDeg(view.yaw);
                    out[2] = 0.0f;
                    return out;
                }
            }

            return g_origGetEyeAngles ? g_origGetEyeAngles(pawn, out) : out;
        }

        // Skip Jump under Jump lock; return 0 mimics its own gate-fail.
        static char BC_FASTCALL HookedJump(void *bot, char mustJump)
        {
            int slot = CCSBotToSlot(bot);
            if (slot >= 0 && BotControllerState::GetJump(slot))
                return 0;
            return g_origJump(bot, mustJump);
        }

        // Resolve a sig from gamedata against the loaded server.dll.
        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen)
        {
            g_addrUpdate = Sig::ResolveSig(gd, serverModule, "CCSBot::Update",
                                           errorOut, errorOutLen);
            if (!g_addrUpdate)
            {
                g_status = "failed: Update sig";
                return false;
            }

            g_addrUpkeep = Sig::ResolveSig(gd, serverModule, "CCSBot::Upkeep",
                                           errorOut, errorOutLen);
            if (!g_addrUpkeep)
            {
                g_status = "failed: Upkeep sig";
                return false;
            }

            // Jump is optional; failure leaves all/aim working, only jump dies.
            char jumpErr[256] = {0};
            g_addrJump = Sig::ResolveSig(gd, serverModule, "CCSBot::Jump",
                                         jumpErr, sizeof(jumpErr));
            if (!g_addrJump)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSBot::Jump sig not resolved (%s); jump-lock disabled\n",
                              jumpErr);
                DebugOut(dbg);
            }

            // UpdateLookAngles is optional
            char ulaErr[256] = {0};
            g_addrUpdateLookAngles = Sig::ResolveSig(gd, serverModule,
                                                     "CCSBot::UpdateLookAngles",
                                                     ulaErr, sizeof(ulaErr));
            if (!g_addrUpdateLookAngles)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSBot::UpdateLookAngles sig not resolved (%s); replay view-drive disabled\n",
                              ulaErr);
                DebugOut(dbg);
            }

            // SetEyeAngles is optional; without it replay view falls back to
            // the (smoothing) UpdateLookAngles hook only.
            char seaErr[256] = {0};
            g_addrSetEyeAngles = Sig::ResolveSig(gd, serverModule,
                                                 "CCSPlayerPawn::SetEyeAngles",
                                                 seaErr, sizeof(seaErr));
            if (!g_addrSetEyeAngles)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSPlayerPawn::SetEyeAngles sig not resolved (%s); replay 1:1 view disabled\n",
                              seaErr);
                DebugOut(dbg);
            }

            // GetEyeAngles is optional; replay can still drive server state
            // without it, but first-person spectator camera may read through
            // this getter instead of raw pawn fields.
            char geaErr[256] = {0};
            g_addrGetEyeAngles = Sig::ResolveSig(gd, serverModule,
                                                 "CBasePlayerPawn::GetEyeAngles",
                                                 geaErr, sizeof(geaErr));
            if (!g_addrGetEyeAngles)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CBasePlayerPawn::GetEyeAngles sig not resolved (%s); replay spectator view override disabled\n",
                              geaErr);
                DebugOut(dbg);
            }

            // required: Update
            if (!g_hookUpdate.Create(g_addrUpdate,
                                     reinterpret_cast<void *>(&HookedUpdate),
                                     reinterpret_cast<void **>(&g_origUpdate)) ||
                !g_hookUpdate.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook CCSBot::Update failed");
                g_hookUpdate.Remove();
                g_origUpdate = nullptr;
                g_status = "failed: hook Update";
                return false;
            }

            // required: Upkeep
            if (!g_hookUpkeep.Create(g_addrUpkeep,
                                     reinterpret_cast<void *>(&HookedUpkeep),
                                     reinterpret_cast<void **>(&g_origUpkeep)) ||
                !g_hookUpkeep.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook CCSBot::Upkeep failed");
                g_hookUpkeep.Remove();
                g_origUpkeep = nullptr;
                g_hookUpdate.Remove();
                g_origUpdate = nullptr;
                g_status = "failed: hook Upkeep";
                return false;
            }

            // optional: Jump
            if (g_addrJump)
            {
                if (!g_hookJump.Create(g_addrJump,
                                       reinterpret_cast<void *>(&HookedJump),
                                       reinterpret_cast<void **>(&g_origJump)) ||
                    !g_hookJump.Enable())
                {
                    DebugOut("[BotController] WARN: hook CCSBot::Jump failed; jump-lock disabled\n");
                    g_hookJump.Remove();
                    g_origJump = nullptr;
                    g_addrJump = nullptr;
                }
            }

            // optional: UpdateLookAngles
            if (g_addrUpdateLookAngles)
            {
                if (!g_hookUpdateLookAngles.Create(g_addrUpdateLookAngles,
                                                   reinterpret_cast<void *>(&HookedUpdateLookAngles),
                                                   reinterpret_cast<void **>(&g_origUpdateLookAngles)) ||
                    !g_hookUpdateLookAngles.Enable())
                {
                    DebugOut("[BotController] WARN: hook UpdateLookAngles failed; replay view-drive disabled\n");
                    g_hookUpdateLookAngles.Remove();
                    g_origUpdateLookAngles = nullptr;
                    g_addrUpdateLookAngles = nullptr;
                }
            }

            // optional: SetEyeAngles
            if (g_addrSetEyeAngles)
            {
                if (!g_hookSetEyeAngles.Create(g_addrSetEyeAngles,
                                               reinterpret_cast<void *>(&HookedSetEyeAngles),
                                               reinterpret_cast<void **>(&g_origSetEyeAngles)) ||
                    !g_hookSetEyeAngles.Enable())
                {
                    DebugOut("[BotController] WARN: hook SetEyeAngles failed; replay 1:1 view disabled\n");
                    g_hookSetEyeAngles.Remove();
                    g_origSetEyeAngles = nullptr;
                    g_addrSetEyeAngles = nullptr;
                }
            }

            // optional: GetEyeAngles
            if (g_addrGetEyeAngles)
            {
                if (!g_hookGetEyeAngles.Create(g_addrGetEyeAngles,
                                               reinterpret_cast<void *>(&HookedGetEyeAngles),
                                               reinterpret_cast<void **>(&g_origGetEyeAngles)) ||
                    !g_hookGetEyeAngles.Enable())
                {
                    DebugOut("[BotController] WARN: hook GetEyeAngles failed; replay spectator view override disabled\n");
                    g_hookGetEyeAngles.Remove();
                    g_origGetEyeAngles = nullptr;
                    g_addrGetEyeAngles = nullptr;
                }
            }

            g_installed = true;
            g_status = "ok";

            char dbg[400];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] Update@%p Upkeep@%p Jump@%p ULA@%p SEA@%p GEA@%p\n",
                          g_addrUpdate, g_addrUpkeep, g_addrJump,
                          g_addrUpdateLookAngles, g_addrSetEyeAngles,
                          g_addrGetEyeAngles);
            DebugOut(dbg);
            return true;
        }

        void Remove()
        {
            if (!g_installed)
                return;
            g_hookGetEyeAngles.Remove();
            g_origGetEyeAngles = nullptr;
            g_hookSetEyeAngles.Remove();
            g_origSetEyeAngles = nullptr;
            g_hookUpdateLookAngles.Remove();
            g_origUpdateLookAngles = nullptr;
            g_hookJump.Remove();
            g_origJump = nullptr;
            g_hookUpkeep.Remove();
            g_origUpkeep = nullptr;
            g_hookUpdate.Remove();
            g_origUpdate = nullptr;
            g_installed = false;
            g_status = "not_attempted";
        }

        const char *Status() { return g_status.c_str(); }
        void *UpdateAddress() { return g_addrUpdate; }
        void *UpkeepAddress() { return g_addrUpkeep; }
        void *JumpAddress() { return g_addrJump; }
        void *UpdateLookAnglesAddress() { return g_addrUpdateLookAngles; }
        void *SetEyeAnglesAddress() { return g_addrSetEyeAngles; }
        void *GetEyeAnglesAddress() { return g_addrGetEyeAngles; }
    }
}
