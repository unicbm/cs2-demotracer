#include "projectile_birth_align.h"

#include "version_targets.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstring>
#include <mutex>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

namespace BotController::ProjectileBirthAlign
{
    namespace
    {
        constexpr int kMaxPending = 64;
        constexpr int kMaxAttempts = 4;

        struct Pending
        {
            uint64_t entityPtr;
            std::array<float, 3> position;
            std::array<float, 3> velocity;
            int attemptsRemaining;
        };

        std::mutex g_mutex;
        std::vector<Pending> g_pending;
        int g_initialPositionOffset = -1;
        int g_initialVelocityOffset = -1;
        int g_queued = 0;
        int g_applied = 0;
        int g_expired = 0;
        int g_failed = 0;

        bool CanWriteMemory(void *ptr, size_t len)
        {
            if (!ptr || len == 0)
                return false;

#ifdef _WIN32
            MEMORY_BASIC_INFORMATION mbi{};
            if (VirtualQuery(ptr, &mbi, sizeof(mbi)) == 0)
                return false;
            if (mbi.State != MEM_COMMIT)
                return false;
            if (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))
                return false;

            const DWORD writable =
                PAGE_READWRITE | PAGE_WRITECOPY |
                PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            if ((mbi.Protect & writable) == 0)
                return false;

            const auto begin = reinterpret_cast<uintptr_t>(ptr);
            const auto end = begin + len;
            const auto regionEnd = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
            return end >= begin && end <= regionEnd;
#else
            return true;
#endif
        }

        void WriteVec3(char *base, int offset, const std::array<float, 3> &value)
        {
            std::memcpy(base + offset, value.data(), sizeof(float) * value.size());
        }

        bool Apply(Pending &pending)
        {
            if (g_initialPositionOffset < 0 || g_initialVelocityOffset < 0)
                return false;

            auto *entity = reinterpret_cast<char *>(static_cast<uintptr_t>(pending.entityPtr));
            if (!entity)
                return false;

            if (!CanWriteMemory(entity + g_initialPositionOffset, sizeof(float) * 3) ||
                !CanWriteMemory(entity + g_initialVelocityOffset, sizeof(float) * 3) ||
                !CanWriteMemory(entity + targets::kEnt_AbsVelocity, sizeof(float) * 3))
            {
                return false;
            }

            WriteVec3(entity, g_initialPositionOffset, pending.position);
            WriteVec3(entity, g_initialVelocityOffset, pending.velocity);
            WriteVec3(entity, targets::kEnt_AbsVelocity, pending.velocity);

            if (CanWriteMemory(entity + targets::kEnt_GameSceneNode, sizeof(void *)))
            {
                auto *node = *reinterpret_cast<char **>(entity + targets::kEnt_GameSceneNode);
                if (node && CanWriteMemory(node + targets::kNode_AbsOrigin, sizeof(float) * 3))
                    WriteVec3(node, targets::kNode_AbsOrigin, pending.position);
            }

            return true;
        }
    } // namespace

    int ConfigureOffsets(int initialPositionOffset, int initialVelocityOffset)
    {
        if (initialPositionOffset < 0 || initialVelocityOffset < 0)
            return -2;

        std::scoped_lock lock(g_mutex);
        g_initialPositionOffset = initialPositionOffset;
        g_initialVelocityOffset = initialVelocityOffset;
        return 0;
    }

    int Queue(
        uint64_t entityPtr,
        float posX,
        float posY,
        float posZ,
        float velX,
        float velY,
        float velZ)
    {
        if (entityPtr == 0)
            return -2;

        std::scoped_lock lock(g_mutex);
        if (g_initialPositionOffset < 0 || g_initialVelocityOffset < 0)
            return -3;

        if (static_cast<int>(g_pending.size()) >= kMaxPending)
        {
            g_pending.erase(g_pending.begin());
            ++g_expired;
        }

        g_pending.push_back(Pending{
            entityPtr,
            {posX, posY, posZ},
            {velX, velY, velZ},
            kMaxAttempts});
        ++g_queued;
        return 0;
    }

    int Clear()
    {
        std::scoped_lock lock(g_mutex);
        const int cleared = static_cast<int>(g_pending.size());
        g_pending.clear();
        return cleared;
    }

    int GetStatus(Status *out, int size)
    {
        if (!out || size < static_cast<int>(sizeof(Status)))
            return -1;

        std::scoped_lock lock(g_mutex);
        Status status{};
        status.size = static_cast<int32_t>(sizeof(Status));
        status.configured = (g_initialPositionOffset >= 0 && g_initialVelocityOffset >= 0) ? 1 : 0;
        status.pending = static_cast<int32_t>(g_pending.size());
        status.queued = g_queued;
        status.applied = g_applied;
        status.expired = g_expired;
        status.failed = g_failed;
        status.initialPositionOffset = g_initialPositionOffset;
        status.initialVelocityOffset = g_initialVelocityOffset;
        std::memcpy(out, &status, sizeof(status));
        return 0;
    }

    void ProcessPending()
    {
        std::scoped_lock lock(g_mutex);
        if (g_pending.empty())
            return;

        auto it = g_pending.begin();
        while (it != g_pending.end())
        {
            if (Apply(*it))
            {
                ++g_applied;
                it = g_pending.erase(it);
                continue;
            }

            --it->attemptsRemaining;
            if (it->attemptsRemaining <= 0)
            {
                ++g_failed;
                it = g_pending.erase(it);
            }
            else
            {
                ++it;
            }
        }
    }
} // namespace BotController::ProjectileBirthAlign
