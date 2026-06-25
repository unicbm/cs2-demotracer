use super::export::{
    export_nade_clips, NadeExportOptions, NadeExportReport, NadeManifest, DEFAULT_OPENING_SECONDS,
    DEFAULT_POST_ROLL_SECONDS, DEFAULT_PRE_ROLL_SECONDS,
};
use super::library::{
    build_nade_library_quiet, build_nade_library_with_progress as build_library_with_progress,
    BuildNadeLibraryOptions, BuildNadeLibraryReport, NadeLibraryManifest, NadeMapManifest,
};
use crate::demo_reader::read_demo;
use crate::model::{ParsedDemo, Side, SubtickMode};
use crate::{io_error, Error, Result};
use serde::de::DeserializeOwned;
use std::collections::BTreeSet;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

pub use super::library::{NadeLibraryDemoStatus, NadeLibraryProgress};

#[derive(Clone, Copy, Debug)]
pub struct NadeContextOptions {
    pub pre_roll_seconds: f32,
    pub post_roll_seconds: f32,
    pub opening_seconds: f32,
}

impl Default for NadeContextOptions {
    fn default() -> Self {
        Self {
            pre_roll_seconds: DEFAULT_PRE_ROLL_SECONDS,
            post_roll_seconds: DEFAULT_POST_ROLL_SECONDS,
            opening_seconds: DEFAULT_OPENING_SECONDS,
        }
    }
}

#[derive(Clone, Debug)]
pub struct NadeClipExportRequest {
    pub demo_path: Option<PathBuf>,
    pub output_dir: PathBuf,
    pub output_stem: Option<String>,
    pub side: Side,
    pub selected_rounds: Option<BTreeSet<u32>>,
    pub context: NadeContextOptions,
    pub subtick_mode: SubtickMode,
}

impl NadeClipExportRequest {
    pub fn new(demo_path: impl Into<PathBuf>, output_dir: impl Into<PathBuf>) -> Self {
        Self {
            demo_path: Some(demo_path.into()),
            output_dir: output_dir.into(),
            output_stem: None,
            side: Side::Both,
            selected_rounds: None,
            context: NadeContextOptions::default(),
            subtick_mode: SubtickMode::Auto,
        }
    }

    pub fn for_parsed(output_dir: impl Into<PathBuf>) -> Self {
        Self {
            demo_path: None,
            output_dir: output_dir.into(),
            output_stem: None,
            side: Side::Both,
            selected_rounds: None,
            context: NadeContextOptions::default(),
            subtick_mode: SubtickMode::Auto,
        }
    }

    fn to_export_options(&self) -> NadeExportOptions {
        NadeExportOptions {
            output_dir: self.output_dir.clone(),
            output_stem: self.output_stem.clone(),
            side: self.side,
            selected_rounds: self.selected_rounds.clone(),
            pre_roll_seconds: self.context.pre_roll_seconds,
            post_roll_seconds: self.context.post_roll_seconds,
            opening_seconds: self.context.opening_seconds,
            subtick_mode: self.subtick_mode,
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct NadeDedupeOptions {
    pub enabled: bool,
    pub origin_units: f32,
    pub yaw_degrees: f32,
    pub velocity_units: f32,
}

impl Default for NadeDedupeOptions {
    fn default() -> Self {
        Self {
            enabled: true,
            origin_units: 48.0,
            yaw_degrees: 8.0,
            velocity_units: 120.0,
        }
    }
}

#[derive(Clone, Debug)]
pub struct NadeLibraryExportRequest {
    pub demo_dir: PathBuf,
    pub output_dir: PathBuf,
    pub recursive: bool,
    pub jobs: usize,
    pub max_demos: Option<usize>,
    pub map_filter: Option<String>,
    pub reuse_roots: Vec<PathBuf>,
    pub aggregate_only: bool,
    pub side: Side,
    pub context: NadeContextOptions,
    pub subtick_mode: SubtickMode,
    pub dedupe: NadeDedupeOptions,
}

impl NadeLibraryExportRequest {
    pub fn new(demo_dir: impl Into<PathBuf>, output_dir: impl Into<PathBuf>) -> Self {
        Self {
            demo_dir: demo_dir.into(),
            output_dir: output_dir.into(),
            recursive: false,
            jobs: 1,
            max_demos: None,
            map_filter: None,
            reuse_roots: Vec::new(),
            aggregate_only: false,
            side: Side::Both,
            context: NadeContextOptions::default(),
            subtick_mode: SubtickMode::Auto,
            dedupe: NadeDedupeOptions::default(),
        }
    }

    fn to_library_options(&self) -> BuildNadeLibraryOptions {
        BuildNadeLibraryOptions {
            demo_dir: self.demo_dir.clone(),
            output_dir: self.output_dir.clone(),
            recursive: self.recursive,
            jobs: self.jobs,
            max_demos: self.max_demos,
            map_filter: self.map_filter.clone(),
            reuse_roots: self.reuse_roots.clone(),
            aggregate_only: self.aggregate_only,
            side: self.side,
            pre_roll_seconds: self.context.pre_roll_seconds,
            post_roll_seconds: self.context.post_roll_seconds,
            opening_seconds: self.context.opening_seconds,
            subtick_mode: self.subtick_mode,
            dedupe: self.dedupe.enabled,
            dedupe_origin_units: self.dedupe.origin_units,
            dedupe_yaw_degrees: self.dedupe.yaw_degrees,
            dedupe_velocity_units: self.dedupe.velocity_units,
        }
    }
}

pub fn export_nade_clips_from_demo_path(
    request: &NadeClipExportRequest,
) -> Result<NadeExportReport> {
    let demo_path = request.demo_path.as_ref().ok_or_else(|| {
        Error::InvalidDemo("demo_path is required for path-based nade export".to_string())
    })?;
    let parsed = read_demo(demo_path)?;
    export_nade_clips_from_parsed(&parsed, request)
}

pub fn export_nade_clips_from_parsed(
    parsed: &ParsedDemo,
    request: &NadeClipExportRequest,
) -> Result<NadeExportReport> {
    export_nade_clips(parsed, &request.to_export_options())
}

pub fn build_nade_library(request: &NadeLibraryExportRequest) -> Result<BuildNadeLibraryReport> {
    build_nade_library_quiet(&request.to_library_options())
}

pub fn build_nade_library_with_progress<F>(
    request: &NadeLibraryExportRequest,
    progress: F,
) -> Result<BuildNadeLibraryReport>
where
    F: FnMut(NadeLibraryProgress),
{
    build_library_with_progress(&request.to_library_options(), progress)
}

pub fn read_nade_manifest(path: impl AsRef<Path>) -> Result<NadeManifest> {
    read_manifest_json(path)
}

pub fn read_nade_map_manifest(path: impl AsRef<Path>) -> Result<NadeMapManifest> {
    read_manifest_json(path)
}

pub fn read_nade_library_manifest(path: impl AsRef<Path>) -> Result<NadeLibraryManifest> {
    read_manifest_json(path)
}

fn read_manifest_json<T: DeserializeOwned>(path: impl AsRef<Path>) -> Result<T> {
    let path = path.as_ref();
    let bytes = read_maybe_brotli(path)?;
    serde_json::from_slice(&bytes).map_err(|e| {
        Error::InvalidDemo(format!(
            "{} contains invalid manifest JSON: {e}",
            path.display()
        ))
    })
}

fn read_maybe_brotli(path: &Path) -> Result<Vec<u8>> {
    let bytes = fs::read(path).map_err(|e| io_error(path, e))?;
    if path
        .extension()
        .and_then(|ext| ext.to_str())
        .is_some_and(|ext| ext.eq_ignore_ascii_case("br"))
    {
        let mut decompressed = Vec::new();
        let mut reader = brotli::Decompressor::new(bytes.as_slice(), 4096);
        reader.read_to_end(&mut decompressed).map_err(|e| {
            Error::InvalidDemo(format!(
                "{} could not be decompressed as Brotli manifest: {e}",
                path.display()
            ))
        })?;
        Ok(decompressed)
    } else {
        Ok(bytes)
    }
}

#[cfg(test)]
mod tests {
    use super::super::export::NadePhase;
    use super::super::library::{
        NadeLibraryDedupeManifest, NadeLibraryMapSummary, LIBRARY_MANIFEST_FORMAT_VERSION,
    };
    use super::*;
    use crate::model::{
        ParsedPlayerTick, ParsedProjectile, ProjectileEffectSource, ProjectileKind, ReplayLoadout,
        DEMOTRACER_ABI, DTR_FORMAT_VERSION,
    };
    use std::io::Write;

    #[test]
    fn export_from_parsed_uses_default_context() {
        let parsed = sample_demo(vec![sample_projectile(164, [10.0, 20.0, 30.0])]);
        let temp = tempfile::tempdir().unwrap();
        let mut request = NadeClipExportRequest::for_parsed(temp.path());
        request.output_stem = Some("sample".to_string());

        let report = export_nade_clips_from_parsed(&parsed, &request).unwrap();

        assert_eq!(report.manifest.pre_roll_seconds, DEFAULT_PRE_ROLL_SECONDS);
        assert_eq!(report.manifest.post_roll_seconds, DEFAULT_POST_ROLL_SECONDS);
        assert_eq!(report.manifest.opening_seconds, DEFAULT_OPENING_SECONDS);
        assert_eq!(report.manifest.clips[0].clip_start_tick, 100);
        assert_eq!(report.manifest.clips[0].clip_end_tick, 196);
    }

    #[test]
    fn export_from_parsed_applies_custom_context() {
        let parsed = sample_demo(vec![sample_projectile(164, [10.0, 20.0, 30.0])]);
        let temp = tempfile::tempdir().unwrap();
        let mut request = NadeClipExportRequest::for_parsed(temp.path());
        request.output_stem = Some("sample".to_string());
        request.context = NadeContextOptions {
            pre_roll_seconds: 0.25,
            post_roll_seconds: 0.25,
            opening_seconds: 7.5,
        };

        let report = export_nade_clips_from_parsed(&parsed, &request).unwrap();

        assert_eq!(report.manifest.pre_roll_seconds, 0.25);
        assert_eq!(report.manifest.post_roll_seconds, 0.25);
        assert_eq!(report.manifest.opening_seconds, 7.5);
        assert_eq!(report.manifest.clips[0].clip_start_tick, 148);
        assert_eq!(report.manifest.clips[0].clip_end_tick, 180);
    }

    #[test]
    fn missing_demo_path_is_a_clear_error() {
        let temp = tempfile::tempdir().unwrap();
        let request = NadeClipExportRequest::for_parsed(temp.path());

        let err = export_nade_clips_from_demo_path(&request).unwrap_err();

        assert!(err.to_string().contains("demo_path is required"));
    }

    #[test]
    fn reads_plain_and_brotli_nade_manifests() {
        let parsed = sample_demo(vec![sample_projectile(164, [10.0, 20.0, 30.0])]);
        let temp = tempfile::tempdir().unwrap();
        let mut request = NadeClipExportRequest::for_parsed(temp.path());
        request.output_stem = Some("sample".to_string());
        let report = export_nade_clips_from_parsed(&parsed, &request).unwrap();

        let plain = read_nade_manifest(&report.manifest_path).unwrap();
        let compressed = read_nade_manifest(report.root.join("nade_manifest.json.br")).unwrap();

        assert_eq!(plain.demo_id, "sample");
        assert_eq!(compressed.demo_id, "sample");
        assert_eq!(plain.clips.len(), compressed.clips.len());
    }

    #[test]
    fn reads_map_and_library_manifests() {
        let temp = tempfile::tempdir().unwrap();
        let map_manifest = NadeMapManifest {
            format_version: 1,
            abi: DEMOTRACER_ABI,
            dtr_format_version: DTR_FORMAT_VERSION,
            map: "de_mirage".to_string(),
            coordinate_mode: "map_absolute".to_string(),
            demo_count: 1,
            source_clip_count: 1,
            clip_count: 1,
            dedupe: NadeLibraryDedupeManifest {
                enabled: true,
                origin_units: 48.0,
                yaw_degrees: 8.0,
                velocity_units: 120.0,
            },
            clips: vec![test_clip("clip")],
        };
        let map_path = temp.path().join("map.json.br");
        write_brotli_json(&map_path, &map_manifest);
        let library_manifest = NadeLibraryManifest {
            format_version: LIBRARY_MANIFEST_FORMAT_VERSION,
            abi: DEMOTRACER_ABI,
            dtr_format_version: DTR_FORMAT_VERSION,
            coordinate_mode: "map_absolute".to_string(),
            demo_count: 1,
            source_clip_count: 1,
            clip_count: 1,
            maps: vec![NadeLibraryMapSummary {
                map: "de_mirage".to_string(),
                manifest: "maps/de_mirage/nade_manifest.json".to_string(),
                demos: 1,
                source_clips: 1,
                clips: 1,
            }],
        };
        let library_path = temp.path().join("library.json");
        fs::write(
            &library_path,
            serde_json::to_vec(&library_manifest).unwrap(),
        )
        .unwrap();

        let read_map = read_nade_map_manifest(&map_path).unwrap();
        let read_library = read_nade_library_manifest(&library_path).unwrap();

        assert_eq!(read_map.map, "de_mirage");
        assert_eq!(read_library.maps[0].map, "de_mirage");
    }

    #[test]
    fn read_manifest_reports_invalid_json_path() {
        let temp = tempfile::tempdir().unwrap();
        let manifest_path = temp.path().join("nade_library.json");
        fs::write(&manifest_path, "{").unwrap();

        let err = read_nade_library_manifest(&manifest_path).unwrap_err();

        assert!(err.to_string().contains("nade_library.json"));
        assert!(err.to_string().contains("contains invalid manifest JSON"));
    }

    #[test]
    fn read_manifest_reports_invalid_brotli_path() {
        let temp = tempfile::tempdir().unwrap();
        let manifest_path = temp.path().join("nade_manifest.json.br");
        fs::write(&manifest_path, b"not brotli").unwrap();

        let err = read_nade_manifest(&manifest_path).unwrap_err();

        assert!(err.to_string().contains("nade_manifest.json.br"));
        assert!(err.to_string().contains("could not be decompressed"));
    }

    #[test]
    fn library_api_defaults_are_quiet_and_progress_can_be_observed() {
        let temp = tempfile::tempdir().unwrap();
        let mut request = NadeLibraryExportRequest::new(temp.path(), temp.path().join("out"));
        request.aggregate_only = true;

        let quiet_report = build_nade_library(&request).unwrap();
        assert_eq!(quiet_report.maps_written, 0);

        let mut events = Vec::new();
        let progress_report = build_nade_library_with_progress(&request, |event| {
            events.push(event);
        })
        .unwrap();

        assert_eq!(progress_report.maps_written, 0);
        assert!(matches!(
            events.as_slice(),
            [NadeLibraryProgress::AggregateOnly { .. }]
        ));
    }

    #[test]
    fn prelude_exposes_public_nade_api_types() {
        use crate::prelude::{NadeClipExportRequest, NadeContextOptions, Side, SubtickMode};

        let mut request = NadeClipExportRequest::for_parsed("out");
        request.side = Side::Both;
        request.subtick_mode = SubtickMode::Auto;
        request.context = NadeContextOptions::default();

        assert_eq!(request.context.pre_roll_seconds, DEFAULT_PRE_ROLL_SECONDS);
    }

    fn write_brotli_json(path: &Path, value: &impl serde::Serialize) {
        let file = fs::File::create(path).unwrap();
        let json = serde_json::to_vec(value).unwrap();
        let mut writer = brotli::CompressorWriter::new(file, 4096, 6, 22);
        writer.write_all(&json).unwrap();
        writer.flush().unwrap();
    }

    fn sample_demo(projectiles: Vec<ParsedProjectile>) -> ParsedDemo {
        let rows = (100..=260).map(sample_row).collect();
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

    fn test_clip(id: &str) -> super::super::export::NadeClip {
        super::super::export::NadeClip {
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
            start_origin: [0.0, 0.0, 0.0],
            start_yaw: 0.0,
            projectile_initial_position: [0.0, 0.0, 0.0],
            projectile_initial_velocity: [1.0, 0.0, 0.0],
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
}
