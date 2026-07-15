use cs2_demotracer::demo_id::output_demo_id;
use cs2_demotracer::demo_reader::{read_demo_with_options, ReadDemoOptions};
use cs2_demotracer::dtr::read_rec_file;
use cs2_demotracer::export::{
    export_demo_to_root_with_progress, ConversionArtifactKind, ConversionProgress,
    ConversionReport, ConvertOptions, DEFAULT_FREEZE_PREROLL_SECONDS,
};
use cs2_demotracer::model::{
    public_demo_path, ConvertedFile, DemoAnalysis, ParsedDemo, RoundStatus, Side, SubtickMode,
    DEMOTRACER_ABI, DTR_FORMAT_VERSION,
};
use cs2_demotracer::quality::{analyze_demo as analyze_parsed_demo, AnalysisOptions};
use cs2_demotracer::validate::validate_dtr_path;
use cs2_demotracer::voice_export::export_round_voice_sidecars;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use tauri::ipc::Channel;
use tauri::State;

const COSMETIC_CONFIRMATION_PHRASE: &str = "I ACCEPT COSMETIC EXPORT RISK";
const MAX_FREEZE_PREROLL_SECONDS: f32 = 120.0;
const MAX_MANIFEST_BYTES: u64 = 32 * 1024 * 1024;
const MIN_SUPPORTED_MANIFEST_ABI: i32 = 12;
const MIN_SUPPORTED_DTR_FORMAT_VERSION: u32 = 3;
const OUTPUT_COMPLETION_MARKER: &str = ".demotracer-complete";
const OUTPUT_COMPLETION_MARKER_CONTENT: &[u8] = b"CS2 DemoTracer output completed successfully.\n";

static NEXT_STAGING_NONCE: AtomicU64 = AtomicU64::new(1);

type CommandResult<T> = Result<T, CommandErrorDto>;

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CommandErrorDto {
    pub code: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

impl CommandErrorDto {
    fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            path: None,
        }
    }

    fn at_path(
        code: impl Into<String>,
        message: impl Into<String>,
        path: impl AsRef<Path>,
    ) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            path: Some(path.as_ref().display().to_string()),
        }
    }

    fn from_core(code: &'static str, error: cs2_demotracer::Error) -> Self {
        Self::new(code, error.to_string())
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum TaskEvent {
    Phase { phase: TaskPhase },
    Log { level: LogLevel, message: String },
    Progress { progress: ConversionProgressDto },
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskPhase {
    Parsing,
    Analyzing,
    Exporting,
    Voice,
    Validating,
    Complete,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum LogLevel {
    Info,
    Warning,
    Error,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(
    tag = "event",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ConversionProgressDto {
    AnalysisStarted,
    AnalysisFinished {
        rounds: usize,
        selected_rounds: usize,
        estimated_files: usize,
    },
    RoundSkipped {
        round: u32,
        reason: String,
    },
    RoundStarted {
        round: u32,
        estimated_players: usize,
    },
    PlayerSkipped {
        round: u32,
        steam_id: String,
        reason: String,
    },
    PlayerWritten {
        round: u32,
        steam_id: String,
        player_name: String,
        side: String,
        path: String,
        ticks: usize,
        subticks: usize,
    },
    RoundFinished {
        round: u32,
        files: usize,
    },
    ArtifactsWritingStarted {
        root: String,
        artifacts: usize,
    },
    ArtifactWritten {
        path: String,
        artifact_kind: String,
    },
    Finished {
        root: String,
        manifest_path: String,
        files_written: usize,
    },
}

impl From<ConversionProgress> for ConversionProgressDto {
    fn from(value: ConversionProgress) -> Self {
        match value {
            ConversionProgress::AnalysisStarted => Self::AnalysisStarted,
            ConversionProgress::AnalysisFinished {
                rounds,
                selected_rounds,
                estimated_files,
            } => Self::AnalysisFinished {
                rounds,
                selected_rounds,
                estimated_files,
            },
            ConversionProgress::RoundSkipped { round, reason } => {
                Self::RoundSkipped { round, reason }
            }
            ConversionProgress::RoundStarted {
                round,
                estimated_players,
            } => Self::RoundStarted {
                round,
                estimated_players,
            },
            ConversionProgress::PlayerSkipped {
                round,
                steam_id,
                reason,
            } => Self::PlayerSkipped {
                round,
                steam_id: steam_id.to_string(),
                reason,
            },
            ConversionProgress::PlayerWritten {
                round,
                steam_id,
                player_name,
                side,
                path,
                ticks,
                subticks,
            } => Self::PlayerWritten {
                round,
                steam_id: steam_id.to_string(),
                player_name,
                side,
                path,
                ticks,
                subticks,
            },
            ConversionProgress::RoundFinished { round, files } => {
                Self::RoundFinished { round, files }
            }
            ConversionProgress::ArtifactsWritingStarted { root, artifacts } => {
                Self::ArtifactsWritingStarted { root, artifacts }
            }
            ConversionProgress::ArtifactWritten { path, kind } => Self::ArtifactWritten {
                path,
                artifact_kind: artifact_kind_label(&kind).to_string(),
            },
            ConversionProgress::Finished {
                root,
                manifest_path,
                files_written,
            } => Self::Finished {
                root,
                manifest_path,
                files_written,
            },
        }
    }
}

fn artifact_kind_label(kind: &ConversionArtifactKind) -> &'static str {
    match kind {
        ConversionArtifactKind::Dtr => "dtr",
        ConversionArtifactKind::Avatar => "avatar",
        ConversionArtifactKind::Manifest => "manifest",
        ConversionArtifactKind::Log => "log",
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AnalyzeDemoRequest {
    pub path: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AnalysisDto {
    pub analysis_id: String,
    pub source_path: String,
    pub file_name: String,
    pub output_demo_id: String,
    pub map: String,
    pub tick_rate: f32,
    pub row_count: usize,
    pub rounds: Vec<RoundDto>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RoundDto {
    pub round: u32,
    pub start_tick: i32,
    pub end_tick: i32,
    pub duration_seconds: f32,
    pub t_players: usize,
    pub ct_players: usize,
    pub total_players: usize,
    pub valid_rows: usize,
    pub status: String,
    pub problems: Vec<String>,
    pub selected_by_default: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConvertDemoRequest {
    pub analysis_id: String,
    pub output_dir: String,
    pub selected_rounds: Vec<u32>,
    #[serde(default)]
    pub include_suspicious: bool,
    #[serde(default)]
    pub full_round: bool,
    #[serde(default)]
    pub side: Side,
    #[serde(default = "default_freeze_preroll_seconds")]
    pub freeze_preroll_seconds: f32,
    #[serde(default = "default_true")]
    pub export_voice: bool,
    #[serde(default)]
    pub export_cosmetics: bool,
    #[serde(default)]
    pub export_stickers: bool,
    #[serde(default)]
    pub export_charms: bool,
    #[serde(default)]
    pub cosmetic_consent: Option<CosmeticConsentDto>,
    #[serde(default)]
    pub overwrite: OverwriteModeDto,
}

fn default_freeze_preroll_seconds() -> f32 {
    DEFAULT_FREEZE_PREROLL_SECONDS
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CosmeticConsentDto {
    pub phrase: String,
}

#[derive(Debug, Clone, Copy, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum OverwriteModeDto {
    #[default]
    Deny,
    Replace,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConversionSummaryDto {
    pub root: String,
    pub manifest_path: String,
    pub files_written: usize,
    pub validated_files: usize,
    pub output_bytes: String,
    pub rounds_exported: usize,
    pub first_exported_round: Option<u32>,
    pub rounds: Vec<RoundFileCountDto>,
    pub players: Vec<PlayerSummaryDto>,
    pub voice: VoiceSummaryDto,
    pub cosmetics: CosmeticSummaryDto,
    pub commands: CommandSummaryDto,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RoundFileCountDto {
    pub round: u32,
    pub files: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerSummaryDto {
    pub team: usize,
    pub steam_id: String,
    pub name: String,
    pub rounds: usize,
    pub files: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct VoiceSummaryDto {
    pub requested: bool,
    pub sidecars: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CosmeticSummaryDto {
    pub files: usize,
    pub sticker_files: usize,
    pub charm_files: usize,
    pub preset: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandSummaryDto {
    pub go_round: String,
    pub go_sequence: String,
    pub round: String,
    pub sequence: String,
    pub cosmetic_round: Option<String>,
    pub cosmetic_sequence: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OpenOutputRequest {
    pub path: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PreflightOutputRequest {
    pub analysis_id: String,
    pub output_dir: String,
}

#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PreflightOutputDto {
    pub root: String,
    pub exists: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestArchiveDto {
    pub root: String,
    pub manifest_path: String,
    pub demo_path: String,
    pub demo_id: String,
    pub demo_sha256: String,
    pub map: String,
    pub tick_rate: f32,
    pub abi: i32,
    pub format_version: u32,
    pub compatibility: String,
    pub total_files: usize,
    pub playable_files: usize,
    pub output_bytes: String,
    pub players: Vec<PlayerSummaryDto>,
    pub voice: ManifestArchiveVoiceDto,
    pub cosmetics: CosmeticSummaryDto,
    pub rounds: Vec<ManifestArchiveRoundDto>,
    pub issues: Vec<ManifestIssueDto>,
    pub playable: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestArchiveVoiceDto {
    pub sidecars: usize,
    pub rounds: Vec<u32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestArchiveRoundDto {
    pub round: u32,
    pub files: usize,
    pub t_files: usize,
    pub ct_files: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub duration_seconds: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pistol_round: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cut_reason: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub t_economy: Option<ManifestTeamEconomyDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ct_economy: Option<ManifestTeamEconomyDto>,
    pub sequence_length: usize,
    pub available: bool,
    pub commands: CommandSummaryDto,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestTeamEconomyDto {
    pub side: String,
    pub players: usize,
    pub round_start_equipment_value: u32,
    pub equipment_value_total: u32,
    pub money_saved_total: u32,
    pub cash_spent_this_round: u32,
    pub class: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ManifestIssueDto {
    pub code: String,
    pub severity: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub round: Option<u32>,
}

impl ManifestIssueDto {
    fn warning(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            severity: "warning".to_string(),
            message: message.into(),
            path: None,
            round: None,
        }
    }

    fn error(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            severity: "error".to_string(),
            message: message.into(),
            path: None,
            round: None,
        }
    }

    fn at_file(mut self, path: impl Into<String>, round: Option<u32>) -> Self {
        self.path = Some(path.into());
        self.round = round;
        self
    }

    fn at_round(mut self, round: u32) -> Self {
        self.round = Some(round);
        self
    }
}

#[derive(Debug, Deserialize)]
struct ManifestArchiveWire {
    #[serde(default)]
    demo_path: String,
    #[serde(default)]
    demo_id: String,
    #[serde(default)]
    demo_sha256: String,
    #[serde(default)]
    map: String,
    tick_rate: Option<f32>,
    abi: Option<i32>,
    format_version: Option<u32>,
    dtr_format_version: Option<u32>,
    avatar_overrides: Option<Vec<ManifestAvatarWire>>,
    rounds: Option<Vec<ManifestRoundWire>>,
    files: Option<Vec<ManifestFileWire>>,
    candidates: Option<Vec<serde_json::Value>>,
}

#[derive(Debug, Deserialize)]
struct ManifestAvatarWire {
    steam_id: Option<u64>,
    path: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
struct ManifestRoundWire {
    round: Option<u32>,
    duration_seconds: Option<f32>,
    pistol_round: Option<bool>,
    cut_reason: Option<String>,
    t_economy: Option<ManifestTeamEconomyWire>,
    ct_economy: Option<ManifestTeamEconomyWire>,
    files: Option<usize>,
}

#[derive(Debug, Clone, Deserialize)]
struct ManifestTeamEconomyWire {
    #[serde(default)]
    side: String,
    #[serde(default)]
    players: usize,
    #[serde(default)]
    round_start_equipment_value: u32,
    #[serde(default)]
    equipment_value_total: u32,
    #[serde(default)]
    money_saved_total: u32,
    #[serde(default)]
    cash_spent_this_round: u32,
    #[serde(default)]
    class: String,
}

impl From<ManifestTeamEconomyWire> for ManifestTeamEconomyDto {
    fn from(value: ManifestTeamEconomyWire) -> Self {
        Self {
            side: value.side,
            players: value.players,
            round_start_equipment_value: value.round_start_equipment_value,
            equipment_value_total: value.equipment_value_total,
            money_saved_total: value.money_saved_total,
            cash_spent_this_round: value.cash_spent_this_round,
            class: value.class,
        }
    }
}

#[derive(Debug, Deserialize)]
struct ManifestFileWire {
    path: Option<String>,
    round: Option<u32>,
    side: Option<String>,
    steam_id: Option<u64>,
    player_name: Option<String>,
    cosmetics: Option<ManifestCosmeticsWire>,
}

#[derive(Debug, Default, Deserialize)]
struct ManifestCosmeticsWire {
    #[serde(default)]
    weapons: Vec<ManifestWeaponCosmeticWire>,
    knife: Option<serde_json::Value>,
    glove: Option<serde_json::Value>,
    agent: Option<serde_json::Value>,
}

impl ManifestCosmeticsWire {
    fn is_empty(&self) -> bool {
        self.weapons.is_empty()
            && self.knife.is_none()
            && self.glove.is_none()
            && self.agent.is_none()
    }

    fn has_stickers(&self) -> bool {
        self.weapons
            .iter()
            .any(|weapon| !weapon.stickers.is_empty())
    }

    fn has_charms(&self) -> bool {
        self.weapons.iter().any(|weapon| !weapon.charms.is_empty())
    }
}

#[derive(Debug, Default, Deserialize)]
struct ManifestWeaponCosmeticWire {
    #[serde(default)]
    stickers: Vec<serde_json::Value>,
    #[serde(default)]
    charms: Vec<serde_json::Value>,
}

#[derive(Debug, Default)]
struct ManifestRoundAccumulator {
    files: usize,
    t_files: usize,
    ct_files: usize,
    playable_files: usize,
}

#[derive(Debug)]
struct PlayableManifestFile {
    round: u32,
    side: String,
    steam_id: u64,
    player_name: String,
    has_cosmetics: bool,
    has_stickers: bool,
    has_charms: bool,
}

#[derive(Clone)]
struct CachedDemo {
    analysis_id: String,
    parsed: Arc<ParsedDemo>,
    analysis: DemoAnalysis,
}

struct AppState {
    cached: Mutex<Option<CachedDemo>>,
    busy: AtomicBool,
    next_analysis_id: AtomicU64,
}

impl Default for AppState {
    fn default() -> Self {
        Self {
            cached: Mutex::new(None),
            busy: AtomicBool::new(false),
            next_analysis_id: AtomicU64::new(1),
        }
    }
}

impl AppState {
    fn acquire_busy(&self) -> CommandResult<BusyGuard<'_>> {
        self.busy
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| {
                CommandErrorDto::new(
                    "busy",
                    "Another demo analysis or conversion is already running.",
                )
            })?;
        Ok(BusyGuard { busy: &self.busy })
    }

    fn cache(&self) -> CommandResult<MutexGuard<'_, Option<CachedDemo>>> {
        self.cached
            .lock()
            .map_err(|_| CommandErrorDto::new("state_poisoned", "Demo cache is unavailable."))
    }

    fn cached_demo(&self, analysis_id: &str) -> CommandResult<CachedDemo> {
        self.cache()?
            .as_ref()
            .filter(|cached| cached.analysis_id == analysis_id)
            .cloned()
            .ok_or_else(|| {
                CommandErrorDto::new(
                    "stale_analysis",
                    "The analyzed demo is no longer cached. Analyze it again before converting.",
                )
            })
    }

    fn session_id(&self, parsed: &ParsedDemo) -> String {
        let sequence = self.next_analysis_id.fetch_add(1, Ordering::Relaxed);
        let hash = parsed.demo_sha256.chars().take(12).collect::<String>();
        format!("{hash}-{sequence}")
    }
}

struct BusyGuard<'a> {
    busy: &'a AtomicBool,
}

impl Drop for BusyGuard<'_> {
    fn drop(&mut self) {
        self.busy.store(false, Ordering::Release);
    }
}

#[tauri::command]
async fn choose_demo() -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("Choose a CS2 demo")
            .add_filter("CS2 demo", &["dem"])
            .pick_file()
            .map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
async fn choose_manifest() -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("Choose a DemoTracer manifest")
            .add_filter("DemoTracer manifest", &["json"])
            .pick_file()
            .map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
async fn choose_output_dir() -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("Choose an output folder")
            .pick_folder()
            .map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
async fn analyze_demo(
    request: AnalyzeDemoRequest,
    events: Channel<TaskEvent>,
    state: State<'_, AppState>,
) -> CommandResult<AnalysisDto> {
    let _busy = state.acquire_busy()?;
    let source_path = validate_demo_path(&request.path)?;
    *state.cache()? = None;

    emit_phase(&events, TaskPhase::Parsing);
    emit_log(
        &events,
        LogLevel::Info,
        format!("Reading {}", source_path.display()),
    );

    let worker_path = source_path.clone();
    let worker_events = events.clone();
    let parsed_result = tauri::async_runtime::spawn_blocking(move || {
        let parsed = read_demo_with_options(
            &worker_path,
            ReadDemoOptions {
                collect_voice: true,
                collect_cosmetics: true,
            },
        )?;
        emit_phase(&worker_events, TaskPhase::Analyzing);
        let analysis = analyze_parsed_demo(&parsed, AnalysisOptions::default());
        Ok::<_, cs2_demotracer::Error>((Arc::new(parsed), analysis))
    })
    .await
    .map_err(|error| CommandErrorDto::new("analysis_worker_failed", error.to_string()))?;

    let (parsed, analysis) = parsed_result.map_err(|error| {
        emit_log(&events, LogLevel::Error, error.to_string());
        CommandErrorDto::from_core("analysis_failed", error)
    })?;
    let analysis_id = state.session_id(&parsed);
    let output_demo_id = output_demo_id(&parsed.stem, &parsed.demo_sha256, None)
        .map_err(|error| CommandErrorDto::from_core("invalid_demo_id", error))?;
    let dto = analysis_dto(&analysis_id, &source_path, &output_demo_id, &analysis);

    *state.cache()? = Some(CachedDemo {
        analysis_id,
        parsed,
        analysis,
    });
    emit_phase(&events, TaskPhase::Complete);
    Ok(dto)
}

#[tauri::command]
async fn convert_demo(
    request: ConvertDemoRequest,
    events: Channel<TaskEvent>,
    state: State<'_, AppState>,
) -> CommandResult<ConversionSummaryDto> {
    let _busy = state.acquire_busy()?;
    let cached = state.cached_demo(&request.analysis_id)?;

    let prepared = prepare_conversion(&request, &cached)?;

    let worker_events = events.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        run_conversion(cached, prepared, request, worker_events)
    })
    .await
    .map_err(|error| CommandErrorDto::new("conversion_worker_failed", error.to_string()))?;

    if let Err(error) = &result {
        emit_log(&events, LogLevel::Error, error.message.clone());
    }
    result
}

#[tauri::command]
async fn preflight_output(
    request: PreflightOutputRequest,
    state: State<'_, AppState>,
) -> CommandResult<PreflightOutputDto> {
    let cached = state.cached_demo(&request.analysis_id)?;
    preflight_output_for(&cached, &request.output_dir)
}

#[tauri::command]
async fn read_manifest(path: String) -> CommandResult<ManifestArchiveDto> {
    tauri::async_runtime::spawn_blocking(move || read_manifest_for(&path))
        .await
        .map_err(|error| CommandErrorDto::new("manifest_worker_failed", error.to_string()))?
}

#[tauri::command]
async fn open_output(request: OpenOutputRequest) -> CommandResult<()> {
    let path = PathBuf::from(request.path.trim());
    if !path.is_dir() {
        return Err(CommandErrorDto::at_path(
            "output_not_found",
            "The output folder does not exist.",
            &path,
        ));
    }
    let worker_path = path.clone();
    tauri::async_runtime::spawn_blocking(move || open_folder_path(&worker_path))
        .await
        .map_err(|error| CommandErrorDto::new("open_output_worker_failed", error.to_string()))?
        .map_err(|error| CommandErrorDto::at_path("open_output_failed", error.to_string(), &path))
}

fn validate_demo_path(value: &str) -> CommandResult<PathBuf> {
    let path = PathBuf::from(value.trim());
    let is_demo = path.is_file()
        && path
            .extension()
            .and_then(|extension| extension.to_str())
            .is_some_and(|extension| extension.eq_ignore_ascii_case("dem"));
    if !is_demo {
        return Err(CommandErrorDto::at_path(
            "invalid_demo_path",
            "Choose an existing CS2 .dem file.",
            &path,
        ));
    }
    Ok(path)
}

fn read_manifest_for(value: &str) -> CommandResult<ManifestArchiveDto> {
    let manifest_path = validate_manifest_input_path(value)?;
    let root = manifest_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .to_path_buf();
    let canonical_root = fs::canonicalize(&root).map_err(|error| {
        CommandErrorDto::at_path("manifest_root_unavailable", error.to_string(), &root)
    })?;
    let text = fs::read_to_string(&manifest_path).map_err(|error| {
        CommandErrorDto::at_path("manifest_read_failed", error.to_string(), &manifest_path)
    })?;
    let mut manifest: ManifestArchiveWire = serde_json::from_str(&text).map_err(|error| {
        CommandErrorDto::at_path("manifest_invalid_json", error.to_string(), &manifest_path)
    })?;

    if manifest.files.is_none() && manifest.candidates.is_some() {
        return Err(CommandErrorDto::at_path(
            "unsupported_manifest_kind",
            "Choose a per-demo replay manifest, not a pool manifest.",
            &manifest_path,
        ));
    }
    if manifest.files.is_none() {
        return Err(CommandErrorDto::at_path(
            "manifest_schema_invalid",
            "The selected JSON is not a per-demo replay manifest (files[] is missing).",
            &manifest_path,
        ));
    }

    let declared_rounds = manifest.rounds.take().unwrap_or_default();
    let declared_files = manifest.files.take().unwrap_or_default();
    let declared_avatars = manifest.avatar_overrides.take().unwrap_or_default();
    let total_files = declared_files.len();
    let abi = manifest.abi.unwrap_or(0);
    let declared_dtr_format_version = manifest.dtr_format_version.unwrap_or(0);
    let format_version = if declared_dtr_format_version != 0 {
        declared_dtr_format_version
    } else {
        manifest.format_version.unwrap_or(0)
    };
    let abi_supported = abi == 0 || (MIN_SUPPORTED_MANIFEST_ABI..=DEMOTRACER_ABI).contains(&abi);
    let format_supported = format_version == 0
        || (MIN_SUPPORTED_DTR_FORMAT_VERSION..=DTR_FORMAT_VERSION).contains(&format_version);
    let version_supported = abi_supported && format_supported;
    let compatibility = if !version_supported {
        "unsupported"
    } else if abi == 0 || format_version == 0 {
        "legacy"
    } else if abi == DEMOTRACER_ABI && format_version == DTR_FORMAT_VERSION {
        "current"
    } else {
        "supported"
    }
    .to_string();

    let mut issues = Vec::new();
    let mut fatal_metadata_issue = false;
    let mut fatal_manifest_structure = false;
    if manifest.abi.is_none() || abi == 0 {
        issues.push(ManifestIssueDto::warning(
            "manifest_abi_missing",
            "The manifest has no explicit ABI and is treated as legacy.",
        ));
    } else if !abi_supported {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_abi_unsupported",
            format!(
                "Manifest ABI {abi} is unsupported; expected {MIN_SUPPORTED_MANIFEST_ABI}..{DEMOTRACER_ABI}."
            ),
        ));
    }
    if manifest.dtr_format_version.is_none() && manifest.format_version.is_none()
        || format_version == 0
    {
        issues.push(ManifestIssueDto::warning(
            "manifest_format_missing",
            "The manifest has no explicit replay format version and is treated as legacy.",
        ));
    } else if !format_supported {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_format_unsupported",
            format!(
                "Replay format {format_version} is unsupported; expected {MIN_SUPPORTED_DTR_FORMAT_VERSION}..{DTR_FORMAT_VERSION}."
            ),
        ));
    }
    if manifest.map.trim().is_empty() {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_map_missing",
            "The manifest map is required for playback.",
        ));
    }
    let tick_rate = manifest.tick_rate.unwrap_or(0.0);
    if !tick_rate.is_finite() || tick_rate <= 0.0 {
        issues.push(ManifestIssueDto::warning(
            "manifest_tick_rate_invalid",
            "The manifest does not contain a positive tick rate.",
        ));
    }
    if total_files == 0 {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_files_empty",
            "The manifest does not contain replay files.",
        ));
    }
    let manifest_display = manifest_path.display().to_string();
    if manifest_display.contains(['\r', '\n']) {
        fatal_metadata_issue = true;
        issues.push(ManifestIssueDto::error(
            "manifest_path_not_console_safe",
            "The manifest path contains a line break and cannot be used in a server command.",
        ));
    }

    let mut metadata_by_round = BTreeMap::new();
    for metadata in declared_rounds {
        let Some(round) = metadata.round else {
            issues.push(ManifestIssueDto::warning(
                "manifest_round_number_missing",
                "A round metadata entry has no source round number.",
            ));
            continue;
        };
        if round > i32::MAX as u32 {
            fatal_manifest_structure = true;
            issues.push(ManifestIssueDto::error(
                "manifest_round_out_of_range",
                format!("Round {round} exceeds the server-supported integer range."),
            ));
            continue;
        }
        if metadata_by_round.contains_key(&round) {
            issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_duplicate",
                    format!("Round {round} has duplicate metadata entries."),
                )
                .at_round(round),
            );
            continue;
        }
        metadata_by_round.insert(round, metadata);
    }

    let mut rounds_by_id = BTreeMap::<u32, ManifestRoundAccumulator>::new();
    let mut playable_files = Vec::new();
    let mut replay_bytes = 0_u64;
    let mut seen_declared_paths = BTreeSet::new();
    let mut seen_canonical_paths = BTreeSet::new();

    let mut seen_avatar_steam_ids = BTreeSet::new();
    for (index, avatar) in declared_avatars.into_iter().enumerate() {
        let steam_id = avatar.steam_id.unwrap_or(0);
        let path = avatar.path.unwrap_or_default();
        if steam_id == 0 {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_avatar_steam_id_invalid",
                    format!("Avatar override {index} steam_id must be a non-zero integer."),
                )
                .at_file(path.clone(), None),
            );
        } else if !seen_avatar_steam_ids.insert(steam_id) {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_avatar_steam_id_duplicate",
                    format!("Avatar override steam_id {steam_id} is duplicated."),
                )
                .at_file(path.clone(), None),
            );
        }
        if let Err((code, message)) = validate_manifest_child_path(&path) {
            fatal_manifest_structure = true;
            issues.push(ManifestIssueDto::error(code, message).at_file(path, None));
        }
    }

    for (index, file) in declared_files.into_iter().enumerate() {
        let Some(round) = file.round else {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_round_missing",
                    format!("Manifest file {index} has no source round."),
                )
                .at_file(format!("files[{index}]"), None),
            );
            continue;
        };
        if round > i32::MAX as u32 {
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_round_out_of_range",
                    format!("Manifest file {index} round {round} exceeds the server-supported integer range."),
                )
                .at_file(file.path.clone().unwrap_or_default(), None),
            );
            continue;
        }
        let accumulator = rounds_by_id.entry(round).or_default();
        accumulator.files += 1;
        let path = file.path.unwrap_or_default();
        let side = file.side.unwrap_or_default().to_ascii_lowercase();
        let mut valid = true;
        if side == "t" {
            accumulator.t_files += 1;
        } else if side == "ct" {
            accumulator.ct_files += 1;
        } else {
            valid = false;
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_side_invalid",
                    format!("Replay side must be t or ct, got {side:?}."),
                )
                .at_file(path.clone(), Some(round)),
            );
        }

        let declared_key = normalized_manifest_path_key(&path);
        if declared_key.is_empty() || !seen_declared_paths.insert(declared_key) {
            valid = false;
            fatal_manifest_structure = true;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_path_duplicate",
                    "Replay file paths must be non-empty and unique.",
                )
                .at_file(path.clone(), Some(round)),
            );
        }

        let resolved = match resolve_manifest_dtr_path(&root, &canonical_root, &path) {
            Ok(resolved) => {
                let canonical_key = normalized_manifest_path_key(&resolved.display().to_string());
                if !seen_canonical_paths.insert(canonical_key) {
                    valid = false;
                    fatal_manifest_structure = true;
                    issues.push(
                        ManifestIssueDto::error(
                            "manifest_file_target_duplicate",
                            "Multiple manifest entries resolve to the same replay file.",
                        )
                        .at_file(path.clone(), Some(round)),
                    );
                }
                Some(resolved)
            }
            Err((code, message)) => {
                valid = false;
                if !matches!(code, "manifest_file_missing" | "manifest_file_not_regular") {
                    fatal_manifest_structure = true;
                }
                issues.push(
                    ManifestIssueDto::error(code, message).at_file(path.clone(), Some(round)),
                );
                None
            }
        };

        let steam_id = file.steam_id.unwrap_or(0);
        if steam_id == 0 {
            valid = false;
            issues.push(
                ManifestIssueDto::error(
                    "manifest_file_steam_id_invalid",
                    "Replay file steam_id must be a non-zero integer.",
                )
                .at_file(path.clone(), Some(round)),
            );
        }
        if valid {
            if let Some(resolved) = resolved.as_ref() {
                if let Err(message) =
                    validate_manifest_dtr(resolved, round, &side, steam_id, manifest.map.trim())
                {
                    valid = false;
                    issues.push(
                        ManifestIssueDto::error("manifest_file_invalid_dtr", message)
                            .at_file(path.clone(), Some(round)),
                    );
                }
            }
        }
        if !valid || resolved.is_none() {
            continue;
        }

        accumulator.playable_files += 1;
        replay_bytes = replay_bytes.saturating_add(
            resolved
                .as_ref()
                .and_then(|path| fs::metadata(path).ok())
                .map_or(0, |metadata| metadata.len()),
        );
        let cosmetics = file.cosmetics.as_ref();
        playable_files.push(PlayableManifestFile {
            round,
            side,
            steam_id,
            player_name: file
                .player_name
                .filter(|name| !name.is_empty())
                .unwrap_or_else(|| steam_id.to_string()),
            has_cosmetics: cosmetics.is_some_and(|value| !value.is_empty()),
            has_stickers: cosmetics.is_some_and(ManifestCosmeticsWire::has_stickers),
            has_charms: cosmetics.is_some_and(ManifestCosmeticsWire::has_charms),
        });
    }

    for (&round, accumulator) in &rounds_by_id {
        match metadata_by_round
            .get(&round)
            .and_then(|metadata| metadata.files)
        {
            Some(files) if files != accumulator.files => issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_file_count_mismatch",
                    format!(
                        "Round {round} metadata reports {files} files, but files[] contains {}.",
                        accumulator.files
                    ),
                )
                .at_round(round),
            ),
            None => issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_metadata_missing",
                    format!("Round {round} has replay files but incomplete round metadata."),
                )
                .at_round(round),
            ),
            _ => {}
        }
    }
    for &round in metadata_by_round.keys() {
        if !rounds_by_id.contains_key(&round) {
            issues.push(
                ManifestIssueDto::warning(
                    "manifest_round_without_files",
                    format!("Round {round} metadata has no replay files and is not selectable."),
                )
                .at_round(round),
            );
        }
    }

    let round_ids = rounds_by_id.keys().copied().collect::<Vec<_>>();
    let voice_rounds = collect_manifest_voice_rounds(&root, &canonical_root, &round_ids);
    let cosmetic_files = playable_files
        .iter()
        .filter(|file| file.has_cosmetics)
        .count();
    let sticker_files = playable_files
        .iter()
        .filter(|file| file.has_stickers)
        .count();
    let charm_files = playable_files.iter().filter(|file| file.has_charms).count();
    let cosmetic_preset = if cosmetic_files == 0 {
        None
    } else if sticker_files > 0 || charm_files > 0 {
        Some("full".to_string())
    } else {
        Some("basic".to_string())
    };

    let mut rounds = rounds_by_id
        .iter()
        .map(|(&round, accumulator)| {
            let metadata = metadata_by_round.get(&round);
            let available = version_supported
                && !fatal_metadata_issue
                && !fatal_manifest_structure
                && accumulator.files > 0
                && accumulator.playable_files == accumulator.files;
            ManifestArchiveRoundDto {
                round,
                files: accumulator.files,
                t_files: accumulator.t_files,
                ct_files: accumulator.ct_files,
                duration_seconds: metadata.and_then(|value| value.duration_seconds),
                pistol_round: metadata.and_then(|value| value.pistol_round),
                cut_reason: metadata.and_then(|value| value.cut_reason.clone()),
                t_economy: metadata
                    .and_then(|value| value.t_economy.clone())
                    .map(Into::into),
                ct_economy: metadata
                    .and_then(|value| value.ct_economy.clone())
                    .map(Into::into),
                sequence_length: 0,
                available,
                commands: build_commands(
                    &manifest_path,
                    Some(round),
                    voice_rounds.len(),
                    cosmetic_preset.as_deref(),
                ),
            }
        })
        .collect::<Vec<_>>();
    let sequence_lengths = (0..rounds.len())
        .map(|index| {
            let suffix = &rounds[index..];
            if suffix.iter().all(|round| round.available) {
                suffix.len()
            } else {
                0
            }
        })
        .collect::<Vec<_>>();
    for (round, sequence_length) in rounds.iter_mut().zip(sequence_lengths) {
        round.sequence_length = sequence_length;
    }

    let demo_path = if manifest.demo_path.trim().is_empty() {
        issues.push(ManifestIssueDto::warning(
            "manifest_demo_path_missing",
            "The manifest does not identify its source demo.",
        ));
        String::new()
    } else {
        public_demo_path(&manifest.demo_path)
    };
    let demo_id = if manifest.demo_id.trim().is_empty() {
        issues.push(ManifestIssueDto::warning(
            "manifest_demo_id_missing",
            "The manifest does not contain a demo ID; the folder name is used for display.",
        ));
        root.file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default()
    } else {
        manifest.demo_id
    };
    if manifest.demo_sha256.trim().is_empty() {
        issues.push(ManifestIssueDto::warning(
            "manifest_demo_hash_missing",
            "The manifest does not contain the source demo hash.",
        ));
    }
    let playable = rounds.iter().any(|round| round.available);

    Ok(ManifestArchiveDto {
        root: root.display().to_string(),
        manifest_path: manifest_display,
        demo_path,
        demo_id,
        demo_sha256: manifest.demo_sha256,
        map: manifest.map,
        tick_rate,
        abi,
        format_version,
        compatibility,
        total_files,
        playable_files: playable_files.len(),
        output_bytes: replay_bytes.to_string(),
        players: summarize_manifest_players(&playable_files),
        voice: ManifestArchiveVoiceDto {
            sidecars: voice_rounds.len(),
            rounds: voice_rounds,
        },
        cosmetics: CosmeticSummaryDto {
            files: cosmetic_files,
            sticker_files,
            charm_files,
            preset: cosmetic_preset,
        },
        rounds,
        issues,
        playable,
    })
}

fn validate_manifest_input_path(value: &str) -> CommandResult<PathBuf> {
    let value = value.trim();
    if value.is_empty() {
        return Err(CommandErrorDto::new(
            "invalid_manifest_path",
            "Choose a DemoTracer manifest JSON file.",
        ));
    }
    let input = PathBuf::from(value);
    let path = if input.is_absolute() {
        normalize_display_path(&input)
    } else {
        let current = std::env::current_dir()
            .map_err(|error| CommandErrorDto::new("manifest_path_failed", error.to_string()))?;
        normalize_display_path(&current.join(input))
    };
    let is_json = path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("json"));
    let metadata = fs::symlink_metadata(&path).map_err(|error| {
        CommandErrorDto::at_path("manifest_not_found", error.to_string(), &path)
    })?;
    if !is_json || !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
        return Err(CommandErrorDto::at_path(
            "invalid_manifest_path",
            "Choose an existing regular .json manifest file.",
            &path,
        ));
    }
    if metadata.len() > MAX_MANIFEST_BYTES {
        return Err(CommandErrorDto::at_path(
            "manifest_too_large",
            format!(
                "Manifest exceeds the {} MiB safety limit.",
                MAX_MANIFEST_BYTES / 1024 / 1024
            ),
            &path,
        ));
    }
    Ok(path)
}

fn normalize_display_path(path: &Path) -> PathBuf {
    let mut output = PathBuf::new();
    for component in path.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                if matches!(
                    output.components().next_back(),
                    Some(std::path::Component::Normal(_))
                ) {
                    output.pop();
                } else {
                    output.push("..");
                }
            }
            other => output.push(other.as_os_str()),
        }
    }
    output
}

fn normalized_manifest_path_key(value: &str) -> String {
    value
        .trim()
        .replace('\\', "/")
        .trim_start_matches("./")
        .to_ascii_lowercase()
}

fn validate_manifest_child_path(value: &str) -> Result<PathBuf, (&'static str, String)> {
    if value.trim().is_empty() {
        return Err((
            "manifest_file_path_empty",
            "Manifest child path is empty.".to_string(),
        ));
    }
    if value.starts_with('/')
        || value.starts_with('\\')
        || value.contains(':')
        || Path::new(value).is_absolute()
    {
        return Err((
            "manifest_file_path_absolute",
            "Manifest child path must be relative to the manifest folder.".to_string(),
        ));
    }
    let normalized = value.replace('\\', std::path::MAIN_SEPARATOR_STR);
    let relative = PathBuf::from(normalized);
    if relative.components().any(|component| {
        matches!(
            component,
            std::path::Component::ParentDir
                | std::path::Component::RootDir
                | std::path::Component::Prefix(_)
        )
    }) {
        return Err((
            "manifest_file_path_escape",
            "Manifest child path escapes the manifest folder.".to_string(),
        ));
    }
    Ok(relative)
}

fn resolve_manifest_dtr_path(
    root: &Path,
    canonical_root: &Path,
    value: &str,
) -> Result<PathBuf, (&'static str, String)> {
    let relative = validate_manifest_child_path(value)?;
    if !relative
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("dtr"))
    {
        return Err((
            "manifest_file_extension_invalid",
            "Replay file path must use the .dtr extension.".to_string(),
        ));
    }
    let candidate = root.join(&relative);
    let canonical = fs::canonicalize(&candidate).map_err(|error| {
        (
            "manifest_file_missing",
            format!("Replay file is missing or unreadable: {error}"),
        )
    })?;
    if !canonical.starts_with(canonical_root) {
        return Err((
            "manifest_file_path_escape",
            "Replay file resolves outside the manifest folder.".to_string(),
        ));
    }
    if !canonical.is_file() {
        return Err((
            "manifest_file_not_regular",
            "Replay target is not a regular file.".to_string(),
        ));
    }
    Ok(canonical)
}

fn validate_manifest_dtr(
    path: &Path,
    round: u32,
    side: &str,
    steam_id: u64,
    map: &str,
) -> Result<(), String> {
    let recording = read_rec_file(path).map_err(|error| error.to_string())?;
    if recording.ticks.is_empty() {
        return Err("Replay file contains no ticks.".to_string());
    }
    if recording.header.round != round {
        return Err(format!(
            "Replay header round {} does not match manifest round {round}.",
            recording.header.round
        ));
    }
    let expected_side = if side.eq_ignore_ascii_case("t") { 2 } else { 3 };
    if recording.header.side != expected_side {
        return Err(format!(
            "Replay header side {} does not match manifest side {side}.",
            recording.header.side
        ));
    }
    if recording.header.steam_id != steam_id {
        return Err(format!(
            "Replay header SteamID {} does not match manifest SteamID {steam_id}.",
            recording.header.steam_id
        ));
    }
    if !map.is_empty() && !recording.header.map.eq_ignore_ascii_case(map) {
        return Err(format!(
            "Replay header map {:?} does not match manifest map {map:?}.",
            recording.header.map
        ));
    }
    Ok(())
}

fn collect_manifest_voice_rounds(root: &Path, canonical_root: &Path, rounds: &[u32]) -> Vec<u32> {
    rounds
        .iter()
        .copied()
        .filter(|round| {
            let candidate = root.join("voice").join(format!("round{round:02}.dtv"));
            fs::canonicalize(candidate)
                .is_ok_and(|path| path.starts_with(canonical_root) && path.is_file())
        })
        .collect()
}

fn summarize_manifest_players(files: &[PlayableManifestFile]) -> Vec<PlayerSummaryDto> {
    let mut players: BTreeMap<u64, PlayerAccumulator> = BTreeMap::new();
    for file in files {
        let player = players
            .entry(file.steam_id)
            .or_insert_with(|| PlayerAccumulator {
                first_round: file.round,
                first_side: file.side.clone(),
                name: file.player_name.clone(),
                rounds: BTreeSet::new(),
                files: 0,
            });
        if file.round < player.first_round
            || (file.round == player.first_round
                && side_rank(&file.side) < side_rank(&player.first_side))
        {
            player.first_round = file.round;
            player.first_side = file.side.clone();
        }
        if player.name == file.steam_id.to_string() && !file.player_name.is_empty() {
            player.name = file.player_name.clone();
        }
        player.rounds.insert(file.round);
        player.files += 1;
    }
    let mut summaries = players
        .into_iter()
        .map(|(steam_id, player)| PlayerSummaryDto {
            team: team_index_from_first_side(&player.first_side),
            steam_id: steam_id.to_string(),
            name: player.name,
            rounds: player.rounds.len(),
            files: player.files,
        })
        .collect::<Vec<_>>();
    summaries.sort_by(|left, right| {
        (left.team, left.steam_id.as_str()).cmp(&(right.team, right.steam_id.as_str()))
    });
    summaries
}

fn analysis_dto(
    analysis_id: &str,
    source_path: &Path,
    output_demo_id: &str,
    analysis: &DemoAnalysis,
) -> AnalysisDto {
    AnalysisDto {
        analysis_id: analysis_id.to_string(),
        source_path: source_path.display().to_string(),
        file_name: source_path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| analysis.demo_stem.clone()),
        output_demo_id: output_demo_id.to_string(),
        map: analysis.map.clone(),
        tick_rate: analysis.tick_rate,
        row_count: analysis.row_count,
        rounds: analysis
            .rounds
            .iter()
            .map(|round| RoundDto {
                round: round.round,
                start_tick: round.start_tick,
                end_tick: round.end_tick,
                duration_seconds: round.duration_seconds,
                t_players: round.t_players,
                ct_players: round.ct_players,
                total_players: round.total_players,
                valid_rows: round.valid_rows,
                status: match round.status {
                    RoundStatus::Recommended => "recommended",
                    RoundStatus::Suspicious => "suspicious",
                }
                .to_string(),
                problems: round.problems.clone(),
                selected_by_default: round.status == RoundStatus::Recommended,
            })
            .collect(),
    }
}

struct PreparedConversion {
    output_dir: PathBuf,
    root: PathBuf,
    options: ConvertOptions,
}

#[derive(Clone, Copy, Debug)]
struct CosmeticFlags {
    cosmetics: bool,
    stickers: bool,
    charms: bool,
}

fn prepare_conversion(
    request: &ConvertDemoRequest,
    cached: &CachedDemo,
) -> CommandResult<PreparedConversion> {
    if !request.freeze_preroll_seconds.is_finite()
        || !(0.0..=MAX_FREEZE_PREROLL_SECONDS).contains(&request.freeze_preroll_seconds)
    {
        return Err(CommandErrorDto::new(
            "invalid_freeze_preroll",
            "Freeze pre-roll must be between 0 and 120 seconds.",
        ));
    }

    let selected_rounds = request
        .selected_rounds
        .iter()
        .copied()
        .collect::<BTreeSet<_>>();
    validate_round_selection(
        &cached.analysis,
        &selected_rounds,
        request.include_suspicious,
    )?;
    let cosmetics = validate_cosmetic_request(request)?;

    let (output_dir, root) = resolve_output_paths(&request.output_dir, &cached.parsed)?;
    let options = ConvertOptions {
        output_dir: output_dir.clone(),
        output_stem: None,
        side: request.side,
        selected_rounds: Some(selected_rounds),
        include_suspicious: request.include_suspicious,
        cut_before_bomb_plant: !request.full_round,
        subtick_mode: SubtickMode::Auto,
        freeze_preroll_seconds: request.freeze_preroll_seconds,
        export_cosmetics: cosmetics.cosmetics,
        export_stickers: cosmetics.stickers,
        export_charms: cosmetics.charms,
        analysis: AnalysisOptions::default(),
    };
    Ok(PreparedConversion {
        output_dir,
        root,
        options,
    })
}

fn preflight_output_for(
    cached: &CachedDemo,
    output_dir: &str,
) -> CommandResult<PreflightOutputDto> {
    let (output_dir, root) = resolve_output_paths(output_dir, &cached.parsed)?;
    let backup_root = output_backup_root(&output_dir, &root)?;
    let final_exists = path_metadata(&root)
        .map_err(|error| {
            CommandErrorDto::at_path("output_inspect_failed", error.to_string(), &root)
        })?
        .is_some();
    let backup_exists = path_metadata(&backup_root)
        .map_err(|error| {
            CommandErrorDto::at_path("output_inspect_failed", error.to_string(), &backup_root)
        })?
        .is_some();
    Ok(PreflightOutputDto {
        exists: final_exists || backup_exists,
        root: root.display().to_string(),
    })
}

fn resolve_output_paths(
    output_dir: &str,
    parsed: &ParsedDemo,
) -> CommandResult<(PathBuf, PathBuf)> {
    let output_dir = PathBuf::from(output_dir.trim());
    if output_dir.as_os_str().is_empty() {
        return Err(CommandErrorDto::new(
            "invalid_output_dir",
            "Choose an output folder before converting.",
        ));
    }
    let demo_id = output_demo_id(&parsed.stem, &parsed.demo_sha256, None)
        .map_err(|error| CommandErrorDto::from_core("invalid_demo_id", error))?;
    let root = output_dir.join(demo_id);
    Ok((output_dir, root))
}

fn validate_round_selection(
    analysis: &DemoAnalysis,
    selected: &BTreeSet<u32>,
    include_suspicious: bool,
) -> CommandResult<()> {
    if selected.is_empty() {
        return Err(CommandErrorDto::new(
            "no_rounds_selected",
            "Select at least one round to export.",
        ));
    }
    let known = analysis
        .rounds
        .iter()
        .map(|round| round.round)
        .collect::<BTreeSet<_>>();
    let unknown = selected.difference(&known).copied().collect::<Vec<_>>();
    if !unknown.is_empty() {
        return Err(CommandErrorDto::new(
            "unknown_rounds",
            format!("Selected rounds are not present in this analysis: {unknown:?}"),
        ));
    }
    if !include_suspicious {
        let suspicious = analysis
            .rounds
            .iter()
            .filter(|round| {
                selected.contains(&round.round) && round.status == RoundStatus::Suspicious
            })
            .map(|round| round.round)
            .collect::<Vec<_>>();
        if !suspicious.is_empty() {
            return Err(CommandErrorDto::new(
                "suspicious_rounds_not_allowed",
                format!("Enable suspicious rounds before exporting rounds {suspicious:?}."),
            ));
        }
    }
    Ok(())
}

fn validate_cosmetic_request(request: &ConvertDemoRequest) -> CommandResult<CosmeticFlags> {
    if !request.export_cosmetics {
        if request
            .cosmetic_consent
            .as_ref()
            .is_some_and(|consent| !consent.phrase.trim().is_empty())
        {
            return Err(CommandErrorDto::new(
                "unexpected_cosmetic_consent",
                "Cosmetic risk confirmation requires cosmetic export to be enabled.",
            ));
        }
        return Ok(CosmeticFlags {
            cosmetics: false,
            stickers: false,
            charms: false,
        });
    }

    let consent = request.cosmetic_consent.as_ref().ok_or_else(|| {
        CommandErrorDto::new(
            "cosmetic_consent_required",
            "Cosmetic export requires the exact risk-confirmation phrase.",
        )
    })?;
    if consent.phrase.trim() != COSMETIC_CONFIRMATION_PHRASE {
        return Err(CommandErrorDto::new(
            "cosmetic_consent_required",
            format!("Type {COSMETIC_CONFIRMATION_PHRASE:?} exactly."),
        ));
    }
    Ok(CosmeticFlags {
        cosmetics: true,
        stickers: request.export_stickers,
        charms: request.export_charms,
    })
}

fn run_conversion(
    cached: CachedDemo,
    prepared: PreparedConversion,
    request: ConvertDemoRequest,
    events: Channel<TaskEvent>,
) -> CommandResult<ConversionSummaryDto> {
    let final_root = prepared.root.clone();
    let output_dir = prepared.output_dir.clone();
    let mut file_ops = RealOutputFileOps;
    let transaction = run_output_transaction(
        &output_dir,
        &final_root,
        request.overwrite,
        &mut file_ops,
        |staging_root| {
            emit_phase(&events, TaskPhase::Exporting);
            let progress_events = events.clone();
            let progress_final_root = final_root.clone();
            let report = export_demo_to_root_with_progress(
                &cached.parsed,
                &prepared.options,
                staging_root,
                move |progress| {
                    if let Some(progress) =
                        public_conversion_progress(progress, &progress_final_root)
                    {
                        emit(&progress_events, TaskEvent::Progress { progress });
                    }
                },
            )
            .map_err(|error| CommandErrorDto::from_core("conversion_failed", error))?;

            let mut voice_summaries = Vec::new();
            if request.export_voice {
                emit_phase(&events, TaskPhase::Voice);
                let reports = export_round_voice_sidecars(&cached.parsed, &report)
                    .map_err(|error| CommandErrorDto::from_core("voice_export_failed", error))?;
                if reports.is_empty() {
                    emit_log(
                        &events,
                        LogLevel::Warning,
                        "Voice sidecar export skipped: the selected rounds contain no voice frames.",
                    );
                } else {
                    for voice in reports {
                        let public_path =
                            public_output_path(&voice.path, staging_root, &final_root);
                        voice_summaries.push(VoiceSidecarSummary {
                            public_path,
                            frame_count: voice.frame_count,
                            speaker_count: voice.speaker_count,
                            duration_seconds: voice.duration_seconds,
                        });
                    }
                }
            }

            emit_phase(&events, TaskPhase::Validating);
            let validated_files = validate_dtr_path(&report.root)
                .map_err(|error| CommandErrorDto::from_core("validation_failed", error))?;
            Ok(StagedConversion {
                report,
                validated_files,
                voice_summaries,
            })
        },
    )?;

    if let Some(warning) = transaction.backup_cleanup_warning {
        emit_log(&events, LogLevel::Warning, warning);
    }
    let mut staged = transaction.value;
    for voice in &staged.voice_summaries {
        emit_log(
            &events,
            LogLevel::Info,
            format!(
                "Voice sidecar {}: {} frames, {} speakers, {:.2}s",
                voice.public_path.display(),
                voice.frame_count,
                voice.speaker_count,
                voice.duration_seconds
            ),
        );
    }
    staged.report.root = final_root.clone();
    staged.report.manifest_path = final_root.join("manifest.json");
    emit(
        &events,
        TaskEvent::Progress {
            progress: ConversionProgressDto::Finished {
                root: staged.report.root.display().to_string(),
                manifest_path: staged.report.manifest_path.display().to_string(),
                files_written: staged.report.files_written,
            },
        },
    );
    let summary = summarize_conversion(
        staged.report,
        staged.validated_files,
        request.export_voice,
        staged.voice_summaries.len(),
    );
    emit_phase(&events, TaskPhase::Complete);
    Ok(summary)
}

struct StagedConversion {
    report: ConversionReport,
    validated_files: usize,
    voice_summaries: Vec<VoiceSidecarSummary>,
}

struct VoiceSidecarSummary {
    public_path: PathBuf,
    frame_count: usize,
    speaker_count: usize,
    duration_seconds: f32,
}

fn public_conversion_progress(
    progress: ConversionProgress,
    final_root: &Path,
) -> Option<ConversionProgressDto> {
    match progress {
        ConversionProgress::ArtifactsWritingStarted { artifacts, .. } => {
            Some(ConversionProgressDto::ArtifactsWritingStarted {
                root: final_root.display().to_string(),
                artifacts,
            })
        }
        ConversionProgress::Finished { .. } => None,
        other => Some(other.into()),
    }
}

fn public_output_path(path: &Path, staging_root: &Path, final_root: &Path) -> PathBuf {
    path.strip_prefix(staging_root)
        .map(|relative| final_root.join(relative))
        .unwrap_or_else(|_| {
            final_root
                .join("voice")
                .join(path.file_name().unwrap_or_default())
        })
}

fn output_exists_error(path: &Path) -> CommandErrorDto {
    CommandErrorDto::at_path(
        "output_exists",
        "Output for this demo already exists. Confirm replacement to continue.",
        path,
    )
}

#[derive(Debug)]
struct OutputTransaction<T> {
    value: T,
    backup_cleanup_warning: Option<String>,
}

trait OutputFileOps {
    fn metadata(&self, path: &Path) -> std::io::Result<Option<fs::Metadata>>;
    fn file_contents_equal(&self, path: &Path, expected: &[u8]) -> std::io::Result<bool>;
    fn create_dir_all(&mut self, path: &Path) -> std::io::Result<()>;
    fn create_dir(&mut self, path: &Path) -> std::io::Result<()>;
    fn rename(&mut self, from: &Path, to: &Path) -> std::io::Result<()>;
    fn remove_dir_all(&mut self, path: &Path) -> std::io::Result<()>;
    fn write_new_file(&mut self, path: &Path, bytes: &[u8]) -> std::io::Result<()>;
}

struct RealOutputFileOps;

impl OutputFileOps for RealOutputFileOps {
    fn metadata(&self, path: &Path) -> std::io::Result<Option<fs::Metadata>> {
        path_metadata(path)
    }

    fn file_contents_equal(&self, path: &Path, expected: &[u8]) -> std::io::Result<bool> {
        file_contents_equal(path, expected)
    }

    fn create_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
        fs::create_dir_all(path)
    }

    fn create_dir(&mut self, path: &Path) -> std::io::Result<()> {
        fs::create_dir(path)
    }

    fn rename(&mut self, from: &Path, to: &Path) -> std::io::Result<()> {
        fs::rename(from, to)
    }

    fn remove_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
        fs::remove_dir_all(path)
    }

    fn write_new_file(&mut self, path: &Path, bytes: &[u8]) -> std::io::Result<()> {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path)?;
        std::io::Write::write_all(&mut file, bytes)
    }
}

fn path_metadata(path: &Path) -> std::io::Result<Option<fs::Metadata>> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => Ok(Some(metadata)),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error),
    }
}

fn file_contents_equal(path: &Path, expected: &[u8]) -> std::io::Result<bool> {
    let Some(metadata) = path_metadata(path)? else {
        return Ok(false);
    };
    if !metadata.file_type().is_file() || metadata.len() != expected.len() as u64 {
        return Ok(false);
    }
    let mut file = fs::File::open(path)?;
    let mut actual = vec![0_u8; expected.len()];
    std::io::Read::read_exact(&mut file, &mut actual)?;
    let mut trailing = [0_u8; 1];
    if std::io::Read::read(&mut file, &mut trailing)? != 0 {
        return Ok(false);
    }
    Ok(actual == expected)
}

fn output_backup_root(output_dir: &Path, final_root: &Path) -> CommandResult<PathBuf> {
    if final_root.parent() != Some(output_dir) {
        return Err(CommandErrorDto::at_path(
            "unsafe_output_root",
            "Refusing to use transaction state outside the selected folder.",
            final_root,
        ));
    }
    let demo_id = output_root_name(final_root)?.to_string_lossy();
    Ok(output_dir.join(format!(".{demo_id}.backup")))
}

fn output_root_name(final_root: &Path) -> CommandResult<&std::ffi::OsStr> {
    final_root
        .file_name()
        .filter(|name| !name.is_empty())
        .ok_or_else(|| {
            CommandErrorDto::at_path(
                "unsafe_output_root",
                "Output root must have a demo identifier.",
                final_root,
            )
        })
}

fn run_output_transaction<T, F, O>(
    output_dir: &Path,
    final_root: &Path,
    overwrite: OverwriteModeDto,
    file_ops: &mut O,
    build: F,
) -> CommandResult<OutputTransaction<T>>
where
    F: FnOnce(&Path) -> CommandResult<T>,
    O: OutputFileOps,
{
    let backup_root = output_backup_root(output_dir, final_root)?;
    let demo_id = output_root_name(final_root)?.to_string_lossy();

    recover_output_state(file_ops, final_root, &backup_root)?;
    if let Some(metadata) = file_ops.metadata(final_root).map_err(|error| {
        CommandErrorDto::at_path("output_inspect_failed", error.to_string(), final_root)
    })? {
        if overwrite == OverwriteModeDto::Deny {
            return Err(output_exists_error(final_root));
        }
        if !metadata.file_type().is_dir() {
            return Err(CommandErrorDto::at_path(
                "output_not_directory",
                "Existing output is not a normal directory and will not be replaced.",
                final_root,
            ));
        }
    }

    file_ops.create_dir_all(output_dir).map_err(|error| {
        CommandErrorDto::at_path("output_stage_failed", error.to_string(), output_dir)
    })?;
    let staging_root = create_unique_staging_root(file_ops, output_dir, &demo_id)?;
    let value = match build(&staging_root) {
        Ok(value) => value,
        Err(error) => {
            cleanup_staging(file_ops, &staging_root);
            return Err(error);
        }
    };

    let marker_path = staging_root.join(OUTPUT_COMPLETION_MARKER);
    if let Err(error) = file_ops.write_new_file(&marker_path, OUTPUT_COMPLETION_MARKER_CONTENT) {
        cleanup_staging(file_ops, &staging_root);
        return Err(CommandErrorDto::at_path(
            "completion_marker_failed",
            error.to_string(),
            marker_path,
        ));
    }

    let backup_cleanup_warning =
        match promote_staged_output(file_ops, &staging_root, final_root, &backup_root) {
            Ok(warning) => warning,
            Err(error) => {
                cleanup_staging(file_ops, &staging_root);
                return Err(error);
            }
        };
    Ok(OutputTransaction {
        value,
        backup_cleanup_warning,
    })
}

fn recover_output_state<O: OutputFileOps>(
    file_ops: &mut O,
    final_root: &Path,
    backup_root: &Path,
) -> CommandResult<()> {
    let final_metadata = file_ops.metadata(final_root).map_err(|error| {
        CommandErrorDto::at_path("output_recovery_failed", error.to_string(), final_root)
    })?;
    let backup_metadata = file_ops.metadata(backup_root).map_err(|error| {
        CommandErrorDto::at_path("output_recovery_failed", error.to_string(), backup_root)
    })?;
    match (final_metadata, backup_metadata) {
        (None, Some(backup_metadata)) => {
            require_normal_output_directory(&backup_metadata, backup_root)?;
            file_ops.rename(backup_root, final_root).map_err(|error| {
                CommandErrorDto::at_path("output_recovery_failed", error.to_string(), backup_root)
            })
        }
        (Some(final_metadata), Some(backup_metadata)) => {
            require_normal_output_directory(&final_metadata, final_root)?;
            require_normal_output_directory(&backup_metadata, backup_root)?;
            let marker_path = final_root.join(OUTPUT_COMPLETION_MARKER);
            let marker_matches = file_ops
                .file_contents_equal(&marker_path, OUTPUT_COMPLETION_MARKER_CONTENT)
                .map_err(|error| {
                    CommandErrorDto::at_path(
                        "output_recovery_failed",
                        error.to_string(),
                        &marker_path,
                    )
                })?;
            if !marker_matches {
                return Err(CommandErrorDto::at_path(
                    "ambiguous_output_state",
                    "Both the output and its backup exist, but the output has no valid completion marker. Refusing to discard either copy.",
                    final_root,
                ));
            }
            file_ops.remove_dir_all(backup_root).map_err(|error| {
                CommandErrorDto::at_path(
                    "output_backup_cleanup_failed",
                    error.to_string(),
                    backup_root,
                )
            })
        }
        _ => Ok(()),
    }
}

fn require_normal_output_directory(metadata: &fs::Metadata, path: &Path) -> CommandResult<()> {
    if metadata.file_type().is_dir() {
        Ok(())
    } else {
        Err(CommandErrorDto::at_path(
            "output_not_directory",
            "Output transaction state contains a non-directory or symbolic link.",
            path,
        ))
    }
}

fn create_unique_staging_root<O: OutputFileOps>(
    file_ops: &mut O,
    output_dir: &Path,
    demo_id: &str,
) -> CommandResult<PathBuf> {
    for _ in 0..32 {
        let sequence = NEXT_STAGING_NONCE.fetch_add(1, Ordering::Relaxed);
        let timestamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let nonce = format!("{timestamp:x}{sequence:x}");
        let staging_root =
            output_dir.join(format!(".{demo_id}.tmp.{}.{nonce}", std::process::id()));
        match file_ops.create_dir(&staging_root) {
            Ok(()) => return Ok(staging_root),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(CommandErrorDto::at_path(
                    "output_stage_failed",
                    error.to_string(),
                    staging_root,
                ));
            }
        }
    }
    Err(CommandErrorDto::at_path(
        "output_stage_failed",
        "Could not reserve a unique staging directory.",
        output_dir,
    ))
}

fn promote_staged_output<O: OutputFileOps>(
    file_ops: &mut O,
    staging_root: &Path,
    final_root: &Path,
    backup_root: &Path,
) -> CommandResult<Option<String>> {
    let staging_metadata = file_ops.metadata(staging_root).map_err(|error| {
        CommandErrorDto::at_path("output_promote_failed", error.to_string(), staging_root)
    })?;
    let Some(staging_metadata) = staging_metadata else {
        return Err(CommandErrorDto::at_path(
            "output_promote_failed",
            "The staging directory disappeared before promotion.",
            staging_root,
        ));
    };
    require_normal_output_directory(&staging_metadata, staging_root)?;

    let final_metadata = file_ops.metadata(final_root).map_err(|error| {
        CommandErrorDto::at_path("output_promote_failed", error.to_string(), final_root)
    })?;
    let Some(final_metadata) = final_metadata else {
        file_ops.rename(staging_root, final_root).map_err(|error| {
            CommandErrorDto::at_path("output_promote_failed", error.to_string(), staging_root)
        })?;
        return Ok(None);
    };
    require_normal_output_directory(&final_metadata, final_root)?;
    if file_ops
        .metadata(backup_root)
        .map_err(|error| {
            CommandErrorDto::at_path("output_promote_failed", error.to_string(), backup_root)
        })?
        .is_some()
    {
        return Err(CommandErrorDto::at_path(
            "ambiguous_output_state",
            "A backup appeared while preparing output promotion. Refusing to overwrite it.",
            backup_root,
        ));
    }

    file_ops.rename(final_root, backup_root).map_err(|error| {
        CommandErrorDto::at_path("output_promote_failed", error.to_string(), final_root)
    })?;
    if let Err(promote_error) = file_ops.rename(staging_root, final_root) {
        if let Err(rollback_error) = file_ops.rename(backup_root, final_root) {
            return Err(CommandErrorDto::at_path(
                "output_rollback_failed",
                format!(
                    "Could not promote staged output ({promote_error}); restoring the previous output also failed ({rollback_error})."
                ),
                backup_root,
            ));
        }
        return Err(CommandErrorDto::at_path(
            "output_promote_failed",
            format!("Could not promote staged output; the previous output was restored: {promote_error}"),
            staging_root,
        ));
    }

    Ok(file_ops.remove_dir_all(backup_root).err().map(|error| {
        format!(
            "New output is ready, but the previous-output backup could not be removed ({}): {error}",
            backup_root.display()
        )
    }))
}

fn cleanup_staging<O: OutputFileOps>(file_ops: &mut O, staging_root: &Path) {
    if matches!(
        file_ops.metadata(staging_root),
        Ok(Some(metadata)) if metadata.file_type().is_dir()
    ) {
        let _ = file_ops.remove_dir_all(staging_root);
    }
}

fn summarize_conversion(
    report: ConversionReport,
    validated_files: usize,
    voice_requested: bool,
    voice_sidecars: usize,
) -> ConversionSummaryDto {
    let output_bytes = directory_size_bytes(&report.root).unwrap_or(0);
    let rounds = report
        .manifest
        .rounds
        .iter()
        .map(|round| RoundFileCountDto {
            round: round.round,
            files: round.files,
        })
        .collect::<Vec<_>>();
    let first_exported_round = rounds.iter().map(|round| round.round).min();
    let players = summarize_exported_players(&report.manifest.files);
    let cosmetic_files = report
        .manifest
        .files
        .iter()
        .filter(|file| {
            file.cosmetics
                .as_ref()
                .is_some_and(|cosmetics| !cosmetics.is_empty())
        })
        .count();
    let sticker_files = report
        .manifest
        .files
        .iter()
        .filter(|file| {
            file.cosmetics.as_ref().is_some_and(|cosmetics| {
                cosmetics
                    .weapons
                    .iter()
                    .any(|weapon| !weapon.stickers.is_empty())
            })
        })
        .count();
    let charm_files = report
        .manifest
        .files
        .iter()
        .filter(|file| {
            file.cosmetics.as_ref().is_some_and(|cosmetics| {
                cosmetics
                    .weapons
                    .iter()
                    .any(|weapon| !weapon.charms.is_empty())
            })
        })
        .count();
    let preset = if cosmetic_files == 0 {
        None
    } else if sticker_files > 0 || charm_files > 0 {
        Some("full".to_string())
    } else {
        Some("basic".to_string())
    };
    let commands = build_commands(
        &report.manifest_path,
        first_exported_round,
        voice_sidecars,
        preset.as_deref(),
    );

    ConversionSummaryDto {
        root: report.root.display().to_string(),
        manifest_path: report.manifest_path.display().to_string(),
        files_written: report.files_written,
        validated_files,
        output_bytes: output_bytes.to_string(),
        rounds_exported: rounds.len(),
        first_exported_round,
        rounds,
        players,
        voice: VoiceSummaryDto {
            requested: voice_requested,
            sidecars: voice_sidecars,
        },
        cosmetics: CosmeticSummaryDto {
            files: cosmetic_files,
            sticker_files,
            charm_files,
            preset,
        },
        commands,
    }
}

struct PlayerAccumulator {
    first_round: u32,
    first_side: String,
    name: String,
    rounds: BTreeSet<u32>,
    files: usize,
}

fn summarize_exported_players(files: &[ConvertedFile]) -> Vec<PlayerSummaryDto> {
    let mut players: BTreeMap<u64, PlayerAccumulator> = BTreeMap::new();
    for file in files {
        let player = players
            .entry(file.steam_id)
            .or_insert_with(|| PlayerAccumulator {
                first_round: file.round,
                first_side: file.side.clone(),
                name: if file.player_name.is_empty() {
                    file.steam_id.to_string()
                } else {
                    file.player_name.clone()
                },
                rounds: BTreeSet::new(),
                files: 0,
            });
        if file.round < player.first_round
            || (file.round == player.first_round
                && side_rank(&file.side) < side_rank(&player.first_side))
        {
            player.first_round = file.round;
            player.first_side = file.side.clone();
        }
        if player.name == file.steam_id.to_string() && !file.player_name.is_empty() {
            player.name = file.player_name.clone();
        }
        player.rounds.insert(file.round);
        player.files += 1;
    }

    let mut summaries = players
        .into_iter()
        .map(|(steam_id, player)| PlayerSummaryDto {
            team: team_index_from_first_side(&player.first_side),
            steam_id: steam_id.to_string(),
            name: player.name,
            rounds: player.rounds.len(),
            files: player.files,
        })
        .collect::<Vec<_>>();
    summaries.sort_by(|left, right| {
        (left.team, left.steam_id.as_str()).cmp(&(right.team, right.steam_id.as_str()))
    });
    summaries
}

fn side_rank(side: &str) -> u8 {
    if side.eq_ignore_ascii_case("t") {
        0
    } else if side.eq_ignore_ascii_case("ct") {
        1
    } else {
        2
    }
}

fn team_index_from_first_side(side: &str) -> usize {
    if side.eq_ignore_ascii_case("t") {
        1
    } else if side.eq_ignore_ascii_case("ct") {
        2
    } else {
        3
    }
}

fn build_commands(
    manifest_path: &Path,
    first_round: Option<u32>,
    voice_sidecars: usize,
    cosmetic_preset: Option<&str>,
) -> CommandSummaryDto {
    let round = first_round.unwrap_or(0);
    let manifest = console_quote_path(manifest_path);
    let go_round = format!("dtr_go round \"{manifest}\" {round}");
    let go_sequence = format!("dtr_go seq \"{manifest}\" {round}");
    CommandSummaryDto {
        go_round: go_round.clone(),
        go_sequence: go_sequence.clone(),
        round: command_with_prefixes(&go_round, voice_sidecars, None),
        sequence: command_with_prefixes(&go_sequence, voice_sidecars, None),
        cosmetic_round: cosmetic_preset
            .map(|preset| command_with_prefixes(&go_round, voice_sidecars, Some(preset))),
        cosmetic_sequence: cosmetic_preset
            .map(|preset| command_with_prefixes(&go_sequence, voice_sidecars, Some(preset))),
    }
}

fn command_with_prefixes(
    command: &str,
    voice_sidecars: usize,
    cosmetic_preset: Option<&str>,
) -> String {
    let mut prefixes = Vec::new();
    if voice_sidecars > 0 {
        prefixes.push("dtr_voice_auto on".to_string());
    }
    if let Some(preset) = cosmetic_preset {
        prefixes.push(format!("dtr_cosmetics {preset}"));
    }
    if prefixes.is_empty() {
        command.to_string()
    } else {
        format!("{}; {command}", prefixes.join("; "))
    }
}

fn console_quote_path(path: &Path) -> String {
    path.display().to_string().replace('"', "\\\"")
}

fn directory_size_bytes(path: &Path) -> std::io::Result<u64> {
    let metadata = fs::metadata(path)?;
    if metadata.is_file() {
        return Ok(metadata.len());
    }
    let mut total = 0_u64;
    for entry in fs::read_dir(path)? {
        total = total.saturating_add(directory_size_bytes(&entry?.path()).unwrap_or(0));
    }
    Ok(total)
}

fn emit(channel: &Channel<TaskEvent>, event: TaskEvent) {
    let _ = channel.send(event);
}

fn emit_phase(channel: &Channel<TaskEvent>, phase: TaskPhase) {
    emit(channel, TaskEvent::Phase { phase });
}

fn emit_log(channel: &Channel<TaskEvent>, level: LogLevel, message: impl Into<String>) {
    emit(
        channel,
        TaskEvent::Log {
            level,
            message: message.into(),
        },
    );
}

fn open_folder_path(path: &Path) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        Command::new("explorer.exe").arg(path).spawn()?;
        return Ok(());
    }

    #[cfg(target_os = "macos")]
    {
        Command::new("open").arg(path).spawn()?;
        return Ok(());
    }

    #[cfg(all(unix, not(target_os = "macos")))]
    {
        Command::new("xdg-open").arg(path).spawn()?;
        Ok(())
    }
}

pub fn run() {
    tauri::Builder::default()
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            choose_demo,
            choose_manifest,
            choose_output_dir,
            analyze_demo,
            preflight_output,
            read_manifest,
            convert_demo,
            open_output
        ])
        .run(tauri::generate_context!())
        .expect("failed to run CS2 DemoTracer desktop app");
}

#[cfg(test)]
mod tests {
    use super::*;
    use cs2_demotracer::model::{
        ConversionManifest, ConvertedRound, EconomyClass, ReplayCosmetics, ReplayItemCosmetic,
        ReplayLoadout, ReplayWeaponCharm, ReplayWeaponCosmetic, ReplayWeaponSticker, TeamEconomy,
    };

    fn round(round: u32, status: RoundStatus) -> cs2_demotracer::model::RoundSummary {
        cs2_demotracer::model::RoundSummary {
            round,
            start_tick: 100,
            end_tick: 740,
            duration_seconds: 10.0,
            t_players: 5,
            ct_players: 5,
            total_players: 10,
            valid_rows: 100,
            status,
            problems: if status == RoundStatus::Recommended {
                Vec::new()
            } else {
                vec!["test warning".to_string()]
            },
        }
    }

    fn analysis() -> DemoAnalysis {
        DemoAnalysis {
            demo_path: "match.dem".to_string(),
            demo_stem: "match".to_string(),
            map: "de_mirage".to_string(),
            tick_rate: 64.0,
            row_count: 1000,
            rounds: vec![
                round(1, RoundStatus::Recommended),
                round(2, RoundStatus::Suspicious),
            ],
        }
    }

    fn request() -> ConvertDemoRequest {
        ConvertDemoRequest {
            analysis_id: "analysis-1".to_string(),
            output_dir: "output".to_string(),
            selected_rounds: vec![1],
            include_suspicious: false,
            full_round: false,
            side: Side::Both,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_voice: true,
            export_cosmetics: false,
            export_stickers: true,
            export_charms: true,
            cosmetic_consent: None,
            overwrite: OverwriteModeDto::Deny,
        }
    }

    fn converted_file(
        round: u32,
        side: &str,
        steam_id: u64,
        name: &str,
        cosmetics: Option<ReplayCosmetics>,
    ) -> ConvertedFile {
        ConvertedFile {
            path: format!("round{round:02}/{side}/{steam_id}.dtr"),
            round,
            side: side.to_string(),
            steam_id,
            player_name: name.to_string(),
            ticks: 100,
            subticks: 0,
            play_start_tick_index: 0,
            first_weapon_def_index: -1,
            preload_weapon_def_indices: Vec::new(),
            hifi_event_count: 0,
            inventory_snapshot_count: 0,
            loadout: ReplayLoadout::default(),
            music_kit_id: None,
            scoreboard_flair: None,
            cosmetics,
            view: None,
            scoreboard: None,
        }
    }

    fn converted_round(round: u32, files: usize) -> ConvertedRound {
        ConvertedRound {
            round,
            recording_start_tick: 90,
            start_tick: 100,
            end_tick: 740,
            original_end_tick: 740,
            bomb_planted_tick: None,
            bomb_planted_seconds_after_live: None,
            freeze_preroll_ticks: 10,
            duration_seconds: 10.0,
            pistol_round: false,
            cut_reason: None,
            t_economy: TeamEconomy {
                side: "t".to_string(),
                class: EconomyClass::Full,
                ..TeamEconomy::default()
            },
            ct_economy: TeamEconomy {
                side: "ct".to_string(),
                class: EconomyClass::Full,
                ..TeamEconomy::default()
            },
            scoreboard: None,
            chat_messages: Vec::new(),
            files,
        }
    }

    struct ManifestTestDir {
        path: PathBuf,
    }

    impl ManifestTestDir {
        fn new(label: &str) -> Self {
            let unique = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let path = std::env::temp_dir().join(format!(
                "cs2-demotracer-manifest-{label}-{}-{unique}",
                std::process::id()
            ));
            fs::create_dir_all(&path).unwrap();
            Self { path }
        }

        fn write_file(&self, relative: &str, bytes: &[u8]) -> PathBuf {
            let path = self
                .path
                .join(relative.replace('/', std::path::MAIN_SEPARATOR_STR));
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(&path, bytes).unwrap();
            path
        }

        fn write_dtr(&self, relative: &str, round: u32, side: &str, steam_id: u64) -> PathBuf {
            let path = self
                .path
                .join(relative.replace('/', std::path::MAIN_SEPARATOR_STR));
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            let mut recording = cs2_demotracer::dtr::Cs2Rec::default();
            recording.header.map = "de_mirage".to_string();
            recording.header.round = round;
            recording.header.side = if side.eq_ignore_ascii_case("t") { 2 } else { 3 };
            recording.header.steam_id = steam_id;
            recording
                .ticks
                .push(cs2_demotracer::dtr::ReplayTick::default());
            cs2_demotracer::dtr::write_rec_file(&path, &recording).unwrap();
            path
        }

        fn write_manifest(&self, value: serde_json::Value) -> PathBuf {
            let path = self.path.join("manifest.json");
            fs::write(&path, serde_json::to_vec_pretty(&value).unwrap()).unwrap();
            path
        }
    }

    impl Drop for ManifestTestDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.path);
        }
    }

    #[derive(Default)]
    struct FaultInjectingOutputFileOps {
        rename_calls: usize,
        fail_rename_call: Option<usize>,
        fail_remove_path: Option<PathBuf>,
    }

    impl OutputFileOps for FaultInjectingOutputFileOps {
        fn metadata(&self, path: &Path) -> std::io::Result<Option<fs::Metadata>> {
            path_metadata(path)
        }

        fn file_contents_equal(&self, path: &Path, expected: &[u8]) -> std::io::Result<bool> {
            file_contents_equal(path, expected)
        }

        fn create_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
            fs::create_dir_all(path)
        }

        fn create_dir(&mut self, path: &Path) -> std::io::Result<()> {
            fs::create_dir(path)
        }

        fn rename(&mut self, from: &Path, to: &Path) -> std::io::Result<()> {
            self.rename_calls += 1;
            if self.fail_rename_call == Some(self.rename_calls) {
                return Err(std::io::Error::other(format!(
                    "injected rename failure {}",
                    self.rename_calls
                )));
            }
            fs::rename(from, to)
        }

        fn remove_dir_all(&mut self, path: &Path) -> std::io::Result<()> {
            if self.fail_remove_path.as_deref() == Some(path) {
                return Err(std::io::Error::other("injected remove failure"));
            }
            fs::remove_dir_all(path)
        }

        fn write_new_file(&mut self, path: &Path, bytes: &[u8]) -> std::io::Result<()> {
            let mut file = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(path)?;
            std::io::Write::write_all(&mut file, bytes)
        }
    }

    fn output_transaction_paths(temp: &ManifestTestDir) -> (PathBuf, PathBuf, PathBuf) {
        let output_dir = temp.path.join("output");
        let final_root = output_dir.join("match-aabbccddeeff");
        let backup_root = output_dir.join(".match-aabbccddeeff.backup");
        (output_dir, final_root, backup_root)
    }

    fn write_transaction_file(root: &Path, name: &str, contents: &[u8]) {
        fs::create_dir_all(root).unwrap();
        fs::write(root.join(name), contents).unwrap();
    }

    fn transaction_file(root: &Path, name: &str) -> Vec<u8> {
        fs::read(root.join(name)).unwrap()
    }

    fn assert_no_staging_directories(output_dir: &Path) {
        let has_staging = fs::read_dir(output_dir).unwrap().any(|entry| {
            entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .contains(".tmp.")
        });
        assert!(!has_staging, "the current transaction staging root leaked");
    }

    fn manifest_file(path: &str, round: u32, side: &str, steam_id: u64) -> serde_json::Value {
        serde_json::json!({
            "path": path,
            "round": round,
            "side": side,
            "steam_id": steam_id,
            "player_name": format!("Player {steam_id}")
        })
    }

    fn manifest_json(
        rounds: Vec<serde_json::Value>,
        files: Vec<serde_json::Value>,
    ) -> serde_json::Value {
        serde_json::json!({
            "demo_path": "C:\\private\\match.dem",
            "demo_id": "match-aabbccddeeff",
            "demo_sha256": "aa".repeat(32),
            "map": "de_mirage",
            "tick_rate": 64.0,
            "abi": DEMOTRACER_ABI,
            "format_version": DTR_FORMAT_VERSION,
            "rounds": rounds,
            "files": files
        })
    }

    fn manifest_round(round: u32, files: usize) -> serde_json::Value {
        serde_json::json!({
            "round": round,
            "duration_seconds": 42.5,
            "pistol_round": round == 0,
            "cut_reason": null,
            "t_economy": {
                "side": "t",
                "players": 5,
                "round_start_equipment_value": 12000,
                "equipment_value_total": 24000,
                "money_saved_total": 1000,
                "cash_spent_this_round": 8000,
                "class": "full"
            },
            "ct_economy": {
                "side": "ct",
                "players": 5,
                "round_start_equipment_value": 13000,
                "equipment_value_total": 25000,
                "money_saved_total": 1200,
                "cash_spent_this_round": 9000,
                "class": "full"
            },
            "files": files
        })
    }

    fn issue_codes(result: &ManifestArchiveDto) -> BTreeSet<&str> {
        result
            .issues
            .iter()
            .map(|issue| issue.code.as_str())
            .collect()
    }

    #[test]
    fn analysis_dto_selects_recommended_rounds_only() {
        let dto = analysis_dto(
            "analysis-1",
            Path::new("C:/demos/match.dem"),
            "match-aabbccddeeff",
            &analysis(),
        );
        assert!(dto.rounds[0].selected_by_default);
        assert!(!dto.rounds[1].selected_by_default);
        assert_eq!(dto.rounds[1].status, "suspicious");
    }

    #[test]
    fn preflight_output_reports_backend_root_and_existing_state_without_creating_it() {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let output_dir = std::env::temp_dir().join(format!(
            "cs2-demotracer-preflight-{}-{unique}",
            std::process::id()
        ));
        let cached = CachedDemo {
            analysis_id: "analysis-1".to_string(),
            parsed: Arc::new(ParsedDemo {
                stem: "match".to_string(),
                demo_sha256: "aabbccddeeff".repeat(6),
                ..ParsedDemo::default()
            }),
            analysis: analysis(),
        };

        let first = preflight_output_for(&cached, &output_dir.display().to_string()).unwrap();
        let expected_root = output_dir.join("match-aabbccddeeff");
        assert_eq!(PathBuf::from(&first.root), expected_root);
        assert!(!first.exists);
        assert!(!output_dir.exists());

        fs::create_dir_all(&expected_root).unwrap();
        let second = preflight_output_for(&cached, &output_dir.display().to_string()).unwrap();
        assert!(second.exists);

        fs::remove_dir_all(&expected_root).unwrap();
        let backup_root = output_dir.join(".match-aabbccddeeff.backup");
        fs::create_dir_all(&backup_root).unwrap();
        let recovered = preflight_output_for(&cached, &output_dir.display().to_string()).unwrap();
        assert!(recovered.exists);
        fs::remove_dir_all(output_dir).unwrap();
    }

    #[test]
    fn build_and_validation_failures_preserve_existing_output() {
        for code in ["conversion_failed", "validation_failed"] {
            let temp = ManifestTestDir::new(code);
            let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
            write_transaction_file(&final_root, "sentinel.txt", b"old output");
            let mut file_ops = FaultInjectingOutputFileOps::default();

            let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
                &output_dir,
                &final_root,
                OverwriteModeDto::Replace,
                &mut file_ops,
                |staging_root| {
                    write_transaction_file(staging_root, "partial.dtr", b"partial");
                    Err(CommandErrorDto::new(code, "injected failure"))
                },
            );

            assert_eq!(result.unwrap_err().code, code);
            assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
            assert!(!backup_root.exists());
            assert_no_staging_directories(&output_dir);
        }
    }

    #[test]
    fn completion_marker_failure_preserves_existing_output() {
        let temp = ManifestTestDir::new("marker-failure");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, OUTPUT_COMPLETION_MARKER, b"collision");
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "completion_marker_failed");
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn first_rename_failure_leaves_existing_output_in_place() {
        let temp = ManifestTestDir::new("rename-one");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_rename_call: Some(1),
            ..FaultInjectingOutputFileOps::default()
        };

        let result = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, "candidate.txt", b"new output");
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "output_promote_failed");
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn second_rename_failure_restores_existing_output() {
        let temp = ManifestTestDir::new("rename-two");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_rename_call: Some(2),
            ..FaultInjectingOutputFileOps::default()
        };

        let result = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, "candidate.txt", b"new output");
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "output_promote_failed");
        assert_eq!(file_ops.rename_calls, 3);
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn backup_cleanup_failure_keeps_valid_new_output_and_reports_warning() {
        let temp = ManifestTestDir::new("backup-cleanup");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_remove_path: Some(backup_root.clone()),
            ..FaultInjectingOutputFileOps::default()
        };

        let result = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |staging_root| {
                write_transaction_file(staging_root, "candidate.txt", b"new output");
                Ok(())
            },
        )
        .unwrap();

        assert!(result.backup_cleanup_warning.is_some());
        assert_eq!(
            transaction_file(&final_root, "candidate.txt"),
            b"new output"
        );
        assert!(final_root.join(OUTPUT_COMPLETION_MARKER).is_file());
        assert_eq!(
            transaction_file(&backup_root, "sentinel.txt"),
            b"old output"
        );
        assert_no_staging_directories(&output_dir);
    }

    #[test]
    fn startup_recovery_restores_backup_before_deny_check() {
        let temp = ManifestTestDir::new("startup-recovery");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();
        let build_called = std::cell::Cell::new(false);

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Deny,
            &mut file_ops,
            |_| {
                build_called.set(true);
                Ok(())
            },
        );

        assert_eq!(result.unwrap_err().code, "output_exists");
        assert!(!build_called.get());
        assert_eq!(transaction_file(&final_root, "sentinel.txt"), b"old output");
        assert!(!backup_root.exists());
    }

    #[test]
    fn ambiguous_output_and_backup_without_marker_fail_closed() {
        let temp = ManifestTestDir::new("ambiguous-recovery");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "sentinel.txt", b"new-unknown");
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |_| unreachable!("ambiguous state must fail before staging"),
        );

        assert_eq!(result.unwrap_err().code, "ambiguous_output_state");
        assert_eq!(
            transaction_file(&final_root, "sentinel.txt"),
            b"new-unknown"
        );
        assert_eq!(
            transaction_file(&backup_root, "sentinel.txt"),
            b"old output"
        );
    }

    #[test]
    fn incomplete_or_wrong_completion_markers_fail_closed() {
        let cases = vec![
            ("empty", Vec::new()),
            (
                "truncated",
                OUTPUT_COMPLETION_MARKER_CONTENT[..OUTPUT_COMPLETION_MARKER_CONTENT.len() - 1]
                    .to_vec(),
            ),
            ("wrong", vec![b'X'; OUTPUT_COMPLETION_MARKER_CONTENT.len()]),
        ];
        for (label, marker) in cases {
            let temp = ManifestTestDir::new(label);
            let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
            write_transaction_file(&final_root, "candidate.txt", b"new-unknown");
            fs::write(final_root.join(OUTPUT_COMPLETION_MARKER), marker).unwrap();
            write_transaction_file(&backup_root, "sentinel.txt", b"old output");
            let mut file_ops = FaultInjectingOutputFileOps::default();

            let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
                &output_dir,
                &final_root,
                OverwriteModeDto::Replace,
                &mut file_ops,
                |_| unreachable!("invalid marker must fail before staging"),
            );

            assert_eq!(result.unwrap_err().code, "ambiguous_output_state");
            assert_eq!(
                transaction_file(&final_root, "candidate.txt"),
                b"new-unknown"
            );
            assert_eq!(
                transaction_file(&backup_root, "sentinel.txt"),
                b"old output"
            );
        }
    }

    #[test]
    fn completed_output_allows_stale_backup_cleanup_before_deny() {
        let temp = ManifestTestDir::new("completed-recovery");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "candidate.txt", b"new output");
        fs::write(
            final_root.join(OUTPUT_COMPLETION_MARKER),
            OUTPUT_COMPLETION_MARKER_CONTENT,
        )
        .unwrap();
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps::default();

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Deny,
            &mut file_ops,
            |_| unreachable!("deny must stop before staging"),
        );

        assert_eq!(result.unwrap_err().code, "output_exists");
        assert_eq!(
            transaction_file(&final_root, "candidate.txt"),
            b"new output"
        );
        assert!(!backup_root.exists());
    }

    #[test]
    fn stale_backup_cleanup_failure_aborts_without_touching_completed_output() {
        let temp = ManifestTestDir::new("stale-cleanup-failure");
        let (output_dir, final_root, backup_root) = output_transaction_paths(&temp);
        write_transaction_file(&final_root, "candidate.txt", b"new output");
        fs::write(
            final_root.join(OUTPUT_COMPLETION_MARKER),
            OUTPUT_COMPLETION_MARKER_CONTENT,
        )
        .unwrap();
        write_transaction_file(&backup_root, "sentinel.txt", b"old output");
        let mut file_ops = FaultInjectingOutputFileOps {
            fail_remove_path: Some(backup_root.clone()),
            ..FaultInjectingOutputFileOps::default()
        };

        let result: CommandResult<OutputTransaction<()>> = run_output_transaction(
            &output_dir,
            &final_root,
            OverwriteModeDto::Replace,
            &mut file_ops,
            |_| unreachable!("recovery failure must stop before staging"),
        );

        assert_eq!(result.unwrap_err().code, "output_backup_cleanup_failed");
        assert_eq!(
            transaction_file(&final_root, "candidate.txt"),
            b"new output"
        );
        assert_eq!(
            transaction_file(&backup_root, "sentinel.txt"),
            b"old output"
        );
    }

    #[test]
    fn staged_progress_never_exposes_temporary_paths_or_finishes_early() {
        let final_root = Path::new("output/match-aabbccddeeff");
        let progress = public_conversion_progress(
            ConversionProgress::ArtifactsWritingStarted {
                root: "output/.match-aabbccddeeff.tmp.1.nonce".to_string(),
                artifacts: 4,
            },
            final_root,
        )
        .unwrap();
        assert_eq!(
            progress,
            ConversionProgressDto::ArtifactsWritingStarted {
                root: final_root.display().to_string(),
                artifacts: 4,
            }
        );
        assert!(public_conversion_progress(
            ConversionProgress::Finished {
                root: "output/.match-aabbccddeeff.tmp.1.nonce".to_string(),
                manifest_path: "output/.match-aabbccddeeff.tmp.1.nonce/manifest.json".to_string(),
                files_written: 3,
            },
            final_root,
        )
        .is_none());
    }

    #[test]
    fn progress_serializes_steam_id_as_a_string() {
        let progress = ConversionProgressDto::from(ConversionProgress::PlayerWritten {
            round: 1,
            steam_id: 76_561_198_012_345_678,
            player_name: "Player".to_string(),
            side: "t".to_string(),
            path: "round01/t/player.dtr".to_string(),
            ticks: 100,
            subticks: 2,
        });
        let json = serde_json::to_value(progress).unwrap();
        assert_eq!(json["steamId"], "76561198012345678");
    }

    #[test]
    fn suspicious_rounds_require_explicit_opt_in() {
        let selected = BTreeSet::from([2]);
        let error = validate_round_selection(&analysis(), &selected, false).unwrap_err();
        assert_eq!(error.code, "suspicious_rounds_not_allowed");
        validate_round_selection(&analysis(), &selected, true).unwrap();
    }

    #[test]
    fn cosmetics_require_exact_confirmation_phrase() {
        let mut request = request();
        request.export_cosmetics = true;
        request.cosmetic_consent = Some(CosmeticConsentDto {
            phrase: "close enough".to_string(),
        });
        assert_eq!(
            validate_cosmetic_request(&request).unwrap_err().code,
            "cosmetic_consent_required"
        );

        request.cosmetic_consent.as_mut().unwrap().phrase =
            COSMETIC_CONFIRMATION_PHRASE.to_string();
        let flags = validate_cosmetic_request(&request).unwrap();
        assert!(flags.cosmetics && flags.stickers && flags.charms);
    }

    #[test]
    fn disabled_cosmetics_gate_detail_flags() {
        let flags = validate_cosmetic_request(&request()).unwrap();
        assert!(!flags.cosmetics && !flags.stickers && !flags.charms);
    }

    #[test]
    fn commands_use_exported_round_and_expected_prefix_order() {
        let commands = build_commands(
            Path::new("output/demo/manifest.json"),
            Some(7),
            2,
            Some("full"),
        );
        assert_eq!(
            commands.go_sequence,
            "dtr_go seq \"output/demo/manifest.json\" 7"
        );
        assert_eq!(
            commands.round,
            "dtr_voice_auto on; dtr_go round \"output/demo/manifest.json\" 7"
        );
        assert_eq!(
            commands.cosmetic_sequence.as_deref(),
            Some(
                "dtr_voice_auto on; dtr_cosmetics full; dtr_go seq \"output/demo/manifest.json\" 7"
            )
        );
    }

    #[test]
    fn player_summary_preserves_large_steam_ids_as_strings() {
        let steam_id = 76_561_198_012_345_678;
        let files = vec![
            converted_file(1, "t", steam_id, "Player", None),
            converted_file(2, "ct", steam_id, "Player", None),
        ];
        let players = summarize_exported_players(&files);
        assert_eq!(players[0].steam_id, "76561198012345678");
        assert_eq!(players[0].rounds, 2);
        assert_eq!(players[0].team, 1);
    }

    #[test]
    fn conversion_summary_reports_full_cosmetic_preset() {
        let cosmetics = ReplayCosmetics {
            weapons: vec![ReplayWeaponCosmetic {
                weapon_def_index: 7,
                paint_kit: 1,
                seed: 2,
                wear: 0.1,
                quality: None,
                stattrak_counter: None,
                original_owner_steam_id: None,
                item_account_id: None,
                item_id: None,
                custom_name: None,
                inspect: None,
                stickers: vec![ReplayWeaponSticker {
                    slot: 0,
                    sticker_id: 10,
                    wear: 0.0,
                    offset_x: 0.0,
                    offset_y: 0.0,
                    scale: None,
                    rotation: None,
                }],
                charms: vec![ReplayWeaponCharm {
                    slot: 0,
                    charm_id: 20,
                    offset_x: 0.0,
                    offset_y: 0.0,
                    offset_z: 0.0,
                    seed: None,
                    highlight: None,
                    sticker_id: None,
                }],
            }],
            knife: Some(ReplayItemCosmetic {
                item_def_index: Some(500),
                paint_kit: 1,
                seed: 2,
                wear: 0.1,
                custom_name: None,
                inspect: None,
            }),
            glove: None,
            agent: None,
        };
        let manifest = ConversionManifest {
            demo_path: "match.dem".to_string(),
            demo_id: "match-aabbccddeeff".to_string(),
            demo_sha256: "aa".repeat(32),
            map: "de_mirage".to_string(),
            tick_rate: 64.0,
            abi: 17,
            format_version: 7,
            avatar_overrides: Vec::new(),
            rounds: vec![converted_round(7, 1)],
            files: vec![converted_file(
                7,
                "t",
                76_561_198_012_345_678,
                "Player",
                Some(cosmetics),
            )],
        };
        let report = ConversionReport {
            root: PathBuf::from("output/demo"),
            manifest_path: PathBuf::from("output/demo/manifest.json"),
            files_written: 1,
            manifest,
        };
        let summary = summarize_conversion(report, 1, true, 1);
        assert_eq!(summary.cosmetics.preset.as_deref(), Some("full"));
        assert_eq!(summary.first_exported_round, Some(7));
        assert_eq!(summary.players[0].steam_id, "76561198012345678");
    }

    #[test]
    fn read_manifest_uses_files_as_round_truth_and_summarizes_safe_artifacts() {
        let temp = ManifestTestDir::new("valid");
        temp.write_dtr("round00/t/a.dtr", 0, "t", 76_561_198_012_345_678);
        temp.write_dtr("round00/ct/b.dtr", 0, "ct", 76_561_198_012_345_679);
        temp.write_file("voice/round00.dtv", b"voice");
        let mut first = manifest_file("round00/t/a.dtr", 0, "t", 76_561_198_012_345_678);
        first["cosmetics"] = serde_json::json!({
            "weapons": [{ "stickers": [{ "slot": 0, "sticker_id": 10 }] }]
        });
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(0, 2), manifest_round(99, 1)],
            vec![
                first,
                manifest_file("round00/ct/b.dtr", 0, "ct", 76_561_198_012_345_679),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(result.playable);
        assert_eq!(result.compatibility, "current");
        assert_eq!(result.demo_path, "match.dem");
        assert_eq!(result.total_files, 2);
        assert_eq!(result.playable_files, 2);
        assert_eq!(result.players[0].steam_id, "76561198012345678");
        assert_eq!(result.voice.rounds, vec![0]);
        assert_eq!(result.cosmetics.preset.as_deref(), Some("full"));
        assert_eq!(result.rounds.len(), 1);
        assert_eq!(result.rounds[0].round, 0);
        assert_eq!(result.rounds[0].t_files, 1);
        assert_eq!(result.rounds[0].ct_files, 1);
        assert_eq!(result.rounds[0].sequence_length, 1);
        assert!(result.rounds[0].available);
        assert!(result.rounds[0]
            .commands
            .go_round
            .ends_with(&format!("\"{}\" 0", manifest_path.display())));
        assert!(result.output_bytes.parse::<u64>().unwrap() > 0);
        assert!(issue_codes(&result).contains("manifest_round_without_files"));
    }

    #[test]
    fn read_manifest_marks_round_unavailable_when_any_declared_file_is_missing() {
        let temp = ManifestTestDir::new("missing");
        temp.write_dtr("round07/t/a.dtr", 7, "t", 101);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(7, 3), manifest_round(8, 1)],
            vec![
                manifest_file("round07/t/a.dtr", 7, "t", 101),
                manifest_file("round07/ct/missing.dtr", 7, "ct", 102),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(!result.playable);
        assert_eq!(result.total_files, 2);
        assert_eq!(result.playable_files, 1);
        assert_eq!(result.rounds.len(), 1);
        assert!(!result.rounds[0].available);
        assert_eq!(result.rounds[0].sequence_length, 0);
        assert!(result.rounds[0]
            .commands
            .go_sequence
            .ends_with(&format!("\"{}\" 7", manifest_path.display())));
        let codes = issue_codes(&result);
        assert!(codes.contains("manifest_file_missing"));
        assert!(codes.contains("manifest_round_file_count_mismatch"));
        assert!(codes.contains("manifest_round_without_files"));
    }

    #[test]
    fn read_manifest_disables_sequence_across_an_unavailable_round() {
        let temp = ManifestTestDir::new("sequence-gap");
        temp.write_dtr("round01/t/a.dtr", 1, "t", 101);
        temp.write_dtr("round03/ct/c.dtr", 3, "ct", 103);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![
                manifest_round(1, 1),
                manifest_round(2, 1),
                manifest_round(3, 1),
            ],
            vec![
                manifest_file("round01/t/a.dtr", 1, "t", 101),
                manifest_file("round02/t/missing.dtr", 2, "t", 102),
                manifest_file("round03/ct/c.dtr", 3, "ct", 103),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(result.playable);
        assert_eq!(result.rounds.len(), 3);
        assert!(result.rounds[0].available);
        assert_eq!(result.rounds[0].sequence_length, 0);
        assert!(!result.rounds[1].available);
        assert_eq!(result.rounds[1].sequence_length, 0);
        assert!(result.rounds[2].available);
        assert_eq!(result.rounds[2].sequence_length, 1);
    }

    #[test]
    fn read_manifest_rejects_corrupt_dtr_payloads() {
        let temp = ManifestTestDir::new("corrupt-dtr");
        temp.write_file("round04/t/a.dtr", b"not a replay");
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(4, 1)],
            vec![manifest_file("round04/t/a.dtr", 4, "t", 401)],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(!result.playable);
        assert_eq!(result.playable_files, 0);
        assert_eq!(result.output_bytes, "0");
        assert!(issue_codes(&result).contains("manifest_file_invalid_dtr"));
    }

    #[test]
    fn read_manifest_reports_duplicate_escape_and_invalid_side_paths() {
        let temp = ManifestTestDir::new("unsafe");
        temp.write_dtr("round01/t/a.dtr", 1, "t", 201);
        temp.write_dtr("round01/t/b.dtr", 1, "t", 203);
        temp.write_dtr("round02/t/c.dtr", 2, "t", 205);
        let manifest_path = temp.write_manifest(manifest_json(
            vec![manifest_round(1, 4), manifest_round(2, 1)],
            vec![
                manifest_file("round01/t/a.dtr", 1, "t", 201),
                manifest_file("round01/t/a.dtr", 1, "t", 202),
                manifest_file("round01/t/b.dtr", 1, "spectator", 203),
                manifest_file("../escape.dtr", 1, "ct", 204),
                manifest_file("round02/t/c.dtr", 2, "t", 205),
            ],
        ));

        let result = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert!(!result.playable);
        assert_eq!(result.playable_files, 2);
        assert!(result.rounds.iter().all(|round| !round.available));
        let codes = issue_codes(&result);
        assert!(codes.contains("manifest_file_path_duplicate"));
        assert!(codes.contains("manifest_file_target_duplicate"));
        assert!(codes.contains("manifest_file_side_invalid"));
        assert!(codes.contains("manifest_file_path_escape"));
    }

    #[test]
    fn read_manifest_honors_format_alias_and_rejects_newer_abi() {
        let temp = ManifestTestDir::new("versions");
        temp.write_dtr("round03/t/a.dtr", 3, "t", 301);
        let mut value = manifest_json(
            vec![manifest_round(3, 1)],
            vec![manifest_file("round03/t/a.dtr", 3, "t", 301)],
        );
        value["abi"] = serde_json::json!(15);
        value["format_version"] = serde_json::json!(999);
        value["dtr_format_version"] = serde_json::json!(6);
        let manifest_path = temp.write_manifest(value.clone());
        let supported = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert_eq!(supported.compatibility, "supported");
        assert_eq!(supported.format_version, 6);
        assert!(supported.playable);

        value["dtr_format_version"] = serde_json::json!(0);
        temp.write_manifest(value.clone());
        let fallback = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert_eq!(fallback.format_version, 999);
        assert_eq!(fallback.compatibility, "unsupported");
        assert!(issue_codes(&fallback).contains("manifest_format_unsupported"));

        value["dtr_format_version"] = serde_json::json!(6);
        value["abi"] = serde_json::json!(DEMOTRACER_ABI + 1);
        temp.write_manifest(value);
        let unsupported = read_manifest_for(&manifest_path.display().to_string()).unwrap();
        assert_eq!(unsupported.compatibility, "unsupported");
        assert!(!unsupported.playable);
        assert!(issue_codes(&unsupported).contains("manifest_abi_unsupported"));
    }

    #[test]
    fn read_manifest_rejects_pool_manifests() {
        let temp = ManifestTestDir::new("pool");
        let path = temp.write_manifest(serde_json::json!({
            "format_version": 1,
            "abi": DEMOTRACER_ABI,
            "map": "de_mirage",
            "candidates": []
        }));
        let error = read_manifest_for(&path.display().to_string()).unwrap_err();
        assert_eq!(error.code, "unsupported_manifest_kind");
    }

    #[test]
    fn read_manifest_rejects_unrelated_json() {
        let temp = ManifestTestDir::new("unrelated-json");
        let path = temp.write_manifest(serde_json::json!({ "name": "not a manifest" }));
        let error = read_manifest_for(&path.display().to_string()).unwrap_err();
        assert_eq!(error.code, "manifest_schema_invalid");
    }
}
