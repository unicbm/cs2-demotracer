// Convert a CCSBot* (server-side entity) to its player slot (0..63).

#pragma once

#include <cstddef>
#include <type_traits>

namespace BotController
{
    // Diagnostic: pawn pointer + pawn entindex used in slot computation.
    struct SlotResolution
    {
        void *pawn;
        int pawnEntIndex;
        int slot; // -1 on failure
    };

    // Returns slot in [0, 63] on success, or -1 if the pointer doesn't look
    // like a CCSBot.
    int CCSBotToSlot(void *bot);

    // Full diagnostic version returning intermediate values.
    SlotResolution ResolveSlot(void *bot);

    // Some bot helpers are called with a small context object whose +0x10 field
    // points back at the CCSBot. Try direct CCSBot first, then that context.
    SlotResolution ResolveSlotFromBotOrContext(void *botOrContext);
    int CCSBotContextToSlot(void *botOrContext);

    bool TryReadMemory(void *base, int offset, void *out, size_t size);
    bool TryWriteMemory(void *base, int offset, const void *value, size_t size);

    template <typename T>
    bool ReadField(void *base, int offset, T &out)
    {
        static_assert(std::is_trivially_copyable_v<T>);
        return TryReadMemory(base, offset, &out, sizeof(T));
    }

    template <typename T>
    bool WriteField(void *base, int offset, const T &value)
    {
        static_assert(std::is_trivially_copyable_v<T>);
        return TryWriteMemory(base, offset, &value, sizeof(T));
    }

    // pawn->m_hController only, no m_hOriginalController fallback
    // used to detect human takeover
    int ControllerSlotForPawn(void *pawn);

    // CCSPlayerController* (PhysicsSimulate arg0) -> slot via its own ehandle.
    int ControllerToSlot(void *controller);
}
