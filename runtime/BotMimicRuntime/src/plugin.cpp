// BotLocker native Metamod:Source plugin entry point.

#include <ISmmPlugin.h>

#include <Windows.h>
#include <cstdio>
#include <cstring>
#include <string>

#include <eiface.h>
#include <icvar.h>
#include <convar.h>
#include <interfaces/interfaces.h>

#include "WeaponLocker.h"
#include "BotLocker.h"
#include "InputInjector.h"
#include "MotionRecorder.h"
#include "dispatch.h"
#include "WeaponLockerState.h"
#include "BotLockerState.h"
#include "commands.h"

class BotLockerPlugin : public ISmmPlugin
{
public:
    bool Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late) override;
    bool Unload(char *error, size_t maxlen) override;

    bool Pause(char * /*error*/, size_t /*maxlen*/) override { return true; }
    bool Unpause(char * /*error*/, size_t /*maxlen*/) override { return true; }
    void AllPluginsLoaded() override {}

    const char *GetAuthor() override { return "XBribo(๑•.•๑)"; }
    const char *GetName() override { return "BotLocker"; }
    const char *GetDescription() override { return "Lock and Record CS2 bots."; }
    const char *GetURL() override { return ""; }
    const char *GetLicense() override { return "GPLv3"; }
    const char *GetVersion() override { return "0.5.0"; }
    const char *GetDate() override { return __DATE__; }
    const char *GetLogTag() override { return "BL"; }
};

BotLockerPlugin g_botLockerPlugin;
PLUGIN_EXPOSE(BotLockerPlugin, g_botLockerPlugin);

static HMODULE GetSelfModule()
{
    HMODULE mod = nullptr;
    GetModuleHandleExA(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCSTR>(&GetSelfModule),
        &mod);
    return mod;
}

// gamedata.json
static std::string ComputeGamedataPath()
{
    char path[MAX_PATH] = {0};
    if (GetModuleFileNameA(GetSelfModule(), path, MAX_PATH) == 0)
        return "";

    for (int i = 0; i < 3; ++i)
    {
        char *slash = std::strrchr(path, '\\');
        if (!slash)
            return "";
        *slash = '\0';
    }
    std::string result(path);
    result += "\\gamedata.json";
    return result;
}

bool BotLockerPlugin::Load(PluginId id, ISmmAPI *ismm,
                           char *error, size_t maxlen, bool /*late*/)
{
    PLUGIN_SAVEVARS();

    g_pCVar = static_cast<ICvar *>(
        ismm->GetEngineFactory()(CVAR_INTERFACE_VERSION, nullptr));
    if (!g_pCVar)
    {
        std::snprintf(error, maxlen,
                      "Failed to get ICvar (%s) via engine factory",
                      CVAR_INTERFACE_VERSION);
        return false;
    }
    ConVar_Register(FCVAR_RELEASE | FCVAR_GAMEDLL);

    // IVEngineServer2::ClientCommand
    BotLocker::Dispatch::g_pEngine = static_cast<IVEngineServer2 *>(
        ismm->GetEngineFactory()(INTERFACEVERSION_VENGINESERVER, nullptr));
    if (!BotLocker::Dispatch::g_pEngine)
    {
        std::snprintf(error, maxlen,
                      "Failed to get IVEngineServer2 (%s)",
                      INTERFACEVERSION_VENGINESERVER);
        return false;
    }

    // Need ISource2GameClients only as the anchor for sig-scan
    void *serverIface =
        ismm->GetServerFactory()(INTERFACEVERSION_SERVERGAMECLIENTS, nullptr);
    if (!serverIface)
    {
        std::snprintf(error, maxlen,
                      "Failed to get ISource2GameClients (%s)",
                      INTERFACEVERSION_SERVERGAMECLIENTS);
        return false;
    }

    // Engine interface used by console command output (ClientPrintf).
    BotLocker::Commands::g_pEngine = BotLocker::Dispatch::g_pEngine;

    std::string gamedataPath = ComputeGamedataPath();
    if (gamedataPath.empty())
    {
        std::snprintf(error, maxlen, "Failed to compute gamedata.json path");
        return false;
    }

    if (!BotLocker::WeaponLockerHooks::Install(gamedataPath, serverIface,
                                               error, maxlen))
        return false;

    if (!BotLocker::BotLockerHooks::Install(gamedataPath, serverIface,
                                            error, maxlen))
    {
        BotLocker::WeaponLockerHooks::Remove();
        return false;
    }

    // ProcessUsercmd hook for demo-replay UserCmd injection. Optional: a sig
    // miss only kills replay injection, not the lock hooks above.
    char injErr[256] = {0};
    if (!BotLocker::InputInjector::Install(gamedataPath, serverIface,
                                           injErr, sizeof(injErr)))
    {
        char dbg[320];
        std::snprintf(dbg, sizeof(dbg),
                      "[BotLocker] WARN: InputInjector::Install failed (%s); "
                      "BotLocker_InjectUserCmd will be a no-op\n",
                      injErr);
        OutputDebugStringA(dbg);
    }

    OutputDebugStringA("[BotLocker] plugin loaded successfully\n");
    return true;
}

bool BotLockerPlugin::Unload(char * /*error*/, size_t /*maxlen*/)
{
    BotLocker::MotionRecorder::ClearAll();
    BotLocker::InputInjector::Remove();
    BotLocker::BotLockerHooks::Remove();
    BotLocker::WeaponLockerHooks::Remove();
    BotLocker::WeaponLockerState::ClearAll();
    BotLocker::BotLockerState::ClearAllAll();
    BotLocker::BotLockerState::ClearAllAim();
    BotLocker::BotLockerState::ClearAllJump();
    BotLocker::Dispatch::g_pEngine = nullptr;
    BotLocker::Commands::g_pEngine = nullptr;
    ConVar_Unregister();
    g_pCVar = nullptr;
    OutputDebugStringA("[BotLocker] plugin unloaded\n");
    return true;
}
