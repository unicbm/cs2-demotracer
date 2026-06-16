// Motion recording & replay implementation

#include "MotionRecorder.h"
#include "BotController.h"
#include "InputInjector.h"
#include "WeaponLocker.h"
#include "version_targets.h"

#include <entity2/entityinstance.h>

#include <array>
#include <atomic>
#include <cmath>
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

        static std::atomic<int> g_replaySnapMode{static_cast<int>(ReplaySnapMode::Hard)};
        static std::atomic<int> g_replayViewMode{static_cast<int>(ReplayViewMode::PostOnly)};
        static std::atomic<int> g_replayCmdViewMode{static_cast<int>(ReplayCommandViewMode::Pre)};
        static std::array<uint32_t, kMaxSlots> g_serverViewChangeIndex = [] {
            std::array<uint32_t, kMaxSlots> values{};
            values.fill(0);
            return values;
        }();
        static std::array<int, kMaxSlots> g_lastFinalViewCursor = [] {
            std::array<int, kMaxSlots> values{};
            values.fill(-1);
            return values;
        }();

        static constexpr float kSoftSnapDistance = 64.0f;
        static constexpr float kSoftSnapVerticalDistance = 48.0f;

        static bool ValidSlot(int s) { return s >= 0 && s < kMaxSlots; }

        static void MarkNetworkStateChanged(void *entity, uint32_t offset,
                                            int arrayIndex = -1)
        {
            if (!entity || offset == 0)
                return;

            NetworkStateChangedData data(offset, arrayIndex);
            reinterpret_cast<CEntityInstance *>(entity)->NetworkStateChanged(data);
        }

        static void MarkReplayViewNetworkChanged(void *pawn, int serverViewElement)
        {
            if (!pawn || serverViewElement < 0)
                return;

            MarkNetworkStateChanged(
                pawn,
                static_cast<uint32_t>(tg::kPawn_ServerViewAngleChanges),
                serverViewElement);
        }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        static ReplaySnapMode ActiveReplaySnapMode()
        {
            switch (g_replaySnapMode.load(std::memory_order_relaxed))
            {
            case static_cast<int>(ReplaySnapMode::Soft):
                return ReplaySnapMode::Soft;
            case static_cast<int>(ReplaySnapMode::Off):
                return ReplaySnapMode::Off;
            default:
                return ReplaySnapMode::Hard;
            }
        }

        static ReplayViewMode ActiveReplayViewMode()
        {
            switch (g_replayViewMode.load(std::memory_order_relaxed))
            {
            case static_cast<int>(ReplayViewMode::PostOnly):
                return ReplayViewMode::PostOnly;
            case static_cast<int>(ReplayViewMode::Cmd):
                return ReplayViewMode::Cmd;
            default:
                return ReplayViewMode::PrePost;
            }
        }

        static ReplayCommandViewMode ActiveReplayCommandViewMode()
        {
            switch (g_replayCmdViewMode.load(std::memory_order_relaxed))
            {
            case static_cast<int>(ReplayCommandViewMode::Post):
                return ReplayCommandViewMode::Post;
            case static_cast<int>(ReplayCommandViewMode::NextPre):
                return ReplayCommandViewMode::NextPre;
            default:
                return ReplayCommandViewMode::Pre;
            }
        }

        const char *ReplaySnapModeName(ReplaySnapMode mode)
        {
            switch (mode)
            {
            case ReplaySnapMode::Hard:
                return "hard";
            case ReplaySnapMode::Soft:
                return "soft";
            case ReplaySnapMode::Off:
                return "off";
            }
            return "hard";
        }

        void SetReplaySnapMode(ReplaySnapMode mode)
        {
            g_replaySnapMode.store(static_cast<int>(mode), std::memory_order_relaxed);
        }

        ReplaySnapMode GetReplaySnapMode()
        {
            return ActiveReplaySnapMode();
        }

        const char *ReplayViewModeName(ReplayViewMode mode)
        {
            switch (mode)
            {
            case ReplayViewMode::PrePost:
                return "prepost";
            case ReplayViewMode::PostOnly:
                return "post";
            case ReplayViewMode::Cmd:
                return "cmd";
            }
            return "prepost";
        }

        void SetReplayViewMode(ReplayViewMode mode)
        {
            g_replayViewMode.store(static_cast<int>(mode), std::memory_order_relaxed);
        }

        ReplayViewMode GetReplayViewMode()
        {
            return ActiveReplayViewMode();
        }

        const char *ReplayCommandViewModeName(ReplayCommandViewMode mode)
        {
            switch (mode)
            {
            case ReplayCommandViewMode::Pre:
                return "pre";
            case ReplayCommandViewMode::Post:
                return "post";
            case ReplayCommandViewMode::NextPre:
                return "nextpre";
            }
            return "pre";
        }

        void SetReplayCommandViewMode(ReplayCommandViewMode mode)
        {
            g_replayCmdViewMode.store(static_cast<int>(mode), std::memory_order_relaxed);
        }

        ReplayCommandViewMode GetReplayCommandViewMode()
        {
            return ActiveReplayCommandViewMode();
        }

        bool ReplayViewAllowsEngineSetEyeAngles()
        {
            return ActiveReplayViewMode() == ReplayViewMode::Cmd;
        }

        static bool ShouldDirectWritePreView()
        {
            return ActiveReplayViewMode() == ReplayViewMode::PrePost;
        }

        static bool ShouldDirectWritePostView()
        {
            ReplayViewMode mode = ActiveReplayViewMode();
            return mode == ReplayViewMode::PrePost || mode == ReplayViewMode::PostOnly;
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

        static bool SnapshotPositionIsFinite(const MovementSnapshot &s)
        {
            return std::isfinite(s.originX) && std::isfinite(s.originY) &&
                   std::isfinite(s.originZ);
        }

        static bool ShouldApplyMovementSnap(ReplaySnapMode mode, int cursor,
                                            void *services, const MovementSnapshot &target)
        {
            if (mode == ReplaySnapMode::Hard)
                return true;
            if (mode == ReplaySnapMode::Off)
                return false;

            if (cursor <= 0)
                return true;
            if (!SnapshotPositionIsFinite(target))
                return false;

            MovementSnapshot live{};
            if (!ReadSnapshot(services, live) || !SnapshotPositionIsFinite(live))
                return true;

            const float dx = live.originX - target.originX;
            const float dy = live.originY - target.originY;
            const float dz = live.originZ - target.originZ;
            const float dist2 = dx * dx + dy * dy;
            return dist2 > (kSoftSnapDistance * kSoftSnapDistance) ||
                   std::fabs(dz) > kSoftSnapVerticalDistance;
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

            {
                std::lock_guard<std::mutex> lk(r.mu);
                ReplayTick t{};
                t.pre = r.havePre ? r.pendingPre : post;
                t.post = post;
                t.weaponDefIndex = def;
                t.numSubtick = static_cast<uint32_t>(r.pendingSubs.size());
                for (const auto &sm : r.pendingSubs)
                    r.subs.push_back(sm);
                r.ticks.push_back(t);
                r.pendingSubs.clear();
                r.havePre = false;
            }
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
            g_lastFinalViewCursor[slot] = -1;
            g_serverViewChangeIndex[slot] = 0;
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
            g_lastFinalViewCursor[slot] = -1;
            g_serverViewChangeIndex[slot] = 0;
            p.loop.store(loop, std::memory_order_relaxed);
            p.playing.store(true, std::memory_order_release);
            return true;
        }

        bool StopReplay(int slot)
        {
            if (!ValidSlot(slot))
                return false;
            g_rep[slot].playing.store(false, std::memory_order_release);
            g_lastFinalViewCursor[slot] = -1;
            g_serverViewChangeIndex[slot] = 0;
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

        bool ReplayCommandViewSnapshot(int slot, MovementSnapshot &out)
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

            const ReplayCommandViewMode mode = ActiveReplayCommandViewMode();
            if (mode == ReplayCommandViewMode::Post)
            {
                out = p.ticks[cur].post;
                return true;
            }
            if (mode == ReplayCommandViewMode::NextPre)
            {
                out = (cur + 1 < total) ? p.ticks[cur + 1].pre : p.ticks[cur].post;
                return true;
            }

            out = p.ticks[cur].pre;
            return true;
        }

        bool ReplaySpectatorView(int slot, MovementSnapshot &out)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            std::lock_guard<std::mutex> lk(p.mu);
            int total = static_cast<int>(p.ticks.size());
            if (total <= 0)
                return false;

            const int cur = p.cursor.load(std::memory_order_relaxed);
            const int lastFinal = g_lastFinalViewCursor[slot];
            int idx = -1;

            if (lastFinal >= 0 && lastFinal < total &&
                (lastFinal == cur || lastFinal + 1 == cur || cur >= total))
            {
                idx = lastFinal;
            }
            else if (cur >= 0 && cur < total)
            {
                idx = cur;
            }
            else if (lastFinal >= 0 && lastFinal < total)
            {
                idx = lastFinal;
            }

            if (idx < 0 || idx >= total)
                return false;

            out = p.ticks[idx].post;
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

        static void WriteReplayViewHistory(void *services, char *pawn, float pitch, float yaw)
        {
            const float normalizedYaw = NormalizeDeg(yaw);
            *reinterpret_cast<float *>(pawn + tg::kPawn_ViewAnglePrevious + 0) = pitch;
            *reinterpret_cast<float *>(pawn + tg::kPawn_ViewAnglePrevious + 4) = normalizedYaw;
            *reinterpret_cast<float *>(pawn + tg::kPawn_ViewAnglePrevious + 8) = 0.0f;

            auto *sv = reinterpret_cast<char *>(services);
            *reinterpret_cast<float *>(sv + tg::kServices_OldViewAngles + 0) = pitch;
            *reinterpret_cast<float *>(sv + tg::kServices_OldViewAngles + 4) = normalizedYaw;
            *reinterpret_cast<float *>(sv + tg::kServices_OldViewAngles + 8) = 0.0f;
        }

        static bool CanWriteMemory(void *ptr, size_t len)
        {
            if (!ptr || len == 0)
                return false;

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
        }

        struct ServerViewVectorCandidate
        {
            const char *layout;
            char *elements;
            int *sizePtr;
            int size;
            int alloc;
        };

        static bool PlausibleServerViewVector(const ServerViewVectorCandidate &c)
        {
            if (!c.elements || !c.sizePtr)
                return false;
            if (c.size < 0 || c.alloc <= 0 || c.size > c.alloc)
                return false;
            if (c.alloc > 64)
                return false;

            constexpr size_t kViewChangeSize = 0x48;
            const int elementIndex = (c.size > 0) ? (c.size - 1) : 0;
            char *element = c.elements + static_cast<size_t>(elementIndex) * kViewChangeSize;
            return CanWriteMemory(c.sizePtr, sizeof(int)) &&
                   CanWriteMemory(element + 0x40, sizeof(uint32_t));
        }

        static bool ResolveServerViewVector(char *pawn, ServerViewVectorCandidate &out)
        {
            char *vec = pawn + tg::kPawn_ServerViewAngleChanges;

            // Current hl2sdk-cs2 CUtlVector layout: int size at +0,
            // padding, then CUtlMemory at +8 (pointer, alloc, grow).
            ServerViewVectorCandidate sdk{};
            sdk.layout = "sdk";
            sdk.sizePtr = reinterpret_cast<int *>(vec + 0x00);
            sdk.size = *sdk.sizePtr;
            sdk.elements = *reinterpret_cast<char **>(vec + 0x08);
            sdk.alloc = *reinterpret_cast<int *>(vec + 0x10);
            if (PlausibleServerViewVector(sdk))
            {
                out = sdk;
                return true;
            }

            // Older Source-style vectors put memory first and size later.
            ServerViewVectorCandidate legacy{};
            legacy.layout = "legacy";
            legacy.elements = *reinterpret_cast<char **>(vec + 0x00);
            legacy.alloc = *reinterpret_cast<int *>(vec + 0x08);
            legacy.sizePtr = reinterpret_cast<int *>(vec + 0x10);
            legacy.size = *legacy.sizePtr;
            if (PlausibleServerViewVector(legacy))
            {
                out = legacy;
                return true;
            }

            return false;
        }

        static bool WriteServerViewAngleChange(char *pawn, int slot,
                                               const MovementSnapshot &s,
                                               int *elementOut)
        {
            if (elementOut)
                *elementOut = -1;

            ServerViewVectorCandidate vec{};
            if (!ResolveServerViewVector(pawn, vec))
                return false;

            constexpr size_t kViewChangeSize = 0x48;
            constexpr uint32_t kFixAngleAbsolute = 1;
            const int elementIndex = (vec.size > 0) ? (vec.size - 1) : 0;
            char *element = vec.elements + static_cast<size_t>(elementIndex) * kViewChangeSize;
            const float normalizedYaw = NormalizeDeg(s.yaw);
            auto *type = reinterpret_cast<uint32_t *>(element + 0x30);
            auto *pitch = reinterpret_cast<float *>(element + 0x34);
            auto *yaw = reinterpret_cast<float *>(element + 0x38);
            auto *roll = reinterpret_cast<float *>(element + 0x3C);
            auto *index = reinterpret_cast<uint32_t *>(element + 0x40);

            uint32_t next = g_serverViewChangeIndex[slot] + 1;
            if (*index >= next)
                next = *index + 1;
            if (next == 0)
                next = 1;

            *type = kFixAngleAbsolute;
            *pitch = s.pitch;
            *yaw = normalizedYaw;
            *roll = 0.0f;
            *index = next;
            g_serverViewChangeIndex[slot] = next;
            if (vec.size == 0)
                *vec.sizePtr = 1;
            if (elementOut)
                *elementOut = elementIndex;
            return true;
        }

        static bool SyncReplayView(int slot, void *services,
                                   const MovementSnapshot &s)
        {
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = *reinterpret_cast<void **>(sv + tg::kServices_Pawn);
            if (!pawn)
                return false;
            auto *p = reinterpret_cast<char *>(pawn);

            BotControllerHooks::ApplyReplayEyeAngles(pawn, s.pitch, s.yaw);
            WriteRawViewAnglesToPawn(p, s.pitch, s.yaw);
            WriteReplayViewHistory(services, p, s.pitch, s.yaw);
            int serverViewElement = -1;
            WriteServerViewAngleChange(p, slot, s, &serverViewElement);
            MarkReplayViewNetworkChanged(pawn, serverViewElement);
            return true;
        }

        // Write velocity onto the pawn. View is controlled separately so
        // we can A/B direct view writes without changing movement replay.
        static void WriteVelocityToPawn(void *services, const MovementSnapshot &s)
        {
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = *reinterpret_cast<void **>(sv + tg::kServices_Pawn);
            if (!pawn)
                return;
            auto *p = reinterpret_cast<char *>(pawn);

            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 0) = s.velX;
            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 4) = s.velY;
            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 8) = s.velZ;
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
            const int cursor = ReplayCursor(slot);
            const ReplaySnapMode snapMode = ActiveReplaySnapMode();
            const bool snapMovement =
                ShouldApplyMovementSnap(snapMode, cursor, services, t.pre);

            if (snapMovement)
            {
                WriteMoveData(moveData, t.pre);
                WriteVelocityToPawn(services, t.pre);
            }
            if (ShouldDirectWritePreView())
                SyncReplayView(slot, services, t.pre);
            auto *sv = reinterpret_cast<char *>(services);
            // Feed recorded buttons so the engine's Duck()/ladder logic runs
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons) = t.pre.buttons;
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons1) = t.pre.buttons1;
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons2) = t.pre.buttons2;
            if (snapMovement)
            {
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
            if (ActiveReplaySnapMode() != ReplaySnapMode::Hard)
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

        // FinishMove (post): publish the final post view before replay commit
        // advances the cursor. This is intentionally separate from movement
        // commit because first-person spectator state can sample before the
        // later PhysicsSimulate boundary.
        void OnReplayFinalView(int slot, void *services)
        {
            if (!ValidSlot(slot) || !services)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;
            if (!ShouldDirectWritePostView())
                return;

            ReplayTick t{};
            int cur = -1;
            {
                std::lock_guard<std::mutex> lk(p.mu);
                int total = static_cast<int>(p.ticks.size());
                cur = p.cursor.load(std::memory_order_relaxed);
                if (cur < 0 || cur >= total)
                    return;
                t = p.ticks[cur];
            }

            SyncReplayView(slot, services, t.post);
            g_lastFinalViewCursor[slot] = cur;
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
                        g_lastFinalViewCursor[slot] = -1;
                        g_serverViewChangeIndex[slot] = 0;
                        return;
                    }
                    p.playing.store(false, std::memory_order_release);
                    return;
                }
                t = p.ticks[cur];
            }

            auto *sv = reinterpret_cast<char *>(services);
            const ReplaySnapMode snapMode = ActiveReplaySnapMode();
            const bool hardSnap = snapMode == ReplaySnapMode::Hard;
            if (hardSnap)
            {
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
            }
            if (ShouldDirectWritePostView() && g_lastFinalViewCursor[slot] != cur)
                SyncReplayView(slot, services, t.post);

            if (hardSnap)
            {
                // Overwrite duck/ladder state only in hard snapshot mode.
                *reinterpret_cast<float *>(sv + tg::kServices_DuckAmount) = t.post.duckAmount;
                *reinterpret_cast<float *>(sv + tg::kServices_DuckSpeed) = t.post.duckSpeed;
                *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 0) = t.post.ladderNormalX;
                *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 4) = t.post.ladderNormalY;
                *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 8) = t.post.ladderNormalZ;
                *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducked) = t.post.ducked;
                *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducking) = t.post.ducking;
                *reinterpret_cast<uint8_t *>(sv + tg::kServices_DesiresDuck) = t.post.desiresDuck;
            }

            p.cursor.store(cur + 1, std::memory_order_relaxed);
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
                g_lastFinalViewCursor[i] = -1;
                g_serverViewChangeIndex[i] = 0;
            }
        }
    }
}
