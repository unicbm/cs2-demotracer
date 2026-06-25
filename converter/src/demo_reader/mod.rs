use crate::model::ParsedDemo;
use crate::{Error, Result};
use std::path::Path;

#[cfg(feature = "demoparser")]
mod demoparser_impl {
    use super::*;
    use crate::io_error;
    use crate::model::{
        ParsedGameEvent, ParsedInventoryWeaponCosmetic, ParsedPlayerTick, ParsedProjectile,
        ParsedWeaponSticker, ProjectileEffectSource, ProjectileKind, SubtickMove,
    };
    use ahash::AHashMap;
    use parser::first_pass::parser_settings::{rm_user_friendly_names, ParserInputs};
    use parser::parse_demo::{Parser, ParsingMode};
    use parser::second_pass::game_events::GameEvent;
    use parser::second_pass::parser_settings::create_huffman_lookup_table;
    use parser::second_pass::variants::{PropColumn, VarVec, Variant};
    use std::fs;

    pub fn read_demo(path: &Path) -> Result<ParsedDemo> {
        let bytes = fs::read(path).map_err(|e| io_error(path, e))?;
        let stem = path
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("demo")
            .to_string();
        read_demo_bytes(&bytes, &stem, &path.display().to_string())
    }

    pub fn read_demo_bytes(bytes: &[u8], stem: &str, display_path: &str) -> Result<ParsedDemo> {
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
            "usercmd_buttonstate_1",
            "usercmd_buttonstate_2",
            "usercmd_buttonstate_3",
            "item_def_idx",
            "inventory_as_ids",
            "inventory_weapon_cosmetics",
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
                active_weapon_paint_kit: get_u32(&columns, "weapon_skin_id", idx),
                active_weapon_paint_seed: get_u32(&columns, "weapon_paint_seed", idx),
                active_weapon_paint_wear: get_f32(&columns, "weapon_float", idx),
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
        let mut bomb_beginplant_ticks = event_ticks(&output.game_events, "bomb_beginplant");
        let mut bomb_planted_ticks = event_ticks(&output.game_events, "bomb_planted");
        bomb_beginplant_ticks.sort_unstable();
        bomb_beginplant_ticks.dedup();
        bomb_planted_ticks.sort_unstable();
        bomb_planted_ticks.dedup();
        let events = parse_game_events(&output.game_events);
        let projectiles = parse_projectiles(bytes, tick_rate, &output.game_events)?;

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
            events,
        })
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

    fn parse_projectiles(
        bytes: &[u8],
        tick_rate: f32,
        game_events: &[GameEvent],
    ) -> Result<Vec<ParsedProjectile>> {
        let wanted_props = vec![
            "Grenade.m_vInitialPosition",
            "Grenade.m_vInitialVelocity",
            "Grenade.m_vSmokeDetonationPos",
            "Grenade.m_nBounces",
        ]
        .into_iter()
        .map(str::to_string)
        .collect::<Vec<_>>();

        let real_props =
            rm_user_friendly_names(&wanted_props).map_err(|e| Error::Parser(format!("{e:?}")))?;
        let mut real_name_to_og_name = AHashMap::default();
        for (real, friendly) in real_props.iter().zip(&wanted_props) {
            real_name_to_og_name.insert(real.clone(), friendly.clone());
        }

        let huf = create_huffman_lookup_table();
        let settings = ParserInputs {
            real_name_to_og_name,
            wanted_players: vec![],
            wanted_player_props: real_props,
            wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: vec![],
            wanted_events: vec![],
            parse_ents: true,
            parse_projectiles: true,
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

        let columns = output_columns(&output);
        let len = columns.values().map(|c| c.len()).max().unwrap_or_default();
        let mut grouped: AHashMap<ProjectileKey, ProjectileCandidate> = AHashMap::default();
        for idx in 0..len {
            let steam_id = get_u64(&columns, "steamid", idx).unwrap_or_default();
            if steam_id == 0 {
                continue;
            }
            let tick = get_i32(&columns, "tick", idx).unwrap_or_default();
            let grenade_type = get_string(&columns, "grenade_type", idx).unwrap_or_default();
            let initial_position = match get_vec3(&columns, "Grenade.m_vInitialPosition", idx) {
                Some(value) if vec3_is_meaningful(value) => value,
                _ => continue,
            };
            let initial_velocity = match get_vec3(&columns, "Grenade.m_vInitialVelocity", idx) {
                Some(value) if vec3_is_meaningful(value) => value,
                _ => continue,
            };
            let detonation_position =
                get_vec3(&columns, "Grenade.m_vSmokeDetonationPos", idx).unwrap_or_default();
            let entity_id = get_i32(&columns, "grenade_entity_id", idx).unwrap_or_default();
            let key =
                ProjectileKey::new(steam_id, &grenade_type, initial_position, initial_velocity);
            let entry = grouped.entry(key).or_insert_with(|| ProjectileCandidate {
                entity_id,
                projectile: ParsedProjectile {
                    tick,
                    steam_id,
                    name: get_string(&columns, "name", idx).unwrap_or_default(),
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
        Ok(projectiles)
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

    fn output_columns<'a>(
        output: &'a parser::parse_demo::DemoOutput,
    ) -> AHashMap<String, &'a PropColumn> {
        let mut columns = AHashMap::default();
        for info in &output.prop_controller.prop_infos {
            if let Some(column) = output.df.get(&info.id) {
                columns.insert(info.prop_friendly_name.clone(), column);
            }
        }
        columns
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
            _ => None,
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
                {
                    return None;
                }
                Some(ParsedWeaponSticker {
                    slot,
                    sticker_id: sticker.id,
                    wear: sticker.wear,
                    offset_x: sticker.x,
                    offset_y: sticker.y,
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
                        {
                            return None;
                        }
                        Some(ParsedWeaponSticker {
                            slot,
                            sticker_id: sticker.id,
                            wear: sticker.wear,
                            offset_x: sticker.x,
                            offset_y: sticker.y,
                        })
                    })
                    .collect::<Vec<_>>();
                Some(ParsedInventoryWeaponCosmetic {
                    item_def_index,
                    paint_kit: weapon.paint_kit,
                    paint_seed: weapon.paint_seed,
                    paint_wear: weapon.paint_wear,
                    custom_name: weapon.custom_name.clone().and_then(normalize_custom_name),
                    stickers,
                })
            })
            .collect::<Vec<_>>();
        (!parsed.is_empty()).then_some(parsed)
    }

    fn normalize_crosshair_code(value: String) -> Option<String> {
        let trimmed = value.trim();
        if trimmed.is_empty() || trimmed.len() > 128 {
            None
        } else {
            Some(trimmed.to_string())
        }
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
}

#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_bytes;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_header_map_bytes;

#[cfg(not(feature = "demoparser"))]
pub fn read_demo(_path: &Path) -> Result<ParsedDemo> {
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
