// BotLocker console commands: bl_lock / bl_unlock / bl_unlock_all / bl_status.

#include "commands.h"
#include "dispatch.h"
#include "WeaponLocker.h"
#include "BotLocker.h"
#include "InputInjector.h"
#include "WeaponLockerState.h"
#include "BotLockerState.h"

#include <tier0/dbg.h>
#include <convar.h>
#include <eiface.h>
#include <playerslot.h>

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace BotLocker
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
    }
}

CON_COMMAND_F(bl_lock,
              "bl_lock <all|aim|jump|weapon> <slot> [slot1..slot5]  "
              "Lock a bot. weapon mode requires the weapon slot.",
              FCVAR_NONE)
{
    using namespace BotLocker;

    if (args.ArgC() < 3)
    {
        Commands::PrintToCaller(context,
                                "usage: bl_lock <all|aim|jump|weapon> <slot> [slot1..slot5]\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BL] error: kind must be all|aim|jump|weapon\n");
        return;
    }

    const int slot = std::atoi(args.Arg(2));
    int arg = 0;

    if (kind == LockKind::Weapon)
    {
        if (args.ArgC() < 4)
        {
            Commands::PrintToCaller(context,
                                    "usage: bl_lock weapon <slot> <slot1..slot5>\n");
            return;
        }
        const auto tgt = Commands::ParseTarget(args.Arg(3));
        if (tgt == LockTarget::None)
        {
            Commands::PrintToCaller(context,
                                    "[BL] error: weapon target must be slot1..slot5\n");
            return;
        }
        arg = static_cast<int>(tgt);
    }

    int rc = Dispatch::Lock(slot, kind, arg);
    if (rc == 0)
    {
        if (kind == LockKind::Weapon)
            Commands::PrintToCaller(context,
                                    "[BL] locked slot %d weapon -> %s\n", slot,
                                    Commands::TargetName(static_cast<LockTarget>(arg)));
        else
            Commands::PrintToCaller(context,
                                    "[BL] locked slot %d (%s)\n", slot, Commands::KindName(kind));
    }
    else
    {
        Commands::PrintToCaller(context,
                                "[BL] error: lock failed (rc=%d)\n", rc);
    }
}

CON_COMMAND_F(bl_unlock,
              "bl_unlock <all|aim|jump|weapon> <slot>  Release one lock on a bot.",
              FCVAR_NONE)
{
    using namespace BotLocker;

    if (args.ArgC() < 3)
    {
        Commands::PrintToCaller(context,
                                "usage: bl_unlock <all|aim|jump|weapon> <slot>\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BL] error: kind must be all|aim|jump|weapon\n");
        return;
    }

    const int slot = std::atoi(args.Arg(2));
    int rc = Dispatch::Unlock(slot, kind);
    if (rc == 0)
        Commands::PrintToCaller(context,
                                "[BL] unlocked slot %d (%s)\n", slot, Commands::KindName(kind));
    else
        Commands::PrintToCaller(context,
                                "[BL] error: unlock failed (rc=%d)\n", rc);
}

CON_COMMAND_F(bl_unlock_all,
              "bl_unlock_all <all|aim|jump|weapon>  Release every lock of that kind.",
              FCVAR_NONE)
{
    using namespace BotLocker;

    if (args.ArgC() < 2)
    {
        Commands::PrintToCaller(context,
                                "usage: bl_unlock_all <all|aim|jump|weapon>\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BL] error: kind must be all|aim|jump|weapon\n");
        return;
    }

    int rc = Dispatch::UnlockAll(kind);
    if (rc == 0)
        Commands::PrintToCaller(context,
                                "[BL] unlocked all (%s)\n", Commands::KindName(kind));
    else
        Commands::PrintToCaller(context,
                                "[BL] error: unlock_all failed (rc=%d)\n", rc);
}

CON_COMMAND_F(bl_status,
              "bl_status  Print hook status and every per-slot lock.",
              FCVAR_NONE)
{
    using namespace BotLocker;

    // Hooks
    Commands::PrintToCaller(context,
                            "[BL] weapon hooks: %s | EquipBest=%p EquipPistol=%p SelectItem=%p GetSlot=%p\n",
                            WeaponLockerHooks::Status(),
                            WeaponLockerHooks::EquipBestWeaponAddress(),
                            WeaponLockerHooks::EquipPistolAddress(),
                            WeaponLockerHooks::SelectItemAddress(),
                            WeaponLockerHooks::GetSlotAddress());

    Commands::PrintToCaller(context,
                            "[BL] bot hooks:    %s | Update=%p Upkeep=%p Jump=%p\n",
                            BotLockerHooks::Status(),
                            BotLockerHooks::UpdateAddress(),
                            BotLockerHooks::UpkeepAddress(),
                            BotLockerHooks::JumpAddress());

    Commands::PrintToCaller(context,
                            "[BL] input inject: %s | ProcessUsercmd=%p\n",
                            InputInjector::Status(),
                            InputInjector::ProcessUsercmdAddress());

    Commands::PrintToCaller(context,
                            "[BL] usercmd hook fired: %llu times | last slot=%d\n",
                            (unsigned long long)InputInjector::HookCallCount(),
                            InputInjector::LastResolvedSlot());

    // All lock
    int nAll = BotLockerState::CountAll();
    Commands::PrintToCaller(context, "[BL] all-locked count:    %d\n", nAll);
    if (nAll > 0)
    {
        for (int s = 0; s < BotLockerState::kMaxSlots; ++s)
            if (BotLockerState::GetAll(s))
                Commands::PrintToCaller(context, "[BL]   all   slot %2d\n", s);
    }

    // Aim lock
    int nAim = BotLockerState::CountAim();
    Commands::PrintToCaller(context, "[BL] aim-locked count:    %d\n", nAim);
    if (nAim > 0)
    {
        for (int s = 0; s < BotLockerState::kMaxSlots; ++s)
            if (BotLockerState::GetAim(s))
                Commands::PrintToCaller(context, "[BL]   aim   slot %2d\n", s);
    }

    // Jump lock
    int nJump = BotLockerState::CountJump();
    Commands::PrintToCaller(context, "[BL] jump-locked count:   %d\n", nJump);
    if (nJump > 0)
    {
        for (int s = 0; s < BotLockerState::kMaxSlots; ++s)
            if (BotLockerState::GetJump(s))
                Commands::PrintToCaller(context, "[BL]   jump  slot %2d\n", s);
    }

    // Weapon lock
    int nWp = WeaponLockerState::CountLocked();
    Commands::PrintToCaller(context, "[BL] weapon-locked count: %d\n", nWp);
    if (nWp > 0)
    {
        for (int s = 0; s < WeaponLockerState::kMaxSlots; ++s)
        {
            auto t = WeaponLockerState::Get(s);
            if (t != LockTarget::None)
                Commands::PrintToCaller(context, "[BL]   weapon slot %2d -> %s\n",
                                        s, Commands::TargetName(t));
        }
    }

    // Input injection
    int nInj = InputInjector::CountActive();
    Commands::PrintToCaller(context, "[BL] inject-active count: %d\n", nInj);
    if (nInj > 0)
    {
        for (int s = 0; s < InputInjector::kMaxSlots; ++s)
            if (InputInjector::IsActive(s))
                Commands::PrintToCaller(context, "[BL]   inject slot %2d\n", s);
    }
}
