use crate::analysis::quality::{analyze_demo, AnalysisOptions};
use crate::demo_reader::read_demo;
use crate::export::{
    export_demo, ConversionReport, ConvertOptions, DEFAULT_FREEZE_PREROLL_SECONDS,
};
use crate::model::{DemoAnalysis, ParsedDemo, RoundStatus, Side, SubtickMode};
use crate::{Error, Result};
use eframe::egui;
use std::collections::BTreeSet;
use std::path::PathBuf;
use std::sync::mpsc::{self, Receiver};
use std::thread;

pub fn run_gui() -> Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default().with_inner_size([1180.0, 760.0]),
        ..Default::default()
    };
    eframe::run_native(
        "CS2 DemoTracer",
        options,
        Box::new(|cc| {
            install_cjk_fonts(&cc.egui_ctx);
            Box::<ConverterApp>::default()
        }),
    )
    .map_err(|e| Error::InvalidDemo(e.to_string()))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Lang {
    ZhHans,
    En,
}

enum WorkerResult {
    Analyze(std::result::Result<(ParsedDemo, DemoAnalysis), String>),
    Export(std::result::Result<ConversionReport, String>),
}

struct ConverterApp {
    lang: Lang,
    demo_path: String,
    output_dir: String,
    side: Side,
    include_suspicious: bool,
    cut_before_bomb_plant: bool,
    write_subticks: bool,
    max_round_seconds: f32,
    parsed: Option<ParsedDemo>,
    analysis: Option<DemoAnalysis>,
    selected_rounds: BTreeSet<u32>,
    log: String,
    busy_label: Option<String>,
    worker: Option<Receiver<WorkerResult>>,
}

impl Default for ConverterApp {
    fn default() -> Self {
        Self {
            lang: Lang::ZhHans,
            demo_path: String::new(),
            output_dir: "output".to_string(),
            side: Side::Both,
            include_suspicious: false,
            cut_before_bomb_plant: true,
            write_subticks: true,
            max_round_seconds: 240.0,
            parsed: None,
            analysis: None,
            selected_rounds: BTreeSet::new(),
            log: String::new(),
            busy_label: None,
            worker: None,
        }
    }
}

impl eframe::App for ConverterApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll_worker();

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.heading(self.t("CS2 DemoTracer", "CS2 DemoTracer 转换器"));
            ui.separator();
            ui.horizontal(|ui| {
                ui.selectable_value(&mut self.lang, Lang::ZhHans, "简体中文");
                ui.selectable_value(&mut self.lang, Lang::En, "English");
            });
            ui.add_space(8.0);

            ui.horizontal(|ui| {
                ui.label(self.t("Demo", "Demo 文件"));
                ui.text_edit_singleline(&mut self.demo_path);
                if ui.button(self.t("Browse", "选择")).clicked() {
                    if let Some(path) = rfd::FileDialog::new()
                        .add_filter("CS2 demo", &["dem"])
                        .pick_file()
                    {
                        self.demo_path = path.display().to_string();
                    }
                }
            });
            ui.horizontal(|ui| {
                ui.label(self.t("Output", "输出目录"));
                ui.text_edit_singleline(&mut self.output_dir);
                if ui.button(self.t("Browse", "选择")).clicked() {
                    if let Some(path) = rfd::FileDialog::new().pick_folder() {
                        self.output_dir = path.display().to_string();
                    }
                }
            });

            ui.horizontal(|ui| {
                let side_label = self.t("Side", "阵营");
                let both_label = self.t("Both", "两边");
                egui::ComboBox::from_label(side_label)
                    .selected_text(match self.side {
                        Side::Both => both_label,
                        Side::T => "T",
                        Side::Ct => "CT",
                    })
                    .show_ui(ui, |ui| {
                        ui.selectable_value(&mut self.side, Side::Both, both_label);
                        ui.selectable_value(&mut self.side, Side::T, "T");
                        ui.selectable_value(&mut self.side, Side::Ct, "CT");
                    });
                let suspicious_label = self.t("Allow suspicious rounds", "允许导出可疑回合");
                ui.checkbox(&mut self.include_suspicious, suspicious_label);
                let cut_label = self.t("Cut before C4 plant", "C4 安放前截断");
                ui.checkbox(&mut self.cut_before_bomb_plant, cut_label);
                let subtick_label = self.t("Write subtick input", "写入 subtick 输入");
                ui.checkbox(&mut self.write_subticks, subtick_label);
                ui.label(self.t("Max round seconds", "最大回合秒数"));
                ui.add(
                    egui::DragValue::new(&mut self.max_round_seconds)
                        .clamp_range(20.0..=600.0)
                        .speed(1.0),
                );
            });

            ui.horizontal(|ui| {
                let busy = self.worker.is_some();
                if ui
                    .add_enabled(!busy, egui::Button::new(self.t("Analyze", "分析回合")))
                    .clicked()
                {
                    self.start_analyze();
                }
                if ui
                    .add_enabled(
                        !busy && self.parsed.is_some() && self.analysis.is_some(),
                        egui::Button::new(self.t("Export selected", "导出勾选回合")),
                    )
                    .clicked()
                {
                    self.start_export();
                }
                if let Some(label) = &self.busy_label {
                    ui.spinner();
                    ui.label(label);
                }
            });

            ui.separator();
            if let Some(analysis) = self.analysis.clone() {
                ui.label(format!(
                    "{} {} | tickrate {:.1} | rows {} | rounds {}",
                    self.t("Map", "地图"),
                    analysis.map,
                    analysis.tick_rate,
                    analysis.row_count,
                    analysis.rounds.len()
                ));
                egui::ScrollArea::vertical()
                    .max_height(420.0)
                    .show(ui, |ui| {
                        egui::Grid::new("rounds")
                            .striped(true)
                            .num_columns(8)
                            .show(ui, |ui| {
                                ui.label(self.t("Export", "导出"));
                                ui.label(self.t("Round", "回合"));
                                ui.label(self.t("Status", "状态"));
                                ui.label("T");
                                ui.label("CT");
                                ui.label(self.t("Seconds", "时长"));
                                ui.label(self.t("Rows", "有效行"));
                                ui.label(self.t("Problems", "问题"));
                                ui.end_row();

                                for round in &analysis.rounds {
                                    let mut selected = self.selected_rounds.contains(&round.round);
                                    if ui.checkbox(&mut selected, "").changed() {
                                        if selected {
                                            self.selected_rounds.insert(round.round);
                                        } else {
                                            self.selected_rounds.remove(&round.round);
                                        }
                                    }
                                    ui.label(round.round.to_string());
                                    let status = match round.status {
                                        RoundStatus::Recommended => self.t("Recommended", "推荐"),
                                        RoundStatus::Suspicious => self.t("Suspicious", "可疑"),
                                    };
                                    ui.label(status);
                                    ui.label(round.t_players.to_string());
                                    ui.label(round.ct_players.to_string());
                                    ui.label(format!("{:.1}", round.duration_seconds));
                                    ui.label(round.valid_rows.to_string());
                                    ui.label(if round.problems.is_empty() {
                                        self.t("None", "无").to_string()
                                    } else {
                                        round.problems.join("; ")
                                    });
                                    ui.end_row();
                                }
                            });
                    });
            }

            ui.separator();
            ui.label(self.t("Log", "日志"));
            egui::ScrollArea::vertical().show(ui, |ui| {
                ui.add(
                    egui::TextEdit::multiline(&mut self.log)
                        .desired_rows(8)
                        .desired_width(f32::INFINITY),
                );
            });
        });
    }
}

impl ConverterApp {
    fn t<'a>(&self, en: &'a str, zh: &'a str) -> &'a str {
        match self.lang {
            Lang::ZhHans => zh,
            Lang::En => en,
        }
    }

    fn start_analyze(&mut self) {
        let demo = PathBuf::from(self.demo_path.trim());
        let max_round_seconds = self.max_round_seconds;
        let (tx, rx) = mpsc::channel();
        self.worker = Some(rx);
        self.busy_label = Some(self.t("Analyzing demo...", "正在分析 demo...").to_string());
        self.log_line(format!("{} {}", self.t("Analyze", "分析"), demo.display()));
        thread::spawn(move || {
            let result = read_demo(&demo)
                .map(|parsed| {
                    let analysis = analyze_demo(
                        &parsed,
                        AnalysisOptions {
                            max_round_seconds,
                            ..AnalysisOptions::default()
                        },
                    );
                    (parsed, analysis)
                })
                .map_err(|e| e.to_string());
            let _ = tx.send(WorkerResult::Analyze(result));
        });
    }

    fn start_export(&mut self) {
        let Some(parsed) = self.parsed.clone() else {
            return;
        };
        let options = ConvertOptions {
            output_dir: PathBuf::from(self.output_dir.trim()),
            output_stem: None,
            side: self.side,
            selected_rounds: Some(self.selected_rounds.clone()),
            include_suspicious: self.include_suspicious,
            cut_before_bomb_plant: self.cut_before_bomb_plant,
            subtick_mode: if self.write_subticks {
                SubtickMode::Auto
            } else {
                SubtickMode::Off
            },
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_cosmetics: false,
            export_stickers: false,
            analysis: AnalysisOptions {
                max_round_seconds: self.max_round_seconds,
                ..AnalysisOptions::default()
            },
        };
        let (tx, rx) = mpsc::channel();
        self.worker = Some(rx);
        self.busy_label = Some(self.t("Exporting .dtr...", "正在导出 .dtr...").to_string());
        thread::spawn(move || {
            let result = export_demo(&parsed, &options).map_err(|e| e.to_string());
            let _ = tx.send(WorkerResult::Export(result));
        });
    }

    fn poll_worker(&mut self) {
        let Some(rx) = &self.worker else {
            return;
        };
        let Ok(result) = rx.try_recv() else {
            return;
        };
        self.worker = None;
        self.busy_label = None;
        match result {
            WorkerResult::Analyze(Ok((parsed, analysis))) => {
                self.selected_rounds = analysis
                    .rounds
                    .iter()
                    .filter(|round| round.recommended())
                    .map(|round| round.round)
                    .collect();
                self.log_line(format!(
                    "{}: {} {}, {} {}",
                    self.t("Analyze complete", "分析完成"),
                    self.t("map", "地图"),
                    analysis.map,
                    self.t("recommended rounds", "推荐回合"),
                    self.selected_rounds.len()
                ));
                self.parsed = Some(parsed);
                self.analysis = Some(analysis);
            }
            WorkerResult::Analyze(Err(err)) => {
                self.log_line(format!("{}: {err}", self.t("Analyze failed", "分析失败")));
            }
            WorkerResult::Export(Ok(report)) => {
                self.log_line(format!(
                    "{}: {} {}",
                    self.t("Export complete", "导出完成"),
                    report.files_written,
                    report.root.display()
                ));
            }
            WorkerResult::Export(Err(err)) => {
                self.log_line(format!("{}: {err}", self.t("Export failed", "导出失败")));
            }
        }
    }

    fn log_line(&mut self, line: String) {
        if !self.log.is_empty() {
            self.log.push('\n');
        }
        self.log.push_str(&line);
    }
}

fn install_cjk_fonts(ctx: &egui::Context) {
    let candidates = [
        r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\msyh.ttf",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\simsun.ttc",
    ];
    let Some(bytes) = candidates.iter().find_map(|path| std::fs::read(path).ok()) else {
        return;
    };

    let mut fonts = egui::FontDefinitions::default();
    fonts
        .font_data
        .insert("windows_cjk".to_string(), egui::FontData::from_owned(bytes));
    for family in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
        fonts
            .families
            .entry(family)
            .or_default()
            .insert(0, "windows_cjk".to_string());
    }
    ctx.set_fonts(fonts);
}
