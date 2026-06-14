# `.cs2rec` v1 Format

All values are little-endian.

## Header

| Field | Type | Notes |
| --- | --- | --- |
| magic | 8 bytes | `CS2BMREC` |
| version | `u32` | `1` |
| tick_rate | `f32` | Demo tickrate estimate |
| round | `u32` | `total_rounds_played` window |
| side | `u8` | `2=T`, `3=CT`, `0=unknown` |
| flags | `u32` | Reserved |
| steam_id | `u64` | Player SteamID64 |
| tick_count | `u32` | Number of replay ticks |
| subtick_count | `u32` | Number of subtick moves |
| map | `u16 len + utf8` | Map name |
| player_name | `u16 len + utf8` | Demo player name |

## ReplayTick

Each tick stores:

- `pre: MovementSnapshot`
- `post: MovementSnapshot`
- `weapon_def_index: i32`
- `num_subtick: u32`

The sum of all `num_subtick` values must equal header `subtick_count`.

## MovementSnapshot

| Field | Type |
| --- | --- |
| origin | `f32[3]` |
| velocity | `f32[3]` |
| angles | `f32[3]` pitch/yaw/roll |
| entity_flags | `u32` |
| move_type | `u8` |
| pad | 3 bytes |
| buttons | `u64` |

## SubtickMove

| Field | Type |
| --- | --- |
| when | `f32` |
| button | `u32` |
| pressed | `f32` |
| analog_forward | `f32` |
| analog_left | `f32` |
| pitch_delta | `f32` |
| yaw_delta | `f32` |

v1 converter may emit zero subticks. Runtime must accept that and replay tick snapshots.
