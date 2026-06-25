mod validate;

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
use dialoguer::{theme::ColorfulTheme, Confirm, Input, Select};
use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Display;
use std::path::PathBuf;
use validate::validate_dtr_path;

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
        #[arg(
            long,
            help = "Write demo-observed weapon/knife/glove cosmetic metadata into manifest JSON; default is no cosmetic export."
        )]
        export_cosmetics: bool,
        #[arg(
            long,
            help = "Also write stable demo-observed weapon sticker metadata into manifest JSON; requires --export-cosmetics and the cosmetic risk confirmations."
        )]
        export_stickers: bool,
        #[arg(
            long,
            help = "Confirm you understand cosmetic export/alignment may carry Valve Game Server Login Token risk."
        )]
        acknowledge_cosmetic_gslt_risk: bool,
        #[arg(
            long,
            help = "Confirm you accept responsibility for using exported cosmetic metadata only where appropriate."
        )]
        accept_cosmetic_export_disclaimer: bool,
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
        #[arg(
            long,
            help = "Write demo-observed weapon/knife/glove cosmetic metadata into manifest JSON; default is no cosmetic export."
        )]
        export_cosmetics: bool,
        #[arg(
            long,
            help = "Also write stable demo-observed weapon sticker metadata into manifest JSON; requires --export-cosmetics and the cosmetic risk confirmations."
        )]
        export_stickers: bool,
        #[arg(
            long,
            help = "Confirm you understand cosmetic export/alignment may carry Valve Game Server Login Token risk."
        )]
        acknowledge_cosmetic_gslt_risk: bool,
        #[arg(
            long,
            help = "Confirm you accept responsibility for using exported cosmetic metadata only where appropriate."
        )]
        accept_cosmetic_export_disclaimer: bool,
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

pub(crate) fn run() -> cs2_demotracer::Result<()> {
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
            export_cosmetics,
            export_stickers,
            acknowledge_cosmetic_gslt_risk,
            accept_cosmetic_export_disclaimer,
        } => {
            let (export_cosmetics, export_stickers) = validate_cosmetic_export_consent(
                export_cosmetics,
                export_stickers,
                acknowledge_cosmetic_gslt_risk,
                accept_cosmetic_export_disclaimer,
            )?;
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
                    export_cosmetics,
                    export_stickers,
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
            export_cosmetics,
            export_stickers,
            acknowledge_cosmetic_gslt_risk,
            accept_cosmetic_export_disclaimer,
        } => {
            let (export_cosmetics, export_stickers) = validate_cosmetic_export_consent(
                export_cosmetics,
                export_stickers,
                acknowledge_cosmetic_gslt_risk,
                accept_cosmetic_export_disclaimer,
            )?;
            let report = build_round_pool(&BuildPoolOptions {
                demo_dir,
                output_dir: output,
                map,
                recursive,
                include_suspicious,
                cut_before_bomb_plant: !full_round,
                subtick_mode: subticks,
                freeze_preroll_seconds,
                export_cosmetics,
                export_stickers,
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
            export_cosmetics: false,
            export_stickers: false,
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

fn validate_cosmetic_export_consent(
    export_cosmetics: bool,
    export_stickers: bool,
    acknowledge_gslt_risk: bool,
    accept_disclaimer: bool,
) -> cs2_demotracer::Result<(bool, bool)> {
    if export_stickers && !export_cosmetics {
        return Err(cs2_demotracer::Error::InvalidDemo(
            "--export-stickers requires --export-cosmetics".to_string(),
        ));
    }

    if !export_cosmetics && (acknowledge_gslt_risk || accept_disclaimer) {
        return Err(cs2_demotracer::Error::InvalidDemo(
            "cosmetic export acknowledgement flags require --export-cosmetics".to_string(),
        ));
    }

    if export_cosmetics && (!acknowledge_gslt_risk || !accept_disclaimer) {
        return Err(cs2_demotracer::Error::InvalidDemo(
            "--export-cosmetics writes demo-observed weapon/knife/glove cosmetic metadata into manifest JSON and requires both --acknowledge-cosmetic-gslt-risk and --accept-cosmetic-export-disclaimer. --export-stickers adds weapon sticker metadata under the same risk gate. Use cosmetic export/alignment only where you have assessed Valve server guideline and GSLT risk.".to_string(),
        ));
    }

    Ok((export_cosmetics, export_stickers))
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cosmetic_export_requires_both_confirmations() {
        let err = validate_cosmetic_export_consent(true, false, true, false).unwrap_err();

        assert!(err
            .to_string()
            .contains("--accept-cosmetic-export-disclaimer"));
    }

    #[test]
    fn cosmetic_export_acknowledgements_require_export_flag() {
        let err = validate_cosmetic_export_consent(false, false, true, true).unwrap_err();

        assert!(err.to_string().contains("--export-cosmetics"));
    }

    #[test]
    fn cosmetic_export_accepts_explicit_full_consent() {
        assert_eq!(
            validate_cosmetic_export_consent(true, false, true, true).unwrap(),
            (true, false)
        );
    }

    #[test]
    fn sticker_export_requires_cosmetic_export() {
        let err = validate_cosmetic_export_consent(false, true, false, false).unwrap_err();

        assert!(err.to_string().contains("--export-cosmetics"));
    }

    #[test]
    fn sticker_export_accepts_explicit_full_consent() {
        assert_eq!(
            validate_cosmetic_export_consent(true, true, true, true).unwrap(),
            (true, true)
        );
    }
}

#[derive(Default)]
struct PlayerRoundStats {
    name: String,
    team_num: u8,
    rows: usize,
    first_tick: i32,
    last_tick: i32,
    glove_item_rows: usize,
    glove_paint_rows: usize,
    glove_seed_rows: usize,
    glove_wear_rows: usize,
    glove_evidence_rows: usize,
    glove_item_defs: BTreeSet<i32>,
    glove_specs: BTreeSet<(u32, u32, u32)>,
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
                glove_item_rows: 0,
                glove_paint_rows: 0,
                glove_seed_rows: 0,
                glove_wear_rows: 0,
                glove_evidence_rows: 0,
                glove_item_defs: BTreeSet::new(),
                glove_specs: BTreeSet::new(),
            });
        entry.rows += 1;
        entry.first_tick = entry.first_tick.min(row.tick);
        entry.last_tick = entry.last_tick.max(row.tick);
        if let Some(item_def) = row.glove_item_def_index {
            entry.glove_item_rows += 1;
            entry.glove_item_defs.insert(item_def);
        }
        if row.glove_paint_kit.is_some() {
            entry.glove_paint_rows += 1;
        }
        if row.glove_paint_seed.is_some() {
            entry.glove_seed_rows += 1;
        }
        if let Some(wear) = row.glove_paint_wear {
            entry.glove_wear_rows += 1;
            if let (Some(item_def), Some(paint)) = (row.glove_item_def_index, row.glove_paint_kit) {
                if is_diagnostic_glove_item_def(item_def)
                    && paint > 0
                    && wear.is_finite()
                    && (0.0..=1.0).contains(&wear)
                {
                    entry.glove_evidence_rows += 1;
                    entry.glove_specs.insert((
                        paint,
                        row.glove_paint_seed.unwrap_or_default(),
                        wear.to_bits(),
                    ));
                }
            }
        }
        if entry.name.is_empty() && !row.name.is_empty() {
            entry.name = row.name.clone();
        }
    }

    scan
}

fn print_player_table(title: &str, players: &BTreeMap<(u8, u64), PlayerRoundStats>) {
    println!("{title}: {}", players.len());
    println!(
        "team steamid rows first_tick last_tick glove_item paint seed wear evidence specs item_defs name"
    );
    for ((_, steam_id), stats) in players {
        let team = match stats.team_num {
            2 => "T",
            3 => "CT",
            _ => "UNK",
        };
        println!(
            "{team:>3} {steam_id} {:>6} {:>10} {:>9} {:>10} {:>5} {:>4} {:>4} {:>8} {:>5} {:>9} {}",
            stats.rows,
            stats.first_tick,
            stats.last_tick,
            stats.glove_item_rows,
            stats.glove_paint_rows,
            stats.glove_seed_rows,
            stats.glove_wear_rows,
            stats.glove_evidence_rows,
            stats.glove_specs.len(),
            format_item_defs(&stats.glove_item_defs),
            stats.name
        );
    }
}

fn is_diagnostic_glove_item_def(item_def: i32) -> bool {
    (5027..=5035).contains(&item_def)
}

fn format_item_defs(item_defs: &BTreeSet<i32>) -> String {
    if item_defs.is_empty() {
        "-".to_string()
    } else {
        item_defs
            .iter()
            .map(ToString::to_string)
            .collect::<Vec<_>>()
            .join(",")
    }
}
