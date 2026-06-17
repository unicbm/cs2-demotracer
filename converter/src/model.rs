use serde::{Deserialize, Serialize};
use std::fmt;
use std::str::FromStr;

pub const DEMOTRACER_ABI: i32 = 11;
pub const DTR_FORMAT_VERSION: u32 = 4;

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

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum SubtickMode {
    #[default]
    Auto,
    Off,
}

impl fmt::Display for SubtickMode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SubtickMode::Auto => f.write_str("auto"),
            SubtickMode::Off => f.write_str("off"),
        }
    }
}

impl FromStr for SubtickMode {
    type Err = String;

    fn from_str(value: &str) -> std::result::Result<Self, Self::Err> {
        match value.to_ascii_lowercase().as_str() {
            "auto" | "on" | "1" | "true" => Ok(SubtickMode::Auto),
            "off" | "0" | "false" => Ok(SubtickMode::Off),
            _ => Err(format!("unknown subtick mode: {value}")),
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
    pub buttons1: u64,
    pub buttons2: u64,
    pub duck_amount: f32,
    pub duck_speed: f32,
    pub ladder_normal: [f32; 3],
    pub ducked: u8,
    pub ducking: u8,
    pub desires_duck: u8,
    pub actual_move_type: u8,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayTick {
    pub pre: MovementSnapshot,
    pub post: MovementSnapshot,
    pub weapon_def_index: i32,
    pub num_subtick: u32,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ProjectileKind {
    #[default]
    Unknown,
    Smoke,
    Flash,
    He,
    Molotov,
    Decoy,
}

impl ProjectileKind {
    pub fn from_grenade_type(value: &str) -> Self {
        let lower = value.to_ascii_lowercase();
        if lower.contains("smoke") {
            Self::Smoke
        } else if lower.contains("flash") {
            Self::Flash
        } else if lower.contains("hegrenade") || lower.contains("he_grenade") {
            Self::He
        } else if lower.contains("molotov")
            || lower.contains("incgrenade")
            || lower.contains("inferno")
        {
            Self::Molotov
        } else if lower.contains("decoy") {
            Self::Decoy
        } else {
            Self::Unknown
        }
    }

    pub fn to_u8(self) -> u8 {
        match self {
            Self::Unknown => 0,
            Self::Smoke => 1,
            Self::Flash => 2,
            Self::He => 3,
            Self::Molotov => 4,
            Self::Decoy => 5,
        }
    }

    pub fn from_u8(value: u8) -> Self {
        match value {
            1 => Self::Smoke,
            2 => Self::Flash,
            3 => Self::He,
            4 => Self::Molotov,
            5 => Self::Decoy,
            _ => Self::Unknown,
        }
    }

    pub fn weapon_def_index(self) -> i32 {
        match self {
            Self::Flash => 43,
            Self::He => 44,
            Self::Smoke => 45,
            Self::Molotov => 46,
            Self::Decoy => 47,
            Self::Unknown => -1,
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct ReplayProjectile {
    pub tick_index: u32,
    pub kind: ProjectileKind,
    pub weapon_def_index: i32,
    pub initial_position: [f32; 3],
    pub initial_velocity: [f32; 3],
    pub detonation_position: [f32; 3],
}

#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Serialize)]
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
            version: DTR_FORMAT_VERSION,
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
    pub projectiles: Vec<ReplayProjectile>,
    pub subticks: Vec<SubtickMove>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedDemo {
    pub path: String,
    pub stem: String,
    pub demo_sha256: String,
    pub map: String,
    pub tick_rate: f32,
    pub round_freeze_end_ticks: Vec<i32>,
    pub bomb_beginplant_ticks: Vec<i32>,
    pub bomb_planted_ticks: Vec<i32>,
    pub rows: Vec<ParsedPlayerTick>,
    pub projectiles: Vec<ParsedProjectile>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ParsedProjectile {
    pub tick: i32,
    pub steam_id: u64,
    pub name: String,
    pub grenade_type: String,
    pub kind: ProjectileKind,
    pub initial_position: [f32; 3],
    pub initial_velocity: [f32; 3],
    pub detonation_position: [f32; 3],
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
    pub buttonstate1: u64,
    pub buttonstate2: u64,
    pub buttonstate3: u64,
    pub item_def_idx: i32,
    pub inventory_as_ids: Vec<i32>,
    pub armor_value: u32,
    pub has_helmet: bool,
    pub has_defuser: bool,
    pub round_start_equip_value: u32,
    pub equipment_value_total: u32,
    pub money_saved_total: u32,
    pub cash_spent_this_round: u32,
    pub entity_flags: u32,
    pub move_type: u8,
    pub duck_amount: Option<f32>,
    pub duck_speed: Option<f32>,
    pub ladder_normal: Option<[f32; 3]>,
    pub ducked: Option<bool>,
    pub ducking: Option<bool>,
    pub desires_duck: Option<bool>,
    pub subtick_moves: Vec<SubtickMove>,
    pub subtick_button_truncated: usize,
}

impl ParsedPlayerTick {
    pub fn snapshot(&self) -> MovementSnapshot {
        const FL_DUCKING: u32 = 1 << 1;
        const IN_DUCK: u64 = 1 << 2;

        let (buttons, buttons1, buttons2) =
            if self.buttonstate1 != 0 || self.buttonstate2 != 0 || self.buttonstate3 != 0 {
                (self.buttonstate1, self.buttonstate2, self.buttonstate3)
            } else {
                (self.buttons, 0, 0)
            };
        let physically_ducked = (self.entity_flags & FL_DUCKING) != 0;
        let wants_duck = ((buttons | buttons1) & IN_DUCK) != 0;
        let ducked = self.ducked.unwrap_or(physically_ducked);
        let ducking = self.ducking.unwrap_or(wants_duck && !physically_ducked);
        let desires_duck = self.desires_duck.unwrap_or(wants_duck);

        MovementSnapshot {
            origin: self.origin,
            velocity: self.velocity,
            angles: [self.pitch, self.yaw, 0.0],
            entity_flags: self.entity_flags,
            move_type: self.move_type,
            buttons,
            buttons1,
            buttons2,
            duck_amount: self
                .duck_amount
                .unwrap_or(if physically_ducked { 1.0 } else { 0.0 }),
            duck_speed: self
                .duck_speed
                .unwrap_or(if physically_ducked || wants_duck {
                    8.0
                } else {
                    0.0
                }),
            ladder_normal: self.ladder_normal.unwrap_or([0.0, 0.0, 0.0]),
            ducked: u8::from(ducked),
            ducking: u8::from(ducking),
            desires_duck: u8::from(desires_duck),
            actual_move_type: self.move_type,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_preserves_real_duck_and_ladder_fields() {
        let row = ParsedPlayerTick {
            duck_amount: Some(0.375),
            duck_speed: Some(-0.0),
            ladder_normal: Some([1.0, -0.0, 0.5]),
            ducked: Some(false),
            ducking: Some(true),
            desires_duck: Some(true),
            ..ParsedPlayerTick::default()
        };

        let snapshot = row.snapshot();

        assert_eq!(snapshot.duck_amount.to_bits(), 0.375_f32.to_bits());
        assert_eq!(snapshot.duck_speed.to_bits(), (-0.0_f32).to_bits());
        assert_eq!(snapshot.ladder_normal[0].to_bits(), 1.0_f32.to_bits());
        assert_eq!(snapshot.ladder_normal[1].to_bits(), (-0.0_f32).to_bits());
        assert_eq!(snapshot.ladder_normal[2].to_bits(), 0.5_f32.to_bits());
        assert_eq!(snapshot.ducked, 0);
        assert_eq!(snapshot.ducking, 1);
        assert_eq!(snapshot.desires_duck, 1);
    }

    #[test]
    fn snapshot_duck_fallback_separates_desire_from_physical_duck() {
        const FL_DUCKING: u32 = 1 << 1;
        const IN_DUCK: u64 = 1 << 2;

        let wanting_duck = ParsedPlayerTick {
            buttons: IN_DUCK,
            ..ParsedPlayerTick::default()
        }
        .snapshot();

        assert_eq!(wanting_duck.duck_amount, 0.0);
        assert_eq!(wanting_duck.ducked, 0);
        assert_eq!(wanting_duck.ducking, 1);
        assert_eq!(wanting_duck.desires_duck, 1);

        let physically_ducked = ParsedPlayerTick {
            entity_flags: FL_DUCKING,
            ..ParsedPlayerTick::default()
        }
        .snapshot();

        assert_eq!(physically_ducked.duck_amount, 1.0);
        assert_eq!(physically_ducked.ducked, 1);
        assert_eq!(physically_ducked.ducking, 0);
        assert_eq!(physically_ducked.desires_duck, 0);
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
    pub demo_id: String,
    pub demo_sha256: String,
    pub map: String,
    pub tick_rate: f32,
    pub abi: i32,
    pub format_version: u32,
    pub rounds: Vec<ConvertedRound>,
    pub files: Vec<ConvertedFile>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ConvertedRound {
    pub round: u32,
    pub start_tick: i32,
    pub end_tick: i32,
    pub original_end_tick: i32,
    pub duration_seconds: f32,
    pub pistol_round: bool,
    pub cut_reason: Option<String>,
    pub t_economy: TeamEconomy,
    pub ct_economy: TeamEconomy,
    pub files: usize,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct TeamEconomy {
    pub side: String,
    pub players: usize,
    pub round_start_equipment_value: u32,
    pub equipment_value_total: u32,
    pub money_saved_total: u32,
    pub cash_spent_this_round: u32,
    pub class: EconomyClass,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum EconomyClass {
    Pistol,
    Eco,
    Force,
    #[default]
    Full,
    Unknown,
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
    #[serde(default)]
    pub loadout: ReplayLoadout,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ReplayLoadout {
    pub weapon_def_indices: Vec<i32>,
    pub armor_value: u32,
    pub has_helmet: bool,
    pub has_defuser: bool,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct RoundPoolManifest {
    pub format_version: u32,
    pub abi: i32,
    pub map: String,
    pub candidates: Vec<RoundPoolCandidate>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct RoundPoolCandidate {
    pub manifest: String,
    pub demo_stem: String,
    pub demo_path: String,
    pub source_round: u32,
    pub pistol_round: bool,
    pub t_economy: TeamEconomy,
    pub ct_economy: TeamEconomy,
    pub duration_seconds: f32,
    pub cut_reason: Option<String>,
    pub files: usize,
}
