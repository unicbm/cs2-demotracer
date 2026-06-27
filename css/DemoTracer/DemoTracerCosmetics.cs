using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const int CosmeticHeartbeatAttempts = 12;
    private const float CosmeticHeartbeatIntervalSeconds = 0.10f;
    private const string AttributeSetterWindowsSignature = "40 53 55 41 56 48 81 EC 90 00 00 00";
    private const string AttributeSetterLinuxSignature = "55 48 89 E5 41 57 41 56 49 89 FE 41 55 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85";
    private static readonly (int WeaponDefIndex, int PaintKit)[] BuiltInLegacyCosmeticPaints =
    [
        // Fallback when skins_en.json is absent. These older USP-S finishes use
        // the legacy bodygroup in CS2; bodygroup 0 renders as the plain dark model.
        (61, 277), // USP-S | Stainless
        (61, 339), // USP-S | Caiman
        (61, 504), // USP-S | Kill Confirmed
    ];
    private static readonly Lazy<MemoryFunctionVoid<nint, string, float>?> AttributeSetter = new(CreateAttributeSetter);
    private readonly HashSet<(int WeaponDefIndex, int PaintKit)> _legacyCosmeticPaints = new();
    private readonly Dictionary<int, int> _cosmeticHeartbeatTokens = new();
    private bool _cosmeticGiveNamedItemHooked;
    private int _nextCosmeticHeartbeatToken;

    private void HookCosmeticGiveNamedItem()
    {
        if (_cosmeticGiveNamedItemHooked)
            return;

        try
        {
            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPostForCosmetics, HookMode.Post);
            _cosmeticGiveNamedItemHooked = true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic GiveNamedItem hook unavailable: {ex.Message}");
        }
    }

    private void UnhookCosmeticGiveNamedItem()
    {
        if (!_cosmeticGiveNamedItemHooked)
            return;

        try
        {
            VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPostForCosmetics, HookMode.Post);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic GiveNamedItem unhook failed: {ex.Message}");
        }
        finally
        {
            _cosmeticGiveNamedItemHooked = false;
        }
    }

    private void LoadCosmeticLegacyPaints()
    {
        _legacyCosmeticPaints.Clear();
        foreach (var (weaponDefIndex, paintKit) in BuiltInLegacyCosmeticPaints)
            _legacyCosmeticPaints.Add((NormalizeWeaponDefIndex(weaponDefIndex), paintKit));

        var path = Path.Combine(ModuleDirectory, "skins_en.json");
        if (!File.Exists(path))
        {
            Server.PrintToConsole(
                $"dtr: cosmetic legacy paint lookup not found; using built-in fallback entries={_legacyCosmeticPaints.Count}");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("legacy_model", out var legacy) ||
                    legacy.ValueKind != JsonValueKind.True)
                {
                    continue;
                }
                if (!item.TryGetProperty("weapon_defindex", out var defElement) ||
                    !item.TryGetProperty("paint", out var paintElement))
                {
                    continue;
                }

                var weaponDefIndex = ReadFlexibleInt(defElement);
                var paintKit = ReadFlexibleInt(paintElement);
                if (weaponDefIndex > 0 && paintKit > 0)
                    _legacyCosmeticPaints.Add((weaponDefIndex, paintKit));
            }
            Server.PrintToConsole($"dtr: loaded cosmetic legacy paint lookup entries={_legacyCosmeticPaints.Count}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to load cosmetic legacy paint lookup: {ex.Message}");
        }

        static int ReadFlexibleInt(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number
                ? element.GetInt32()
                : int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
            : 0;
        }
    }

    private static ReplayCosmetics NormalizeReplayCosmetics(ReplayCosmetics? cosmetics)
    {
        var normalized = new ReplayCosmetics();
        if (cosmetics == null)
            return normalized;

        foreach (var group in (cosmetics.Weapons ?? [])
                     .Where(IsValidWeaponCosmetic)
                     .GroupBy(weapon => NormalizeWeaponDefIndex(weapon.WeaponDefIndex)))
        {
            if (!IsWeaponCosmeticDefIndex(group.Key) || group.Count() != 1)
                continue;
            var weapon = group.First();
            normalized.Weapons.Add(new ReplayWeaponCosmetic
            {
                WeaponDefIndex = group.Key,
                PaintKit = weapon.PaintKit,
                Seed = weapon.Seed,
                Wear = weapon.Wear,
                Quality = NormalizeStattrakQuality(weapon.Quality),
                StattrakCounter = NormalizeStattrakCounter(weapon.StattrakCounter),
                CustomName = NormalizeCosmeticCustomName(weapon.CustomName),
                Stickers = NormalizeWeaponStickers(weapon.Stickers),
                Charms = NormalizeWeaponCharms(weapon.Charms)
            });
        }

        if (cosmetics.Knife is { } knife &&
            IsValidItemCosmetic(knife) &&
            knife.ItemDefIndex is { } knifeDef &&
            IsKnifeCosmeticDefIndex(knifeDef))
        {
            normalized.Knife = CloneItemCosmetic(knife);
        }

        if (cosmetics.Glove is { } glove &&
            IsValidItemCosmetic(glove) &&
            (glove.ItemDefIndex == null ||
             IsPlausibleCosmeticItemDefIndex(glove.ItemDefIndex.Value)))
        {
            normalized.Glove = CloneItemCosmetic(glove);
        }

        normalized.Weapons = normalized.Weapons
            .OrderBy(weapon => weapon.WeaponDefIndex)
            .ToList();
        return normalized;
    }

    private static ReplayItemCosmetic CloneItemCosmetic(ReplayItemCosmetic source)
        => new()
        {
            ItemDefIndex = source.ItemDefIndex,
            PaintKit = source.PaintKit,
            Seed = source.Seed,
            Wear = source.Wear,
            CustomName = NormalizeCosmeticCustomName(source.CustomName)
        };

    private static bool HasCosmeticEvidence(ReplayCosmetics? cosmetics)
        => cosmetics != null &&
           ((cosmetics.Weapons?.Count ?? 0) > 0 ||
            cosmetics.Knife != null ||
            cosmetics.Glove != null);

    private static bool IsValidWeaponCosmetic(ReplayWeaponCosmetic cosmetic)
        => cosmetic.PaintKit > 0 && cosmetic.Wear is >= 0.0f and <= 1.0f && float.IsFinite(cosmetic.Wear);

    private static bool IsValidItemCosmetic(ReplayItemCosmetic? cosmetic)
        => cosmetic != null &&
           cosmetic.PaintKit > 0 &&
           cosmetic.Wear is >= 0.0f and <= 1.0f &&
           float.IsFinite(cosmetic.Wear);

    private static int? NormalizeStattrakQuality(int? quality)
        => quality == 9 ? 9 : null;

    private static int? NormalizeStattrakCounter(int? counter)
        => counter is >= 0 ? counter : null;

    private static List<ReplayWeaponSticker> NormalizeWeaponStickers(IEnumerable<ReplayWeaponSticker>? stickers)
    {
        if (stickers == null)
            return [];

        var normalized = new List<ReplayWeaponSticker>();
        var slots = new HashSet<int>();
        foreach (var sticker in stickers)
        {
            if (sticker.Slot is < 0 or > 4 ||
                sticker.StickerId == 0 ||
                sticker.Wear is < 0.0f or > 1.0f ||
                !float.IsFinite(sticker.Wear) ||
                !float.IsFinite(sticker.OffsetX) ||
                !float.IsFinite(sticker.OffsetY) ||
                (sticker.Scale.HasValue && !float.IsFinite(sticker.Scale.Value)) ||
                (sticker.Rotation.HasValue && !float.IsFinite(sticker.Rotation.Value)) ||
                !slots.Add(sticker.Slot))
            {
                return [];
            }

            normalized.Add(new ReplayWeaponSticker
            {
                Slot = sticker.Slot,
                StickerId = sticker.StickerId,
                Wear = sticker.Wear,
                OffsetX = sticker.OffsetX,
                OffsetY = sticker.OffsetY,
                Scale = sticker.Scale,
                Rotation = sticker.Rotation
            });
        }

        return normalized
            .OrderBy(sticker => sticker.Slot)
            .ToList();
    }

    private static List<ReplayWeaponCharm> NormalizeWeaponCharms(IEnumerable<ReplayWeaponCharm>? charms)
    {
        if (charms == null)
            return [];

        var normalized = new List<ReplayWeaponCharm>();
        var slots = new HashSet<int>();
        foreach (var charm in charms)
        {
            if (charm.Slot != 0 ||
                charm.CharmId == 0 ||
                !float.IsFinite(charm.OffsetX) ||
                !float.IsFinite(charm.OffsetY) ||
                !float.IsFinite(charm.OffsetZ) ||
                !slots.Add(charm.Slot))
            {
                return [];
            }

            normalized.Add(new ReplayWeaponCharm
            {
                Slot = charm.Slot,
                CharmId = charm.CharmId,
                OffsetX = charm.OffsetX,
                OffsetY = charm.OffsetY,
                OffsetZ = charm.OffsetZ,
                Seed = NormalizeOptionalUInt(charm.Seed),
                Highlight = NormalizeOptionalUInt(charm.Highlight),
                StickerId = NormalizeOptionalUInt(charm.StickerId)
            });
        }

        return normalized
            .OrderBy(charm => charm.Slot)
            .ToList();
    }

    private static uint? NormalizeOptionalUInt(uint? value)
        => value is > 0 ? value : null;

    private static string? NormalizeCosmeticCustomName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = new string(value
            .Trim()
            .Where(ch => !char.IsControl(ch) || ch == '\t')
            .Take(128)
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static bool IsWeaponCosmeticDefIndex(int weaponDefIndex)
        => IsKnownWeaponDefIndex(weaponDefIndex) &&
           weaponDefIndex is not 31 and not 42 and not 43 and not 44 and not 45 and not 46 and not 47 and not 48 and not 49;

    private static bool IsKnifeCosmeticDefIndex(int weaponDefIndex)
        => weaponDefIndex == 42 || weaponDefIndex == 59 || weaponDefIndex is >= 500 and < 600;

    private static bool IsPlausibleCosmeticItemDefIndex(int itemDefIndex)
        => itemDefIndex is >= 5027 and <= 5035;

    private void ResetCosmeticAlignState(bool resetCounters = false)
    {
        _cosmeticSyncedSlots.Clear();
        _cosmeticHeartbeatTokens.Clear();
        if (resetCounters)
        {
            _cosmeticAppliedCount = 0;
            _cosmeticSkippedCount = 0;
        }
    }

    private void ResetStickerAlignState(bool resetCounters = false)
    {
        if (resetCounters)
        {
            _stickerAppliedCount = 0;
            _stickerSkippedCount = 0;
        }
    }

    private void ResetCharmAlignState(bool resetCounters = false)
    {
        if (resetCounters)
        {
            _charmAppliedCount = 0;
            _charmSkippedCount = 0;
        }
    }

    private string FormatCosmeticStatusCounts()
    {
        var counts = CountLoadedCosmeticEvidence();
        return
            $"cosmetics_evidence={counts.Files} cosmetic_weapons={counts.Weapons} cosmetic_knives={counts.Knives} cosmetic_gloves={counts.Gloves} sticker_evidence={counts.Stickers} charm_evidence={counts.Charms} applied={_cosmeticAppliedCount} skipped={_cosmeticSkippedCount} sticker_applied={_stickerAppliedCount} sticker_skipped={_stickerSkippedCount} charm_applied={_charmAppliedCount} charm_skipped={_charmSkippedCount}";
    }

    private (int Files, int Weapons, int Knives, int Gloves, int Stickers, int Charms) CountLoadedCosmeticEvidence()
    {
        var files = 0;
        var weapons = 0;
        var knives = 0;
        var gloves = 0;
        var stickers = 0;
        var charms = 0;

        foreach (var replay in _loadedReplays.Values)
        {
            if (replay.UtilityOnly || !HasCosmeticEvidence(replay.Cosmetics))
                continue;

            files++;
            weapons += replay.Cosmetics.Weapons.Count;
            stickers += replay.Cosmetics.Weapons.Sum(weapon => weapon.Stickers.Count);
            charms += replay.Cosmetics.Weapons.Sum(weapon => weapon.Charms.Count);
            if (replay.Cosmetics.Knife != null)
                knives++;
            if (replay.Cosmetics.Glove != null)
                gloves++;
        }

        return (files, weapons, knives, gloves, stickers, charms);
    }

    private void ApplyLoadedReplayCosmeticsForSlot(int slot, LoadedReplay replay)
    {
        if (!_cosmeticAlignEnabled ||
            replay.UtilityOnly ||
            !HasCosmeticEvidence(replay.Cosmetics) ||
            !IsReplaySlotStillSafe(slot))
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
            return;

        var applied = 0;
        var skipped = 0;
        foreach (var cosmetic in replay.Cosmetics.Weapons)
        {
            if (TryFindReplayWeaponByDefIndex(pawn, cosmetic.WeaponDefIndex, out var weapon) &&
                TryApplyWeaponCosmetic(player, weapon, cosmetic))
            {
                applied++;
                ScheduleReplayWeaponCosmeticRetry(slot, cosmetic, framesRemaining: 3);
            }
            else
            {
                skipped++;
            }
        }

        if (replay.Cosmetics.Knife != null)
        {
            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyItemCosmetic(player, knife, replay.Cosmetics.Knife, allowSubclassChange: true))
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }

        if (replay.Cosmetics.Glove != null)
        {
            if (TryApplyGloveCosmetic(player, pawn, replay.Cosmetics.Glove))
                applied++;
            else
                skipped++;
        }

        _cosmeticAppliedCount += applied;
        _cosmeticSkippedCount += skipped;
        if (applied > 0)
        {
            _cosmeticSyncedSlots.Add(slot);
            Server.PrintToConsole(
                $"dtr: cosmetic aligned slot={slot} player={replay.PlayerName} applied={applied} skipped={skipped}");
        }

        if (replay.Cosmetics.Weapons.Count > 0)
            ScheduleReplayCosmeticHeartbeat(slot);
    }

    private void ScheduleReplayCosmeticHeartbeat(int slot)
    {
        var token = ++_nextCosmeticHeartbeatToken;
        _cosmeticHeartbeatTokens[slot] = token;
        AddTimer(
            CosmeticHeartbeatIntervalSeconds,
            () => RunReplayCosmeticHeartbeat(slot, token, CosmeticHeartbeatAttempts),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RunReplayCosmeticHeartbeat(int slot, int token, int attemptsRemaining)
    {
        if (attemptsRemaining <= 0 ||
            !_cosmeticHeartbeatTokens.TryGetValue(slot, out var activeToken) ||
            activeToken != token)
        {
            return;
        }

        if (!_cosmeticAlignEnabled ||
            !_weaponAlignEnabled ||
            !_loadedReplays.TryGetValue(slot, out var replay) ||
            replay.UtilityOnly ||
            !HasCosmeticEvidence(replay.Cosmetics) ||
            !IsReplaySlotStillSafe(slot))
        {
            _cosmeticHeartbeatTokens.Remove(slot);
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is { IsValid: true, PawnIsAlive: true } && pawn is { IsValid: true })
        {
            foreach (var cosmetic in replay.Cosmetics.Weapons)
            {
                if (TryFindReplayWeaponByDefIndex(pawn, cosmetic.WeaponDefIndex, out var weapon))
                    _ = TryApplyWeaponCosmetic(player, weapon, cosmetic, countStickerStats: false);
            }
        }

        if (attemptsRemaining == 1)
        {
            _cosmeticHeartbeatTokens.Remove(slot);
            return;
        }

        AddTimer(
            CosmeticHeartbeatIntervalSeconds,
            () => RunReplayCosmeticHeartbeat(slot, token, attemptsRemaining - 1),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ScheduleReplayWeaponCosmeticRetry(
        int slot,
        ReplayWeaponCosmetic cosmetic,
        int framesRemaining)
    {
        if (framesRemaining <= 0)
            return;

        Server.NextFrame(() =>
        {
            if (!_cosmeticAlignEnabled || !_weaponAlignEnabled || !IsReplaySlotStillSafe(slot))
                return;

            var refreshedPlayer = Utilities.GetPlayerFromSlot(slot);
            var refreshedPawn = refreshedPlayer?.PlayerPawn.Value;
            if (refreshedPlayer is not { IsValid: true, PawnIsAlive: true } ||
                refreshedPawn is not { IsValid: true })
            {
                return;
            }

            if (TryFindReplayWeaponByDefIndex(refreshedPawn, cosmetic.WeaponDefIndex, out var refreshedWeapon) &&
                TryApplyWeaponCosmetic(refreshedPlayer, refreshedWeapon, cosmetic))
            {
                ScheduleReplayWeaponCosmeticRetry(slot, cosmetic, framesRemaining - 1);
            }
        });
    }

    private void ApplyReplayWeaponCosmeticForSlot(int slot, int weaponDefIndex)
    {
        if (!_cosmeticAlignEnabled ||
            !_loadedReplays.TryGetValue(slot, out var replay) ||
            replay.UtilityOnly ||
            !HasCosmeticEvidence(replay.Cosmetics) ||
            !IsReplaySlotStillSafe(slot))
        {
            return;
        }

        var cosmetic = replay.Cosmetics.Weapons
            .FirstOrDefault(weapon => weapon.WeaponDefIndex == NormalizeWeaponDefIndex(weaponDefIndex));
        if (cosmetic == null)
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
            return;

        if (TryFindReplayWeaponByDefIndex(pawn, cosmetic.WeaponDefIndex, out var weapon) &&
            TryApplyWeaponCosmetic(player, weapon, cosmetic))
        {
            _cosmeticAppliedCount++;
        }
        else
        {
            _cosmeticSkippedCount++;
        }
    }

    private HookResult OnGiveNamedItemPostForCosmetics(DynamicHook hook)
    {
        try
        {
            if (!_cosmeticAlignEnabled || !_weaponAlignEnabled)
                return HookResult.Continue;

            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var weapon = hook.GetReturn<CBasePlayerWeapon>();
            if (weapon == null || !weapon.IsValid)
                return HookResult.Continue;

            if (!TryFindReplayPlayerByItemServices(itemServices, out var slot, out var player))
                return HookResult.Continue;

            TryApplyGivenWeaponCosmetic(slot, player, weapon, countResult: true);
            var handle = weapon.Handle;
            Server.NextFrame(() =>
            {
                if (!_cosmeticAlignEnabled || !_weaponAlignEnabled || !IsReplaySlotStillSafe(slot))
                    return;

                try
                {
                    var retryWeapon = new CBasePlayerWeapon(handle);
                    var retryPlayer = Utilities.GetPlayerFromSlot(slot);
                    if (retryWeapon.IsValid &&
                        retryPlayer is { IsValid: true, PawnIsAlive: true })
                    {
                        TryApplyGivenWeaponCosmetic(slot, retryPlayer, retryWeapon, countResult: false);
                    }
                }
                catch
                {
                }
            });
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic GiveNamedItem post failed: {ex.Message}");
        }

        return HookResult.Continue;
    }

    private bool TryFindReplayPlayerByItemServices(
        CCSPlayer_ItemServices itemServices,
        out int slot,
        out CCSPlayerController player)
    {
        slot = -1;
        player = null!;
        if (itemServices == null || itemServices.Handle == IntPtr.Zero)
            return false;

        foreach (var candidateSlot in _loadedSlots.ToArray())
        {
            if (!IsReplaySlotStillSafe(candidateSlot))
                continue;

            var candidate = Utilities.GetPlayerFromSlot(candidateSlot);
            var pawn = candidate?.PlayerPawn.Value;
            if (candidate is not { IsValid: true, PawnIsAlive: true } ||
                pawn is not { IsValid: true } ||
                pawn.ItemServices == null ||
                pawn.ItemServices.Handle != itemServices.Handle)
            {
                continue;
            }

            slot = candidateSlot;
            player = candidate;
            return true;
        }

        return false;
    }

    private bool TryApplyGivenWeaponCosmetic(
        int slot,
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        bool countResult)
    {
        if (!_loadedReplays.TryGetValue(slot, out var replay) ||
            replay.UtilityOnly ||
            !HasCosmeticEvidence(replay.Cosmetics) ||
            !IsReplaySlotStillSafe(slot))
        {
            return false;
        }

        var weaponDefIndex = WeaponDefIndex(weapon);
        if (!IsWeaponCosmeticDefIndex(weaponDefIndex))
            return false;

        var cosmetic = replay.Cosmetics.Weapons
            .FirstOrDefault(candidate => candidate.WeaponDefIndex == weaponDefIndex);
        if (cosmetic == null)
            return false;

        var ok = TryApplyWeaponCosmetic(player, weapon, cosmetic);
        if (countResult)
        {
            if (ok)
                _cosmeticAppliedCount++;
            else
                _cosmeticSkippedCount++;
        }
        return ok;
    }

    private void TryApplySpawnedReplayWeaponCosmetic(CEntityInstance entity)
    {
        if (!_cosmeticAlignEnabled || !_weaponAlignEnabled)
            return;
        var name = entity.DesignerName;
        if (string.IsNullOrWhiteSpace(name) ||
            !name.Contains("weapon", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var handle = entity.Handle;
        Server.NextFrame(() =>
        {
            if (!_cosmeticAlignEnabled || !_weaponAlignEnabled)
                return;

            CBasePlayerWeapon weapon;
            try
            {
                weapon = new CBasePlayerWeapon(handle);
            }
            catch
            {
                return;
            }

            if (!weapon.IsValid)
                return;

            var weaponDefIndex = WeaponDefIndex(weapon);
            if (!IsWeaponCosmeticDefIndex(weaponDefIndex))
                return;

            foreach (var slot in _loadedSlots.ToArray())
            {
                if (!_loadedReplays.TryGetValue(slot, out var replay) ||
                    replay.UtilityOnly ||
                    !IsReplaySlotStillSafe(slot))
                {
                    continue;
                }

                var cosmetic = replay.Cosmetics.Weapons
                    .FirstOrDefault(candidate => candidate.WeaponDefIndex == weaponDefIndex);
                if (cosmetic == null)
                    continue;

                var player = Utilities.GetPlayerFromSlot(slot);
                var pawn = player?.PlayerPawn.Value;
                if (player is not { IsValid: true, PawnIsAlive: true } ||
                    pawn is not { IsValid: true } ||
                    !PawnOwnsWeapon(pawn, weapon))
                {
                    continue;
                }

                if (TryApplyWeaponCosmetic(player, weapon, cosmetic))
                    _cosmeticAppliedCount++;
                else
                    _cosmeticSkippedCount++;

                Server.NextFrame(() =>
                {
                    if (!_cosmeticAlignEnabled || !IsReplaySlotStillSafe(slot))
                        return;
                    var retryPlayer = Utilities.GetPlayerFromSlot(slot);
                    if (retryPlayer is { IsValid: true, PawnIsAlive: true } && weapon.IsValid)
                        _ = TryApplyWeaponCosmetic(retryPlayer, weapon, cosmetic);
                });
                return;
            }
        });
    }

    private static bool PawnOwnsWeapon(CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            if (candidate.Handle == weapon.Handle)
                return true;
        }

        return false;
    }

    private static bool TryFindReplayWeaponByDefIndex(
        CCSPlayerPawn pawn,
        int weaponDefIndex,
        out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className) ||
            pawn.WeaponServices == null)
        {
            return false;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            if (WeaponClassMatches(candidate.DesignerName, className) ||
                WeaponDefIndex(candidate) == weaponDefIndex)
            {
                weapon = candidate;
                return true;
            }
        }

        return false;
    }

    private static int WeaponDefIndex(CBasePlayerWeapon weapon)
    {
        try
        {
            var itemDef = NormalizeWeaponDefIndex(weapon.AttributeManager.Item.ItemDefinitionIndex);
            if (IsKnownWeaponDefIndex(itemDef))
                return itemDef;
        }
        catch
        {
        }

        return WeaponDefIndex(weapon.DesignerName);
    }

    private static bool TryFindReplayKnife(CCSPlayerPawn pawn, out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            var name = candidate.DesignerName;
            if (name.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
            {
                weapon = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryApplyWeaponCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        bool countStickerStats = true)
    {
        if (!TryGetWeaponClassByDefIndex(cosmetic.WeaponDefIndex, out _))
            return false;

        var applied = TryApplyItemCosmetic(
            player,
            weapon,
            new ReplayItemCosmetic
            {
                ItemDefIndex = cosmetic.WeaponDefIndex,
                PaintKit = cosmetic.PaintKit,
                Seed = cosmetic.Seed,
                Wear = cosmetic.Wear,
                CustomName = cosmetic.CustomName
            },
            allowSubclassChange: false);
        if (!applied)
        {
            if (countStickerStats)
            {
                RecordStickerSkipped(cosmetic.Stickers.Count);
                RecordCharmSkipped(cosmetic.Charms.Count);
            }
            return false;
        }

        ApplyWeaponStattrakEvidence(weapon, cosmetic);
        ApplyWeaponStickers(weapon, cosmetic, countStickerStats);
        ApplyWeaponCharms(weapon, cosmetic, countStickerStats);
        return true;
    }

    private bool TryApplyItemCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayItemCosmetic cosmetic,
        bool allowSubclassChange)
    {
        try
        {
            var item = weapon.AttributeManager.Item;
            if (cosmetic.ItemDefIndex is { } itemDef)
            {
                if (allowSubclassChange && IsKnifeCosmeticDefIndex(itemDef))
                    weapon.AcceptInput("ChangeSubclass", value: itemDef.ToString(CultureInfo.InvariantCulture));
                item.ItemDefinitionIndex = (ushort)itemDef;
            }

            item.EntityQuality = allowSubclassChange ? 3 : 0;
            item.AccountID = (uint)player.SteamID;
            item.AttributeList.Attributes.RemoveAll();
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            UpdateReplayEconItemId(item);
            weapon.FallbackPaintKit = (int)Math.Min(cosmetic.PaintKit, int.MaxValue);
            weapon.FallbackSeed = (int)Math.Min(cosmetic.Seed, int.MaxValue);
            weapon.FallbackWear = cosmetic.Wear;
            MarkWeaponPaintStateChanged(weapon);
            if (!string.IsNullOrWhiteSpace(cosmetic.CustomName))
                item.CustomName = cosmetic.CustomName;
            _ = TrySetTextureAttributes(item.NetworkedDynamicAttributes.Handle, cosmetic);
            _ = TrySetTextureAttributes(item.AttributeList.Handle, cosmetic);
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            var bodygroup = IsLegacyCosmeticPaint(
                item.ItemDefinitionIndex,
                (int)Math.Min(cosmetic.PaintKit, int.MaxValue)) ? 1 : 0;
            weapon.AcceptInput("SetBodygroup", value: $"body,{bodygroup}");
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: cosmetic apply failed slot={player.Slot} item={weapon.DesignerName}: {ex.Message}");
            return false;
        }
    }

    private bool IsLegacyCosmeticPaint(int weaponDefIndex, int paintKit)
        => _legacyCosmeticPaints.Contains((NormalizeWeaponDefIndex(weaponDefIndex), paintKit));

    private static void ApplyWeaponStattrakEvidence(
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic)
    {
        if (cosmetic.Quality != 9 && cosmetic.StattrakCounter == null)
            return;

        var item = weapon.AttributeManager.Item;
        item.EntityQuality = 9;
        weapon.FallbackStatTrak = cosmetic.StattrakCounter ?? 0;
        _ = TrySetStattrakAttributes(item.NetworkedDynamicAttributes.Handle, weapon.FallbackStatTrak);
        _ = TrySetStattrakAttributes(item.AttributeList.Handle, weapon.FallbackStatTrak);
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackStatTrak");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
    }

    private bool TryApplyGloveCosmetic(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemCosmetic cosmetic)
    {
        try
        {
            if (AttributeSetter.Value == null)
                return false;

            if (!ApplyGloveEconItem(player, pawn, cosmetic))
                return false;
            AddTimer(0.10f, () => ApplyGloveCosmeticForSlot(player.Slot, cosmetic), TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(0.25f, () => ApplyGloveCosmeticForSlot(player.Slot, cosmetic), TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic apply failed slot={player.Slot}: {ex.Message}");
            return false;
        }
    }

    private void ApplyGloveCosmeticForSlot(int slot, ReplayItemCosmetic cosmetic)
    {
        try
        {
            if (!_cosmeticAlignEnabled || !IsReplaySlotStillSafe(slot))
                return;

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            ApplyGloveEconItem(player, pawn, cosmetic);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic delayed apply failed slot={slot}: {ex.Message}");
        }
    }

    private bool ApplyGloveEconItem(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemCosmetic cosmetic)
    {
        var item = pawn.EconGloves;
        if (cosmetic.ItemDefIndex is { } itemDef)
            item.ItemDefinitionIndex = (ushort)itemDef;
        item.AccountID = (uint)player.SteamID;
        item.Initialized = true;
        UpdateReplayEconItemId(item);

        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();
        if (!TrySetTextureAttributes(item.NetworkedDynamicAttributes.Handle, cosmetic) ||
            !TrySetTextureAttributes(item.AttributeList.Handle, cosmetic))
        {
            return false;
        }

        MarkGloveCosmeticStateChanged(pawn);
        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
        AddTimer(0.2f, () =>
        {
            if (pawn.IsValid)
                pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    private static ulong _nextReplayEconItemId = 10_000_000_000;

    private static void MarkWeaponPaintStateChanged(CBasePlayerWeapon weapon)
    {
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackPaintKit");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackSeed");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_flFallbackWear");
    }

    private static void MarkGloveCosmeticStateChanged(CCSPlayerPawn pawn)
    {
        try
        {
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_EconGloves");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic state change failed: {ex.Message}");
        }
    }

    private static void UpdateReplayEconItemId(CEconItemView item)
    {
        var itemId = _nextReplayEconItemId++;
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & 0xFFFFFFFF);
        item.ItemIDHigh = (uint)(itemId >> 32);
    }

    private static MemoryFunctionVoid<nint, string, float>? CreateAttributeSetter()
    {
        try
        {
            var signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AttributeSetterWindowsSignature
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? AttributeSetterLinuxSignature
                    : string.Empty;
            return string.IsNullOrWhiteSpace(signature)
                ? null
                : new MemoryFunctionVoid<nint, string, float>(signature);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic attribute setter unavailable: {ex.Message}");
            return null;
        }
    }

    private static bool TrySetTextureAttributes(nint attributeListHandle, ReplayItemCosmetic cosmetic)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture prefab", cosmetic.PaintKit);
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture seed", cosmetic.Seed);
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture wear", cosmetic.Wear);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic attribute write failed: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetStattrakAttributes(nint attributeListHandle, int counter)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            AttributeSetter.Value.Invoke(attributeListHandle, "kill eater", BitConverter.Int32BitsToSingle(counter));
            AttributeSetter.Value.Invoke(attributeListHandle, "kill eater score type", 0.0f);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: StatTrak attribute write failed: {ex.Message}");
            return false;
        }
    }

    private void ApplyWeaponStickers(
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        bool countStickerStats)
    {
        if (!_cosmeticAlignEnabled ||
            !_stickerAlignEnabled ||
            cosmetic.Stickers.Count == 0)
        {
            return;
        }

        if (AttributeSetter.Value == null)
        {
            if (countStickerStats)
                RecordStickerSkipped(cosmetic.Stickers.Count);
            return;
        }

        try
        {
            var item = weapon.AttributeManager.Item;
            var applied = 0;
            var skipped = 0;
            foreach (var sticker in cosmetic.Stickers)
            {
                var networkedOk = TrySetStickerAttributes(item.NetworkedDynamicAttributes.Handle, sticker);
                var listOk = TrySetStickerAttributes(item.AttributeList.Handle, sticker);
                if (networkedOk && listOk)
                    applied++;
                else
                    skipped++;
            }

            if (applied > 0)
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            if (countStickerStats)
            {
                _stickerAppliedCount += applied;
                _stickerSkippedCount += skipped;
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: sticker apply failed item={weapon.DesignerName}: {ex.Message}");
            if (countStickerStats)
                RecordStickerSkipped(cosmetic.Stickers.Count);
        }
    }

    private void RecordStickerSkipped(int count)
    {
        if (_cosmeticAlignEnabled && _stickerAlignEnabled && count > 0)
            _stickerSkippedCount += count;
    }

    private void ApplyWeaponCharms(
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        bool countCharmStats)
    {
        if (!_cosmeticAlignEnabled ||
            !_charmAlignEnabled ||
            cosmetic.Charms.Count == 0)
        {
            return;
        }

        if (AttributeSetter.Value == null)
        {
            if (countCharmStats)
                RecordCharmSkipped(cosmetic.Charms.Count);
            return;
        }

        try
        {
            var item = weapon.AttributeManager.Item;
            var applied = 0;
            var skipped = 0;
            foreach (var charm in cosmetic.Charms)
            {
                var networkedOk = TrySetCharmAttributes(item.NetworkedDynamicAttributes.Handle, charm);
                var listOk = TrySetCharmAttributes(item.AttributeList.Handle, charm);
                if (networkedOk && listOk)
                    applied++;
                else
                    skipped++;
            }

            if (applied > 0)
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            if (countCharmStats)
            {
                _charmAppliedCount += applied;
                _charmSkippedCount += skipped;
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: charm apply failed item={weapon.DesignerName}: {ex.Message}");
            if (countCharmStats)
                RecordCharmSkipped(cosmetic.Charms.Count);
        }
    }

    private void RecordCharmSkipped(int count)
    {
        if (_cosmeticAlignEnabled && _charmAlignEnabled && count > 0)
            _charmSkippedCount += count;
    }

    private static bool TrySetStickerAttributes(nint attributeListHandle, ReplayWeaponSticker sticker)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            var slot = $"sticker slot {sticker.Slot}";
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} id", BitConverter.UInt32BitsToSingle(sticker.StickerId));
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} wear", sticker.Wear);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset x", sticker.OffsetX);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset y", sticker.OffsetY);
            if (sticker.Scale.HasValue)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} scale", sticker.Scale.Value);
            if (sticker.Rotation.HasValue)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} rotation", sticker.Rotation.Value);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: sticker attribute write failed slot={sticker.Slot}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetCharmAttributes(nint attributeListHandle, ReplayWeaponCharm charm)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            var slot = $"keychain slot {charm.Slot}";
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} id", BitConverter.UInt32BitsToSingle(charm.CharmId));
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset x", charm.OffsetX);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset y", charm.OffsetY);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset z", charm.OffsetZ);
            if (charm.Seed is { } seed)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} seed", BitConverter.UInt32BitsToSingle(seed));
            if (charm.Highlight is { } highlight)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} highlight", BitConverter.UInt32BitsToSingle(highlight));
            if (charm.StickerId is { } stickerId)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} sticker", BitConverter.UInt32BitsToSingle(stickerId));
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: charm attribute write failed slot={charm.Slot}: {ex.Message}");
            return false;
        }
    }
}
