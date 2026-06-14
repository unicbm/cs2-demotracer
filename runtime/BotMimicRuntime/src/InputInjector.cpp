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

#include <Windows.h>
#include <MinHook.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <vector>

namespace tg = cs2bl::targets;

using ProcessMovement_t = void(__fastcall *)(void *services, void *moveData);
using FinishMove_t = void(__fastcall *)(void *services, void *cmd, void *moveData);
using PlayerRunCommand_t = void(__fastcall *)(void *services, void *cmd);
using PhysicsSimulate_t = void(__fastcall *)(void *controller);

namespace BotLocker
{
    namespace InputInjector
    {
        struct SlotState
        {
            std::atomic<bool> active{false};
            InjectedInput input{};
        };

        static std::array<SlotState, kMaxSlots> g_slots;

        static ProcessMovement_t g_origProcessMovement = nullptr;
        static FinishMove_t g_origFinishMove = nullptr;
        static PlayerRunCommand_t g_origPlayerRunCommand = nullptr;
        static PhysicsSimulate_t g_origPhysicsSimulate = nullptr;

        static void *g_addrProcessMovement = nullptr;
        static void *g_addrFinishMove = nullptr;
        static void *g_addrPlayerRunCommand = nullptr;
        static void *g_addrPhysicsSimulate = nullptr;
        static bool g_installed = false;
        // True once PhysicsSimulate is hooked
        static bool g_physicsActive = false;
        // True once PlayerRunCommand is hooked
        static bool g_subtickActive = false;
        static std::string g_status = "not_attempted";

        // slot -> live CCSPlayer_MovementServices*
        static std::array<std::atomic<void *>, kMaxSlots> g_slotServices{};
        static std::array<std::atomic<uint64_t>, kMaxSlots> g_replayLastButtons{};

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

        static void ApplyReplayButtonsToCommand(int slot, PlayerCommand *cmd,
                                                CBaseUserCmdPB *base,
                                                uint64_t buttons)
        {
            if (!cmd || !base || slot < 0 || slot >= kMaxSlots)
                return;

            uint64_t previous = g_replayLastButtons[slot].exchange(
                buttons, std::memory_order_relaxed);
            uint64_t changed = previous ^ buttons;

            cmd->buttonstates.m_pButtonStates[0] = buttons;
            cmd->buttonstates.m_pButtonStates[1] = changed;
            cmd->buttonstates.m_pButtonStates[2] = 0;

            CInButtonStatePB *buttonPb = base->mutable_buttons_pb();
            buttonPb->set_buttonstate1(buttons);
            buttonPb->set_buttonstate2(changed);
            buttonPb->set_buttonstate3(0);
        }

        // ---- ProcessMovement: record pre/post + manual inject + replay pre ----

        // Defined after HookedFinishMove
        static void EnsureVtableHooks(void *services);

        static void __fastcall HookedProcessMovement(void *services, void *moveData)
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
            bool replaying = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsReplaying(slot);

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

            // Manual inject (bl_inject): override the
            // MoveData move fields for this tick.
            if (slot >= 0 && slot < kMaxSlots &&
                g_slots[slot].active.load(std::memory_order_acquire))
            {
                const InjectedInput &p = g_slots[slot].input;
                auto *s = reinterpret_cast<char *>(services);
                *reinterpret_cast<uint64_t *>(s + tg::kServices_Buttons) = p.buttons;
                if (moveData)
                {
                    auto *c = reinterpret_cast<char *>(moveData);
                    *reinterpret_cast<float *>(c + tg::kCmd_ForwardMove) = p.forwardMove;
                    *reinterpret_cast<float *>(c + tg::kCmd_SideMove) = p.sideMove;
                    *reinterpret_cast<float *>(c + tg::kCmd_UpMove) = p.upMove;
                }
            }

            g_origProcessMovement(services, moveData);

            // Recording: commit the tick here only when PhysicsSimulate isn't the boundary
            // With PhysicsSimulate hooked, the post snapshot + commit happen once per server tick there, not per subtick
            if (recording && !g_physicsActive)
                MotionRecorder::OnCapturePost(slot, services, moveData);
        }

        // ---- FinishMove: replay post-write + commit ----

        static void __fastcall HookedFinishMove(void *services, void *cmd,
                                                void *moveData)
        {
            int slot = ServicesToSlot(services);
            bool replaying = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsReplaying(slot);

            // Before original: write post snapshot into MoveData + force resync.
            if (replaying)
                MotionRecorder::OnReplayFinishMove(slot, services, moveData);

            g_origFinishMove(services, cmd, moveData);

            // After original: commit moveType/flags + advance the replay cursor.
            // With PhysicsSimulate hooked, cursor++ happens once per server tick there instead
            if (replaying && !g_physicsActive)
                MotionRecorder::OnReplayCommit(slot, services);
        }

        // ---- PlayerRunCommand: subtick record + replay input re-inject ----

        static void __fastcall HookedPlayerRunCommand(void *services, void *cmd)
        {
            int slot = ServicesToSlot(services);
            bool recording = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsRecording(slot);
            bool replaying = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsReplaying(slot);
            if (slot >= 0 && slot < kMaxSlots && !replaying)
                g_replayLastButtons[slot].store(0, std::memory_order_relaxed);

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
                    ReplayTick tick{};
                    if (MotionRecorder::PendingReplayTick(slot, tick))
                        ApplyReplayButtonsToCommand(slot, pc, base,
                                                    tick.pre.buttons);

                    // Replace the command's subtick_moves with the recorded set
                    // for this tick
                    SubtickMove out[MotionRecorder::kMaxSubtickPerTick];
                    int n = MotionRecorder::CurrentReplaySubticks(
                        slot, out, MotionRecorder::kMaxSubtickPerTick);
                    base->clear_subtick_moves();
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
                    }
                }
            }

            g_origPlayerRunCommand(services, cmd);
        }

        // ---- PhysicsSimulate: the per-tick boundary ----
        // Records pre/post + commits exactly one frame

        static void __fastcall HookedPhysicsSimulate(void *controller)
        {
            int slot = ControllerToSlot(controller);
            void *services = (slot >= 0 && slot < kMaxSlots)
                                 ? g_slotServices[slot].load(std::memory_order_acquire)
                                 : nullptr;

            bool recording = slot >= 0 && slot < kMaxSlots && services &&
                             MotionRecorder::IsRecording(slot);
            bool replaying = slot >= 0 && slot < kMaxSlots && services &&
                             MotionRecorder::IsReplaying(slot);

            // pre: snapshot start-of-tick state once (before any subtick mover).
            if (recording)
                MotionRecorder::OnCapturePre(slot, services, nullptr);

            g_origPhysicsSimulate(controller);

            // post: snapshot end-of-tick state + commit one frame; advance the
            // replay cursor once. cmd=nullptr => OnCapturePost reads origin from
            // the scene node, which now holds this tick's committed end position.
            if (recording)
                MotionRecorder::OnCapturePost(slot, services, nullptr);
            if (replaying)
                MotionRecorder::OnReplayCommit(slot, services);
        }

        // CCSPlayer_MovementServices vtable indices (Windows)
        static constexpr int kVtIdx_PlayerRunCommand = 22;
        static constexpr int kVtIdx_FinishMove = 35;

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

            g_addrFinishMove = vt[kVtIdx_FinishMove];
            if (g_addrFinishMove &&
                MH_CreateHook(g_addrFinishMove,
                              reinterpret_cast<void *>(&HookedFinishMove),
                              reinterpret_cast<void **>(&g_origFinishMove)) == MH_OK)
                MH_EnableHook(g_addrFinishMove);

            // PlayerRunCommand (subtick record/re-inject)
            g_addrPlayerRunCommand = vt[kVtIdx_PlayerRunCommand];
            if (g_addrPlayerRunCommand &&
                MH_CreateHook(g_addrPlayerRunCommand,
                              reinterpret_cast<void *>(&HookedPlayerRunCommand),
                              reinterpret_cast<void **>(&g_origPlayerRunCommand)) == MH_OK &&
                MH_EnableHook(g_addrPlayerRunCommand) == MH_OK)
            {
                g_subtickActive = true;
            }
            else if (g_addrPlayerRunCommand)
            {
                MH_RemoveHook(g_addrPlayerRunCommand);
                g_addrPlayerRunCommand = nullptr;
                g_origPlayerRunCommand = nullptr;
            }

            char dbg[200];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotLocker] vtable hooks: FinishMove @ %p, "
                          "PlayerRunCommand @ %p (subtick=%d)\n",
                          g_addrFinishMove, g_addrPlayerRunCommand,
                          g_subtickActive ? 1 : 0);
            OutputDebugStringA(dbg);
        }

        static void *ResolveSig(const std::string &gd, HMODULE serverModule,
                                const char *name,
                                char *errorOut, size_t errorOutLen)
        {
            std::string sig = Sig::FindWindowsSig(gd, name);
            if (sig.empty())
            {
                std::snprintf(errorOut, errorOutLen,
                              "gamedata missing '%s.signatures.windows'", name);
                return nullptr;
            }
            std::vector<uint8_t> bytes;
            std::vector<bool> wild;
            if (!Sig::ParseSigString(sig, bytes, wild))
            {
                std::snprintf(errorOut, errorOutLen,
                              "failed to parse '%s' sig: '%s'", name, sig.c_str());
                return nullptr;
            }
            void *addr = Sig::FindPatternIn(serverModule, bytes, wild);
            if (!addr)
            {
                std::snprintf(errorOut, errorOutLen,
                              "sig '%s' not found in server.dll", name);
                return nullptr;
            }
            return addr;
        }

        bool Install(const std::string &gamedataPath, void *serverIface,
                     char *errorOut, size_t errorOutLen)
        {
            HMODULE serverModule = Sig::ModuleFromInterfacePtr(serverIface);
            if (!serverModule)
            {
                std::snprintf(errorOut, errorOutLen,
                              "ModuleFromInterfacePtr returned null");
                g_status = "failed: no server module";
                return false;
            }
            std::string gd = Sig::ReadFile(gamedataPath);
            if (gd.empty())
            {
                std::snprintf(errorOut, errorOutLen,
                              "failed to read gamedata: %s", gamedataPath.c_str());
                g_status = "failed: gamedata missing";
                return false;
            }

            g_addrProcessMovement = ResolveSig(
                gd, serverModule, "CCSPlayer_MovementServices::ProcessMovement",
                errorOut, errorOutLen);
            if (!g_addrProcessMovement)
            {
                g_status = "failed: ProcessMovement sig";
                return false;
            }
            if (MH_CreateHook(g_addrProcessMovement,
                              reinterpret_cast<void *>(&HookedProcessMovement),
                              reinterpret_cast<void **>(&g_origProcessMovement)) != MH_OK)
            {
                std::snprintf(errorOut, errorOutLen, "MH_CreateHook ProcessMovement failed");
                g_status = "failed: MH_CreateHook";
                return false;
            }
            if (MH_EnableHook(g_addrProcessMovement) != MH_OK)
            {
                std::snprintf(errorOut, errorOutLen, "MH_EnableHook ProcessMovement failed");
                MH_RemoveHook(g_addrProcessMovement);
                g_origProcessMovement = nullptr;
                g_status = "failed: MH_EnableHook";
                return false;
            }

            // PhysicsSimulate: the per-tick boundary
            char psErr[256] = {0};
            g_addrPhysicsSimulate = ResolveSig(
                gd, serverModule, "CCSPlayer_MovementServices::PhysicsSimulate",
                psErr, sizeof(psErr));
            if (g_addrPhysicsSimulate &&
                MH_CreateHook(g_addrPhysicsSimulate,
                              reinterpret_cast<void *>(&HookedPhysicsSimulate),
                              reinterpret_cast<void **>(&g_origPhysicsSimulate)) == MH_OK &&
                MH_EnableHook(g_addrPhysicsSimulate) == MH_OK)
            {
                g_physicsActive = true;
            }
            else
            {
                if (g_addrPhysicsSimulate)
                {
                    MH_RemoveHook(g_addrPhysicsSimulate);
                    g_addrPhysicsSimulate = nullptr;
                }
                g_origPhysicsSimulate = nullptr;
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotLocker] WARN: PhysicsSimulate hook unavailable (%s); "
                              "replay falls back to per-subtick boundary (may stutter)\n",
                              psErr[0] ? psErr : "MinHook failed");
                OutputDebugStringA(dbg);
            }

            // FinishMove is hooked lazily from the live vtable on the first ProcessMovement tick.
            g_installed = true;
            g_status = "ok";
            char dbg[160];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotLocker] ProcessMovement @ %p\n", g_addrProcessMovement);
            OutputDebugStringA(dbg);
            return true;
        }

        void Remove()
        {
            if (!g_installed)
                return;
            MH_DisableHook(g_addrProcessMovement);
            MH_RemoveHook(g_addrProcessMovement);
            if (g_addrFinishMove)
            {
                MH_DisableHook(g_addrFinishMove);
                MH_RemoveHook(g_addrFinishMove);
            }
            if (g_addrPlayerRunCommand)
            {
                MH_DisableHook(g_addrPlayerRunCommand);
                MH_RemoveHook(g_addrPlayerRunCommand);
            }
            if (g_addrPhysicsSimulate)
            {
                MH_DisableHook(g_addrPhysicsSimulate);
                MH_RemoveHook(g_addrPhysicsSimulate);
            }
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
            g_installed = false;
            g_status = "not_attempted";
            ClearAll();
        }

        const char *Status() { return g_status.c_str(); }

        void *ProcessUsercmdAddress() { return g_addrProcessMovement; }

        bool SetInput(int slot, const InjectedInput &input)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;
            g_slots[slot].input = input;
            g_slots[slot].active.store(true, std::memory_order_release);
            return true;
        }

        bool ClearInput(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;
            g_slots[slot].active.store(false, std::memory_order_release);
            return true;
        }

        void ClearAll()
        {
            for (auto &s : g_slots)
                s.active.store(false, std::memory_order_release);
        }

        bool IsActive(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;
            return g_slots[slot].active.load(std::memory_order_acquire);
        }

        int CountActive()
        {
            int n = 0;
            for (auto &s : g_slots)
                if (s.active.load(std::memory_order_acquire))
                    ++n;
            return n;
        }

        uint64_t HookCallCount() { return g_hookCalls.load(std::memory_order_relaxed); }
        int LastResolvedSlot() { return g_lastSlot.load(std::memory_order_relaxed); }
    }
}
