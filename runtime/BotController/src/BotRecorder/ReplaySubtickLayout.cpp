#include "ReplaySubtickLayout.h"

#include <cstdint>
#include <limits>

namespace BotController::ReplaySubtickLayout
{
    void ReplayLoadStaging::Swap(ReplayLoadStaging &other) noexcept
    {
        ticks.swap(other.ticks);
        subs.swap(other.subs);
        commands.swap(other.commands);
        movementExtras.swap(other.movementExtras);
        offsets.swap(other.offsets);
    }

    bool TryBuildReplaySubtickOffsets(
        const ReplayTick *ticks,
        int tickCount,
        int subCount,
        std::vector<std::size_t> &offsets) noexcept
    {
        try
        {
            if (!ticks || tickCount < 0 || subCount < 0)
                return false;

            const auto tickSize = static_cast<std::size_t>(tickCount);
            if (tickSize == std::numeric_limits<std::size_t>::max())
                return false;

            std::uint64_t total = 0;
            const auto expected = static_cast<std::uint64_t>(subCount);
            for (std::size_t i = 0; i < tickSize; ++i)
            {
                const std::uint64_t count = ticks[i].numSubtick;
                if (count > static_cast<std::uint64_t>(MotionRecorder::kMaxSubtickPerTick) ||
                    total > std::numeric_limits<std::uint64_t>::max() - count)
                {
                    return false;
                }
                total += count;
                if (total > expected)
                    return false;
            }
            if (total != expected || total > std::numeric_limits<std::size_t>::max())
                return false;

            std::vector<std::size_t> candidate(tickSize + 1, 0);
            std::size_t accumulated = 0;
            for (std::size_t i = 0; i < tickSize; ++i)
            {
                candidate[i] = accumulated;
                accumulated += static_cast<std::size_t>(ticks[i].numSubtick);
            }
            candidate[tickSize] = accumulated;
            offsets.swap(candidate);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    bool TryGetReplaySubtickRange(
        const ReplayTick *ticks,
        std::size_t tickCount,
        const std::vector<std::size_t> &offsets,
        std::size_t subCount,
        std::size_t tickIndex,
        std::size_t &begin,
        std::size_t &end) noexcept
    {
        if (!ticks || tickIndex >= tickCount ||
            tickCount == std::numeric_limits<std::size_t>::max() ||
            offsets.size() != tickCount + 1 || offsets.empty() ||
            offsets.front() != 0 || offsets.back() != subCount)
        {
            return false;
        }

        const std::size_t candidateBegin = offsets[tickIndex];
        const std::size_t candidateEnd = offsets[tickIndex + 1];
        const std::uint32_t expectedCount = ticks[tickIndex].numSubtick;
        if (expectedCount > static_cast<std::uint32_t>(MotionRecorder::kMaxSubtickPerTick) ||
            candidateBegin > candidateEnd || candidateEnd > subCount ||
            candidateEnd - candidateBegin != static_cast<std::size_t>(expectedCount))
        {
            return false;
        }

        begin = candidateBegin;
        end = candidateEnd;
        return true;
    }

    bool TryStageReplayLoad(
        const ReplayTick *ticks,
        int tickCount,
        const SubtickMove *subs,
        int subCount,
        const ReplayCommandFrameData *commands,
        int commandCount,
        const ReplayMovementExtra *movementExtras,
        int movementExtraCount,
        ReplayLoadStaging &staged) noexcept
    {
        try
        {
            if (!ticks || tickCount < 0 || subCount < 0 ||
                (subCount > 0 && !subs) ||
                (commandCount != 0 && commandCount != tickCount) ||
                (commandCount > 0 && !commands) ||
                (movementExtraCount != 0 && movementExtraCount != tickCount) ||
                (movementExtraCount > 0 && !movementExtras))
            {
                return false;
            }

            // Check the raw ABI layout before copying the other parallel
            // buffers, then validate the exact staged tick bytes again below.
            std::vector<std::size_t> rawOffsets;
            if (!TryBuildReplaySubtickOffsets(
                    ticks, tickCount, subCount, rawOffsets))
            {
                return false;
            }

            ReplayLoadStaging candidate;
            if (tickCount > 0)
                candidate.ticks.assign(ticks, ticks + tickCount);
            if (subCount > 0)
                candidate.subs.assign(subs, subs + subCount);
            if (commandCount > 0)
                candidate.commands.assign(commands, commands + commandCount);
            if (movementExtraCount > 0)
            {
                candidate.movementExtras.assign(
                    movementExtras, movementExtras + movementExtraCount);
            }

            const ReplayTick *candidateTicks =
                candidate.ticks.empty() ? ticks : candidate.ticks.data();
            if (!TryBuildReplaySubtickOffsets(
                    candidateTicks, tickCount, subCount, candidate.offsets))
            {
                return false;
            }

            staged.Swap(candidate);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }
} // namespace BotController::ReplaySubtickLayout
