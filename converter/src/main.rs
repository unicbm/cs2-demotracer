use clap::{Parser, Subcommand};
use cs2_demo_botmimic_converter::demo_reader::read_demo;
use cs2_demo_botmimic_converter::export::{export_demo, parse_round_list, ConvertOptions};
use cs2_demo_botmimic_converter::model::{Side, SubtickMode};
use cs2_demo_botmimic_converter::pool::{build_round_pool, BuildPoolOptions};
use cs2_demo_botmimic_converter::quality::{analyze_demo, AnalysisOptions};
use cs2_demo_botmimic_converter::rec_writer::read_rec_file;
use std::collections::BTreeMap;
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "cs2-demo-botmimic-converter")]
#[command(about = "CS2 .dem -> .cs2rec converter")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    Inspect {
        #[arg(long)]
        demo: PathBuf,
        #[arg(long, default_value_t = 240.0)]
        max_round_seconds: f32,
    },
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
    },
    InspectRound {
        #[arg(long)]
        demo: PathBuf,
        #[arg(long)]
        round: u32,
        #[arg(long, default_value_t = 240.0)]
        max_round_seconds: f32,
    },
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
    },
    Validate {
        #[arg(long)]
        input: PathBuf,
    },
    #[cfg(feature = "gui")]
    Gui,
}

fn main() {
    if let Err(err) = run() {
        eprintln!("error: {err}");
        std::process::exit(1);
    }
}

fn run() -> cs2_demo_botmimic_converter::Result<()> {
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
                return Err(cs2_demo_botmimic_converter::Error::InvalidDemo(format!(
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
        Command::ConvertPool {
            demo_dir,
            output,
            map,
            recursive,
            include_suspicious,
            max_round_seconds,
            full_round,
            subticks,
        } => {
            let report = build_round_pool(&BuildPoolOptions {
                demo_dir,
                output_dir: output,
                map,
                recursive,
                include_suspicious,
                cut_before_bomb_plant: !full_round,
                subtick_mode: subticks,
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
            let mut count = 0_usize;
            for path in collect_cs2rec_files(&input)? {
                let rec = read_rec_file(&path)?;
                if rec.ticks.is_empty() {
                    return Err(cs2_demo_botmimic_converter::Error::InvalidRec(format!(
                        "{} has no ticks",
                        path.display()
                    )));
                }
                count += 1;
            }
            println!("validated {count} .cs2rec files");
        }
        #[cfg(feature = "gui")]
        Command::Gui => {
            cs2_demo_botmimic_converter::gui::run_gui()?;
        }
    }
    Ok(())
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
    parsed: &cs2_demo_botmimic_converter::model::ParsedDemo,
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
    parsed: &cs2_demo_botmimic_converter::model::ParsedDemo,
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

fn collect_cs2rec_files(root: &PathBuf) -> cs2_demo_botmimic_converter::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    collect_recursively(root, &mut out)?;
    Ok(out)
}

fn collect_recursively(
    path: &PathBuf,
    out: &mut Vec<PathBuf>,
) -> cs2_demo_botmimic_converter::Result<()> {
    if path.is_file() {
        if path.extension().and_then(|e| e.to_str()) == Some("cs2rec") {
            out.push(path.clone());
        }
        return Ok(());
    }
    let entries =
        std::fs::read_dir(path).map_err(|e| cs2_demo_botmimic_converter::io_error(path, e))?;
    for entry in entries {
        let entry = entry.map_err(|e| cs2_demo_botmimic_converter::io_error(path, e))?;
        collect_recursively(&entry.path(), out)?;
    }
    Ok(())
}
