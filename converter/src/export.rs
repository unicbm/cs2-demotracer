use crate::demo_id::output_demo_id;
use crate::model::{
    public_demo_path, ConversionManifest, ConvertedFile, ConvertedRound, EconomyClass, ParsedDemo,
    ParsedPlayerTick, ParsedProjectile, ReplayLoadout, Side, SubtickMode, TeamEconomy,
    DEMOTRACER_ABI, DTR_FORMAT_VERSION,
};
use crate::quality::{analyze_demo, AnalysisOptions};
use crate::rec_writer::write_rec;
use crate::synthesis::{synthesize_player_rec_with_row_refs, SynthesisOptions, SynthesisStats};
use crate::{io_error, Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};

pub const DEFAULT_FREEZE_PREROLL_SECONDS: f32 = 10.0;

#[derive(Clone, Debug)]
pub struct ConvertOptions {
    pub output_dir: PathBuf,
    pub output_stem: Option<String>,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub include_suspicious: bool,
    pub cut_before_bomb_plant: bool,
    pub subtick_mode: SubtickMode,
    pub freeze_preroll_seconds: f32,
    pub analysis: AnalysisOptions,
}

#[derive(Clone, Debug)]
pub struct ConvertMemoryOptions {
    pub output_stem: Option<String>,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub include_suspicious: bool,
    pub cut_before_bomb_plant: bool,
    pub subtick_mode: SubtickMode,
    pub freeze_preroll_seconds: f32,
    pub analysis: AnalysisOptions,
}

impl From<&ConvertOptions> for ConvertMemoryOptions {
    fn from(options: &ConvertOptions) -> Self {
        Self {
            output_stem: options.output_stem.clone(),
            side: options.side,
            selected_rounds: options.selected_rounds.clone(),
            include_suspicious: options.include_suspicious,
            cut_before_bomb_plant: options.cut_before_bomb_plant,
            subtick_mode: options.subtick_mode,
            freeze_preroll_seconds: options.freeze_preroll_seconds,
            analysis: options.analysis,
        }
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConversionReport {
    pub root: PathBuf,
    pub manifest_path: PathBuf,
    pub files_written: usize,
    pub manifest: ConversionManifest,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ConversionArtifactKind {
    Dtr,
    Manifest,
    Log,
}

#[derive(Clone, Debug)]
pub struct ConversionArtifact {
    pub path: String,
    pub bytes: Vec<u8>,
    pub kind: ConversionArtifactKind,
    pub round: Option<u32>,
    pub steam_id: Option<u64>,
}

#[derive(Clone, Debug)]
pub struct MemoryConversionReport {
    pub demo_id: String,
    pub files_written: usize,
    pub manifest: ConversionManifest,
    pub log: String,
    pub artifacts: Vec<ConversionArtifact>,
}

pub fn export_demo_to_memory(
    parsed: &ParsedDemo,
    options: &ConvertMemoryOptions,
) -> Result<MemoryConversionReport> {
    validate_freeze_preroll_seconds(options.freeze_preroll_seconds)?;
    let analysis = analyze_demo(parsed, options.analysis);
    let output_stem = output_demo_id(
        &parsed.stem,
        &parsed.demo_sha256,
        options.output_stem.as_deref(),
    )?;

    let mut manifest = ConversionManifest {
        demo_path: public_demo_path(&parsed.path),
        demo_id: output_stem.clone(),
        demo_sha256: parsed.demo_sha256.clone(),
        map: parsed.map.clone(),
        tick_rate: parsed.tick_rate,
        abi: DEMOTRACER_ABI,
        format_version: DTR_FORMAT_VERSION,
        rounds: Vec::new(),
        files: Vec::new(),
    };
    let mut log = Vec::new();
    let mut artifacts = Vec::new();
    let mut subtick_stats = SynthesisStats::default();
    let rows_by_round = rows_by_round(&parsed.rows);
    let projectiles_by_steam_id = projectiles_by_steam_id(&parsed.projectiles);
    log.push(format!(
        "demo={} id={} sha256={} map={} tick_rate={:.3}",
        parsed.path, output_stem, parsed.demo_sha256, parsed.map, parsed.tick_rate
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
        let bomb_planted_tick =
            bomb_planted_tick_for_round(parsed, round.start_tick, round.end_tick);
        let bomb_planted_seconds_after_live = bomb_planted_tick
            .map(|tick| ticks_to_seconds(tick - round.start_tick, parsed.tick_rate));
        if end_tick <= round.start_tick {
            log.push(format!(
                "skip round {}: cut window empty after {:?}",
                round.round, cut_reason
            ));
            continue;
        }
        let round_rows: &[&ParsedPlayerTick] = rows_by_round
            .get(&round.round)
            .map(Vec::as_slice)
            .unwrap_or(&[]);
        let recording_start_tick = recording_start_tick_for_round(
            round_rows,
            parsed.tick_rate,
            round.start_tick,
            options.freeze_preroll_seconds,
        );
        let freeze_preroll_ticks = round.start_tick.saturating_sub(recording_start_tick);
        let pistol_round = is_pistol_round(round.round);
        let t_economy = team_economy(round_rows, round.start_tick, end_tick, 2, pistol_round);
        let ct_economy = team_economy(round_rows, round.start_tick, end_tick, 3, pistol_round);
        let first_file_index = manifest.files.len();

        let mut players: BTreeMap<u64, Vec<&ParsedPlayerTick>> = BTreeMap::new();
        for &row in round_rows {
            if row.tick < recording_start_tick
                || row.tick > end_tick
                || (row.tick < round.start_tick && !row.is_freeze_period)
                || !row.is_alive
                || row.steam_id == 0
                || !options.side.matches_team(row.team_num)
            {
                continue;
            }
            players.entry(row.steam_id).or_default().push(row);
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
            let play_start_tick_index = play_start_tick_index(&player_rows, round.start_tick);
            let player_projectiles = projectiles_by_steam_id
                .get(&steam_id)
                .map(Vec::as_slice)
                .unwrap_or(&[]);
            let (rec, stats) = synthesize_player_rec_with_row_refs(
                &player_rows,
                player_projectiles,
                &parsed.map,
                parsed.tick_rate,
                round.round,
                SynthesisOptions {
                    subtick_mode: options.subtick_mode,
                    play_start_tick_index,
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
                .join(format!("{}_{}.dtr", steam_id, slugify(&player_name)));
            let mut bytes = Vec::new();
            write_rec(&mut bytes, &rec)?;
            let path = rel_path.to_string_lossy().replace('\\', "/");
            manifest.files.push(ConvertedFile {
                path: path.clone(),
                round: round.round,
                side: team_dir.to_string(),
                steam_id,
                player_name,
                ticks: rec.ticks.len(),
                subticks: rec.subticks.len(),
                play_start_tick_index: rec.header.play_start_tick_index,
                first_weapon_def_index: first_weapon_def_index(&rec),
                preload_weapon_def_indices: preload_weapon_def_indices_from_refs(
                    &player_rows,
                    &rec,
                ),
                loadout: replay_loadout(player_rows[0]),
            });
            artifacts.push(ConversionArtifact {
                path,
                bytes,
                kind: ConversionArtifactKind::Dtr,
                round: Some(round.round),
                steam_id: Some(steam_id),
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
                recording_start_tick,
                start_tick: round.start_tick,
                end_tick,
                original_end_tick: round.end_tick,
                bomb_planted_tick,
                bomb_planted_seconds_after_live,
                freeze_preroll_ticks,
                duration_seconds,
                pistol_round,
                cut_reason,
                t_economy,
                ct_economy,
                files,
            });
        }
    }

    let manifest_json = serde_json::to_string_pretty(&manifest)?;

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
    let log = log.join("\n");
    artifacts.push(ConversionArtifact {
        path: "manifest.json".to_string(),
        bytes: manifest_json.into_bytes(),
        kind: ConversionArtifactKind::Manifest,
        round: None,
        steam_id: None,
    });
    artifacts.push(ConversionArtifact {
        path: "conversion.log".to_string(),
        bytes: log.as_bytes().to_vec(),
        kind: ConversionArtifactKind::Log,
        round: None,
        steam_id: None,
    });

    Ok(MemoryConversionReport {
        demo_id: output_stem,
        files_written: manifest.files.len(),
        manifest,
        log,
        artifacts,
    })
}

pub fn export_demo(parsed: &ParsedDemo, options: &ConvertOptions) -> Result<ConversionReport> {
    let memory = export_demo_to_memory(parsed, &ConvertMemoryOptions::from(options))?;
    let root = options.output_dir.join(&memory.demo_id);
    fs::create_dir_all(&root).map_err(|e| io_error(&root, e))?;

    for artifact in &memory.artifacts {
        let path = root.join(&artifact.path);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| io_error(parent, e))?;
        }
        fs::write(&path, &artifact.bytes).map_err(|e| io_error(&path, e))?;
    }

    Ok(ConversionReport {
        root: root.clone(),
        manifest_path: root.join("manifest.json"),
        files_written: memory.files_written,
        manifest: memory.manifest,
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

fn bomb_planted_tick_for_round(parsed: &ParsedDemo, start_tick: i32, end_tick: i32) -> Option<i32> {
    parsed
        .bomb_planted_ticks
        .iter()
        .copied()
        .filter(|tick| *tick >= start_tick && *tick <= end_tick)
        .min()
}

fn ticks_to_seconds(ticks: i32, tick_rate: f32) -> f32 {
    if tick_rate > 0.0 {
        ticks as f32 / tick_rate
    } else {
        0.0
    }
}

fn validate_freeze_preroll_seconds(seconds: f32) -> Result<()> {
    if seconds.is_finite() && seconds >= 0.0 {
        return Ok(());
    }
    Err(Error::InvalidDemo(
        "freeze pre-roll must be a finite non-negative number".to_string(),
    ))
}

fn rows_by_round(rows: &[ParsedPlayerTick]) -> BTreeMap<u32, Vec<&ParsedPlayerTick>> {
    let mut by_round: BTreeMap<u32, Vec<&ParsedPlayerTick>> = BTreeMap::new();
    for row in rows {
        by_round.entry(row.round).or_default().push(row);
    }
    by_round
}

fn projectiles_by_steam_id(
    projectiles: &[ParsedProjectile],
) -> BTreeMap<u64, Vec<&ParsedProjectile>> {
    let mut by_steam_id: BTreeMap<u64, Vec<&ParsedProjectile>> = BTreeMap::new();
    for projectile in projectiles {
        by_steam_id
            .entry(projectile.steam_id)
            .or_default()
            .push(projectile);
    }
    by_steam_id
}

fn recording_start_tick_for_round(
    round_rows: &[&ParsedPlayerTick],
    tick_rate: f32,
    live_start_tick: i32,
    freeze_preroll_seconds: f32,
) -> i32 {
    let cap_ticks = seconds_to_ticks(freeze_preroll_seconds, tick_rate);
    if cap_ticks <= 0 {
        return live_start_tick;
    }
    let floor_tick = live_start_tick.saturating_sub(cap_ticks);
    round_rows
        .iter()
        .copied()
        .filter(|row| {
            row.tick >= floor_tick
                && row.tick < live_start_tick
                && row.is_freeze_period
                && row.is_alive
                && row.steam_id != 0
                && matches!(row.team_num, 2 | 3)
        })
        .map(|row| row.tick)
        .min()
        .unwrap_or(live_start_tick)
}

fn seconds_to_ticks(seconds: f32, tick_rate: f32) -> i32 {
    if !seconds.is_finite() || !tick_rate.is_finite() || seconds <= 0.0 || tick_rate <= 0.0 {
        return 0;
    }
    (seconds * tick_rate).round() as i32
}

fn play_start_tick_index(rows: &[&ParsedPlayerTick], live_start_tick: i32) -> u32 {
    let tick_count = rows.len().saturating_sub(1);
    if tick_count == 0 {
        return 0;
    }
    rows.iter()
        .take(tick_count)
        .position(|row| row.tick >= live_start_tick)
        .unwrap_or_else(|| tick_count.saturating_sub(1)) as u32
}

fn team_economy(
    round_rows: &[&ParsedPlayerTick],
    start_tick: i32,
    end_tick: i32,
    team_num: u8,
    pistol_round: bool,
) -> TeamEconomy {
    let mut first_rows: BTreeMap<u64, &ParsedPlayerTick> = BTreeMap::new();
    for &row in round_rows {
        if row.tick < start_tick
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

pub(crate) fn first_weapon_def_index(rec: &crate::model::Cs2Rec) -> i32 {
    rec.ticks
        .iter()
        .map(|tick| normalize_weapon_def_index(tick.weapon_def_index))
        .find(|def| is_known_weapon_def_index(*def))
        .unwrap_or(-1)
}

pub(crate) fn preload_weapon_def_indices_from_refs(
    rows: &[&ParsedPlayerTick],
    rec: &crate::model::Cs2Rec,
) -> Vec<i32> {
    preload_weapon_def_indices_from_iter(rows.iter().copied(), rec)
}

fn preload_weapon_def_indices_from_iter<'a>(
    rows: impl IntoIterator<Item = &'a ParsedPlayerTick>,
    rec: &crate::model::Cs2Rec,
) -> Vec<i32> {
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

pub(crate) fn replay_loadout(row: &ParsedPlayerTick) -> ReplayLoadout {
    ReplayLoadout {
        weapon_def_indices: row
            .inventory_as_ids
            .iter()
            .map(|def| normalize_weapon_def_index(*def))
            .filter(|def| is_loadout_weapon_def_index(*def))
            .collect(),
        armor_value: row.armor_value,
        has_helmet: row.has_helmet,
        has_defuser: row.has_defuser,
    }
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

fn is_loadout_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 42 | 49)
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
    use crate::model::{Cs2Rec, ParsedDemo, ReplayTick};
    use crate::rec_writer::read_rec;

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

        let row_refs = rows.iter().collect::<Vec<_>>();
        assert_eq!(
            preload_weapon_def_indices_from_refs(&row_refs, &rec),
            vec![7, 43, 44, 61]
        );
    }

    #[test]
    fn replay_loadout_keeps_counts_and_skips_non_equipment() {
        let row = ParsedPlayerTick {
            inventory_as_ids: vec![7, 43, 43, 49, 508, 31],
            armor_value: 87,
            has_helmet: true,
            has_defuser: true,
            ..ParsedPlayerTick::default()
        };

        let loadout = replay_loadout(&row);
        assert_eq!(loadout.weapon_def_indices, vec![7, 43, 43, 31]);
        assert_eq!(loadout.armor_value, 87);
        assert!(loadout.has_helmet);
        assert!(loadout.has_defuser);
    }

    #[test]
    fn memory_export_matches_filesystem_export_surface() {
        let parsed = sample_demo();
        let selected_rounds = Some(BTreeSet::from([1]));
        let memory_options = ConvertMemoryOptions {
            output_stem: Some("sample-demo".to_string()),
            side: Side::Both,
            selected_rounds: selected_rounds.clone(),
            include_suspicious: true,
            cut_before_bomb_plant: true,
            subtick_mode: SubtickMode::Auto,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            analysis: AnalysisOptions::default(),
        };

        let memory = export_demo_to_memory(&parsed, &memory_options).unwrap();

        assert_eq!(memory.demo_id, "sample-demo");
        assert_eq!(memory.files_written, 1);
        assert!(memory
            .artifacts
            .iter()
            .any(|artifact| artifact.path == "manifest.json"));
        assert!(memory
            .artifacts
            .iter()
            .any(|artifact| artifact.path == "conversion.log"));
        assert!(memory.log.contains("files_written=1"));

        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.kind == ConversionArtifactKind::Dtr)
            .unwrap();
        assert_eq!(dtr.path, "round01/t/76561198000000001_alpha.dtr");
        let parsed_rec = read_rec(&mut &dtr.bytes[..]).unwrap();
        assert_eq!(parsed_rec.header.round, 1);
        assert_eq!(parsed_rec.ticks.len(), 1);

        let temp = tempfile::tempdir().unwrap();
        let filesystem = export_demo(
            &parsed,
            &ConvertOptions {
                output_dir: temp.path().to_path_buf(),
                output_stem: Some("sample-demo".to_string()),
                side: Side::Both,
                selected_rounds,
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap();

        assert_eq!(
            serde_json::to_value(&filesystem.manifest).unwrap(),
            serde_json::to_value(&memory.manifest).unwrap()
        );
        let disk_dtr = std::fs::read(filesystem.root.join(&dtr.path)).unwrap();
        assert_eq!(disk_dtr, dtr.bytes);
        assert!(filesystem.manifest_path.exists());
        assert!(filesystem.root.join("conversion.log").exists());
    }

    #[test]
    fn export_rejects_escaping_output_stem() {
        let parsed = sample_demo();
        let err = export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("../escape".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap_err();

        assert!(err.to_string().contains("output_stem"));
    }

    #[test]
    fn export_includes_bounded_freeze_preroll_and_live_play_start() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            freeze_row(20),
            freeze_row(80),
            sample_row(100),
            sample_row(164),
            sample_row(228),
        ];
        parsed.projectiles = vec![crate::model::ParsedProjectile {
            tick: 164,
            steam_id: 76561198000000001,
            name: "alpha".to_string(),
            grenade_type: "smokegrenade_projectile".to_string(),
            kind: crate::model::ProjectileKind::Smoke,
            weapon_def_index: 45,
            initial_position: [1.0, 2.0, 3.0],
            initial_velocity: [4.0, 5.0, 6.0],
            detonation_position: [7.0, 8.0, 9.0],
            effect_position: [0.0; 3],
            effect_tick: None,
            effect_source: crate::model::ProjectileEffectSource::Unknown,
            effect_confidence: 0.0,
        }];

        let memory = export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("freeze-demo".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap();

        assert_eq!(memory.manifest.rounds[0].recording_start_tick, 20);
        assert_eq!(memory.manifest.rounds[0].start_tick, 100);
        assert_eq!(memory.manifest.rounds[0].freeze_preroll_ticks, 80);
        assert_eq!(memory.manifest.files[0].play_start_tick_index, 2);

        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.kind == ConversionArtifactKind::Dtr)
            .unwrap();
        let rec = read_rec(&mut &dtr.bytes[..]).unwrap();
        assert_eq!(rec.header.play_start_tick_index, 2);
        assert_eq!(rec.ticks.len(), 4);
        assert_eq!(rec.ticks[0].pre.origin[0], 20.0);
        assert_eq!(rec.ticks[2].pre.origin[0], 100.0);
        assert_eq!(rec.projectiles[0].tick_index, 3);
    }

    #[test]
    fn export_caps_freeze_preroll_before_pause_tail() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            freeze_row(-1_000),
            freeze_row(60),
            sample_row(100),
            sample_row(164),
        ];

        let memory = export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("cap-demo".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: true,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: 1.0,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap();

        assert_eq!(memory.manifest.rounds[0].recording_start_tick, 60);
        assert_eq!(memory.manifest.rounds[0].freeze_preroll_ticks, 40);
        assert_eq!(memory.manifest.files[0].play_start_tick_index, 1);

        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.kind == ConversionArtifactKind::Dtr)
            .unwrap();
        let rec = read_rec(&mut &dtr.bytes[..]).unwrap();
        assert_eq!(rec.ticks[0].pre.origin[0], 60.0);
    }

    fn sample_demo() -> ParsedDemo {
        ParsedDemo {
            path: "<demo.dem>".to_string(),
            stem: "demo".to_string(),
            demo_sha256: "00".repeat(32),
            map: "de_mirage".to_string(),
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            bomb_beginplant_ticks: Vec::new(),
            bomb_planted_ticks: Vec::new(),
            rows: vec![sample_row(100), sample_row(164)],
            projectiles: Vec::new(),
        }
    }

    fn sample_row(tick: i32) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id: 76561198000000001,
            name: "alpha".to_string(),
            team_num: 2,
            is_alive: true,
            round: 1,
            round_in_progress: true,
            is_freeze_period: false,
            game_time: Some(tick as f32 / 64.0),
            origin: [tick as f32, 1.0, 2.0],
            velocity: [1.0, 0.0, 0.0],
            pitch: 3.0,
            yaw: 4.0,
            buttons: 1,
            buttonstate1: 1,
            buttonstate2: 0,
            buttonstate3: 0,
            item_def_idx: 7,
            inventory_as_ids: vec![7],
            armor_value: 100,
            has_helmet: true,
            has_defuser: false,
            round_start_equip_value: 2_700,
            equipment_value_total: 2_700,
            money_saved_total: 800,
            cash_spent_this_round: 0,
            entity_flags: 1,
            move_type: 2,
            duck_amount: None,
            duck_speed: None,
            ladder_normal: None,
            ducked: None,
            ducking: None,
            desires_duck: None,
            subtick_moves: Vec::new(),
            subtick_button_truncated: 0,
            team_rounds_total: None,
            team_name: None,
            team_clan_name: None,
        }
    }

    fn freeze_row(tick: i32) -> ParsedPlayerTick {
        ParsedPlayerTick {
            round_in_progress: false,
            is_freeze_period: true,
            ..sample_row(tick)
        }
    }
}
