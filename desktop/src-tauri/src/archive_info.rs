use cs2_demotracer::browser_analysis::{
    BrowserDemoAnalysis, BrowserDemoSource, BrowserPlayerSummary, BrowserScoreSummary,
};
use cs2_demotracer::demo_reader::is_supported_demo_path;
use cs2_demotracer::model::ParsedDemo;
use serde::{Deserialize, Serialize};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

pub(crate) const DEMO_INFO_FILE_NAME: &str = "demo-info.json";
pub(crate) const DEMO_SOURCE_FILE_NAME: &str = "demo-source.json";
pub(crate) const DEMO_INFO_SCHEMA_VERSION: u32 = 1;
pub(crate) const DEMO_INFO_ANALYSIS_REVISION: u32 = 2;
const MAX_DEMO_INFO_BYTES: u64 = 1024 * 1024;
const MAX_DEMO_SOURCE_BYTES: u64 = 64 * 1024;

static NEXT_INFO_NONCE: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DemoArchiveInfo {
    pub schema_version: u32,
    pub analysis_revision: u32,
    pub demo_id: String,
    pub demo_sha256: String,
    pub display_name: String,
    pub source_file_name: String,
    /// Absolute path used only by the local desktop archive. The portable
    /// manifest intentionally keeps its sanitized basename.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_file_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_file_modified_at_ms: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_file_size_bytes: Option<u64>,
    pub source_file_date_is_approximate: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub played_at: Option<String>,
    pub map: String,
    pub tick_rate: f32,
    pub duration_seconds: f32,
    pub duration_evidence: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub demo_patch_version: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub demo_version_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub server_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub demo_source: Option<BrowserDemoSource>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub score: Option<BrowserScoreSummary>,
    pub score_evidence: String,
    pub players: Vec<BrowserPlayerSummary>,
    pub manifest_abi: i32,
    pub dtr_format_version: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub conversion: Option<DemoInfoConversion>,
    pub generated_by: DemoInfoGenerator,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DemoInfoGenerator {
    pub app: String,
    pub version: String,
    pub reason: String,
    pub generated_at_ms: u64,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DemoInfoConversion {
    pub converter_version: String,
    pub selected_rounds: Vec<u32>,
    pub side: String,
    pub full_round: bool,
    pub include_suspicious: bool,
    pub freeze_preroll_seconds: f32,
    pub voice: bool,
    pub cosmetics: bool,
    pub stickers: bool,
    pub charms: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub manifest_sha256: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub manifest_bytes: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub round_count: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub file_count: Option<usize>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct DemoSourcePointer {
    schema_version: u32,
    demo_sha256: String,
    source_file_path: String,
    source_file_name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    source_file_modified_at_ms: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    source_file_size_bytes: Option<u64>,
    updated_at_ms: u64,
}

pub(crate) enum DemoInfoRead {
    Current(Box<DemoArchiveInfo>),
    Missing,
    Stale,
    Invalid,
}

impl DemoArchiveInfo {
    pub(crate) fn from_analysis(
        demo_id: impl Into<String>,
        parsed: &ParsedDemo,
        browser: &BrowserDemoAnalysis,
        source_file_modified_at_ms: Option<u64>,
        source_file_size_bytes: Option<u64>,
        manifest_abi: i32,
        dtr_format_version: u32,
        reason: &str,
    ) -> Self {
        Self {
            schema_version: DEMO_INFO_SCHEMA_VERSION,
            analysis_revision: DEMO_INFO_ANALYSIS_REVISION,
            demo_id: demo_id.into(),
            demo_sha256: parsed.demo_sha256.clone(),
            display_name: archive_display_name(parsed, browser),
            source_file_name: Path::new(&parsed.path)
                .file_name()
                .map(|name| name.to_string_lossy().into_owned())
                .unwrap_or_else(|| format!("{}.dem", parsed.stem)),
            source_file_path: local_source_file_path(&parsed.path),
            source_file_modified_at_ms,
            source_file_size_bytes,
            source_file_date_is_approximate: source_file_modified_at_ms.is_some(),
            played_at: None,
            map: parsed.map.clone(),
            tick_rate: parsed.tick_rate,
            duration_seconds: browser.duration_seconds,
            duration_evidence: if parsed.playback_time_seconds.is_some() {
                "demoFileInfo".to_string()
            } else {
                "observedTicks".to_string()
            },
            demo_patch_version: browser.demo_patch_version,
            demo_version_name: browser.demo_version_name.clone(),
            // Raw server names may contain private host/IP details. Persist only
            // the normalized source label and its evidence in portable archives.
            server_name: None,
            demo_source: browser.demo_source.clone(),
            score: browser.score.clone(),
            score_evidence: if browser.score.is_some() {
                "roundEndEvents".to_string()
            } else {
                "unavailable".to_string()
            },
            players: browser.players.clone(),
            manifest_abi,
            dtr_format_version,
            conversion: None,
            generated_by: DemoInfoGenerator {
                app: "CS2 DemoTracer".to_string(),
                version: env!("CARGO_PKG_VERSION").to_string(),
                reason: reason.to_string(),
                generated_at_ms: unix_time_ms(SystemTime::now()),
            },
        }
    }
}

pub(crate) fn archive_display_name(parsed: &ParsedDemo, browser: &BrowserDemoAnalysis) -> String {
    let team_a = browser
        .score
        .as_ref()
        .and_then(|score| clean_display_name(score.team_a.name.as_deref()));
    let team_b = browser
        .score
        .as_ref()
        .and_then(|score| clean_display_name(score.team_b.name.as_deref()));
    match (team_a, team_b) {
        (Some(team_a), Some(team_b)) if !team_a.eq_ignore_ascii_case(&team_b) => {
            format!("{team_a} vs {team_b}")
        }
        _ => clean_display_name(Some(&parsed.stem)).unwrap_or_else(|| "Demo".to_string()),
    }
}

pub(crate) fn archive_directory_name(parsed: &ParsedDemo, browser: &BrowserDemoAnalysis) -> String {
    archive_directory_name_with_hash_len(parsed, browser, 12)
}

pub(crate) fn archive_directory_name_with_hash_len(
    parsed: &ParsedDemo,
    browser: &BrowserDemoAnalysis,
    hash_len: usize,
) -> String {
    archive_directory_name_from(
        &archive_display_name(parsed, browser),
        &parsed.demo_sha256,
        hash_len,
    )
}

pub(crate) fn archive_directory_name_from_parts(
    display_name: &str,
    demo_sha256: &str,
    hash_len: usize,
) -> String {
    archive_directory_name_from(display_name, demo_sha256, hash_len)
}

fn archive_directory_name_from(display_name: &str, demo_sha256: &str, hash_len: usize) -> String {
    let label = portable_slug(display_name, 64, "demo");
    let hash = demo_sha256.chars().take(hash_len).collect::<String>();
    format!("{label}--{hash}")
}

pub(crate) fn map_directory_name(map: &str) -> String {
    let normalized = map.replace('\\', "/");
    let leaf = normalized
        .rsplit('/')
        .next()
        .unwrap_or(map)
        .trim_end_matches(".vpk")
        .trim_end_matches(".bsp");
    portable_map_slug(leaf)
}

pub(crate) fn demo_info_path(root: &Path) -> PathBuf {
    root.join(DEMO_INFO_FILE_NAME)
}

pub(crate) fn demo_source_path(root: &Path) -> PathBuf {
    root.join(DEMO_SOURCE_FILE_NAME)
}

pub(crate) fn read_demo_source_path(root: &Path, expected_sha256: &str) -> Option<String> {
    let path = demo_source_path(root);
    let metadata = fs::symlink_metadata(&path).ok()?;
    if !metadata.is_file()
        || crate::catalog::is_symlink_or_reparse(&metadata)
        || metadata.len() > MAX_DEMO_SOURCE_BYTES
    {
        return None;
    }
    let pointer = serde_json::from_slice::<DemoSourcePointer>(&fs::read(path).ok()?).ok()?;
    (pointer.schema_version == 1
        && !expected_sha256.trim().is_empty()
        && pointer
            .demo_sha256
            .eq_ignore_ascii_case(expected_sha256.trim())
        && Path::new(&pointer.source_file_path).is_absolute())
    .then_some(pointer.source_file_path)
}

pub(crate) fn write_demo_source_pointer(
    root: &Path,
    demo_sha256: &str,
    source_path: &Path,
) -> io::Result<PathBuf> {
    if demo_sha256.len() != 64
        || !demo_sha256
            .chars()
            .all(|character| character.is_ascii_hexdigit())
        || !source_path.is_absolute()
        || !is_supported_demo_path(source_path)
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "source pointer requires a full demo hash and absolute .dem or .dem.zst path",
        ));
    }
    let metadata = fs::metadata(source_path).ok();
    let pointer = DemoSourcePointer {
        schema_version: 1,
        demo_sha256: demo_sha256.to_ascii_lowercase(),
        source_file_path: source_path.display().to_string(),
        source_file_name: source_path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default(),
        source_file_modified_at_ms: metadata.as_ref().and_then(|value| {
            value
                .modified()
                .ok()
                .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
                .map(|duration| duration.as_millis().min(u64::MAX as u128) as u64)
        }),
        source_file_size_bytes: metadata.as_ref().map(fs::Metadata::len),
        updated_at_ms: unix_time_ms(SystemTime::now()),
    };
    let mut bytes = serde_json::to_vec_pretty(&pointer)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    bytes.push(b'\n');
    write_local_json(
        root,
        DEMO_SOURCE_FILE_NAME,
        "demo-source.json",
        MAX_DEMO_SOURCE_BYTES,
        &bytes,
    )
}

enum DemoInfoFileRead {
    Found(Box<DemoArchiveInfo>),
    Missing,
    Invalid,
}

fn read_demo_info_file(root: &Path) -> DemoInfoFileRead {
    let path = demo_info_path(root);
    let metadata = match fs::symlink_metadata(&path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return DemoInfoFileRead::Missing,
        Err(_) => return DemoInfoFileRead::Invalid,
    };
    if !metadata.file_type().is_file()
        || crate::catalog::is_symlink_or_reparse(&metadata)
        || metadata.len() > MAX_DEMO_INFO_BYTES
    {
        return DemoInfoFileRead::Invalid;
    }
    let text = match fs::read_to_string(&path) {
        Ok(text) => text,
        Err(_) => return DemoInfoFileRead::Invalid,
    };
    let info = match serde_json::from_str::<DemoArchiveInfo>(&text) {
        Ok(info) => info,
        Err(_) => return DemoInfoFileRead::Invalid,
    };
    DemoInfoFileRead::Found(Box::new(info))
}

pub(crate) fn read_demo_info(root: &Path, expected_sha256: &str) -> DemoInfoRead {
    let info = match read_demo_info_file(root) {
        DemoInfoFileRead::Found(info) => info,
        DemoInfoFileRead::Missing => return DemoInfoRead::Missing,
        DemoInfoFileRead::Invalid => return DemoInfoRead::Invalid,
    };
    if info.schema_version != DEMO_INFO_SCHEMA_VERSION
        || info.analysis_revision != DEMO_INFO_ANALYSIS_REVISION
        || expected_sha256.is_empty()
        || !info.demo_sha256.eq_ignore_ascii_case(expected_sha256)
    {
        return DemoInfoRead::Stale;
    }
    DemoInfoRead::Current(info)
}

/// Reads local provenance even when the analysis payload is stale. The full
/// demo hash and sidecar schema must still match, so imported or unrelated
/// sidecars cannot redirect an archive to another demo.
pub(crate) fn read_matching_demo_info(
    root: &Path,
    expected_sha256: &str,
) -> Option<Box<DemoArchiveInfo>> {
    let DemoInfoFileRead::Found(info) = read_demo_info_file(root) else {
        return None;
    };
    (info.schema_version == DEMO_INFO_SCHEMA_VERSION
        && !expected_sha256.trim().is_empty()
        && info
            .demo_sha256
            .eq_ignore_ascii_case(expected_sha256.trim()))
    .then_some(info)
}

pub(crate) fn write_demo_info(root: &Path, info: &DemoArchiveInfo) -> io::Result<PathBuf> {
    let mut bytes = serde_json::to_vec_pretty(info)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    bytes.push(b'\n');
    write_local_json(
        root,
        DEMO_INFO_FILE_NAME,
        "demo-info.json",
        MAX_DEMO_INFO_BYTES,
        &bytes,
    )
}

fn write_local_json(
    root: &Path,
    file_name: &str,
    display_name: &str,
    max_bytes: u64,
    bytes: &[u8],
) -> io::Result<PathBuf> {
    let root_metadata = fs::symlink_metadata(root)?;
    if !root_metadata.is_dir() || crate::catalog::is_symlink_or_reparse(&root_metadata) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "archive root is not a normal folder",
        ));
    }
    if bytes.len() as u64 > max_bytes {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("{display_name} exceeds the metadata size limit"),
        ));
    }
    let target = root.join(file_name);
    match fs::symlink_metadata(&target) {
        Ok(metadata) => {
            if !metadata.is_file() || crate::catalog::is_symlink_or_reparse(&metadata) {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    format!("{display_name} is not a normal file"),
                ));
            }
            if metadata.len() == bytes.len() as u64 {
                let mut file = File::open(&target)?;
                let mut current = vec![0_u8; bytes.len()];
                if file.read_exact(&mut current).is_ok() {
                    let mut trailing = [0_u8; 1];
                    if current == bytes && file.read(&mut trailing)? == 0 {
                        return Ok(target);
                    }
                }
            }
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {}
        Err(error) => return Err(error),
    }

    let nonce = NEXT_INFO_NONCE.fetch_add(1, Ordering::Relaxed);
    let temp = root.join(format!(".{file_name}.tmp.{}.{}", std::process::id(), nonce));
    let backup = root.join(format!(
        ".{file_name}.backup.{}.{}",
        std::process::id(),
        nonce
    ));
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temp)?;
    if let Err(error) = file.write_all(&bytes).and_then(|_| file.sync_all()) {
        let _ = fs::remove_file(&temp);
        return Err(error);
    }
    drop(file);

    match fs::symlink_metadata(&target) {
        Ok(metadata) => {
            if !metadata.is_file() || crate::catalog::is_symlink_or_reparse(&metadata) {
                let _ = fs::remove_file(&temp);
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    format!("{display_name} is not a normal file"),
                ));
            }
            if let Err(error) = fs::rename(&target, &backup) {
                let _ = fs::remove_file(&temp);
                return Err(error);
            }
            if let Err(error) = fs::rename(&temp, &target) {
                let _ = fs::rename(&backup, &target);
                let _ = fs::remove_file(&temp);
                return Err(error);
            }
            let _ = fs::remove_file(&backup);
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            if let Err(error) = fs::rename(&temp, &target) {
                let _ = fs::remove_file(&temp);
                return Err(error);
            }
        }
        Err(error) => {
            let _ = fs::remove_file(&temp);
            return Err(error);
        }
    }
    Ok(target)
}

fn clean_display_name(value: Option<&str>) -> Option<String> {
    let value = value?.trim();
    if value.is_empty() {
        return None;
    }
    let normalized = value
        .chars()
        .filter(|character| character.is_alphanumeric())
        .flat_map(char::to_lowercase)
        .collect::<String>();
    if matches!(
        normalized.as_str(),
        "t" | "ct" | "terrorist" | "terrorists" | "counterterrorist" | "counterterrorists"
    ) {
        None
    } else {
        Some(value.to_string())
    }
}

fn portable_slug(value: &str, max_chars: usize, fallback: &str) -> String {
    let mut slug = String::new();
    let mut separator = false;
    for character in value.chars() {
        if character.is_ascii_alphanumeric() {
            if separator && !slug.is_empty() && slug.len() < max_chars {
                slug.push('-');
            }
            separator = false;
            if slug.len() < max_chars {
                slug.push(character.to_ascii_lowercase());
            }
        } else {
            separator = true;
        }
        if slug.len() >= max_chars {
            break;
        }
    }
    while slug.ends_with('-') {
        slug.pop();
    }
    if slug.is_empty() {
        fallback.to_string()
    } else {
        slug
    }
}

fn portable_map_slug(value: &str) -> String {
    let mut slug = String::new();
    let mut separator = false;
    for character in value.chars() {
        if character.is_ascii_alphanumeric() || matches!(character, '-' | '_') {
            if separator && !slug.is_empty() {
                slug.push('-');
            }
            separator = false;
            if slug.len() < 48 {
                slug.push(character.to_ascii_lowercase());
            }
        } else {
            separator = true;
        }
        if slug.len() >= 48 {
            break;
        }
    }
    let slug = slug.trim_matches(['-', '_']).to_string();
    if slug.is_empty() {
        "unknown-map".to_string()
    } else if is_windows_reserved_segment(&slug) {
        format!("map-{slug}")
    } else {
        slug
    }
}

fn is_windows_reserved_segment(value: &str) -> bool {
    let stem = value
        .split('.')
        .next()
        .unwrap_or(value)
        .to_ascii_lowercase();
    matches!(stem.as_str(), "con" | "prn" | "aux" | "nul")
        || stem
            .strip_prefix("com")
            .or_else(|| stem.strip_prefix("lpt"))
            .is_some_and(|suffix| {
                suffix.len() == 1 && matches!(suffix.as_bytes().first(), Some(b'1'..=b'9'))
            })
}

fn unix_time_ms(time: SystemTime) -> u64 {
    time.duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| u64::try_from(duration.as_millis()).ok())
        .unwrap_or_default()
}

fn local_source_file_path(value: &str) -> Option<String> {
    let value = value.trim();
    let path = Path::new(value);
    if value.is_empty() || !path.is_absolute() {
        None
    } else {
        Some(path.display().to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use cs2_demotracer::model::DemoAnalysis;

    fn test_directory(label: &str) -> PathBuf {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "cs2-demotracer-archive-info-{label}-{}-{unique}",
            std::process::id()
        ));
        fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn readable_directory_keeps_hash_identity() {
        assert_eq!(
            archive_directory_name_from(
                "PARIVISION vs Team Spirit",
                &"abcdef1234567890".repeat(4),
                12,
            ),
            "parivision-vs-team-spirit--abcdef123456"
        );
    }

    #[test]
    fn map_directory_is_one_portable_segment() {
        assert_eq!(map_directory_name("maps/de_mirage.vpk"), "de_mirage");
        assert_eq!(map_directory_name(""), "unknown-map");
        assert_eq!(map_directory_name("CON"), "map-con");
        assert_eq!(map_directory_name("LPT1"), "map-lpt1");
    }

    #[test]
    fn local_source_pointer_requires_an_absolute_path() {
        assert_eq!(local_source_file_path("demo.dem"), None);
        let absolute = if cfg!(windows) {
            r"C:\Demos\match.dem"
        } else {
            "/demos/match.dem"
        };
        assert_eq!(local_source_file_path(absolute).as_deref(), Some(absolute));
    }

    #[test]
    fn analysis_sidecar_remembers_the_original_demo_path() {
        let source = std::env::temp_dir().join("remembered-source.dem");
        let parsed = ParsedDemo {
            path: source.display().to_string(),
            stem: "remembered-source".to_string(),
            map: "de_mirage".to_string(),
            demo_sha256: "aa".repeat(32),
            tick_rate: 64.0,
            ..ParsedDemo::default()
        };
        let browser = BrowserDemoAnalysis {
            analysis: DemoAnalysis {
                demo_path: parsed.path.clone(),
                demo_stem: parsed.stem.clone(),
                map: parsed.map.clone(),
                tick_rate: parsed.tick_rate,
                row_count: 0,
                rounds: Vec::new(),
            },
            duration_seconds: 0.0,
            demo_patch_version: None,
            demo_version_name: None,
            server_name: None,
            demo_source: None,
            players: Vec::new(),
            score: None,
        };

        let info = DemoArchiveInfo::from_analysis(
            "remembered-source-aabbccdd",
            &parsed,
            &browser,
            None,
            None,
            17,
            7,
            "test",
        );

        assert_eq!(info.source_file_path.as_deref(), source.to_str());
    }

    #[test]
    fn source_pointer_round_trips_and_is_bound_to_the_full_hash() {
        let root = test_directory("source-pointer");
        let source = root.join("match.dem.zst");
        fs::write(&source, b"demo").unwrap();
        let hash = "ab".repeat(32);

        write_demo_source_pointer(&root, &hash, &source).unwrap();

        assert_eq!(
            read_demo_source_path(&root, &hash).as_deref(),
            source.to_str()
        );
        assert!(read_demo_source_path(&root, &"cd".repeat(32)).is_none());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_pointer_rejects_relative_and_corrupt_payloads() {
        let root = test_directory("invalid-source-pointer");
        fs::write(
            demo_source_path(&root),
            serde_json::to_vec(&serde_json::json!({
                "schemaVersion": 1,
                "demoSha256": "ab".repeat(32),
                "sourceFilePath": "relative.dem",
                "sourceFileName": "relative.dem",
                "updatedAtMs": 1
            }))
            .unwrap(),
        )
        .unwrap();
        assert!(read_demo_source_path(&root, &"ab".repeat(32)).is_none());

        fs::write(demo_source_path(&root), b"not json").unwrap();
        assert!(read_demo_source_path(&root, &"ab".repeat(32)).is_none());
        fs::remove_dir_all(root).unwrap();
    }
}
