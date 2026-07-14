use crate::demo_id::output_demo_id;
use crate::demo_reader::{read_demo_with_options, ReadDemoOptions};
use crate::export::{
    export_demo_with_progress, ConversionProgress, ConversionReport, ConvertOptions,
    DEFAULT_FREEZE_PREROLL_SECONDS,
};
use crate::model::{DemoAnalysis, ParsedDemo, RoundStatus, Side, SubtickMode};
use crate::quality::{analyze_demo, AnalysisOptions};
use crate::validate::validate_dtr_path;
use crate::voice_export::export_round_voice_sidecars;
use eframe::egui::{
    self, Color32, FontData, FontDefinitions, FontFamily, FontId, RichText, ScrollArea, TextStyle,
};
use egui_extras::{Column, TableBuilder};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

const COSMETIC_CONFIRMATION_PHRASE: &str = "I ACCEPT COSMETIC EXPORT RISK";
const GOOD: Color32 = Color32::from_rgb(80, 210, 146);
const WARN: Color32 = Color32::from_rgb(242, 170, 76);
const DANGER: Color32 = Color32::from_rgb(255, 92, 92);
const INFO: Color32 = Color32::from_rgb(92, 178, 255);
const PANEL: Color32 = Color32::from_rgb(24, 30, 38);
const PANEL_DEEP: Color32 = Color32::from_rgb(13, 18, 24);

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
enum LanguageChoice {
    System,
    ZhCn,
    En,
}

impl Default for LanguageChoice {
    fn default() -> Self {
        Self::System
    }
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
enum ThemeChoice {
    System,
    Dark,
    Light,
}

impl Default for ThemeChoice {
    fn default() -> Self {
        Self::System
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum UiLanguage {
    ZhCn,
    En,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ResolvedTheme {
    Dark,
    Light,
}

pub fn run_gui() -> eframe::Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1180.0, 760.0])
            .with_min_inner_size([960.0, 620.0])
            .with_decorations(false),
        ..Default::default()
    };
    eframe::run_native(
        "CS2 DemoTracer",
        options,
        Box::new(|cc| Ok(Box::new(DemoTracerGui::new(cc)))),
    )
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(default)]
struct GuiSettings {
    demo_path: String,
    output_dir: String,
    language: LanguageChoice,
    theme: ThemeChoice,
    side: Side,
    include_suspicious: bool,
    full_round: bool,
    freeze_preroll_seconds: f32,
    #[serde(default = "default_true")]
    export_voice: bool,
    export_cosmetics: bool,
    #[serde(default = "default_true")]
    export_stickers: bool,
    #[serde(default = "default_true")]
    export_charms: bool,
    cosmetics_open: bool,
    advanced_open: bool,
    activity_open: bool,
}

impl Default for GuiSettings {
    fn default() -> Self {
        Self {
            demo_path: String::new(),
            output_dir: "output".to_string(),
            language: LanguageChoice::System,
            theme: ThemeChoice::System,
            side: Side::Both,
            include_suspicious: false,
            full_round: false,
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_voice: true,
            export_cosmetics: false,
            export_stickers: true,
            export_charms: true,
            cosmetics_open: false,
            advanced_open: false,
            activity_open: false,
        }
    }
}

fn default_true() -> bool {
    true
}

struct DemoTracerGui {
    settings: GuiSettings,
    parsed: Option<Arc<ParsedDemo>>,
    analysis: Option<DemoAnalysis>,
    round_selection: BTreeMap<u32, bool>,
    result: Option<ConversionResultView>,
    pending_overwrite: Option<PendingConversion>,
    show_cosmetic_disclaimer: bool,
    cosmetic_confirmation: String,
    cosmetic_acknowledged: bool,
    receiver: Option<Receiver<WorkerMessage>>,
    running: Option<RunningTask>,
    progress: GuiProgress,
    logs: Vec<String>,
    error: Option<String>,
}

impl DemoTracerGui {
    fn new(cc: &eframe::CreationContext<'_>) -> Self {
        let settings = load_settings();
        install_system_cjk_font(&cc.egui_ctx);
        apply_visuals(&cc.egui_ctx, resolve_theme(settings.theme, &cc.egui_ctx));
        let mut progress = GuiProgress::default();
        progress.begin("Choose a demo to begin", Some(0.0));
        Self {
            settings,
            parsed: None,
            analysis: None,
            round_selection: BTreeMap::new(),
            result: None,
            pending_overwrite: None,
            show_cosmetic_disclaimer: false,
            cosmetic_confirmation: String::new(),
            cosmetic_acknowledged: false,
            receiver: None,
            running: None,
            progress,
            logs: Vec::new(),
            error: None,
        }
    }

    fn is_running(&self) -> bool {
        self.running.is_some()
    }

    fn language(&self) -> UiLanguage {
        resolve_language(self.settings.language)
    }

    fn theme(&self, ctx: &egui::Context) -> ResolvedTheme {
        resolve_theme(self.settings.theme, ctx)
    }

    fn analyze(&mut self) {
        if self.is_running() {
            return;
        }
        let demo_path = PathBuf::from(self.settings.demo_path.trim());
        if !is_demo_file(&demo_path) {
            self.error = Some("Choose a .dem file before analyzing.".to_string());
            return;
        }
        self.save_settings();
        self.parsed = None;
        self.analysis = None;
        self.round_selection.clear();
        self.result = None;
        self.error = None;
        self.logs.clear();
        self.progress.begin("Parsing demo", None);

        let (tx, rx) = mpsc::channel();
        self.receiver = Some(rx);
        self.running = Some(RunningTask::Analyze);
        thread::spawn(move || analyze_worker(demo_path, tx));
    }

    fn request_convert(&mut self) {
        if self.is_running() {
            return;
        }
        let Some(parsed) = self.parsed.clone() else {
            self.error = Some("Analyze a demo before converting.".to_string());
            return;
        };
        let selected_rounds = self.selected_rounds();
        if selected_rounds.is_empty() {
            self.error = Some("Select at least one round to export.".to_string());
            return;
        }
        let output_dir = PathBuf::from(self.settings.output_dir.trim());
        if output_dir.as_os_str().is_empty() {
            self.error = Some("Choose an output directory.".to_string());
            return;
        }
        if !cosmetic_export_ready(&self.settings, self.cosmetic_acknowledged) {
            self.show_cosmetic_disclaimer = true;
            self.cosmetic_confirmation.clear();
            self.error = Some("Confirm cosmetic export risk before converting.".to_string());
            return;
        }
        self.save_settings();

        let demo_id = match output_demo_id(&parsed.stem, &parsed.demo_sha256, None) {
            Ok(value) => value,
            Err(err) => {
                self.error = Some(err.to_string());
                return;
            }
        };
        let root = output_dir.join(demo_id);
        let options = self.convert_options(output_dir, selected_rounds);
        let pending = PendingConversion {
            parsed,
            options,
            export_voice: self.settings.export_voice,
            overwrite_root: root.clone(),
        };
        if root.exists() {
            self.pending_overwrite = Some(pending);
        } else {
            self.start_convert(pending, false);
        }
    }

    fn start_convert(&mut self, pending: PendingConversion, clear_existing: bool) {
        self.error = None;
        self.result = None;
        self.logs.clear();
        self.progress.begin("Preparing export", Some(0.0));

        let (tx, rx) = mpsc::channel();
        self.receiver = Some(rx);
        self.running = Some(RunningTask::Convert);
        thread::spawn(move || convert_worker(pending, clear_existing, tx));
    }

    fn convert_options(
        &self,
        output_dir: PathBuf,
        selected_rounds: BTreeSet<u32>,
    ) -> ConvertOptions {
        ConvertOptions {
            output_dir,
            output_stem: None,
            side: self.settings.side,
            selected_rounds: Some(selected_rounds),
            include_suspicious: self.settings.include_suspicious,
            cut_before_bomb_plant: !self.settings.full_round,
            subtick_mode: SubtickMode::Auto,
            freeze_preroll_seconds: self.settings.freeze_preroll_seconds,
            export_cosmetics: self.settings.export_cosmetics,
            export_stickers: self.settings.export_cosmetics && self.settings.export_stickers,
            export_charms: self.settings.export_cosmetics && self.settings.export_charms,
            analysis: AnalysisOptions::default(),
        }
    }

    fn selected_rounds(&self) -> BTreeSet<u32> {
        self.round_selection
            .iter()
            .filter_map(|(round, selected)| selected.then_some(*round))
            .collect()
    }

    fn reconcile_round_selection(&mut self) {
        if self.settings.include_suspicious {
            return;
        }
        if let Some(analysis) = &self.analysis {
            for round in &analysis.rounds {
                if round.status == RoundStatus::Suspicious {
                    self.round_selection.insert(round.round, false);
                }
            }
        }
    }

    fn receive_worker_messages(&mut self, ctx: &egui::Context) {
        let mut clear_receiver = false;
        if let Some(receiver) = self.receiver.take() {
            while let Ok(message) = receiver.try_recv() {
                match message {
                    WorkerMessage::Log(message) => self.push_log(message),
                    WorkerMessage::AnalysisComplete { parsed, analysis } => {
                        self.progress
                            .finish(format!("Parsed {} rounds", analysis.rounds.len()));
                        self.round_selection = default_round_selection(&analysis);
                        self.parsed = Some(parsed);
                        self.analysis = Some(analysis);
                        self.result = None;
                        self.error = None;
                        clear_receiver = true;
                    }
                    WorkerMessage::ConversionProgress(event) => {
                        self.progress.apply_conversion_event(&event);
                        self.push_log(format_progress_event(&event));
                    }
                    WorkerMessage::ConversionComplete {
                        report,
                        validated,
                        voice_requested,
                        voice_sidecars,
                    } => {
                        self.progress.finish("Conversion complete");
                        self.result = Some(ConversionResultView::from_report(
                            report,
                            validated,
                            voice_requested,
                            voice_sidecars,
                        ));
                        self.error = None;
                        clear_receiver = true;
                    }
                    WorkerMessage::Failed(message) => {
                        self.progress.fail("Failed");
                        self.error = Some(message);
                        clear_receiver = true;
                    }
                }
            }
            if !clear_receiver {
                self.receiver = Some(receiver);
            }
        }
        if clear_receiver {
            self.running = None;
        }
        if self.is_running() {
            ctx.request_repaint_after(Duration::from_millis(100));
        }
    }

    fn push_log(&mut self, message: String) {
        if message.trim().is_empty() {
            return;
        }
        self.logs.push(message);
        if self.logs.len() > 240 {
            let drop_count = self.logs.len() - 240;
            self.logs.drain(0..drop_count);
        }
    }

    fn save_settings(&mut self) {
        self.settings.freeze_preroll_seconds =
            self.settings.freeze_preroll_seconds.clamp(0.0, 120.0);
        if let Err(err) = save_settings(&self.settings) {
            self.push_log(format!("settings not saved: {err}"));
        }
    }

    fn browse_demo(&mut self) {
        if let Some(path) = rfd::FileDialog::new()
            .add_filter("CS2 demo", &["dem"])
            .pick_file()
        {
            self.set_demo_path(path);
        }
    }

    fn set_demo_path(&mut self, path: PathBuf) {
        let next = path.display().to_string();
        if self.settings.demo_path != next {
            self.parsed = None;
            self.analysis = None;
            self.round_selection.clear();
            self.result = None;
            self.error = None;
        }
        self.settings.demo_path = next;
        self.save_settings();
    }

    fn browse_output(&mut self) {
        if let Some(path) = rfd::FileDialog::new().pick_folder() {
            self.settings.output_dir = path.display().to_string();
            self.save_settings();
        }
    }

    fn handle_drops(&mut self, ctx: &egui::Context) {
        let dropped_files = ctx.input(|input| input.raw.dropped_files.clone());
        for dropped in dropped_files {
            let Some(path) = dropped.path else {
                continue;
            };
            if is_demo_file(&path) {
                self.set_demo_path(path);
            } else if path.is_dir() {
                self.settings.output_dir = path.display().to_string();
                self.save_settings();
            }
        }
    }

    fn open_result_folder(&mut self) {
        let Some(path) = self.result.as_ref().map(|result| result.root.clone()) else {
            return;
        };
        if let Err(err) = open_folder_path(&path) {
            self.push_log(format!("could not open output folder: {err}"));
        }
    }
}

impl eframe::App for DemoTracerGui {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        apply_visuals(ctx, self.theme(ctx));
        self.handle_drops(ctx);
        self.receive_worker_messages(ctx);
        self.reconcile_round_selection();

        egui::TopBottomPanel::top("workspace-top")
            .resizable(false)
            .show(ctx, |ui| {
                self.draw_header(ui);
                self.draw_controls(ui);
                self.draw_progress(ui);
            });

        egui::CentralPanel::default().show(ctx, |ui| {
            let available = ui.available_size();
            let advanced_height = if self.settings.advanced_open {
                let mut height = 120.0;
                if self.settings.export_cosmetics {
                    height += 54.0;
                    if self.settings.cosmetics_open {
                        height += 34.0;
                    }
                }
                height
            } else {
                32.0
            };
            let footer_height = advanced_height
                + if self.settings.activity_open {
                    154.0
                } else {
                    32.0
                }
                + 12.0;
            let main_height = (available.y - footer_height).max(240.0);
            let left_width = if available.x > 840.0 {
                (available.x * 0.66).min(available.x - 300.0)
            } else {
                available.x * 0.60
            };

            ui.allocate_ui_with_layout(
                egui::vec2(available.x, main_height),
                egui::Layout::left_to_right(egui::Align::Min),
                |ui| {
                    ui.allocate_ui_with_layout(
                        egui::vec2(left_width, main_height),
                        egui::Layout::top_down(egui::Align::Min),
                        |ui| self.draw_rounds(ui),
                    );
                    ui.separator();
                    ui.allocate_ui_with_layout(
                        egui::vec2(ui.available_width(), main_height),
                        egui::Layout::top_down(egui::Align::Min),
                        |ui| self.draw_result(ui),
                    );
                },
            );
            ui.add_space(6.0);
            self.draw_advanced(ui);
            ui.add_space(4.0);
            self.draw_logs(ui);
        });

        self.draw_resize_grip(ctx);
        self.draw_overwrite_dialog(ctx);
        self.draw_cosmetic_disclaimer_dialog(ctx);
    }
}

impl DemoTracerGui {
    fn draw_header(&mut self, ui: &mut egui::Ui) {
        let lang = self.language();
        let mut changed = false;
        egui::Frame::new()
            .fill(top_bar_color(ui))
            .stroke(egui::Stroke::new(1.0, border_color(ui)))
            .inner_margin(egui::Margin::symmetric(10, 5))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new("DT")
                            .strong()
                            .color(Color32::BLACK)
                            .background_color(GOOD),
                    );
                    ui.label(
                        RichText::new("CS2 DemoTracer")
                            .strong()
                            .color(ui.visuals().strong_text_color()),
                    );
                    ui.label(RichText::new("v1").color(ui.visuals().weak_text_color()));

                    let drag_width = (ui.available_width() - 390.0).max(24.0);
                    let drag_response =
                        ui.allocate_response(egui::vec2(drag_width, 28.0), egui::Sense::drag());
                    if drag_response.drag_started() {
                        ui.ctx().send_viewport_cmd(egui::ViewportCommand::StartDrag);
                    }
                    if drag_response.double_clicked() {
                        toggle_maximized(ui.ctx());
                    }

                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if titlebar_button(ui, "x", DANGER, tr(lang, "close_window")).clicked() {
                            ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
                        }
                        let maximized = ui
                            .ctx()
                            .input(|input| input.viewport().maximized.unwrap_or(false));
                        let maximize_label = if maximized { "□" } else { "▢" };
                        if titlebar_button(ui, maximize_label, INFO, tr(lang, "maximize_window"))
                            .clicked()
                        {
                            toggle_maximized(ui.ctx());
                        }
                        if titlebar_button(ui, "-", INFO, tr(lang, "minimize_window")).clicked() {
                            ui.ctx()
                                .send_viewport_cmd(egui::ViewportCommand::Minimized(true));
                        }
                        ui.separator();
                        egui::ComboBox::from_id_salt("theme-choice")
                            .selected_text(theme_choice_label(self.settings.theme, lang))
                            .show_ui(ui, |ui| {
                                changed |= ui
                                    .selectable_value(
                                        &mut self.settings.theme,
                                        ThemeChoice::System,
                                        tr(lang, "theme_system"),
                                    )
                                    .changed();
                                changed |= ui
                                    .selectable_value(
                                        &mut self.settings.theme,
                                        ThemeChoice::Dark,
                                        tr(lang, "theme_dark"),
                                    )
                                    .changed();
                                changed |= ui
                                    .selectable_value(
                                        &mut self.settings.theme,
                                        ThemeChoice::Light,
                                        tr(lang, "theme_light"),
                                    )
                                    .changed();
                            });
                        egui::ComboBox::from_id_salt("language-choice")
                            .selected_text(language_choice_label(self.settings.language, lang))
                            .show_ui(ui, |ui| {
                                changed |= ui
                                    .selectable_value(
                                        &mut self.settings.language,
                                        LanguageChoice::System,
                                        tr(lang, "lang_system"),
                                    )
                                    .changed();
                                changed |= ui
                                    .selectable_value(
                                        &mut self.settings.language,
                                        LanguageChoice::ZhCn,
                                        "简体中文",
                                    )
                                    .changed();
                                changed |= ui
                                    .selectable_value(
                                        &mut self.settings.language,
                                        LanguageChoice::En,
                                        "English",
                                    )
                                    .changed();
                            });
                    });
                });
            });
        if changed {
            self.save_settings();
        }
    }

    fn draw_resize_grip(&self, ctx: &egui::Context) {
        let maximized = ctx.input(|input| input.viewport().maximized.unwrap_or(false));
        if maximized {
            return;
        }
        egui::Area::new("window-resize-grip".into())
            .anchor(egui::Align2::RIGHT_BOTTOM, egui::vec2(-4.0, -4.0))
            .order(egui::Order::Foreground)
            .show(ctx, |ui| {
                let (rect, response) =
                    ui.allocate_exact_size(egui::vec2(20.0, 20.0), egui::Sense::drag());
                let response = response.on_hover_cursor(egui::CursorIcon::ResizeNwSe);
                if response.drag_started() {
                    ui.ctx()
                        .send_viewport_cmd(egui::ViewportCommand::BeginResize(
                            egui::ResizeDirection::SouthEast,
                        ));
                }
                let stroke = egui::Stroke::new(1.0, table_header_color(ui));
                let painter = ui.painter();
                for offset in [5.0, 9.0, 13.0] {
                    painter.line_segment(
                        [
                            egui::pos2(rect.right() - offset, rect.bottom() - 2.0),
                            egui::pos2(rect.right() - 2.0, rect.bottom() - offset),
                        ],
                        stroke,
                    );
                }
            });
    }

    fn draw_controls(&mut self, ui: &mut egui::Ui) {
        let lang = self.language();
        egui::Frame::new()
            .fill(panel_color(ui))
            .stroke(egui::Stroke::new(1.0, border_color(ui)))
            .inner_margin(egui::Margin::symmetric(12, 8))
            .show(ui, |ui| {
                let total = ui.available_width();
                let demo_width = (total * 0.34).clamp(220.0, 420.0);
                let output_width = (total * 0.24).clamp(160.0, 320.0);
                ui.horizontal_wrapped(|ui| {
                    ui.label(RichText::new(tr(lang, "demo")).strong());
                    ui.add_sized(
                        [demo_width, 30.0],
                        egui::TextEdit::singleline(&mut self.settings.demo_path),
                    );
                    if ui.button(tr(lang, "browse")).clicked() {
                        self.browse_demo();
                    }
                    ui.separator();
                    ui.label(RichText::new(tr(lang, "output")).strong());
                    ui.add_sized(
                        [output_width, 30.0],
                        egui::TextEdit::singleline(&mut self.settings.output_dir),
                    );
                    if ui.button(tr(lang, "folder")).clicked() {
                        self.browse_output();
                    }
                    ui.separator();
                    ui.add_enabled_ui(!self.is_running(), |ui| {
                        if ui
                            .add(
                                egui::Button::new(RichText::new(tr(lang, "analyze")).strong())
                                    .fill(Color32::from_rgb(38, 86, 118))
                                    .min_size(egui::vec2(102.0, 32.0)),
                            )
                            .clicked()
                        {
                            self.analyze();
                        }
                        if ui
                            .add(
                                egui::Button::new(RichText::new(tr(lang, "convert")).strong())
                                    .fill(Color32::from_rgb(28, 118, 82))
                                    .min_size(egui::vec2(108.0, 32.0)),
                            )
                            .clicked()
                        {
                            self.request_convert();
                        }
                    });
                    if ui.button(tr(lang, "open_result")).clicked() {
                        self.open_result_folder();
                    }
                });
            });
    }

    fn draw_progress(&mut self, ui: &mut egui::Ui) {
        let lang = self.language();
        let progress_color = if self.error.is_some() {
            DANGER
        } else if self.result.is_some() {
            GOOD
        } else if self.is_running() {
            INFO
        } else if self.analysis.is_some() {
            INFO
        } else {
            WARN
        };
        egui::Frame::new()
            .fill(panel_deep_color(ui))
            .stroke(egui::Stroke::new(1.0, border_color(ui)))
            .inner_margin(egui::Margin::symmetric(12, 6))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(status_badge_text(self, lang).color(progress_color).strong());
                    ui.label(
                        RichText::new(&self.progress.stage).color(ui.visuals().weak_text_color()),
                    );
                    let progress = self.progress.fraction.unwrap_or(0.0);
                    let mut bar = egui::ProgressBar::new(progress)
                        .desired_width(ui.available_width())
                        .fill(progress_color);
                    if self.progress.fraction.is_some() {
                        bar = bar.show_percentage();
                    } else {
                        bar = bar.animate(true);
                    }
                    ui.add(bar);
                });
                if let Some(error) = &self.error {
                    ui.colored_label(DANGER, error);
                }
            });
    }

    fn draw_rounds(&mut self, ui: &mut egui::Ui) {
        let lang = self.language();
        let Some(analysis) = self.analysis.clone() else {
            ui.heading(RichText::new(tr(lang, "rounds")).color(ui.visuals().strong_text_color()));
            empty_panel(ui, tr(lang, "no_demo"), tr(lang, "no_demo_hint"));
            return;
        };

        ui.horizontal(|ui| {
            ui.heading(RichText::new(tr(lang, "rounds")).color(ui.visuals().strong_text_color()));
            ui.label(
                RichText::new(format!(
                    "{} / {}",
                    self.selected_rounds().len(),
                    analysis.rounds.len()
                ))
                .color(ui.visuals().weak_text_color()),
            );
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                let changed = ui
                    .checkbox(
                        &mut self.settings.include_suspicious,
                        tr(lang, "include_suspicious"),
                    )
                    .changed();
                if changed {
                    self.reconcile_round_selection();
                    self.save_settings();
                }
            });
        });
        egui::Frame::new()
            .fill(panel_color(ui))
            .stroke(egui::Stroke::new(1.0, border_color(ui)))
            .inner_margin(egui::Margin::symmetric(8, 6))
            .show(ui, |ui| {
                ui.horizontal_wrapped(|ui| {
                    metric_chip(ui, tr(lang, "map"), &analysis.map, INFO);
                    metric_chip(
                        ui,
                        tr(lang, "tick"),
                        &format!("{:.1}", analysis.tick_rate),
                        INFO,
                    );
                    metric_chip(ui, tr(lang, "rows"), &analysis.row_count.to_string(), INFO);
                    metric_chip(
                        ui,
                        tr(lang, "recommended"),
                        &analysis
                            .rounds
                            .iter()
                            .filter(|round| round.status == RoundStatus::Recommended)
                            .count()
                            .to_string(),
                        GOOD,
                    );
                    metric_chip(
                        ui,
                        tr(lang, "suspicious"),
                        &analysis
                            .rounds
                            .iter()
                            .filter(|round| round.status == RoundStatus::Suspicious)
                            .count()
                            .to_string(),
                        WARN,
                    );
                });
            });
        ui.add_space(8.0);
        let table_height = ui.available_height().max(180.0);
        TableBuilder::new(ui)
            .id_salt("round-table")
            .striped(true)
            .resizable(true)
            .auto_shrink([false, false])
            .max_scroll_height(table_height)
            .column(Column::exact(34.0))
            .column(Column::exact(58.0))
            .column(Column::exact(112.0))
            .column(Column::exact(78.0))
            .column(Column::exact(70.0))
            .column(Column::exact(82.0))
            .column(Column::exact(58.0))
            .column(Column::remainder().at_least(220.0))
            .header(26.0, |mut header| {
                header.col(|ui| table_header_text(ui, ""));
                header.col(|ui| table_header_text(ui, tr(lang, "round")));
                header.col(|ui| table_header_text(ui, tr(lang, "status")));
                header.col(|ui| table_header_text(ui, tr(lang, "time")));
                header.col(|ui| table_header_text(ui, "T/CT"));
                header.col(|ui| table_header_text(ui, tr(lang, "rows")));
                header.col(|ui| table_header_text(ui, tr(lang, "files")));
                header.col(|ui| table_header_text(ui, tr(lang, "notes")));
            })
            .body(|mut body| {
                for round in &analysis.rounds {
                    let allowed = round.status == RoundStatus::Recommended
                        || self.settings.include_suspicious;
                    let files = self.files_for_round(round.round);
                    let selected = self.round_selection.entry(round.round).or_insert(false);
                    if !allowed {
                        *selected = false;
                    }
                    body.row(28.0, |mut row| {
                        row.col(|ui| {
                            ui.spacing_mut().interact_size = egui::vec2(22.0, 22.0);
                            let checkbox_response =
                                ui.add_enabled(allowed, egui::Checkbox::without_text(selected));
                            if !allowed {
                                checkbox_response
                                    .on_disabled_hover_text(tr(lang, "enable_suspicious_hint"));
                            }
                        });
                        row.col(|ui| {
                            table_text(
                                ui,
                                format!("{:02}", round.round),
                                table_body_text_color(ui),
                                true,
                            )
                        });
                        row.col(|ui| {
                            let status_color = match round.status {
                                RoundStatus::Recommended => recommended_text_color(ui),
                                RoundStatus::Suspicious => suspicious_text_color(ui),
                            };
                            table_text(
                                ui,
                                round_status_label(round.status, lang),
                                status_color,
                                true,
                            );
                        });
                        row.col(|ui| {
                            table_text(
                                ui,
                                format!("{:.1}s", round.duration_seconds),
                                table_body_text_color(ui),
                                false,
                            );
                        });
                        row.col(|ui| {
                            table_text(
                                ui,
                                format!("{}/{}", round.t_players, round.ct_players),
                                table_body_text_color(ui),
                                false,
                            );
                        });
                        row.col(|ui| {
                            table_text(
                                ui,
                                round.valid_rows.to_string(),
                                table_body_text_color(ui),
                                false,
                            )
                        });
                        row.col(|ui| table_text(ui, files, table_body_text_color(ui), false));
                        row.col(|ui| {
                            let notes = if round.problems.is_empty() {
                                String::new()
                            } else {
                                round.problems.join("; ")
                            };
                            ui.add(
                                egui::Label::new(
                                    RichText::new(notes)
                                        .size(14.0)
                                        .color(table_muted_text_color(ui)),
                                )
                                .wrap(),
                            );
                        });
                    });
                }
            });
    }

    fn draw_result(&mut self, ui: &mut egui::Ui) {
        let lang = self.language();
        ui.heading(RichText::new(tr(lang, "output")).color(ui.visuals().strong_text_color()));
        ScrollArea::vertical()
            .id_salt("result-scroll")
            .auto_shrink([false, false])
            .show(ui, |ui| {
                let Some(result) = self.result.as_ref() else {
                    empty_panel(ui, tr(lang, "no_output"), tr(lang, "no_output_hint"));
                    return;
                };

                let manifest_text = result.manifest_path.display().to_string();
                let root_text = result.root.display().to_string();
                let root_path = result.root.clone();
                let first_round = self.first_selected_or_exported_round();
                let round_command = result.console_round_command(first_round);
                let seq_command = result.console_seq_command(first_round);
                let risky_round_command = result.console_risky_round_command(first_round);
                let risky_seq_command = result.console_risky_seq_command(first_round);
                let players = result.players.clone();

                egui::Frame::new()
                    .fill(panel_deep_color(ui))
                    .stroke(egui::Stroke::new(1.5, GOOD))
                    .inner_margin(egui::Margin::symmetric(12, 9))
                    .show(ui, |ui| {
                        ui.horizontal(|ui| {
                            ui.label(RichText::new("|").strong().size(24.0).color(GOOD));
                            ui.vertical(|ui| {
                                ui.label(
                                    RichText::new(tr(lang, "conversion_complete"))
                                        .strong()
                                        .size(20.0)
                                        .color(ui.visuals().strong_text_color()),
                                );
                                ui.label(
                                    RichText::new(format!(
                                        "{} / {} {}",
                                        format_bytes(result.output_bytes),
                                        result.rounds_exported,
                                        tr(lang, "rounds")
                                    ))
                                    .color(ui.visuals().weak_text_color()),
                                );
                            });
                        });
                    });
                ui.add_space(10.0);
                ui.horizontal_wrapped(|ui| {
                    summary_tile(
                        ui,
                        tr(lang, "size"),
                        &format_bytes(result.output_bytes),
                        GOOD,
                    );
                    summary_tile(
                        ui,
                        tr(lang, "rounds"),
                        &result.rounds_exported.to_string(),
                        INFO,
                    );
                    summary_tile(ui, tr(lang, "players"), &players.len().to_string(), INFO);
                    summary_tile(ui, ".dtr", &result.files_written.to_string(), GOOD);
                    if result.voice_requested || result.voice_sidecars > 0 {
                        let voice_value = if result.voice_sidecars > 0 {
                            result.voice_sidecars.to_string()
                        } else {
                            tr(lang, "none").to_string()
                        };
                        summary_tile(
                            ui,
                            tr(lang, "voice"),
                            &voice_value,
                            if result.voice_sidecars > 0 {
                                GOOD
                            } else {
                                WARN
                            },
                        );
                    }
                    summary_tile(
                        ui,
                        tr(lang, "validated"),
                        &result.validated.to_string(),
                        GOOD,
                    );
                });
                if result.cosmetic_files > 0 {
                    ui.add_space(6.0);
                    warning_strip(
                        ui,
                        tr(lang, "cosmetics_exported"),
                        &format!("{} / {}", result.cosmetic_files, result.sticker_files),
                    );
                }
                ui.add_space(10.0);
                egui::Frame::new()
                    .fill(panel_color(ui))
                    .stroke(egui::Stroke::new(1.0, border_color(ui)))
                    .inner_margin(egui::Margin::symmetric(10, 8))
                    .show(ui, |ui| {
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new(tr(lang, "players"))
                                    .strong()
                                    .color(ui.visuals().strong_text_color()),
                            );
                            ui.label(
                                RichText::new(tr(lang, "team_group_hint"))
                                    .color(ui.visuals().weak_text_color()),
                            );
                        });
                        ui.add_space(6.0);
                        if players.is_empty() {
                            ui.label(
                                RichText::new(tr(lang, "no_players"))
                                    .color(ui.visuals().weak_text_color()),
                            );
                        } else {
                            for team in [1_usize, 2, 3] {
                                let team_players: Vec<_> = players
                                    .iter()
                                    .filter(|player| player.team == team)
                                    .collect();
                                if !team_players.is_empty() {
                                    draw_player_team(ui, lang, team, &team_players);
                                    ui.add_space(6.0);
                                }
                            }
                        }
                    });
                ui.add_space(10.0);
                path_block(ui, tr(lang, "root"), &root_text);
                let mut open_output = false;
                ui.horizontal(|ui| {
                    open_output = ui.button(tr(lang, "open_output")).clicked();
                });
                path_block(ui, tr(lang, "manifest"), &manifest_text);
                ui.add_space(8.0);
                let mut copy_round_command = false;
                let mut copy_seq_command = false;
                let mut copy_manifest = false;
                let mut copy_risky_round_command = false;
                let mut copy_risky_seq_command = false;
                ui.horizontal(|ui| {
                    ui.label(RichText::new(tr(lang, "cs2_console")).strong());
                    copy_round_command = ui.button(tr(lang, "copy_round_command")).clicked();
                    copy_seq_command = ui.button(tr(lang, "copy_seq_command")).clicked();
                    if risky_round_command.is_some() {
                        copy_risky_round_command =
                            ui.button(tr(lang, "copy_risky_round_command")).clicked();
                        copy_risky_seq_command =
                            ui.button(tr(lang, "copy_risky_seq_command")).clicked();
                    }
                    copy_manifest = ui.button(tr(lang, "copy_manifest")).clicked();
                });
                if open_output {
                    if let Err(err) = open_folder_path(&root_path) {
                        self.push_log(format!("could not open output folder: {err}"));
                    }
                }
                if copy_round_command {
                    ui.ctx().copy_text(round_command.clone());
                    self.push_log(tr(lang, "copied_round_command").to_string());
                }
                if copy_seq_command {
                    ui.ctx().copy_text(seq_command.clone());
                    self.push_log(tr(lang, "copied_seq_command").to_string());
                }
                if copy_risky_round_command {
                    if let Some(command) = &risky_round_command {
                        ui.ctx().copy_text(command.clone());
                        self.push_log(tr(lang, "copied_risky_command").to_string());
                    }
                }
                if copy_risky_seq_command {
                    if let Some(command) = &risky_seq_command {
                        ui.ctx().copy_text(command.clone());
                        self.push_log(tr(lang, "copied_risky_command").to_string());
                    }
                }
                if copy_manifest {
                    ui.ctx().copy_text(manifest_text.clone());
                    self.push_log(tr(lang, "copied_manifest").to_string());
                }
                let mut round_text = round_command;
                ui.add(
                    egui::TextEdit::singleline(&mut round_text)
                        .desired_width(ui.available_width())
                        .code_editor()
                        .interactive(false),
                );
                let mut seq_text = seq_command;
                ui.add(
                    egui::TextEdit::singleline(&mut seq_text)
                        .desired_width(ui.available_width())
                        .code_editor()
                        .interactive(false),
                );
                if let Some(command) = risky_seq_command {
                    ui.add_space(6.0);
                    warning_strip(
                        ui,
                        tr(lang, "risky_runtime_command"),
                        tr(lang, "risky_runtime_command_body"),
                    );
                    let mut risky_text = command;
                    ui.add(
                        egui::TextEdit::singleline(&mut risky_text)
                            .desired_width(ui.available_width())
                            .code_editor()
                            .interactive(false),
                    );
                }
            });
    }

    fn draw_advanced(&mut self, ui: &mut egui::Ui) {
        let lang = self.language();
        let arrow = if self.settings.advanced_open {
            "v"
        } else {
            ">"
        };
        if ui
            .add_sized(
                [ui.available_width(), 28.0],
                egui::Button::new(format!("{arrow} {}", tr(lang, "advanced")))
                    .fill(panel_color(ui)),
            )
            .clicked()
        {
            self.settings.advanced_open = !self.settings.advanced_open;
            self.save_settings();
        }

        if !self.settings.advanced_open {
            return;
        }

        let mut changed = false;
        egui::Frame::new()
            .fill(panel_color(ui))
            .stroke(egui::Stroke::new(1.0, border_color(ui)))
            .inner_margin(egui::Margin::symmetric(10, 8))
            .show(ui, |ui| {
                ui.horizontal_wrapped(|ui| {
                    egui::ComboBox::from_label(tr(lang, "side"))
                        .selected_text(side_label(self.settings.side, lang))
                        .show_ui(ui, |ui| {
                            changed |= ui
                                .selectable_value(
                                    &mut self.settings.side,
                                    Side::Both,
                                    tr(lang, "side_both"),
                                )
                                .changed();
                            changed |= ui
                                .selectable_value(&mut self.settings.side, Side::T, "T")
                                .changed();
                            changed |= ui
                                .selectable_value(&mut self.settings.side, Side::Ct, "CT")
                                .changed();
                        });
                    changed |= ui
                        .checkbox(&mut self.settings.full_round, tr(lang, "full_round"))
                        .changed();
                    ui.label(tr(lang, "freeze_preroll"));
                    changed |= ui
                        .add(
                            egui::DragValue::new(&mut self.settings.freeze_preroll_seconds)
                                .speed(0.5)
                                .range(0.0..=120.0)
                                .suffix("s"),
                        )
                        .changed();
                    let voice_response =
                        ui.checkbox(&mut self.settings.export_voice, tr(lang, "export_voice"));
                    changed |= voice_response.changed();
                    voice_response.on_hover_text(tr(lang, "export_voice_hint"));
                    ui.separator();
                    let cosmetics_changed = ui
                        .checkbox(
                            &mut self.settings.export_cosmetics,
                            tr(lang, "export_cosmetics"),
                        )
                        .changed();
                    if cosmetics_changed {
                        changed = true;
                        self.cosmetic_acknowledged = false;
                        self.cosmetic_confirmation.clear();
                        if self.settings.export_cosmetics {
                            self.show_cosmetic_disclaimer = true;
                        } else {
                            self.show_cosmetic_disclaimer = false;
                        }
                    }
                    if self.settings.export_cosmetics
                        && ui
                            .small_button(format!(
                                "{} {}",
                                if self.settings.cosmetics_open {
                                    "v"
                                } else {
                                    ">"
                                },
                                tr(lang, "cosmetic_details")
                            ))
                            .clicked()
                    {
                        self.settings.cosmetics_open = !self.settings.cosmetics_open;
                        changed = true;
                    }
                    if self.settings.export_cosmetics && self.cosmetic_acknowledged {
                        ui.colored_label(GOOD, tr(lang, "risk_confirmed"));
                    } else if self.settings.export_cosmetics {
                        ui.colored_label(WARN, tr(lang, "confirmation_required"));
                    }
                });
                if self.settings.export_cosmetics && self.settings.cosmetics_open {
                    ui.horizontal_wrapped(|ui| {
                        ui.add_space(18.0);
                        changed |= ui
                            .checkbox(
                                &mut self.settings.export_stickers,
                                tr(lang, "export_stickers"),
                            )
                            .changed();
                        changed |= ui
                            .checkbox(&mut self.settings.export_charms, tr(lang, "export_charms"))
                            .changed();
                    });
                }
                if self.settings.export_cosmetics {
                    ui.add_space(4.0);
                    warning_strip(
                        ui,
                        tr(lang, "high_risk_option"),
                        tr(lang, "high_risk_option_body"),
                    );
                }
            });
        if changed {
            self.save_settings();
        }
    }

    fn draw_logs(&mut self, ui: &mut egui::Ui) {
        let lang = self.language();
        let arrow = if self.settings.activity_open {
            "v"
        } else {
            ">"
        };
        let latest = self
            .logs
            .last()
            .map(String::as_str)
            .unwrap_or_else(|| tr(lang, "no_activity"));
        if ui
            .add_sized(
                [ui.available_width(), 28.0],
                egui::Button::new(format!("{arrow} {}  {}", tr(lang, "activity"), latest))
                    .fill(panel_deep_color(ui)),
            )
            .clicked()
        {
            self.settings.activity_open = !self.settings.activity_open;
            self.save_settings();
        }

        if !self.settings.activity_open {
            return;
        }

        egui::Frame::new()
            .fill(panel_deep_color(ui))
            .stroke(egui::Stroke::new(1.0, border_color(ui)))
            .inner_margin(egui::Margin::symmetric(8, 6))
            .show(ui, |ui| {
                ScrollArea::vertical()
                    .stick_to_bottom(true)
                    .id_salt("log-scroll")
                    .auto_shrink([false, false])
                    .max_height(118.0)
                    .show(ui, |ui| {
                        for line in &self.logs {
                            ui.label(RichText::new(line).color(ui.visuals().weak_text_color()));
                        }
                    });
            });
    }

    fn draw_overwrite_dialog(&mut self, ctx: &egui::Context) {
        if self.pending_overwrite.is_none() {
            return;
        };
        let lang = self.language();
        let path = self
            .pending_overwrite
            .as_ref()
            .map(|pending| pending.overwrite_root.display().to_string())
            .unwrap_or_default();
        egui::Window::new(tr(lang, "output_exists"))
            .collapsible(false)
            .resizable(false)
            .show(ctx, |ui| {
                ui.label(tr(lang, "output_exists_body"));
                ui.label(path);
                ui.horizontal(|ui| {
                    if ui.button(tr(lang, "clear_and_convert")).clicked() {
                        if let Some(pending) = self.pending_overwrite.take() {
                            self.start_convert(pending, true);
                        }
                    }
                    if ui.button(tr(lang, "cancel")).clicked() {
                        self.pending_overwrite = None;
                    }
                });
            });
    }

    fn draw_cosmetic_disclaimer_dialog(&mut self, ctx: &egui::Context) {
        if !self.show_cosmetic_disclaimer {
            return;
        }
        let lang = self.language();
        egui::Window::new(tr(lang, "cosmetic_confirmation"))
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .collapsible(false)
            .resizable(false)
            .frame(
                egui::Frame::new()
                    .fill(Color32::from_rgb(52, 18, 20))
                    .stroke(egui::Stroke::new(2.0, DANGER))
                    .corner_radius(8)
                    .inner_margin(egui::Margin::symmetric(18, 16)),
            )
            .show(ctx, |ui| {
                ui.label(
                    RichText::new(tr(lang, "high_risk_title"))
                        .strong()
                        .size(24.0)
                        .color(Color32::WHITE),
                );
                ui.label(
                    RichText::new(tr(lang, "risk_intro")).color(Color32::from_rgb(255, 220, 220)),
                );
                ui.add_space(8.0);
                egui::Frame::new()
                    .fill(Color32::from_rgb(76, 24, 27))
                    .stroke(egui::Stroke::new(1.0, Color32::from_rgb(180, 66, 66)))
                    .corner_radius(6)
                    .inner_margin(egui::Margin::symmetric(12, 10))
                    .show(ui, |ui| {
                        ui.label(RichText::new(tr(lang, "before_enable")).strong());
                        ui.label(tr(lang, "risk_bullet_guidelines"));
                        ui.label(tr(lang, "risk_bullet_default_off"));
                        ui.label(tr(lang, "risk_bullet_public"));
                    });
                ui.add_space(10.0);
                ui.label(RichText::new(tr(lang, "type_to_unlock")).strong());
                ui.label(
                    RichText::new(COSMETIC_CONFIRMATION_PHRASE)
                        .monospace()
                        .strong()
                        .color(Color32::from_rgb(255, 214, 102)),
                );
                ui.add_sized(
                    [420.0, 28.0],
                    egui::TextEdit::singleline(&mut self.cosmetic_confirmation),
                );
                let confirmed = cosmetic_confirmation_matches(&self.cosmetic_confirmation);
                if !confirmed {
                    ui.colored_label(DANGER, tr(lang, "phrase_required"));
                }
                ui.add_space(8.0);
                ui.horizontal(|ui| {
                    if ui
                        .add_enabled(
                            confirmed,
                            egui::Button::new(
                                RichText::new(tr(lang, "enable_risky_export")).strong(),
                            )
                            .fill(DANGER)
                            .min_size(egui::vec2(172.0, 34.0)),
                        )
                        .clicked()
                    {
                        self.cosmetic_acknowledged = true;
                        self.show_cosmetic_disclaimer = false;
                        self.error = None;
                        self.save_settings();
                        self.push_log(tr(lang, "risk_confirmed_log").to_string());
                    }
                    if ui.button(tr(lang, "cancel")).clicked() {
                        self.settings.export_cosmetics = false;
                        self.settings.export_stickers = false;
                        self.settings.export_charms = false;
                        self.cosmetic_acknowledged = false;
                        self.cosmetic_confirmation.clear();
                        self.show_cosmetic_disclaimer = false;
                        self.save_settings();
                    }
                });
            });
    }

    fn files_for_round(&self, round: u32) -> String {
        self.result
            .as_ref()
            .and_then(|result| result.files_by_round.get(&round).copied())
            .map(|files| files.to_string())
            .unwrap_or_else(|| "-".to_string())
    }

    fn first_selected_or_exported_round(&self) -> Option<u32> {
        self.selected_rounds().into_iter().next().or_else(|| {
            self.result
                .as_ref()
                .and_then(|result| result.files_by_round.keys().next().copied())
        })
    }
}

#[derive(Clone)]
struct PendingConversion {
    parsed: Arc<ParsedDemo>,
    options: ConvertOptions,
    export_voice: bool,
    overwrite_root: PathBuf,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum RunningTask {
    Analyze,
    Convert,
}

enum WorkerMessage {
    Log(String),
    AnalysisComplete {
        parsed: Arc<ParsedDemo>,
        analysis: DemoAnalysis,
    },
    ConversionProgress(ConversionProgress),
    ConversionComplete {
        report: ConversionReport,
        validated: usize,
        voice_requested: bool,
        voice_sidecars: usize,
    },
    Failed(String),
}

#[derive(Default)]
struct GuiProgress {
    stage: String,
    fraction: Option<f32>,
    file_units_done: usize,
    file_units_total: usize,
    artifact_units_done: usize,
    artifact_units_total: usize,
}

impl GuiProgress {
    fn begin(&mut self, stage: impl Into<String>, fraction: Option<f32>) {
        self.stage = stage.into();
        self.fraction = fraction;
        self.file_units_done = 0;
        self.file_units_total = 0;
        self.artifact_units_done = 0;
        self.artifact_units_total = 0;
    }

    fn apply_conversion_event(&mut self, event: &ConversionProgress) {
        match event {
            ConversionProgress::AnalysisStarted => self.begin("Analyzing selected rounds", None),
            ConversionProgress::AnalysisFinished {
                selected_rounds,
                estimated_files,
                ..
            } => {
                self.stage = format!("Exporting {selected_rounds} rounds");
                self.file_units_total = (*estimated_files).max(1);
                self.file_units_done = 0;
                self.fraction = Some(0.08);
            }
            ConversionProgress::RoundStarted { round, .. } => {
                self.stage = format!("Round {round:02}");
            }
            ConversionProgress::PlayerWritten { .. } => {
                self.file_units_done += 1;
                let ratio = self.file_units_done as f32 / self.file_units_total.max(1) as f32;
                self.fraction = Some(0.10 + 0.72 * ratio.min(1.0));
            }
            ConversionProgress::ArtifactsWritingStarted { artifacts, .. } => {
                self.stage = "Writing output files".to_string();
                self.artifact_units_total = (*artifacts).max(1);
                self.artifact_units_done = 0;
                self.fraction = Some(0.84);
            }
            ConversionProgress::ArtifactWritten { .. } => {
                self.artifact_units_done += 1;
                let ratio =
                    self.artifact_units_done as f32 / self.artifact_units_total.max(1) as f32;
                self.fraction = Some(0.84 + 0.10 * ratio.min(1.0));
            }
            ConversionProgress::Finished { .. } => {
                self.stage = "Validating output".to_string();
                self.fraction = Some(0.96);
            }
            ConversionProgress::RoundSkipped { .. }
            | ConversionProgress::PlayerSkipped { .. }
            | ConversionProgress::RoundFinished { .. } => {}
        }
    }

    fn finish(&mut self, stage: impl Into<String>) {
        self.stage = stage.into();
        self.fraction = Some(1.0);
    }

    fn fail(&mut self, stage: impl Into<String>) {
        self.stage = stage.into();
        self.fraction = Some(0.0);
    }
}

#[derive(Clone)]
struct PlayerSummary {
    team: usize,
    steam_id: u64,
    name: String,
    rounds: usize,
    files: usize,
}

struct PlayerAccumulator {
    first_round: u32,
    first_side: String,
    name: String,
    rounds: BTreeSet<u32>,
    files: usize,
}

struct ConversionResultView {
    root: PathBuf,
    manifest_path: PathBuf,
    files_written: usize,
    validated: usize,
    output_bytes: u64,
    rounds_exported: usize,
    files_by_round: BTreeMap<u32, usize>,
    players: Vec<PlayerSummary>,
    voice_requested: bool,
    voice_sidecars: usize,
    cosmetic_files: usize,
    sticker_files: usize,
    charm_files: usize,
}

impl ConversionResultView {
    fn from_report(
        report: ConversionReport,
        validated: usize,
        voice_requested: bool,
        voice_sidecars: usize,
    ) -> Self {
        let output_bytes = directory_size_bytes(&report.root).unwrap_or(0);
        let rounds_exported = report.manifest.rounds.len();
        let files_by_round = report
            .manifest
            .rounds
            .iter()
            .map(|round| (round.round, round.files))
            .collect();
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
        Self {
            root: report.root,
            manifest_path: report.manifest_path,
            files_written: report.files_written,
            validated,
            output_bytes,
            rounds_exported,
            files_by_round,
            players,
            voice_requested,
            voice_sidecars,
            cosmetic_files,
            sticker_files,
            charm_files,
        }
    }

    fn console_round_command(&self, first_round: Option<u32>) -> String {
        self.console_round_command_with_options(first_round, None)
    }

    fn console_seq_command(&self, first_round: Option<u32>) -> String {
        self.console_seq_command_with_options(first_round, None)
    }

    fn console_risky_round_command(&self, first_round: Option<u32>) -> Option<String> {
        let preset = self.cosmetic_runtime_preset()?;
        Some(self.console_round_command_with_options(first_round, Some(preset)))
    }

    fn console_risky_seq_command(&self, first_round: Option<u32>) -> Option<String> {
        let preset = self.cosmetic_runtime_preset()?;
        Some(self.console_seq_command_with_options(first_round, Some(preset)))
    }

    fn console_round_command_with_options(
        &self,
        first_round: Option<u32>,
        cosmetic_preset: Option<&str>,
    ) -> String {
        let round = first_round.unwrap_or(0);
        let manifest = console_quote_path(&self.manifest_path);
        self.console_command_with_prefixes(
            format!("dtr_go round \"{manifest}\" {round}"),
            cosmetic_preset,
        )
    }

    fn console_seq_command_with_options(
        &self,
        first_round: Option<u32>,
        cosmetic_preset: Option<&str>,
    ) -> String {
        let round = first_round.unwrap_or(0);
        let manifest = console_quote_path(&self.manifest_path);
        self.console_command_with_prefixes(
            format!("dtr_go seq \"{manifest}\" {round}"),
            cosmetic_preset,
        )
    }

    fn console_command_with_prefixes(
        &self,
        command: String,
        cosmetic_preset: Option<&str>,
    ) -> String {
        let mut prefixes = Vec::new();
        if self.voice_sidecars > 0 {
            prefixes.push("dtr_voice_auto on".to_string());
        }
        if let Some(preset) = cosmetic_preset {
            prefixes.push(format!("dtr_cosmetics {preset}"));
        }
        if prefixes.is_empty() {
            command
        } else {
            format!("{}; {command}", prefixes.join("; "))
        }
    }

    fn cosmetic_runtime_preset(&self) -> Option<&'static str> {
        if self.cosmetic_files == 0 {
            return None;
        }
        if self.sticker_files > 0 || self.charm_files > 0 {
            Some("full")
        } else {
            Some("basic")
        }
    }
}

fn analyze_worker(demo_path: PathBuf, tx: Sender<WorkerMessage>) {
    let _ = tx.send(WorkerMessage::Log(format!(
        "reading {} with voice metadata",
        demo_path.display()
    )));
    let result = read_demo_with_options(
        &demo_path,
        ReadDemoOptions {
            collect_voice: true,
            // Analysis is cached, and users may enable cosmetic export afterwards.
            collect_cosmetics: true,
        },
    )
    .map(|parsed| {
        let analysis = analyze_demo(&parsed, AnalysisOptions::default());
        (Arc::new(parsed), analysis)
    });
    match result {
        Ok((parsed, analysis)) => {
            let _ = tx.send(WorkerMessage::Log(format!(
                "analysis complete: {} rounds",
                analysis.rounds.len()
            )));
            let _ = tx.send(WorkerMessage::AnalysisComplete { parsed, analysis });
        }
        Err(err) => {
            let _ = tx.send(WorkerMessage::Failed(err.to_string()));
        }
    }
}

fn convert_worker(pending: PendingConversion, clear_existing: bool, tx: Sender<WorkerMessage>) {
    let result = (|| -> crate::Result<(ConversionReport, usize, usize)> {
        if clear_existing && pending.overwrite_root.exists() {
            fs::remove_dir_all(&pending.overwrite_root)
                .map_err(|err| crate::io_error(&pending.overwrite_root, err))?;
        }
        let progress_tx = tx.clone();
        let report = export_demo_with_progress(&pending.parsed, &pending.options, move |event| {
            let _ = progress_tx.send(WorkerMessage::ConversionProgress(event));
        })?;
        let mut voice_sidecars = 0;
        if pending.export_voice {
            let _ = tx.send(WorkerMessage::Log("exporting voice sidecars".to_string()));
            match export_round_voice_sidecars(&pending.parsed, &report) {
                Ok(reports) => {
                    voice_sidecars = reports.len();
                    for voice in reports {
                        let _ = tx.send(WorkerMessage::Log(format!(
                            "voice sidecar {} frames={} speakers={} duration={:.2}s",
                            voice.path.display(),
                            voice.frame_count,
                            voice.speaker_count,
                            voice.duration_seconds
                        )));
                    }
                }
                Err(err) => {
                    let _ = tx.send(WorkerMessage::Log(format!(
                        "warning: voice sidecar export skipped: {err}"
                    )));
                }
            }
        }
        let _ = tx.send(WorkerMessage::Log("validating output".to_string()));
        let validated = validate_dtr_path(&report.root)?;
        Ok((report, validated, voice_sidecars))
    })();

    match result {
        Ok((report, validated, voice_sidecars)) => {
            let _ = tx.send(WorkerMessage::ConversionComplete {
                report,
                validated,
                voice_requested: pending.export_voice,
                voice_sidecars,
            });
        }
        Err(err) => {
            let _ = tx.send(WorkerMessage::Failed(err.to_string()));
        }
    }
}

fn install_system_cjk_font(ctx: &egui::Context) {
    let Some(font_bytes) = load_system_cjk_font() else {
        return;
    };

    let mut fonts = FontDefinitions::default();
    let font_name = "cs2-demotracer-cjk".to_string();
    fonts.font_data.insert(
        font_name.clone(),
        Arc::new(FontData::from_owned(font_bytes)),
    );
    for family in [FontFamily::Proportional, FontFamily::Monospace] {
        if let Some(fallbacks) = fonts.families.get_mut(&family) {
            if !fallbacks.iter().any(|name| name == &font_name) {
                fallbacks.push(font_name.clone());
            }
        }
    }
    ctx.set_fonts(fonts);
}

fn load_system_cjk_font() -> Option<Vec<u8>> {
    system_cjk_font_candidates()
        .into_iter()
        .find_map(|path| match fs::read(path) {
            Ok(bytes) if !bytes.is_empty() => Some(bytes),
            _ => None,
        })
}

fn system_cjk_font_candidates() -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    #[cfg(windows)]
    {
        if let Some(windir) = std::env::var_os("WINDIR") {
            append_windows_cjk_font_candidates(
                &mut candidates,
                &PathBuf::from(windir).join("Fonts"),
            );
        }
    }
    candidates
}

fn append_windows_cjk_font_candidates(candidates: &mut Vec<PathBuf>, fonts_dir: &Path) {
    for file_name in [
        "msyh.ttc",
        "msyhbd.ttc",
        "msyhl.ttc",
        "simsun.ttc",
        "simsunb.ttf",
    ] {
        candidates.push(fonts_dir.join(file_name));
    }
}

fn resolve_language(choice: LanguageChoice) -> UiLanguage {
    match choice {
        LanguageChoice::ZhCn => UiLanguage::ZhCn,
        LanguageChoice::En => UiLanguage::En,
        LanguageChoice::System => system_language(),
    }
}

fn system_language() -> UiLanguage {
    #[cfg(windows)]
    {
        #[link(name = "kernel32")]
        extern "system" {
            fn GetUserDefaultUILanguage() -> u16;
        }
        let primary_language_id = unsafe { GetUserDefaultUILanguage() } & 0x03ff;
        if primary_language_id == 0x04 {
            return UiLanguage::ZhCn;
        }
        if primary_language_id == 0x09 {
            return UiLanguage::En;
        }
    }

    let locale = std::env::var("LANG")
        .or_else(|_| std::env::var("LANGUAGE"))
        .or_else(|_| std::env::var("LC_ALL"))
        .unwrap_or_default()
        .to_ascii_lowercase();
    if locale.starts_with("zh") || locale.contains("zh_cn") || locale.contains("zh-hans") {
        UiLanguage::ZhCn
    } else {
        UiLanguage::En
    }
}

fn resolve_theme(choice: ThemeChoice, ctx: &egui::Context) -> ResolvedTheme {
    match choice {
        ThemeChoice::Dark => ResolvedTheme::Dark,
        ThemeChoice::Light => ResolvedTheme::Light,
        ThemeChoice::System => match ctx.system_theme() {
            Some(egui::Theme::Light) => ResolvedTheme::Light,
            _ => ResolvedTheme::Dark,
        },
    }
}

fn apply_visuals(ctx: &egui::Context, theme: ResolvedTheme) {
    let mut visuals = match theme {
        ResolvedTheme::Dark => egui::Visuals::dark(),
        ResolvedTheme::Light => egui::Visuals::light(),
    };
    if theme == ResolvedTheme::Dark {
        visuals.panel_fill = Color32::from_rgb(18, 19, 22);
        visuals.window_fill = Color32::from_rgb(25, 26, 30);
        visuals.extreme_bg_color = Color32::from_rgb(11, 12, 15);
        visuals.faint_bg_color = Color32::from_rgb(31, 32, 37);
        visuals.widgets.active.bg_fill = Color32::from_rgb(40, 86, 70);
        visuals.widgets.hovered.bg_fill = Color32::from_rgb(58, 60, 68);
        visuals.selection.bg_fill = Color32::from_rgb(48, 98, 78);
    } else {
        visuals.panel_fill = Color32::from_rgb(236, 238, 242);
        visuals.window_fill = Color32::from_rgb(248, 249, 251);
        visuals.extreme_bg_color = Color32::from_rgb(255, 255, 255);
        visuals.faint_bg_color = Color32::from_rgb(229, 232, 238);
        visuals.widgets.active.bg_fill = Color32::from_rgb(206, 223, 216);
        visuals.widgets.hovered.bg_fill = Color32::from_rgb(225, 229, 236);
        visuals.selection.bg_fill = Color32::from_rgb(188, 214, 203);
    }
    ctx.set_visuals(visuals);

    let mut style = (*ctx.style()).clone();
    style
        .text_styles
        .insert(TextStyle::Heading, FontId::proportional(25.0));
    style
        .text_styles
        .insert(TextStyle::Body, FontId::proportional(16.0));
    style
        .text_styles
        .insert(TextStyle::Button, FontId::proportional(16.0));
    style
        .text_styles
        .insert(TextStyle::Monospace, FontId::monospace(15.0));
    style.spacing.item_spacing = egui::vec2(9.0, 8.0);
    style.spacing.button_padding = egui::vec2(12.0, 7.0);
    style.spacing.interact_size = egui::vec2(44.0, 30.0);
    ctx.set_style(style);
}

fn top_bar_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(19, 20, 24)
    } else {
        Color32::from_rgb(244, 246, 249)
    }
}

fn panel_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        PANEL
    } else {
        Color32::from_rgb(250, 251, 253)
    }
}

fn panel_deep_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        PANEL_DEEP
    } else {
        Color32::from_rgb(233, 236, 242)
    }
}

fn border_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(50, 56, 66)
    } else {
        Color32::from_rgb(207, 213, 224)
    }
}

fn toggle_maximized(ctx: &egui::Context) {
    let maximized = ctx.input(|input| input.viewport().maximized.unwrap_or(false));
    ctx.send_viewport_cmd(egui::ViewportCommand::Maximized(!maximized));
}

fn titlebar_button(
    ui: &mut egui::Ui,
    label: &str,
    accent: Color32,
    tooltip: &str,
) -> egui::Response {
    let (rect, response) = ui.allocate_exact_size(egui::vec2(34.0, 26.0), egui::Sense::click());
    let hovered = response.hovered();
    let fill = if hovered {
        if label == "x" {
            Color32::from_rgb(176, 45, 55)
        } else if ui.visuals().dark_mode {
            Color32::from_rgb(42, 47, 55)
        } else {
            Color32::from_rgb(218, 225, 232)
        }
    } else {
        Color32::TRANSPARENT
    };
    ui.painter().rect_filled(rect, 4.0, fill);
    let text_color = if hovered && label == "x" {
        Color32::WHITE
    } else if hovered {
        accent
    } else {
        ui.visuals().weak_text_color()
    };
    ui.painter().text(
        rect.center(),
        egui::Align2::CENTER_CENTER,
        label,
        FontId::proportional(15.0),
        text_color,
    );
    response.on_hover_text(tooltip)
}

fn table_body_text_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(224, 230, 238)
    } else {
        Color32::from_rgb(20, 27, 36)
    }
}

fn table_header_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(154, 165, 180)
    } else {
        Color32::from_rgb(92, 99, 110)
    }
}

fn table_muted_text_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(136, 148, 164)
    } else {
        Color32::from_rgb(91, 99, 111)
    }
}

fn recommended_text_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(97, 215, 150)
    } else {
        Color32::from_rgb(35, 122, 83)
    }
}

fn suspicious_text_color(ui: &egui::Ui) -> Color32 {
    if ui.visuals().dark_mode {
        Color32::from_rgb(236, 175, 89)
    } else {
        Color32::from_rgb(151, 96, 35)
    }
}

fn language_choice_label(choice: LanguageChoice, lang: UiLanguage) -> &'static str {
    match choice {
        LanguageChoice::System => tr(lang, "lang_system"),
        LanguageChoice::ZhCn => "简体中文",
        LanguageChoice::En => "English",
    }
}

fn theme_choice_label(choice: ThemeChoice, lang: UiLanguage) -> &'static str {
    match choice {
        ThemeChoice::System => tr(lang, "theme_system"),
        ThemeChoice::Dark => tr(lang, "theme_dark"),
        ThemeChoice::Light => tr(lang, "theme_light"),
    }
}

fn side_label(side: Side, lang: UiLanguage) -> &'static str {
    match side {
        Side::Both => tr(lang, "side_both"),
        Side::T => "T",
        Side::Ct => "CT",
    }
}

fn player_team_label(team: usize, lang: UiLanguage) -> &'static str {
    match team {
        1 => tr(lang, "team_1"),
        2 => tr(lang, "team_2"),
        _ => tr(lang, "team_other"),
    }
}

fn player_count_label(count: usize, lang: UiLanguage) -> String {
    match lang {
        UiLanguage::ZhCn => format!("{count} 人"),
        UiLanguage::En => {
            if count == 1 {
                "1 player".to_string()
            } else {
                format!("{count} players")
            }
        }
    }
}

fn round_status_label(status: RoundStatus, lang: UiLanguage) -> &'static str {
    match status {
        RoundStatus::Recommended => tr(lang, "recommended"),
        RoundStatus::Suspicious => tr(lang, "suspicious"),
    }
}

fn status_badge_text(app: &DemoTracerGui, lang: UiLanguage) -> RichText {
    let label = if app.error.is_some() {
        tr(lang, "status_error")
    } else if app.result.is_some() {
        tr(lang, "status_complete")
    } else {
        match app.running {
            Some(RunningTask::Analyze) => tr(lang, "status_parsing"),
            Some(RunningTask::Convert) => tr(lang, "status_converting"),
            None if app.analysis.is_some() => tr(lang, "status_parsed"),
            None => tr(lang, "status_idle"),
        }
    };
    RichText::new(label)
}

fn tr(lang: UiLanguage, key: &str) -> &'static str {
    match lang {
        UiLanguage::ZhCn => match key {
            "demo" => "DEMO",
            "output" => "输出",
            "browse" => "浏览",
            "folder" => "目录",
            "analyze" => "解析",
            "convert" => "转换",
            "open_result" => "打开 output",
            "rounds" => "回合",
            "round" => "回合",
            "status" => "状态",
            "time" => "时长",
            "rows" => "Rows",
            "files" => "文件",
            "notes" => "异常",
            "map" => "地图",
            "tick" => "Tick",
            "recommended" => "推荐",
            "suspicious" => "可疑",
            "include_suspicious" => "包含可疑回合",
            "enable_suspicious_hint" => "先开启包含可疑回合",
            "no_demo" => "未解析",
            "no_demo_hint" => "选择 demo 后点击解析",
            "no_output" => "未输出",
            "no_output_hint" => "转换完成后显示结果",
            "conversion_complete" => "转换完成",
            "size" => "大小",
            "players" => "选手",
            "team_group_hint" => "按最早导出回合分组，避免换边误读",
            "team_1" => "队伍 1",
            "team_2" => "队伍 2",
            "team_other" => "其他",
            "round_short" => "回合",
            "file_short" => "文件",
            "validated" => "验证",
            "voice" => "语音",
            "none" => "无",
            "cosmetics_exported" => "已导出饰品元数据",
            "no_players" => "没有导出选手文件",
            "root" => "根目录",
            "manifest" => "Manifest",
            "cs2_console" => "CS2 控制台",
            "open_output" => "打开 output 文件夹",
            "copy_command" => "复制指令",
            "copy_round_command" => "复制 round",
            "copy_seq_command" => "复制 seq",
            "copy_risky_round_command" => "复制饰品 round",
            "copy_risky_seq_command" => "复制饰品 seq",
            "copy_manifest" => "复制 manifest",
            "copied_command" => "已复制 CS2 指令",
            "copied_round_command" => "已复制 round 指令",
            "copied_seq_command" => "已复制 seq 指令",
            "copied_risky_command" => "已复制带饰品风险指令",
            "copied_manifest" => "已复制 manifest 路径",
            "risky_runtime_command" => "带饰品 runtime 指令",
            "risky_runtime_command_body" => "这条指令会在服务器开启 dtr_cosmetics，需自行评估 GSLT 风险。",
            "advanced" => "高级选项",
            "activity" => "Activity",
            "no_activity" => "无事件",
            "side" => "阵营",
            "side_both" => "双方",
            "full_round" => "完整回合",
            "freeze_preroll" => "freeze pre-roll",
            "export_voice" => "导出语音(若有)",
            "export_voice_hint" => "默认导出 demo 自带游戏内语音 sidecar；demo 没有语音时不会生成 voice/。",
            "export_cosmetics" => "导出饰品",
            "cosmetic_details" => "饰品细项",
            "export_stickers" => "导出贴纸",
            "export_charms" => "导出挂坠",
            "risk_confirmed" => "风险已确认",
            "confirmation_required" => "需要确认",
            "high_risk_option" => "高风险选项",
            "high_risk_option_body" => "只写入 demo 证据；后续 runtime 启用饰品/探员/贴纸/挂坠对齐前需自行评估 GSLT 风险。",
            "output_exists" => "输出已存在",
            "output_exists_body" => "目标 demo 输出目录已存在。",
            "clear_and_convert" => "清理并转换",
            "cancel" => "取消",
            "cosmetic_confirmation" => "饰品导出确认",
            "high_risk_title" => "高风险：GSLT / 饰品导出",
            "risk_intro" => "这只会把 demo 里的武器、刀、手套、探员、贴纸、挂坠证据写入 manifest；风险来自后续 runtime 使用这些证据做饰品/探员/贴纸/挂坠对齐。",
            "before_enable" => "启用前确认：",
            "risk_bullet_guidelines" => "- 你已评估 Valve 服务器规则和 GSLT 风险。",
            "risk_bullet_default_off" => "- runtime 饰品/探员/贴纸/挂坠对齐仍保持默认关闭。",
            "risk_bullet_public" => "- 不要在公网或真人可控制/可观察 bot 的环境暴露模拟饰品，除非你接受该风险。",
            "type_to_unlock" => "输入固定短语解锁：",
            "phrase_required" => "短语不匹配时不会启用导出。",
            "enable_risky_export" => "启用高风险导出",
            "risk_confirmed_log" => "饰品导出风险已确认",
            "lang_system" => "系统语言",
            "theme_system" => "系统主题",
            "theme_dark" => "深色",
            "theme_light" => "浅色",
            "minimize_window" => "最小化",
            "maximize_window" => "最大化/还原",
            "close_window" => "关闭",
            "status_idle" => "Idle",
            "status_parsing" => "Parsing",
            "status_parsed" => "Parsed",
            "status_converting" => "Converting",
            "status_complete" => "Complete",
            "status_error" => "Error",
            _ => "",
        },
        UiLanguage::En => match key {
            "demo" => "DEMO",
            "output" => "Output",
            "browse" => "Browse",
            "folder" => "Folder",
            "analyze" => "Analyze",
            "convert" => "Convert",
            "open_result" => "Open output",
            "rounds" => "Rounds",
            "round" => "Round",
            "status" => "Status",
            "time" => "Time",
            "rows" => "Rows",
            "files" => "Files",
            "notes" => "Notes",
            "map" => "Map",
            "tick" => "Tick",
            "recommended" => "Recommended",
            "suspicious" => "Suspicious",
            "include_suspicious" => "Include suspicious",
            "enable_suspicious_hint" => "Enable include suspicious first",
            "no_demo" => "No demo",
            "no_demo_hint" => "Choose a demo and analyze",
            "no_output" => "No output",
            "no_output_hint" => "Results appear after conversion",
            "conversion_complete" => "Conversion complete",
            "size" => "Size",
            "players" => "Players",
            "team_group_hint" => "Grouped by first exported round, not fixed T/CT side",
            "team_1" => "Team 1",
            "team_2" => "Team 2",
            "team_other" => "Other",
            "round_short" => "r",
            "file_short" => "f",
            "validated" => "Validated",
            "voice" => "Voice",
            "none" => "none",
            "cosmetics_exported" => "Cosmetic metadata exported",
            "no_players" => "No player files exported",
            "root" => "Root",
            "manifest" => "Manifest",
            "cs2_console" => "CS2 console",
            "open_output" => "Open output folder",
            "copy_command" => "Copy command",
            "copy_round_command" => "Copy round",
            "copy_seq_command" => "Copy seq",
            "copy_risky_round_command" => "Copy cosmetic round",
            "copy_risky_seq_command" => "Copy cosmetic seq",
            "copy_manifest" => "Copy manifest",
            "copied_command" => "Copied CS2 command",
            "copied_round_command" => "Copied round command",
            "copied_seq_command" => "Copied seq command",
            "copied_risky_command" => "Copied risky cosmetic command",
            "copied_manifest" => "Copied manifest path",
            "risky_runtime_command" => "Cosmetic runtime command",
            "risky_runtime_command_body" => "This enables dtr_cosmetics on the server; assess GSLT risk before using it.",
            "advanced" => "Advanced Options",
            "activity" => "Activity",
            "no_activity" => "No activity",
            "side" => "Side",
            "side_both" => "Both",
            "full_round" => "Full round",
            "freeze_preroll" => "freeze pre-roll",
            "export_voice" => "Export voice if present",
            "export_voice_hint" => "Writes demo-backed in-game voice sidecars when the demo contains usable voice data.",
            "export_cosmetics" => "Export cosmetics",
            "cosmetic_details" => "Cosmetic details",
            "export_stickers" => "Export stickers",
            "export_charms" => "Export charms",
            "risk_confirmed" => "risk confirmed",
            "confirmation_required" => "confirmation required",
            "high_risk_option" => "High-risk option",
            "high_risk_option_body" => "Writes demo evidence only; assess GSLT risk before enabling runtime cosmetic/agent/sticker/charm alignment.",
            "output_exists" => "Output already exists",
            "output_exists_body" => "The target demo output directory already exists.",
            "clear_and_convert" => "Clear and convert",
            "cancel" => "Cancel",
            "cosmetic_confirmation" => "Cosmetic export confirmation",
            "high_risk_title" => "HIGH RISK: GSLT / cosmetic export",
            "risk_intro" => "This only writes demo-observed weapon, knife, glove, agent, sticker, and charm evidence into the manifest; risk comes from later runtime cosmetic/agent/sticker/charm alignment.",
            "before_enable" => "Before enabling this, confirm:",
            "risk_bullet_guidelines" => "- You have assessed Valve server guideline and GSLT risk.",
            "risk_bullet_default_off" => "- Runtime cosmetic/agent/sticker/charm alignment stays default-off.",
            "risk_bullet_public" => "- Do not expose simulated cosmetics to public or human-controlled bot usage unless you accept that risk.",
            "type_to_unlock" => "Type exactly to unlock export:",
            "phrase_required" => "Export remains disabled until the phrase matches.",
            "enable_risky_export" => "Enable risky export",
            "risk_confirmed_log" => "Cosmetic export risk confirmed",
            "lang_system" => "System language",
            "theme_system" => "System theme",
            "theme_dark" => "Dark",
            "theme_light" => "Light",
            "minimize_window" => "Minimize",
            "maximize_window" => "Maximize/restore",
            "close_window" => "Close",
            "status_idle" => "Idle",
            "status_parsing" => "Parsing",
            "status_parsed" => "Parsed",
            "status_converting" => "Converting",
            "status_complete" => "Complete",
            "status_error" => "Error",
            _ => "",
        },
    }
}

fn default_round_selection(analysis: &DemoAnalysis) -> BTreeMap<u32, bool> {
    analysis
        .rounds
        .iter()
        .map(|round| (round.round, round.status == RoundStatus::Recommended))
        .collect()
}

fn cosmetic_confirmation_matches(input: &str) -> bool {
    input.trim() == COSMETIC_CONFIRMATION_PHRASE
}

fn cosmetic_export_ready(settings: &GuiSettings, acknowledged: bool) -> bool {
    !settings.export_cosmetics || acknowledged
}

fn metric_chip(ui: &mut egui::Ui, label: &str, value: &str, accent: Color32) {
    egui::Frame::new()
        .fill(panel_deep_color(ui))
        .stroke(egui::Stroke::new(1.0, border_color(ui)))
        .inner_margin(egui::Margin::symmetric(8, 5))
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                ui.label(RichText::new(label).color(ui.visuals().weak_text_color()));
                ui.label(RichText::new(value).strong().color(accent));
            });
        });
}

fn summary_tile(ui: &mut egui::Ui, label: &str, value: &str, accent: Color32) {
    egui::Frame::new()
        .fill(panel_color(ui))
        .stroke(egui::Stroke::new(1.0, border_color(ui)))
        .inner_margin(egui::Margin::symmetric(10, 8))
        .show(ui, |ui| {
            ui.set_min_width(104.0);
            ui.horizontal(|ui| {
                ui.label(RichText::new("|").strong().color(accent));
                ui.label(
                    RichText::new(label)
                        .size(13.0)
                        .color(ui.visuals().weak_text_color()),
                );
            });
            ui.label(
                RichText::new(value)
                    .strong()
                    .size(20.0)
                    .color(ui.visuals().strong_text_color()),
            );
        });
}

fn draw_player_team(ui: &mut egui::Ui, lang: UiLanguage, team: usize, players: &[&PlayerSummary]) {
    let accent = match team {
        1 => Color32::from_rgb(230, 177, 83),
        2 => Color32::from_rgb(106, 176, 220),
        _ => ui.visuals().weak_text_color(),
    };
    egui::Frame::new()
        .fill(panel_deep_color(ui))
        .stroke(egui::Stroke::new(1.0, border_color(ui)))
        .inner_margin(egui::Margin::symmetric(8, 6))
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                ui.label(
                    RichText::new(player_team_label(team, lang))
                        .strong()
                        .color(accent),
                );
                ui.label(
                    RichText::new(player_count_label(players.len(), lang))
                        .color(ui.visuals().weak_text_color()),
                );
            });
            ui.add_space(4.0);
            for player in players {
                ui.horizontal(|ui| {
                    ui.add_sized(
                        [158.0, 20.0],
                        egui::Label::new(
                            RichText::new(player.steam_id.to_string())
                                .font(FontId::monospace(13.0))
                                .strong()
                                .color(ui.visuals().strong_text_color()),
                        ),
                    );
                    ui.label(RichText::new(&player.name).color(ui.visuals().strong_text_color()));
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.label(
                            RichText::new(format!(
                                "{} {} / {} {}",
                                player.rounds,
                                tr(lang, "round_short"),
                                player.files,
                                tr(lang, "file_short")
                            ))
                            .color(ui.visuals().weak_text_color()),
                        );
                    });
                });
            }
        });
}

fn warning_strip(ui: &mut egui::Ui, title: &str, body: &str) {
    egui::Frame::new()
        .fill(Color32::from_rgb(58, 36, 18))
        .stroke(egui::Stroke::new(1.0, WARN))
        .corner_radius(6)
        .inner_margin(egui::Margin::symmetric(10, 8))
        .show(ui, |ui| {
            ui.label(
                RichText::new(title)
                    .strong()
                    .color(Color32::from_rgb(255, 217, 145)),
            );
            ui.label(RichText::new(body).color(Color32::from_rgb(238, 211, 174)));
        });
}

fn empty_panel(ui: &mut egui::Ui, title: &str, body: &str) {
    egui::Frame::new()
        .fill(panel_color(ui))
        .stroke(egui::Stroke::new(1.0, border_color(ui)))
        .inner_margin(egui::Margin::symmetric(12, 10))
        .show(ui, |ui| {
            ui.label(
                RichText::new(title)
                    .strong()
                    .color(ui.visuals().strong_text_color()),
            );
            ui.label(RichText::new(body).color(ui.visuals().weak_text_color()));
        });
}

fn path_block(ui: &mut egui::Ui, label: &str, value: &str) {
    ui.label(
        RichText::new(label)
            .strong()
            .color(ui.visuals().strong_text_color()),
    );
    let mut text = value.to_string();
    ui.add(
        egui::TextEdit::singleline(&mut text)
            .code_editor()
            .interactive(false)
            .desired_width(ui.available_width()),
    );
}

fn table_header_text(ui: &mut egui::Ui, text: &str) {
    ui.label(
        RichText::new(text)
            .strong()
            .size(14.0)
            .color(table_header_color(ui)),
    );
}

fn table_text(ui: &mut egui::Ui, text: impl Into<String>, color: Color32, strong: bool) {
    let mut rich = RichText::new(text.into()).size(15.0).color(color);
    if strong {
        rich = rich.strong();
    }
    ui.label(rich);
}

fn summarize_exported_players(files: &[crate::model::ConvertedFile]) -> Vec<PlayerSummary> {
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

    let mut summaries: Vec<_> = players
        .into_iter()
        .map(|(steam_id, player)| PlayerSummary {
            team: team_index_from_first_side(&player.first_side),
            steam_id,
            name: player.name,
            rounds: player.rounds.len(),
            files: player.files,
        })
        .collect();
    summaries.sort_by_key(|player| (player.team, player.steam_id));
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

fn directory_size_bytes(path: &Path) -> std::io::Result<u64> {
    let metadata = fs::metadata(path)?;
    if metadata.is_file() {
        return Ok(metadata.len());
    }

    let mut total = 0_u64;
    for entry in fs::read_dir(path)? {
        let entry = entry?;
        total += directory_size_bytes(&entry.path()).unwrap_or(0);
    }
    Ok(total)
}

fn format_bytes(bytes: u64) -> String {
    const KB: f64 = 1024.0;
    const MB: f64 = 1024.0 * KB;
    const GB: f64 = 1024.0 * MB;
    let bytes = bytes as f64;
    if bytes >= GB {
        format!("{:.2} GB", bytes / GB)
    } else if bytes >= MB {
        format!("{:.1} MB", bytes / MB)
    } else if bytes >= KB {
        format!("{:.0} KB", bytes / KB)
    } else {
        format!("{bytes:.0} B")
    }
}

fn format_progress_event(event: &ConversionProgress) -> String {
    match event {
        ConversionProgress::AnalysisStarted => "analysis started".to_string(),
        ConversionProgress::AnalysisFinished {
            rounds,
            selected_rounds,
            estimated_files,
        } => format!(
            "analysis rounds={rounds} selected={selected_rounds} estimated_files={estimated_files}"
        ),
        ConversionProgress::RoundSkipped { round, reason } => {
            format!("skip round {round}: {reason}")
        }
        ConversionProgress::RoundStarted {
            round,
            estimated_players,
        } => format!("round {round} players={estimated_players}"),
        ConversionProgress::PlayerSkipped {
            round,
            steam_id,
            reason,
        } => format!("skip round {round} player {steam_id}: {reason}"),
        ConversionProgress::PlayerWritten {
            round,
            steam_id,
            path,
            ticks,
            ..
        } => format!("wrote round {round} player {steam_id} ticks={ticks} path={path}"),
        ConversionProgress::RoundFinished { round, files } => {
            format!("round {round} files={files}")
        }
        ConversionProgress::ArtifactsWritingStarted { root, artifacts } => {
            format!("writing {artifacts} artifacts under {root}")
        }
        ConversionProgress::ArtifactWritten { path, kind } => {
            format!("wrote {:?} {path}", kind)
        }
        ConversionProgress::Finished {
            manifest_path,
            files_written,
            ..
        } => format!("finished files={files_written} manifest={manifest_path}"),
    }
}

fn is_demo_file(path: &Path) -> bool {
    path.is_file()
        && path
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| ext.eq_ignore_ascii_case("dem"))
}

fn console_quote_path(path: &Path) -> String {
    path.display().to_string().replace('"', "\\\"")
}

fn settings_path() -> Option<PathBuf> {
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .map(|root| root.join("CS2 DemoTracer").join("gui-settings.json"))
}

fn load_settings() -> GuiSettings {
    let Some(path) = settings_path() else {
        return GuiSettings::default();
    };
    let Ok(text) = fs::read_to_string(path) else {
        return GuiSettings::default();
    };
    serde_json::from_str(&text).unwrap_or_default()
}

fn save_settings(settings: &GuiSettings) -> std::io::Result<()> {
    let Some(path) = settings_path() else {
        return Ok(());
    };
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let text = serde_json::to_string_pretty(settings).map_err(std::io::Error::other)?;
    fs::write(path, text)
}

fn open_folder_path(path: &Path) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        Command::new("explorer").arg(path).spawn()?;
        return Ok(());
    }

    #[cfg(not(windows))]
    {
        Command::new("xdg-open").arg(path).spawn()?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn progress_reducer_tracks_export_units() {
        let mut progress = GuiProgress::default();
        progress.apply_conversion_event(&ConversionProgress::AnalysisFinished {
            rounds: 3,
            selected_rounds: 2,
            estimated_files: 4,
        });
        assert_eq!(progress.file_units_total, 4);
        assert_eq!(progress.fraction, Some(0.08));

        progress.apply_conversion_event(&ConversionProgress::PlayerWritten {
            round: 1,
            steam_id: 1,
            player_name: "alpha".to_string(),
            side: "t".to_string(),
            path: "round01/t/1_alpha.dtr".to_string(),
            ticks: 64,
            subticks: 0,
        });
        assert_eq!(progress.file_units_done, 1);
        assert!(progress.fraction.unwrap() > 0.1);

        progress.apply_conversion_event(&ConversionProgress::ArtifactsWritingStarted {
            root: "out/demo".to_string(),
            artifacts: 6,
        });
        progress.apply_conversion_event(&ConversionProgress::ArtifactWritten {
            path: "manifest.json".to_string(),
            kind: crate::export::ConversionArtifactKind::Manifest,
        });
        assert_eq!(progress.artifact_units_done, 1);
        assert!(progress.fraction.unwrap() > 0.84);
    }

    #[test]
    fn default_selection_uses_recommended_rounds_only() {
        let analysis = DemoAnalysis {
            demo_path: "demo.dem".to_string(),
            demo_stem: "demo".to_string(),
            map: "de_mirage".to_string(),
            tick_rate: 64.0,
            row_count: 10,
            rounds: vec![
                crate::model::RoundSummary {
                    round: 1,
                    start_tick: 0,
                    end_tick: 64,
                    duration_seconds: 1.0,
                    t_players: 5,
                    ct_players: 5,
                    total_players: 10,
                    valid_rows: 10,
                    status: RoundStatus::Recommended,
                    problems: Vec::new(),
                },
                crate::model::RoundSummary {
                    round: 2,
                    start_tick: 0,
                    end_tick: 64,
                    duration_seconds: 1.0,
                    t_players: 4,
                    ct_players: 5,
                    total_players: 9,
                    valid_rows: 9,
                    status: RoundStatus::Suspicious,
                    problems: vec!["available players 9 != 10".to_string()],
                },
            ],
        };

        let selection = default_round_selection(&analysis);

        assert_eq!(selection.get(&1), Some(&true));
        assert_eq!(selection.get(&2), Some(&false));
    }

    #[test]
    fn cosmetic_confirmation_requires_exact_phrase() {
        assert!(cosmetic_confirmation_matches(COSMETIC_CONFIRMATION_PHRASE));
        assert!(cosmetic_confirmation_matches(&format!(
            "  {COSMETIC_CONFIRMATION_PHRASE}  "
        )));
        assert!(!cosmetic_confirmation_matches(
            "I accept cosmetic export risk"
        ));
    }

    #[test]
    fn cosmetic_export_requires_acknowledgement() {
        let mut settings = GuiSettings::default();
        assert!(cosmetic_export_ready(&settings, false));

        settings.export_cosmetics = true;
        assert!(!cosmetic_export_ready(&settings, false));
        assert!(cosmetic_export_ready(&settings, true));
    }

    #[test]
    fn gui_defaults_keep_system_language_and_theme() {
        let settings = GuiSettings::default();

        assert_eq!(settings.language, LanguageChoice::System);
        assert_eq!(settings.theme, ThemeChoice::System);
        assert!(!settings.advanced_open);
        assert!(!settings.activity_open);
        assert!(settings.export_voice);
        assert!(!settings.export_cosmetics);
        assert!(settings.export_stickers);
        assert!(settings.export_charms);
        assert!(!settings.cosmetics_open);
    }

    #[test]
    fn ui_message_table_covers_initial_languages() {
        assert_eq!(resolve_language(LanguageChoice::ZhCn), UiLanguage::ZhCn);
        assert_eq!(resolve_language(LanguageChoice::En), UiLanguage::En);
        assert_eq!(tr(UiLanguage::ZhCn, "convert"), "转换");
        assert_eq!(tr(UiLanguage::En, "convert"), "Convert");
        assert_eq!(
            theme_choice_label(ThemeChoice::System, UiLanguage::En),
            "System theme"
        );
    }

    #[test]
    fn windows_cjk_font_candidates_prefer_ui_fonts() {
        let mut candidates = Vec::new();
        append_windows_cjk_font_candidates(&mut candidates, Path::new("Fonts"));
        let file_names: Vec<_> = candidates
            .iter()
            .map(|path| {
                path.file_name()
                    .and_then(|name| name.to_str())
                    .unwrap_or_default()
            })
            .collect();

        assert_eq!(file_names[0], "msyh.ttc");
        assert_eq!(file_names[1], "msyhbd.ttc");
        assert!(file_names.contains(&"simsun.ttc"));
    }

    #[test]
    fn output_size_uses_human_units() {
        assert_eq!(format_bytes(0), "0 B");
        assert_eq!(format_bytes(1536), "2 KB");
        assert_eq!(format_bytes(2 * 1024 * 1024 + 512 * 1024), "2.5 MB");
    }

    #[test]
    fn console_commands_are_split_by_mode() {
        let mut result = ConversionResultView {
            root: PathBuf::from("out/demo"),
            manifest_path: PathBuf::from("out/demo/manifest.json"),
            files_written: 0,
            validated: 0,
            output_bytes: 0,
            rounds_exported: 1,
            files_by_round: BTreeMap::new(),
            players: Vec::new(),
            voice_requested: false,
            voice_sidecars: 0,
            cosmetic_files: 0,
            sticker_files: 0,
            charm_files: 0,
        };

        assert_eq!(
            result.console_round_command(Some(7)),
            "dtr_go round \"out/demo/manifest.json\" 7"
        );
        assert_eq!(
            result.console_seq_command(Some(7)),
            "dtr_go seq \"out/demo/manifest.json\" 7"
        );
        assert_eq!(result.console_risky_seq_command(Some(7)), None);

        result.voice_requested = true;
        result.voice_sidecars = 2;
        assert_eq!(
            result.console_seq_command(Some(7)),
            "dtr_voice_auto on; dtr_go seq \"out/demo/manifest.json\" 7"
        );

        result.cosmetic_files = 10;
        assert_eq!(
            result.console_risky_seq_command(Some(7)),
            Some(
                "dtr_voice_auto on; dtr_cosmetics basic; dtr_go seq \"out/demo/manifest.json\" 7"
                    .to_string()
            )
        );
        result.sticker_files = 1;
        assert_eq!(
            result.console_risky_seq_command(Some(7)),
            Some(
                "dtr_voice_auto on; dtr_cosmetics full; dtr_go seq \"out/demo/manifest.json\" 7"
                    .to_string()
            )
        );
    }

    #[test]
    fn exported_players_are_grouped_by_steam_id_and_team() {
        let files = vec![
            converted_file(1, "t", 11, "alpha"),
            converted_file(2, "ct", 11, "alpha"),
            converted_file(1, "ct", 22, "bravo"),
        ];

        let players = summarize_exported_players(&files);

        assert_eq!(players.len(), 2);
        assert_eq!(players[0].team, 1);
        assert_eq!(players[0].steam_id, 11);
        assert_eq!(players[0].rounds, 2);
        assert_eq!(players[0].files, 2);
        assert_eq!(players[1].team, 2);
        assert_eq!(players[1].steam_id, 22);
    }

    fn converted_file(
        round: u32,
        side: &str,
        steam_id: u64,
        player_name: &str,
    ) -> crate::model::ConvertedFile {
        crate::model::ConvertedFile {
            path: format!("round{round:02}/{side}/{steam_id}_{player_name}.dtr"),
            round,
            side: side.to_string(),
            steam_id,
            player_name: player_name.to_string(),
            ticks: 64,
            subticks: 0,
            play_start_tick_index: 0,
            first_weapon_def_index: 0,
            preload_weapon_def_indices: Vec::new(),
            hifi_event_count: 0,
            inventory_snapshot_count: 0,
            loadout: crate::model::ReplayLoadout::default(),
            music_kit_id: None,
            scoreboard_flair: None,
            cosmetics: None,
            view: None,
            scoreboard: None,
        }
    }
}
