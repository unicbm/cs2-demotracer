use super::quality::{analyze_demo, AnalysisOptions};
use crate::model::{DemoAnalysis, ParsedDemo};
use serde::Serialize;
use std::collections::{BTreeMap, BTreeSet};

#[derive(Clone, Debug, Serialize)]
pub struct BrowserDemoAnalysis {
    #[serde(flatten)]
    pub analysis: DemoAnalysis,
    pub players: Vec<BrowserPlayerSummary>,
    pub score: Option<BrowserScoreSummary>,
}

#[derive(Clone, Debug, Serialize)]
pub struct BrowserPlayerSummary {
    pub name: String,
    pub steam_id: String,
    pub side: String,
    pub rounds: usize,
    pub rows: usize,
}

#[derive(Clone, Debug, Serialize)]
pub struct BrowserScoreSummary {
    pub t: BrowserTeamScore,
    pub ct: BrowserTeamScore,
}

#[derive(Clone, Debug, Serialize)]
pub struct BrowserTeamScore {
    pub score: u32,
    pub name: Option<String>,
}

pub fn analyze_browser_demo(parsed: &ParsedDemo, options: AnalysisOptions) -> BrowserDemoAnalysis {
    BrowserDemoAnalysis {
        analysis: analyze_demo(parsed, options),
        players: summarize_players(parsed),
        score: summarize_score(parsed),
    }
}

fn summarize_players(parsed: &ParsedDemo) -> Vec<BrowserPlayerSummary> {
    let mut players: BTreeMap<u64, PlayerAccumulator> = BTreeMap::new();
    for row in &parsed.rows {
        if row.steam_id == 0 || !matches!(row.team_num, 2 | 3) {
            continue;
        }
        let player = players.entry(row.steam_id).or_default();
        player.rows += 1;
        player.rounds.insert(row.round);
        *player.side_rows.entry(row.team_num).or_default() += 1;
        if player.name.is_empty() && !row.name.is_empty() {
            player.name = row.name.clone();
        }
    }

    players
        .into_iter()
        .map(|(steam_id, player)| {
            let side = side_label(player.primary_side());
            let name = if player.name.is_empty() {
                steam_id.to_string()
            } else {
                player.name
            };
            BrowserPlayerSummary {
                name,
                steam_id: steam_id.to_string(),
                side,
                rounds: player.rounds.len(),
                rows: player.rows,
            }
        })
        .collect()
}

fn summarize_score(parsed: &ParsedDemo) -> Option<BrowserScoreSummary> {
    let mut t = None::<ScoreCandidate>;
    let mut ct = None::<ScoreCandidate>;
    for row in &parsed.rows {
        let Some(score) = row.team_rounds_total else {
            continue;
        };
        let candidate = ScoreCandidate {
            tick: row.tick,
            score,
            name: clean_team_name(row),
        };
        match row.team_num {
            2 => update_score(&mut t, candidate),
            3 => update_score(&mut ct, candidate),
            _ => {}
        }
    }

    Some(BrowserScoreSummary {
        t: t?.into_team_score(),
        ct: ct?.into_team_score(),
    })
}

fn update_score(slot: &mut Option<ScoreCandidate>, candidate: ScoreCandidate) {
    if slot
        .as_ref()
        .map(|current| candidate.tick >= current.tick)
        .unwrap_or(true)
    {
        *slot = Some(candidate);
    }
}

fn clean_team_name(row: &crate::model::ParsedPlayerTick) -> Option<String> {
    row.team_clan_name
        .as_deref()
        .filter(|value| !value.trim().is_empty())
        .or_else(|| {
            row.team_name
                .as_deref()
                .filter(|value| !value.trim().is_empty())
        })
        .map(str::trim)
        .map(str::to_string)
}

fn side_label(team_num: u8) -> String {
    match team_num {
        2 => "T".to_string(),
        3 => "CT".to_string(),
        _ => "Unknown".to_string(),
    }
}

#[derive(Default)]
struct PlayerAccumulator {
    name: String,
    rows: usize,
    rounds: BTreeSet<u32>,
    side_rows: BTreeMap<u8, usize>,
}

impl PlayerAccumulator {
    fn primary_side(&self) -> u8 {
        self.side_rows
            .iter()
            .max_by_key(|(team_num, rows)| (**rows, std::cmp::Reverse(**team_num)))
            .map(|(team_num, _)| *team_num)
            .unwrap_or(0)
    }
}

struct ScoreCandidate {
    tick: i32,
    score: u32,
    name: Option<String>,
}

impl ScoreCandidate {
    fn into_team_score(self) -> BrowserTeamScore {
        BrowserTeamScore {
            score: self.score,
            name: self.name,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{ParsedDemo, ParsedPlayerTick};

    fn row(round: u32, tick: i32, team_num: u8, steam_id: u64, name: &str) -> ParsedPlayerTick {
        ParsedPlayerTick {
            round,
            tick,
            team_num,
            steam_id,
            name: name.to_string(),
            is_alive: true,
            round_in_progress: true,
            ..ParsedPlayerTick::default()
        }
    }

    fn demo(rows: Vec<ParsedPlayerTick>) -> ParsedDemo {
        ParsedDemo {
            path: "demo.dem".to_string(),
            stem: "demo".to_string(),
            map: "de_anubis".to_string(),
            tick_rate: 64.0,
            rows,
            ..ParsedDemo::default()
        }
    }

    #[test]
    fn summarizes_players_by_steam_id_side_rounds_and_rows() {
        let mut rows = Vec::new();
        for steam_id in 1..=5 {
            rows.push(row(1, 100, 2, steam_id, &format!("t{steam_id}")));
            rows.push(row(2, 200, 2, steam_id, &format!("t{steam_id}")));
        }
        for steam_id in 6..=10 {
            rows.push(row(1, 100, 3, steam_id, &format!("ct{steam_id}")));
        }

        let players = summarize_players(&demo(rows));

        assert_eq!(players.len(), 10);
        assert_eq!(players[0].name, "t1");
        assert_eq!(players[0].side, "T");
        assert_eq!(players[0].rounds, 2);
        assert_eq!(players[0].rows, 2);
        assert_eq!(players[9].side, "CT");
    }

    #[test]
    fn returns_none_when_final_score_is_unavailable() {
        let parsed = demo(vec![row(1, 100, 2, 1, "alpha"), row(1, 100, 3, 2, "bravo")]);

        assert!(summarize_score(&parsed).is_none());
    }

    #[test]
    fn summarizes_latest_real_team_score() {
        let mut t_row = row(1, 100, 2, 1, "alpha");
        t_row.team_rounds_total = Some(12);
        t_row.team_clan_name = Some("T Squad".to_string());
        let mut ct_row = row(1, 100, 3, 2, "bravo");
        ct_row.team_rounds_total = Some(10);
        ct_row.team_name = Some("Counter".to_string());
        let mut late_t_row = row(2, 200, 2, 1, "alpha");
        late_t_row.team_rounds_total = Some(13);

        let score = summarize_score(&demo(vec![t_row, ct_row, late_t_row])).unwrap();

        assert_eq!(score.t.score, 13);
        assert_eq!(score.ct.score, 10);
        assert_eq!(score.ct.name.as_deref(), Some("Counter"));
    }
}
