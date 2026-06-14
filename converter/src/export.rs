use crate::model::{
    ConversionManifest, ConvertedFile, ParsedDemo, ParsedPlayerTick, Side, CS2BM_ABI,
    CS2REC_VERSION,
};
use crate::quality::{analyze_demo, AnalysisOptions};
use crate::rec_writer::write_rec_file;
use crate::synthesis::synthesize_player_rec;
use crate::{io_error, Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug)]
pub struct ConvertOptions {
    pub output_dir: PathBuf,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub include_suspicious: bool,
    pub analysis: AnalysisOptions,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConversionReport {
    pub root: PathBuf,
    pub manifest_path: PathBuf,
    pub files_written: usize,
}

pub fn export_demo(parsed: &ParsedDemo, options: &ConvertOptions) -> Result<ConversionReport> {
    let analysis = analyze_demo(parsed, options.analysis);
    let root = options.output_dir.join(&parsed.stem);
    fs::create_dir_all(&root).map_err(|e| io_error(&root, e))?;

    let mut manifest = ConversionManifest {
        demo_path: parsed.path.clone(),
        map: parsed.map.clone(),
        tick_rate: parsed.tick_rate,
        abi: CS2BM_ABI,
        format_version: CS2REC_VERSION,
        files: Vec::new(),
    };
    let mut log = Vec::new();
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

        let round_rows: Vec<_> = parsed
            .rows
            .iter()
            .filter(|row| {
                row.round == round.round
                    && row.tick >= round.start_tick
                    && row.tick <= round.end_tick
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
            let rec =
                synthesize_player_rec(&player_rows, &parsed.map, parsed.tick_rate, round.round)?;
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
    }

    let manifest_path = root.join("manifest.json");
    let manifest_json = serde_json::to_string_pretty(&manifest)?;
    fs::write(&manifest_path, manifest_json).map_err(|e| io_error(&manifest_path, e))?;

    let log_path = root.join("conversion.log");
    log.push(format!("files_written={}", manifest.files.len()));
    fs::write(&log_path, log.join("\n")).map_err(|e| io_error(&log_path, e))?;

    Ok(ConversionReport {
        root,
        manifest_path,
        files_written: manifest.files.len(),
    })
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
