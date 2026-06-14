#pragma once

class CCommandContext;
class IVEngineServer2;

namespace BotLocker
{
    namespace Commands
    {
        // Set by plugin.cpp Load(). Used to ClientPrintf back to the player
        // who issued a console command. nullptr -> fall back to server log.
        extern IVEngineServer2 *g_pEngine;

        void PrintToCaller(const CCommandContext &context, const char *fmt, ...);
    }
}
