// MinHook for CCSBot EquipBestWeapon/EquipPistol + WeaponServices::SelectItem.

#pragma once

#include <string>

namespace BotLocker
{
    namespace WeaponLockerHooks
    {
        // Sentinel def index meaning any knife
        constexpr int kKnifeDef = 9001;

        bool Install(const std::string &gamedataPath,
                     void *serverIface,
                     char *errorOut, size_t errorOutLen);

        void Remove();

        const char *Status();

        void *EquipBestWeaponAddress();
        void *EquipPistolAddress();
        void *SelectItemAddress();
        void *GetSlotAddress();

        // Force bot at `slot` to its locked weapon
        // Returns: 0 ok / 1 no ws / 2 no target / 3 hooks not installed.
        int SwitchToLockTarget(int slot, bool quiet = false);

        // ---- helpers for MotionRecorder ----

        // True once GetSlot + SelectItem are resolved and hooks installed.
        bool WeaponHooksReady();

        // Read a weapon's item-definition index (weapon+0x9E0). -1 if null.
        int ReadDefIndex(void *weapon);

        // Active weapon's def index for a WeaponServices*, matched by entity
        // handle against GetSlot(0..4). -1 if none/unresolved (no false match).
        int ActiveWeaponDef(void *ws);

        // First weapon in slots 0..4 whose def index == def. nullptr if absent.
        void *FindWeaponByDef(void *ws, int def);

        // Switch via the original (un-hooked) SelectItem. Proven reliable path.
        bool SelectWeaponRaw(void *ws, void *weapon);

        // Cached WeaponServices* for a bot slot (populated when its AI ticks).
        void *WsForSlot(int slot);
    }
}
