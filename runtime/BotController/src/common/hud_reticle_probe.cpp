#include "hud_reticle_probe.h"

#include "hook.h"
#include "platform.h"
#include "sig_scan.h"

#include <array>
#include <atomic>
#include <climits>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <mutex>

#if defined(_WIN32)
#include <Windows.h>
#endif

namespace BotController::HudReticleProbe
{
    namespace
    {
        // Builds the final HUD crosshair paint config for the currently
        // rendered POV. Resolve it from gamedata because client.dll RVAs move
        // across CS2 updates even when this call contract and layout survive.
        constexpr const char *kPaintConfigBuildSigName =
            "CCSGO_HudReticle::BuildPaintConfig";
        constexpr unsigned char kPaintConfigBuildPrologue[] = {
            0x48, 0x89, 0x5C, 0x24, 0x18, 0x55, 0x56, 0x57};
        constexpr int kPaintConfigMapSlots = 64;

        struct PaintConfigMapEntry
        {
            std::atomic<int> enabled{0};
            std::atomic<int> pawnIndex{-1};
            std::atomic<int> weaponIndex{-1};
            std::atomic<int> style{4};
            std::atomic<int> color{1};
            std::atomic<int> drawOutline{0};
            std::atomic<int> dot{0};
            std::atomic<int> gapUseWeaponValue{0};
            std::atomic<int> useAlpha{0};
            std::atomic<int> tStyle{0};
            std::atomic<int> gap100{0};
            std::atomic<int> size100{100};
            std::atomic<int> thickness100{50};
            std::atomic<int> outline100{100};
            std::atomic<int> alpha{255};
            std::atomic<int> red{50};
            std::atomic<int> green{250};
            std::atomic<int> blue{50};
        };

        struct PaintValues
        {
            bool ok{false};
            int style{4};
            int color{1};
            int drawOutline{0};
            int dot{0};
            int gapUseWeaponValue{0};
            int useAlpha{0};
            int tStyle{0};
            int alpha{255};
            int red{50};
            int green{250};
            int blue{50};
            float gap{0.0f};
            float size{1.0f};
            float thickness{0.5f};
            float outlineThickness{1.0f};
        };

        using PaintConfigBuildFn = void(BC_FASTCALL *)(void *, void *, void *);

        std::mutex g_mutex;
        Hook g_configHook;
        PaintConfigBuildFn g_configOriginal = nullptr;
        std::string g_configSignature;
        uintptr_t g_clientBase = 0;
        uintptr_t g_configTarget = 0;

        std::atomic<int> g_enabled{0};
        std::atomic<int> g_flags{0};
        std::atomic<int> g_configInstallRc{0};
        std::atomic<int> g_configCalls{0};
        std::atomic<int> g_configPatched{0};
        std::atomic<int> g_configErrors{0};
        std::atomic<int> g_configGuardMatched{0};
        std::atomic<int> g_configGuardMissed{0};

        std::atomic<int> g_forcedConfigValid{0};
        std::atomic<int> g_forcedConfigStyle{4};
        std::atomic<int> g_forcedConfigColor{1};
        std::atomic<int> g_forcedConfigDrawOutline{0};
        std::atomic<int> g_forcedConfigDot{0};
        std::atomic<int> g_forcedConfigGapUseWeaponValue{0};
        std::atomic<int> g_forcedConfigUseAlpha{0};
        std::atomic<int> g_forcedConfigTStyle{0};
        std::atomic<int> g_forcedConfigGap100{0};
        std::atomic<int> g_forcedConfigSize100{100};
        std::atomic<int> g_forcedConfigThickness100{50};
        std::atomic<int> g_forcedConfigOutline100{100};
        std::atomic<int> g_forcedConfigAlpha{255};
        std::atomic<int> g_forcedConfigRed{50};
        std::atomic<int> g_forcedConfigGreen{250};
        std::atomic<int> g_forcedConfigBlue{50};

        std::atomic<uint64_t> g_configGuardControllerPtr{0};
        std::atomic<uint64_t> g_configGuardPawnPtr{0};
        std::atomic<uint64_t> g_configGuardWeaponPtr{0};
        std::atomic<int> g_configGuardPawnIndex{-1};
        std::atomic<int> g_configGuardWeaponIndex{-1};
        std::array<PaintConfigMapEntry, kPaintConfigMapSlots> g_paintConfigMap{};
        std::atomic<int> g_paintConfigMapCount{0};

        std::atomic<int> g_configArg1EntIndex{-1};
        std::atomic<int> g_configArg2EntIndex{-1};
        std::atomic<int> g_configModeBefore{INT_MIN};
        std::atomic<int> g_configModeAfter{INT_MIN};
        std::atomic<int> g_configColorBefore{INT_MIN};
        std::atomic<int> g_configColorAfter{INT_MIN};
        std::atomic<int> g_configGap100Before{INT_MIN};
        std::atomic<int> g_configGap100After{INT_MIN};
        std::atomic<int> g_configSize100Before{INT_MIN};
        std::atomic<int> g_configSize100After{INT_MIN};
        std::atomic<int> g_configThickness100Before{INT_MIN};
        std::atomic<int> g_configThickness100After{INT_MIN};
        std::atomic<int> g_configDotBefore{INT_MIN};
        std::atomic<int> g_configDotAfter{INT_MIN};
        std::atomic<int> g_configUseAlphaAfter{INT_MIN};
        std::atomic<int> g_configAlphaAfter{INT_MIN};
        std::atomic<int> g_configOutline100After{INT_MIN};
        std::atomic<int> g_configLiveGap100Before{INT_MIN};
        std::atomic<int> g_configLiveGap100After{INT_MIN};
        std::atomic<int> g_configSmoothGap100Before{INT_MIN};
        std::atomic<int> g_configSmoothGap100After{INT_MIN};
        std::atomic<int> g_configRecoilAfter{INT_MIN};
        std::atomic<int> g_configGapUseWeaponAfter{INT_MIN};
        std::atomic<uint64_t> g_configRgbaPacked{0};

        bool BytesMatch(void *target, const unsigned char *expected, size_t expectedSize)
        {
            if (!target)
                return false;
#if defined(_WIN32)
            __try
            {
#endif
                return std::memcmp(target, expected, expectedSize) == 0;
#if defined(_WIN32)
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
#endif
        }

        bool PaintConfigBuildPrologueMatches(void *target)
        {
            return BytesMatch(target, kPaintConfigBuildPrologue, sizeof(kPaintConfigBuildPrologue));
        }

        int ClampByte(int value)
        {
            if (value < 0)
                return 0;
            if (value > 255)
                return 255;
            return value;
        }

        int RoundFloat(float value)
        {
            if (!std::isfinite(value))
                return INT_MIN;
            return static_cast<int>(std::lround(value));
        }

        float ReadConfigFloat(void *config, uintptr_t offset)
        {
            if (!config)
                return 0.0f;
#if defined(_WIN32)
            __try
            {
#endif
                return *reinterpret_cast<float *>(reinterpret_cast<uintptr_t>(config) + offset);
#if defined(_WIN32)
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return 0.0f;
            }
#endif
        }

        int ReadConfigInt(void *config, uintptr_t offset)
        {
            if (!config)
                return INT_MIN;
#if defined(_WIN32)
            __try
            {
#endif
                return *reinterpret_cast<int32_t *>(reinterpret_cast<uintptr_t>(config) + offset);
#if defined(_WIN32)
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return INT_MIN;
            }
#endif
        }

        int ReadConfigByte(void *config, uintptr_t offset)
        {
            if (!config)
                return INT_MIN;
#if defined(_WIN32)
            __try
            {
#endif
                return *reinterpret_cast<uint8_t *>(reinterpret_cast<uintptr_t>(config) + offset);
#if defined(_WIN32)
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return INT_MIN;
            }
#endif
        }

        void WriteConfigFloat(void *config, uintptr_t offset, float value)
        {
            *reinterpret_cast<float *>(reinterpret_cast<uintptr_t>(config) + offset) = value;
        }

        void WriteConfigInt(void *config, uintptr_t offset, int value)
        {
            *reinterpret_cast<int32_t *>(reinterpret_cast<uintptr_t>(config) + offset) = value;
        }

        void WriteConfigByte(void *config, uintptr_t offset, int value)
        {
            *reinterpret_cast<uint8_t *>(reinterpret_cast<uintptr_t>(config) + offset) =
                static_cast<uint8_t>(ClampByte(value));
        }

        int EntityIndexFromArg(void *arg)
        {
            if (!arg)
                return -1;
#if defined(_WIN32)
            __try
            {
#endif
                const auto base = reinterpret_cast<uintptr_t>(arg);
                const auto identity = *reinterpret_cast<uintptr_t *>(base + 0x10u);
                if (identity == 0)
                    return -1;
                const auto handle = *reinterpret_cast<uint32_t *>(identity + 0x10u);
                return static_cast<int>(handle & 0x7fffu);
#if defined(_WIN32)
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return -1;
            }
#endif
        }

        int RecountPaintConfigMap()
        {
            int count = 0;
            for (const auto &entry : g_paintConfigMap)
            {
                if (entry.enabled.load(std::memory_order_acquire) != 0)
                    ++count;
            }
            g_paintConfigMapCount.store(count, std::memory_order_release);
            return count;
        }

        void ResetPaintConfigMap()
        {
            for (auto &entry : g_paintConfigMap)
            {
                entry.enabled.store(0, std::memory_order_release);
                entry.pawnIndex.store(-1, std::memory_order_relaxed);
                entry.weaponIndex.store(-1, std::memory_order_relaxed);
            }
            g_paintConfigMapCount.store(0, std::memory_order_release);
        }

        void ClearForcedPaintConfig()
        {
            g_forcedConfigValid.store(0, std::memory_order_release);
            g_configGuardControllerPtr.store(0, std::memory_order_release);
            g_configGuardPawnPtr.store(0, std::memory_order_release);
            g_configGuardWeaponPtr.store(0, std::memory_order_release);
            g_configGuardPawnIndex.store(-1, std::memory_order_release);
            g_configGuardWeaponIndex.store(-1, std::memory_order_release);
            ResetPaintConfigMap();
        }

        void Configure(int flags)
        {
            g_flags.store(flags, std::memory_order_relaxed);
            if ((flags & kFlagUseForcedPaintConfig) == 0)
                ClearForcedPaintConfig();
            g_enabled.store(flags != 0 ? 1 : 0, std::memory_order_release);
        }

        bool TryReadForcedPaintConfig(PaintValues &values)
        {
            if (g_forcedConfigValid.load(std::memory_order_acquire) == 0)
                return false;

            values.ok = true;
            values.style = g_forcedConfigStyle.load(std::memory_order_relaxed);
            values.color = g_forcedConfigColor.load(std::memory_order_relaxed);
            values.drawOutline = g_forcedConfigDrawOutline.load(std::memory_order_relaxed);
            values.dot = g_forcedConfigDot.load(std::memory_order_relaxed);
            values.gapUseWeaponValue = g_forcedConfigGapUseWeaponValue.load(std::memory_order_relaxed);
            values.useAlpha = g_forcedConfigUseAlpha.load(std::memory_order_relaxed);
            values.tStyle = g_forcedConfigTStyle.load(std::memory_order_relaxed);
            values.gap = static_cast<float>(g_forcedConfigGap100.load(std::memory_order_relaxed)) / 100.0f;
            values.size = static_cast<float>(g_forcedConfigSize100.load(std::memory_order_relaxed)) / 100.0f;
            values.thickness = static_cast<float>(g_forcedConfigThickness100.load(std::memory_order_relaxed)) / 100.0f;
            values.outlineThickness = static_cast<float>(g_forcedConfigOutline100.load(std::memory_order_relaxed)) / 100.0f;
            values.alpha = g_forcedConfigAlpha.load(std::memory_order_relaxed);
            values.red = g_forcedConfigRed.load(std::memory_order_relaxed);
            values.green = g_forcedConfigGreen.load(std::memory_order_relaxed);
            values.blue = g_forcedConfigBlue.load(std::memory_order_relaxed);
            return true;
        }

        bool TryReadMappedPaintConfig(PaintValues &values)
        {
            const int arg1EntIndex = g_configArg1EntIndex.load(std::memory_order_relaxed);
            const int arg2EntIndex = g_configArg2EntIndex.load(std::memory_order_relaxed);
            if (arg1EntIndex < 0 && arg2EntIndex < 0)
            {
                g_configGuardMissed.fetch_add(1, std::memory_order_relaxed);
                return false;
            }

            for (const auto &entry : g_paintConfigMap)
            {
                if (entry.enabled.load(std::memory_order_acquire) == 0)
                    continue;

                const int pawnIndex = entry.pawnIndex.load(std::memory_order_relaxed);
                const int weaponIndex = entry.weaponIndex.load(std::memory_order_relaxed);
                const bool matched =
                    (pawnIndex >= 0 && (arg1EntIndex == pawnIndex || arg2EntIndex == pawnIndex)) ||
                    (weaponIndex >= 0 && (arg1EntIndex == weaponIndex || arg2EntIndex == weaponIndex));
                if (!matched)
                    continue;

                values.ok = true;
                values.style = entry.style.load(std::memory_order_relaxed);
                values.color = entry.color.load(std::memory_order_relaxed);
                values.drawOutline = entry.drawOutline.load(std::memory_order_relaxed);
                values.dot = entry.dot.load(std::memory_order_relaxed);
                values.gapUseWeaponValue = entry.gapUseWeaponValue.load(std::memory_order_relaxed);
                values.useAlpha = entry.useAlpha.load(std::memory_order_relaxed);
                values.tStyle = entry.tStyle.load(std::memory_order_relaxed);
                values.gap = static_cast<float>(entry.gap100.load(std::memory_order_relaxed)) / 100.0f;
                values.size = static_cast<float>(entry.size100.load(std::memory_order_relaxed)) / 100.0f;
                values.thickness = static_cast<float>(entry.thickness100.load(std::memory_order_relaxed)) / 100.0f;
                values.outlineThickness = static_cast<float>(entry.outline100.load(std::memory_order_relaxed)) / 100.0f;
                values.alpha = entry.alpha.load(std::memory_order_relaxed);
                values.red = entry.red.load(std::memory_order_relaxed);
                values.green = entry.green.load(std::memory_order_relaxed);
                values.blue = entry.blue.load(std::memory_order_relaxed);
                g_configGuardMatched.fetch_add(1, std::memory_order_relaxed);
                return true;
            }

            g_configGuardMissed.fetch_add(1, std::memory_order_relaxed);
            return false;
        }

        bool PaintConfigTargetMatches(void *player, void *weapon)
        {
            const auto controllerPtr = g_configGuardControllerPtr.load(std::memory_order_acquire);
            const auto pawnPtr = g_configGuardPawnPtr.load(std::memory_order_acquire);
            const auto guardedWeaponPtr = g_configGuardWeaponPtr.load(std::memory_order_acquire);
            const auto pawnIndex = g_configGuardPawnIndex.load(std::memory_order_acquire);
            const auto weaponIndex = g_configGuardWeaponIndex.load(std::memory_order_acquire);
            if (controllerPtr == 0 &&
                pawnPtr == 0 &&
                guardedWeaponPtr == 0 &&
                pawnIndex < 0 &&
                weaponIndex < 0)
            {
                return true;
            }

            const auto playerPtr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(player));
            const auto weaponPtr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(weapon));
            const auto arg1EntIndex = g_configArg1EntIndex.load(std::memory_order_relaxed);
            const auto arg2EntIndex = g_configArg2EntIndex.load(std::memory_order_relaxed);
            const auto matched =
                (controllerPtr != 0 && (playerPtr == controllerPtr || weaponPtr == controllerPtr)) ||
                (pawnPtr != 0 && (playerPtr == pawnPtr || weaponPtr == pawnPtr)) ||
                (guardedWeaponPtr != 0 && (playerPtr == guardedWeaponPtr || weaponPtr == guardedWeaponPtr)) ||
                (pawnIndex >= 0 && (arg1EntIndex == pawnIndex || arg2EntIndex == pawnIndex)) ||
                (weaponIndex >= 0 && (arg1EntIndex == weaponIndex || arg2EntIndex == weaponIndex));
            if (matched)
                g_configGuardMatched.fetch_add(1, std::memory_order_relaxed);
            else
                g_configGuardMissed.fetch_add(1, std::memory_order_relaxed);
            return matched;
        }

        void PatchPaintConfig(void *config, const PaintValues &values)
        {
            if (!config || !values.ok)
                return;

#if defined(_WIN32)
            __try
            {
#endif
                g_configModeBefore.store(ReadConfigInt(config, 0x18), std::memory_order_relaxed);
                g_configColorBefore.store(ReadConfigByte(config, 0x14), std::memory_order_relaxed);
                g_configGap100Before.store(RoundFloat(ReadConfigFloat(config, 0x24) * 100.0f), std::memory_order_relaxed);
                g_configSize100Before.store(RoundFloat(ReadConfigFloat(config, 0x30) * 100.0f), std::memory_order_relaxed);
                g_configThickness100Before.store(RoundFloat(ReadConfigFloat(config, 0x2C) * 100.0f), std::memory_order_relaxed);
                g_configDotBefore.store(ReadConfigByte(config, 0x1D), std::memory_order_relaxed);
                g_configLiveGap100Before.store(RoundFloat(ReadConfigFloat(config, 0x0C) * 100.0f), std::memory_order_relaxed);
                g_configSmoothGap100Before.store(RoundFloat(ReadConfigFloat(config, 0x10) * 100.0f), std::memory_order_relaxed);

                WriteConfigByte(config, 0x14, values.color);
                WriteConfigInt(config, 0x18, values.style);
                WriteConfigByte(config, 0x1C, values.drawOutline);
                WriteConfigByte(config, 0x1D, values.dot);
                WriteConfigByte(config, 0x1E, 0);
                WriteConfigByte(config, 0x1F, 0);
                WriteConfigByte(config, 0x20, values.useAlpha);
                WriteConfigByte(config, 0x21, values.tStyle);
                WriteConfigFloat(config, 0x0C, 4.0f);
                WriteConfigFloat(config, 0x10, 4.0f);
                WriteConfigFloat(config, 0x24, values.gap);
                WriteConfigFloat(config, 0x2C, values.thickness);
                WriteConfigFloat(config, 0x30, values.size);
                WriteConfigFloat(config, 0x34, values.outlineThickness);
                WriteConfigByte(config, 0x38, values.red);
                WriteConfigByte(config, 0x39, values.green);
                WriteConfigByte(config, 0x3A, values.blue);
                WriteConfigByte(config, 0x3B, values.alpha);

                g_configModeAfter.store(ReadConfigInt(config, 0x18), std::memory_order_relaxed);
                g_configColorAfter.store(ReadConfigByte(config, 0x14), std::memory_order_relaxed);
                g_configGap100After.store(RoundFloat(ReadConfigFloat(config, 0x24) * 100.0f), std::memory_order_relaxed);
                g_configSize100After.store(RoundFloat(ReadConfigFloat(config, 0x30) * 100.0f), std::memory_order_relaxed);
                g_configThickness100After.store(RoundFloat(ReadConfigFloat(config, 0x2C) * 100.0f), std::memory_order_relaxed);
                g_configDotAfter.store(ReadConfigByte(config, 0x1D), std::memory_order_relaxed);
                g_configUseAlphaAfter.store(ReadConfigByte(config, 0x20), std::memory_order_relaxed);
                g_configAlphaAfter.store(ReadConfigByte(config, 0x3B), std::memory_order_relaxed);
                g_configOutline100After.store(RoundFloat(ReadConfigFloat(config, 0x34) * 100.0f), std::memory_order_relaxed);
                g_configLiveGap100After.store(RoundFloat(ReadConfigFloat(config, 0x0C) * 100.0f), std::memory_order_relaxed);
                g_configSmoothGap100After.store(RoundFloat(ReadConfigFloat(config, 0x10) * 100.0f), std::memory_order_relaxed);
                g_configRecoilAfter.store(ReadConfigByte(config, 0x1E), std::memory_order_relaxed);
                g_configGapUseWeaponAfter.store(ReadConfigByte(config, 0x1F), std::memory_order_relaxed);
                g_configRgbaPacked.store(
                    static_cast<uint64_t>(ClampByte(values.red)) |
                        (static_cast<uint64_t>(ClampByte(values.green)) << 8) |
                        (static_cast<uint64_t>(ClampByte(values.blue)) << 16) |
                        (static_cast<uint64_t>(ClampByte(values.alpha)) << 24),
                    std::memory_order_relaxed);
                g_configPatched.fetch_add(1, std::memory_order_relaxed);
#if defined(_WIN32)
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                g_configErrors.fetch_add(1, std::memory_order_relaxed);
            }
#endif
        }

        void PatchPaintConfigFromConfiguredSource(void *player, void *weapon, void *config)
        {
            if (!config)
                return;

            g_configArg1EntIndex.store(EntityIndexFromArg(player), std::memory_order_relaxed);
            g_configArg2EntIndex.store(EntityIndexFromArg(weapon), std::memory_order_relaxed);
            if (g_enabled.load(std::memory_order_acquire) == 0 ||
                (g_flags.load(std::memory_order_relaxed) & kFlagPatchPaintConfig) == 0 ||
                (g_flags.load(std::memory_order_relaxed) & kFlagUseForcedPaintConfig) == 0)
            {
                return;
            }

            PaintValues values{};
            if (g_paintConfigMapCount.load(std::memory_order_acquire) > 0)
            {
                if (!TryReadMappedPaintConfig(values))
                    return;
            }
            else
            {
                if (!PaintConfigTargetMatches(player, weapon))
                    return;
                if (!TryReadForcedPaintConfig(values))
                    return;
            }

            PatchPaintConfig(config, values);
        }

        void BC_FASTCALL PaintConfigBuildDetour(void *player, void *weapon, void *config)
        {
            g_configCalls.fetch_add(1, std::memory_order_relaxed);
            if (!g_configOriginal)
                return;

#if defined(_WIN32)
            __try
            {
#endif
                g_configOriginal(player, weapon, config);
                PatchPaintConfigFromConfiguredSource(player, weapon, config);
#if defined(_WIN32)
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                g_configErrors.fetch_add(1, std::memory_order_relaxed);
            }
#endif
        }

        int InstallConfigHook()
        {
            if (g_configHook.Active())
            {
                g_configInstallRc.store(0, std::memory_order_relaxed);
                return 0;
            }

#if defined(_WIN32)
            const auto clientModule = Sig::ModuleFromName("client.dll");
            if (!clientModule)
                return -3;

            std::vector<uint8_t> pattern;
            std::vector<bool> wildcards;
            if (!Sig::ParseSigString(g_configSignature, pattern, wildcards))
            {
                g_configInstallRc.store(-13, std::memory_order_relaxed);
                return -13;
            }

            void *target = Sig::FindPatternIn(clientModule, pattern, wildcards);
            g_clientBase = reinterpret_cast<uintptr_t>(clientModule.Base);
            g_configTarget = reinterpret_cast<uintptr_t>(target);
            if (!PaintConfigBuildPrologueMatches(target))
            {
                g_configInstallRc.store(-13, std::memory_order_relaxed);
                return -13;
            }
#else
            return -3;
#endif

            void *orig = nullptr;
            if (!g_configHook.Create(target, reinterpret_cast<void *>(&PaintConfigBuildDetour), &orig))
            {
                g_configInstallRc.store(-14, std::memory_order_relaxed);
                return -14;
            }
            g_configOriginal = reinterpret_cast<PaintConfigBuildFn>(orig);
            if (!g_configHook.Enable())
            {
                g_configHook.Remove();
                g_configOriginal = nullptr;
                g_configInstallRc.store(-15, std::memory_order_relaxed);
                return -15;
            }

            g_configInstallRc.store(0, std::memory_order_relaxed);
            DebugOut("[BotController] hud_reticle paint-config hook installed\n");
            return 0;
        }

        void FillState(ProbeState &state, int rc, int actionsApplied)
        {
            state.size = static_cast<int32_t>(sizeof(ProbeState));
            state.rc = rc;
            state.installed = g_configHook.Active() ? 1 : 0;
            state.enabled = g_enabled.load(std::memory_order_acquire);
            state.actionsApplied = actionsApplied;
            state.clientBase = static_cast<uint64_t>(g_clientBase);
            state.configTargetPtr = static_cast<uint64_t>(g_configTarget);
            state.configOriginalPtr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(g_configOriginal));
            state.flags = g_flags.load(std::memory_order_relaxed);
            state.configInstallRc = g_configInstallRc.load(std::memory_order_relaxed);
            state.configCalls = g_configCalls.load(std::memory_order_relaxed);
            state.configPatched = g_configPatched.load(std::memory_order_relaxed);
            state.configErrors = g_configErrors.load(std::memory_order_relaxed);
            state.configModeBefore = g_configModeBefore.load(std::memory_order_relaxed);
            state.configModeAfter = g_configModeAfter.load(std::memory_order_relaxed);
            state.configColorBefore = g_configColorBefore.load(std::memory_order_relaxed);
            state.configColorAfter = g_configColorAfter.load(std::memory_order_relaxed);
            state.configGap100Before = g_configGap100Before.load(std::memory_order_relaxed);
            state.configGap100After = g_configGap100After.load(std::memory_order_relaxed);
            state.configSize100Before = g_configSize100Before.load(std::memory_order_relaxed);
            state.configSize100After = g_configSize100After.load(std::memory_order_relaxed);
            state.configThickness100Before = g_configThickness100Before.load(std::memory_order_relaxed);
            state.configThickness100After = g_configThickness100After.load(std::memory_order_relaxed);
            state.configDotBefore = g_configDotBefore.load(std::memory_order_relaxed);
            state.configDotAfter = g_configDotAfter.load(std::memory_order_relaxed);
            state.configUseAlphaAfter = g_configUseAlphaAfter.load(std::memory_order_relaxed);
            state.configAlphaAfter = g_configAlphaAfter.load(std::memory_order_relaxed);
            state.configOutline100After = g_configOutline100After.load(std::memory_order_relaxed);
            state.configRgbaPacked = g_configRgbaPacked.load(std::memory_order_relaxed);
            state.configLiveGap100Before = g_configLiveGap100Before.load(std::memory_order_relaxed);
            state.configLiveGap100After = g_configLiveGap100After.load(std::memory_order_relaxed);
            state.configSmoothGap100Before = g_configSmoothGap100Before.load(std::memory_order_relaxed);
            state.configSmoothGap100After = g_configSmoothGap100After.load(std::memory_order_relaxed);
            state.configRecoilAfter = g_configRecoilAfter.load(std::memory_order_relaxed);
            state.configGapUseWeaponAfter = g_configGapUseWeaponAfter.load(std::memory_order_relaxed);
            state.configGuardMatched = g_configGuardMatched.load(std::memory_order_relaxed);
            state.configGuardMissed = g_configGuardMissed.load(std::memory_order_relaxed);
            state.configMapCount = g_paintConfigMapCount.load(std::memory_order_relaxed);
            state.configGuardActive =
                (g_configGuardControllerPtr.load(std::memory_order_relaxed) != 0 ||
                 g_configGuardPawnPtr.load(std::memory_order_relaxed) != 0 ||
                 g_configGuardWeaponPtr.load(std::memory_order_relaxed) != 0 ||
                 g_configGuardPawnIndex.load(std::memory_order_relaxed) >= 0 ||
                 g_configGuardWeaponIndex.load(std::memory_order_relaxed) >= 0 ||
                 state.configMapCount > 0)
                    ? 1
                    : 0;
        }
    } // namespace

    void LoadFromGamedata(const nlohmann::json &gamedata)
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_configSignature = Sig::FindPlatformSig(gamedata, kPaintConfigBuildSigName);
    }

    void Remove()
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        Configure(0);
        g_configHook.Remove();
        g_configOriginal = nullptr;
        g_configTarget = 0;
        g_clientBase = 0;
    }

    int Probe(int action, int forceMode, int forceGap, int forceRadius, int flags, ProbeState *out, int size)
    {
        (void)forceMode;
        (void)forceGap;
        (void)forceRadius;
        if (!out || size < static_cast<int>(sizeof(ProbeState)))
            return -1;

        int rc = 0;
        int applied = 0;
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            if ((action & kActionInstall) != 0)
            {
                rc = InstallConfigHook();
                if (rc == 0)
                    applied |= kActionInstall;
            }
            if (rc == 0 && (action & kActionConfigure) != 0)
            {
                Configure(flags);
                applied |= kActionConfigure;
            }
            if (rc == 0 && (action & kActionRemove) != 0)
            {
                Configure(0);
                g_configHook.Remove();
                g_configOriginal = nullptr;
                g_configTarget = 0;
                g_clientBase = 0;
                applied |= kActionRemove;
            }
        }

        ProbeState state{};
        FillState(state, rc, applied);
        std::memcpy(out, &state, sizeof(state));
        return rc;
    }

    int SetPaintConfig(const PaintConfigOverride *config, int size)
    {
        if (!config || size < static_cast<int>(sizeof(PaintConfigOverride)))
        {
            g_forcedConfigValid.store(0, std::memory_order_release);
            return -1;
        }
        if (config->size < static_cast<int32_t>(sizeof(PaintConfigOverride)))
        {
            g_forcedConfigValid.store(0, std::memory_order_release);
            return -2;
        }

        g_forcedConfigStyle.store(config->style, std::memory_order_relaxed);
        g_forcedConfigColor.store(config->color, std::memory_order_relaxed);
        g_forcedConfigDrawOutline.store(config->drawOutline, std::memory_order_relaxed);
        g_forcedConfigDot.store(config->dot, std::memory_order_relaxed);
        g_forcedConfigGapUseWeaponValue.store(config->gapUseWeaponValue, std::memory_order_relaxed);
        g_forcedConfigUseAlpha.store(config->useAlpha, std::memory_order_relaxed);
        g_forcedConfigTStyle.store(config->tStyle, std::memory_order_relaxed);
        g_forcedConfigGap100.store(config->gap100, std::memory_order_relaxed);
        g_forcedConfigSize100.store(config->size100, std::memory_order_relaxed);
        g_forcedConfigThickness100.store(config->thickness100, std::memory_order_relaxed);
        g_forcedConfigOutline100.store(config->outline100, std::memory_order_relaxed);
        g_forcedConfigAlpha.store(config->alpha, std::memory_order_relaxed);
        g_forcedConfigRed.store(config->red, std::memory_order_relaxed);
        g_forcedConfigGreen.store(config->green, std::memory_order_relaxed);
        g_forcedConfigBlue.store(config->blue, std::memory_order_relaxed);
        g_forcedConfigValid.store(1, std::memory_order_release);
        return 0;
    }

    int SetPaintConfigTarget(uint64_t controllerPtr, uint64_t pawnPtr, uint64_t weaponPtr, int pawnIndex, int weaponIndex)
    {
        g_configGuardControllerPtr.store(controllerPtr, std::memory_order_release);
        g_configGuardPawnPtr.store(pawnPtr, std::memory_order_release);
        g_configGuardWeaponPtr.store(weaponPtr, std::memory_order_release);
        g_configGuardPawnIndex.store(pawnIndex, std::memory_order_release);
        g_configGuardWeaponIndex.store(weaponIndex, std::memory_order_release);
        return 0;
    }

    int SetPaintConfigMapEntry(int slot, int pawnIndex, int weaponIndex, const PaintConfigOverride *config, int size)
    {
        if (slot < 0 || slot >= kPaintConfigMapSlots)
            return -1;
        if (!config || size < static_cast<int>(sizeof(PaintConfigOverride)))
            return -2;
        if (config->size < static_cast<int32_t>(sizeof(PaintConfigOverride)))
            return -3;
        if (pawnIndex < 0 && weaponIndex < 0)
            return -4;

        auto &entry = g_paintConfigMap[slot];
        entry.enabled.store(0, std::memory_order_release);
        entry.pawnIndex.store(pawnIndex, std::memory_order_relaxed);
        entry.weaponIndex.store(weaponIndex, std::memory_order_relaxed);
        entry.style.store(config->style, std::memory_order_relaxed);
        entry.color.store(config->color, std::memory_order_relaxed);
        entry.drawOutline.store(config->drawOutline, std::memory_order_relaxed);
        entry.dot.store(config->dot, std::memory_order_relaxed);
        entry.gapUseWeaponValue.store(config->gapUseWeaponValue, std::memory_order_relaxed);
        entry.useAlpha.store(config->useAlpha, std::memory_order_relaxed);
        entry.tStyle.store(config->tStyle, std::memory_order_relaxed);
        entry.gap100.store(config->gap100, std::memory_order_relaxed);
        entry.size100.store(config->size100, std::memory_order_relaxed);
        entry.thickness100.store(config->thickness100, std::memory_order_relaxed);
        entry.outline100.store(config->outline100, std::memory_order_relaxed);
        entry.alpha.store(config->alpha, std::memory_order_relaxed);
        entry.red.store(config->red, std::memory_order_relaxed);
        entry.green.store(config->green, std::memory_order_relaxed);
        entry.blue.store(config->blue, std::memory_order_relaxed);
        entry.enabled.store(1, std::memory_order_release);
        RecountPaintConfigMap();
        return 0;
    }

    int ClearPaintConfigMapEntry(int slot)
    {
        if (slot < 0 || slot >= kPaintConfigMapSlots)
            return -1;
        auto &entry = g_paintConfigMap[slot];
        entry.enabled.store(0, std::memory_order_release);
        entry.pawnIndex.store(-1, std::memory_order_relaxed);
        entry.weaponIndex.store(-1, std::memory_order_relaxed);
        RecountPaintConfigMap();
        return 0;
    }

    int ClearPaintConfigMap()
    {
        ResetPaintConfigMap();
        return 0;
    }
} // namespace BotController::HudReticleProbe
