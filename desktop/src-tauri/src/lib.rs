use cs2_demotracer::demo_id::output_demo_id;
use cs2_demotracer::demo_reader::{read_demo_with_options, ReadDemoOptions};
use cs2_demotracer::export::{
    export_demo_with_progress, ConversionArtifactKind, ConversionProgress, ConversionReport,
    ConvertOptions, DEFAULT_FREEZE_PREROLL_SECONDS,
};
use cs2_demotracer::model::{
    ConvertedFile, DemoAnalysis, ParsedDemo, RoundStatus, Side, SubtickMode,
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
    pub acknowledge_gslt_risk: bool,
    pub accept_export_disclaimer: bool,
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
    let cached = state
        .cache()?
        .as_ref()
        .filter(|cached| cached.analysis_id == request.analysis_id)
        .cloned()
        .ok_or_else(|| {
            CommandErrorDto::new(
                "stale_analysis",
                "The analyzed demo is no longer cached. Analyze it again before converting.",
            )
        })?;

    let prepared = prepare_conversion(&request, &cached)?;
    if prepared.root.exists() && request.overwrite == OverwriteModeDto::Deny {
        return Err(output_exists_error(&prepared.root));
    }

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

    let output_dir = PathBuf::from(request.output_dir.trim());
    if output_dir.as_os_str().is_empty() {
        return Err(CommandErrorDto::new(
            "invalid_output_dir",
            "Choose an output folder before converting.",
        ));
    }
    let demo_id = output_demo_id(&cached.parsed.stem, &cached.parsed.demo_sha256, None)
        .map_err(|error| CommandErrorDto::from_core("invalid_demo_id", error))?;
    let root = output_dir.join(demo_id);
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
        if request.cosmetic_consent.as_ref().is_some_and(|consent| {
            consent.acknowledge_gslt_risk
                || consent.accept_export_disclaimer
                || !consent.phrase.trim().is_empty()
        }) {
            return Err(CommandErrorDto::new(
                "unexpected_cosmetic_consent",
                "Cosmetic risk acknowledgements require cosmetic export to be enabled.",
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
            "Cosmetic export requires explicit GSLT risk and export disclaimer acknowledgement.",
        )
    })?;
    if !consent.acknowledge_gslt_risk
        || !consent.accept_export_disclaimer
        || consent.phrase.trim() != COSMETIC_CONFIRMATION_PHRASE
    {
        return Err(CommandErrorDto::new(
            "cosmetic_consent_required",
            format!(
                "Accept both cosmetic warnings and type {COSMETIC_CONFIRMATION_PHRASE:?} exactly."
            ),
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
    if prepared.root.exists() {
        match request.overwrite {
            OverwriteModeDto::Deny => return Err(output_exists_error(&prepared.root)),
            OverwriteModeDto::Replace => {
                clear_output_root(&prepared.output_dir, &prepared.root)?;
            }
        }
    }

    emit_phase(&events, TaskPhase::Exporting);
    let progress_events = events.clone();
    let report = export_demo_with_progress(&cached.parsed, &prepared.options, move |progress| {
        emit(
            &progress_events,
            TaskEvent::Progress {
                progress: progress.into(),
            },
        );
    })
    .map_err(|error| CommandErrorDto::from_core("conversion_failed", error))?;

    let mut voice_sidecars = 0;
    if request.export_voice {
        emit_phase(&events, TaskPhase::Voice);
        match export_round_voice_sidecars(&cached.parsed, &report) {
            Ok(reports) => {
                voice_sidecars = reports.len();
                for voice in reports {
                    emit_log(
                        &events,
                        LogLevel::Info,
                        format!(
                            "Voice sidecar {}: {} frames, {} speakers, {:.2}s",
                            voice.path.display(),
                            voice.frame_count,
                            voice.speaker_count,
                            voice.duration_seconds
                        ),
                    );
                }
            }
            Err(error) => emit_log(
                &events,
                LogLevel::Warning,
                format!("Voice sidecar export skipped: {error}"),
            ),
        }
    }

    emit_phase(&events, TaskPhase::Validating);
    let validated_files = validate_dtr_path(&report.root)
        .map_err(|error| CommandErrorDto::from_core("validation_failed", error))?;
    let summary = summarize_conversion(
        report,
        validated_files,
        request.export_voice,
        voice_sidecars,
    );
    emit_phase(&events, TaskPhase::Complete);
    Ok(summary)
}

fn output_exists_error(path: &Path) -> CommandErrorDto {
    CommandErrorDto::at_path(
        "output_exists",
        "Output for this demo already exists. Confirm replacement to continue.",
        path,
    )
}

fn clear_output_root(output_dir: &Path, root: &Path) -> CommandResult<()> {
    if root.parent() != Some(output_dir) {
        return Err(CommandErrorDto::at_path(
            "unsafe_output_root",
            "Refusing to clear an output path outside the selected folder.",
            root,
        ));
    }
    let metadata = fs::symlink_metadata(root).map_err(|error| {
        CommandErrorDto::at_path("output_clear_failed", error.to_string(), root)
    })?;
    if !metadata.file_type().is_dir() {
        return Err(CommandErrorDto::at_path(
            "output_not_directory",
            "Existing output is not a normal directory and will not be replaced.",
            root,
        ));
    }
    fs::remove_dir_all(root)
        .map_err(|error| CommandErrorDto::at_path("output_clear_failed", error.to_string(), root))
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
    let base_round = format!("dtr_go round \"{manifest}\" {round}");
    let base_sequence = format!("dtr_go seq \"{manifest}\" {round}");
    CommandSummaryDto {
        round: command_with_prefixes(&base_round, voice_sidecars, None),
        sequence: command_with_prefixes(&base_sequence, voice_sidecars, None),
        cosmetic_round: cosmetic_preset
            .map(|preset| command_with_prefixes(&base_round, voice_sidecars, Some(preset))),
        cosmetic_sequence: cosmetic_preset
            .map(|preset| command_with_prefixes(&base_sequence, voice_sidecars, Some(preset))),
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
            choose_output_dir,
            analyze_demo,
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
    fn cosmetics_require_both_acknowledgements_and_exact_phrase() {
        let mut request = request();
        request.export_cosmetics = true;
        request.cosmetic_consent = Some(CosmeticConsentDto {
            acknowledge_gslt_risk: true,
            accept_export_disclaimer: true,
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
}
