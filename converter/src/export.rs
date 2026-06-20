use crate::demo_id::output_demo_id;
use crate::model::{
    public_demo_path, ConversionManifest, ConvertedFile, ConvertedRound, EconomyClass,
    HighFidelityMetadata, ParsedDemo, ParsedGameEvent, ParsedPlayerTick, ParsedProjectile,
    ReplayHifiEvent, ReplayHifiEventKind, ReplayInventoryItemCount, ReplayInventorySnapshot,
    ReplayLoadout, Side, SubtickMode, TeamEconomy, DEMOTRACER_ABI, DTR_FORMAT_VERSION,
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
            let (mut rec, stats) = synthesize_player_rec_with_row_refs(
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
            rec.high_fidelity = build_player_high_fidelity_metadata(
                parsed,
                round.round,
                recording_start_tick,
                end_tick,
                &player_rows,
                round_rows,
            );
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
                hifi_event_count: rec.high_fidelity.events.len(),
                inventory_snapshot_count: rec.high_fidelity.inventory_snapshots.len(),
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

#[derive(Clone, Debug)]
struct AbsoluteHifiEvent {
    tick: i32,
    kind: ReplayHifiEventKind,
    actor_steam_id: Option<u64>,
    target_steam_id: Option<u64>,
    weapon_def_index: Option<i32>,
    item_name: Option<String>,
    entity_id: Option<i32>,
    actor_count_after: Option<u32>,
    target_count_after: Option<u32>,
    damage: Option<i32>,
    health: Option<i32>,
}

#[derive(Clone, Debug)]
struct InventoryChange {
    tick: i32,
    steam_id: u64,
    weapon_def_index: i32,
    count_after: u32,
    item_name: Option<String>,
    paired_steam_id: Option<u64>,
}

impl AbsoluteHifiEvent {
    fn with_tick_index(self, player_rows: &[&ParsedPlayerTick]) -> Option<ReplayHifiEvent> {
        let tick_index = tick_index_for_event(player_rows, self.tick)?;
        Some(ReplayHifiEvent {
            tick_index,
            tick: self.tick,
            kind: self.kind,
            actor_steam_id: self.actor_steam_id,
            target_steam_id: self.target_steam_id,
            weapon_def_index: self.weapon_def_index,
            item_name: self.item_name,
            entity_id: self.entity_id,
            actor_count_after: self.actor_count_after,
            target_count_after: self.target_count_after,
            damage: self.damage,
            health: self.health,
        })
    }
}

fn build_player_high_fidelity_metadata(
    parsed: &ParsedDemo,
    _round: u32,
    start_tick: i32,
    end_tick: i32,
    player_rows: &[&ParsedPlayerTick],
    round_rows: &[&ParsedPlayerTick],
) -> HighFidelityMetadata {
    let steam_id = player_rows
        .first()
        .map(|row| row.steam_id)
        .unwrap_or_default();
    if steam_id == 0 {
        return HighFidelityMetadata::default();
    }

    let mut events = player_scoped_hifi_events(parsed, steam_id, start_tick, end_tick, round_rows)
        .into_iter()
        .filter_map(|event| event.with_tick_index(player_rows))
        .collect::<Vec<_>>();
    events.sort_by_key(|event| (event.tick_index, event.tick, hifi_event_rank(event.kind)));
    events.dedup_by(|lhs, rhs| {
        lhs.tick_index == rhs.tick_index
            && lhs.tick == rhs.tick
            && lhs.kind == rhs.kind
            && lhs.actor_steam_id == rhs.actor_steam_id
            && lhs.target_steam_id == rhs.target_steam_id
            && lhs.weapon_def_index == rhs.weapon_def_index
            && lhs.entity_id == rhs.entity_id
    });

    HighFidelityMetadata::new(events, inventory_snapshots_for_player(player_rows))
}

fn player_scoped_hifi_events(
    parsed: &ParsedDemo,
    steam_id: u64,
    start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
) -> Vec<AbsoluteHifiEvent> {
    let mut drops = inferred_inventory_drops(parsed, start_tick, end_tick, round_rows);
    let mut pickups = inferred_inventory_pickups(start_tick, end_tick, round_rows, &parsed.events);
    pair_inventory_transfers(&mut drops, &mut pickups);

    let mut events = Vec::new();
    for drop in drops.into_iter().filter(|drop| drop.steam_id == steam_id) {
        events.push(AbsoluteHifiEvent {
            tick: drop.tick,
            kind: ReplayHifiEventKind::ItemDrop,
            actor_steam_id: Some(drop.steam_id),
            target_steam_id: drop.paired_steam_id,
            weapon_def_index: Some(drop.weapon_def_index),
            item_name: drop.item_name,
            entity_id: None,
            actor_count_after: Some(drop.count_after),
            target_count_after: None,
            damage: None,
            health: None,
        });
    }
    for pickup in pickups
        .into_iter()
        .filter(|pickup| pickup.steam_id == steam_id)
    {
        events.push(AbsoluteHifiEvent {
            tick: pickup.tick,
            kind: if pickup.paired_steam_id.is_some() {
                ReplayHifiEventKind::ItemTransfer
            } else {
                ReplayHifiEventKind::ItemPickup
            },
            actor_steam_id: pickup.paired_steam_id,
            target_steam_id: Some(pickup.steam_id),
            weapon_def_index: Some(pickup.weapon_def_index),
            item_name: pickup.item_name,
            entity_id: None,
            actor_count_after: None,
            target_count_after: Some(pickup.count_after),
            damage: None,
            health: None,
        });
    }

    for event in &parsed.events {
        if event.tick < start_tick || event.tick > end_tick {
            continue;
        }
        if let Some(mapped) = map_recorded_game_event(event, steam_id) {
            events.push(mapped);
        }
    }

    events
}

fn map_recorded_game_event(event: &ParsedGameEvent, steam_id: u64) -> Option<AbsoluteHifiEvent> {
    let kind = match event.name.as_str() {
        "bomb_dropped" => ReplayHifiEventKind::BombDrop,
        "bomb_pickup" => ReplayHifiEventKind::BombPickup,
        "bomb_beginplant" => ReplayHifiEventKind::BombBeginplant,
        "bomb_planted" => ReplayHifiEventKind::BombPlanted,
        "weapon_fire" => ReplayHifiEventKind::WeaponFire,
        "player_hurt" => ReplayHifiEventKind::PlayerHurt,
        "player_death" => ReplayHifiEventKind::PlayerDeath,
        _ => return None,
    };
    let actor_steam_id = match kind {
        ReplayHifiEventKind::PlayerHurt | ReplayHifiEventKind::PlayerDeath => {
            event.attacker_steam_id.or(event.user_steam_id)
        }
        _ => event.user_steam_id.or(event.attacker_steam_id),
    };
    let target_steam_id = match kind {
        ReplayHifiEventKind::BombPickup => event.user_steam_id,
        _ => event.victim_steam_id,
    };
    if actor_steam_id != Some(steam_id) && target_steam_id != Some(steam_id) {
        return None;
    }

    let weapon_def_index = event
        .weapon_def_index
        .map(normalize_weapon_def_index)
        .or_else(|| {
            event
                .item_name
                .as_deref()
                .and_then(weapon_def_index_from_item_name)
        })
        .or(match kind {
            ReplayHifiEventKind::BombDrop
            | ReplayHifiEventKind::BombPickup
            | ReplayHifiEventKind::BombBeginplant
            | ReplayHifiEventKind::BombPlanted => Some(49),
            _ => None,
        });

    Some(AbsoluteHifiEvent {
        tick: event.tick,
        kind,
        actor_steam_id,
        target_steam_id,
        weapon_def_index,
        item_name: weapon_def_index
            .and_then(weapon_item_name)
            .map(str::to_string)
            .or_else(|| event.item_name.clone()),
        entity_id: event.entity_id,
        actor_count_after: None,
        target_count_after: None,
        damage: event.damage,
        health: event.health,
    })
}

fn inferred_inventory_drops(
    parsed: &ParsedDemo,
    start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
) -> Vec<InventoryChange> {
    let mut changes = Vec::new();
    for rows in live_rows_by_player(round_rows).into_values() {
        for pair in rows.windows(2) {
            let prev = pair[0];
            let current = pair[1];
            if current.tick < start_tick || current.tick > end_tick {
                continue;
            }
            let prev_counts = inventory_counts(prev);
            let current_counts = inventory_counts(current);
            for (def, prev_count) in prev_counts {
                if !is_replay_equipment_event_def(def) {
                    continue;
                }
                let current_count = current_counts.get(&def).copied().unwrap_or(0);
                if current_count >= prev_count {
                    continue;
                }
                if projectile_consumed_inventory(parsed, current.steam_id, def, current.tick) {
                    continue;
                }
                changes.push(InventoryChange {
                    tick: current.tick,
                    steam_id: current.steam_id,
                    weapon_def_index: def,
                    count_after: current_count,
                    item_name: weapon_item_name(def).map(str::to_string),
                    paired_steam_id: None,
                });
            }
        }
    }
    changes.sort_by_key(|change| (change.tick, change.steam_id, change.weapon_def_index));
    changes
}

fn inferred_inventory_pickups(
    start_tick: i32,
    end_tick: i32,
    round_rows: &[&ParsedPlayerTick],
    game_events: &[ParsedGameEvent],
) -> Vec<InventoryChange> {
    let by_player = live_rows_by_player(round_rows);
    let mut changes = Vec::new();
    let mut seen = BTreeSet::new();

    for event in game_events {
        if event.tick < start_tick || event.tick > end_tick || event.name != "item_pickup" {
            continue;
        }
        let Some(steam_id) = event.user_steam_id else {
            continue;
        };
        let Some(def) = event
            .weapon_def_index
            .map(normalize_weapon_def_index)
            .or_else(|| {
                event
                    .item_name
                    .as_deref()
                    .and_then(weapon_def_index_from_item_name)
            })
        else {
            continue;
        };
        if !is_replay_equipment_event_def(def) {
            continue;
        }
        if !seen.insert((event.tick, steam_id, def)) {
            continue;
        }
        let count_after = by_player
            .get(&steam_id)
            .and_then(|rows| inventory_count_at_or_after(rows, event.tick, def))
            .unwrap_or(1);
        changes.push(InventoryChange {
            tick: event.tick,
            steam_id,
            weapon_def_index: def,
            count_after,
            item_name: weapon_item_name(def)
                .map(str::to_string)
                .or_else(|| event.item_name.clone()),
            paired_steam_id: None,
        });
    }

    for rows in by_player.into_values() {
        for pair in rows.windows(2) {
            let prev = pair[0];
            let current = pair[1];
            if current.tick < start_tick || current.tick > end_tick {
                continue;
            }
            let prev_counts = inventory_counts(prev);
            let current_counts = inventory_counts(current);
            for (def, current_count) in current_counts {
                if !is_replay_equipment_event_def(def) {
                    continue;
                }
                let prev_count = prev_counts.get(&def).copied().unwrap_or(0);
                if current_count <= prev_count {
                    continue;
                }
                if !seen.insert((current.tick, current.steam_id, def)) {
                    continue;
                }
                changes.push(InventoryChange {
                    tick: current.tick,
                    steam_id: current.steam_id,
                    weapon_def_index: def,
                    count_after: current_count,
                    item_name: weapon_item_name(def).map(str::to_string),
                    paired_steam_id: None,
                });
            }
        }
    }

    changes.sort_by_key(|change| (change.tick, change.steam_id, change.weapon_def_index));
    changes
}

fn pair_inventory_transfers(drops: &mut [InventoryChange], pickups: &mut [InventoryChange]) {
    const TRANSFER_PAIR_TICKS: i32 = 128;
    for drop in drops.iter_mut() {
        let mut best_pickup = None;
        let mut best_delta = i32::MAX;
        for (index, pickup) in pickups.iter().enumerate() {
            if pickup.paired_steam_id.is_some()
                || pickup.steam_id == drop.steam_id
                || pickup.weapon_def_index != drop.weapon_def_index
                || pickup.tick < drop.tick
            {
                continue;
            }
            let delta = pickup.tick - drop.tick;
            if delta <= TRANSFER_PAIR_TICKS && delta < best_delta {
                best_pickup = Some(index);
                best_delta = delta;
            }
        }
        if let Some(index) = best_pickup {
            drop.paired_steam_id = Some(pickups[index].steam_id);
            pickups[index].paired_steam_id = Some(drop.steam_id);
        }
    }
}

fn inventory_snapshots_for_player(
    player_rows: &[&ParsedPlayerTick],
) -> Vec<ReplayInventorySnapshot> {
    let mut snapshots = Vec::new();
    let mut previous_counts: Option<Vec<ReplayInventoryItemCount>> = None;
    for (row_index, row) in player_rows.iter().enumerate() {
        let counts = inventory_counts(row)
            .into_iter()
            .map(|(weapon_def_index, count)| ReplayInventoryItemCount {
                weapon_def_index,
                count,
            })
            .collect::<Vec<_>>();
        if previous_counts.as_ref() == Some(&counts) && row_index != 0 {
            continue;
        }
        previous_counts = Some(counts.clone());
        let Some(tick_index) = tick_index_for_event(player_rows, row.tick) else {
            continue;
        };
        snapshots.push(ReplayInventorySnapshot {
            tick_index,
            tick: row.tick,
            steam_id: row.steam_id,
            weapon_def_counts: counts,
            active_weapon_def_index: normalize_weapon_def_index(row.item_def_idx),
            armor_value: row.armor_value,
            has_helmet: row.has_helmet,
            has_defuser: row.has_defuser,
        });
    }
    snapshots
}

fn live_rows_by_player<'a>(
    round_rows: &[&'a ParsedPlayerTick],
) -> BTreeMap<u64, Vec<&'a ParsedPlayerTick>> {
    let mut by_player: BTreeMap<u64, Vec<&ParsedPlayerTick>> = BTreeMap::new();
    for &row in round_rows {
        if row.steam_id == 0 || !row.is_alive {
            continue;
        }
        by_player.entry(row.steam_id).or_default().push(row);
    }
    for rows in by_player.values_mut() {
        rows.sort_by_key(|row| row.tick);
        rows.dedup_by_key(|row| row.tick);
    }
    by_player
}

fn inventory_counts(row: &ParsedPlayerTick) -> BTreeMap<i32, u32> {
    let mut counts = BTreeMap::new();
    for raw_def in &row.inventory_as_ids {
        let def = normalize_weapon_def_index(*raw_def);
        if !is_known_weapon_def_index(def) {
            continue;
        }
        *counts.entry(def).or_insert(0) += 1;
    }
    counts
}

fn inventory_count_at_or_after(rows: &[&ParsedPlayerTick], tick: i32, def: i32) -> Option<u32> {
    rows.iter()
        .find(|row| row.tick >= tick)
        .map(|row| inventory_counts(row).get(&def).copied().unwrap_or(0))
}

fn projectile_consumed_inventory(
    parsed: &ParsedDemo,
    steam_id: u64,
    weapon_def_index: i32,
    tick: i32,
) -> bool {
    parsed.projectiles.iter().any(|projectile| {
        projectile.steam_id == steam_id
            && normalize_weapon_def_index(projectile.weapon_def_index) == weapon_def_index
            && (projectile.tick - tick).abs() <= 16
    })
}

fn tick_index_for_event(player_rows: &[&ParsedPlayerTick], tick: i32) -> Option<u32> {
    if player_rows.len() < 2 {
        return None;
    }
    let max_index = player_rows.len().saturating_sub(2);
    let index = player_rows
        .iter()
        .take(player_rows.len().saturating_sub(1))
        .position(|row| row.tick >= tick)
        .unwrap_or(max_index)
        .min(max_index);
    Some(index as u32)
}

fn hifi_event_rank(kind: ReplayHifiEventKind) -> u8 {
    match kind {
        ReplayHifiEventKind::RoundStart => 0,
        ReplayHifiEventKind::RoundFreezeEnd => 1,
        ReplayHifiEventKind::BombDrop => 2,
        ReplayHifiEventKind::ItemDrop => 3,
        ReplayHifiEventKind::BombPickup => 4,
        ReplayHifiEventKind::ItemPickup => 5,
        ReplayHifiEventKind::ItemTransfer => 6,
        ReplayHifiEventKind::BombBeginplant => 7,
        ReplayHifiEventKind::BombPlanted => 8,
        ReplayHifiEventKind::WeaponFire => 9,
        ReplayHifiEventKind::PlayerHurt => 10,
        ReplayHifiEventKind::PlayerDeath => 11,
    }
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

fn is_replay_equipment_event_def(def: i32) -> bool {
    matches!(normalize_weapon_def_index(def), 43 | 44 | 45 | 46 | 47 | 48)
}

fn is_preload_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 31 | 42 | 49)
}

fn is_loadout_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 42 | 49)
}

fn weapon_def_index_from_item_name(item_name: &str) -> Option<i32> {
    match normalize_item_event_name(item_name).as_str() {
        "weapon_deagle" => Some(1),
        "weapon_elite" => Some(2),
        "weapon_fiveseven" => Some(3),
        "weapon_glock" => Some(4),
        "weapon_ak47" => Some(7),
        "weapon_aug" => Some(8),
        "weapon_awp" => Some(9),
        "weapon_famas" => Some(10),
        "weapon_g3sg1" => Some(11),
        "weapon_galilar" => Some(13),
        "weapon_m249" => Some(14),
        "weapon_m4a1" => Some(16),
        "weapon_mac10" => Some(17),
        "weapon_p90" => Some(19),
        "weapon_mp5sd" => Some(23),
        "weapon_ump45" => Some(24),
        "weapon_xm1014" => Some(25),
        "weapon_bizon" => Some(26),
        "weapon_mag7" => Some(27),
        "weapon_negev" => Some(28),
        "weapon_sawedoff" => Some(29),
        "weapon_tec9" => Some(30),
        "weapon_taser" => Some(31),
        "weapon_hkp2000" => Some(32),
        "weapon_mp7" => Some(33),
        "weapon_mp9" => Some(34),
        "weapon_nova" => Some(35),
        "weapon_p250" => Some(36),
        "weapon_scar20" => Some(38),
        "weapon_sg556" => Some(39),
        "weapon_ssg08" => Some(40),
        "weapon_knife" => Some(42),
        "weapon_flashbang" => Some(43),
        "weapon_hegrenade" => Some(44),
        "weapon_smokegrenade" => Some(45),
        "weapon_molotov" => Some(46),
        "weapon_decoy" => Some(47),
        "weapon_incgrenade" => Some(48),
        "weapon_c4" => Some(49),
        "weapon_m4a1_silencer" => Some(60),
        "weapon_usp_silencer" => Some(61),
        "weapon_cz75a" => Some(63),
        "weapon_revolver" => Some(64),
        _ => None,
    }
}

fn weapon_item_name(def: i32) -> Option<&'static str> {
    match normalize_weapon_def_index(def) {
        1 => Some("weapon_deagle"),
        2 => Some("weapon_elite"),
        3 => Some("weapon_fiveseven"),
        4 => Some("weapon_glock"),
        7 => Some("weapon_ak47"),
        8 => Some("weapon_aug"),
        9 => Some("weapon_awp"),
        10 => Some("weapon_famas"),
        11 => Some("weapon_g3sg1"),
        13 => Some("weapon_galilar"),
        14 => Some("weapon_m249"),
        16 => Some("weapon_m4a1"),
        17 => Some("weapon_mac10"),
        19 => Some("weapon_p90"),
        23 => Some("weapon_mp5sd"),
        24 => Some("weapon_ump45"),
        25 => Some("weapon_xm1014"),
        26 => Some("weapon_bizon"),
        27 => Some("weapon_mag7"),
        28 => Some("weapon_negev"),
        29 => Some("weapon_sawedoff"),
        30 => Some("weapon_tec9"),
        31 => Some("weapon_taser"),
        32 => Some("weapon_hkp2000"),
        33 => Some("weapon_mp7"),
        34 => Some("weapon_mp9"),
        35 => Some("weapon_nova"),
        36 => Some("weapon_p250"),
        38 => Some("weapon_scar20"),
        39 => Some("weapon_sg556"),
        40 => Some("weapon_ssg08"),
        42 => Some("weapon_knife"),
        43 => Some("weapon_flashbang"),
        44 => Some("weapon_hegrenade"),
        45 => Some("weapon_smokegrenade"),
        46 => Some("weapon_molotov"),
        47 => Some("weapon_decoy"),
        48 => Some("weapon_incgrenade"),
        49 => Some("weapon_c4"),
        60 => Some("weapon_m4a1_silencer"),
        61 => Some("weapon_usp_silencer"),
        63 => Some("weapon_cz75a"),
        64 => Some("weapon_revolver"),
        _ => None,
    }
}

fn normalize_item_event_name(item_name: &str) -> String {
    let lower = item_name.trim().to_ascii_lowercase();
    match lower.as_str() {
        "decoy_grenade" | "weapon_decoy_grenade" => "weapon_decoy".to_string(),
        "c4" | "weapon_c4_explosive" => "weapon_c4".to_string(),
        value if value.starts_with("weapon_") => value.to_string(),
        value => format!("weapon_{value}"),
    }
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

    #[test]
    fn grenade_throw_inventory_loss_is_not_item_drop() {
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, 76561198000000001, "alpha", vec![7, 45]),
            row_with_inventory(116, 76561198000000001, "alpha", vec![7]),
        ];
        parsed.projectiles = vec![crate::model::ParsedProjectile {
            tick: 116,
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

        let rec = rec_for_steam(&export_memory(parsed), 76561198000000001);

        assert!(rec.high_fidelity.events.iter().all(|event| {
            !matches!(
                event.kind,
                ReplayHifiEventKind::ItemDrop
                    | ReplayHifiEventKind::ItemPickup
                    | ReplayHifiEventKind::ItemTransfer
            )
        }));
    }

    #[test]
    fn primary_weapon_inventory_loss_is_not_item_drop() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, steam_id, "alpha", vec![7, 45]),
            row_with_inventory(116, steam_id, "alpha", vec![45]),
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert!(rec.high_fidelity.events.iter().all(|event| {
            !matches!(
                event.kind,
                ReplayHifiEventKind::ItemDrop
                    | ReplayHifiEventKind::ItemPickup
                    | ReplayHifiEventKind::ItemTransfer
            )
        }));
    }

    #[test]
    fn smoke_transfer_generates_source_drop_and_target_transfer() {
        let source = 76561198000000001;
        let target = 76561198000000002;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, source, "magixx", vec![45]),
            row_with_inventory(112, source, "magixx", vec![]),
            row_with_inventory(100, target, "sh1ro", vec![]),
            row_with_inventory(120, target, "sh1ro", vec![45]),
        ];
        parsed.events = vec![ParsedGameEvent {
            tick: 120,
            name: "item_pickup".to_string(),
            user_steam_id: Some(target),
            weapon_def_index: Some(45),
            item_name: Some("smokegrenade".to_string()),
            ..ParsedGameEvent::default()
        }];

        let memory = export_memory(parsed);
        let source_rec = rec_for_steam(&memory, source);
        let target_rec = rec_for_steam(&memory, target);

        let drop = source_rec
            .high_fidelity
            .events
            .iter()
            .find(|event| event.kind == ReplayHifiEventKind::ItemDrop)
            .unwrap();
        assert_eq!(drop.actor_steam_id, Some(source));
        assert_eq!(drop.target_steam_id, Some(target));
        assert_eq!(drop.weapon_def_index, Some(45));
        assert_eq!(drop.actor_count_after, Some(0));

        let transfer = target_rec
            .high_fidelity
            .events
            .iter()
            .find(|event| event.kind == ReplayHifiEventKind::ItemTransfer)
            .unwrap();
        assert_eq!(transfer.actor_steam_id, Some(source));
        assert_eq!(transfer.target_steam_id, Some(target));
        assert_eq!(transfer.weapon_def_index, Some(45));
        assert_eq!(transfer.target_count_after, Some(1));
    }

    #[test]
    fn bomb_events_are_recorded_from_game_events() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, steam_id, "alpha", vec![7, 49]),
            row_with_inventory(128, steam_id, "alpha", vec![7, 49]),
        ];
        parsed.events = vec![
            ParsedGameEvent {
                tick: 110,
                name: "bomb_beginplant".to_string(),
                user_steam_id: Some(steam_id),
                ..ParsedGameEvent::default()
            },
            ParsedGameEvent {
                tick: 124,
                name: "bomb_planted".to_string(),
                user_steam_id: Some(steam_id),
                ..ParsedGameEvent::default()
            },
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert!(rec
            .high_fidelity
            .events
            .iter()
            .any(|event| event.kind == ReplayHifiEventKind::BombBeginplant
                && event.weapon_def_index == Some(49)));
        assert!(rec
            .high_fidelity
            .events
            .iter()
            .any(|event| event.kind == ReplayHifiEventKind::BombPlanted
                && event.weapon_def_index == Some(49)));
    }

    #[test]
    fn inventory_snapshots_are_written_only_when_inventory_changes() {
        let steam_id = 76561198000000001;
        let mut parsed = sample_demo();
        parsed.rows = vec![
            row_with_inventory(100, steam_id, "alpha", vec![7]),
            row_with_inventory(110, steam_id, "alpha", vec![7]),
            row_with_inventory(120, steam_id, "alpha", vec![7, 45]),
        ];

        let rec = rec_for_steam(&export_memory(parsed), steam_id);

        assert_eq!(rec.high_fidelity.inventory_snapshots.len(), 2);
        assert_eq!(rec.high_fidelity.inventory_snapshots[0].tick, 100);
        assert_eq!(rec.high_fidelity.inventory_snapshots[1].tick, 120);
        assert_eq!(
            rec.high_fidelity.inventory_snapshots[1].weapon_def_counts,
            vec![
                ReplayInventoryItemCount {
                    weapon_def_index: 7,
                    count: 1,
                },
                ReplayInventoryItemCount {
                    weapon_def_index: 45,
                    count: 1,
                },
            ]
        );
    }

    fn export_memory(parsed: ParsedDemo) -> MemoryConversionReport {
        export_demo_to_memory(
            &parsed,
            &ConvertMemoryOptions {
                output_stem: Some("hifi-demo".to_string()),
                side: Side::Both,
                selected_rounds: Some(BTreeSet::from([1])),
                include_suspicious: true,
                cut_before_bomb_plant: false,
                subtick_mode: SubtickMode::Auto,
                freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
                analysis: AnalysisOptions::default(),
            },
        )
        .unwrap()
    }

    fn rec_for_steam(memory: &MemoryConversionReport, steam_id: u64) -> Cs2Rec {
        let dtr = memory
            .artifacts
            .iter()
            .find(|artifact| artifact.steam_id == Some(steam_id))
            .unwrap();
        read_rec(&mut &dtr.bytes[..]).unwrap()
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
            events: Vec::new(),
        }
    }

    fn row_with_inventory(
        tick: i32,
        steam_id: u64,
        name: &str,
        inventory_as_ids: Vec<i32>,
    ) -> ParsedPlayerTick {
        ParsedPlayerTick {
            steam_id,
            name: name.to_string(),
            inventory_as_ids,
            item_def_idx: 7,
            ..sample_row(tick)
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
