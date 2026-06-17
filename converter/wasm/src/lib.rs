use cs2_demotracer::demo_reader::read_demo_bytes;
use cs2_demotracer::export::{export_demo_to_memory, ConvertMemoryOptions, MemoryConversionReport};
use cs2_demotracer::model::{Side, SubtickMode};
use cs2_demotracer::quality::AnalysisOptions;
use serde::Deserialize;
use std::collections::BTreeSet;
use std::str::FromStr;
use wasm_bindgen::prelude::*;

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct JsConvertOptions {
    side: Option<String>,
    rounds: Option<Vec<u32>>,
    include_suspicious: Option<bool>,
    full_round: Option<bool>,
    subticks: Option<String>,
    max_round_seconds: Option<f32>,
}

#[wasm_bindgen]
pub fn analyze_demo(bytes: Vec<u8>, file_name: String) -> Result<String, JsValue> {
    let parsed = parse_demo(bytes, &file_name)?;
    let analysis =
        cs2_demotracer::browser_analysis::analyze_browser_demo(&parsed, AnalysisOptions::default());
    serde_json::to_string(&analysis).map_err(js_error)
}

#[wasm_bindgen]
pub fn convert_demo(
    bytes: Vec<u8>,
    file_name: String,
    options: JsValue,
) -> Result<WasmConversion, JsValue> {
    let options = parse_convert_options(options)?;
    let parsed = parse_demo(bytes, &file_name)?;
    let report = export_demo_to_memory(&parsed, &options).map_err(js_error)?;
    WasmConversion::new(report)
}

#[wasm_bindgen]
pub struct WasmConversion {
    demo_id: String,
    manifest_json: String,
    log: String,
    files_written: usize,
    paths: Vec<String>,
    bytes: Vec<Vec<u8>>,
}

#[wasm_bindgen]
impl WasmConversion {
    pub fn demo_id(&self) -> String {
        self.demo_id.clone()
    }

    pub fn manifest_json(&self) -> String {
        self.manifest_json.clone()
    }

    pub fn log(&self) -> String {
        self.log.clone()
    }

    pub fn dtr_file_count(&self) -> usize {
        self.files_written
    }

    pub fn file_count(&self) -> usize {
        self.paths.len()
    }

    pub fn file_path(&self, index: usize) -> Result<String, JsValue> {
        self.paths
            .get(index)
            .cloned()
            .ok_or_else(|| JsValue::from_str("file index out of range"))
    }

    pub fn file_bytes(&self, index: usize) -> Result<Vec<u8>, JsValue> {
        self.bytes
            .get(index)
            .cloned()
            .ok_or_else(|| JsValue::from_str("file index out of range"))
    }
}

impl WasmConversion {
    fn new(report: MemoryConversionReport) -> Result<Self, JsValue> {
        let manifest_json = serde_json::to_string_pretty(&report.manifest).map_err(js_error)?;
        let mut paths = Vec::with_capacity(report.artifacts.len());
        let mut bytes = Vec::with_capacity(report.artifacts.len());
        for artifact in report.artifacts {
            paths.push(format!("{}/{}", report.demo_id, artifact.path));
            bytes.push(artifact.bytes);
        }
        Ok(Self {
            demo_id: report.demo_id,
            manifest_json,
            log: report.log,
            files_written: report.files_written,
            paths,
            bytes,
        })
    }
}

fn parse_demo(
    bytes: Vec<u8>,
    file_name: &str,
) -> Result<cs2_demotracer::model::ParsedDemo, JsValue> {
    let stem = demo_stem(file_name);
    read_demo_bytes(&bytes, &stem, file_name).map_err(js_error)
}

fn parse_convert_options(value: JsValue) -> Result<ConvertMemoryOptions, JsValue> {
    let raw = if value.is_undefined() || value.is_null() {
        JsConvertOptions::default()
    } else {
        serde_wasm_bindgen::from_value(value).map_err(js_error)?
    };
    let side = raw
        .side
        .as_deref()
        .map(Side::from_str)
        .transpose()
        .map_err(js_error)?
        .unwrap_or(Side::Both);
    let subtick_mode = raw
        .subticks
        .as_deref()
        .map(SubtickMode::from_str)
        .transpose()
        .map_err(js_error)?
        .unwrap_or(SubtickMode::Auto);
    let selected_rounds = raw.rounds.map(BTreeSet::from_iter);

    Ok(ConvertMemoryOptions {
        output_stem: None,
        side,
        selected_rounds,
        include_suspicious: raw.include_suspicious.unwrap_or(false),
        cut_before_bomb_plant: !raw.full_round.unwrap_or(false),
        subtick_mode,
        analysis: AnalysisOptions {
            max_round_seconds: raw.max_round_seconds.unwrap_or(240.0),
            ..AnalysisOptions::default()
        },
    })
}

fn demo_stem(file_name: &str) -> String {
    let normalized = file_name.replace('\\', "/");
    let file = normalized.rsplit('/').next().unwrap_or("demo");
    file.rsplit_once('.')
        .map(|(stem, _)| stem)
        .filter(|stem| !stem.is_empty())
        .unwrap_or("demo")
        .to_string()
}

fn js_error(err: impl std::fmt::Display) -> JsValue {
    JsValue::from_str(&err.to_string())
}
