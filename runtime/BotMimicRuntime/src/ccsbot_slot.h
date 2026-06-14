// Convert a CCSBot* (server-side entity) to its player slot (0..63).

#pragma once

namespace BotLocker
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

    // pawn->m_hController only, no m_hOriginalController fallback
    // used to detect human takeover
    int ControllerSlotForPawn(void *pawn);

    // CCSPlayerController* (PhysicsSimulate arg0) -> slot via its own ehandle.
    int ControllerToSlot(void *controller);
}
