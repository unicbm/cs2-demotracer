// Sig scanning + gamedata.json loader

#pragma once

#include <Windows.h>
#include <cstdint>
#include <string>
#include <vector>

namespace BotLocker::Sig
{
    // Read a UTF-8 file into a string. Empty on failure.
    std::string ReadFile(const std::string &path);

    // Look up "<name>.signatures.windows" string from gamedataText.
    std::string FindWindowsSig(const std::string &gamedataText, const std::string &name);

    // "AA BB ? CC" -> bytes + wildcard mask
    bool ParseSigString(const std::string &sigStr,
                        std::vector<uint8_t> &outBytes,
                        std::vector<bool> &outWild);

    // Wildcard scan over a module's image; returns first match or nullptr.
    void *FindPatternIn(HMODULE module,
                        const std::vector<uint8_t> &pattern,
                        const std::vector<bool> &wild);

    // Resolve real CS2 server.dll
    HMODULE ModuleFromInterfacePtr(void *interfacePtr);
}
