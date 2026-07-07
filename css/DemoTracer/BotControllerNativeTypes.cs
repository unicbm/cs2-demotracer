using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DemoTracer;

internal static partial class BotControllerNative
{
    public const int ExpectedAbiVersion = 16;
    public const uint RecFormatVersion = 7;
    public const uint MinRecFormatVersion = 3;
    public const int MovementSnapshotByteSize = 92;
    public const int ReplayTickByteSize = 192;
    public const int SubtickMoveByteSize = 28;
    public const int ReplayCommandFrameByteSize = 68;
    public const int ReplayMovementExtraByteSize = 48;
    public const int HudReticleProbeStateByteSize = 172;
    public const int HudReticlePaintConfigByteSize = 64;
    internal const uint CommandFieldLeftHand = 1U << 7;
    public const int ReplaySlotStateByteSize = 24;
    public const int MaxSlots = 64;
    public const int DemoTracerApiVersion = 4;

    internal const int LockKindAll = 0;
    internal const int LockKindAim = 1;
    internal const int LockKindWeapon = 2;
    internal const int LockKindJump = 3;

    internal const int HudReticleActionInstall = 1 << 0;
    internal const int HudReticleActionRemove = 1 << 1;
    internal const int HudReticleActionConfigure = 1 << 2;
    internal const int HudReticleFlagPatchPaintConfig = 1 << 0;
    internal const int HudReticleFlagUseForcedPaintConfig = 1 << 1;

    private const ulong CapabilityReplaySlotState = 1UL << 0;
    private const ulong CapabilityStartReplayAt = 1UL << 1;
    private const ulong CapabilityStartReplayUntil = 1UL << 2;
    private const ulong CapabilityReplayTick = 1UL << 3;
    private const ulong CapabilityWeaponSwitchRead = 1UL << 4;
    private const ulong CapabilityPovMask = 1UL << 5;
    private const ulong CapabilityBuyPlan = 1UL << 6;
    private const ulong CapabilityControllerBotOffset = 1UL << 7;
    internal const ulong CapabilityExtendedReplay = 1UL << 8;
    internal const ulong CapabilityUsercmdMovementIntent = 1UL << 9;
    internal const ulong CapabilityVoiceSend = 1UL << 10;

    public const ulong RequiredCapabilityMask =
        CapabilityReplaySlotState |
        CapabilityStartReplayAt |
        CapabilityStartReplayUntil |
        CapabilityReplayTick |
        CapabilityWeaponSwitchRead |
        CapabilityPovMask |
        CapabilityBuyPlan |
        CapabilityControllerBotOffset |
        CapabilityExtendedReplay;

    public static string RuntimePlatformName
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "windows-x64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "linux-x64"
                : RuntimeInformation.OSDescription;

    internal static void EnsureNativeLayout()
    {
        var snapshotSize = Marshal.SizeOf<NativeMovementSnapshot>();
        if (snapshotSize != MovementSnapshotByteSize)
            throw new InvalidOperationException($"MovementSnapshot layout is {snapshotSize}, expected {MovementSnapshotByteSize}");

        var tickSize = Marshal.SizeOf<NativeReplayTick>();
        if (tickSize != ReplayTickByteSize)
            throw new InvalidOperationException($"ReplayTick layout is {tickSize}, expected {ReplayTickByteSize}");

        var subtickSize = Marshal.SizeOf<NativeSubtickMove>();
        if (subtickSize != SubtickMoveByteSize)
            throw new InvalidOperationException($"SubtickMove layout is {subtickSize}, expected {SubtickMoveByteSize}");

        var commandFrameSize = Marshal.SizeOf<NativeReplayCommandFrame>();
        if (commandFrameSize != ReplayCommandFrameByteSize)
            throw new InvalidOperationException($"ReplayCommandFrame layout is {commandFrameSize}, expected {ReplayCommandFrameByteSize}");

        var movementExtraSize = Marshal.SizeOf<NativeReplayMovementExtra>();
        if (movementExtraSize != ReplayMovementExtraByteSize)
            throw new InvalidOperationException($"ReplayMovementExtra layout is {movementExtraSize}, expected {ReplayMovementExtraByteSize}");

        var hudReticleProbeSize = Marshal.SizeOf<NativeHudReticleProbeState>();
        if (hudReticleProbeSize != HudReticleProbeStateByteSize)
            throw new InvalidOperationException($"HudReticleProbeState layout is {hudReticleProbeSize}, expected {HudReticleProbeStateByteSize}");

        var hudReticlePaintConfigSize = Marshal.SizeOf<NativeHudReticlePaintConfig>();
        if (hudReticlePaintConfigSize != HudReticlePaintConfigByteSize)
            throw new InvalidOperationException($"HudReticlePaintConfig layout is {hudReticlePaintConfigSize}, expected {HudReticlePaintConfigByteSize}");

    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct BotControllerAbiInfo
{
    public const int ByteSize = 44;

    public int AbiMajor;
    public int AbiMinor;
    public int MovementSnapshotSize;
    public int ReplayTickSize;
    public int SubtickMoveSize;
    public int ReplaySlotStateSize;
    public int MaxSlots;
    public ulong Capabilities;
    public int Reserved0;
    public int Reserved1;

    public static BotControllerAbiInfo Unavailable => new()
    {
        AbiMajor = -1,
        AbiMinor = 0,
        MovementSnapshotSize = BotControllerNative.MovementSnapshotByteSize,
        ReplayTickSize = BotControllerNative.ReplayTickByteSize,
        SubtickMoveSize = BotControllerNative.SubtickMoveByteSize,
        ReplaySlotStateSize = BotControllerNative.ReplaySlotStateByteSize,
        MaxSlots = BotControllerNative.MaxSlots,
        Capabilities = 0
    };
}

internal readonly record struct ReplayFileMetadata(
    float TickRate,
    uint PlayStartTickIndex,
    int TickCount,
    ReplayProjectileEvent[] Projectiles,
    ReplayHighFidelityMetadata HighFidelity,
    int[] WeaponDefIndices)
{
    public static ReplayFileMetadata Empty { get; } = new(0.0f, 0, 0, [], ReplayHighFidelityMetadata.Empty, []);
}

internal readonly record struct ReplayState(
    int Cursor,
    int Total,
    bool Playing,
    int CurrentTickIndex,
    int WeaponDefIndex,
    int NumSubtick)
{
    public static ReplayState Empty { get; } = new(-1, 0, false, -1, -1, 0);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplaySlotState
{
    public int Playing;
    public int Cursor;
    public int Total;
    public int CurrentTickIndex;
    public int WeaponDefIndex;
    public int NumSubtick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeHudReticleProbeState
{
    public int Size;
    public int Rc;
    public int Installed;
    public int Enabled;
    public int ActionsApplied;

    public ulong ClientBase;
    public ulong ConfigTargetPtr;
    public ulong ConfigOriginalPtr;

    public int Flags;
    public int ConfigInstallRc;
    public int ConfigCalls;
    public int ConfigPatched;
    public int ConfigErrors;
    public int ConfigModeBefore;
    public int ConfigModeAfter;
    public int ConfigColorBefore;
    public int ConfigColorAfter;
    public int ConfigGap100Before;
    public int ConfigGap100After;
    public int ConfigSize100Before;
    public int ConfigSize100After;
    public int ConfigThickness100Before;
    public int ConfigThickness100After;
    public int ConfigDotBefore;
    public int ConfigDotAfter;
    public int ConfigUseAlphaAfter;
    public int ConfigAlphaAfter;
    public int ConfigOutline100After;
    public ulong ConfigRgbaPacked;
    public int ConfigLiveGap100Before;
    public int ConfigLiveGap100After;
    public int ConfigSmoothGap100Before;
    public int ConfigSmoothGap100After;
    public int ConfigRecoilAfter;
    public int ConfigGapUseWeaponAfter;
    public int ConfigGuardMatched;
    public int ConfigGuardMissed;
    public int ConfigGuardActive;
    public int ConfigMapCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeHudReticlePaintConfig
{
    public int Size;
    public int Style;
    public int Color;
    public int DrawOutline;
    public int Dot;
    public int GapUseWeaponValue;
    public int UseAlpha;
    public int TStyle;
    public int Gap100;
    public int Size100;
    public int Thickness100;
    public int Outline100;
    public int Alpha;
    public int Red;
    public int Green;
    public int Blue;
}

internal enum ReplayProjectileKind : byte
{
    Unknown = 0,
    Smoke = 1,
    Flash = 2,
    He = 3,
    Molotov = 4,
    Decoy = 5
}

internal readonly record struct ReplayVector3(float X, float Y, float Z);

internal readonly record struct ReplayProjectileEvent(
    uint TickIndex,
    ReplayProjectileKind Kind,
    int WeaponDefIndex,
    ReplayVector3 InitialPosition,
    ReplayVector3 InitialVelocity,
    ReplayVector3 DetonationPosition,
    ReplayVector3 EffectPosition,
    int EffectTickIndex,
    string EffectSource,
    float EffectConfidence);

internal sealed class ReplayHighFidelityMetadata
{
    public static ReplayHighFidelityMetadata Empty { get; } = new();

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 3;

    [JsonPropertyName("events")]
    public ReplayHifiEvent[] Events { get; set; } = [];

    [JsonPropertyName("inventory_snapshots")]
    public ReplayInventorySnapshot[] InventorySnapshots { get; set; } = [];

    [JsonPropertyName("projectiles")]
    public ReplayProjectileMetadata[] Projectiles { get; set; } = [];
}

internal sealed class ReplayProjectileMetadata
{
    [JsonPropertyName("tick_index")]
    public uint TickIndex { get; set; }

    [JsonPropertyName("tick")]
    public int Tick { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("weapon_def_index")]
    public int WeaponDefIndex { get; set; }

    [JsonPropertyName("effect_tick_index")]
    public uint? EffectTickIndex { get; set; }

    [JsonPropertyName("effect_tick")]
    public int? EffectTick { get; set; }

    [JsonPropertyName("effect_position")]
    public float[]? EffectPosition { get; set; }

    [JsonPropertyName("effect_source")]
    public string EffectSource { get; set; } = string.Empty;

    [JsonPropertyName("effect_confidence")]
    public float EffectConfidence { get; set; }
}

internal sealed class ReplayHifiEvent
{
    [JsonPropertyName("tick_index")]
    public uint TickIndex { get; set; }

    [JsonPropertyName("tick")]
    public int Tick { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("actor_steam_id")]
    public ulong? ActorSteamId { get; set; }

    [JsonPropertyName("target_steam_id")]
    public ulong? TargetSteamId { get; set; }

    [JsonPropertyName("weapon_def_index")]
    public int? WeaponDefIndex { get; set; }

    [JsonPropertyName("item_name")]
    public string? ItemName { get; set; }

    [JsonPropertyName("entity_id")]
    public int? EntityId { get; set; }

    [JsonPropertyName("actor_count_after")]
    public int? ActorCountAfter { get; set; }

    [JsonPropertyName("target_count_after")]
    public int? TargetCountAfter { get; set; }

    [JsonPropertyName("damage")]
    public int? Damage { get; set; }

    [JsonPropertyName("health")]
    public int? Health { get; set; }
}

internal sealed class ReplayInventorySnapshot
{
    [JsonPropertyName("tick_index")]
    public uint TickIndex { get; set; }

    [JsonPropertyName("tick")]
    public int Tick { get; set; }

    [JsonPropertyName("steam_id")]
    public ulong SteamId { get; set; }

    [JsonPropertyName("weapon_def_counts")]
    public ReplayInventoryItemCount[] WeaponDefCounts { get; set; } = [];

    [JsonPropertyName("active_weapon_def_index")]
    public int ActiveWeaponDefIndex { get; set; }

    [JsonPropertyName("armor_value")]
    public int ArmorValue { get; set; }

    [JsonPropertyName("has_helmet")]
    public bool HasHelmet { get; set; }

    [JsonPropertyName("has_defuser")]
    public bool HasDefuser { get; set; }
}

internal sealed class ReplayInventoryItemCount
{
    [JsonPropertyName("weapon_def_index")]
    public int WeaponDefIndex { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeMovementSnapshot
{
    public float OriginX, OriginY, OriginZ;
    public float VelX, VelY, VelZ;
    public float Pitch, Yaw, Roll;
    public uint EntityFlags;
    public byte MoveType;
    public byte Pad0, Pad1, Pad2;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public float DuckAmount;
    public float DuckSpeed;
    public float LadderNormalX;
    public float LadderNormalY;
    public float LadderNormalZ;
    public byte Ducked;
    public byte Ducking;
    public byte DesiresDuck;
    public byte ActualMoveType;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayTick
{
    public NativeMovementSnapshot Pre;
    public NativeMovementSnapshot Post;
    public int WeaponDefIndex;
    public uint NumSubtick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeSubtickMove
{
    public float When;
    public uint Button;
    public float Pressed;
    public float AnalogForward;
    public float AnalogLeft;
    public float PitchDelta;
    public float YawDelta;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayCommandFrame
{
    public float ForwardMove;
    public float LeftMove;
    public float UpMove;
    public float Pitch;
    public float Yaw;
    public float Roll;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public int MouseDx;
    public int MouseDy;
    public int WeaponSelect;
    public uint Fields;
    public byte LeftHandDesired;
    public byte Pad0;
    public byte Pad1;
    public byte Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayMovementExtra
{
    public uint Fields;
    public float JumpPressedTime;
    public float LastDuckTime;
    public int LastActualJumpPressTick;
    public float LastActualJumpPressFrac;
    public int LastUsableJumpPressTick;
    public float LastUsableJumpPressFrac;
    public int LastLandedTick;
    public float LastLandedFrac;
    public float LastLandedVelocityX;
    public float LastLandedVelocityY;
    public float LastLandedVelocityZ;
}
