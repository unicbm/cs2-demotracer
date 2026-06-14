#include "Cs2RecFile.h"
#include "MotionRecorder.h"

#include <cstdint>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

namespace
{
    constexpr char kMagic[8] = {'C', 'S', '2', 'B', 'M', 'R', 'E', 'C'};
    constexpr uint32_t kVersion = 1;
    constexpr uint32_t kMaxTicks = 2'000'000;
    constexpr uint32_t kMaxSubticks = 72'000'000;

    template <typename T>
    bool ReadPod(std::ifstream &in, T &out)
    {
        in.read(reinterpret_cast<char *>(&out), sizeof(T));
        return static_cast<bool>(in);
    }

    bool ReadString(std::ifstream &in, std::string &out)
    {
        uint16_t len = 0;
        if (!ReadPod(in, len))
            return false;
        out.assign(len, '\0');
        if (len == 0)
            return true;
        in.read(out.data(), len);
        return static_cast<bool>(in);
    }

    bool ReadSnapshot(std::ifstream &in, BotLocker::MovementSnapshot &out)
    {
        return ReadPod(in, out.originX) &&
               ReadPod(in, out.originY) &&
               ReadPod(in, out.originZ) &&
               ReadPod(in, out.velX) &&
               ReadPod(in, out.velY) &&
               ReadPod(in, out.velZ) &&
               ReadPod(in, out.pitch) &&
               ReadPod(in, out.yaw) &&
               ReadPod(in, out.roll) &&
               ReadPod(in, out.entityFlags) &&
               ReadPod(in, out.moveType) &&
               static_cast<bool>(in.read(reinterpret_cast<char *>(out._pad), sizeof(out._pad))) &&
               ReadPod(in, out.buttons);
    }
}

namespace BotLocker::Cs2RecFile
{
    bool LoadFromFile(int slot, const char *path)
    {
        if (!path || !*path)
            return false;

        std::ifstream in(path, std::ios::binary);
        if (!in)
            return false;

        char magic[8]{};
        in.read(magic, sizeof(magic));
        if (!in || std::memcmp(magic, kMagic, sizeof(kMagic)) != 0)
            return false;

        uint32_t version = 0;
        float tickRate = 0.0f;
        uint32_t round = 0;
        uint8_t side = 0;
        uint32_t flags = 0;
        uint64_t steamId = 0;
        uint32_t tickCount = 0;
        uint32_t subtickCount = 0;
        if (!ReadPod(in, version) ||
            !ReadPod(in, tickRate) ||
            !ReadPod(in, round) ||
            !ReadPod(in, side) ||
            !ReadPod(in, flags) ||
            !ReadPod(in, steamId) ||
            !ReadPod(in, tickCount) ||
            !ReadPod(in, subtickCount))
            return false;

        if (version != kVersion || tickCount == 0 || tickCount > kMaxTicks ||
            subtickCount > kMaxSubticks)
            return false;

        std::string map;
        std::string playerName;
        if (!ReadString(in, map) || !ReadString(in, playerName))
            return false;

        std::vector<ReplayTick> ticks;
        ticks.resize(tickCount);
        uint64_t expectedSubticks = 0;
        for (uint32_t i = 0; i < tickCount; ++i)
        {
            ReplayTick &tick = ticks[i];
            if (!ReadSnapshot(in, tick.pre) ||
                !ReadSnapshot(in, tick.post) ||
                !ReadPod(in, tick.weaponDefIndex) ||
                !ReadPod(in, tick.numSubtick))
                return false;
            expectedSubticks += tick.numSubtick;
            if (expectedSubticks > kMaxSubticks)
                return false;
        }
        if (expectedSubticks != subtickCount)
            return false;

        std::vector<SubtickMove> subticks;
        subticks.resize(subtickCount);
        for (uint32_t i = 0; i < subtickCount; ++i)
        {
            SubtickMove &move = subticks[i];
            if (!ReadPod(in, move.when) ||
                !ReadPod(in, move.button) ||
                !ReadPod(in, move.pressed) ||
                !ReadPod(in, move.analogForward) ||
                !ReadPod(in, move.analogLeft) ||
                !ReadPod(in, move.pitchDelta) ||
                !ReadPod(in, move.yawDelta))
                return false;
        }

        SubtickMove dummySubtick{};
        const SubtickMove *subtickData = subticks.empty() ? &dummySubtick : subticks.data();

        return MotionRecorder::LoadReplay(
            slot,
            ticks.data(),
            static_cast<int>(ticks.size()),
            subtickData,
            static_cast<int>(subticks.size()));
    }
}
