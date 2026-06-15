use crate::model::ParsedDemo;
use crate::{Error, Result};
use std::path::Path;

#[cfg(feature = "demoparser")]
mod demoparser_impl {
    use super::*;
    use crate::io_error;
    use crate::model::{ParsedPlayerTick, SubtickMove};
    use ahash::AHashMap;
    use parser::first_pass::parser_settings::{rm_user_friendly_names, ParserInputs};
    use parser::parse_demo::{Parser, ParsingMode};
    use parser::second_pass::parser_settings::create_huffman_lookup_table;
    use parser::second_pass::variants::{PropColumn, VarVec};
    use std::fs;

    pub fn read_demo(path: &Path) -> Result<ParsedDemo> {
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
            "item_def_idx",
            "inventory_as_ids",
            "round_start_equip_value",
            "equipment_value_total",
            "money_saved_total",
            "cash_spent_this_round",
            "team_num",
            "is_alive",
            "is_airborne",
            "move_type",
            "CCSPlayerPawn.m_fFlags",
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
        let mut real_name_to_og_name = AHashMap::default();
        for (real, friendly) in real_props.iter().zip(&wanted_props) {
            real_name_to_og_name.insert(real.clone(), friendly.clone());
        }

        let bytes = fs::read(path).map_err(|e| io_error(path, e))?;
        let huf = create_huffman_lookup_table();
        let settings = ParserInputs {
            real_name_to_og_name,
            wanted_players: vec![],
            wanted_player_props: real_props,
            wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: vec![],
            wanted_events: vec![
                "round_freeze_end".to_string(),
                "bomb_beginplant".to_string(),
                "bomb_planted".to_string(),
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
            .parse_demo(&bytes)
            .map_err(|e| Error::Parser(format!("{e:?}")))?;

        let header = output.header.unwrap_or_default();
        let map = header
            .get("map_name")
            .cloned()
            .unwrap_or_else(|| "unknown".to_string());
        let stem = path
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("demo")
            .to_string();

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
                item_def_idx: get_i32(&columns, "item_def_idx", idx)
                    .or_else(|| get_u32(&columns, "item_def_idx", idx).map(|v| v as i32))
                    .unwrap_or(-1),
                inventory_as_ids: get_u32_vec(&columns, "inventory_as_ids", idx)
                    .unwrap_or_default()
                    .into_iter()
                    .map(|v| v as i32)
                    .collect(),
                round_start_equip_value: get_u32(&columns, "round_start_equip_value", idx)
                    .unwrap_or_default(),
                equipment_value_total: get_u32(&columns, "equipment_value_total", idx)
                    .unwrap_or_default(),
                money_saved_total: get_u32(&columns, "money_saved_total", idx).unwrap_or_default(),
                cash_spent_this_round: get_u32(&columns, "cash_spent_this_round", idx)
                    .unwrap_or_default(),
                entity_flags: explicit_flags.unwrap_or(if is_airborne { 0 } else { 1 }),
                move_type: get_u32(&columns, "move_type", idx).unwrap_or(2) as u8,
                subtick_moves,
                subtick_button_truncated,
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

        Ok(ParsedDemo {
            path: path.display().to_string(),
            stem,
            map,
            tick_rate,
            round_freeze_end_ticks,
            bomb_beginplant_ticks,
            bomb_planted_ticks,
            rows,
        })
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
            _ => None,
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
}

#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo;

#[cfg(not(feature = "demoparser"))]
pub fn read_demo(_path: &Path) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}
