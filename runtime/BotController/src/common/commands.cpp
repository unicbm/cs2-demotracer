// BotController console commands: bc_lock / bc_unlock / bc_unlock_all / bc_status.

#include "commands.h"
#include "dispatch.h"
#include "WeaponLocker.h"
#include "BotController.h"
#include "InputInjector.h"
#include "MotionRecorder.h"
#include "WeaponLockerState.h"
#include "BotControllerState.h"

#include <tier0/dbg.h>
#include <convar.h>
#include <eiface.h>
#include <playerslot.h>

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace BotController
{
    namespace Commands
    {
        IVEngineServer2 *g_pEngine = nullptr;

        // ClientPrintf to the calling player, or server log if from console.
        void PrintToCaller(const CCommandContext &context, const char *fmt, ...)
        {
            char buf[1024];
            va_list args;
            va_start(args, fmt);
            std::vsnprintf(buf, sizeof(buf), fmt, args);
            va_end(args);

            const CPlayerSlot slot = context.GetPlayerSlot();
            if (g_pEngine && slot.IsValid())
                g_pEngine->ClientPrintf(slot, buf);
            else
                Msg("%s", buf);
        }

        // Parse kind string into LockKind.
        static bool ParseKind(const char *s, LockKind &out)
        {
            if (!s)
                return false;
            if (std::strcmp(s, "all") == 0)
            {
                out = LockKind::All;
                return true;
            }
            if (std::strcmp(s, "aim") == 0)
            {
                out = LockKind::Aim;
                return true;
            }
            if (std::strcmp(s, "weapon") == 0)
            {
                out = LockKind::Weapon;
                return true;
            }
            if (std::strcmp(s, "jump") == 0)
            {
                out = LockKind::Jump;
                return true;
            }
            return false;
        }

        // Parse "slotN" into LockTarget.
        static LockTarget ParseTarget(const char *s)
        {
            if (!s)
                return LockTarget::None;
            if (std::strcmp(s, "slot1") == 0)
                return LockTarget::Slot1;
            if (std::strcmp(s, "slot2") == 0)
                return LockTarget::Slot2;
            if (std::strcmp(s, "slot3") == 0)
                return LockTarget::Slot3;
            if (std::strcmp(s, "slot4") == 0)
                return LockTarget::Slot4;
            if (std::strcmp(s, "slot5") == 0)
                return LockTarget::Slot5;
            return LockTarget::None;
        }

        static const char *TargetName(LockTarget t)
        {
            switch (t)
            {
            case LockTarget::Slot1:
                return "slot1";
            case LockTarget::Slot2:
                return "slot2";
            case LockTarget::Slot3:
                return "slot3";
            case LockTarget::Slot4:
                return "slot4";
            case LockTarget::Slot5:
                return "slot5";
            default:
                return "none";
            }
        }

        static const char *KindName(LockKind k)
        {
            switch (k)
            {
            case LockKind::All:
                return "all";
            case LockKind::Aim:
                return "aim";
            case LockKind::Weapon:
                return "weapon";
            case LockKind::Jump:
                return "jump";
            }
            return "?";
        }

        static const char *ViewDebugName(int target, char *buf, size_t len)
        {
            if (target == -2)
                return "all";
            if (target >= 0)
            {
                std::snprintf(buf, len, "slot%d", target);
                return buf;
            }
            return "off";
        }
    }
}

CON_COMMAND_F(bc_lock,
              "bc_lock <all|aim|jump|weapon> <slot> [slot1..slot5]  "
              "Lock a bot. weapon mode requires the weapon slot.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 3)
    {
        Commands::PrintToCaller(context,
                                "usage: bc_lock <all|aim|jump|weapon> <slot> [slot1..slot5]\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BC] error: kind must be all|aim|jump|weapon\n");
        return;
    }

    const int slot = std::atoi(args.Arg(2));
    int arg = 0;

    if (kind == LockKind::Weapon)
    {
        if (args.ArgC() < 4)
        {
            Commands::PrintToCaller(context,
                                    "usage: bc_lock weapon <slot> <slot1..slot5>\n");
            return;
        }
        const auto tgt = Commands::ParseTarget(args.Arg(3));
        if (tgt == LockTarget::None)
        {
            Commands::PrintToCaller(context,
                                    "[BC] error: weapon target must be slot1..slot5\n");
            return;
        }
        arg = static_cast<int>(tgt);
    }

    int rc = Dispatch::Lock(slot, kind, arg);
    if (rc == 0)
    {
        if (kind == LockKind::Weapon)
            Commands::PrintToCaller(context,
                                    "[BC] locked slot %d weapon -> %s\n", slot,
                                    Commands::TargetName(static_cast<LockTarget>(arg)));
        else
            Commands::PrintToCaller(context,
                                    "[BC] locked slot %d (%s)\n", slot, Commands::KindName(kind));
    }
    else
    {
        Commands::PrintToCaller(context,
                                "[BC] error: lock failed (rc=%d)\n", rc);
    }
}

CON_COMMAND_F(bc_unlock,
              "bc_unlock <all|aim|jump|weapon> <slot>  Release one lock on a bot.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 3)
    {
        Commands::PrintToCaller(context,
                                "usage: bc_unlock <all|aim|jump|weapon> <slot>\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BC] error: kind must be all|aim|jump|weapon\n");
        return;
    }

    const int slot = std::atoi(args.Arg(2));
    int rc = Dispatch::Unlock(slot, kind);
    if (rc == 0)
        Commands::PrintToCaller(context,
                                "[BC] unlocked slot %d (%s)\n", slot, Commands::KindName(kind));
    else
        Commands::PrintToCaller(context,
                                "[BC] error: unlock failed (rc=%d)\n", rc);
}

CON_COMMAND_F(bc_unlock_all,
              "bc_unlock_all <all|aim|jump|weapon>  Release every lock of that kind.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 2)
    {
        Commands::PrintToCaller(context,
                                "usage: bc_unlock_all <all|aim|jump|weapon>\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BC] error: kind must be all|aim|jump|weapon\n");
        return;
    }

    int rc = Dispatch::UnlockAll(kind);
    if (rc == 0)
        Commands::PrintToCaller(context,
                                "[BC] unlocked all (%s)\n", Commands::KindName(kind));
    else
        Commands::PrintToCaller(context,
                                "[BC] error: unlock_all failed (rc=%d)\n", rc);
}

CON_COMMAND_F(bc_view_debug,
              "bc_view_debug <0|1> [slot]  Toggle replay view phase debug logs.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 2)
    {
        Commands::PrintToCaller(context,
                                "usage: bc_view_debug <0|1> [slot]\n");
        return;
    }

    int target = -1;
    if (std::strcmp(args.Arg(1), "0") == 0 ||
        std::strcmp(args.Arg(1), "off") == 0)
    {
        target = -1;
    }
    else if (std::strcmp(args.Arg(1), "1") == 0 ||
             std::strcmp(args.Arg(1), "on") == 0)
    {
        target = -2;
        if (args.ArgC() >= 3)
        {
            target = std::atoi(args.Arg(2));
            if (target < 0 || target >= MotionRecorder::kMaxSlots)
            {
                Commands::PrintToCaller(context,
                                        "[BC] error: slot must be 0..%d\n",
                                        MotionRecorder::kMaxSlots - 1);
                return;
            }
        }
    }
    else
    {
        Commands::PrintToCaller(context,
                                "usage: bc_view_debug <0|1> [slot]\n");
        return;
    }

    MotionRecorder::SetViewDebugTarget(target);
    char name[32];
    Commands::PrintToCaller(context, "[BC] view_debug=%s\n",
                            Commands::ViewDebugName(
                                MotionRecorder::ViewDebugTarget(), name, sizeof(name)));
}

CON_COMMAND_F(bc_status,
              "bc_status  Print hook status and every per-slot lock.",
              FCVAR_NONE)
{
    using namespace BotController;

    // Hooks
    Commands::PrintToCaller(context,
                            "[BC] weapon hooks: %s | EquipBest=%p EquipPistol=%p SelectItem=%p GetSlot=%p\n",
                            WeaponLockerHooks::Status(),
                            WeaponLockerHooks::EquipBestWeaponAddress(),
                            WeaponLockerHooks::EquipPistolAddress(),
                            WeaponLockerHooks::SelectItemAddress(),
                            WeaponLockerHooks::GetSlotAddress());

    Commands::PrintToCaller(context,
                            "[BC] bot hooks:    %s | Update=%p Upkeep=%p Jump=%p\n",
                            BotControllerHooks::Status(),
                            BotControllerHooks::UpdateAddress(),
                            BotControllerHooks::UpkeepAddress(),
                            BotControllerHooks::JumpAddress());

    Commands::PrintToCaller(context,
                            "[BC] input inject: %s | ProcessUsercmd=%p\n",
                            InputInjector::Status(),
                            InputInjector::ProcessUsercmdAddress());

    Commands::PrintToCaller(context,
                            "[BC] usercmd hook fired: %llu times | last slot=%d\n",
                            (unsigned long long)InputInjector::HookCallCount(),
                            InputInjector::LastResolvedSlot());

    char viewDebugName[32];
    Commands::PrintToCaller(context,
                            "[BC] view_debug: %s\n",
                            Commands::ViewDebugName(
                                MotionRecorder::ViewDebugTarget(),
                                viewDebugName, sizeof(viewDebugName)));

    // All lock
    int nAll = BotControllerState::CountAll();
    Commands::PrintToCaller(context, "[BC] all-locked count:    %d\n", nAll);
    if (nAll > 0)
    {
        for (int s = 0; s < BotControllerState::kMaxSlots; ++s)
            if (BotControllerState::GetAll(s))
                Commands::PrintToCaller(context, "[BC]   all   slot %2d\n", s);
    }

    // Aim lock
    int nAim = BotControllerState::CountAim();
    Commands::PrintToCaller(context, "[BC] aim-locked count:    %d\n", nAim);
    if (nAim > 0)
    {
        for (int s = 0; s < BotControllerState::kMaxSlots; ++s)
            if (BotControllerState::GetAim(s))
                Commands::PrintToCaller(context, "[BC]   aim   slot %2d\n", s);
    }

    // Jump lock
    int nJump = BotControllerState::CountJump();
    Commands::PrintToCaller(context, "[BC] jump-locked count:   %d\n", nJump);
    if (nJump > 0)
    {
        for (int s = 0; s < BotControllerState::kMaxSlots; ++s)
            if (BotControllerState::GetJump(s))
                Commands::PrintToCaller(context, "[BC]   jump  slot %2d\n", s);
    }

    // Weapon lock
    int nWp = WeaponLockerState::CountLocked();
    Commands::PrintToCaller(context, "[BC] weapon-locked count: %d\n", nWp);
    if (nWp > 0)
    {
        for (int s = 0; s < WeaponLockerState::kMaxSlots; ++s)
        {
            auto t = WeaponLockerState::Get(s);
            if (t != LockTarget::None)
                Commands::PrintToCaller(context, "[BC]   weapon slot %2d -> %s\n",
                                        s, Commands::TargetName(t));
        }
    }
}
