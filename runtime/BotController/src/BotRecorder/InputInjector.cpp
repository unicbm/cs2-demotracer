// CS2 movement hooks
// ProcessMovement (record + apply pre)
// FinishMove (replay post into MoveData + commit)
// PlayerRunCommand(subtick record + re-inject)

#include "playercommand.h"

#include "InputInjector.h"
#include "ccsbot_slot.h"
#include "sig_scan.h"
#include "MotionRecorder.h"
#include "version_targets.h"
#include "hook.h"
#include "platform.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <cmath>
#include <cstdio>
#include <vector>

namespace tg = BotController::targets;

using ProcessMovement_t = void(BC_FASTCALL *)(void *services, void *moveData);
using FinishMove_t = void(BC_FASTCALL *)(void *services, void *cmd, void *moveData);
using PlayerRunCommand_t = void(BC_FASTCALL *)(void *services, void *cmd);
using PhysicsSimulate_t = void(BC_FASTCALL *)(void *controller);

namespace BotController
{
    namespace InputInjector
    {
        static ProcessMovement_t g_origProcessMovement = nullptr;
        static FinishMove_t g_origFinishMove = nullptr;
        static PlayerRunCommand_t g_origPlayerRunCommand = nullptr;
        static PhysicsSimulate_t g_origPhysicsSimulate = nullptr;

        static void *g_addrProcessMovement = nullptr;
        static void *g_addrFinishMove = nullptr;
        static void *g_addrPlayerRunCommand = nullptr;
        static void *g_addrPhysicsSimulate = nullptr;

        static Hook g_hookProcessMovement;
        static Hook g_hookFinishMove;
        static Hook g_hookPlayerRunCommand;
        static Hook g_hookPhysicsSimulate;
        static bool g_installed = false;
        // True once PhysicsSimulate is hooked
        static bool g_physicsActive = false;
        // True once PlayerRunCommand is hooked
        static bool g_subtickActive = false;
        static std::string g_status = "not_attempted";

        // slot -> live CCSPlayer_MovementServices*
        static std::array<std::atomic<void *>, kMaxSlots> g_slotServices{};
        static std::array<std::atomic<bool>, kMaxSlots> g_slotControllingBot{};
        static std::atomic<int> g_controllerControllingBotOffset{-1};

        static std::atomic<uint64_t> g_hookCalls{0};
        static std::atomic<int> g_lastSlot{-1};

        // services -> player slot via pawn ptr at services+56, then m_hController.
        static int ServicesToSlot(void *services)
        {
            if (!services)
                return -1;
            void *pawn = *reinterpret_cast<void **>(
                reinterpret_cast<char *>(services) + tg::kServices_Pawn);
            if (!pawn)
                return -1;
            return ControllerSlotForPawn(pawn);
        }

        // services -> pawn -> WeaponServices*, for the recording weapon tap.
        static void *ServicesToWeaponServices(void *services)
        {
            if (!services)
                return nullptr;
            void *pawn = *reinterpret_cast<void **>(
                reinterpret_cast<char *>(services) + tg::kServices_Pawn);
            if (!pawn)
                return nullptr;
            return *reinterpret_cast<void **>(
                reinterpret_cast<char *>(pawn) + tg::kPawn_WeaponServices);
        }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        bool SetControllerControllingBotOffset(int offset)
        {
            if (offset < 0 || offset > 0x10000)
                offset = -1;
            g_controllerControllingBotOffset.store(offset, std::memory_order_release);
            return true;
        }

        static bool ControllerIsControllingBot(void *controller)
        {
            int offset = g_controllerControllingBotOffset.load(std::memory_order_acquire);
            if (!controller || offset < 0)
                return false;
            return *reinterpret_cast<uint8_t *>(reinterpret_cast<char *>(controller) + offset) != 0;
        }

        static bool ReplayActiveAndSafe(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots || !MotionRecorder::IsReplaying(slot))
                return false;
            if (!g_slotControllingBot[slot].load(std::memory_order_acquire))
                return true;

            MotionRecorder::StopReplay(slot);
            char dbg[128];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] stopped replay slot=%d: controller is controlling a bot\n",
                          slot);
            DebugOut(dbg);
            return false;
        }

        // ---- ProcessMovement: record pre/post + replay pre ----

        // Defined after HookedFinishMove
        static void EnsureVtableHooks(void *services);

        static void BC_FASTCALL HookedProcessMovement(void *services, void *moveData)
        {
            g_hookCalls.fetch_add(1, std::memory_order_relaxed);
            int slot = ServicesToSlot(services);
            g_lastSlot.store(slot, std::memory_order_relaxed);

            // Lazily hook FinishMove from the live services vtable on first tick.
            EnsureVtableHooks(services);

            // Cache slot -> services so PhysicsSimulate
            if (slot >= 0 && slot < kMaxSlots)
                g_slotServices[slot].store(services, std::memory_order_release);

            bool recording = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsRecording(slot);
            bool replaying = ReplayActiveAndSafe(slot);

            // Recording weapon tap
            if (recording)
            {
                MotionRecorder::SetLiveWs(slot, ServicesToWeaponServices(services));
                if (!g_physicsActive)
                    MotionRecorder::OnCapturePre(slot, services, moveData);
            }

            // Replay: seed CMoveData + pawn with this tick's pre snapshot
            if (replaying)
                MotionRecorder::OnReplayPre(slot, services, moveData);

            g_origProcessMovement(services, moveData);

            // Recording: commit the tick here only when PhysicsSimulate isn't the boundary
            if (recording && !g_physicsActive)
                MotionRecorder::OnCapturePost(slot, services, moveData);
        }

        // ---- FinishMove: replay post-write + commit ----

        static void BC_FASTCALL HookedFinishMove(void *services, void *cmd,
                                                void *moveData)
        {
            int slot = ServicesToSlot(services);
            bool replaying = ReplayActiveAndSafe(slot);

            // Before original: write post snapshot into MoveData + force resync.
            if (replaying)
                MotionRecorder::OnReplayFinishMove(slot, services, moveData);

            g_origFinishMove(services, cmd, moveData);

            // After original: commit moveType/flags + advance the replay cursor
            if (replaying && !g_physicsActive)
                MotionRecorder::OnReplayCommit(slot, services);
        }

        // ---- PlayerRunCommand: subtick record + re-inject ----

        static void BC_FASTCALL HookedPlayerRunCommand(void *services, void *cmd)
        {
            int slot = ServicesToSlot(services);
            bool recording = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsRecording(slot);
            bool replaying = ReplayActiveAndSafe(slot);

            if (cmd && (recording || replaying))
            {
                // Compiler computes the multiple-inheritance adjust here.
                auto *pc = reinterpret_cast<PlayerCommand *>(cmd);
                CBaseUserCmdPB *base = pc->mutable_base();

                if (recording)
                {
                    // Read this tick's subtick_moves into SubtickMove[] and
                    // stash; OnCapturePost (PhysicsSimulate-post) commits them.
                    int n = base->subtick_moves_size();
                    if (n > MotionRecorder::kMaxSubtickPerTick)
                        n = MotionRecorder::kMaxSubtickPerTick;
                    SubtickMove moves[MotionRecorder::kMaxSubtickPerTick];
                    for (int i = 0; i < n; ++i)
                    {
                        const CSubtickMoveStep &s = base->subtick_moves(i);
                        moves[i].when = s.when();
                        moves[i].button = static_cast<uint32_t>(s.button());
                        moves[i].pressed = s.pressed() ? 1.0f : 0.0f;
                        moves[i].analogForward = s.analog_forward_delta();
                        moves[i].analogLeft = s.analog_left_delta();
                        moves[i].pitchDelta = s.pitch_delta();
                        moves[i].yawDelta = s.yaw_delta();
                    }
                    MotionRecorder::OnCaptureSubticks(slot, moves, n);
                }

                if (replaying)
                {
                    uint64_t b0 = 0, b1 = 0, b2 = 0;
                    if (MotionRecorder::CurrentReplayInputButtons(slot, b0, b1, b2))
                    {
                        CInButtonStatePB *bp = base->mutable_buttons_pb();
                        bp->set_buttonstate1(b0);
                        bp->set_buttonstate2(b1);
                        bp->set_buttonstate3(b2);
                        pc->buttonstates.m_pButtonStates[0] = b0;
                        pc->buttonstates.m_pButtonStates[1] = b1;
                        pc->buttonstates.m_pButtonStates[2] = b2;
                    }

                    ReplayTick simTick{};
                    if (MotionRecorder::ReplayTickForSimulation(slot, simTick))
                    {
                        CMsgQAngle *view = base->mutable_viewangles();
                        view->set_x(simTick.pre.pitch);
                        view->set_y(NormalizeDeg(simTick.pre.yaw));
                        view->set_z(0.0f);
                    }

                    int wsel = MotionRecorder::CurrentReplayWeaponSelect(slot);
                    if (wsel >= 0)
                        base->set_weaponselect(wsel);

                    // Replace the command's subtick_moves with the recorded set for this tick
                    SubtickMove out[MotionRecorder::kMaxSubtickPerTick];
                    int n = MotionRecorder::CurrentReplaySubticks(
                        slot, out, MotionRecorder::kMaxSubtickPerTick);
                    MotionRecorder::DebugReplayCommandView(slot, n, out);
                    base->clear_subtick_moves();
                    /* ? Throw-window diagnostic */
                    int dbgStBtn = -1;
                    float dbgStPressed = -1.0f, dbgStWhen = -1.0f;
                    for (int i = 0; i < n; ++i)
                    {
                        CSubtickMoveStep *m = base->add_subtick_moves();
                        m->set_when(out[i].when);
                        m->set_button(out[i].button);
                        if (out[i].button != 0) // digital press/release
                            m->set_pressed(out[i].pressed != 0.0f);
                        if (out[i].pitchDelta != 0.0f)
                            m->set_pitch_delta(out[i].pitchDelta);
                        if (out[i].yawDelta != 0.0f)
                            m->set_yaw_delta(out[i].yawDelta);
                        if (out[i].analogForward != 0.0f)
                            m->set_analog_forward_delta(out[i].analogForward);
                        if (out[i].analogLeft != 0.0f)
                            m->set_analog_left_delta(out[i].analogLeft);
                        if (out[i].button & 1ull) // IN_ATTACK subtick
                        {
                            dbgStBtn = static_cast<int>(out[i].button);
                            dbgStPressed = out[i].pressed;
                            dbgStWhen = out[i].when;
                        }
                    }

                    /* ? Throw-window diagnostic */
                    const uint64_t kInAttack = 1ull; // IN_ATTACK bit0
                    if (((b0 | b1 | b2) & kInAttack) || wsel >= 0 || dbgStBtn >= 0)
                    {
                        char dbg[256];
                        std::snprintf(dbg, sizeof(dbg),
                                      "[BL][rep] c=%d held=%llX prs=%llX rel=%llX "
                                      "wsel=%d actDef=%d nSt=%d stBtn=%d stPr=%.2f stWhen=%.2f\n",
                                      MotionRecorder::ReplayCursor(slot),
                                      (unsigned long long)b0,
                                      (unsigned long long)b1,
                                      (unsigned long long)b2,
                                      wsel, MotionRecorder::BotActiveWeaponDef(slot),
                                      n, dbgStBtn, dbgStPressed, dbgStWhen);
                        DebugOut(dbg);
                    }
                }
            }

            g_origPlayerRunCommand(services, cmd);
        }

        // ---- PhysicsSimulate: the per-tick boundary ----
        // Records pre/post + commits

        static void BC_FASTCALL HookedPhysicsSimulate(void *controller)
        {
            int slot = ControllerToSlot(controller);
            void *services = (slot >= 0 && slot < kMaxSlots)
                                 ? g_slotServices[slot].load(std::memory_order_acquire)
                                 : nullptr;

            bool recording = slot >= 0 && slot < kMaxSlots && services &&
                             MotionRecorder::IsRecording(slot);
            if (slot >= 0 && slot < kMaxSlots)
                g_slotControllingBot[slot].store(ControllerIsControllingBot(controller), std::memory_order_release);

            bool replaying = services && ReplayActiveAndSafe(slot);

            // pre: snapshot start-of-tick state once (before any subtick mover).
            if (recording)
                MotionRecorder::OnCapturePre(slot, services, nullptr);

            g_origPhysicsSimulate(controller);

            // post: snapshot end-of-tick state + commit one frame
            if (recording)
                MotionRecorder::OnCapturePost(slot, services, nullptr);
            if (replaying)
                MotionRecorder::OnReplayCommit(slot, services);
        }

        static std::atomic<bool> g_vtHooksTried{false};

        static void EnsureVtableHooks(void *services)
        {
            if (g_vtHooksTried.exchange(true, std::memory_order_acq_rel))
                return;
            if (!services)
                return;
            void **vt = *reinterpret_cast<void ***>(services);
            if (!vt)
                return;

            g_addrFinishMove = vt[tg::kVtIdx_FinishMove];
            if (g_addrFinishMove &&
                g_hookFinishMove.Create(g_addrFinishMove,
                                        reinterpret_cast<void *>(&HookedFinishMove),
                                        reinterpret_cast<void **>(&g_origFinishMove)))
                g_hookFinishMove.Enable();

            // PlayerRunCommand (subtick record/re-inject)
            g_addrPlayerRunCommand = vt[tg::kVtIdx_PlayerRunCommand];
            if (g_addrPlayerRunCommand &&
                g_hookPlayerRunCommand.Create(g_addrPlayerRunCommand,
                                              reinterpret_cast<void *>(&HookedPlayerRunCommand),
                                              reinterpret_cast<void **>(&g_origPlayerRunCommand)) &&
                g_hookPlayerRunCommand.Enable())
            {
                g_subtickActive = true;
            }
            else if (g_addrPlayerRunCommand)
            {
                g_hookPlayerRunCommand.Remove();
                g_addrPlayerRunCommand = nullptr;
                g_origPlayerRunCommand = nullptr;
            }

            char dbg[200];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] vtable hooks: FinishMove @ %p, "
                          "PlayerRunCommand @ %p (subtick=%d)\n",
                          g_addrFinishMove, g_addrPlayerRunCommand,
                          g_subtickActive ? 1 : 0);
            DebugOut(dbg);
        }

        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen)
        {
            g_addrProcessMovement = Sig::ResolveSig(
                gd, serverModule, "CCSPlayer_MovementServices::ProcessMovement",
                errorOut, errorOutLen);
            if (!g_addrProcessMovement)
            {
                g_status = "failed: ProcessMovement sig";
                return false;
            }
            if (!g_hookProcessMovement.Create(g_addrProcessMovement,
                                              reinterpret_cast<void *>(&HookedProcessMovement),
                                              reinterpret_cast<void **>(&g_origProcessMovement)) ||
                !g_hookProcessMovement.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook ProcessMovement failed");
                g_hookProcessMovement.Remove();
                g_origProcessMovement = nullptr;
                g_status = "failed: hook ProcessMovement";
                return false;
            }

            // PhysicsSimulate: the per-tick boundary
            char psErr[256] = {0};
            g_addrPhysicsSimulate = Sig::ResolveSig(
                gd, serverModule, "CCSPlayer_MovementServices::PhysicsSimulate",
                psErr, sizeof(psErr));
            if (g_addrPhysicsSimulate &&
                g_hookPhysicsSimulate.Create(g_addrPhysicsSimulate,
                                             reinterpret_cast<void *>(&HookedPhysicsSimulate),
                                             reinterpret_cast<void **>(&g_origPhysicsSimulate)) &&
                g_hookPhysicsSimulate.Enable())
            {
                g_physicsActive = true;
            }
            else
            {
                if (g_addrPhysicsSimulate)
                {
                    g_hookPhysicsSimulate.Remove();
                    g_addrPhysicsSimulate = nullptr;
                }
                g_origPhysicsSimulate = nullptr;
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: PhysicsSimulate hook unavailable (%s); "
                              "replay falls back to per-subtick boundary (may stutter)\n",
                              psErr[0] ? psErr : "funchook failed");
                DebugOut(dbg);
            }

            // FinishMove is hooked lazily from the live vtable on the first ProcessMovement tick.
            g_installed = true;
            g_status = "ok";
            char dbg[160];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] ProcessMovement @ %p\n", g_addrProcessMovement);
            DebugOut(dbg);
            return true;
        }

        void Remove()
        {
            if (!g_installed)
                return;
            g_hookProcessMovement.Remove();
            g_hookFinishMove.Remove();
            g_hookPlayerRunCommand.Remove();
            g_hookPhysicsSimulate.Remove();
            g_origProcessMovement = nullptr;
            g_origFinishMove = nullptr;
            g_origPlayerRunCommand = nullptr;
            g_origPhysicsSimulate = nullptr;
            g_addrProcessMovement = nullptr;
            g_addrFinishMove = nullptr;
            g_addrPlayerRunCommand = nullptr;
            g_addrPhysicsSimulate = nullptr;
            g_physicsActive = false;
            g_subtickActive = false;
            g_vtHooksTried.store(false, std::memory_order_release);
            for (auto &s : g_slotServices)
                s.store(nullptr, std::memory_order_release);
            for (auto &taken : g_slotControllingBot)
                taken.store(false, std::memory_order_release);
            g_installed = false;
            g_status = "not_attempted";
        }

        const char *Status() { return g_status.c_str(); }

        void *ProcessUsercmdAddress() { return g_addrProcessMovement; }

        uint64_t HookCallCount() { return g_hookCalls.load(std::memory_order_relaxed); }
        int LastResolvedSlot() { return g_lastSlot.load(std::memory_order_relaxed); }
    }
}
