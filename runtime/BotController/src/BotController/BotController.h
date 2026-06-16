// MinHook install/remove for CCSBot Update/Upkeep/Jump.

#pragma once

#include <string>

#include <nlohmann/json.hpp>
#include "sig_scan.h"

namespace BotController
{
    namespace BotControllerHooks
    {
        // Resolve sigs and install detours.
        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen);

        // Disable + remove detours.
        void Remove();

        const char *Status();

        // Apply replay-owned eye angles through the native engine path so
        // derived third-person/body orientation state stays synchronized.
        bool ApplyReplayEyeAngles(void *pawn, float pitch, float yaw);

        void *UpdateAddress();
        void *UpkeepAddress();
        void *JumpAddress();
        void *UpdateLookAnglesAddress();
        void *SetEyeAnglesAddress();
        void *GetEyeAnglesAddress();
    }
}
