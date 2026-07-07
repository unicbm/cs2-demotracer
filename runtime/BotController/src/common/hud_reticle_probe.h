#pragma once

#include <cstdint>

namespace BotController::HudReticleProbe
{
    constexpr int kActionInstall = 1 << 0;
    constexpr int kActionRemove = 1 << 1;
    constexpr int kActionConfigure = 1 << 2;

    constexpr int kFlagPatchPaintConfig = 1 << 0;
    constexpr int kFlagUseForcedPaintConfig = 1 << 1;

    struct PaintConfigOverride
    {
        int32_t size;
        int32_t style;
        int32_t color;
        int32_t drawOutline;
        int32_t dot;
        int32_t gapUseWeaponValue;
        int32_t useAlpha;
        int32_t tStyle;
        int32_t gap100;
        int32_t size100;
        int32_t thickness100;
        int32_t outline100;
        int32_t alpha;
        int32_t red;
        int32_t green;
        int32_t blue;
    };

#pragma pack(push, 4)
    struct ProbeState
    {
        int32_t size;
        int32_t rc;
        int32_t installed;
        int32_t enabled;
        int32_t actionsApplied;

        uint64_t clientBase;
        uint64_t configTargetPtr;
        uint64_t configOriginalPtr;

        int32_t flags;
        int32_t configInstallRc;
        int32_t configCalls;
        int32_t configPatched;
        int32_t configErrors;

        int32_t configModeBefore;
        int32_t configModeAfter;
        int32_t configColorBefore;
        int32_t configColorAfter;
        int32_t configGap100Before;
        int32_t configGap100After;
        int32_t configSize100Before;
        int32_t configSize100After;
        int32_t configThickness100Before;
        int32_t configThickness100After;
        int32_t configDotBefore;
        int32_t configDotAfter;
        int32_t configUseAlphaAfter;
        int32_t configAlphaAfter;
        int32_t configOutline100After;
        uint64_t configRgbaPacked;
        int32_t configLiveGap100Before;
        int32_t configLiveGap100After;
        int32_t configSmoothGap100Before;
        int32_t configSmoothGap100After;
        int32_t configRecoilAfter;
        int32_t configGapUseWeaponAfter;

        int32_t configGuardMatched;
        int32_t configGuardMissed;
        int32_t configGuardActive;
        int32_t configMapCount;
    };
#pragma pack(pop)

    static_assert(sizeof(ProbeState) == 172);

    int Probe(int action, int forceMode, int forceGap, int forceRadius, int flags, ProbeState *out, int size);
    int SetPaintConfigMapEntry(int slot, int pawnIndex, int weaponIndex, const PaintConfigOverride *config, int size);
    int ClearPaintConfigMapEntry(int slot);
    int ClearPaintConfigMap();
    void Remove();
} // namespace BotController::HudReticleProbe
