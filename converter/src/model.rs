use serde::{Deserialize, Serialize};
use std::fmt;
use std::str::FromStr;

pub const CS2BM_ABI: i32 = 10;
pub const CS2REC_VERSION: u32 = 1;

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Side {
    Both,
    T,
    Ct,
}

impl Side {
    pub fn matches_team(self, team_num: u8) -> bool {
        match self {
            Side::Both => team_num == 2 || team_num == 3,
            Side::T => team_num == 2,
            Side::Ct => team_num == 3,
        }
    }

    pub fn team_dir(team_num: u8) -> &'static str {
        match team_num {
            2 => "t",
            3 => "ct",
            _ => "unknown",
        }
    }
}

impl Default for Side {
    fn default() -> Self {
        Side::Both
    }
}

impl fmt::Display for Side {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Side::Both => f.write_str("both"),
            Side::T => f.write_str("t"),
            Side::Ct => f.write_str("ct"),
        }
    }
}

impl FromStr for Side {
    type Err = String;

    fn from_str(value: &str) -> std::result::Result<Self, Self::Err> {
        match value.to_ascii_lowercase().as_str() {
            "both" | "all" | "两边" | "全部" => Ok(Side::Both),
            "t" | "terrorist" | "terrorists" => Ok(Side::T),
            "ct" | "counter" | "counter-terrorist" | "counter-terrorists" => Ok(Side::Ct),
            _ => Err(format!("unknown side: {value}")),
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct MovementSnapshot {
    pub origin: [f32; 3],
    pub velocity: [f32; 3],
    pub angles: [f32; 3],
    pub entity_flags: u32,
    pub move_type: u8,
    pub buttons: u64,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayTick {
    pub pre: MovementSnapshot,
    pub post: MovementSnapshot,
    pub weapon_def_index: i32,
    pub num_subtick: u32,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct SubtickMove {
    pub when: f32,
    pub button: u32,
    pub pressed: f32,
    pub analog_forward: f32,
    pub analog_left: f32,
    pub pitch_delta: f32,
    pub yaw_delta: f32,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub struct Cs2RecHeader {
    pub version: u32,
    pub tick_rate: f32,
    pub map: String,
    pub round: u32,
    pub side: u8,
    pub steam_id: u64,
    pub player_name: String,
    pub flags: u32,
}

impl Default for Cs2RecHeader {
    fn default() -> Self {
        Self {
            version: CS2REC_VERSION,
            tick_rate: 64.0,
            map: String::new(),
            round: 0,
            side: 0,
            steam_id: 0,
            player_name: String::new(),
            flags: 0,
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct Cs2Rec {
    pub header: Cs2RecHeader,
    pub ticks: Vec<ReplayTick>,
    pub subticks: Vec<SubtickMove>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedDemo {
    pub path: String,
    pub stem: String,
    pub map: String,
    pub tick_rate: f32,
    pub round_freeze_end_ticks: Vec<i32>,
    pub rows: Vec<ParsedPlayerTick>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedPlayerTick {
    pub tick: i32,
    pub steam_id: u64,
    pub name: String,
    pub team_num: u8,
    pub is_alive: bool,
    pub round: u32,
    pub round_in_progress: bool,
    pub is_freeze_period: bool,
    pub game_time: Option<f32>,
    pub origin: [f32; 3],
    pub velocity: [f32; 3],
    pub pitch: f32,
    pub yaw: f32,
    pub buttons: u64,
    pub item_def_idx: i32,
    pub inventory_as_ids: Vec<i32>,
    pub entity_flags: u32,
    pub move_type: u8,
}

impl ParsedPlayerTick {
    pub fn snapshot(&self) -> MovementSnapshot {
        MovementSnapshot {
            origin: self.origin,
            velocity: self.velocity,
            angles: [self.pitch, self.yaw, 0.0],
            entity_flags: self.entity_flags,
            move_type: self.move_type,
            buttons: self.buttons,
        }
    }
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum RoundStatus {
    Recommended,
    Suspicious,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct RoundSummary {
    pub round: u32,
    pub start_tick: i32,
    pub end_tick: i32,
    pub duration_seconds: f32,
    pub t_players: usize,
    pub ct_players: usize,
    pub total_players: usize,
    pub valid_rows: usize,
    pub status: RoundStatus,
    pub problems: Vec<String>,
}

impl RoundSummary {
    pub fn recommended(&self) -> bool {
        self.status == RoundStatus::Recommended
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct DemoAnalysis {
    pub demo_path: String,
    pub demo_stem: String,
    pub map: String,
    pub tick_rate: f32,
    pub row_count: usize,
    pub rounds: Vec<RoundSummary>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConversionManifest {
    pub demo_path: String,
    pub map: String,
    pub tick_rate: f32,
    pub abi: i32,
    pub format_version: u32,
    pub files: Vec<ConvertedFile>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConvertedFile {
    pub path: String,
    pub round: u32,
    pub side: String,
    pub steam_id: u64,
    pub player_name: String,
    pub ticks: usize,
    pub subticks: usize,
    pub first_weapon_def_index: i32,
    pub preload_weapon_def_indices: Vec<i32>,
}
