// Motion recording & replay

#pragma once

#include <cstdint>

namespace BotLocker
{
    // State of the player at one boundary of a movement tick. Captured twice
    // per tick: pre (before the mover runs) and post (after).
#pragma pack(push, 4)
    struct MovementSnapshot
    {
        float originX, originY, originZ; // scene node m_vecAbsOrigin
        float velX, velY, velZ;          // m_vecAbsVelocity
        float pitch, yaw, roll;          // view angles
        uint32_t entityFlags;            // m_fFlags (bit0 = FL_ONGROUND)
        uint8_t moveType;                // m_MoveType (MoveType_t)
        uint8_t _pad[3];                 // keep 4-byte alignment explicit
        uint64_t buttons;                // services button mask
    };

    // One recorded server tick. numSubtick subtick moves follow this tick in
    // the parallel SubtickMove buffer
    struct ReplayTick
    {
        MovementSnapshot pre;
        MovementSnapshot post;
        int32_t weaponDefIndex; // active weapon item-def index, -1 = none
        uint32_t numSubtick;    // subtick moves for this tick, 0..36
    };

    struct SubtickMove
    {
        float when;          // [0,1) time within the tick
        uint32_t button;     // 0 = analog, else engine button bit
        float pressed;       // digital: 1=down 0=up (stored as float)
        float analogForward; // analog_forward_delta
        float analogLeft;    // analog_left_delta
        float pitchDelta;    // pitch_delta
        float yawDelta;      // yaw_delta
    };
#pragma pack(pop)

    namespace MotionRecorder
    {
        constexpr int kMaxSlots = 64;
        constexpr int kMaxSubtickPerTick = 36;

        // ---- recording ----
        bool StartRecord(int slot); // clears old buffer, begins capture
        bool StopRecord(int slot);  // stops; buffers kept for CopyOut
        bool IsRecording(int slot);
        int RecordedTickCount(int slot);    // <0 on bad slot
        int RecordedSubtickCount(int slot); // <0 on bad slot

        // ProcessMovement hook: capture pre snapshot (call before original).
        void OnCapturePre(int slot, void *services, void *cmd);
        // ProcessMovement hook: capture post snapshot + commit the tick (call
        // after original). Pairs the pending subtick moves to this tick.
        void OnCapturePost(int slot, void *services, void *cmd);
        // PlayerRunCommand hook: stash this tick's subtick moves (pending).
        void OnCaptureSubticks(int slot, const SubtickMove *moves, int count);

        // Track which WeaponServices* maps to this recording slot (set per tick).
        void SetLiveWs(int slot, void *ws);
        void *LiveWs(int slot);
        // SelectItem tap: update the slot's current weapon def index.
        void SetCurrentDef(int slot, int defIndex);

        // Copy recorded data out to caller buffers; returns elements written.
        int CopyTicks(int slot, ReplayTick *out, int maxTicks);
        int CopySubticks(int slot, SubtickMove *out, int maxSubticks);

        // ---- replay ----
        // Load parallel arrays into a slot's replay buffer (copies in).
        bool LoadReplay(int slot, const ReplayTick *ticks, int tickCount,
                        const SubtickMove *subs, int subCount);
        bool StartReplay(int slot, bool loop); // play from tick 0
        bool StopReplay(int slot);             // stop + clear injection
        bool IsReplaying(int slot);
        int ReplayCursor(int slot); // current tick index, <0 if idle
        int ReplayTotal(int slot);  // loaded tick count

        // Current tick being applied this server tick (cursor-1).
        bool CurrentReplayTick(int slot, ReplayTick &out);
        // Tick currently queued for command/movement application (cursor).
        bool PendingReplayTick(int slot, ReplayTick &out);
        // Copy the current tick's subtick moves into out (up to maxOut).
        // Returns count, or -1 if not replaying.
        int CurrentReplaySubticks(int slot, SubtickMove *out, int maxOut);

        // Switch a bot to the weapon with this def index.
        bool SwitchBotWeaponByDef(int slot, int defIndex);

        // ---- replay write hooks ----
        // ProcessMovement (pre original): write pre snapshot into CMoveData +
        // pawn angles + entity moveType, so the mover starts from recorded state.
        void OnReplayPre(int slot, void *services, void *moveData);
        // FinishMove (pre original): write post snapshot into CMoveData + force
        // scene-node origin resync (+1000 on Z).
        void OnReplayFinishMove(int slot, void *services, void *moveData);
        // FinishMove (post original): commit post moveType/flags, advance cursor.
        void OnReplayCommit(int slot, void *services);

        void ClearAll(); // wipe all record + replay buffers (on unload)
    }
}
