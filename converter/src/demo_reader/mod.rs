use crate::model::ParsedDemo;
use crate::{Error, Result};
use std::path::Path;

#[derive(Clone, Copy, Debug, Default)]
pub struct ReadDemoOptions {
    pub collect_voice: bool,
}

#[cfg(feature = "demoparser")]
mod demoparser_impl {
    use super::*;
    use crate::io_error;
    use crate::model::{
        AvatarImageFormat, ParsedAvatarOverride, ParsedEconItem, ParsedGameEvent,
        ParsedInventoryWeaponAttribute, ParsedInventoryWeaponCosmetic, ParsedPlayerTick,
        ParsedProjectile, ParsedVoiceFrame, ParsedWeaponSticker, ProjectileEffectSource,
        ProjectileKind, SubtickMove,
    };
    use ahash::AHashMap;
    use parser::first_pass::parser_settings::{rm_user_friendly_names, ParserInputs};
    use parser::parse_demo::{Parser, ParsingMode};
    use parser::second_pass::collect_data::ProjectileRecord;
    use parser::second_pass::game_events::GameEvent;
    use parser::second_pass::parser_settings::create_huffman_lookup_table;
    use parser::second_pass::variants::{PropColumn, VarVec, Variant};
    use std::collections::{BTreeMap, BTreeSet};
    use std::fs;

    pub fn read_demo(path: &Path) -> Result<ParsedDemo> {
        read_demo_with_options(path, ReadDemoOptions::default())
    }

    pub fn read_demo_with_options(path: &Path, options: ReadDemoOptions) -> Result<ParsedDemo> {
        let bytes = fs::read(path).map_err(|e| io_error(path, e))?;
        let stem = path
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("demo")
            .to_string();
        read_demo_bytes_with_options(&bytes, &stem, &path.display().to_string(), options)
    }

    pub fn read_demo_bytes(bytes: &[u8], stem: &str, display_path: &str) -> Result<ParsedDemo> {
        read_demo_bytes_with_options(bytes, stem, display_path, ReadDemoOptions::default())
    }

    fn read_demo_bytes_with_options(
        bytes: &[u8],
        stem: &str,
        display_path: &str,
        options: ReadDemoOptions,
    ) -> Result<ParsedDemo> {
        let wanted_props = vec![
            "X",
            "Y",
            "Z",
            "velocity_X",
            "velocity_Y",
            "velocity_Z",
            "pitch",
            "yaw",
            "buttons",
            "usercmd_viewangle_x",
            "usercmd_viewangle_y",
            "usercmd_viewangle_z",
            "usercmd_buttonstate_1",
            "usercmd_buttonstate_2",
            "usercmd_buttonstate_3",
            "usercmd_forward_move",
            "usercmd_left_move",
            "usercmd_up_move",
            "usercmd_mouse_dx",
            "usercmd_mouse_dy",
            "usercmd_weapon_select",
            "usercmd_left_hand_desired",
            "item_def_idx",
            "inventory_as_ids",
            "inventory_weapon_cosmetics",
            "music_kit_id",
            "agent_skin",
            "CCSPlayerController.m_nPawnCharacterDefIndex",
            "active_weapon_original_owner",
            "item_id_high",
            "item_id_low",
            "item_account_id",
            "weapon_skin_id",
            "weapon_paint_seed",
            "weapon_float",
            "custom_name",
            "weapon_stickers",
            "glove_item_idx",
            "glove_paint_id",
            "glove_paint_seed",
            "glove_paint_float",
            "crosshair_code",
            "score",
            "mvps",
            "kills_total",
            "deaths_total",
            "assists_total",
            "user_id",
            "entity_id",
            "player_color",
            "armor_value",
            "has_helmet",
            "has_defuser",
            "round_start_equip_value",
            "equipment_value_total",
            "money_saved_total",
            "cash_spent_this_round",
            "team_num",
            "is_alive",
            "is_airborne",
            "move_type",
            "CCSPlayerPawn.m_fFlags",
            "duck_amount",
            "duck_speed",
            "ducked",
            "ducking",
            "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
            "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal",
            "round_in_progress",
            "is_freeze_period",
            "total_rounds_played",
            "game_time",
            "usercmd_subtick_moves",
        ]
        .into_iter()
        .map(str::to_string)
        .collect::<Vec<_>>();

        let real_props =
            rm_user_friendly_names(&wanted_props).map_err(|e| Error::Parser(format!("{e:?}")))?;
        let wanted_other_props = vec!["team_rounds_total", "team_name", "team_clan_name"]
            .into_iter()
            .map(str::to_string)
            .collect::<Vec<_>>();
        let real_other_props = rm_user_friendly_names(&wanted_other_props)
            .map_err(|e| Error::Parser(format!("{e:?}")))?;
        let mut real_name_to_og_name = AHashMap::default();
        for (real, friendly) in real_props.iter().zip(&wanted_props) {
            real_name_to_og_name.insert(real.clone(), friendly.clone());
        }
        for (real, friendly) in real_other_props.iter().zip(&wanted_other_props) {
            real_name_to_og_name.insert(real.clone(), friendly.clone());
        }

        let demo_sha256 = crate::demo_id::sha256_hex(bytes);
        let huf = create_huffman_lookup_table();
        let settings = ParserInputs {
            real_name_to_og_name,
            wanted_players: vec![],
            wanted_player_props: real_props,
            wanted_other_props: real_other_props,
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: vec![],
            wanted_events: vec![
                "round_freeze_end".to_string(),
                "round_end".to_string(),
                "round_start".to_string(),
                "bomb_beginplant".to_string(),
                "bomb_planted".to_string(),
                "bomb_dropped".to_string(),
                "bomb_pickup".to_string(),
                "item_pickup".to_string(),
                "weapon_fire".to_string(),
                "player_hurt".to_string(),
                "player_death".to_string(),
                "smokegrenade_detonate".to_string(),
                "flashbang_detonate".to_string(),
                "hegrenade_detonate".to_string(),
                "molotov_detonate".to_string(),
                "inferno_startburn".to_string(),
                "decoy_started".to_string(),
                "decoy_detonate".to_string(),
            ],
            parse_ents: true,
            parse_projectiles: false,
            collect_projectile_records: true,
            parse_grenades: false,
            only_header: false,
            only_convars: false,
            huffman_lookup_table: &huf,
            order_by_steamid: false,
            list_props: false,
            fallback_bytes: None,
        };
        let mut parser = Parser::new(settings, ParsingMode::Normal);
        let output = parser
            .parse_demo(bytes)
            .map_err(|e| Error::Parser(format!("{e:?}")))?;

        let header = output.header.unwrap_or_default();
        let map = header
            .get("map_name")
            .cloned()
            .unwrap_or_else(|| "unknown".to_string());

        let mut columns: AHashMap<String, &PropColumn> = AHashMap::default();
        for info in &output.prop_controller.prop_infos {
            if let Some(column) = output.df.get(&info.id) {
                columns.insert(info.prop_friendly_name.clone(), column);
            }
        }

        let len = columns.values().map(|c| c.len()).max().unwrap_or_default();
        let mut rows = Vec::with_capacity(len);
        for idx in 0..len {
            let steam_id = get_u64(&columns, "steamid", idx).unwrap_or_default();
            if steam_id == 0 {
                continue;
            }
            let tick = get_i32(&columns, "tick", idx).unwrap_or_default();
            let round = get_u32(&columns, "total_rounds_played", idx).unwrap_or_default();
            let team_num = get_u32(&columns, "team_num", idx).unwrap_or_default() as u8;
            let is_airborne = get_bool(&columns, "is_airborne", idx).unwrap_or(false);
            let explicit_flags = get_u32(&columns, "CCSPlayerPawn.m_fFlags", idx);
            let (subtick_moves, subtick_button_truncated) =
                get_subtick_moves(&columns, "usercmd_subtick_moves", idx).unwrap_or_default();
            rows.push(ParsedPlayerTick {
                tick,
                steam_id,
                name: get_string(&columns, "name", idx).unwrap_or_default(),
                team_num,
                is_alive: get_bool(&columns, "is_alive", idx).unwrap_or(false),
                round,
                round_in_progress: get_bool(&columns, "round_in_progress", idx).unwrap_or(false),
                is_freeze_period: get_bool(&columns, "is_freeze_period", idx).unwrap_or(false),
                game_time: get_f32(&columns, "game_time", idx),
                origin: [
                    get_f32(&columns, "X", idx).unwrap_or_default(),
                    get_f32(&columns, "Y", idx).unwrap_or_default(),
                    get_f32(&columns, "Z", idx).unwrap_or_default(),
                ],
                velocity: [
                    get_f32(&columns, "velocity_X", idx).unwrap_or_default(),
                    get_f32(&columns, "velocity_Y", idx).unwrap_or_default(),
                    get_f32(&columns, "velocity_Z", idx).unwrap_or_default(),
                ],
                pitch: get_f32(&columns, "pitch", idx).unwrap_or_default(),
                yaw: get_f32(&columns, "yaw", idx).unwrap_or_default(),
                buttons: get_u64(&columns, "buttons", idx).unwrap_or_default(),
                buttonstate1: get_u64(&columns, "usercmd_buttonstate_1", idx).unwrap_or_default(),
                buttonstate2: get_u64(&columns, "usercmd_buttonstate_2", idx).unwrap_or_default(),
                buttonstate3: get_u64(&columns, "usercmd_buttonstate_3", idx).unwrap_or_default(),
                usercmd_forward_move: get_f32(&columns, "usercmd_forward_move", idx),
                usercmd_left_move: get_f32(&columns, "usercmd_left_move", idx),
                usercmd_up_move: get_f32(&columns, "usercmd_up_move", idx),
                usercmd_pitch: get_f32(&columns, "usercmd_viewangle_x", idx),
                usercmd_yaw: get_f32(&columns, "usercmd_viewangle_y", idx),
                usercmd_roll: get_f32(&columns, "usercmd_viewangle_z", idx),
                usercmd_mouse_dx: get_i32(&columns, "usercmd_mouse_dx", idx),
                usercmd_mouse_dy: get_i32(&columns, "usercmd_mouse_dy", idx),
                usercmd_weapon_select: get_i32(&columns, "usercmd_weapon_select", idx),
                usercmd_left_hand_desired: get_bool(&columns, "usercmd_left_hand_desired", idx),
                item_def_idx: get_i32(&columns, "item_def_idx", idx)
                    .or_else(|| get_u32(&columns, "item_def_idx", idx).map(|v| v as i32))
                    .unwrap_or(-1),
                inventory_as_ids: get_u32_vec(&columns, "inventory_as_ids", idx)
                    .unwrap_or_default()
                    .into_iter()
                    .map(|v| v as i32)
                    .collect(),
                inventory_weapon_cosmetics: get_inventory_weapon_cosmetics(
                    &columns,
                    "inventory_weapon_cosmetics",
                    idx,
                )
                .unwrap_or_default(),
                music_kit_id: get_u32(&columns, "music_kit_id", idx).filter(|value| *value != 0),
                agent_item_def_index: get_u32(
                    &columns,
                    "CCSPlayerController.m_nPawnCharacterDefIndex",
                    idx,
                )
                .filter(|value| *value != 0),
                agent_skin: get_string(&columns, "agent_skin", idx).and_then(normalize_agent_skin),
                active_weapon_paint_kit: get_u32(&columns, "weapon_skin_id", idx),
                active_weapon_paint_seed: get_u32(&columns, "weapon_paint_seed", idx),
                active_weapon_paint_wear: get_f32(&columns, "weapon_float", idx),
                active_weapon_original_owner_steam_id: get_string(
                    &columns,
                    "active_weapon_original_owner",
                    idx,
                )
                .and_then(|value| value.parse::<u64>().ok())
                .filter(|value| *value != 0),
                active_weapon_item_account_id: get_u32(&columns, "item_account_id", idx)
                    .filter(|value| *value != 0),
                active_weapon_item_id: combine_item_id(
                    get_u32(&columns, "item_id_high", idx),
                    get_u32(&columns, "item_id_low", idx),
                )
                .filter(|value| *value != 0),
                active_weapon_custom_name: get_string(&columns, "custom_name", idx)
                    .and_then(normalize_custom_name),
                active_weapon_stickers: get_weapon_stickers(&columns, "weapon_stickers", idx)
                    .unwrap_or_default(),
                glove_item_def_index: get_i32(&columns, "glove_item_idx", idx),
                glove_paint_kit: get_u32(&columns, "glove_paint_id", idx),
                glove_paint_seed: get_u32(&columns, "glove_paint_seed", idx),
                glove_paint_wear: get_f32(&columns, "glove_paint_float", idx),
                crosshair_code: get_string(&columns, "crosshair_code", idx)
                    .and_then(normalize_crosshair_code),
                scoreboard_score: get_i32(&columns, "score", idx),
                scoreboard_mvps: get_u32(&columns, "mvps", idx),
                scoreboard_kills: get_u32(&columns, "kills_total", idx),
                scoreboard_deaths: get_u32(&columns, "deaths_total", idx),
                scoreboard_assists: get_u32(&columns, "assists_total", idx),
                armor_value: get_u32(&columns, "armor_value", idx).unwrap_or_default(),
                has_helmet: get_bool(&columns, "has_helmet", idx).unwrap_or(false),
                has_defuser: get_bool(&columns, "has_defuser", idx).unwrap_or(false),
                round_start_equip_value: get_u32(&columns, "round_start_equip_value", idx)
                    .unwrap_or_default(),
                equipment_value_total: get_u32(&columns, "equipment_value_total", idx)
                    .unwrap_or_default(),
                money_saved_total: get_u32(&columns, "money_saved_total", idx).unwrap_or_default(),
                cash_spent_this_round: get_u32(&columns, "cash_spent_this_round", idx)
                    .unwrap_or_default(),
                entity_flags: explicit_flags.unwrap_or(if is_airborne { 0 } else { 1 }),
                move_type: get_u32(&columns, "move_type", idx).unwrap_or(2) as u8,
                duck_amount: get_f32(&columns, "duck_amount", idx),
                duck_speed: get_f32(&columns, "duck_speed", idx),
                ladder_normal: get_vec3(
                    &columns,
                    "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal",
                    idx,
                ),
                ducked: get_bool(&columns, "ducked", idx),
                ducking: get_bool(&columns, "ducking", idx),
                desires_duck: get_bool(
                    &columns,
                    "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
                    idx,
                ),
                subtick_moves,
                subtick_button_truncated,
                player_user_id: get_i32(&columns, "user_id", idx),
                player_entity_id: get_i32(&columns, "entity_id", idx),
                player_color: get_string(&columns, "player_color", idx),
                team_rounds_total: get_u32(&columns, "team_rounds_total", idx),
                team_name: get_string(&columns, "team_name", idx),
                team_clan_name: get_string(&columns, "team_clan_name", idx),
            });
        }
        rows.sort_by_key(|row| (row.round, row.tick, row.steam_id));

        let tick_rate = estimate_tick_rate(&rows).unwrap_or(64.0);
        let mut round_freeze_end_ticks = output
            .game_events
            .iter()
            .filter(|event| event.name == "round_freeze_end")
            .map(|event| event.tick)
            .collect::<Vec<_>>();
        round_freeze_end_ticks.sort_unstable();
        round_freeze_end_ticks.dedup();
        let mut round_end_ticks = output
            .game_events
            .iter()
            .filter(|event| event.name == "round_end")
            .map(|event| event.tick)
            .collect::<Vec<_>>();
        round_end_ticks.sort_unstable();
        round_end_ticks.dedup();
        repair_round_phase_after_events(&mut rows, &round_freeze_end_ticks, &round_end_ticks);
        let mut bomb_beginplant_ticks = event_ticks(&output.game_events, "bomb_beginplant");
        let mut bomb_planted_ticks = event_ticks(&output.game_events, "bomb_planted");
        bomb_beginplant_ticks.sort_unstable();
        bomb_beginplant_ticks.dedup();
        bomb_planted_ticks.sort_unstable();
        bomb_planted_ticks.dedup();
        let events = parse_game_events(&output.game_events);
        let projectiles =
            parse_projectile_records(&output.projectiles, tick_rate, &output.game_events);
        let mut voice_frames = Vec::new();
        if options.collect_voice {
            for (tick, msg) in &output.voice_data {
                let Some(audio) = msg.audio.as_ref() else {
                    continue;
                };
                let Some(data) = audio.voice_data.as_ref() else {
                    continue;
                };
                if data.is_empty() {
                    continue;
                }
                let xuid = msg.xuid.unwrap_or_default();
                if xuid == 0 {
                    continue;
                }
                voice_frames.push(ParsedVoiceFrame {
                    tick: *tick,
                    xuid,
                    client: msg.client.unwrap_or(-1),
                    format: audio.format.unwrap_or_default(),
                    sample_rate: audio.sample_rate,
                    voice_level: audio.voice_level,
                    sequence_bytes: audio.sequence_bytes,
                    section_number: audio.section_number,
                    uncompressed_sample_offset: audio.uncompressed_sample_offset,
                    num_packets: audio.num_packets,
                    packet_offsets: audio.packet_offsets.clone(),
                    audio: data.to_vec(),
                });
            }
            voice_frames.sort_by_key(|frame| (frame.xuid, frame.tick));
        }
        let avatar_overrides = parse_avatar_overrides(output.server_avatar_overrides);
        let econ_items = parse_econ_items(output.skins);

        Ok(ParsedDemo {
            path: display_path.to_string(),
            stem: stem.to_string(),
            demo_sha256,
            map,
            tick_rate,
            round_freeze_end_ticks,
            bomb_beginplant_ticks,
            bomb_planted_ticks,
            rows,
            projectiles,
            voice_frames,
            events,
            avatar_overrides,
            econ_items,
        })
    }

    fn parse_avatar_overrides(raw: Vec<(String, Vec<u8>)>) -> Vec<ParsedAvatarOverride> {
        let mut by_steam_id: BTreeMap<u64, BTreeMap<String, (AvatarImageFormat, Vec<u8>)>> =
            BTreeMap::new();
        for (key, bytes) in raw {
            if bytes.is_empty() {
                continue;
            }
            let Ok(steam_id) = key.trim().parse::<u64>() else {
                continue;
            };
            let sha256 = crate::demo_id::sha256_hex(&bytes);
            by_steam_id
                .entry(steam_id)
                .or_default()
                .entry(sha256)
                .or_insert_with(|| (detect_avatar_image_format(&bytes), bytes));
        }

        by_steam_id
            .into_iter()
            .filter_map(|(steam_id, mut variants)| {
                if variants.len() != 1 {
                    return None;
                }
                let (sha256, (format, bytes)) = variants.pop_first()?;
                Some(ParsedAvatarOverride {
                    steam_id,
                    format,
                    sha256,
                    source: "ServerAvatarOverrides".to_string(),
                    bytes,
                })
            })
            .collect()
    }

    fn parse_econ_items(
        items: Vec<parser::second_pass::parser_settings::EconItem>,
    ) -> Vec<ParsedEconItem> {
        items
            .into_iter()
            .map(|item| ParsedEconItem {
                steam_id: item.steamid,
                item_def_index: item.def_index,
                paint_kit: item.paint_index,
                paint_seed: item.paint_seed,
                paint_wear_raw: item.paint_wear,
                paint_wear: item.paint_wear.map(f32::from_bits),
                item_name: item.item_name,
                skin_name: item.skin_name,
            })
            .collect()
    }

    fn detect_avatar_image_format(bytes: &[u8]) -> AvatarImageFormat {
        if bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
            AvatarImageFormat::Png
        } else if bytes.starts_with(&[0xff, 0xd8, 0xff]) {
            AvatarImageFormat::Jpeg
        } else {
            AvatarImageFormat::Binary
        }
    }

    pub fn read_demo_header_map_bytes(bytes: &[u8]) -> Result<Option<String>> {
        let huf = create_huffman_lookup_table();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(),
            wanted_players: Vec::new(),
            wanted_player_props: Vec::new(),
            wanted_other_props: Vec::new(),
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: Vec::new(),
            wanted_events: Vec::new(),
            parse_ents: false,
            parse_projectiles: false,
            collect_projectile_records: false,
            parse_grenades: false,
            only_header: true,
            only_convars: false,
            huffman_lookup_table: &huf,
            order_by_steamid: false,
            list_props: false,
            fallback_bytes: None,
        };
        let mut parser = Parser::new(settings, ParsingMode::ForceSingleThreaded);
        let output = parser
            .parse_demo(bytes)
            .map_err(|e| Error::Parser(format!("{e:?}")))?;
        let header = output.header.unwrap_or_default();
        Ok(header.get("map_name").cloned())
    }

    fn parse_projectile_records(
        records: &[ProjectileRecord],
        tick_rate: f32,
        game_events: &[GameEvent],
    ) -> Vec<ParsedProjectile> {
        let mut grouped: AHashMap<ProjectileKey, ProjectileCandidate> = AHashMap::default();
        for record in records {
            let steam_id = record.steamid.unwrap_or_default();
            if steam_id == 0 {
                continue;
            }
            let tick = record.tick.unwrap_or_default();
            let grenade_type = record.grenade_type.clone().unwrap_or_default();
            let initial_position = match record.initial_position {
                Some(value) if vec3_is_meaningful(value) => value,
                _ => continue,
            };
            let initial_velocity = match record.initial_velocity {
                Some(value) if vec3_is_meaningful(value) => value,
                _ => continue,
            };
            let detonation_position = record.smoke_detonation_position.unwrap_or_default();
            let entity_id = record.entity_id.unwrap_or_default();
            let key =
                ProjectileKey::new(steam_id, &grenade_type, initial_position, initial_velocity);
            let entry = grouped.entry(key).or_insert_with(|| ProjectileCandidate {
                entity_id,
                projectile: ParsedProjectile {
                    tick,
                    steam_id,
                    name: record.name.clone().unwrap_or_default(),
                    kind: ProjectileKind::from_grenade_type(&grenade_type),
                    weapon_def_index: ProjectileKind::weapon_def_index_from_grenade_type(
                        &grenade_type,
                    ),
                    grenade_type,
                    initial_position,
                    initial_velocity,
                    detonation_position: [0.0, 0.0, 0.0],
                    effect_position: [0.0, 0.0, 0.0],
                    effect_tick: None,
                    effect_source: ProjectileEffectSource::Unknown,
                    effect_confidence: 0.0,
                },
            });
            if tick < entry.projectile.tick {
                entry.projectile.tick = tick;
                entry.entity_id = entity_id;
            }
            if vec3_is_meaningful(detonation_position) {
                entry.projectile.detonation_position = detonation_position;
            }
        }

        let mut candidates = grouped.into_values().collect::<Vec<_>>();
        candidates
            .sort_by_key(|candidate| (candidate.projectile.tick, candidate.projectile.steam_id));
        attach_projectile_effects(&mut candidates, game_events, tick_rate);
        let mut projectiles = candidates
            .into_iter()
            .map(|candidate| candidate.projectile)
            .collect::<Vec<_>>();
        projectiles.sort_by_key(|projectile| (projectile.tick, projectile.steam_id));
        projectiles
    }

    #[derive(Clone, Debug)]
    struct ProjectileCandidate {
        projectile: ParsedProjectile,
        entity_id: i32,
    }

    #[derive(Clone, Debug)]
    struct ProjectileEffectEvent {
        tick: i32,
        entity_id: Option<i32>,
        steam_id: Option<u64>,
        kind: ProjectileKind,
        position: [f32; 3],
        source: ProjectileEffectSource,
    }

    fn attach_projectile_effects(
        candidates: &mut [ProjectileCandidate],
        game_events: &[GameEvent],
        tick_rate: f32,
    ) {
        let max_delta_ticks = seconds_to_ticks(20.0, tick_rate).max(1);
        let events = game_events
            .iter()
            .filter_map(effect_event_from_game_event)
            .collect::<Vec<_>>();
        let mut used = vec![false; events.len()];

        for candidate in candidates {
            if vec3_is_meaningful(candidate.projectile.detonation_position) {
                candidate.projectile.effect_position = candidate.projectile.detonation_position;
                candidate.projectile.effect_source = ProjectileEffectSource::SmokeDetonationProp;
                candidate.projectile.effect_confidence = 0.90;
            }

            let best = events
                .iter()
                .enumerate()
                .filter(|(idx, event)| {
                    !used[*idx] && effect_can_match(candidate, event, max_delta_ticks)
                })
                .min_by_key(|(_, event)| effect_match_score(candidate, event));

            if let Some((idx, event)) = best {
                used[idx] = true;
                candidate.projectile.effect_position = event.position;
                candidate.projectile.effect_tick = Some(event.tick);
                candidate.projectile.effect_source = event.source;
                candidate.projectile.effect_confidence = effect_confidence(candidate, event);
                candidate.projectile.detonation_position = event.position;
            }
        }
    }

    fn effect_event_from_game_event(event: &GameEvent) -> Option<ProjectileEffectEvent> {
        let (kind, source) = match event.name.as_str() {
            "smokegrenade_detonate" => (
                ProjectileKind::Smoke,
                ProjectileEffectSource::SmokeDetonationEvent,
            ),
            "flashbang_detonate" => (
                ProjectileKind::Flash,
                ProjectileEffectSource::FlashDetonationEvent,
            ),
            "hegrenade_detonate" => (
                ProjectileKind::He,
                ProjectileEffectSource::HeDetonationEvent,
            ),
            "molotov_detonate" => (
                ProjectileKind::Molotov,
                ProjectileEffectSource::MolotovDetonationEvent,
            ),
            "inferno_startburn" => (
                ProjectileKind::Molotov,
                ProjectileEffectSource::InfernoStartBurnEvent,
            ),
            "decoy_started" => (
                ProjectileKind::Decoy,
                ProjectileEffectSource::DecoyStartedEvent,
            ),
            "decoy_detonate" => (
                ProjectileKind::Decoy,
                ProjectileEffectSource::DecoyDetonationEvent,
            ),
            _ => return None,
        };
        let position = event_vec3(event)?;
        if !vec3_is_meaningful(position) {
            return None;
        }
        Some(ProjectileEffectEvent {
            tick: event.tick,
            entity_id: event_field_i32(event, "entityid"),
            steam_id: event_steam_id(event),
            kind,
            position,
            source,
        })
    }

    fn effect_can_match(
        candidate: &ProjectileCandidate,
        event: &ProjectileEffectEvent,
        max_delta_ticks: i32,
    ) -> bool {
        if candidate.projectile.kind != event.kind {
            return false;
        }
        if let Some(steam_id) = event.steam_id {
            if steam_id != candidate.projectile.steam_id {
                return false;
            }
        }
        let delta = event.tick - candidate.projectile.tick;
        delta >= -2 && delta <= max_delta_ticks
    }

    fn effect_match_score(
        candidate: &ProjectileCandidate,
        event: &ProjectileEffectEvent,
    ) -> (u8, u8, i32) {
        let entity_rank = if event_source_uses_projectile_entity(event.source)
            && candidate.entity_id > 0
            && event.entity_id == Some(candidate.entity_id)
        {
            0
        } else {
            1
        };
        let steam_rank = if event.steam_id == Some(candidate.projectile.steam_id) {
            0
        } else {
            1
        };
        (
            entity_rank,
            steam_rank,
            event.tick - candidate.projectile.tick,
        )
    }

    fn effect_confidence(candidate: &ProjectileCandidate, event: &ProjectileEffectEvent) -> f32 {
        if event_source_uses_projectile_entity(event.source)
            && candidate.entity_id > 0
            && event.entity_id == Some(candidate.entity_id)
        {
            1.0
        } else if event.steam_id == Some(candidate.projectile.steam_id) {
            0.85
        } else {
            0.65
        }
    }

    fn event_source_uses_projectile_entity(source: ProjectileEffectSource) -> bool {
        !matches!(source, ProjectileEffectSource::InfernoStartBurnEvent)
    }

    fn event_vec3(event: &GameEvent) -> Option<[f32; 3]> {
        let x = event_field_f32(event, "x")
            .or_else(|| event_field_f32(event, "pos_x"))
            .or_else(|| event_field_f32(event, "origin_x"))?;
        let y = event_field_f32(event, "y")
            .or_else(|| event_field_f32(event, "pos_y"))
            .or_else(|| event_field_f32(event, "origin_y"))?;
        let z = event_field_f32(event, "z")
            .or_else(|| event_field_f32(event, "pos_z"))
            .or_else(|| event_field_f32(event, "origin_z"))?;
        Some([x, y, z])
    }

    fn event_steam_id(event: &GameEvent) -> Option<u64> {
        [
            "user_steamid",
            "steamid",
            "thrower_steamid",
            "attacker_steamid",
        ]
        .iter()
        .find_map(|name| event_field_u64(event, name))
    }

    fn event_field_f32(event: &GameEvent, name: &str) -> Option<f32> {
        match event_field(event, name)? {
            Variant::F32(value) => Some(*value),
            Variant::I32(value) => Some(*value as f32),
            Variant::U32(value) => Some(*value as f32),
            Variant::String(value) => value.parse::<f32>().ok(),
            _ => None,
        }
    }

    fn event_field_i32(event: &GameEvent, name: &str) -> Option<i32> {
        match event_field(event, name)? {
            Variant::I32(value) => Some(*value),
            Variant::U32(value) => i32::try_from(*value).ok(),
            Variant::U64(value) => i32::try_from(*value).ok(),
            Variant::String(value) => value.parse::<i32>().ok(),
            _ => None,
        }
    }

    fn event_field_u64(event: &GameEvent, name: &str) -> Option<u64> {
        match event_field(event, name)? {
            Variant::U64(value) => Some(*value),
            Variant::U32(value) => Some(u64::from(*value)),
            Variant::I32(value) => u64::try_from(*value).ok(),
            Variant::String(value) => value.parse::<u64>().ok(),
            _ => None,
        }
    }

    fn event_field_string(event: &GameEvent, name: &str) -> Option<String> {
        match event_field(event, name)? {
            Variant::String(value) => Some(value.clone()),
            Variant::U64(value) => Some(value.to_string()),
            Variant::U32(value) => Some(value.to_string()),
            Variant::I32(value) => Some(value.to_string()),
            _ => None,
        }
    }

    fn event_field<'a>(event: &'a GameEvent, name: &str) -> Option<&'a Variant> {
        event
            .fields
            .iter()
            .find(|field| field.name == name)
            .and_then(|field| field.data.as_ref())
    }

    fn parse_game_events(events: &[GameEvent]) -> Vec<ParsedGameEvent> {
        events
            .iter()
            .filter_map(parsed_game_event)
            .collect::<Vec<_>>()
    }

    fn parsed_game_event(event: &GameEvent) -> Option<ParsedGameEvent> {
        let supported = matches!(
            event.name.as_str(),
            "round_start"
                | "round_freeze_end"
                | "bomb_beginplant"
                | "bomb_planted"
                | "bomb_dropped"
                | "bomb_pickup"
                | "item_pickup"
                | "weapon_fire"
                | "player_hurt"
                | "player_death"
        );
        if !supported {
            return None;
        }

        Some(ParsedGameEvent {
            tick: event.tick,
            name: event.name.clone(),
            user_steam_id: event_steam_id(event),
            attacker_steam_id: event_field_u64(event, "attacker_steamid")
                .or_else(|| event_field_u64(event, "attacker")),
            victim_steam_id: event_field_u64(event, "victim_steamid")
                .or_else(|| event_field_u64(event, "userid")),
            weapon_def_index: event_field_i32(event, "defindex")
                .or_else(|| event_field_i32(event, "item_def_index"))
                .or_else(|| event_field_i32(event, "weapon_id")),
            item_name: event_field_string(event, "item")
                .or_else(|| event_field_string(event, "weapon"))
                .or_else(|| event_field_string(event, "weapon_name")),
            entity_id: event_field_i32(event, "entindex")
                .or_else(|| event_field_i32(event, "entityid")),
            damage: event_field_i32(event, "dmg_health")
                .or_else(|| event_field_i32(event, "damage")),
            health: event_field_i32(event, "health"),
        })
    }

    #[derive(Clone, Debug, Eq, Hash, PartialEq)]
    struct ProjectileKey {
        steam_id: u64,
        grenade_type: String,
        initial_position: [u32; 3],
        initial_velocity: [u32; 3],
    }

    impl ProjectileKey {
        fn new(
            steam_id: u64,
            grenade_type: &str,
            initial_position: [f32; 3],
            initial_velocity: [f32; 3],
        ) -> Self {
            Self {
                steam_id,
                grenade_type: grenade_type.to_string(),
                initial_position: initial_position.map(f32::to_bits),
                initial_velocity: initial_velocity.map(f32::to_bits),
            }
        }
    }

    fn vec3_is_meaningful(value: [f32; 3]) -> bool {
        value.iter().all(|component| component.is_finite())
            && value.iter().any(|component| component.abs() > f32::EPSILON)
    }

    fn seconds_to_ticks(seconds: f32, tick_rate: f32) -> i32 {
        if !seconds.is_finite() || !tick_rate.is_finite() || seconds <= 0.0 || tick_rate <= 0.0 {
            return 0;
        }
        (seconds * tick_rate).round() as i32
    }

    fn repair_round_phase_after_events(
        rows: &mut [ParsedPlayerTick],
        freeze_end_ticks: &[i32],
        round_end_ticks: &[i32],
    ) {
        if rows.is_empty() || freeze_end_ticks.is_empty() {
            return;
        }

        let mut rounds: BTreeMap<u32, RoundPhaseRows> = BTreeMap::new();
        for (idx, row) in rows.iter().enumerate() {
            rounds
                .entry(row.round)
                .and_modify(|round_rows| {
                    round_rows.min_tick = round_rows.min_tick.min(row.tick);
                    round_rows.max_tick = round_rows.max_tick.max(row.tick);
                    round_rows.indices.push(idx);
                })
                .or_insert_with(|| RoundPhaseRows {
                    min_tick: row.tick,
                    max_tick: row.tick,
                    indices: vec![idx],
                });
        }

        let mut phase_by_round = BTreeMap::new();
        for (round, round_rows) in &rounds {
            let candidates = freeze_end_ticks
                .iter()
                .copied()
                .filter(|tick| *tick >= round_rows.min_tick && *tick <= round_rows.max_tick)
                .collect::<Vec<_>>();
            let freeze_end = if is_pistol_round_number(*round) {
                candidates.into_iter().max().map(|freeze_end| {
                    let round_end =
                        round_end_after(round_end_ticks, freeze_end, round_rows.max_tick);
                    (freeze_end, round_end, (0, 0))
                })
            } else {
                candidates
                    .into_iter()
                    .map(|freeze_end| {
                        let round_end =
                            round_end_after(round_end_ticks, freeze_end, round_rows.max_tick);
                        let score =
                            round_phase_candidate_score(rows, round_rows, freeze_end, round_end);
                        (freeze_end, round_end, score)
                    })
                    .max_by_key(|(freeze_end, _round_end, score)| (*score, *freeze_end))
            };
            if let Some((freeze_end, round_end, _score)) = freeze_end {
                phase_by_round.insert(*round, (freeze_end, round_end));
            }
        }

        for row in rows {
            let Some((freeze_end, round_end)) = phase_by_round.get(&row.round).copied() else {
                continue;
            };
            if row.tick >= freeze_end {
                row.is_freeze_period = false;
            }
            if row.tick >= freeze_end && round_end.map_or(true, |round_end| row.tick < round_end) {
                row.round_in_progress = true;
            } else if round_end.is_some_and(|round_end| row.tick >= round_end) {
                row.round_in_progress = false;
            }
        }
    }

    fn is_pistol_round_number(round: u32) -> bool {
        round == 0 || round == 12
    }

    fn round_end_after(round_end_ticks: &[i32], freeze_end: i32, max_tick: i32) -> Option<i32> {
        round_end_ticks
            .iter()
            .copied()
            .filter(|tick| *tick >= freeze_end && *tick <= max_tick)
            .min()
    }

    #[derive(Clone, Debug)]
    struct RoundPhaseRows {
        min_tick: i32,
        max_tick: i32,
        indices: Vec<usize>,
    }

    fn round_phase_candidate_score(
        rows: &[ParsedPlayerTick],
        round_rows: &RoundPhaseRows,
        freeze_end: i32,
        round_end: Option<i32>,
    ) -> (usize, usize) {
        let live_end = round_end.unwrap_or(round_rows.max_tick.saturating_add(1));
        let mut players = BTreeSet::new();
        let mut live_rows = 0_usize;
        for idx in &round_rows.indices {
            let row = &rows[*idx];
            if row.tick < freeze_end
                || row.tick >= live_end
                || !row.is_alive
                || row.steam_id == 0
                || !matches!(row.team_num, 2 | 3)
            {
                continue;
            }
            players.insert(row.steam_id);
            live_rows += 1;
        }
        (players.len(), live_rows)
    }

    fn event_ticks(events: &[parser::second_pass::game_events::GameEvent], name: &str) -> Vec<i32> {
        events
            .iter()
            .filter(|event| event.name == name)
            .map(|event| event.tick)
            .collect()
    }

    fn estimate_tick_rate(rows: &[ParsedPlayerTick]) -> Option<f32> {
        let mut first = None;
        let mut last = None;
        for row in rows {
            if let Some(time) = row.game_time {
                if time.is_finite() {
                    first.get_or_insert((row.tick, time));
                    last = Some((row.tick, time));
                }
            }
        }
        let (first_tick, first_time) = first?;
        let (last_tick, last_time) = last?;
        let dt = last_time - first_time;
        let ticks = (last_tick - first_tick) as f32;
        if dt > 0.0 && ticks > 0.0 {
            let rate = ticks / dt;
            if (16.0..=256.0).contains(&rate) {
                return Some(rate.round());
            }
        }
        None
    }

    fn get_f32(columns: &AHashMap<String, &PropColumn>, name: &str, idx: usize) -> Option<f32> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::F32(v) => v.get(idx).copied().flatten(),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v as f32),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(|v| v as f32),
            _ => None,
        }
    }

    fn get_i32(columns: &AHashMap<String, &PropColumn>, name: &str, idx: usize) -> Option<i32> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::I32(v) => v.get(idx).copied().flatten(),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(|v| v as i32),
            VarVec::U64(v) => v.get(idx).copied().flatten().map(|v| v as i32),
            VarVec::F32(v) => v.get(idx).copied().flatten().and_then(f32_to_whole_i32),
            _ => None,
        }
    }

    fn f32_to_whole_i32(value: f32) -> Option<i32> {
        if value.is_finite()
            && value.fract() == 0.0
            && value >= i32::MIN as f32
            && value <= i32::MAX as f32
        {
            Some(value as i32)
        } else {
            None
        }
    }

    fn get_u32(columns: &AHashMap<String, &PropColumn>, name: &str, idx: usize) -> Option<u32> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::U32(v) => v.get(idx).copied().flatten(),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v as u32),
            VarVec::U64(v) => v.get(idx).copied().flatten().map(|v| v as u32),
            VarVec::F32(v) => v.get(idx).copied().flatten().and_then(f32_to_whole_u32),
            _ => None,
        }
    }

    fn f32_to_whole_u32(value: f32) -> Option<u32> {
        if value.is_finite() && value >= 0.0 && value.fract() == 0.0 && value <= u32::MAX as f32 {
            Some(value as u32)
        } else {
            None
        }
    }

    fn get_u64(columns: &AHashMap<String, &PropColumn>, name: &str, idx: usize) -> Option<u64> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::U64(v) => v.get(idx).copied().flatten(),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(u64::from),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v as u64),
            VarVec::String(v) => v
                .get(idx)
                .and_then(|v| v.as_ref())
                .and_then(|v| v.parse::<u64>().ok()),
            _ => None,
        }
    }

    fn get_u32_vec(
        columns: &AHashMap<String, &PropColumn>,
        name: &str,
        idx: usize,
    ) -> Option<Vec<u32>> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::U32Vec(v) => v.get(idx).cloned(),
            _ => None,
        }
    }

    fn get_vec3(
        columns: &AHashMap<String, &PropColumn>,
        name: &str,
        idx: usize,
    ) -> Option<[f32; 3]> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::XYZVec(v) => v.get(idx).copied().flatten(),
            _ => None,
        }
    }

    fn get_subtick_moves(
        columns: &AHashMap<String, &PropColumn>,
        name: &str,
        idx: usize,
    ) -> Option<(Vec<SubtickMove>, usize)> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::UserCmdSubtickMoves(v) => {
                let raw = v.get(idx)?;
                let mut truncated = 0_usize;
                let moves = raw
                    .iter()
                    .map(|subtick| {
                        if subtick.button > u32::MAX as u64 {
                            truncated += 1;
                        }
                        SubtickMove {
                            when: subtick.when,
                            button: subtick.button as u32,
                            pressed: if subtick.pressed { 1.0 } else { 0.0 },
                            analog_forward: subtick.analog_forward,
                            analog_left: subtick.analog_left,
                            pitch_delta: subtick.pitch_delta,
                            yaw_delta: subtick.yaw_delta,
                        }
                    })
                    .collect();
                Some((moves, truncated))
            }
            _ => None,
        }
    }

    fn get_bool(columns: &AHashMap<String, &PropColumn>, name: &str, idx: usize) -> Option<bool> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::Bool(v) => v.get(idx).copied().flatten(),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(|v| v != 0),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v != 0),
            _ => None,
        }
    }

    fn get_string(
        columns: &AHashMap<String, &PropColumn>,
        name: &str,
        idx: usize,
    ) -> Option<String> {
        match columns.get(name)?.data.as_ref()? {
            VarVec::String(v) => v.get(idx).cloned().flatten(),
            _ => None,
        }
    }

    fn get_weapon_stickers(
        columns: &AHashMap<String, &PropColumn>,
        name: &str,
        idx: usize,
    ) -> Option<Vec<ParsedWeaponSticker>> {
        let stickers = match columns.get(name)?.data.as_ref()? {
            VarVec::Stickers(v) => v.get(idx)?,
            _ => return None,
        };
        let parsed = stickers
            .iter()
            .filter_map(|sticker| {
                let slot = u8::try_from(sticker.slot).ok()?;
                if slot > 4
                    || sticker.id == 0
                    || !sticker.wear.is_finite()
                    || !(0.0..=1.0).contains(&sticker.wear)
                    || !sticker.x.is_finite()
                    || !sticker.y.is_finite()
                    || sticker.scale.is_some_and(|value| !value.is_finite())
                    || sticker.rotation.is_some_and(|value| !value.is_finite())
                {
                    return None;
                }
                Some(ParsedWeaponSticker {
                    slot,
                    sticker_id: sticker.id,
                    wear: sticker.wear,
                    offset_x: sticker.x,
                    offset_y: sticker.y,
                    scale: sticker.scale,
                    rotation: sticker.rotation,
                })
            })
            .collect::<Vec<_>>();
        (!parsed.is_empty()).then_some(parsed)
    }

    fn get_inventory_weapon_cosmetics(
        columns: &AHashMap<String, &PropColumn>,
        name: &str,
        idx: usize,
    ) -> Option<Vec<ParsedInventoryWeaponCosmetic>> {
        let weapons = match columns.get(name)?.data.as_ref()? {
            VarVec::InventoryWeaponCosmetics(v) => v.get(idx)?,
            _ => return None,
        };
        let parsed = weapons
            .iter()
            .filter_map(|weapon| {
                let item_def_index = i32::try_from(weapon.item_def_index).ok()?;
                let stickers = weapon
                    .stickers
                    .iter()
                    .filter_map(|sticker| {
                        let slot = u8::try_from(sticker.slot).ok()?;
                        if slot > 4
                            || sticker.id == 0
                            || !sticker.wear.is_finite()
                            || !(0.0..=1.0).contains(&sticker.wear)
                            || !sticker.x.is_finite()
                            || !sticker.y.is_finite()
                            || sticker.scale.is_some_and(|value| !value.is_finite())
                            || sticker.rotation.is_some_and(|value| !value.is_finite())
                        {
                            return None;
                        }
                        Some(ParsedWeaponSticker {
                            slot,
                            sticker_id: sticker.id,
                            wear: sticker.wear,
                            offset_x: sticker.x,
                            offset_y: sticker.y,
                            scale: sticker.scale,
                            rotation: sticker.rotation,
                        })
                    })
                    .collect::<Vec<_>>();
                Some(ParsedInventoryWeaponCosmetic {
                    item_def_index,
                    item_id_high: weapon.item_id_high,
                    item_id_low: weapon.item_id_low,
                    item_account_id: weapon.item_account_id,
                    original_owner_xuid: weapon.original_owner_xuid,
                    paint_kit: weapon.paint_kit,
                    paint_seed: weapon.paint_seed,
                    paint_wear: weapon.paint_wear,
                    entity_quality: weapon.entity_quality,
                    stattrak_counter: weapon.stattrak_counter,
                    attributes: weapon
                        .attributes
                        .iter()
                        .map(|attribute| ParsedInventoryWeaponAttribute {
                            definition_index: attribute.definition_index,
                            raw_value: attribute.raw_value,
                            raw_value_bits: attribute.raw_value_bits,
                        })
                        .collect(),
                    custom_name: weapon.custom_name.clone().and_then(normalize_custom_name),
                    stickers,
                })
            })
            .collect::<Vec<_>>();
        (!parsed.is_empty()).then_some(parsed)
    }

    fn combine_item_id(high: Option<u32>, low: Option<u32>) -> Option<u64> {
        Some((u64::from(high?) << 32) | u64::from(low?))
    }

    fn normalize_crosshair_code(value: String) -> Option<String> {
        let trimmed = value.trim();
        if trimmed.is_empty() || trimmed.len() > 128 {
            None
        } else {
            Some(trimmed.to_string())
        }
    }

    fn normalize_agent_skin(value: String) -> Option<String> {
        let trimmed = value.trim();
        if trimmed.is_empty()
            || trimmed.len() > 128
            || !trimmed
                .chars()
                .all(|ch| ch.is_ascii_alphanumeric() || ch == '_')
        {
            return None;
        }
        Some(trimmed.to_ascii_lowercase())
    }

    fn normalize_custom_name(value: String) -> Option<String> {
        let trimmed = value.trim().trim_matches('\0').trim();
        if trimmed.is_empty() {
            return None;
        }
        let cleaned = trimmed
            .chars()
            .filter(|ch| !ch.is_control() || *ch == '\t')
            .collect::<String>();
        let cleaned = cleaned.trim();
        if cleaned.is_empty() {
            None
        } else {
            Some(cleaned.chars().take(128).collect())
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn freeze_end_event_repairs_sticky_freeze_period_rows() {
            let mut rows = vec![
                row(1, 90, true),
                row(1, 99, true),
                row(1, 100, true),
                row(1, 120, true),
                row(1, 130, true),
            ];

            repair_round_phase_after_events(&mut rows, &[100], &[130]);

            assert!(rows[0].is_freeze_period);
            assert!(rows[1].is_freeze_period);
            assert!(!rows[2].is_freeze_period);
            assert!(!rows[3].is_freeze_period);
            assert!(!rows[4].is_freeze_period);
            assert!(!rows[1].round_in_progress);
            assert!(rows[2].round_in_progress);
            assert!(rows[3].round_in_progress);
            assert!(!rows[4].round_in_progress);
        }

        #[test]
        fn pistol_round_uses_latest_freeze_end_candidate() {
            let mut rows = vec![
                row(0, 90, true),
                row(0, 120, true),
                row(0, 1_490, true),
                row(0, 1_500, true),
                row(0, 1_520, true),
            ];

            repair_round_phase_after_events(&mut rows, &[100, 1_500], &[2_000]);

            assert!(rows[0].is_freeze_period);
            assert!(rows[1].is_freeze_period);
            assert!(rows[2].is_freeze_period);
            assert!(!rows[3].is_freeze_period);
            assert!(!rows[4].is_freeze_period);
            assert!(!rows[2].round_in_progress);
            assert!(rows[3].round_in_progress);
            assert!(rows[4].round_in_progress);
        }

        #[test]
        fn phase_repair_prefers_candidate_with_live_player_rows() {
            let mut rows = vec![
                row(1, 110, true),
                row(1, 120, true),
                row(1, 190, true),
                live_row(1, 200, true),
                live_row(1, 220, true),
                live_row(1, 240, true),
            ];

            repair_round_phase_after_events(&mut rows, &[100, 200], &[130, 260]);

            assert!(rows[2].is_freeze_period);
            assert!(!rows[3].is_freeze_period);
            assert!(rows[3].round_in_progress);
            assert!(rows[4].round_in_progress);
            assert!(rows[5].round_in_progress);
        }

        fn row(round: u32, tick: i32, is_freeze_period: bool) -> ParsedPlayerTick {
            ParsedPlayerTick {
                round,
                tick,
                is_freeze_period,
                ..ParsedPlayerTick::default()
            }
        }

        fn live_row(round: u32, tick: i32, is_freeze_period: bool) -> ParsedPlayerTick {
            ParsedPlayerTick {
                steam_id: tick as u64,
                team_num: 2,
                is_alive: true,
                ..row(round, tick, is_freeze_period)
            }
        }
    }
}

#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_bytes;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_header_map_bytes;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_with_options;

#[cfg(not(feature = "demoparser"))]
pub fn read_demo(_path: &Path) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_demo_with_options(_path: &Path, _options: ReadDemoOptions) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_demo_bytes(_bytes: &[u8], _stem: &str, _display_path: &str) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_demo_header_map_bytes(_bytes: &[u8]) -> Result<Option<String>> {
    Err(Error::FeatureDisabled("demoparser"))
}
