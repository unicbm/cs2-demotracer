// Sig scanning + gamedata.json loader.

#include "sig_scan.h"

#include <psapi.h>

#include <cstdlib>
#include <fstream>
#include <sstream>

namespace BotLocker::Sig
{
    std::string ReadFile(const std::string &path)
    {
        std::ifstream f(path, std::ios::binary);
        if (!f)
            return "";
        std::stringstream ss;
        ss << f.rdbuf();
        return ss.str();
    }

    // Find "<name>.signatures.windows" string value in flat JSON text.
    std::string FindWindowsSig(const std::string &text, const std::string &name)
    {
        std::string quoted = "\"" + name + "\"";
        size_t k = text.find(quoted);
        if (k == std::string::npos)
            return "";
        size_t sigKey = text.find("\"signatures\"", k);
        if (sigKey == std::string::npos)
            return "";
        size_t winKey = text.find("\"windows\"", sigKey);
        if (winKey == std::string::npos)
            return "";
        size_t colon = text.find(':', winKey);
        if (colon == std::string::npos)
            return "";
        size_t q1 = text.find('"', colon + 1);
        if (q1 == std::string::npos)
            return "";
        size_t q2 = text.find('"', q1 + 1);
        if (q2 == std::string::npos)
            return "";
        return text.substr(q1 + 1, q2 - q1 - 1);
    }

    bool ParseSigString(const std::string &sigStr,
                        std::vector<uint8_t> &outBytes,
                        std::vector<bool> &outWild)
    {
        outBytes.clear();
        outWild.clear();
        std::stringstream ss(sigStr);
        std::string tok;
        while (ss >> tok)
        {
            if (tok == "?" || tok == "??")
            {
                outBytes.push_back(0);
                outWild.push_back(true);
                continue;
            }
            if (tok.size() == 0 || tok.size() > 2)
                return false;
            char *end = nullptr;
            unsigned long v = std::strtoul(tok.c_str(), &end, 16);
            if (end == tok.c_str() || *end != '\0' || v > 0xFF)
                return false;
            outBytes.push_back(static_cast<uint8_t>(v));
            outWild.push_back(false);
        }
        return !outBytes.empty();
    }

    void *FindPatternIn(HMODULE module,
                        const std::vector<uint8_t> &pattern,
                        const std::vector<bool> &wild)
    {
        MODULEINFO mi{};
        if (!GetModuleInformation(GetCurrentProcess(), module, &mi, sizeof(mi)))
            return nullptr;
        auto base = static_cast<unsigned char *>(mi.lpBaseOfDll);
        const size_t size = mi.SizeOfImage;
        const size_t plen = pattern.size();
        for (size_t i = 0; i + plen <= size; ++i)
        {
            bool match = true;
            for (size_t j = 0; j < plen; ++j)
            {
                if (!wild[j] && base[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return base + i;
        }
        return nullptr;
    }

    HMODULE ModuleFromInterfacePtr(void *interfacePtr)
    {
        if (!interfacePtr)
            return nullptr;
        void *vtable = *reinterpret_cast<void **>(interfacePtr);
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(vtable, &mbi, sizeof(mbi)))
            return nullptr;
        if (mbi.Type != MEM_IMAGE)
            return nullptr;
        return reinterpret_cast<HMODULE>(mbi.AllocationBase);
    }
}
