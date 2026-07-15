param(
    [Parameter(Mandatory = $true)]
    [string] $EconIndexRoot,

    [string] $OutputPath,
    [string] $LegacySkinsPath,
    [string] $ExistingIndexPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "shared\econ\demotracer-econ-index.v1.json"
}
if ([string]::IsNullOrWhiteSpace($ExistingIndexPath)) {
    $ExistingIndexPath = $OutputPath
}

function Read-Json([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "missing JSON input: $Path"
    }
    Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Normalize-WeaponDef([int] $Def) {
    if ($Def -eq 41 -or $Def -eq 42 -or $Def -eq 59 -or ($Def -ge 500 -and $Def -lt 600)) {
        return 42
    }
    return $Def
}

function New-PaintPair(
    [int] $WeaponDefIndex,
    [uint32] $PaintKit,
    [Nullable[int]] $Rarity = $null
) {
    $pair = [ordered]@{
        weapon_defidx = $WeaponDefIndex
        paint_kit = $PaintKit
    }
    if ($null -ne $Rarity) {
        $pair.rarity = [int] $Rarity
    }
    $pair
}

function Get-EconCommit([string] $Root) {
    try {
        return (git -C $Root rev-parse HEAD).Trim()
    } catch {
        return "unknown"
    }
}

function Get-LegacyBodygroupPaints([string] $LegacyPath, [string] $ExistingPath) {
    if (-not [string]::IsNullOrWhiteSpace($LegacyPath) -and (Test-Path -LiteralPath $LegacyPath)) {
        return @(
            Read-Json $LegacyPath |
                Where-Object { $_.legacy_model -eq $true } |
                ForEach-Object {
                    [pscustomobject]@{
                        Def = [int](Normalize-WeaponDef ([int] $_.weapon_defindex))
                        Paint = [uint32] $_.paint
                    }
                } |
                Sort-Object Def, Paint -Unique |
                ForEach-Object { New-PaintPair $_.Def $_.Paint }
        )
    }

    if (Test-Path -LiteralPath $ExistingPath) {
        $existing = Read-Json $ExistingPath
        return @(
            $existing.legacy_bodygroup_paints |
                ForEach-Object {
                    New-PaintPair ([int] $_.weapon_defidx) ([uint32] $_.paint_kit)
                }
        )
    }

    throw "legacy bodygroup source is unavailable; pass -LegacySkinsPath or keep an existing compact index"
}

$econRoot = Resolve-Path $EconIndexRoot
$dataRoot = Join-Path $econRoot "data"
$summary = Read-Json (Join-Path $dataRoot "summary.json")
$weaponSkins = Read-Json (Join-Path $dataRoot "weapon-skins.json")
$paintKits = Read-Json (Join-Path $dataRoot "paint-kits.json")
$itemDefs = Read-Json (Join-Path $dataRoot "item-definitions.json")
$stickers = Read-Json (Join-Path $dataRoot "sticker-kits.json")
$keychains = Read-Json (Join-Path $dataRoot "keychains.json")
$music = Read-Json (Join-Path $dataRoot "music-definitions.json")
$flair = Read-Json (Join-Path $dataRoot "scoreboard-flair-defidx.compact.json")

$rarityCodes = @{
    default = 0
    common = 1
    uncommon = 2
    rare = 3
    mythical = 4
    legendary = 5
    ancient = 6
    immortal = 7
}

$weaponPairs = @(
    $weaponSkins.items.PSObject.Properties |
        ForEach-Object {
            $item = $_.Value
            if ($null -ne $item.weapon_defidx -and $null -ne $item.paint_kit -and [uint32] $item.paint_kit -gt 0) {
                $rarityName = [string] $item.rarity
                if (-not $rarityCodes.ContainsKey($rarityName)) {
                    throw "unknown weapon skin rarity '$rarityName' for $($_.Name)"
                }
                [pscustomobject]@{
                    Def = [int] $item.weapon_defidx
                    Paint = [uint32] $item.paint_kit
                    Rarity = [int] $rarityCodes[$rarityName]
                }
            }
        } |
        Sort-Object Def, Paint -Unique |
        ForEach-Object { New-PaintPair $_.Def $_.Paint $_.Rarity }
)

$paintKitIds = @(
    $paintKits.items.PSObject.Properties |
        ForEach-Object { [uint32] $_.Value.paint_kit } |
        Where-Object { $_ -gt 0 } |
        Sort-Object -Unique
)

$weaponDefs = @(
    $itemDefs.items.PSObject.Properties |
        ForEach-Object {
            $item = $_.Value
            $def = [int] $item.defidx
            if ($item.category -eq "weapon_or_knife" -and
                $item.schema_name -notmatch "^weapon_knife" -and
                $def -notin @(41, 42, 59) -and
                -not ($def -ge 500 -and $def -lt 600)) {
                $def
            }
        } |
        Sort-Object -Unique
)

$knifeDefs = @(
    $itemDefs.items.PSObject.Properties |
        ForEach-Object {
            $item = $_.Value
            $def = [int] $item.defidx
            if ($item.category -eq "weapon_or_knife" -and
                ($item.schema_name -match "^weapon_knife" -or
                 $def -in @(41, 42, 59) -or
                 ($def -ge 500 -and $def -lt 600))) {
                $def
            }
        } |
        Sort-Object -Unique
)

$gloveDefs = @(
    $itemDefs.items.PSObject.Properties |
        ForEach-Object {
            $item = $_.Value
            # The source index currently categorizes Hand Wraps (5032) as
            # "other", but its schema prefab still identifies it as a
            # paintable glove. Keep the category lane for stock gloves and use
            # the prefab as the authoritative fallback for painted gloves.
            if ($item.category -eq "gloves" -or $item.prefab -eq "hands_paintable") {
                [int] $item.defidx
            }
        } |
        Sort-Object -Unique
)

$agentDefs = @(
    $itemDefs.items.PSObject.Properties |
        ForEach-Object {
            $item = $_.Value
            if ($item.category -eq "agent") {
                [int] $item.defidx
            }
        } |
        Sort-Object -Unique
)

$stickerIds = @(
    $stickers.items.PSObject.Properties |
        ForEach-Object { [uint32] $_.Value.sticker_id } |
        Where-Object { $_ -gt 0 } |
        Sort-Object -Unique
)

$keychainIds = @(
    $keychains.items.PSObject.Properties |
        ForEach-Object { [uint32] $_.Value.keychain_id } |
        Where-Object { $_ -gt 0 } |
        Sort-Object -Unique
)

$musicKitIds = @(
    $music.items.PSObject.Properties |
        ForEach-Object { [uint32] $_.Value.music_kit_id } |
        Where-Object { $_ -gt 0 } |
        Sort-Object -Unique
)

$flairDefs = @(
    $flair.items.PSObject.Properties |
        ForEach-Object { [uint32] $_.Name } |
        Where-Object { $_ -gt 0 } |
        Sort-Object -Unique
)

$legacyPairs = Get-LegacyBodygroupPaints $LegacySkinsPath $ExistingIndexPath

$index = [ordered]@{
    name = "demotracer-econ-index"
    schema_version = 1
    description = "Compact CS2 econ allowlists and inspect metadata consumed by CS2 DemoTracer converter and runtime."
    source = [ordered]@{
        repository = "https://github.com/unicbm/cs2-econ-id-index"
        commit = Get-EconCommit $econRoot
        snapshot_date = $summary.snapshot_date
        cs2_patch_version = $summary.source.steam_inf.PatchVersion
        cs2_client_version = $summary.source.steam_inf.ClientVersion
        source_revision = $summary.source.steam_inf.SourceRevision
        version_date = $summary.source.steam_inf.VersionDate
        version_time = $summary.source.steam_inf.VersionTime
        legacy_bodygroup_paints_source = "pre-index DemoTracer legacy_model rows preserved in this compact index; normalized through DemoTracer knife def rules."
    }
    id_space_warning = "Do not compare IDs across arrays unless the array name/domain matches."
    weapon_paints = $weaponPairs
    legacy_bodygroup_paints = $legacyPairs
    paint_kit_ids = $paintKitIds
    weapon_defidx = $weaponDefs
    knife_defidx = $knifeDefs
    glove_defidx = $gloveDefs
    agent_defidx = $agentDefs
    sticker_ids = $stickerIds
    keychain_ids = $keychainIds
    music_kit_ids = $musicKitIds
    scoreboard_flair_defidx = $flairDefs
    counts = [ordered]@{
        weapon_paints = $weaponPairs.Count
        legacy_bodygroup_paints = $legacyPairs.Count
        paint_kit_ids = $paintKitIds.Count
        weapon_defidx = $weaponDefs.Count
        knife_defidx = $knifeDefs.Count
        glove_defidx = $gloveDefs.Count
        agent_defidx = $agentDefs.Count
        sticker_ids = $stickerIds.Count
        keychain_ids = $keychainIds.Count
        music_kit_ids = $musicKitIds.Count
        scoreboard_flair_defidx = $flairDefs.Count
        weapon_paint_rarities = $weaponPairs.Count
    }
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$index | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "wrote $OutputPath"
