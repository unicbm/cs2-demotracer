// Motion recording & replay implementation

#include "MotionRecorder.h"
#include "BotController.h"
#include "InputInjector.h"
#include "WeaponLocker.h"
#include "version_targets.h"

#include <Windows.h>

#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <mutex>
#include <vector>

namespace tg = BotController::targets;

namespace BotController
{
    namespace MotionRecorder
    {
        struct RecordState
        {
            std::atomic<bool> recording{false};
            std::vector<ReplayTick> ticks;
            std::vector<SubtickMove> subs;
            // Subtick moves seen on PlayerRunCommand, awaiting the matching
            // ProcessMovement post that commits them to a tick.
            std::vector<SubtickMove> pendingSubs;
            MovementSnapshot pendingPre{};
            bool havePre{false};
            std::atomic<void *> liveWs{nullptr};
            std::atomic<int> currentDef{-1};
            std::mutex mu; // guards ticks/subs/pending/pre
        };

        struct ReplayState
        {
            std::atomic<bool> playing{false};
            std::atomic<bool> loop{false};
            std::vector<ReplayTick> ticks;
            std::vector<SubtickMove> subs;
            std::vector<uint32_t> subOffset; // prefix sum, size ticks.size()+1
            std::atomic<int> cursor{0};
            std::atomic<int> lastAppliedDef{-1};
            std::mutex mu; // guards ticks/subs/subOffset
        };

        static std::array<RecordState, kMaxSlots> g_rec;
        static std::array<ReplayState, kMaxSlots> g_rep;

        /* ! Replay-rate probe */
        static LARGE_INTEGER g_qpcFreq{};
        static int64_t g_lastCommitQpc[kMaxSlots] = {0};
        /* ? Velocity-vs-displacement probe */
        static float g_lastPostX[kMaxSlots] = {0};
        static float g_lastPostY[kMaxSlots] = {0};
        static bool g_haveLastPost[kMaxSlots] = {false};
        /* ? Record-side probe */
        static int64_t g_recLastQpc[kMaxSlots] = {0};
        static float g_recLastNodeX[kMaxSlots] = {0};
        static float g_recLastNodeY[kMaxSlots] = {0};
        static bool g_recHaveLast[kMaxSlots] = {false};
        static std::atomic<int> g_viewDebugTarget{-1};
        static int g_viewDebugLastSeaCursor[kMaxSlots] = {0};
        static int g_viewDebugSeaCalls[kMaxSlots] = {0};

        static bool ValidSlot(int s) { return s >= 0 && s < kMaxSlots; }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        static float AngleDelta(float to, float from)
        {
            return NormalizeDeg(to - from);
        }

        static bool ShouldViewDebug(int slot)
        {
            int target = g_viewDebugTarget.load(std::memory_order_relaxed);
            return target == -2 || target == slot;
        }

        static void DebugReplayView(const char *phase, int slot, int cursor,
                                    const ReplayTick &t)
        {
            if (!ShouldViewDebug(slot))
                return;

            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[BC][view][%s] slot=%d cursor=%d pre=(%.2f,%.2f) "
                          "post=(%.2f,%.2f) d=(%.2f,%.2f) nSub=%u\n",
                          phase, slot, cursor,
                          t.pre.pitch, NormalizeDeg(t.pre.yaw),
                          t.post.pitch, NormalizeDeg(t.post.yaw),
                          t.post.pitch - t.pre.pitch,
                          AngleDelta(t.post.yaw, t.pre.yaw),
                          t.numSubtick);
            OutputDebugStringA(dbg);
        }

        // Read a MovementSnapshot from live engine state (services -> pawn).
        static bool ReadSnapshot(void *services, MovementSnapshot &out)
        {
            if (!services)
                return false;
            auto *s = reinterpret_cast<char *>(services);
            void *pawn = *reinterpret_cast<void **>(s + tg::kServices_Pawn);
            if (!pawn)
                return false;
            auto *p = reinterpret_cast<char *>(pawn);

            out.velX = *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 0);
            out.velY = *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 4);
            out.velZ = *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 8);
            out.entityFlags = *reinterpret_cast<uint32_t *>(p + tg::kEnt_Flags);
            out.moveType = *reinterpret_cast<uint8_t *>(p + tg::kEnt_MoveType);
            out.actualMoveType = *reinterpret_cast<uint8_t *>(p + tg::kEnt_ActualMoveType);
            out.buttons = *reinterpret_cast<uint64_t *>(s + tg::kServices_Buttons);
            out.buttons1 = *reinterpret_cast<uint64_t *>(s + tg::kServices_Buttons1);
            out.buttons2 = *reinterpret_cast<uint64_t *>(s + tg::kServices_Buttons2);

            // duck/ladder state (drives crouch + ladder anim on replay)
            out.duckAmount = *reinterpret_cast<float *>(s + tg::kServices_DuckAmount);
            out.duckSpeed = *reinterpret_cast<float *>(s + tg::kServices_DuckSpeed);
            out.ladderNormalX = *reinterpret_cast<float *>(s + tg::kServices_LadderNormal + 0);
            out.ladderNormalY = *reinterpret_cast<float *>(s + tg::kServices_LadderNormal + 4);
            out.ladderNormalZ = *reinterpret_cast<float *>(s + tg::kServices_LadderNormal + 8);
            out.ducked = *reinterpret_cast<uint8_t *>(s + tg::kServices_Ducked);
            out.ducking = *reinterpret_cast<uint8_t *>(s + tg::kServices_Ducking);
            out.desiresDuck = *reinterpret_cast<uint8_t *>(s + tg::kServices_DesiresDuck);

            // view angles from pawn v_angle
            out.pitch = *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 0);
            out.yaw = *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 4);
            out.roll = *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 8);

            void *node = *reinterpret_cast<void **>(p + tg::kEnt_GameSceneNode);
            if (node)
            {
                auto *n = reinterpret_cast<char *>(node);
                out.originX = *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 0);
                out.originY = *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 4);
                out.originZ = *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 8);
            }
            return true;
        }

        // ---- recording ----

        bool StartRecord(int slot)
        {
            if (!ValidSlot(slot))
                return false;
            RecordState &r = g_rec[slot];
            {
                std::lock_guard<std::mutex> lk(r.mu);
                r.ticks.clear();
                r.subs.clear();
                r.pendingSubs.clear();
                r.havePre = false;
                r.ticks.reserve(4096); // ~64s @ 64 tick
                r.subs.reserve(4096);
            }
            r.currentDef.store(-1, std::memory_order_relaxed);
            r.liveWs.store(nullptr, std::memory_order_relaxed);
            r.recording.store(true, std::memory_order_release);
            return true;
        }

        bool StopRecord(int slot)
        {
            if (!ValidSlot(slot))
                return false;
            g_rec[slot].recording.store(false, std::memory_order_release);
            return true;
        }

        bool IsRecording(int slot)
        {
            return ValidSlot(slot) &&
                   g_rec[slot].recording.load(std::memory_order_acquire);
        }

        int RecordedTickCount(int slot)
        {
            if (!ValidSlot(slot))
                return -1;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            return static_cast<int>(r.ticks.size());
        }

        int RecordedSubtickCount(int slot)
        {
            if (!ValidSlot(slot))
                return -1;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            return static_cast<int>(r.subs.size());
        }

        void SetLiveWs(int slot, void *ws)
        {
            if (ValidSlot(slot))
                g_rec[slot].liveWs.store(ws, std::memory_order_relaxed);
        }

        void *LiveWs(int slot)
        {
            return ValidSlot(slot)
                       ? g_rec[slot].liveWs.load(std::memory_order_relaxed)
                       : nullptr;
        }

        void SetCurrentDef(int slot, int defIndex)
        {
            if (ValidSlot(slot))
                g_rec[slot].currentDef.store(defIndex, std::memory_order_relaxed);
        }

        void OnCapturePre(int slot, void *services, void *cmd)
        {
            (void)cmd;
            if (!ValidSlot(slot) || !services)
                return;
            RecordState &r = g_rec[slot];
            if (!r.recording.load(std::memory_order_acquire))
                return;
            MovementSnapshot pre{};
            if (!ReadSnapshot(services, pre))
                return;
            std::lock_guard<std::mutex> lk(r.mu);
            r.pendingPre = pre;
            r.havePre = true;
        }

        void OnCaptureSubticks(int slot, const SubtickMove *moves, int count)
        {
            if (!ValidSlot(slot) || count < 0)
                return;
            RecordState &r = g_rec[slot];
            if (!r.recording.load(std::memory_order_acquire))
                return;
            if (count > kMaxSubtickPerTick)
                count = kMaxSubtickPerTick;
            std::lock_guard<std::mutex> lk(r.mu);
            r.pendingSubs.clear();
            for (int i = 0; i < count; ++i)
                r.pendingSubs.push_back(moves[i]);

            /* ? Record-side subtick diagnostic */
            for (int i = 0; i < count; ++i)
            {
                if (moves[i].button & 1u)
                {
                    char dbg[160];
                    std::snprintf(dbg, sizeof(dbg),
                                  "[BL][recSt] slot=%d i=%d btn=%u pressed=%.2f when=%.3f\n",
                                  slot, i, moves[i].button, moves[i].pressed, moves[i].when);
                    OutputDebugStringA(dbg);
                }
            }
        }

        void OnCapturePost(int slot, void *services, void *cmd)
        {
            // cmd is actually the CMoveData* (hook passes moveData here)
            if (!ValidSlot(slot) || !services)
                return;
            RecordState &r = g_rec[slot];
            if (!r.recording.load(std::memory_order_acquire))
                return;

            MovementSnapshot post{};
            if (!ReadSnapshot(services, post))
                return;

            if (cmd)
            {
                auto *m = reinterpret_cast<char *>(cmd);
                post.originX = *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 0);
                post.originY = *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 4);
                post.originZ = *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 8);
            }

            // Active weapon def for this tick.
            void *ws = r.liveWs.load(std::memory_order_relaxed);
            int def = WeaponLockerHooks::ActiveWeaponDef(ws);
            if (def < 0)
                def = r.currentDef.load(std::memory_order_relaxed);

            int tickIdx;
            uint32_t nSub;
            {
                std::lock_guard<std::mutex> lk(r.mu);
                ReplayTick t{};
                t.pre = r.havePre ? r.pendingPre : post;
                t.post = post;
                t.weaponDefIndex = def;
                nSub = static_cast<uint32_t>(r.pendingSubs.size());
                t.numSubtick = nSub;
                for (const auto &sm : r.pendingSubs)
                    r.subs.push_back(sm);
                r.ticks.push_back(t);
                r.pendingSubs.clear();
                r.havePre = false;
                tickIdx = static_cast<int>(r.ticks.size()) - 1;
            }

            /* ? Record-side diagnostics */
            if (g_qpcFreq.QuadPart == 0)
                QueryPerformanceFrequency(&g_qpcFreq);
            LARGE_INTEGER now;
            QueryPerformanceCounter(&now);
            long long dtUs = g_recHaveLast[slot]
                                 ? (now.QuadPart - g_recLastQpc[slot]) * 1000000LL / g_qpcFreq.QuadPart
                                 : -1;
            float velR = std::sqrt(post.velX * post.velX + post.velY * post.velY);
            float nodeD = -1.0f;
            if (g_recHaveLast[slot])
            {
                float dx = post.originX - g_recLastNodeX[slot];
                float dy = post.originY - g_recLastNodeY[slot];
                nodeD = std::sqrt(dx * dx + dy * dy) * 64.0f;
            }
            // MoveData origin this tick (cmd == moveData)
            float mvX = 0, mvY = 0;
            if (cmd)
            {
                auto *m = reinterpret_cast<char *>(cmd);
                mvX = *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 0);
                mvY = *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 4);
            }
            g_recLastQpc[slot] = now.QuadPart;
            g_recLastNodeX[slot] = post.originX;
            g_recLastNodeY[slot] = post.originY;
            g_recHaveLast[slot] = true;

            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[BL][rec] t=%d dt_us=%lld mt=%u nSub=%u def=%d velR=%.1f nodeD=%.1f "
                          "node=(%.1f,%.1f) mv=(%.1f,%.1f)\n",
                          tickIdx, dtUs, (unsigned)post.moveType, nSub, def, velR, nodeD,
                          post.originX, post.originY, mvX, mvY);
            OutputDebugStringA(dbg);
        }

        int CopyTicks(int slot, ReplayTick *out, int maxTicks)
        {
            if (!ValidSlot(slot) || !out || maxTicks <= 0)
                return 0;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            int n = static_cast<int>(r.ticks.size());
            if (n > maxTicks)
                n = maxTicks;
            for (int i = 0; i < n; ++i)
                out[i] = r.ticks[i];
            return n;
        }

        int CopySubticks(int slot, SubtickMove *out, int maxSubticks)
        {
            if (!ValidSlot(slot) || !out || maxSubticks <= 0)
                return 0;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            int n = static_cast<int>(r.subs.size());
            if (n > maxSubticks)
                n = maxSubticks;
            for (int i = 0; i < n; ++i)
                out[i] = r.subs[i];
            return n;
        }

        // ---- replay ----

        // Rebuild the prefix-sum offset table from each tick's numSubtick.
        // subOffset[i] = first subtick index for tick i; size = nTicks+1.
        static void RebuildSubOffset(ReplayState &p)
        {
            p.subOffset.assign(p.ticks.size() + 1, 0);
            uint32_t acc = 0;
            for (size_t i = 0; i < p.ticks.size(); ++i)
            {
                p.subOffset[i] = acc;
                acc += p.ticks[i].numSubtick;
            }
            p.subOffset[p.ticks.size()] = acc;
        }

        bool LoadReplay(int slot, const ReplayTick *ticks, int tickCount,
                        const SubtickMove *subs, int subCount)
        {
            if (!ValidSlot(slot) || !ticks || tickCount < 0 ||
                (subCount > 0 && !subs))
                return false;
            ReplayState &p = g_rep[slot];
            if (p.playing.load(std::memory_order_acquire))
                return false; // don't swap frames mid-playback
            std::lock_guard<std::mutex> lk(p.mu);
            p.ticks.assign(ticks, ticks + tickCount);
            p.subs.assign(subs, subs + (subCount > 0 ? subCount : 0));
            RebuildSubOffset(p);
            p.cursor.store(0, std::memory_order_relaxed);
            p.lastAppliedDef.store(-1, std::memory_order_relaxed);
            return true;
        }

        bool StartReplay(int slot, bool loop)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            {
                std::lock_guard<std::mutex> lk(p.mu);
                if (p.ticks.empty())
                    return false;
            }
            p.cursor.store(0, std::memory_order_relaxed);
            p.lastAppliedDef.store(-1, std::memory_order_relaxed);
            p.loop.store(loop, std::memory_order_relaxed);
            p.playing.store(true, std::memory_order_release);
            return true;
        }

        bool StopReplay(int slot)
        {
            if (!ValidSlot(slot))
                return false;
            g_rep[slot].playing.store(false, std::memory_order_release);
            return true;
        }

        bool IsReplaying(int slot)
        {
            return ValidSlot(slot) &&
                   g_rep[slot].playing.load(std::memory_order_acquire);
        }

        int ReplayCursor(int slot)
        {
            if (!ValidSlot(slot))
                return -1;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return -1;
            return p.cursor.load(std::memory_order_relaxed);
        }

        int ReplayTotal(int slot)
        {
            if (!ValidSlot(slot))
                return 0;
            ReplayState &p = g_rep[slot];
            std::lock_guard<std::mutex> lk(p.mu);
            return static_cast<int>(p.ticks.size());
        }

        bool ReplayTickForSimulation(int slot, ReplayTick &out)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            std::lock_guard<std::mutex> lk(p.mu);
            int total = static_cast<int>(p.ticks.size());
            int cur = p.cursor.load(std::memory_order_relaxed);
            if (cur < 0 || cur >= total)
                return false;
            out = p.ticks[cur];
            return true;
        }

        // Public status API: cursor points at the next tick, so the last
        // applied tick is cursor - 1. Clamp at 0 during the opening tick.
        bool CurrentReplayTick(int slot, ReplayTick &out)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            std::lock_guard<std::mutex> lk(p.mu);
            int total = static_cast<int>(p.ticks.size());
            int idx = p.cursor.load(std::memory_order_relaxed) - 1;
            if (idx < 0)
                idx = 0;
            if (idx >= total)
                return false;
            out = p.ticks[idx];
            return true;
        }

        int CurrentReplaySubticks(int slot, SubtickMove *out, int maxOut)
        {
            if (!ValidSlot(slot) || !out || maxOut <= 0)
                return -1;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return -1;
            std::lock_guard<std::mutex> lk(p.mu);
            int total = static_cast<int>(p.ticks.size());
            int idx = p.cursor.load(std::memory_order_relaxed);
            if (idx < 0 || idx >= total)
                return -1;
            uint32_t begin = p.subOffset[idx];
            uint32_t end = p.subOffset[idx + 1];
            int n = static_cast<int>(end - begin);
            if (n > maxOut)
                n = maxOut;
            for (int i = 0; i < n; ++i)
                out[i] = p.subs[begin + i];
            return n;
        }

        bool CurrentReplayInputButtons(int slot, uint64_t &b0, uint64_t &b1,
                                       uint64_t &b2)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            std::lock_guard<std::mutex> lk(p.mu);
            int total = static_cast<int>(p.ticks.size());
            int cur = p.cursor.load(std::memory_order_relaxed);
            if (cur < 0 || cur >= total)
                return false;
            uint64_t heldNow = p.ticks[cur].pre.buttons;
            // Previous tick's held mask; 0 before the first tick so the opening press registers as a fresh edge.
            uint64_t heldPrev = (cur > 0) ? p.ticks[cur - 1].pre.buttons : 0;
            b0 = heldNow;
            b1 = heldNow & ~heldPrev; // pressed this tick
            b2 = heldPrev & ~heldNow; // released this tick
            return true;
        }

        bool SwitchBotWeaponByDef(int slot, int defIndex)
        {
            if (!ValidSlot(slot) || defIndex < 0)
                return false;
            if (!WeaponLockerHooks::WeaponHooksReady())
                return false;
            void *ws = WeaponLockerHooks::WsForSlot(slot);
            if (!ws)
                return false;
            void *weapon = WeaponLockerHooks::FindWeaponByDef(ws, defIndex);
            if (!weapon)
                return false;
            return WeaponLockerHooks::SelectWeaponRaw(ws, weapon);
        }

        // Def index of the bot's current active weapon
        int BotActiveWeaponDef(int slot)
        {
            if (!ValidSlot(slot) || !WeaponLockerHooks::WeaponHooksReady())
                return -1;
            void *ws = WeaponLockerHooks::WsForSlot(slot);
            if (!ws)
                return -1;
            return WeaponLockerHooks::ActiveWeaponDef(ws);
        }

        // Entity index for cmd.weaponselect this replay tick
        int CurrentReplayWeaponSelect(int slot)
        {
            if (!ValidSlot(slot) || !WeaponLockerHooks::WeaponHooksReady())
                return -1;

            // Recorded def for the tick about to be simulated
            int recordedDef;
            {
                ReplayState &p = g_rep[slot];
                if (!p.playing.load(std::memory_order_acquire))
                    return -1;
                std::lock_guard<std::mutex> lk(p.mu);
                int total = static_cast<int>(p.ticks.size());
                int cur = p.cursor.load(std::memory_order_relaxed);
                if (cur < 0 || cur >= total)
                    return -1;
                recordedDef = p.ticks[cur].weaponDefIndex;
            }
            if (recordedDef < 0)
                return -1;

            void *ws = WeaponLockerHooks::WsForSlot(slot);
            if (!ws)
                return -1;

            // Already holding the recorded weapon -> no switch
            if (WeaponLockerHooks::ActiveWeaponDef(ws) == recordedDef)
                return -1;

            void *weapon = WeaponLockerHooks::FindWeaponByDef(ws, recordedDef);
            if (!weapon)
                return -1;
            return WeaponLockerHooks::WeaponEntIndex(weapon);
        }

        static void WriteRawViewAnglesToPawn(char *p, float pitch, float yaw)
        {
            const float normalizedYaw = NormalizeDeg(yaw);
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 0) = pitch;
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 4) = normalizedYaw;
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 8) = 0.0f;
            *reinterpret_cast<float *>(p + tg::kPawn_EyeAngles + 0) = pitch;
            *reinterpret_cast<float *>(p + tg::kPawn_EyeAngles + 4) = normalizedYaw;
            *reinterpret_cast<float *>(p + tg::kPawn_EyeAngles + 8) = 0.0f;
        }

        static void WriteViewAnglesToPawn(void *services, const MovementSnapshot &s)
        {
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = *reinterpret_cast<void **>(sv + tg::kServices_Pawn);
            if (!pawn)
                return;
            auto *p = reinterpret_cast<char *>(pawn);

            WriteRawViewAnglesToPawn(p, s.pitch, s.yaw);
            BotControllerHooks::ApplyReplayEyeAngles(pawn, s.pitch, s.yaw);
            WriteRawViewAnglesToPawn(p, s.pitch, s.yaw);
        }

        // Write velocity + view angles onto the pawn
        static void WriteAnglesVelToPawn(void *services, const MovementSnapshot &s)
        {
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = *reinterpret_cast<void **>(sv + tg::kServices_Pawn);
            if (!pawn)
                return;
            auto *p = reinterpret_cast<char *>(pawn);

            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 0) = s.velX;
            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 4) = s.velY;
            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 8) = s.velZ;
            WriteViewAnglesToPawn(services, s);
        }

        // Write origin + velocity into CMoveData.
        static void WriteMoveData(void *moveData, const MovementSnapshot &s)
        {
            auto *m = reinterpret_cast<char *>(moveData);
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 0) = s.originX;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 4) = s.originY;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 8) = s.originZ;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 0) = s.velX;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 4) = s.velY;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 8) = s.velZ;
        }

        // ProcessMovement (pre): seed CMoveData + pawn + moveType with pre state.
        void OnReplayPre(int slot, void *services, void *moveData)
        {
            if (!ValidSlot(slot) || !services || !moveData)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;
            ReplayTick t{};
            {
                std::lock_guard<std::mutex> lk(p.mu);
                int total = static_cast<int>(p.ticks.size());
                int cur = p.cursor.load(std::memory_order_relaxed);
                if (cur >= total)
                    return; // commit handler will stop/loop
                t = p.ticks[cur];
            }
            DebugReplayView("pre", slot, ReplayCursor(slot), t);
            WriteMoveData(moveData, t.pre);
            WriteAnglesVelToPawn(services, t.pre);
            auto *sv = reinterpret_cast<char *>(services);
            // Feed recorded buttons so the engine's Duck()/ladder logic runs
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons) = t.pre.buttons;
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons1) = t.pre.buttons1;
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons2) = t.pre.buttons2;
            void *pawn = *reinterpret_cast<void **>(sv + tg::kServices_Pawn);
            if (pawn)
            {
                auto *pp = reinterpret_cast<char *>(pawn);
                *reinterpret_cast<uint8_t *>(pp + tg::kEnt_MoveType) = t.pre.moveType;
                void *node = *reinterpret_cast<void **>(pp + tg::kEnt_GameSceneNode);
                if (node)
                {
                    auto *n = reinterpret_cast<char *>(node);
                    *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 0) = t.pre.originX;
                    *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 4) = t.pre.originY;
                    *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 8) = t.pre.originZ;
                }
            }
        }

        // FinishMove (pre): write post snapshot into CMoveData and force a
        // scene-node origin resync (+1000 on Z).
        void OnReplayFinishMove(int slot, void *services, void *moveData)
        {
            if (!ValidSlot(slot) || !services || !moveData)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;
            ReplayTick t{};
            {
                std::lock_guard<std::mutex> lk(p.mu);
                int total = static_cast<int>(p.ticks.size());
                int cur = p.cursor.load(std::memory_order_relaxed);
                if (cur >= total)
                    return;
                t = p.ticks[cur];
            }
            auto *m = reinterpret_cast<char *>(moveData);
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 0) = t.post.originX;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 4) = t.post.originY;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 8) = t.post.originZ;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 0) = t.post.velX;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 4) = t.post.velY;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 8) = t.post.velZ;

            // Force engine to resync the entity origin from MoveData
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = *reinterpret_cast<void **>(sv + tg::kServices_Pawn);
            if (pawn)
            {
                void *node = *reinterpret_cast<void **>(
                    reinterpret_cast<char *>(pawn) + tg::kEnt_GameSceneNode);
                if (node)
                {
                    auto *n = reinterpret_cast<char *>(node);
                    *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 0) = t.post.originX;
                    *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 4) = t.post.originY;
                    *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 8) = t.post.originZ + 1000.0f;
                }
            }
        }

        // FinishMove (post): commit post moveType/flags + advance cursor.
        void OnReplayCommit(int slot, void *services)
        {
            if (!ValidSlot(slot) || !services)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;

            ReplayTick t{};
            int cur, total;
            {
                std::lock_guard<std::mutex> lk(p.mu);
                total = static_cast<int>(p.ticks.size());
                cur = p.cursor.load(std::memory_order_relaxed);
                if (cur >= total)
                {
                    if (p.loop.load(std::memory_order_relaxed) && total > 0)
                    {
                        p.cursor.store(0, std::memory_order_relaxed);
                        p.lastAppliedDef.store(-1, std::memory_order_relaxed);
                        return;
                    }
                    p.playing.store(false, std::memory_order_release);
                    return;
                }
                t = p.ticks[cur];
            }

            DebugReplayView("commit", slot, cur, t);
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = *reinterpret_cast<void **>(sv + tg::kServices_Pawn);
            if (pawn)
            {
                auto *pp = reinterpret_cast<char *>(pawn);
                *reinterpret_cast<uint8_t *>(pp + tg::kEnt_MoveType) = t.post.moveType;
                *reinterpret_cast<uint8_t *>(pp + tg::kEnt_ActualMoveType) = t.post.actualMoveType;
                // Merge ground + ducking bits from the recording, keep the rest live.
                uint32_t live = *reinterpret_cast<uint32_t *>(pp + tg::kEnt_Flags);
                uint32_t mask = tg::kFL_OnGround | tg::kFL_Ducking;
                live = (live & ~mask) | (t.post.entityFlags & mask);
                *reinterpret_cast<uint32_t *>(pp + tg::kEnt_Flags) = live;
            }
            WriteViewAnglesToPawn(services, t.post);

            // Overwrite duck/ladder state
            *reinterpret_cast<float *>(sv + tg::kServices_DuckAmount) = t.post.duckAmount;
            *reinterpret_cast<float *>(sv + tg::kServices_DuckSpeed) = t.post.duckSpeed;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 0) = t.post.ladderNormalX;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 4) = t.post.ladderNormalY;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 8) = t.post.ladderNormalZ;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducked) = t.post.ducked;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducking) = t.post.ducking;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_DesiresDuck) = t.post.desiresDuck;

            p.cursor.store(cur + 1, std::memory_order_relaxed);

            /* ! Rate probe */
            if (g_qpcFreq.QuadPart == 0)
                QueryPerformanceFrequency(&g_qpcFreq);
            LARGE_INTEGER now;
            QueryPerformanceCounter(&now);
            int64_t prev = g_lastCommitQpc[slot];
            g_lastCommitQpc[slot] = now.QuadPart;
            long long dtUs = prev ? (now.QuadPart - prev) * 1000000LL / g_qpcFreq.QuadPart : -1;

            /* ? Speed probe */
            float velR = std::sqrt(t.post.velX * t.post.velX + t.post.velY * t.post.velY);
            float velD = -1.0f;
            if (g_haveLastPost[slot])
            {
                float dx = t.post.originX - g_lastPostX[slot];
                float dy = t.post.originY - g_lastPostY[slot];
                velD = std::sqrt(dx * dx + dy * dy) * 64.0f;
            }
            g_lastPostX[slot] = t.post.originX;
            g_lastPostY[slot] = t.post.originY;
            g_haveLastPost[slot] = true;

            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[BL][replay] t=%d/%d dt_us=%lld mt=%u grnd=%d velR=%.1f velD=%.1f "
                          "post=(%.1f,%.1f,%.1f)\n",
                          cur, total, dtUs, (unsigned)t.post.moveType, (int)(t.post.entityFlags & 1),
                          velR, velD, t.post.originX, t.post.originY, t.post.originZ);
            OutputDebugStringA(dbg);
        }

        void SetViewDebugTarget(int target)
        {
            if (target < -2 || target >= kMaxSlots)
                target = -1;
            g_viewDebugTarget.store(target, std::memory_order_relaxed);
            for (int i = 0; i < kMaxSlots; ++i)
            {
                g_viewDebugLastSeaCursor[i] = -999;
                g_viewDebugSeaCalls[i] = 0;
            }
        }

        int ViewDebugTarget()
        {
            return g_viewDebugTarget.load(std::memory_order_relaxed);
        }

        bool ViewDebugEnabled(int slot)
        {
            return ValidSlot(slot) && ShouldViewDebug(slot);
        }

        void DebugSetEyeAnglesSuppressed(int slot, const float *angle)
        {
            if (!ValidSlot(slot) || !ShouldViewDebug(slot))
                return;

            int cursor = ReplayCursor(slot);
            if (g_viewDebugLastSeaCursor[slot] != cursor)
            {
                g_viewDebugLastSeaCursor[slot] = cursor;
                g_viewDebugSeaCalls[slot] = 0;
            }
            ++g_viewDebugSeaCalls[slot];

            const float inPitch = angle ? angle[0] : 0.0f;
            const float inYaw = angle ? NormalizeDeg(angle[1]) : 0.0f;
            char dbg[192];
            std::snprintf(dbg, sizeof(dbg),
                          "[BC][view][sea_suppress] slot=%d cursor=%d calls=%d "
                          "in=(%.2f,%.2f)\n",
                          slot, cursor, g_viewDebugSeaCalls[slot], inPitch, inYaw);
            OutputDebugStringA(dbg);
        }

        void DebugReplayCommandView(int slot, int subtickCount, const SubtickMove *subticks)
        {
            if (!ValidSlot(slot) || !ShouldViewDebug(slot))
                return;

            if (subtickCount < 0)
                subtickCount = 0;
            float sumPitch = 0.0f;
            float sumYaw = 0.0f;
            for (int i = 0; i < subtickCount; ++i)
            {
                sumPitch += subticks[i].pitchDelta;
                sumYaw += subticks[i].yawDelta;
            }

            char dbg[192];
            std::snprintf(dbg, sizeof(dbg),
                          "[BC][view][cmd] slot=%d cursor=%d nSub=%d "
                          "sumPitch=%.3f sumYaw=%.3f\n",
                          slot, ReplayCursor(slot), subtickCount, sumPitch, sumYaw);
            OutputDebugStringA(dbg);
        }

        void ClearAll()
        {
            for (int i = 0; i < kMaxSlots; ++i)
            {
                g_rec[i].recording.store(false, std::memory_order_release);
                g_rep[i].playing.store(false, std::memory_order_release);
                {
                    std::lock_guard<std::mutex> lk(g_rec[i].mu);
                    g_rec[i].ticks.clear();
                    g_rec[i].subs.clear();
                    g_rec[i].pendingSubs.clear();
                    g_rec[i].havePre = false;
                }
                {
                    std::lock_guard<std::mutex> lk(g_rep[i].mu);
                    g_rep[i].ticks.clear();
                    g_rep[i].subs.clear();
                    g_rep[i].subOffset.clear();
                }
                g_rec[i].currentDef.store(-1, std::memory_order_relaxed);
                g_rec[i].liveWs.store(nullptr, std::memory_order_relaxed);
                g_rep[i].cursor.store(0, std::memory_order_relaxed);
                g_rep[i].lastAppliedDef.store(-1, std::memory_order_relaxed);
            }
        }
    }
}
