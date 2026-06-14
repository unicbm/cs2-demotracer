// version_targets.h

#pragma once

namespace cs2bl::targets
{
    // ---- CCSBot ----

    // AI-ran-this-tick byte flag; set to 1 to fake a completed tick
    inline constexpr int kBot_AiTickedFlag = 21196;
    // CCSBot -> pawn (CCSPlayerPawn*)
    inline constexpr int kBot_Pawn = 0x18;

    // ---- CBaseEntity / CEntityIdentity ----

    // entity -> CEntityIdentity*
    inline constexpr int kEnt_Identity = 0x10;
    // CEntityIdentity -> m_EHandle (low 15 bits = entity index)
    inline constexpr int kEntIdentity_EHandle = 0x10;
    // m_MoveType (MoveType_t, 1 byte) — restored each replay tick for §8
    inline constexpr int kEnt_MoveType = 0x2F3;
    // m_fFlags (bit0 = FL_ONGROUND)
    inline constexpr int kEnt_Flags = 0x388;
    // m_vecAbsVelocity
    inline constexpr int kEnt_AbsVelocity = 0x38C;
    // entity -> m_pGameSceneNode -> m_vecAbsOrigin (world pos), written each
    // replay tick (direct write, not Teleport, to keep client interp smooth)
    inline constexpr int kEnt_GameSceneNode = 0x270;
    inline constexpr int kNode_AbsOrigin = 0xC8;

    // ---- CCSPlayerPawn ----

    // m_pWeaponServices
    inline constexpr int kPawn_WeaponServices = 0xA00;
    // m_hController (CHandle)
    inline constexpr int kPawn_Controller = 0xB80;
    // m_hOriginalController (CHandle)
    inline constexpr int kPawn_OriginalController = 0xB84;
    // CCSPlayerPawn -> v_angle (QAngle)
    inline constexpr int kPawn_ViewAngle = 0xAB8;
    // m_angEyeAngles (QAngle) — written each replay tick alongside v_angle
    inline constexpr int kPawn_EyeAngles = 0x1340;

    // ---- CCSPlayer_WeaponServices ----

    // m_hActiveWeapon (CHandle)
    inline constexpr int kWs_ActiveWeapon = 0x60;

    // ---- CBasePlayerWeapon ----

    // m_AttributeManager(0x958) -> m_Item(0x50) -> m_iItemDefinitionIndex(0x38),
    // all embedded; net direct add (no deref)
    inline constexpr int kWeapon_ItemDefIndex = 0x958 + 0x50 + 0x38; // 0x9E0

    // ---- CCSPlayer_MovementServices ----

    // m_pawn (CCSPlayerPawn*)
    inline constexpr int kServices_Pawn = 56;
    // m_nButtons button mask
    inline constexpr int kServices_Buttons = 88;

    // ---- CMoveData (mover working copy, a2 in WalkMove/AirMove) ----

    // m_vecVelocity — the velocity TryPlayerMove integrates into origin
    inline constexpr int kMove_Velocity = 56;
    // m_vecAbsOrigin — post-move origin written here before FinishMove commits
    // it to the entity (§8). SDK struct offset, re-verify on engine update.
    inline constexpr int kMove_AbsOrigin = 200;

    // ---- CUserCmd ----

    inline constexpr int kCmd_ForwardMove = 44;
    inline constexpr int kCmd_SideMove = 48;
    inline constexpr int kCmd_UpMove = 52;

} // namespace cs2bl::targets
