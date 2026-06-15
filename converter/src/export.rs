use crate::model::{
    ConversionManifest, ConvertedFile, ConvertedRound, EconomyClass, ParsedDemo, ParsedPlayerTick,
    Side, SubtickMode, TeamEconomy, CS2BM_ABI, CS2REC_VERSION,
};
use crate::quality::{analyze_demo, AnalysisOptions};
use crate::rec_writer::write_rec_file;
use crate::synthesis::{synthesize_player_rec_with_options, SynthesisOptions, SynthesisStats};
use crate::{io_error, Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug)]
pub struct ConvertOptions {
    pub output_dir: PathBuf,
    pub output_stem: Option<String>,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub include_suspicious: bool,
    pub cut_before_bomb_plant: bool,
    pub subtick_mode: SubtickMode,
    pub analysis: AnalysisOptions,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConversionReport {
    pub root: PathBuf,
    pub manifest_path: PathBuf,
    pub files_written: usize,
    pub manifest: ConversionManifest,
}

pub fn export_demo(parsed: &ParsedDemo, options: &ConvertOptions) -> Result<ConversionReport> {
    let analysis = analyze_demo(parsed, options.analysis);
    let output_stem = options.output_stem.as_deref().unwrap_or(&parsed.stem);
    let root = options.output_dir.join(output_stem);
    fs::create_dir_all(&root).map_err(|e| io_error(&root, e))?;

    let mut manifest = ConversionManifest {
        demo_path: parsed.path.clone(),
        map: parsed.map.clone(),
        tick_rate: parsed.tick_rate,
        abi: CS2BM_ABI,
        format_version: CS2REC_VERSION,
        rounds: Vec::new(),
        files: Vec::new(),
    };
    let mut log = Vec::new();
    let mut subtick_stats = SynthesisStats::default();
    log.push(format!(
        "demo={} map={} tick_rate={:.3}",
        parsed.path, parsed.map, parsed.tick_rate
    ));

    for round in &analysis.rounds {
        let selected = match &options.selected_rounds {
            Some(rounds) => rounds.contains(&round.round),
            None => round.recommended() || options.include_suspicious,
        };
        if !selected {
            log.push(format!("skip round {}: not selected", round.round));
            continue;
        }
        if !round.recommended() && !options.include_suspicious {
            log.push(format!(
                "skip round {}: suspicious ({})",
                round.round,
                round.problems.join("; ")
            ));
            continue;
        }

        let (end_tick, cut_reason) = if options.cut_before_bomb_plant {
            cut_before_bomb_plant(parsed, round.start_tick, round.end_tick)
        } else {
            (round.end_tick, None)
        };
        if end_tick <= round.start_tick {
            log.push(format!(
                "skip round {}: cut window empty after {:?}",
                round.round, cut_reason
            ));
            continue;
        }
        let pistol_round = is_pistol_round(round.round);
        let t_economy = team_economy(
            parsed,
            round.round,
            round.start_tick,
            end_tick,
            2,
            pistol_round,
        );
        let ct_economy = team_economy(
            parsed,
            round.round,
            round.start_tick,
            end_tick,
            3,
            pistol_round,
        );
        let first_file_index = manifest.files.len();

        let round_rows: Vec<_> = parsed
            .rows
            .iter()
            .filter(|row| {
                row.round == round.round
                    && row.tick >= round.start_tick
                    && row.tick <= end_tick
                    && row.is_alive
                    && row.steam_id != 0
                    && options.side.matches_team(row.team_num)
            })
            .collect();
        let mut players: BTreeMap<u64, Vec<ParsedPlayerTick>> = BTreeMap::new();
        for row in round_rows {
            players.entry(row.steam_id).or_default().push(row.clone());
        }

        for (steam_id, mut player_rows) in players {
            player_rows.sort_by_key(|row| row.tick);
            player_rows.dedup_by_key(|row| row.tick);
            if player_rows.len() < 2 {
                log.push(format!(
                    "skip round {} player {}: {} rows",
                    round.round,
                    steam_id,
                    player_rows.len()
                ));
                continue;
            }
            let (rec, stats) = synthesize_player_rec_with_options(
                &player_rows,
                &parsed.map,
                parsed.tick_rate,
                round.round,
                SynthesisOptions {
                    subtick_mode: options.subtick_mode,
                },
            )?;
            subtick_stats.add_assign(&stats);
            let team_dir = Side::team_dir(player_rows[0].team_num);
            let player_name = if player_rows[0].name.is_empty() {
                steam_id.to_string()
            } else {
                player_rows[0].name.clone()
            };
            let rel_path = Path::new(&format!("round{:02}", round.round))
                .join(team_dir)
                .join(format!("{}_{}.cs2rec", steam_id, slugify(&player_name)));
            let full_path = root.join(&rel_path);
            if let Some(parent) = full_path.parent() {
                fs::create_dir_all(parent).map_err(|e| io_error(parent, e))?;
            }
            write_rec_file(&full_path, &rec)?;
            manifest.files.push(ConvertedFile {
                path: rel_path.to_string_lossy().replace('\\', "/"),
                round: round.round,
                side: team_dir.to_string(),
                steam_id,
                player_name,
                ticks: rec.ticks.len(),
                subticks: rec.subticks.len(),
                first_weapon_def_index: first_weapon_def_index(&rec),
                preload_weapon_def_indices: preload_weapon_def_indices(&player_rows, &rec),
            });
        }

        let files = manifest.files.len() - first_file_index;
        if files > 0 {
            let duration_seconds = if parsed.tick_rate > 0.0 {
                ((end_tick - round.start_tick).max(0) as f32) / parsed.tick_rate
            } else {
                0.0
            };
            manifest.rounds.push(ConvertedRound {
                round: round.round,
                start_tick: round.start_tick,
                end_tick,
                original_end_tick: round.end_tick,
                duration_seconds,
                pistol_round,
                cut_reason,
                t_economy,
                ct_economy,
                files,
            });
        }
    }

    let manifest_path = root.join("manifest.json");
    let manifest_json = serde_json::to_string_pretty(&manifest)?;
    fs::write(&manifest_path, manifest_json).map_err(|e| io_error(&manifest_path, e))?;

    let log_path = root.join("conversion.log");
    log.push(format!("files_written={}", manifest.files.len()));
    log.push(format!(
        "subticks mode={} source={} written={} ticks_with_source={} ticks_with_written={} dropped_invalid={} dropped_overflow={} truncated_buttons={}",
        options.subtick_mode,
        subtick_stats.source_subticks,
        subtick_stats.written_subticks,
        subtick_stats.ticks_with_source_subticks,
        subtick_stats.ticks_with_written_subticks,
        subtick_stats.dropped_invalid_subticks,
        subtick_stats.dropped_overflow_subticks,
        subtick_stats.truncated_button_subticks
    ));
    fs::write(&log_path, log.join("\n")).map_err(|e| io_error(&log_path, e))?;

    Ok(ConversionReport {
        root,
        manifest_path,
        files_written: manifest.files.len(),
        manifest,
    })
}

fn cut_before_bomb_plant(
    parsed: &ParsedDemo,
    start_tick: i32,
    end_tick: i32,
) -> (i32, Option<String>) {
    let first_plant_tick = parsed
        .bomb_beginplant_ticks
        .iter()
        .chain(parsed.bomb_planted_ticks.iter())
        .copied()
        .filter(|tick| *tick > start_tick && *tick <= end_tick)
        .min();
    match first_plant_tick {
        Some(tick) => (
            tick.saturating_sub(1),
            Some(format!("before_bomb_plant_tick_{tick}")),
        ),
        None => (end_tick, None),
    }
}

fn team_economy(
    parsed: &ParsedDemo,
    round: u32,
    start_tick: i32,
    end_tick: i32,
    team_num: u8,
    pistol_round: bool,
) -> TeamEconomy {
    let mut first_rows: BTreeMap<u64, &ParsedPlayerTick> = BTreeMap::new();
    for row in &parsed.rows {
        if row.round != round
            || row.tick < start_tick
            || row.tick > end_tick
            || row.team_num != team_num
            || !row.is_alive
            || row.steam_id == 0
        {
            continue;
        }
        first_rows.entry(row.steam_id).or_insert(row);
    }

    let mut round_start_equipment_value = 0_u32;
    let mut equipment_value_total = 0_u32;
    let mut money_saved_total = 0_u32;
    let mut cash_spent_this_round = 0_u32;
    for row in first_rows.values() {
        let inferred = infer_inventory_value(&row.inventory_as_ids);
        let start_value = row.round_start_equip_value.max(inferred);
        let total_value = row.equipment_value_total.max(start_value);
        round_start_equipment_value = round_start_equipment_value.saturating_add(start_value);
        equipment_value_total = equipment_value_total.saturating_add(total_value);
        money_saved_total = money_saved_total.saturating_add(row.money_saved_total);
        cash_spent_this_round = cash_spent_this_round.saturating_add(row.cash_spent_this_round);
    }

    let class = classify_economy(
        first_rows.len(),
        round_start_equipment_value.max(equipment_value_total),
        pistol_round,
    );
    TeamEconomy {
        side: Side::team_dir(team_num).to_string(),
        players: first_rows.len(),
        round_start_equipment_value,
        equipment_value_total,
        money_saved_total,
        cash_spent_this_round,
        class,
    }
}

fn classify_economy(players: usize, team_equipment_value: u32, pistol_round: bool) -> EconomyClass {
    if pistol_round {
        return EconomyClass::Pistol;
    }
    if players == 0 {
        return EconomyClass::Unknown;
    }
    let per_player = team_equipment_value as f32 / players as f32;
    if per_player < 1_400.0 {
        EconomyClass::Eco
    } else if per_player < 3_600.0 {
        EconomyClass::Force
    } else {
        EconomyClass::Full
    }
}

fn infer_inventory_value(defs: &[i32]) -> u32 {
    defs.iter()
        .map(|def| weapon_value(normalize_weapon_def_index(*def)))
        .sum()
}

fn weapon_value(def: i32) -> u32 {
    match def {
        1 => 700,
        2 => 300,
        3 => 500,
        4 => 200,
        7 => 2700,
        8 => 3300,
        9 => 4750,
        10 => 2050,
        11 => 5000,
        13 => 1800,
        14 => 5200,
        16 => 3100,
        17 => 1050,
        19 => 2350,
        23 => 1500,
        24 => 1200,
        25 => 2000,
        26 => 1400,
        27 => 1300,
        28 => 1700,
        29 => 1100,
        30 => 500,
        31 => 200,
        32 => 200,
        33 => 1500,
        34 => 1250,
        35 => 1050,
        36 => 300,
        38 => 5000,
        39 => 3000,
        40 => 1700,
        43 => 200,
        44 => 300,
        45 => 300,
        46 => 400,
        47 => 50,
        48 => 600,
        60 => 2900,
        61 => 200,
        63 => 500,
        64 => 600,
        _ => 0,
    }
}

fn is_pistol_round(round: u32) -> bool {
    round == 0 || round == 12
}

fn first_weapon_def_index(rec: &crate::model::Cs2Rec) -> i32 {
    rec.ticks
        .iter()
        .map(|tick| normalize_weapon_def_index(tick.weapon_def_index))
        .find(|def| is_known_weapon_def_index(*def))
        .unwrap_or(-1)
}

fn preload_weapon_def_indices(rows: &[ParsedPlayerTick], rec: &crate::model::Cs2Rec) -> Vec<i32> {
    let mut seen = BTreeSet::new();
    let mut defs = Vec::new();
    for row in rows {
        for raw_def in &row.inventory_as_ids {
            let def = normalize_weapon_def_index(*raw_def);
            if is_preload_weapon_def_index(def) && seen.insert(def) {
                defs.push(def);
            }
        }
    }
    for tick in &rec.ticks {
        let def = normalize_weapon_def_index(tick.weapon_def_index);
        if is_preload_weapon_def_index(def) && seen.insert(def) {
            defs.push(def);
        }
    }
    defs
}

fn normalize_weapon_def_index(def: i32) -> i32 {
    if def == 42 || def == 59 || (500..600).contains(&def) {
        42
    } else {
        def
    }
}

fn is_known_weapon_def_index(def: i32) -> bool {
    matches!(
        def,
        1 | 2
            | 3
            | 4
            | 7
            | 8
            | 9
            | 10
            | 11
            | 13
            | 14
            | 16
            | 17
            | 19
            | 23
            | 24
            | 25
            | 26
            | 27
            | 28
            | 29
            | 30
            | 31
            | 32
            | 33
            | 34
            | 35
            | 36
            | 38
            | 39
            | 40
            | 42
            | 43
            | 44
            | 45
            | 46
            | 47
            | 48
            | 49
            | 60
            | 61
            | 63
            | 64
    )
}

fn is_preload_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 31 | 42 | 49)
}

fn slugify(value: &str) -> String {
    let mut out = String::new();
    for ch in value.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            out.push(ch);
        } else if ch.is_whitespace() {
            out.push('_');
        }
    }
    if out.is_empty() {
        "player".to_string()
    } else {
        out
    }
}

pub fn parse_round_list(value: &str) -> Result<BTreeSet<u32>> {
    let mut rounds = BTreeSet::new();
    for part in value.split(',').map(str::trim).filter(|p| !p.is_empty()) {
        if let Some((start, end)) = part.split_once('-') {
            let start = start
                .trim()
                .parse::<u32>()
                .map_err(|_| Error::InvalidDemo(format!("bad round range: {part}")))?;
            let end = end
                .trim()
                .parse::<u32>()
                .map_err(|_| Error::InvalidDemo(format!("bad round range: {part}")))?;
            for round in start.min(end)..=start.max(end) {
                rounds.insert(round);
            }
        } else {
            rounds.insert(
                part.parse::<u32>()
                    .map_err(|_| Error::InvalidDemo(format!("bad round: {part}")))?,
            );
        }
    }
    Ok(rounds)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Cs2Rec, ReplayTick};

    #[test]
    fn preload_weapon_defs_are_normalized_and_deduped() {
        let rec = Cs2Rec {
            ticks: vec![
                ReplayTick {
                    weapon_def_index: 508,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 61,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 61,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 43,
                    ..ReplayTick::default()
                },
                ReplayTick {
                    weapon_def_index: 49,
                    ..ReplayTick::default()
                },
            ],
            ..Cs2Rec::default()
        };

        assert_eq!(first_weapon_def_index(&rec), 42);
        let rows = vec![
            ParsedPlayerTick {
                inventory_as_ids: vec![7, 43],
                ..ParsedPlayerTick::default()
            },
            ParsedPlayerTick {
                inventory_as_ids: vec![7, 44],
                ..ParsedPlayerTick::default()
            },
        ];

        assert_eq!(preload_weapon_def_indices(&rows, &rec), vec![7, 43, 44, 61]);
    }
}
