use clap::{Parser, Subcommand};
use cs2_demotracer::api::{
    build_nade_library_with_progress, export_nade_clips_from_demo_path, NadeClipExportRequest,
    NadeContextOptions, NadeDedupeOptions, NadeLibraryExportRequest,
};
use cs2_demotracer::demo_reader::read_demo;
use cs2_demotracer::export::{
    export_demo, parse_round_list, ConvertOptions, DEFAULT_FREEZE_PREROLL_SECONDS,
};
use cs2_demotracer::model::{Side, SubtickMode};
use cs2_demotracer::nade_export::{
    DEFAULT_OPENING_SECONDS, DEFAULT_POST_ROLL_SECONDS, DEFAULT_PRE_ROLL_SECONDS,
};
use cs2_demotracer::nade_library::print_nade_library_progress;
use cs2_demotracer::pool::{build_round_pool, BuildPoolOptions};
use cs2_demotracer::quality::{analyze_demo, AnalysisOptions};
use cs2_demotracer::rec_writer::read_rec_file;
use dialoguer::{theme::ColorfulTheme, Confirm, Input, Select};
use std::collections::BTreeMap;
use std::fmt::Display;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

#[derive(Parser)]
#[command(name = "cs2-demotracer")]
#[command(about = "Trace CS2 demos into bot-executable route replays.")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Analyze a demo and print round quality.
    Inspect {
        #[arg(long)]
        demo: PathBuf,
        #[arg(long, default_value_t = 240.0)]
        max_round_seconds: f32,
    },
    /// Convert many demos into a map-specific round pool.
    ConvertPool {
        #[arg(long)]
        demo_dir: PathBuf,
        #[arg(long)]
        output: PathBuf,
        #[arg(long, default_value = "de_mirage")]
        map: String,
        #[arg(long)]
        recursive: bool,
        #[arg(long)]
        include_suspicious: bool,
        #[arg(long, default_value_t = 240.0)]
        max_round_seconds: f32,
        #[arg(long)]
        full_round: bool,
        #[arg(long, default_value_t = SubtickMode::Auto)]
        subticks: SubtickMode,
        #[arg(long, default_value_t = DEFAULT_FREEZE_PREROLL_SECONDS)]
        freeze_preroll_seconds: f32,
    },
    /// Inspect per-player row coverage for one round.
    InspectRound {
        #[arg(long)]
        demo: PathBuf,
        #[arg(long)]
        round: u32,
        #[arg(long, default_value_t = 240.0)]
        max_round_seconds: f32,
    },
    /// Convert one demo into compressed .dtr files and a manifest.
    Convert {
        #[arg(long)]
        demo: PathBuf,
        #[arg(long)]
        output: PathBuf,
        #[arg(long, default_value_t = Side::Both)]
        side: Side,
        #[arg(long)]
        rounds: Option<String>,
        #[arg(long)]
        include_suspicious: bool,
        #[arg(long, default_value_t = 240.0)]
        max_round_seconds: f32,
        #[arg(long)]
        full_round: bool,
        #[arg(long, default_value_t = SubtickMode::Auto)]
        subticks: SubtickMode,
        #[arg(long, default_value_t = DEFAULT_FREEZE_PREROLL_SECONDS)]
        freeze_preroll_seconds: f32,
    },
    /// Convert grenade throws into short .dtr clips and a nade manifest.
    ConvertNades {
        #[arg(long)]
        demo: PathBuf,
        #[arg(long)]
        output: PathBuf,
        #[arg(long, default_value_t = Side::Both)]
        side: Side,
        #[arg(long)]
        rounds: Option<String>,
        #[arg(long, default_value_t = DEFAULT_PRE_ROLL_SECONDS)]
        pre_roll: f32,
        #[arg(long, default_value_t = DEFAULT_POST_ROLL_SECONDS)]
        post_roll: f32,
        #[arg(long, default_value_t = DEFAULT_OPENING_SECONDS)]
        opening_seconds: f32,
    },
    /// Convert many demos into a local map-indexed nade library.
    ConvertNadesLibrary {
        #[arg(long)]
        demo_dir: PathBuf,
        #[arg(long)]
        output: PathBuf,
        #[arg(long)]
        recursive: bool,
        #[arg(long, default_value_t = 1)]
        jobs: usize,
        #[arg(long)]
        max_demos: Option<usize>,
        #[arg(long)]
        map: Option<String>,
        #[arg(long = "reuse-root")]
        reuse_roots: Vec<PathBuf>,
        #[arg(long)]
        aggregate_only: bool,
        #[arg(long, default_value_t = Side::Both)]
        side: Side,
        #[arg(long, default_value_t = DEFAULT_PRE_ROLL_SECONDS)]
        pre_roll: f32,
        #[arg(long, default_value_t = DEFAULT_POST_ROLL_SECONDS)]
        post_roll: f32,
        #[arg(long, default_value_t = DEFAULT_OPENING_SECONDS)]
        opening_seconds: f32,
        #[arg(long = "no-dedupe")]
        no_dedupe: bool,
        #[arg(long, default_value_t = 48.0)]
        dedupe_origin_units: f32,
        #[arg(long, default_value_t = 8.0)]
        dedupe_yaw_degrees: f32,
        #[arg(long, default_value_t = 120.0)]
        dedupe_velocity_units: f32,
    },
    /// Validate .dtr files and public output-pack hygiene.
    Validate {
        #[arg(long)]
        input: PathBuf,
    },
    /// Run the interactive conversion wizard.
    Wizard,
    #[cfg(feature = "gui")]
    /// Run the experimental GUI build.
    Gui,
}

fn main() {
    if let Err(err) = run() {
        eprintln!("error: {err}");
        std::process::exit(1);
    }
}

fn run() -> cs2_demotracer::Result<()> {
    let cli = Cli::parse();
    match cli.command {
        Command::Inspect {
            demo,
            max_round_seconds,
        } => {
            let parsed = read_demo(&demo)?;
            let analysis = analyze_demo(
                &parsed,
                AnalysisOptions {
                    max_round_seconds,
                    ..AnalysisOptions::default()
                },
            );
            println!(
                "map={} tick_rate={:.1} rows={} rounds={}",
                analysis.map,
                analysis.tick_rate,
                analysis.row_count,
                analysis.rounds.len()
            );
            for round in analysis.rounds {
                println!(
                    "round {:02} {:?} T={} CT={} total={} {:.1}s rows={} {}",
                    round.round,
                    round.status,
                    round.t_players,
                    round.ct_players,
                    round.total_players,
                    round.duration_seconds,
                    round.valid_rows,
                    if round.problems.is_empty() {
                        "ok".to_string()
                    } else {
                        round.problems.join("; ")
                    }
                );
            }
        }
        Command::InspectRound {
            demo,
            round,
            max_round_seconds,
        } => {
            let parsed = read_demo(&demo)?;
            let analysis = analyze_demo(
                &parsed,
                AnalysisOptions {
                    max_round_seconds,
                    ..AnalysisOptions::default()
                },
            );
            let Some(summary) = analysis.rounds.iter().find(|r| r.round == round) else {
                return Err(cs2_demotracer::Error::InvalidDemo(format!(
                    "round {round} not found"
                )));
            };
            println!(
                "round {:02} {:?} T={} CT={} total={} {:.1}s ticks {}..{} rows={} {}",
                summary.round,
                summary.status,
                summary.t_players,
                summary.ct_players,
                summary.total_players,
                summary.duration_seconds,
                summary.start_tick,
                summary.end_tick,
                summary.valid_rows,
                if summary.problems.is_empty() {
                    "ok".to_string()
                } else {
                    summary.problems.join("; ")
                }
            );
            print_round_players(&parsed, summary.start_tick, summary.end_tick, round);
        }
        Command::Convert {
            demo,
            output,
            side,
            rounds,
            include_suspicious,
            max_round_seconds,
            full_round,
            subticks,
            freeze_preroll_seconds,
        } => {
            let parsed = read_demo(&demo)?;
            let selected_rounds = rounds.as_deref().map(parse_round_list).transpose()?;
            let report = export_demo(
                &parsed,
                &ConvertOptions {
                    output_dir: output,
                    output_stem: None,
                    side,
                    selected_rounds,
                    include_suspicious,
                    cut_before_bomb_plant: !full_round,
                    subtick_mode: subticks,
                    freeze_preroll_seconds,
                    analysis: AnalysisOptions {
                        max_round_seconds,
                        ..AnalysisOptions::default()
                    },
                },
            )?;
            println!(
                "wrote {} files under {}",
                report.files_written,
                report.root.display()
            );
            println!("manifest {}", report.manifest_path.display());
        }
        Command::ConvertNades {
            demo,
            output,
            side,
            rounds,
            pre_roll,
            post_roll,
            opening_seconds,
        } => {
            let selected_rounds = rounds.as_deref().map(parse_round_list).transpose()?;
            let report = export_nade_clips_from_demo_path(&NadeClipExportRequest {
                demo_path: Some(demo),
                output_dir: output,
                output_stem: None,
                side,
                selected_rounds,
                context: NadeContextOptions {
                    pre_roll_seconds: pre_roll,
                    post_roll_seconds: post_roll,
                    opening_seconds,
                },
                subtick_mode: SubtickMode::Auto,
            })?;
            println!(
                "wrote {} nade clips under {} (skipped {})",
                report.clips_written,
                report.root.display(),
                report.skipped
            );
            println!("nade manifest {}", report.manifest_path.display());
        }
        Command::ConvertNadesLibrary {
            demo_dir,
            output,
            recursive,
            jobs,
            max_demos,
            map,
            reuse_roots,
            aggregate_only,
            side,
            pre_roll,
            post_roll,
            opening_seconds,
            no_dedupe,
            dedupe_origin_units,
            dedupe_yaw_degrees,
            dedupe_velocity_units,
        } => {
            let report = build_nade_library_with_progress(
                &NadeLibraryExportRequest {
                    demo_dir,
                    output_dir: output,
                    recursive,
                    jobs,
                    max_demos,
                    map_filter: map,
                    reuse_roots,
                    aggregate_only,
                    side,
                    context: NadeContextOptions {
                        pre_roll_seconds: pre_roll,
                        post_roll_seconds: post_roll,
                        opening_seconds,
                    },
                    subtick_mode: SubtickMode::Auto,
                    dedupe: NadeDedupeOptions {
                        enabled: !no_dedupe,
                        origin_units: dedupe_origin_units,
                        yaw_degrees: dedupe_yaw_degrees,
                        velocity_units: dedupe_velocity_units,
                    },
                },
                |event| print_nade_library_progress(&event),
            )?;
            println!(
                "nade library demos={} converted={} reused={} existing={} filtered_map={} failures={} maps={} source_clips={} clips={} root={}",
                report.demos_done,
                report.demos_converted,
                report.demos_reused,
                report.demos_skipped_existing,
                report.demos_filtered_map,
                report.failures,
                report.maps_written,
                report.source_clips,
                report.clips,
                report.root.display()
            );
        }
        Command::ConvertPool {
            demo_dir,
            output,
            map,
            recursive,
            include_suspicious,
            max_round_seconds,
            full_round,
            subticks,
            freeze_preroll_seconds,
        } => {
            let report = build_round_pool(&BuildPoolOptions {
                demo_dir,
                output_dir: output,
                map,
                recursive,
                include_suspicious,
                cut_before_bomb_plant: !full_round,
                subtick_mode: subticks,
                freeze_preroll_seconds,
                analysis: AnalysisOptions {
                    max_round_seconds,
                    ..AnalysisOptions::default()
                },
            })?;
            println!(
                "pool demos_seen={} demos_matched={} candidates={} failures={}",
                report.demos_seen, report.demos_matched, report.candidates, report.failures
            );
            println!("pool manifest {}", report.manifest_path.display());
        }
        Command::Validate { input } => {
            let count = validate_dtr_path(&input)?;
            println!("validated {count} .dtr files");
        }
        Command::Wizard => {
            run_wizard()?;
        }
        #[cfg(feature = "gui")]
        Command::Gui => {
            cs2_demotracer::gui::run_gui()?;
        }
    }
    Ok(())
}

fn run_wizard() -> cs2_demotracer::Result<()> {
    let theme = ColorfulTheme::default();
    println!("CS2 DemoTracer wizard");
    println!("Trace a CS2 .dem into compressed .dtr route replay files.\n");

    let demo: String = Input::with_theme(&theme)
        .with_prompt("Demo path")
        .interact_text()
        .map_err(dialog_error)?;
    let demo = prompt_path(&demo);

    let output: String = Input::with_theme(&theme)
        .with_prompt("Output directory")
        .default("output".to_string())
        .interact_text()
        .map_err(dialog_error)?;
    let output = prompt_path(&output);

    println!("\nAnalyzing {} ...", demo.display());
    let parsed = read_demo(&demo)?;
    let analysis = analyze_demo(&parsed, AnalysisOptions::default());
    print_analysis_summary(&analysis);

    let recommended_rounds: std::collections::BTreeSet<u32> = analysis
        .rounds
        .iter()
        .filter(|round| round.recommended())
        .map(|round| round.round)
        .collect();
    let default_rounds = format_round_list(&recommended_rounds);
    let round_prompt = if default_rounds.is_empty() {
        "Rounds to export (comma/ranges, e.g. 0,1,5-8)".to_string()
    } else {
        "Rounds to export (Enter = recommended)".to_string()
    };
    let rounds_input: String = Input::with_theme(&theme)
        .with_prompt(round_prompt)
        .default(default_rounds.clone())
        .interact_text()
        .map_err(dialog_error)?;
    let selected_rounds = parse_wizard_rounds(&rounds_input, &recommended_rounds)?;

    let include_suspicious = Confirm::with_theme(&theme)
        .with_prompt("Allow suspicious rounds if selected")
        .default(false)
        .interact()
        .map_err(dialog_error)?;
    let full_round = Confirm::with_theme(&theme)
        .with_prompt("Export full rounds instead of cutting before C4 plant")
        .default(false)
        .interact()
        .map_err(dialog_error)?;

    let side_idx = Select::with_theme(&theme)
        .with_prompt("Side")
        .items(&["both", "t", "ct"])
        .default(0)
        .interact()
        .map_err(dialog_error)?;
    let side = match side_idx {
        1 => Side::T,
        2 => Side::Ct,
        _ => Side::Both,
    };

    let subtick_idx = Select::with_theme(&theme)
        .with_prompt("Subtick input")
        .items(&["auto", "off"])
        .default(0)
        .interact()
        .map_err(dialog_error)?;
    let subtick_mode = if subtick_idx == 1 {
        SubtickMode::Off
    } else {
        SubtickMode::Auto
    };

    println!("\nConverting selected rounds ...");
    let report = export_demo(
        &parsed,
        &ConvertOptions {
            output_dir: output,
            output_stem: None,
            side,
            selected_rounds: Some(selected_rounds),
            include_suspicious,
            cut_before_bomb_plant: !full_round,
            subtick_mode,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            analysis: AnalysisOptions::default(),
        },
    )?;

    let validated = validate_dtr_path(&report.root)?;
    println!("\nDone.");
    println!("manifest {}", report.manifest_path.display());
    println!(
        "wrote {} .dtr files under {}",
        report.files_written,
        report.root.display()
    );
    println!("validated {validated} .dtr files");
    Ok(())
}

fn prompt_path(input: &str) -> PathBuf {
    PathBuf::from(input.trim().trim_matches('"').trim_matches('\''))
}

fn parse_wizard_rounds(
    input: &str,
    recommended_rounds: &std::collections::BTreeSet<u32>,
) -> cs2_demotracer::Result<std::collections::BTreeSet<u32>> {
    let trimmed = input.trim();
    let rounds = if trimmed.is_empty() {
        recommended_rounds.clone()
    } else {
        parse_round_list(trimmed)?
    };
    if rounds.is_empty() {
        return Err(cs2_demotracer::Error::InvalidDemo(
            "no rounds selected".to_string(),
        ));
    }
    Ok(rounds)
}

fn format_round_list(rounds: &std::collections::BTreeSet<u32>) -> String {
    rounds
        .iter()
        .map(u32::to_string)
        .collect::<Vec<_>>()
        .join(",")
}

fn print_analysis_summary(analysis: &cs2_demotracer::model::DemoAnalysis) {
    println!(
        "map={} tick_rate={:.1} rows={} rounds={}",
        analysis.map,
        analysis.tick_rate,
        analysis.row_count,
        analysis.rounds.len()
    );
    for round in &analysis.rounds {
        println!(
            "round {:02} {:?} T={} CT={} total={} {:.1}s rows={} {}",
            round.round,
            round.status,
            round.t_players,
            round.ct_players,
            round.total_players,
            round.duration_seconds,
            round.valid_rows,
            if round.problems.is_empty() {
                "ok".to_string()
            } else {
                round.problems.join("; ")
            }
        );
    }
}

fn dialog_error(err: impl Display) -> cs2_demotracer::Error {
    cs2_demotracer::Error::InvalidDemo(format!("interactive prompt failed: {err}"))
}

#[derive(Default)]
struct PlayerRoundStats {
    name: String,
    team_num: u8,
    rows: usize,
    first_tick: i32,
    last_tick: i32,
}

#[derive(Default)]
struct PlayerRoundScan {
    in_round: usize,
    in_window: usize,
    active_rows: usize,
    alive_rows: usize,
    steam_rows: usize,
    players: BTreeMap<(u8, u64), PlayerRoundStats>,
}

fn print_round_players(
    parsed: &cs2_demotracer::model::ParsedDemo,
    start_tick: i32,
    end_tick: i32,
    round: u32,
) {
    let strict = collect_round_players(parsed, start_tick, end_tick, round, true);
    let raw = collect_round_players(parsed, start_tick, end_tick, round, false);

    println!(
        "strict active rows: in_round={} in_window={} active={} alive={} with_steam={}",
        strict.in_round, strict.in_window, strict.active_rows, strict.alive_rows, strict.steam_rows
    );
    print_player_table("strict active players", &strict.players);

    if strict.players.is_empty() || raw.players.len() != strict.players.len() {
        println!(
            "raw window rows: in_round={} in_window={} active={} alive={} with_steam={}",
            raw.in_round, raw.in_window, raw.active_rows, raw.alive_rows, raw.steam_rows
        );
        print_player_table("raw window players", &raw.players);
    }
}

fn collect_round_players(
    parsed: &cs2_demotracer::model::ParsedDemo,
    start_tick: i32,
    end_tick: i32,
    round: u32,
    require_active: bool,
) -> PlayerRoundScan {
    let mut scan = PlayerRoundScan::default();
    for row in &parsed.rows {
        if row.round != round {
            continue;
        }
        scan.in_round += 1;
        if row.tick < start_tick || row.tick > end_tick {
            continue;
        }
        scan.in_window += 1;
        let is_active = row.round_in_progress && !row.is_freeze_period;
        if is_active {
            scan.active_rows += 1;
        }
        if require_active && !is_active {
            continue;
        }
        if !row.is_alive {
            continue;
        }
        scan.alive_rows += 1;
        if row.steam_id == 0 {
            continue;
        }
        scan.steam_rows += 1;
        let entry = scan
            .players
            .entry((row.team_num, row.steam_id))
            .or_insert_with(|| PlayerRoundStats {
                name: row.name.clone(),
                team_num: row.team_num,
                rows: 0,
                first_tick: row.tick,
                last_tick: row.tick,
            });
        entry.rows += 1;
        entry.first_tick = entry.first_tick.min(row.tick);
        entry.last_tick = entry.last_tick.max(row.tick);
        if entry.name.is_empty() && !row.name.is_empty() {
            entry.name = row.name.clone();
        }
    }

    scan
}

fn print_player_table(title: &str, players: &BTreeMap<(u8, u64), PlayerRoundStats>) {
    println!("{title}: {}", players.len());
    println!("team steamid rows first_tick last_tick name");
    for ((_, steam_id), stats) in players {
        let team = match stats.team_num {
            2 => "T",
            3 => "CT",
            _ => "UNK",
        };
        println!(
            "{team:>3} {steam_id} {:>6} {:>10} {:>9} {}",
            stats.rows, stats.first_tick, stats.last_tick, stats.name
        );
    }
}

fn collect_dtr_files(root: &PathBuf) -> cs2_demotracer::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    collect_recursively(root, &mut out)?;
    Ok(out)
}

fn validate_dtr_path(input: &Path) -> cs2_demotracer::Result<usize> {
    validate_public_artifacts(input)?;
    let mut count = 0_usize;
    for path in collect_dtr_files(&input.to_path_buf())? {
        let rec = read_rec_file(&path)?;
        if rec.ticks.is_empty() {
            return Err(cs2_demotracer::Error::InvalidRec(format!(
                "{} has no ticks",
                path.display()
            )));
        }
        count += 1;
    }
    if count == 0 {
        return Err(cs2_demotracer::Error::InvalidDemo(format!(
            "no .dtr files found under {}",
            input.display()
        )));
    }
    Ok(count)
}

fn validate_public_artifacts(input: &Path) -> cs2_demotracer::Result<()> {
    let pack_root = if input.is_file() {
        input.parent().unwrap_or_else(|| Path::new("."))
    } else {
        input
    };
    for path in collect_files(input)? {
        if let Some(reason) = forbidden_public_artifact_reason(&path) {
            return Err(cs2_demotracer::Error::InvalidDemo(format!(
                "{reason} must not be included in output packs: {}",
                path.display()
            )));
        }

        if is_manifest_json(&path) {
            let text = read_manifest_text(&path)?;
            let json: serde_json::Value = serde_json::from_str(&text)?;
            validate_manifest_demo_paths(&path, &json)?;
            validate_manifest_artifact_paths(pack_root, &path, &json)?;
        }
    }
    Ok(())
}

fn forbidden_public_artifact_reason(path: &Path) -> Option<&'static str> {
    let ext = path.extension()?.to_str()?.to_ascii_lowercase();
    match ext.as_str() {
        "dem" => Some("raw demo file"),
        "cs2rec" => Some("raw replay dump"),
        "csv" | "parquet" => Some("debug trace/data dump"),
        _ => None,
    }
}

fn collect_files(input: &Path) -> cs2_demotracer::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    collect_files_recursively(input, &mut out)?;
    Ok(out)
}

fn collect_files_recursively(path: &Path, out: &mut Vec<PathBuf>) -> cs2_demotracer::Result<()> {
    if path.is_file() {
        out.push(path.to_path_buf());
        return Ok(());
    }
    let entries = fs::read_dir(path).map_err(|e| cs2_demotracer::io_error(path, e))?;
    for entry in entries {
        let entry = entry.map_err(|e| cs2_demotracer::io_error(path, e))?;
        collect_files_recursively(&entry.path(), out)?;
    }
    Ok(())
}

fn is_manifest_json(path: &Path) -> bool {
    let Some(name) = path.file_name().and_then(|value| value.to_str()) else {
        return false;
    };
    name.ends_with(".json") || name.ends_with(".json.br")
}

fn read_manifest_text(path: &Path) -> cs2_demotracer::Result<String> {
    if !path
        .file_name()
        .and_then(|value| value.to_str())
        .is_some_and(|name| name.ends_with(".json.br"))
    {
        return fs::read_to_string(path).map_err(|e| cs2_demotracer::io_error(path, e));
    }

    let file = fs::File::open(path).map_err(|e| cs2_demotracer::io_error(path, e))?;
    let mut decompressor = brotli::Decompressor::new(file, 4096);
    let mut text = String::new();
    decompressor
        .read_to_string(&mut text)
        .map_err(|e| cs2_demotracer::Error::InvalidDemo(e.to_string()))?;
    Ok(text)
}

fn validate_manifest_demo_paths(
    path: &Path,
    value: &serde_json::Value,
) -> cs2_demotracer::Result<()> {
    match value {
        serde_json::Value::Object(map) => {
            for (key, value) in map {
                if key == "demo_path" {
                    if let Some(text) = value.as_str() {
                        if is_local_demo_path(text) {
                            return Err(cs2_demotracer::Error::InvalidDemo(format!(
                                "{} contains local demo_path {:?}",
                                path.display(),
                                text
                            )));
                        }
                    }
                }
                validate_manifest_demo_paths(path, value)?;
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                validate_manifest_demo_paths(path, item)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn validate_manifest_artifact_paths(
    pack_root: &Path,
    manifest_path: &Path,
    value: &serde_json::Value,
) -> cs2_demotracer::Result<()> {
    match value {
        serde_json::Value::Object(map) => {
            for (key, value) in map {
                if is_manifest_artifact_path_key(key) {
                    if let Some(text) = value.as_str() {
                        validate_manifest_artifact_path(pack_root, manifest_path, key, text)?;
                    }
                }
                validate_manifest_artifact_paths(pack_root, manifest_path, value)?;
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                validate_manifest_artifact_paths(pack_root, manifest_path, item)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn is_manifest_artifact_path_key(key: &str) -> bool {
    key == "path" || key == "manifest"
}

fn validate_manifest_artifact_path(
    pack_root: &Path,
    manifest_path: &Path,
    key: &str,
    value: &str,
) -> cs2_demotracer::Result<()> {
    if value.trim().is_empty() {
        return Err(cs2_demotracer::Error::InvalidDemo(format!(
            "{} contains empty {key}",
            manifest_path.display()
        )));
    }
    if is_absolute_manifest_artifact_path(value) {
        return Err(cs2_demotracer::Error::InvalidDemo(format!(
            "{} contains absolute {key} {:?}",
            manifest_path.display(),
            value
        )));
    }

    let manifest_dir = manifest_path.parent().unwrap_or_else(|| Path::new("."));
    let full =
        normalize_path(&manifest_dir.join(value.replace('\\', std::path::MAIN_SEPARATOR_STR)));
    let root = normalize_path(pack_root);
    if !path_is_under_root(&full, &root) {
        return Err(cs2_demotracer::Error::InvalidDemo(format!(
            "{} contains {key} outside output pack {:?}",
            manifest_path.display(),
            value
        )));
    }
    if !full.exists() {
        return Err(cs2_demotracer::Error::InvalidDemo(format!(
            "{} contains missing {key} target {:?}",
            manifest_path.display(),
            value
        )));
    }
    Ok(())
}

fn is_absolute_manifest_artifact_path(value: &str) -> bool {
    Path::new(value).is_absolute()
        || value.starts_with('/')
        || value.starts_with('\\')
        || value.contains(':')
}

fn normalize_path(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for component in path.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                out.pop();
            }
            other => out.push(other.as_os_str()),
        }
    }
    out
}

fn path_is_under_root(path: &Path, root: &Path) -> bool {
    path == root || path.starts_with(root)
}

fn is_local_demo_path(value: &str) -> bool {
    value.contains('\\') || value.contains('/') || value.contains(':')
}

fn collect_recursively(path: &PathBuf, out: &mut Vec<PathBuf>) -> cs2_demotracer::Result<()> {
    if path.is_file() {
        if path.extension().and_then(|e| e.to_str()) == Some("dtr") {
            out.push(path.clone());
        }
        return Ok(());
    }
    let entries = std::fs::read_dir(path).map_err(|e| cs2_demotracer::io_error(path, e))?;
    for entry in entries {
        let entry = entry.map_err(|e| cs2_demotracer::io_error(path, e))?;
        collect_recursively(&entry.path(), out)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn local_demo_path_detector_rejects_directories_and_drive_letters() {
        assert!(is_local_demo_path(r"C:\demos\match.dem"));
        assert!(is_local_demo_path("C:/demos/match.dem"));
        assert!(is_local_demo_path("/home/user/match.dem"));
        assert!(is_local_demo_path("demos/match.dem"));
        assert!(!is_local_demo_path("match.dem"));
    }

    #[test]
    fn public_artifact_hygiene_rejects_raw_and_debug_dumps() {
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("match.dem")),
            Some("raw demo file")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("round.cs2rec")),
            Some("raw replay dump")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("utility.csv")),
            Some("debug trace/data dump")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("ticks.parquet")),
            Some("debug trace/data dump")
        );
        assert_eq!(
            forbidden_public_artifact_reason(Path::new("conversion.log")),
            None
        );
    }

    #[test]
    fn public_artifact_hygiene_scans_output_pack_files() {
        let temp = tempfile::tempdir().unwrap();
        let trace = temp.path().join("debug_trace.csv");
        fs::write(&trace, b"slot,tick").unwrap();

        let err = validate_public_artifacts(temp.path()).unwrap_err();

        assert!(err.to_string().contains("debug trace/data dump"));
    }

    #[test]
    fn validate_rejects_inputs_without_dtr_files() {
        let temp = tempfile::tempdir().unwrap();

        let err = validate_dtr_path(temp.path()).unwrap_err();

        assert!(err.to_string().contains("no .dtr files"));
    }

    #[test]
    fn manifest_hygiene_rejects_nested_local_demo_path() {
        let manifest = json!({
            "files": [],
            "candidates": [
                { "demo_path": r"C:\demos\match.dem" }
            ]
        });

        let err =
            validate_manifest_demo_paths(Path::new("pool_manifest.json"), &manifest).unwrap_err();

        assert!(err.to_string().contains("contains local demo_path"));
    }

    #[test]
    fn manifest_hygiene_allows_sanitized_demo_path() {
        let manifest = json!({
            "demo_path": "match.dem",
            "files": []
        });

        validate_manifest_demo_paths(Path::new("manifest.json"), &manifest).unwrap();
    }

    #[test]
    fn manifest_hygiene_allows_artifact_paths_inside_pack() {
        let temp = tempfile::tempdir().unwrap();
        let pack = temp.path();
        let map_manifest_path = pack.join("maps/de_mirage/nade_manifest.json");
        let clip_path = pack.join("demos/demo-a/nades/t/opening/smoke/a.dtr");
        fs::create_dir_all(map_manifest_path.parent().unwrap()).unwrap();
        fs::create_dir_all(clip_path.parent().unwrap()).unwrap();
        fs::write(&map_manifest_path, "{}").unwrap();
        fs::write(&clip_path, b"dtr").unwrap();

        let map_manifest = json!({
            "clips": [
                { "path": "../../demos/demo-a/nades/t/opening/smoke/a.dtr" }
            ]
        });
        validate_manifest_artifact_paths(pack, &map_manifest_path, &map_manifest).unwrap();

        let library_manifest = json!({
            "maps": [
                { "manifest": "maps/de_mirage/nade_manifest.json" }
            ]
        });

        validate_manifest_artifact_paths(pack, &pack.join("nade_library.json"), &library_manifest)
            .unwrap();
    }

    #[test]
    fn manifest_hygiene_rejects_missing_artifact_targets() {
        let temp = tempfile::tempdir().unwrap();
        let pack = temp.path();
        let manifest_path = pack.join("manifest.json");
        let manifest = json!({
            "files": [
                { "path": "round01/t/missing.dtr" }
            ]
        });

        let err = validate_manifest_artifact_paths(pack, &manifest_path, &manifest).unwrap_err();

        assert!(err.to_string().contains("missing path target"));
    }

    #[test]
    fn manifest_hygiene_rejects_artifact_paths_outside_pack() {
        let manifest = json!({
            "files": [
                { "path": "../../../outside.dtr" }
            ]
        });

        let err = validate_manifest_artifact_paths(
            Path::new("pack"),
            Path::new("pack/maps/de_mirage/nade_manifest.json"),
            &manifest,
        )
        .unwrap_err();

        assert!(err.to_string().contains("outside output pack"));
    }

    #[test]
    fn manifest_hygiene_rejects_absolute_artifact_paths() {
        let manifest = json!({
            "candidates": [
                { "manifest": r"C:\demos\manifest.json" }
            ]
        });

        let err = validate_manifest_artifact_paths(
            Path::new("pack"),
            Path::new("pack/pool_manifest.json"),
            &manifest,
        )
        .unwrap_err();

        assert!(err.to_string().contains("absolute manifest"));
    }
}
