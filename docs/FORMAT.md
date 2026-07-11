# `.dtr` Format Contract

`.dtr` is the native replay file consumed by DemoTracer's CounterStrikeSharp
loader and BotController runtime.

All values are little-endian. The format is lossless for stored replay evidence:
movement snapshots, projectile events, high-fidelity metadata, subtick records,
and command-frame data retain their original `f32`, integer, or UTF-8 JSON
values.

## Version Gates

- Magic: `CSDTRREC`
- Current writer format: `.dtr` v7
- Runtime reader support: v3 through v7
- Current manifest ABI: 17
- Current BotController native ABI: 16
- Current DemoTracer companion API: 6

Compatibility notes:

- v3 files do not contain projectile metadata.
- v3/v4 files use `play_start_tick_index = 0`.
- v3-v5 files do not contain high-fidelity metadata JSON.
- v7 files require the matching server bundle with BotController native ABI 16
  and extended replay capability.

## Header

| Field | Type | Notes |
| --- | --- | --- |
| magic | 8 bytes | `CSDTRREC` |
| version | `u32` | Current writer emits `7` |
| tick_rate | `f32` | Demo tickrate estimate |
| round | `u32` | `total_rounds_played` window |
| side | `u8` | `2=T`, `3=CT`, `0=unknown` |
| flags | `u32` | Reserved |
| steam_id | `u64` | Player SteamID64 |
| tick_count | `u32` | Number of replay ticks |
| subtick_count | `u32` | Number of subtick moves |
| projectile_count | `u32` | Number of replay projectile events |
| play_start_tick_index | `u32` | First tick simulated at playback start; v5+ |
| metadata_json_len | `u32` | Byte length of high-fidelity metadata JSON; v6+ |
| map | `u16 len + utf8` | Map name |
| player_name | `u16 len + utf8` | Demo player name |
| section_count | `u32` | v7 only; number of section records |

For v3-v6 legacy files, the header continues after `player_name` with
`codec: u8`, `body_uncompressed_len: u64`, `body_compressed_len: u64`, followed
by one Brotli-compressed legacy body.

Round replay v5+ files may store up to 10 seconds of same-round freeze-time
context before `play_start_tick_index`. Playback still begins at
`round_freeze_end`; the pre-start context preserves held grenade button state
without replaying arbitrarily long paused freeze time.

## v7 Sections

Each v7 section is:

| Field | Type | Notes |
| --- | --- | --- |
| section_id | `u32` | Known IDs listed below |
| section_version | `u32` | `1` for current layouts |
| codec | `u8` | `0 = none`; readers may also accept `1 = Brotli` |
| pad | 3 bytes | Ignored by readers |
| flags | `u32` | Reserved |
| element_count | `u32` | Logical item count |
| uncompressed_len | `u64` | Expected decoded payload byte length |
| compressed_len | `u64` | Stored payload byte length |
| payload | bytes | Raw or compressed section payload |

Required sections:

| ID | Section | Count | Bytes Each |
| ---: | --- | ---: | ---: |
| 1 | `MovementSnapshotV3` chain | `0 if tick_count == 0, else tick_count + 1` | 92 |
| 2 | tick metadata | `tick_count` | 8 |
| 5 | `SubtickMoveV3` | `subtick_count` | 28 |

Optional sections:

| ID | Section | Count | Bytes Each |
| ---: | --- | ---: | ---: |
| 3 | `ProjectileEventV4` | `projectile_count` | 48 |
| 4 | `HighFidelityMetadataV6` | `0 or 1` | UTF-8 JSON |
| 6 | `CommandFrameV1` | `tick_count` | 68 |
| 7 | `MovementExtraV1` | `tick_count` | 48 |

Unknown section IDs must be skipped using `compressed_len`. Duplicate known
sections are invalid. Missing required sections are invalid. Optional
tick-aligned sections may be omitted; when present, their `element_count` must
equal `tick_count`.

## Legacy v3-v6 Body

After legacy body decompression, the layout is:

| Part | Count | Bytes Each |
| --- | ---: | ---: |
| `MovementSnapshotV3` | `0 if tick_count == 0, else tick_count + 1` | 92 |
| tick metadata | `tick_count` | 8 |
| `ProjectileEventV4` | `projectile_count` | 48 |
| `HighFidelityMetadataV6` | `metadata_json_len` | UTF-8 JSON |
| `SubtickMoveV3` | `subtick_count` | 28 |

Tick metadata is:

| Field | Type |
| --- | --- |
| weapon_def_index | `i32` |
| num_subtick | `u32` |

Reconstruct replay ticks as:

- `tick[i].pre = snapshots[i]`
- `tick[i].post = snapshots[i + 1]`
- `tick[i].weapon_def_index = metadata[i].weapon_def_index`
- `tick[i].num_subtick = metadata[i].num_subtick`

The sum of all `num_subtick` values must equal header `subtick_count`.

## Structs

### `MovementSnapshotV3`

This layout is 92 bytes with `Pack=4`.

| Field | Type |
| --- | --- |
| origin | `f32[3]` |
| velocity | `f32[3]` |
| angles | `f32[3]` pitch/yaw/roll |
| entity_flags | `u32` |
| move_type | `u8` |
| pad | 3 bytes |
| buttons | `u64` |
| buttons1 | `u64` |
| buttons2 | `u64` |
| duck_amount | `f32` |
| duck_speed | `f32` |
| ladder_normal | `f32[3]` |
| ducked | `u8` |
| ducking | `u8` |
| desires_duck | `u8` |
| actual_move_type | `u8` |

### `SubtickMoveV3`

| Field | Type |
| --- | --- |
| when | `f32` |
| button | `u32` |
| pressed | `f32` |
| analog_forward | `f32` |
| analog_left | `f32` |
| pitch_delta | `f32` |
| yaw_delta | `f32` |

### `ProjectileEventV4`

| Field | Type | Notes |
| --- | --- | --- |
| tick_index | `u32` | |
| weapon_def_index | `i32` | |
| kind | `u8` | `0=unknown`, `1=smoke`, `2=flash`, `3=he`, `4=molotov/incendiary`, `5=decoy` |
| pad | 3 bytes | |
| initial_position | `f32[3]` | |
| initial_velocity | `f32[3]` | |
| detonation_position | `f32[3]` | |

### `CommandFrameV1`

| Field | Type | Notes |
| --- | --- | --- |
| forward_move | `f32` | Present when bit `0` is set |
| left_move | `f32` | Present when bit `1` is set |
| up_move | `f32` | Present when bit `2` is set |
| view_angles | `f32[3]` | pitch/yaw/roll; present when bit `3` is set |
| buttons | `u64[3]` | buttonstate0/1/2; present when bit `4` is set |
| mouse_dx | `i32` | Present with mouse bit `5` |
| mouse_dy | `i32` | Present with mouse bit `5` |
| weapon_select | `i32` | Raw demo command value; present when bit `6` is set |
| fields | `u32` | Presence bitset |
| left_hand_desired | `u8` | Present when bit `7` is set |
| pad | 3 bytes | |

### `MovementExtraV1`

| Field | Type |
| --- | --- |
| fields | `u32` |
| jump_pressed_time | `f32` |
| last_duck_time | `f32` |
| last_actual_jump_press_tick | `i32` |
| last_actual_jump_press_frac | `f32` |
| last_usable_jump_press_tick | `i32` |
| last_usable_jump_press_frac | `f32` |
| last_landed_tick | `i32` |
| last_landed_frac | `f32` |
| last_landed_velocity | `f32[3]` |

## High-Fidelity Metadata

v6+ files may include a UTF-8 JSON blob. In v3-v6 legacy files it appears after
projectile events and before subtick moves inside the Brotli body. In v7 it is
section ID `4`.

The top-level object contains:

- `schema_version`: current metadata schema is `3`.
- `events`: player-scoped high-fidelity events.
- `inventory_snapshots`: inventory state after inventory changes.
- `projectiles`: player-scoped projectile effect metadata. This supplements
  the fixed-size `ProjectileEventV4` section without changing its binary
  layout.

Event `kind` values include `bomb_initial_owner`, `item_drop`, `item_pickup`,
`item_transfer`, `bomb_drop`, `bomb_pickup`, `bomb_beginplant`, `bomb_planted`,
`weapon_fire`, `player_hurt`, `player_death`, `round_start`, and
`round_freeze_end`.

Combat events are record-only for now: the CSS plugin loads them for diagnostics
and future behavior, but does not force damage or death.

Projectile metadata entries contain:

| Field | Type | Notes |
| --- | --- | --- |
| tick_index | `u32` | Replay tick index of the throw event |
| tick | `i32` | Original demo tick of the throw event |
| kind | string | `smoke`, `flash`, `he`, `molotov`, `decoy`, or `unknown` |
| weapon_def_index | `i32` | Demo weapon definition index when known |
| effect_tick_index | `u32?` | Replay tick index of the matched effect event |
| effect_tick | `i32?` | Original demo tick of the matched effect event |
| effect_position | `f32[3]` | Demo effect position, such as inferno start burn |
| effect_source | string | Source event/property used for the effect position |
| effect_confidence | `f32` | Converter confidence in the effect match |

## Parser Checklist

1. Read and validate magic `CSDTRREC`.
2. Require `version == 7` for current writer output, or accept `version == 3`
   through `6` for backward compatibility.
3. Read `tick_count`, `subtick_count`, `projectile_count`,
   `play_start_tick_index`, `metadata_json_len`, `map`, and `player_name`. For
   v3, treat `projectile_count` as `0`; for v3/v4, treat
   `play_start_tick_index` as `0`; for v3-v5, treat `metadata_json_len` as `0`.
4. For v7, read `section_count`, parse known sections, and skip unknown
   sections using `compressed_len`.
5. For v7, require snapshot, tick metadata, and subtick sections; require
   projectile/high-fidelity sections when their header counts are non-zero.
6. For v3-v6, require legacy `codec == 1`, verify legacy body length, then
   Brotli-decompress exactly `body_compressed_len` bytes.
7. Rebuild ticks from the snapshot chain and metadata.
8. Sum all tick `num_subtick` values and verify it equals `subtick_count`.
9. If `metadata_json_len > 0`, parse exactly that many bytes as UTF-8 JSON.
10. For non-empty replays, require `play_start_tick_index < tick_count`.
