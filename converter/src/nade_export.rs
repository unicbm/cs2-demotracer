use crate::demo_id::{demo_id, sha256_hex};
use crate::export::{first_weapon_def_index, preload_weapon_def_indices_from_refs, replay_loadout};
use crate::model::{
    public_demo_path, ParsedDemo, ParsedPlayerTick, ParsedProjectile, ProjectileEffectSource,
    ProjectileKind, ReplayLoadout, ReplayProjectile, Side, SubtickMode, DEMOTRACER_ABI,
    DTR_FORMAT_VERSION,
};
use crate::rec_writer::write_rec_file;
use crate::synthesis::{synthesize_player_rec_with_row_refs, SynthesisOptions, SynthesisStats};
use crate::{io_error, Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

pub const NADE_MANIFEST_FORMAT_VERSION: u32 = 1;
pub const DEFAULT_PRE_ROLL_SECONDS: f32 = 1.0;
pub const DEFAULT_POST_ROLL_SECONDS: f32 = 0.5;
pub const DEFAULT_OPENING_SECONDS: f32 = 20.0;
const NADE_MANIFEST_BROTLI_BUFFER_SIZE: usize = 4096;
const NADE_MANIFEST_BROTLI_QUALITY: u32 = 6;
const NADE_MANIFEST_BROTLI_LGWIN: u32 = 22;

#[derive(Clone, Debug)]
pub struct NadeExportOptions {
    pub output_dir: PathBuf,
    pub output_stem: Option<String>,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub pre_roll_seconds: f32,
    pub post_roll_seconds: f32,
    pub opening_seconds: f32,
    pub subtick_mode: SubtickMode,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeExportReport {
    pub root: PathBuf,
    pub manifest_path: PathBuf,
    pub clips_written: usize,
    pub skipped: usize,
    pub manifest: NadeManifest,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeManifest {
    pub format_version: u32,
    pub demo_path: String,
    pub demo_id: String,
    pub demo_sha256: String,
    pub map: String,
    pub tick_rate: f32,
    pub abi: i32,
    pub dtr_format_version: u32,
    pub coordinate_mode: String,
    pub pre_roll_seconds: f32,
    pub post_roll_seconds: f32,
    #[serde(default)]
    pub opening_seconds: f32,
    pub clips: Vec<NadeClip>,
    pub skipped: Vec<NadeSkip>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeClip {
    pub clip_id: String,
    pub path: String,
    pub kind: ProjectileKind,
    pub grenade_type: String,
    pub weapon_def_index: i32,
    pub phase: NadePhase,
    pub round: u32,
    pub side: String,
    pub steam_id: u64,
    pub player_name: String,
    pub throw_tick: i32,
    pub clip_start_tick: i32,
    pub clip_end_tick: i32,
    pub release_tick_index: u32,
    pub start_origin: [f32; 3],
    pub start_yaw: f32,
    pub projectile_initial_position: [f32; 3],
    pub projectile_initial_velocity: [f32; 3],
    pub projectile_detonation_position: [f32; 3],
    #[serde(default)]
    pub projectile_effect_position: [f32; 3],
    #[serde(default)]
    pub projectile_effect_tick: Option<i32>,
    #[serde(default)]
    pub projectile_effect_source: ProjectileEffectSource,
    #[serde(default)]
    pub projectile_effect_confidence: f32,
    pub first_weapon_def_index: i32,
    pub preload_weapon_def_indices: Vec<i32>,
    pub loadout: ReplayLoadout,
    #[serde(default)]
    pub timing: NadeTiming,
    pub source_context: NadeSourceContext,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct NadeTiming {
    pub round: u32,
    pub throw_tick: i32,
    pub freeze_end_tick: Option<i32>,
    pub round_live_tick: Option<i32>,
    pub bomb_planted_tick: Option<i32>,
    pub seconds_after_freeze_end: Option<f32>,
    pub seconds_after_round_live: Option<f32>,
    pub seconds_after_bomb_planted: Option<f32>,
    pub time_bucket: NadeTimeBucket,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum NadeTimeBucket {
    #[default]
    Unknown,
    SpawnExec,
    Opening,
    Midround,
    Retake,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeSourceContext {
    pub source_tick_rate: f32,
    pub rows: usize,
    pub ticks: usize,
    pub subticks: usize,
    pub release_game_time: Option<f32>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeSkip {
    pub tick: i32,
    pub steam_id: u64,
    pub kind: ProjectileKind,
    pub grenade_type: String,
    pub reason: String,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum NadePhase {
    Opening,
    Combat,
    Retake,
}

impl NadePhase {
    fn as_dir(self) -> &'static str {
        match self {
            Self::Opening => "opening",
            Self::Combat => "combat",
            Self::Retake => "retake",
        }
    }
}

pub fn export_nade_clips(
    parsed: &ParsedDemo,
    options: &NadeExportOptions,
) -> Result<NadeExportReport> {
    validate_options(options)?;
    let output_stem = options
        .output_stem
        .clone()
        .unwrap_or_else(|| demo_id(&parsed.stem, &parsed.demo_sha256));
    let root = options.output_dir.join(&output_stem);
    fs::create_dir_all(&root).map_err(|e| io_error(&root, e))?;

    let round_bounds = round_bounds(parsed);
    let mut manifest = NadeManifest {
        format_version: NADE_MANIFEST_FORMAT_VERSION,
        demo_path: public_demo_path(&parsed.path),
        demo_id: output_stem,
        demo_sha256: parsed.demo_sha256.clone(),
        map: parsed.map.clone(),
        tick_rate: parsed.tick_rate,
        abi: DEMOTRACER_ABI,
        dtr_format_version: DTR_FORMAT_VERSION,
        coordinate_mode: "map_absolute".to_string(),
        pre_roll_seconds: options.pre_roll_seconds,
        post_roll_seconds: options.post_roll_seconds,
        opening_seconds: options.opening_seconds,
        clips: Vec::new(),
        skipped: Vec::new(),
    };
    let mut log = Vec::new();
    let mut subtick_stats = SynthesisStats::default();
    log.push(format!(
        "demo={} id={} sha256={} map={} tick_rate={:.3}",
        parsed.path, manifest.demo_id, parsed.demo_sha256, parsed.map, parsed.tick_rate
    ));

    for projectile in &parsed.projectiles {
        match build_clip(parsed, projectile, options, &round_bounds, &root) {
            Ok(Some((clip, stats))) => {
                subtick_stats.add_assign(&stats);
                log.push(format!(
                    "clip {} round={} side={} phase={:?} kind={:?} tick={} path={}",
                    clip.clip_id,
                    clip.round,
                    clip.side,
                    clip.phase,
                    clip.kind,
                    clip.throw_tick,
                    clip.path
                ));
                manifest.clips.push(clip);
            }
            Ok(None) => {}
            Err(err) => {
                let skip = NadeSkip {
                    tick: projectile.tick,
                    steam_id: projectile.steam_id,
                    kind: projectile.kind,
                    grenade_type: projectile.grenade_type.clone(),
                    reason: err.to_string(),
                };
                log.push(format!(
                    "skip tick={} steam_id={} kind={:?}: {}",
                    skip.tick, skip.steam_id, skip.kind, skip.reason
                ));
                manifest.skipped.push(skip);
            }
        }
    }

    manifest.clips.sort_by(|a, b| a.clip_id.cmp(&b.clip_id));
    let manifest_path = root.join("nade_manifest.json");
    let manifest_json = serde_json::to_string_pretty(&manifest)?;
    fs::write(&manifest_path, manifest_json.as_bytes()).map_err(|e| io_error(&manifest_path, e))?;
    let compressed_manifest_path = root.join("nade_manifest.json.br");
    write_brotli_file(&compressed_manifest_path, manifest_json.as_bytes())?;

    log.push(format!("clips_written={}", manifest.clips.len()));
    log.push(format!("skipped={}", manifest.skipped.len()));
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
    let log_path = root.join("nade_conversion.log");
    fs::write(&log_path, log.join("\n")).map_err(|e| io_error(&log_path, e))?;

    Ok(NadeExportReport {
        root,
        manifest_path,
        clips_written: manifest.clips.len(),
        skipped: manifest.skipped.len(),
        manifest,
    })
}

fn write_brotli_file(path: &Path, bytes: &[u8]) -> Result<()> {
    let file = fs::File::create(path).map_err(|e| io_error(path, e))?;
    let mut writer = brotli::CompressorWriter::new(
        file,
        NADE_MANIFEST_BROTLI_BUFFER_SIZE,
        NADE_MANIFEST_BROTLI_QUALITY,
        NADE_MANIFEST_BROTLI_LGWIN,
    );
    writer.write_all(bytes).map_err(|e| io_error(path, e))?;
    writer.flush().map_err(|e| io_error(path, e))?;
    Ok(())
}

fn build_clip(
    parsed: &ParsedDemo,
    projectile: &ParsedProjectile,
    options: &NadeExportOptions,
    round_bounds: &BTreeMap<u32, (i32, i32)>,
    root: &Path,
) -> Result<Option<(NadeClip, SynthesisStats)>> {
    if projectile.kind == ProjectileKind::Unknown || projectile.weapon_def_index < 0 {
        return Err(Error::InvalidDemo("unknown grenade kind".to_string()));
    }
    if !vec3_is_meaningful(projectile.initial_position) {
        return Err(Error::InvalidDemo(
            "projectile initial position missing".to_string(),
        ));
    }
    if !vec3_is_meaningful(projectile.initial_velocity) {
        return Err(Error::InvalidDemo(
            "projectile initial velocity missing".to_string(),
        ));
    }

    let Some(release_row) = find_release_row(parsed, projectile) else {
        return Err(Error::InvalidDemo(
            "matching thrower row not found".to_string(),
        ));
    };
    if !options.side.matches_team(release_row.team_num) {
        return Ok(None);
    }
    if let Some(rounds) = &options.selected_rounds {
        if !rounds.contains(&release_row.round) {
            return Ok(None);
        }
    }

    let pre_ticks = seconds_to_ticks(options.pre_roll_seconds, parsed.tick_rate);
    let post_ticks = seconds_to_ticks(options.post_roll_seconds, parsed.tick_rate);
    let clip_start_tick = projectile.tick.saturating_sub(pre_ticks);
    let clip_end_tick = projectile.tick.saturating_add(post_ticks);
    let mut player_rows = parsed
        .rows
        .iter()
        .filter(|row| {
            row.steam_id == projectile.steam_id
                && row.round == release_row.round
                && row.team_num == release_row.team_num
                && row.tick >= clip_start_tick
                && row.tick <= clip_end_tick
                && row.is_alive
        })
        .collect::<Vec<_>>();
    player_rows.sort_by_key(|row| row.tick);
    player_rows.dedup_by_key(|row| row.tick);
    if player_rows.len() < 2 {
        return Err(Error::InvalidDemo(format!(
            "clip has {} matching rows",
            player_rows.len()
        )));
    }

    let (mut rec, stats) = synthesize_player_rec_with_row_refs(
        &player_rows,
        &[],
        &parsed.map,
        parsed.tick_rate,
        release_row.round,
        SynthesisOptions {
            subtick_mode: options.subtick_mode,
            ..SynthesisOptions::default()
        },
    )?;
    let release_tick_index = release_tick_index(&player_rows, projectile.tick);
    let effective_weapon_def_index = effective_projectile_weapon_def_index(projectile, release_row);
    rec.projectiles = vec![ReplayProjectile {
        tick_index: release_tick_index,
        kind: projectile.kind,
        weapon_def_index: effective_weapon_def_index,
        initial_position: projectile.initial_position,
        initial_velocity: projectile.initial_velocity,
        detonation_position: projectile.detonation_position,
    }];

    let side = Side::team_dir(release_row.team_num).to_string();
    let phase = classify_phase(
        release_row.round,
        projectile.tick,
        parsed,
        round_bounds,
        options.opening_seconds,
    );
    let timing = build_timing(
        release_row.round,
        projectile.tick,
        parsed,
        round_bounds,
        options.opening_seconds,
    );
    let kind_dir = grenade_dir(effective_weapon_def_index, projectile.kind);
    let clip_id = clip_id(
        &parsed.demo_sha256,
        projectile,
        release_row,
        phase,
        effective_weapon_def_index,
    );
    let rel_path = Path::new("nades")
        .join(&side)
        .join(phase.as_dir())
        .join(kind_dir)
        .join(format!("{clip_id}.dtr"));
    let full_path = root.join(&rel_path);
    if let Some(parent) = full_path.parent() {
        fs::create_dir_all(parent).map_err(|e| io_error(parent, e))?;
    }
    write_rec_file(&full_path, &rec)?;

    let release_game_time = player_rows
        .iter()
        .min_by_key(|row| (row.tick - projectile.tick).abs())
        .and_then(|row| row.game_time);
    let clip = NadeClip {
        clip_id,
        path: rel_path.to_string_lossy().replace('\\', "/"),
        kind: projectile.kind,
        grenade_type: projectile.grenade_type.clone(),
        weapon_def_index: effective_weapon_def_index,
        phase,
        round: release_row.round,
        side,
        steam_id: projectile.steam_id,
        player_name: if release_row.name.is_empty() {
            projectile.steam_id.to_string()
        } else {
            release_row.name.clone()
        },
        throw_tick: projectile.tick,
        clip_start_tick: player_rows
            .first()
            .map(|row| row.tick)
            .unwrap_or(clip_start_tick),
        clip_end_tick: player_rows
            .last()
            .map(|row| row.tick)
            .unwrap_or(clip_end_tick),
        release_tick_index,
        start_origin: player_rows[0].origin,
        start_yaw: player_rows[0].yaw,
        projectile_initial_position: projectile.initial_position,
        projectile_initial_velocity: projectile.initial_velocity,
        projectile_detonation_position: projectile.detonation_position,
        projectile_effect_position: projectile.effect_position,
        projectile_effect_tick: projectile.effect_tick,
        projectile_effect_source: projectile.effect_source,
        projectile_effect_confidence: projectile.effect_confidence,
        first_weapon_def_index: first_weapon_def_index(&rec),
        preload_weapon_def_indices: preload_weapon_def_indices_from_refs(&player_rows, &rec),
        loadout: replay_loadout(player_rows[0]),
        timing,
        source_context: NadeSourceContext {
            source_tick_rate: parsed.tick_rate,
            rows: player_rows.len(),
            ticks: rec.ticks.len(),
            subticks: rec.subticks.len(),
            release_game_time,
        },
    };

    Ok(Some((clip, stats)))
}

fn validate_options(options: &NadeExportOptions) -> Result<()> {
    if !options.pre_roll_seconds.is_finite() || options.pre_roll_seconds < 0.0 {
        return Err(Error::InvalidDemo(
            "pre-roll must be a finite non-negative number".to_string(),
        ));
    }
    if !options.post_roll_seconds.is_finite() || options.post_roll_seconds < 0.0 {
        return Err(Error::InvalidDemo(
            "post-roll must be a finite non-negative number".to_string(),
        ));
    }
    if !options.opening_seconds.is_finite() || options.opening_seconds < 0.0 {
        return Err(Error::InvalidDemo(
            "opening-seconds must be a finite non-negative number".to_string(),
        ));
    }
    if options.pre_roll_seconds == 0.0 && options.post_roll_seconds == 0.0 {
        return Err(Error::InvalidDemo(
            "pre-roll and post-roll cannot both be zero".to_string(),
        ));
    }
    Ok(())
}

fn round_bounds(parsed: &ParsedDemo) -> BTreeMap<u32, (i32, i32)> {
    let mut bounds: BTreeMap<u32, (i32, i32)> = BTreeMap::new();
    for row in &parsed.rows {
        bounds
            .entry(row.round)
            .and_modify(|(min_tick, max_tick)| {
                *min_tick = (*min_tick).min(row.tick);
                *max_tick = (*max_tick).max(row.tick);
            })
            .or_insert((row.tick, row.tick));
    }
    bounds
}

fn classify_phase(
    round: u32,
    tick: i32,
    parsed: &ParsedDemo,
    round_bounds: &BTreeMap<u32, (i32, i32)>,
    opening_seconds: f32,
) -> NadePhase {
    let Some((min_tick, max_tick)) = round_bounds.get(&round).copied() else {
        return NadePhase::Combat;
    };
    let planted_tick = bomb_planted_tick_for_round(parsed, min_tick, max_tick);
    match planted_tick {
        Some(plant_tick) if tick >= plant_tick => NadePhase::Retake,
        _ if is_opening_throw(round, tick, parsed, min_tick, max_tick, opening_seconds) => {
            NadePhase::Opening
        }
        _ => NadePhase::Combat,
    }
}

fn is_opening_throw(
    round: u32,
    tick: i32,
    parsed: &ParsedDemo,
    min_tick: i32,
    max_tick: i32,
    opening_seconds: f32,
) -> bool {
    if opening_seconds <= 0.0 || !opening_seconds.is_finite() {
        return false;
    }
    let live_start = round_live_tick_for_round(round, parsed, min_tick, max_tick);
    let opening_ticks = seconds_to_ticks(opening_seconds, parsed.tick_rate);
    tick < live_start.saturating_add(opening_ticks)
}

fn build_timing(
    round: u32,
    tick: i32,
    parsed: &ParsedDemo,
    round_bounds: &BTreeMap<u32, (i32, i32)>,
    opening_seconds: f32,
) -> NadeTiming {
    let Some((min_tick, max_tick)) = round_bounds.get(&round).copied() else {
        return NadeTiming {
            round,
            throw_tick: tick,
            ..NadeTiming::default()
        };
    };
    let freeze_end_tick = freeze_end_tick_for_round(parsed, min_tick, max_tick);
    let round_live_tick = Some(round_live_tick_for_round(round, parsed, min_tick, max_tick));
    let bomb_planted_tick = bomb_planted_tick_for_round(parsed, min_tick, max_tick);
    let seconds_after_freeze_end =
        freeze_end_tick.map(|anchor| ticks_to_seconds(tick - anchor, parsed.tick_rate));
    let seconds_after_round_live =
        round_live_tick.map(|anchor| ticks_to_seconds(tick - anchor, parsed.tick_rate));
    let seconds_after_bomb_planted =
        bomb_planted_tick.map(|anchor| ticks_to_seconds(tick - anchor, parsed.tick_rate));
    let time_bucket = classify_time_bucket(
        tick,
        opening_seconds,
        bomb_planted_tick,
        seconds_after_round_live,
    );

    NadeTiming {
        round,
        throw_tick: tick,
        freeze_end_tick,
        round_live_tick,
        bomb_planted_tick,
        seconds_after_freeze_end,
        seconds_after_round_live,
        seconds_after_bomb_planted,
        time_bucket,
    }
}

fn classify_time_bucket(
    tick: i32,
    opening_seconds: f32,
    bomb_planted_tick: Option<i32>,
    seconds_after_round_live: Option<f32>,
) -> NadeTimeBucket {
    if bomb_planted_tick.is_some_and(|plant_tick| tick >= plant_tick) {
        return NadeTimeBucket::Retake;
    }
    let Some(seconds_after_round_live) = seconds_after_round_live else {
        return NadeTimeBucket::Unknown;
    };
    if seconds_after_round_live <= 5.0 {
        NadeTimeBucket::SpawnExec
    } else if opening_seconds.is_finite()
        && opening_seconds > 0.0
        && seconds_after_round_live <= opening_seconds
    {
        NadeTimeBucket::Opening
    } else {
        NadeTimeBucket::Midround
    }
}

fn freeze_end_tick_for_round(parsed: &ParsedDemo, min_tick: i32, max_tick: i32) -> Option<i32> {
    parsed
        .round_freeze_end_ticks
        .iter()
        .copied()
        .filter(|freeze_end_tick| *freeze_end_tick >= min_tick && *freeze_end_tick <= max_tick)
        .min()
}

fn round_live_tick_for_round(round: u32, parsed: &ParsedDemo, min_tick: i32, max_tick: i32) -> i32 {
    freeze_end_tick_for_round(parsed, min_tick, max_tick).unwrap_or_else(|| {
        parsed
            .rows
            .iter()
            .filter(|row| row.round == round && row.round_in_progress)
            .map(|row| row.tick)
            .min()
            .unwrap_or(min_tick)
    })
}

fn bomb_planted_tick_for_round(parsed: &ParsedDemo, min_tick: i32, max_tick: i32) -> Option<i32> {
    parsed
        .bomb_planted_ticks
        .iter()
        .copied()
        .filter(|plant_tick| *plant_tick >= min_tick && *plant_tick <= max_tick)
        .min()
}

fn find_release_row<'a>(
    parsed: &'a ParsedDemo,
    projectile: &ParsedProjectile,
) -> Option<&'a ParsedPlayerTick> {
    let max_distance = seconds_to_ticks(1.0, parsed.tick_rate).max(1);
    parsed
        .rows
        .iter()
        .filter(|row| {
            row.steam_id == projectile.steam_id
                && row.is_alive
                && matches!(row.team_num, 2 | 3)
                && (row.tick - projectile.tick).abs() <= max_distance
        })
        .min_by_key(|row| {
            (
                (row.tick - projectile.tick).abs(),
                if row.tick <= projectile.tick { 0 } else { 1 },
            )
        })
}

fn release_tick_index(rows: &[&ParsedPlayerTick], throw_tick: i32) -> u32 {
    let tick_count = rows.len().saturating_sub(1);
    if tick_count == 0 {
        return 0;
    }
    rows.iter()
        .take(tick_count)
        .enumerate()
        .min_by_key(|(_, row)| {
            (
                (row.tick - throw_tick).abs(),
                if row.tick <= throw_tick { 0 } else { 1 },
            )
        })
        .map(|(index, _)| index as u32)
        .unwrap_or_default()
}

fn seconds_to_ticks(seconds: f32, tick_rate: f32) -> i32 {
    if !seconds.is_finite() || !tick_rate.is_finite() || tick_rate <= 0.0 {
        return 0;
    }
    (seconds * tick_rate).round().max(0.0) as i32
}

fn ticks_to_seconds(ticks: i32, tick_rate: f32) -> f32 {
    if !tick_rate.is_finite() || tick_rate <= 0.0 {
        return 0.0;
    }
    ticks as f32 / tick_rate
}

fn clip_id(
    demo_sha256: &str,
    projectile: &ParsedProjectile,
    release_row: &ParsedPlayerTick,
    phase: NadePhase,
    weapon_def_index: i32,
) -> String {
    let raw = format!(
        "{}:{}:{}:{}:{}:{}:{:?}:{:?}:{:08x?}:{:08x?}",
        demo_sha256,
        release_row.round,
        projectile.steam_id,
        projectile.tick,
        weapon_def_index,
        grenade_dir(weapon_def_index, projectile.kind),
        projectile.initial_position,
        projectile.initial_velocity,
        projectile.initial_position.map(f32::to_bits),
        projectile.initial_velocity.map(f32::to_bits)
    );
    let hash = sha256_hex(raw.as_bytes());
    format!(
        "{}_{}_r{:02}_t{}_{}",
        Side::team_dir(release_row.team_num),
        phase.as_dir(),
        release_row.round,
        projectile.tick,
        &hash[..12]
    )
}

fn effective_projectile_weapon_def_index(
    projectile: &ParsedProjectile,
    release_row: &ParsedPlayerTick,
) -> i32 {
    let release_weapon_def_index = release_row.item_def_idx;
    if grenade_weapon_def_matches_kind(release_weapon_def_index, projectile.kind) {
        release_weapon_def_index
    } else {
        projectile.weapon_def_index
    }
}

fn grenade_weapon_def_matches_kind(weapon_def_index: i32, kind: ProjectileKind) -> bool {
    matches!(
        (weapon_def_index, kind),
        (43, ProjectileKind::Flash)
            | (44, ProjectileKind::He)
            | (45, ProjectileKind::Smoke)
            | (46, ProjectileKind::Molotov)
            | (48, ProjectileKind::Molotov)
            | (47, ProjectileKind::Decoy)
    )
}

fn grenade_dir(weapon_def_index: i32, kind: ProjectileKind) -> &'static str {
    match weapon_def_index {
        43 => "flash",
        44 => "he",
        45 => "smoke",
        46 => "molotov",
        47 => "decoy",
        48 => "incgrenade",
        _ => match kind {
            ProjectileKind::Smoke => "smoke",
            ProjectileKind::Flash => "flash",
            ProjectileKind::He => "he",
            ProjectileKind::Molotov => "molotov",
            ProjectileKind::Decoy => "decoy",
            ProjectileKind::Unknown => "unknown",
        },
    }
}

fn vec3_is_meaningful(value: [f32; 3]) -> bool {
    value.iter().all(|component| component.is_finite())
        && value.iter().any(|component| component.abs() > f32::EPSILON)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::rec_writer::read_rec_file;

    #[test]
    fn exports_single_nade_clip_with_default_window() {
        let parsed = sample_demo(vec![sample_projectile(164, [10.0, 20.0, 30.0])]);
        let temp = tempfile::tempdir().unwrap();
        let report = export_nade_clips(
            &parsed,
            &NadeExportOptions {
                output_dir: temp.path().to_path_buf(),
                output_stem: Some("sample".to_string()),
                side: Side::Both,
                selected_rounds: None,
                pre_roll_seconds: DEFAULT_PRE_ROLL_SECONDS,
                post_roll_seconds: DEFAULT_POST_ROLL_SECONDS,
                opening_seconds: DEFAULT_OPENING_SECONDS,
                subtick_mode: SubtickMode::Auto,
            },
        )
        .unwrap();

        assert_eq!(report.clips_written, 1);
        assert_eq!(report.skipped, 0);
        let clip = &report.manifest.clips[0];
        assert_eq!(clip.kind, ProjectileKind::Smoke);
        assert_eq!(clip.phase, NadePhase::Opening);
        assert_eq!(clip.clip_start_tick, 100);
        assert_eq!(clip.clip_end_tick, 196);
        assert_eq!(clip.release_tick_index, 64);
        assert_eq!(clip.side, "t");
        assert!(clip.path.starts_with("nades/t/opening/smoke/"));
        assert_eq!(clip.timing.round, 1);
        assert_eq!(clip.timing.throw_tick, 164);
        assert_eq!(clip.timing.freeze_end_tick, None);
        assert_eq!(clip.timing.round_live_tick, Some(100));
        assert_eq!(clip.timing.time_bucket, NadeTimeBucket::SpawnExec);
        assert_option_f32_eq(clip.timing.seconds_after_round_live, 1.0);
        assert_eq!(clip.projectile_effect_position, [400.0, 500.0, 600.0]);
        assert_eq!(clip.projectile_effect_tick, Some(228));
        assert_eq!(
            clip.projectile_effect_source,
            ProjectileEffectSource::SmokeDetonationProp
        );
        assert_eq!(clip.projectile_effect_confidence, 0.9);

        let rec = read_rec_file(&report.root.join(&clip.path)).unwrap();
        assert_eq!(rec.projectiles.len(), 1);
        assert_eq!(rec.projectiles[0].tick_index, 64);
        assert_eq!(rec.projectiles[0].initial_velocity, [10.0, 20.0, 30.0]);
        assert!(report.manifest_path.exists());
        assert!(report.root.join("nade_manifest.json.br").exists());
        assert!(report.root.join("nade_conversion.log").exists());
    }

    #[test]
    fn marks_after_opening_pre_plant_clip_as_combat() {
        let parsed = sample_demo_with_rows(
            vec![sample_projectile(1500, [10.0, 20.0, 30.0])],
            100..=1600,
        );
        let temp = tempfile::tempdir().unwrap();
        let report = export_nade_clips(&parsed, &test_options(temp.path())).unwrap();

        assert_eq!(report.manifest.clips[0].phase, NadePhase::Combat);
        assert_eq!(
            report.manifest.clips[0].timing.time_bucket,
            NadeTimeBucket::Midround
        );
        assert!(report.manifest.clips[0]
            .path
            .starts_with("nades/t/combat/smoke/"));
    }

    #[test]
    fn timing_uses_freeze_end_event_when_available() {
        let mut parsed = sample_demo(vec![sample_projectile(164, [10.0, 20.0, 30.0])]);
        parsed.round_freeze_end_ticks = vec![132];
        let temp = tempfile::tempdir().unwrap();
        let report = export_nade_clips(&parsed, &test_options(temp.path())).unwrap();
        let timing = &report.manifest.clips[0].timing;

        assert_eq!(timing.freeze_end_tick, Some(132));
        assert_eq!(timing.round_live_tick, Some(132));
        assert_option_f32_eq(timing.seconds_after_freeze_end, 0.5);
        assert_option_f32_eq(timing.seconds_after_round_live, 0.5);
        assert_eq!(timing.time_bucket, NadeTimeBucket::SpawnExec);
    }

    #[test]
    fn marks_post_plant_clip_as_retake() {
        let mut parsed = sample_demo(vec![sample_projectile(210, [10.0, 20.0, 30.0])]);
        parsed.bomb_planted_ticks = vec![200];
        let temp = tempfile::tempdir().unwrap();
        let report = export_nade_clips(&parsed, &test_options(temp.path())).unwrap();

        assert_eq!(report.manifest.clips[0].phase, NadePhase::Retake);
        assert_eq!(
            report.manifest.clips[0].timing.time_bucket,
            NadeTimeBucket::Retake
        );
        assert_eq!(report.manifest.clips[0].timing.bomb_planted_tick, Some(200));
        assert_option_f32_eq(
            report.manifest.clips[0].timing.seconds_after_bomb_planted,
            10.0 / 64.0,
        );
        assert!(report.manifest.clips[0]
            .path
            .starts_with("nades/t/retake/smoke/"));
    }

    #[test]
    fn duplicate_projectiles_get_distinct_stable_ids() {
        let parsed = sample_demo(vec![
            sample_projectile(164, [10.0, 20.0, 30.0]),
            sample_projectile(164, [11.0, 20.0, 30.0]),
        ]);
        let temp = tempfile::tempdir().unwrap();
        let report = export_nade_clips(&parsed, &test_options(temp.path())).unwrap();

        assert_eq!(report.clips_written, 2);
        assert_ne!(
            report.manifest.clips[0].clip_id,
            report.manifest.clips[1].clip_id
        );
    }

    #[test]
    fn skips_unknown_or_empty_projectiles() {
        let mut projectile = sample_projectile(164, [10.0, 20.0, 30.0]);
        projectile.kind = ProjectileKind::Unknown;
        projectile.weapon_def_index = -1;
        let parsed = sample_demo(vec![projectile]);
        let temp = tempfile::tempdir().unwrap();
        let report = export_nade_clips(&parsed, &test_options(temp.path())).unwrap();

        assert_eq!(report.clips_written, 0);
        assert_eq!(report.skipped, 1);
        assert!(report.manifest.skipped[0]
            .reason
            .contains("unknown grenade kind"));
    }

    #[test]
    fn maps_incendiary_weapon_def_index_separately_from_molotov() {
        assert_eq!(
            ProjectileKind::weapon_def_index_from_grenade_type("CIncGrenadeProjectile"),
            48
        );
        assert_eq!(
            ProjectileKind::weapon_def_index_from_grenade_type("CMolotovProjectile"),
            46
        );
    }

    #[test]
    fn corrects_fire_weapon_def_from_release_row() {
        let mut projectile = sample_projectile(164, [10.0, 20.0, 30.0]);
        projectile.kind = ProjectileKind::Molotov;
        projectile.grenade_type = "CMolotovProjectile".to_string();
        projectile.weapon_def_index = 46;
        let mut parsed = sample_demo(vec![projectile]);
        for row in &mut parsed.rows {
            row.item_def_idx = 48;
            row.inventory_as_ids = vec![48];
        }
        let temp = tempfile::tempdir().unwrap();
        let report = export_nade_clips(&parsed, &test_options(temp.path())).unwrap();

        let clip = &report.manifest.clips[0];
        assert_eq!(clip.weapon_def_index, 48);
        assert!(clip.path.starts_with("nades/t/opening/incgrenade/"));

        let rec = read_rec_file(&report.root.join(&clip.path)).unwrap();
        assert_eq!(rec.projectiles[0].weapon_def_index, 48);
    }

    fn test_options(output_dir: &Path) -> NadeExportOptions {
        NadeExportOptions {
            output_dir: output_dir.to_path_buf(),
            output_stem: Some("sample".to_string()),
            side: Side::Both,
            selected_rounds: None,
            pre_roll_seconds: DEFAULT_PRE_ROLL_SECONDS,
            post_roll_seconds: DEFAULT_POST_ROLL_SECONDS,
            opening_seconds: DEFAULT_OPENING_SECONDS,
            subtick_mode: SubtickMode::Auto,
        }
    }

    fn sample_demo(projectiles: Vec<ParsedProjectile>) -> ParsedDemo {
        sample_demo_with_rows(projectiles, 100..=260)
    }

    fn sample_demo_with_rows(
        projectiles: Vec<ParsedProjectile>,
        range: std::ops::RangeInclusive<i32>,
    ) -> ParsedDemo {
        let rows = range.map(sample_row).collect();
        ParsedDemo {
            path: "<demo.dem>".to_string(),
            stem: "demo".to_string(),
            demo_sha256: "12".repeat(32),
            map: "de_mirage".to_string(),
            tick_rate: 64.0,
            rows,
            projectiles,
            ..ParsedDemo::default()
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
            game_time: Some(tick as f32 / 64.0),
            origin: [tick as f32, 1.0, 2.0],
            velocity: [1.0, 0.0, 0.0],
            pitch: 3.0,
            yaw: 4.0,
            item_def_idx: 45,
            inventory_as_ids: vec![45],
            entity_flags: 1,
            move_type: 2,
            ..ParsedPlayerTick::default()
        }
    }

    fn sample_projectile(tick: i32, velocity: [f32; 3]) -> ParsedProjectile {
        ParsedProjectile {
            tick,
            steam_id: 76561198000000001,
            name: "alpha".to_string(),
            grenade_type: "smokegrenade_projectile".to_string(),
            kind: ProjectileKind::Smoke,
            weapon_def_index: 45,
            initial_position: [100.0, 200.0, 300.0],
            initial_velocity: velocity,
            detonation_position: [400.0, 500.0, 600.0],
            effect_position: [400.0, 500.0, 600.0],
            effect_tick: Some(tick + 64),
            effect_source: ProjectileEffectSource::SmokeDetonationProp,
            effect_confidence: 0.9,
        }
    }

    fn assert_option_f32_eq(actual: Option<f32>, expected: f32) {
        let actual = actual.expect("expected timing value");
        assert!(
            (actual - expected).abs() < 0.0001,
            "actual={actual} expected={expected}"
        );
    }
}
