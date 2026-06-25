use super::export::{
    export_nade_clips, NadeClip, NadeExportOptions, NadeManifest, NADE_MANIFEST_FORMAT_VERSION,
};
use crate::demo_id::{demo_id, sha256_hex};
use crate::demo_reader::{read_demo_bytes, read_demo_header_map_bytes};
use crate::model::{Side, SubtickMode, DEMOTRACER_ABI, DTR_FORMAT_VERSION};
use crate::{io_error, Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, VecDeque};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{mpsc, Arc, Mutex};
use std::time::{Duration, Instant};

pub const LIBRARY_MANIFEST_FORMAT_VERSION: u32 = 1;
const MANIFEST_BROTLI_BUFFER_SIZE: usize = 4096;
const MANIFEST_BROTLI_QUALITY: u32 = 6;
const MANIFEST_BROTLI_LGWIN: u32 = 22;

#[derive(Clone, Debug)]
pub struct BuildNadeLibraryOptions {
    pub demo_dir: PathBuf,
    pub output_dir: PathBuf,
    pub recursive: bool,
    pub jobs: usize,
    pub max_demos: Option<usize>,
    pub map_filter: Option<String>,
    pub reuse_roots: Vec<PathBuf>,
    pub aggregate_only: bool,
    pub side: Side,
    pub pre_roll_seconds: f32,
    pub post_roll_seconds: f32,
    pub opening_seconds: f32,
    pub subtick_mode: SubtickMode,
    pub dedupe: bool,
    pub dedupe_origin_units: f32,
    pub dedupe_yaw_degrees: f32,
    pub dedupe_velocity_units: f32,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct BuildNadeLibraryReport {
    pub root: PathBuf,
    pub demos_seen: usize,
    pub demos_done: usize,
    pub demos_converted: usize,
    pub demos_reused: usize,
    pub demos_skipped_existing: usize,
    pub demos_filtered_map: usize,
    pub failures: usize,
    pub maps_written: usize,
    pub source_clips: usize,
    pub clips: usize,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeLibraryManifest {
    pub format_version: u32,
    pub abi: i32,
    pub dtr_format_version: u32,
    pub coordinate_mode: String,
    pub demo_count: usize,
    pub source_clip_count: usize,
    pub clip_count: usize,
    pub maps: Vec<NadeLibraryMapSummary>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeLibraryMapSummary {
    pub map: String,
    pub manifest: String,
    pub demos: usize,
    pub source_clips: usize,
    pub clips: usize,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeMapManifest {
    pub format_version: u32,
    pub abi: i32,
    pub dtr_format_version: u32,
    pub map: String,
    pub coordinate_mode: String,
    pub demo_count: usize,
    pub source_clip_count: usize,
    pub clip_count: usize,
    pub dedupe: NadeLibraryDedupeManifest,
    pub clips: Vec<NadeClip>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NadeLibraryDedupeManifest {
    pub enabled: bool,
    pub origin_units: f32,
    pub yaw_degrees: f32,
    pub velocity_units: f32,
}

#[derive(Clone, Debug)]
struct DedupeOptions {
    enabled: bool,
    origin_units: f32,
    yaw_degrees: f32,
    velocity_units: f32,
}

#[derive(Clone, Debug)]
struct ExistingExport {
    root: PathBuf,
    manifest: NadeManifest,
}

#[derive(Clone, Debug)]
struct DemoTask {
    demo_path: PathBuf,
}

#[derive(Clone, Debug)]
enum DemoTaskResult {
    Converted {
        demo_id: String,
        map: String,
        clips: usize,
        skipped: usize,
        elapsed: Duration,
    },
    Reused {
        demo_id: String,
        map: String,
        clips: usize,
        elapsed: Duration,
    },
    SkippedExisting {
        demo_id: String,
        map: String,
        clips: usize,
    },
    SkippedMap {
        demo_id: String,
        map: String,
        elapsed: Duration,
    },
    Failed {
        path: PathBuf,
        error: String,
        elapsed: Duration,
    },
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum NadeLibraryProgress {
    Started {
        demos: usize,
        queued: usize,
        known_existing: usize,
        reuse_roots: usize,
        jobs: usize,
    },
    Demo {
        total: usize,
        done: usize,
        worker_index: Option<usize>,
        status: NadeLibraryDemoStatus,
    },
    AggregateOnly {
        maps_written: usize,
        demos: usize,
        source_clips: usize,
        clips: usize,
    },
    Aggregated {
        maps_written: usize,
        source_clips: usize,
        clips: usize,
        result_clips: usize,
    },
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum NadeLibraryDemoStatus {
    Converted {
        demo_id: String,
        map: String,
        clips: usize,
        skipped: usize,
        elapsed_seconds: f32,
    },
    Reused {
        demo_id: String,
        map: String,
        clips: usize,
        elapsed_seconds: f32,
    },
    SkippedExisting {
        demo_id: String,
        map: String,
        clips: usize,
    },
    SkippedMap {
        demo_id: String,
        map: String,
        elapsed_seconds: f32,
    },
    Failed {
        path: PathBuf,
        error: String,
        elapsed_seconds: f32,
    },
}

pub fn build_nade_library(options: &BuildNadeLibraryOptions) -> Result<BuildNadeLibraryReport> {
    build_nade_library_with_progress(options, |event| print_nade_library_progress(&event))
}

pub fn build_nade_library_quiet(
    options: &BuildNadeLibraryOptions,
) -> Result<BuildNadeLibraryReport> {
    build_nade_library_inner(options, None)
}

pub fn build_nade_library_with_progress<F>(
    options: &BuildNadeLibraryOptions,
    mut progress: F,
) -> Result<BuildNadeLibraryReport>
where
    F: FnMut(NadeLibraryProgress),
{
    build_nade_library_inner(options, Some(&mut progress))
}

fn build_nade_library_inner(
    options: &BuildNadeLibraryOptions,
    mut progress: Option<&mut dyn FnMut(NadeLibraryProgress)>,
) -> Result<BuildNadeLibraryReport> {
    validate_options(options)?;
    fs::create_dir_all(&options.output_dir).map_err(|e| io_error(&options.output_dir, e))?;
    let demos_root = options.output_dir.join("demos");
    fs::create_dir_all(&demos_root).map_err(|e| io_error(&demos_root, e))?;

    if options.aggregate_only {
        let dedupe_options = DedupeOptions::from_build_options(options);
        let (maps_written, source_clips, clips) = rebuild_map_manifests(
            &options.output_dir,
            &dedupe_options,
            options.map_filter.as_deref(),
        )?;
        let demo_count = scan_existing_exports(&demos_root)?
            .values()
            .filter(|export| map_matches(options.map_filter.as_deref(), &export.manifest.map))
            .count();
        emit_progress(
            &mut progress,
            NadeLibraryProgress::AggregateOnly {
                maps_written,
                demos: demo_count,
                source_clips,
                clips,
            },
        );
        return Ok(BuildNadeLibraryReport {
            root: options.output_dir.clone(),
            demos_seen: demo_count,
            demos_done: demo_count,
            demos_converted: 0,
            demos_reused: 0,
            demos_skipped_existing: demo_count,
            demos_filtered_map: 0,
            failures: 0,
            maps_written,
            source_clips,
            clips,
        });
    }

    let existing_exports = scan_existing_exports(&demos_root)?;
    let reuse_exports = scan_reuse_exports(&options.reuse_roots)?;
    let mut demo_paths = Vec::new();
    collect_demo_files(&options.demo_dir, options.recursive, &mut demo_paths)?;
    demo_paths.sort();
    if let Some(max) = options.max_demos {
        demo_paths.truncate(max);
    }

    let total = demo_paths.len();
    let tasks = demo_paths
        .into_iter()
        .map(|demo_path| DemoTask { demo_path })
        .collect::<Vec<_>>();
    emit_progress(
        &mut progress,
        NadeLibraryProgress::Started {
            demos: total,
            queued: tasks.len(),
            known_existing: existing_exports.len(),
            reuse_roots: options.reuse_roots.len(),
            jobs: options.jobs,
        },
    );

    let queue = Arc::new(Mutex::new(VecDeque::from(tasks)));
    let existing_exports = Arc::new(existing_exports);
    let reuse_exports = Arc::new(reuse_exports);
    let options = Arc::new(options.clone());
    let (tx, rx) = mpsc::channel();
    let jobs = options.jobs.max(1);
    for worker_index in 0..jobs {
        let queue = Arc::clone(&queue);
        let existing_exports = Arc::clone(&existing_exports);
        let reuse_exports = Arc::clone(&reuse_exports);
        let options = Arc::clone(&options);
        let tx = tx.clone();
        std::thread::spawn(move || loop {
            let task = {
                let mut queue = queue.lock().expect("nade library queue poisoned");
                queue.pop_front()
            };
            let Some(task) = task else {
                break;
            };
            let result = process_demo_task(&options, &existing_exports, &reuse_exports, task);
            if tx.send((worker_index, result)).is_err() {
                break;
            }
        });
    }
    drop(tx);

    let mut demos_done = 0_usize;
    let mut demos_converted = 0_usize;
    let mut demos_reused = 0_usize;
    let mut demos_skipped_existing = 0_usize;
    let mut demos_filtered_map = 0_usize;
    let mut failures = 0_usize;
    let mut clips_from_results = 0_usize;

    for (worker_index, result) in rx {
        demos_done += 1;
        match &result {
            DemoTaskResult::Converted { clips, .. } => {
                demos_converted += 1;
                clips_from_results += clips;
            }
            DemoTaskResult::Reused { clips, .. } => {
                demos_reused += 1;
                clips_from_results += clips;
            }
            DemoTaskResult::SkippedExisting { clips, .. } => {
                demos_skipped_existing += 1;
                clips_from_results += clips;
            }
            DemoTaskResult::SkippedMap { .. } => demos_filtered_map += 1,
            DemoTaskResult::Failed { .. } => failures += 1,
        }
        emit_progress(
            &mut progress,
            NadeLibraryProgress::Demo {
                total,
                done: demos_done,
                worker_index: Some(worker_index),
                status: NadeLibraryDemoStatus::from_task_result(&result),
            },
        );
    }

    let dedupe_options = DedupeOptions::from_build_options(&options);
    let (maps_written, source_clips, clips) = rebuild_map_manifests(
        &options.output_dir,
        &dedupe_options,
        options.map_filter.as_deref(),
    )?;
    emit_progress(
        &mut progress,
        NadeLibraryProgress::Aggregated {
            maps_written,
            source_clips,
            clips,
            result_clips: clips_from_results,
        },
    );

    Ok(BuildNadeLibraryReport {
        root: options.output_dir.clone(),
        demos_seen: total,
        demos_done,
        demos_converted,
        demos_reused,
        demos_skipped_existing,
        demos_filtered_map,
        failures,
        maps_written,
        source_clips,
        clips,
    })
}

fn emit_progress(
    progress: &mut Option<&mut dyn FnMut(NadeLibraryProgress)>,
    event: NadeLibraryProgress,
) {
    if let Some(callback) = progress.as_deref_mut() {
        callback(event);
    }
}

fn process_demo_task(
    options: &BuildNadeLibraryOptions,
    existing_exports: &BTreeMap<String, ExistingExport>,
    reuse_exports: &BTreeMap<String, ExistingExport>,
    task: DemoTask,
) -> DemoTaskResult {
    let started = Instant::now();
    let demos_root = options.output_dir.join("demos");
    let bytes = match fs::read(&task.demo_path) {
        Ok(bytes) => bytes,
        Err(err) => {
            return DemoTaskResult::Failed {
                path: task.demo_path,
                error: err.to_string(),
                elapsed: started.elapsed(),
            }
        }
    };
    let demo_sha256 = sha256_hex(&bytes);
    let stem = task
        .demo_path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("demo");
    let id = demo_id(stem, &demo_sha256);

    if let Some(existing) = existing_exports.get(&demo_sha256) {
        if !map_matches(options.map_filter.as_deref(), &existing.manifest.map) {
            return DemoTaskResult::SkippedMap {
                demo_id: existing.manifest.demo_id.clone(),
                map: existing.manifest.map.clone(),
                elapsed: started.elapsed(),
            };
        }
        return DemoTaskResult::SkippedExisting {
            demo_id: existing.manifest.demo_id.clone(),
            map: existing.manifest.map.clone(),
            clips: existing.manifest.clips.len(),
        };
    }

    if let Some(existing) = reuse_exports.get(&demo_sha256) {
        if !map_matches(options.map_filter.as_deref(), &existing.manifest.map) {
            return DemoTaskResult::SkippedMap {
                demo_id: existing.manifest.demo_id.clone(),
                map: existing.manifest.map.clone(),
                elapsed: started.elapsed(),
            };
        }
        let target_root = demos_root.join(&existing.manifest.demo_id);
        if let Err(err) = copy_export_root(&existing.root, &target_root) {
            return DemoTaskResult::Failed {
                path: task.demo_path,
                error: format!("failed to reuse {}: {err}", existing.root.display()),
                elapsed: started.elapsed(),
            };
        }
        return DemoTaskResult::Reused {
            demo_id: existing.manifest.demo_id.clone(),
            map: existing.manifest.map.clone(),
            clips: existing.manifest.clips.len(),
            elapsed: started.elapsed(),
        };
    }

    if let Some(filter) = options.map_filter.as_deref() {
        if let Ok(Some(map)) = read_demo_header_map_bytes(&bytes) {
            if !map_matches(Some(filter), &map) {
                return DemoTaskResult::SkippedMap {
                    demo_id: id,
                    map,
                    elapsed: started.elapsed(),
                };
            }
        }
    }

    let parsed = match read_demo_bytes(&bytes, stem, &task.demo_path.display().to_string()) {
        Ok(parsed) => parsed,
        Err(err) => {
            return DemoTaskResult::Failed {
                path: task.demo_path,
                error: err.to_string(),
                elapsed: started.elapsed(),
            }
        }
    };
    if !map_matches(options.map_filter.as_deref(), &parsed.map) {
        return DemoTaskResult::SkippedMap {
            demo_id: id,
            map: parsed.map,
            elapsed: started.elapsed(),
        };
    }

    match export_nade_clips(
        &parsed,
        &NadeExportOptions {
            output_dir: demos_root,
            output_stem: Some(id),
            side: options.side,
            selected_rounds: None,
            pre_roll_seconds: options.pre_roll_seconds,
            post_roll_seconds: options.post_roll_seconds,
            opening_seconds: options.opening_seconds,
            subtick_mode: options.subtick_mode,
        },
    ) {
        Ok(report) => DemoTaskResult::Converted {
            demo_id: report.manifest.demo_id,
            map: report.manifest.map,
            clips: report.clips_written,
            skipped: report.skipped,
            elapsed: started.elapsed(),
        },
        Err(err) => DemoTaskResult::Failed {
            path: task.demo_path,
            error: err.to_string(),
            elapsed: started.elapsed(),
        },
    }
}

fn rebuild_map_manifests(
    root: &Path,
    dedupe_options: &DedupeOptions,
    map_filter: Option<&str>,
) -> Result<(usize, usize, usize)> {
    let demos_root = root.join("demos");
    let map_root = root.join("maps");
    fs::create_dir_all(&map_root).map_err(|e| io_error(&map_root, e))?;

    let exports = scan_existing_exports(&demos_root)?;
    let mut by_map: BTreeMap<String, Vec<NadeManifest>> = BTreeMap::new();
    for export in exports.into_values() {
        if !map_matches(map_filter, &export.manifest.map) {
            continue;
        }
        by_map
            .entry(export.manifest.map.clone())
            .or_default()
            .push(export.manifest);
    }

    let mut summaries = Vec::new();
    let mut total_clips = 0_usize;
    let mut total_source_clips = 0_usize;
    for (map, mut manifests) in by_map {
        manifests.sort_by(|a, b| a.demo_id.cmp(&b.demo_id));
        let map_dir = map_root.join(&map);
        fs::create_dir_all(&map_dir).map_err(|e| io_error(&map_dir, e))?;
        let mut clips = Vec::new();
        for manifest in &manifests {
            for clip in &manifest.clips {
                let mut clip = clip.clone();
                clip.path = Path::new("..")
                    .join("..")
                    .join("demos")
                    .join(&manifest.demo_id)
                    .join(clip.path.replace('/', std::path::MAIN_SEPARATOR_STR))
                    .to_string_lossy()
                    .replace('\\', "/");
                clips.push(clip);
            }
        }
        clips.sort_by(|a, b| a.clip_id.cmp(&b.clip_id));
        let source_clip_count = clips.len();
        total_source_clips += source_clip_count;
        if dedupe_options.enabled {
            clips = dedupe_clips(clips, dedupe_options);
        }
        total_clips += clips.len();
        let map_manifest = NadeMapManifest {
            format_version: NADE_MANIFEST_FORMAT_VERSION,
            abi: DEMOTRACER_ABI,
            dtr_format_version: DTR_FORMAT_VERSION,
            map: map.clone(),
            coordinate_mode: "map_absolute".to_string(),
            demo_count: manifests.len(),
            source_clip_count,
            clip_count: clips.len(),
            dedupe: dedupe_options.to_manifest(),
            clips,
        };
        let json = serde_json::to_string_pretty(&map_manifest)?;
        let json_path = map_dir.join("nade_manifest.json");
        fs::write(&json_path, json.as_bytes()).map_err(|e| io_error(&json_path, e))?;
        write_brotli_file(&map_dir.join("nade_manifest.json.br"), json.as_bytes())?;
        summaries.push(NadeLibraryMapSummary {
            map,
            manifest: json_path
                .strip_prefix(root)
                .unwrap_or(&json_path)
                .to_string_lossy()
                .replace('\\', "/"),
            demos: manifests.len(),
            source_clips: source_clip_count,
            clips: map_manifest.clip_count,
        });
    }

    let library = NadeLibraryManifest {
        format_version: LIBRARY_MANIFEST_FORMAT_VERSION,
        abi: DEMOTRACER_ABI,
        dtr_format_version: DTR_FORMAT_VERSION,
        coordinate_mode: "map_absolute".to_string(),
        demo_count: summaries.iter().map(|summary| summary.demos).sum(),
        source_clip_count: summaries.iter().map(|summary| summary.source_clips).sum(),
        clip_count: summaries.iter().map(|summary| summary.clips).sum(),
        maps: summaries,
    };
    let json = serde_json::to_string_pretty(&library)?;
    let json_path = root.join("nade_library.json");
    fs::write(&json_path, json.as_bytes()).map_err(|e| io_error(&json_path, e))?;
    write_brotli_file(&root.join("nade_library.json.br"), json.as_bytes())?;
    Ok((library.maps.len(), total_source_clips, total_clips))
}

impl NadeLibraryDemoStatus {
    fn from_task_result(result: &DemoTaskResult) -> Self {
        match result {
            DemoTaskResult::Converted {
                demo_id,
                map,
                clips,
                skipped,
                elapsed,
            } => Self::Converted {
                demo_id: demo_id.clone(),
                map: map.clone(),
                clips: *clips,
                skipped: *skipped,
                elapsed_seconds: elapsed.as_secs_f32(),
            },
            DemoTaskResult::Reused {
                demo_id,
                map,
                clips,
                elapsed,
            } => Self::Reused {
                demo_id: demo_id.clone(),
                map: map.clone(),
                clips: *clips,
                elapsed_seconds: elapsed.as_secs_f32(),
            },
            DemoTaskResult::SkippedExisting {
                demo_id,
                map,
                clips,
            } => Self::SkippedExisting {
                demo_id: demo_id.clone(),
                map: map.clone(),
                clips: *clips,
            },
            DemoTaskResult::SkippedMap {
                demo_id,
                map,
                elapsed,
            } => Self::SkippedMap {
                demo_id: demo_id.clone(),
                map: map.clone(),
                elapsed_seconds: elapsed.as_secs_f32(),
            },
            DemoTaskResult::Failed {
                path,
                error,
                elapsed,
            } => Self::Failed {
                path: path.clone(),
                error: error.clone(),
                elapsed_seconds: elapsed.as_secs_f32(),
            },
        }
    }
}

fn map_matches(map_filter: Option<&str>, map: &str) -> bool {
    map_filter.is_none_or(|filter| filter.eq_ignore_ascii_case(map))
}

impl DedupeOptions {
    fn from_build_options(options: &BuildNadeLibraryOptions) -> Self {
        Self {
            enabled: options.dedupe,
            origin_units: options.dedupe_origin_units,
            yaw_degrees: options.dedupe_yaw_degrees,
            velocity_units: options.dedupe_velocity_units,
        }
    }

    fn to_manifest(&self) -> NadeLibraryDedupeManifest {
        NadeLibraryDedupeManifest {
            enabled: self.enabled,
            origin_units: self.origin_units,
            yaw_degrees: self.yaw_degrees,
            velocity_units: self.velocity_units,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq, Ord, PartialOrd)]
struct NadeDedupeKey {
    kind: String,
    side: String,
    phase: String,
    origin_x: i32,
    origin_y: i32,
    origin_z: i32,
    yaw: i32,
    velocity_x: i32,
    velocity_y: i32,
    velocity_z: i32,
}

fn dedupe_clips(clips: Vec<NadeClip>, options: &DedupeOptions) -> Vec<NadeClip> {
    let mut seen = BTreeMap::new();
    for clip in clips {
        let key = dedupe_key(&clip, options);
        seen.entry(key).or_insert(clip);
    }
    seen.into_values().collect()
}

fn dedupe_key(clip: &NadeClip, options: &DedupeOptions) -> NadeDedupeKey {
    NadeDedupeKey {
        kind: format!("{:?}", clip.kind),
        side: clip.side.to_ascii_lowercase(),
        phase: format!("{:?}", clip.phase),
        origin_x: quantize(clip.start_origin[0], options.origin_units),
        origin_y: quantize(clip.start_origin[1], options.origin_units),
        origin_z: quantize(clip.start_origin[2], options.origin_units),
        yaw: quantize(normalize_yaw(clip.start_yaw), options.yaw_degrees),
        velocity_x: quantize(clip.projectile_initial_velocity[0], options.velocity_units),
        velocity_y: quantize(clip.projectile_initial_velocity[1], options.velocity_units),
        velocity_z: quantize(clip.projectile_initial_velocity[2], options.velocity_units),
    }
}

fn quantize(value: f32, unit: f32) -> i32 {
    (value / unit)
        .round()
        .clamp(i32::MIN as f32, i32::MAX as f32) as i32
}

fn normalize_yaw(mut yaw: f32) -> f32 {
    while yaw > 180.0 {
        yaw -= 360.0;
    }
    while yaw < -180.0 {
        yaw += 360.0;
    }
    yaw
}

fn scan_reuse_exports(roots: &[PathBuf]) -> Result<BTreeMap<String, ExistingExport>> {
    let mut out = BTreeMap::new();
    for root in roots {
        for export in scan_manifests_under(root)? {
            out.entry(export.manifest.demo_sha256.clone())
                .or_insert(export);
        }
    }
    Ok(out)
}

fn scan_existing_exports(root: &Path) -> Result<BTreeMap<String, ExistingExport>> {
    let mut out = BTreeMap::new();
    if !root.exists() {
        return Ok(out);
    }
    for export in scan_manifests_under(root)? {
        out.insert(export.manifest.demo_sha256.clone(), export);
    }
    Ok(out)
}

fn scan_manifests_under(root: &Path) -> Result<Vec<ExistingExport>> {
    let mut manifests = Vec::new();
    if !root.exists() {
        return Ok(manifests);
    }
    let mut dirs = vec![root.to_path_buf()];
    while let Some(dir) = dirs.pop() {
        let entries = fs::read_dir(&dir).map_err(|e| io_error(&dir, e))?;
        for entry in entries {
            let entry = entry.map_err(|e| io_error(&dir, e))?;
            let path = entry.path();
            if path.is_dir() {
                dirs.push(path);
                continue;
            }
            if path.file_name().and_then(|s| s.to_str()) != Some("nade_manifest.json") {
                continue;
            }
            let json = fs::read_to_string(&path).map_err(|e| io_error(&path, e))?;
            let Ok(manifest) = serde_json::from_str::<NadeManifest>(&json) else {
                continue;
            };
            if manifest.demo_sha256.is_empty() || manifest.demo_id.is_empty() {
                continue;
            }
            let Some(parent) = path.parent() else {
                continue;
            };
            manifests.push(ExistingExport {
                root: parent.to_path_buf(),
                manifest,
            });
        }
    }
    Ok(manifests)
}

fn copy_export_root(source: &Path, target: &Path) -> Result<()> {
    if target.exists() {
        return Ok(());
    }
    fs::create_dir_all(target).map_err(|e| io_error(target, e))?;
    let entries = fs::read_dir(source).map_err(|e| io_error(source, e))?;
    for entry in entries {
        let entry = entry.map_err(|e| io_error(source, e))?;
        let source_path = entry.path();
        let target_path = target.join(entry.file_name());
        if source_path.is_dir() {
            copy_export_root(&source_path, &target_path)?;
        } else {
            fs::copy(&source_path, &target_path).map_err(|e| io_error(&target_path, e))?;
        }
    }
    Ok(())
}

pub fn print_nade_library_progress(event: &NadeLibraryProgress) {
    match event {
        NadeLibraryProgress::Started {
            demos,
            queued,
            known_existing,
            reuse_roots,
            jobs,
        } => println!(
            "nade-library: demos={demos} queued={queued} known_existing={known_existing} reuse_roots={reuse_roots} jobs={jobs}"
        ),
        NadeLibraryProgress::AggregateOnly {
            maps_written,
            demos,
            source_clips,
            clips,
        } => println!(
            "nade-library: aggregate-only maps={maps_written} demos={demos} source_clips={source_clips} clips={clips}"
        ),
        NadeLibraryProgress::Aggregated {
            maps_written,
            source_clips,
            clips,
            result_clips,
        } => println!(
            "nade-library: aggregate maps={maps_written} source_clips={source_clips} clips={clips} result_clips={result_clips}"
        ),
        NadeLibraryProgress::Demo {
            total,
            done,
            worker_index,
            status,
        } => {
            let worker = worker_index
                .map(|index| format!("w{index}"))
                .unwrap_or_else(|| "--".to_string());
            match status {
                NadeLibraryDemoStatus::Converted {
                    demo_id,
                    map,
                    clips,
                    skipped,
                    elapsed_seconds,
                } => println!(
                    "[{done}/{total}] {worker} converted map={map} demo={demo_id} clips={clips} skipped={skipped} time={:.1}s",
                    elapsed_seconds
                ),
                NadeLibraryDemoStatus::Reused {
                    demo_id,
                    map,
                    clips,
                    elapsed_seconds,
                } => println!(
                    "[{done}/{total}] {worker} reused map={map} demo={demo_id} clips={clips} time={:.1}s",
                    elapsed_seconds
                ),
                NadeLibraryDemoStatus::SkippedExisting {
                    demo_id,
                    map,
                    clips,
                } => println!(
                    "[{done}/{total}] {worker} existing map={map} demo={demo_id} clips={clips}"
                ),
                NadeLibraryDemoStatus::SkippedMap {
                    demo_id,
                    map,
                    elapsed_seconds,
                } => println!(
                    "[{done}/{total}] {worker} skipped-map map={map} demo={demo_id} time={:.1}s",
                    elapsed_seconds
                ),
                NadeLibraryDemoStatus::Failed {
                    path,
                    error,
                    elapsed_seconds,
                } => println!(
                    "[{done}/{total}] {worker} failed demo={} time={:.1}s error={error}",
                    path.display(),
                    elapsed_seconds
                ),
            }
        }
    }
    let _ = std::io::stdout().flush();
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

fn validate_options(options: &BuildNadeLibraryOptions) -> Result<()> {
    if options.jobs == 0 {
        return Err(Error::InvalidDemo("jobs must be at least 1".to_string()));
    }
    if options.pre_roll_seconds < 0.0 || !options.pre_roll_seconds.is_finite() {
        return Err(Error::InvalidDemo(
            "pre-roll seconds must be finite and non-negative".to_string(),
        ));
    }
    if options.post_roll_seconds < 0.0 || !options.post_roll_seconds.is_finite() {
        return Err(Error::InvalidDemo(
            "post-roll seconds must be finite and non-negative".to_string(),
        ));
    }
    if options.opening_seconds < 0.0 || !options.opening_seconds.is_finite() {
        return Err(Error::InvalidDemo(
            "opening seconds must be finite and non-negative".to_string(),
        ));
    }
    if options.dedupe_origin_units <= 0.0 || !options.dedupe_origin_units.is_finite() {
        return Err(Error::InvalidDemo(
            "dedupe-origin-units must be finite and positive".to_string(),
        ));
    }
    if options.dedupe_yaw_degrees <= 0.0 || !options.dedupe_yaw_degrees.is_finite() {
        return Err(Error::InvalidDemo(
            "dedupe-yaw-degrees must be finite and positive".to_string(),
        ));
    }
    if options.dedupe_velocity_units <= 0.0 || !options.dedupe_velocity_units.is_finite() {
        return Err(Error::InvalidDemo(
            "dedupe-velocity-units must be finite and positive".to_string(),
        ));
    }
    Ok(())
}

fn write_brotli_file(path: &Path, bytes: &[u8]) -> Result<()> {
    let file = fs::File::create(path).map_err(|e| io_error(path, e))?;
    let mut writer = brotli::CompressorWriter::new(
        file,
        MANIFEST_BROTLI_BUFFER_SIZE,
        MANIFEST_BROTLI_QUALITY,
        MANIFEST_BROTLI_LGWIN,
    );
    writer.write_all(bytes).map_err(|e| io_error(path, e))?;
    writer.flush().map_err(|e| io_error(path, e))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::super::export::NadePhase;
    use super::*;
    use crate::model::{ProjectileEffectSource, ProjectileKind, ReplayLoadout};

    #[test]
    fn dedupe_collapses_near_identical_nade_clips() {
        let options = DedupeOptions {
            enabled: true,
            origin_units: 48.0,
            yaw_degrees: 8.0,
            velocity_units: 120.0,
        };
        let clips = vec![
            test_clip("a", [100.0, 200.0, -40.0], 90.0, [300.0, 20.0, 450.0]),
            test_clip("b", [104.0, 204.0, -39.0], 91.0, [315.0, 10.0, 440.0]),
        ];

        let deduped = dedupe_clips(clips, &options);

        assert_eq!(deduped.len(), 1);
        assert_eq!(deduped[0].clip_id, "a");
    }

    #[test]
    fn dedupe_keeps_different_velocity_bucket() {
        let options = DedupeOptions {
            enabled: true,
            origin_units: 48.0,
            yaw_degrees: 8.0,
            velocity_units: 120.0,
        };
        let clips = vec![
            test_clip("a", [100.0, 200.0, -40.0], 91.0, [300.0, 20.0, 450.0]),
            test_clip("b", [104.0, 204.0, -39.0], 93.0, [620.0, 10.0, 440.0]),
        ];

        let deduped = dedupe_clips(clips, &options);

        assert_eq!(deduped.len(), 2);
    }

    #[test]
    fn rebuild_map_manifests_honors_map_filter() {
        let temp = tempfile::tempdir().unwrap();
        write_test_export(temp.path(), "mirage_demo", "sha_mirage", "de_mirage");
        write_test_export(temp.path(), "inferno_demo", "sha_inferno", "de_inferno");
        let options = DedupeOptions {
            enabled: false,
            origin_units: 48.0,
            yaw_degrees: 8.0,
            velocity_units: 120.0,
        };

        let (maps, source_clips, clips) =
            rebuild_map_manifests(temp.path(), &options, Some("de_mirage")).unwrap();

        assert_eq!(maps, 1);
        assert_eq!(source_clips, 1);
        assert_eq!(clips, 1);
        assert!(temp
            .path()
            .join("maps/de_mirage/nade_manifest.json")
            .exists());
        assert!(!temp
            .path()
            .join("maps/de_inferno/nade_manifest.json")
            .exists());
    }

    fn test_clip(id: &str, origin: [f32; 3], yaw: f32, velocity: [f32; 3]) -> NadeClip {
        NadeClip {
            clip_id: id.to_string(),
            path: format!("{id}.dtr"),
            kind: ProjectileKind::Smoke,
            grenade_type: "smoke".to_string(),
            weapon_def_index: 45,
            phase: NadePhase::Combat,
            round: 1,
            side: "t".to_string(),
            steam_id: 1,
            player_name: "tester".to_string(),
            throw_tick: 100,
            clip_start_tick: 90,
            clip_end_tick: 110,
            release_tick_index: 10,
            start_origin: origin,
            start_yaw: yaw,
            projectile_initial_position: origin,
            projectile_initial_velocity: velocity,
            projectile_detonation_position: [0.0, 0.0, 0.0],
            projectile_effect_position: [0.0, 0.0, 0.0],
            projectile_effect_tick: None,
            projectile_effect_source: ProjectileEffectSource::Unknown,
            projectile_effect_confidence: 0.0,
            first_weapon_def_index: 45,
            preload_weapon_def_indices: vec![45],
            loadout: ReplayLoadout::default(),
            timing: super::super::export::NadeTiming::default(),
            source_context: super::super::export::NadeSourceContext {
                source_tick_rate: 64.0,
                rows: 2,
                ticks: 2,
                subticks: 0,
                release_game_time: None,
            },
        }
    }

    fn write_test_export(root: &Path, demo_id: &str, sha: &str, map: &str) {
        let export_root = root.join("demos").join(demo_id);
        fs::create_dir_all(&export_root).unwrap();
        let manifest = NadeManifest {
            format_version: NADE_MANIFEST_FORMAT_VERSION,
            demo_path: format!("{demo_id}.dem"),
            demo_id: demo_id.to_string(),
            demo_sha256: sha.to_string(),
            map: map.to_string(),
            tick_rate: 64.0,
            abi: DEMOTRACER_ABI,
            dtr_format_version: DTR_FORMAT_VERSION,
            coordinate_mode: "map_absolute".to_string(),
            pre_roll_seconds: 1.0,
            post_roll_seconds: 0.5,
            opening_seconds: 20.0,
            clips: vec![test_clip(
                &format!("{demo_id}_clip"),
                [100.0, 200.0, -40.0],
                90.0,
                [300.0, 20.0, 450.0],
            )],
            skipped: Vec::new(),
        };
        fs::write(
            export_root.join("nade_manifest.json"),
            serde_json::to_string_pretty(&manifest).unwrap(),
        )
        .unwrap();
    }
}
