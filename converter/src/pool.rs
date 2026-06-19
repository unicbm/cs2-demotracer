use crate::demo_id::{demo_id, unique_demo_id};
use crate::demo_reader::read_demo;
use crate::export::{export_demo, ConvertOptions};
use crate::model::{
    public_demo_path, RoundPoolCandidate, RoundPoolManifest, Side, SubtickMode, DEMOTRACER_ABI,
};
use crate::quality::AnalysisOptions;
use crate::{io_error, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug)]
pub struct BuildPoolOptions {
    pub demo_dir: PathBuf,
    pub output_dir: PathBuf,
    pub map: String,
    pub recursive: bool,
    pub include_suspicious: bool,
    pub cut_before_bomb_plant: bool,
    pub subtick_mode: SubtickMode,
    pub freeze_preroll_seconds: f32,
    pub analysis: AnalysisOptions,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct BuildPoolReport {
    pub root: PathBuf,
    pub manifest_path: PathBuf,
    pub demos_seen: usize,
    pub demos_matched: usize,
    pub candidates: usize,
    pub failures: usize,
}

pub fn build_round_pool(options: &BuildPoolOptions) -> Result<BuildPoolReport> {
    let root = options.output_dir.clone();
    let replay_root = root.join("replays");
    fs::create_dir_all(&replay_root).map_err(|e| io_error(&replay_root, e))?;

    let mut demo_paths = Vec::new();
    collect_demo_files(&options.demo_dir, options.recursive, &mut demo_paths)?;
    demo_paths.sort();

    let mut pool = RoundPoolManifest {
        format_version: 1,
        abi: DEMOTRACER_ABI,
        map: options.map.clone(),
        candidates: Vec::new(),
    };
    let mut used_ids = BTreeSet::new();
    let mut log = Vec::new();
    let mut demos_matched = 0_usize;
    let mut failures = 0_usize;

    for demo_path in &demo_paths {
        match read_demo(demo_path) {
            Ok(parsed) => {
                if !map_matches(&parsed.map, &options.map) {
                    log.push(format!("skip {}: map={}", demo_path.display(), parsed.map));
                    continue;
                }
                demos_matched += 1;
                let base_demo_id = demo_id(&parsed.stem, &parsed.demo_sha256);
                let demo_id = unique_demo_id(&base_demo_id, &mut used_ids);
                let report = export_demo(
                    &parsed,
                    &ConvertOptions {
                        output_dir: replay_root.clone(),
                        output_stem: Some(demo_id.clone()),
                        side: Side::Both,
                        selected_rounds: None,
                        include_suspicious: options.include_suspicious,
                        cut_before_bomb_plant: options.cut_before_bomb_plant,
                        subtick_mode: options.subtick_mode,
                        freeze_preroll_seconds: options.freeze_preroll_seconds,
                        analysis: options.analysis,
                    },
                )?;
                let manifest_rel = report
                    .manifest_path
                    .strip_prefix(&root)
                    .unwrap_or(&report.manifest_path)
                    .to_string_lossy()
                    .replace('\\', "/");

                let round_count = report.manifest.rounds.len();
                for round in &report.manifest.rounds {
                    if round.files == 0 {
                        continue;
                    }
                    pool.candidates.push(RoundPoolCandidate {
                        manifest: manifest_rel.clone(),
                        demo_stem: parsed.stem.clone(),
                        demo_path: public_demo_path(&parsed.path),
                        source_round: round.round,
                        pistol_round: round.pistol_round,
                        t_economy: round.t_economy.clone(),
                        ct_economy: round.ct_economy.clone(),
                        duration_seconds: round.duration_seconds,
                        cut_reason: round.cut_reason.clone(),
                        files: round.files,
                    });
                }
                log.push(format!(
                    "converted {}: id={} files={} candidates={}",
                    demo_path.display(),
                    demo_id,
                    report.files_written,
                    round_count
                ));
            }
            Err(err) => {
                failures += 1;
                log.push(format!("failed {}: {err}", demo_path.display()));
            }
        }
    }

    let manifest_path = root.join("pool_manifest.json");
    fs::write(&manifest_path, serde_json::to_string_pretty(&pool)?)
        .map_err(|e| io_error(&manifest_path, e))?;
    let log_path = root.join("pool_conversion.log");
    log.push(format!("demos_seen={}", demo_paths.len()));
    log.push(format!("demos_matched={demos_matched}"));
    log.push(format!("candidates={}", pool.candidates.len()));
    log.push(format!("failures={failures}"));
    fs::write(&log_path, log.join("\n")).map_err(|e| io_error(&log_path, e))?;

    Ok(BuildPoolReport {
        root,
        manifest_path,
        demos_seen: demo_paths.len(),
        demos_matched,
        candidates: pool.candidates.len(),
        failures,
    })
}

fn collect_demo_files(path: &Path, recursive: bool, out: &mut Vec<PathBuf>) -> Result<()> {
    if path.is_file() {
        if is_demo_file(path) {
            out.push(path.to_path_buf());
        }
        return Ok(());
    }

    let entries = fs::read_dir(path).map_err(|e| io_error(path, e))?;
    for entry in entries {
        let entry = entry.map_err(|e| io_error(path, e))?;
        let path = entry.path();
        if path.is_dir() && recursive {
            collect_demo_files(&path, recursive, out)?;
        } else if is_demo_file(&path) {
            out.push(path);
        }
    }
    Ok(())
}

fn is_demo_file(path: &Path) -> bool {
    path.extension()
        .and_then(|ext| ext.to_str())
        .is_some_and(|ext| ext.eq_ignore_ascii_case("dem"))
}

fn map_matches(actual: &str, expected: &str) -> bool {
    let actual = actual.trim().to_ascii_lowercase();
    let expected = expected.trim().to_ascii_lowercase();
    actual == expected || actual.strip_prefix("de_") == Some(expected.as_str())
}
