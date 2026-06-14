use crate::model::{DemoAnalysis, ParsedDemo, RoundStatus, RoundSummary};
use std::collections::BTreeMap;

#[derive(Clone, Copy, Debug)]
pub struct AnalysisOptions {
    pub min_round_seconds: f32,
    pub max_round_seconds: f32,
}

impl Default for AnalysisOptions {
    fn default() -> Self {
        Self {
            min_round_seconds: 10.0,
            max_round_seconds: 240.0,
        }
    }
}

pub fn analyze_demo(parsed: &ParsedDemo, options: AnalysisOptions) -> DemoAnalysis {
    let mut by_round: BTreeMap<u32, Vec<_>> = BTreeMap::new();
    for (idx, row) in parsed.rows.iter().enumerate() {
        by_round.entry(row.round).or_default().push(idx);
    }

    let mut rounds = Vec::new();
    for (round, indices) in by_round {
        let round_min_tick = indices.iter().map(|idx| parsed.rows[*idx].tick).min();
        let round_max_tick = indices.iter().map(|idx| parsed.rows[*idx].tick).max();
        let freeze_end_tick = match (round_min_tick, round_max_tick) {
            (Some(min_tick), Some(max_tick)) => parsed
                .round_freeze_end_ticks
                .iter()
                .copied()
                .find(|tick| *tick >= min_tick && *tick <= max_tick),
            _ => None,
        };
        let active: Vec<_> = indices
            .iter()
            .copied()
            .filter(|idx| {
                let row = &parsed.rows[*idx];
                row.round_in_progress && !row.is_freeze_period
            })
            .collect();
        let has_active_window = !active.is_empty();
        let source = if has_active_window { active } else { indices };
        let source = apply_round_start_floor(parsed, &source, freeze_end_tick);
        let Some(window) = select_stable_window(parsed, &source) else {
            continue;
        };

        let start_tick = window.start_tick;
        let end_tick = window.end_tick;
        let tick_span = (end_tick - start_tick).max(0) as f32;
        let duration_seconds = if parsed.tick_rate > 0.0 {
            tick_span / parsed.tick_rate
        } else {
            0.0
        };

        let t_count = window.t_players;
        let ct_count = window.ct_players;
        let total = t_count + ct_count;
        let mut problems = Vec::new();
        if total != 10 {
            problems.push(format!("available players {total} != 10"));
        }
        if t_count != 5 {
            problems.push(format!("T players {t_count} != 5"));
        }
        if ct_count != 5 {
            problems.push(format!("CT players {ct_count} != 5"));
        }
        if duration_seconds < options.min_round_seconds {
            problems.push(format!(
                "round window too short: {:.1}s < {:.1}s",
                duration_seconds, options.min_round_seconds
            ));
        }
        if duration_seconds > options.max_round_seconds {
            problems.push(format!(
                "round window too long: {:.1}s > {:.1}s",
                duration_seconds, options.max_round_seconds
            ));
        }
        if window.valid_rows < 2 {
            problems.push(format!("valid player rows {} < 2", window.valid_rows));
        }
        if !has_active_window && freeze_end_tick.is_none() {
            problems.push("missing active/freeze-end window; used raw round rows".to_string());
        }

        let status = if problems.is_empty() {
            RoundStatus::Recommended
        } else {
            RoundStatus::Suspicious
        };
        rounds.push(RoundSummary {
            round,
            start_tick,
            end_tick,
            duration_seconds,
            t_players: t_count,
            ct_players: ct_count,
            total_players: total,
            valid_rows: window.valid_rows,
            status,
            problems,
        });
    }

    DemoAnalysis {
        demo_path: parsed.path.clone(),
        demo_stem: parsed.stem.clone(),
        map: parsed.map.clone(),
        tick_rate: parsed.tick_rate,
        row_count: parsed.rows.len(),
        rounds,
    }
}

fn apply_round_start_floor(
    parsed: &ParsedDemo,
    indices: &[usize],
    freeze_end_tick: Option<i32>,
) -> Vec<usize> {
    let Some(start_tick) =
        freeze_end_tick.or_else(|| infer_round_start_from_movement(parsed, indices))
    else {
        return indices.to_vec();
    };
    let filtered = indices
        .iter()
        .copied()
        .filter(|idx| parsed.rows[*idx].tick >= start_tick)
        .collect::<Vec<_>>();
    if filtered.is_empty() {
        indices.to_vec()
    } else {
        filtered
    }
}

fn infer_round_start_from_movement(parsed: &ParsedDemo, indices: &[usize]) -> Option<i32> {
    let mut by_tick: BTreeMap<i32, usize> = BTreeMap::new();
    for idx in indices {
        let row = &parsed.rows[*idx];
        if !row.is_alive || row.steam_id == 0 || !matches!(row.team_num, 2 | 3) {
            continue;
        }
        let speed2d =
            (row.velocity[0] * row.velocity[0] + row.velocity[1] * row.velocity[1]).sqrt();
        if speed2d >= 30.0 {
            *by_tick.entry(row.tick).or_default() += 1;
        }
    }
    by_tick
        .into_iter()
        .find_map(|(tick, moving_players)| (moving_players >= 3).then_some(tick))
}

#[derive(Clone, Copy, Debug, Default)]
struct TeamSpan {
    rows: usize,
    first_tick: i32,
    last_tick: i32,
}

#[derive(Clone, Debug, Default)]
struct PlayerSpans {
    by_team: BTreeMap<u8, TeamSpan>,
}

#[derive(Clone, Copy, Debug)]
struct StableWindow {
    start_tick: i32,
    end_tick: i32,
    t_players: usize,
    ct_players: usize,
    valid_rows: usize,
}

fn select_stable_window(parsed: &ParsedDemo, indices: &[usize]) -> Option<StableWindow> {
    if indices.is_empty() {
        return None;
    }

    let mut players: BTreeMap<u64, PlayerSpans> = BTreeMap::new();
    for idx in indices {
        let row = &parsed.rows[*idx];
        if !row.is_alive || row.steam_id == 0 || !matches!(row.team_num, 2 | 3) {
            continue;
        }
        let spans = players.entry(row.steam_id).or_default();
        let span = spans.by_team.entry(row.team_num).or_insert(TeamSpan {
            rows: 0,
            first_tick: row.tick,
            last_tick: row.tick,
        });
        span.rows += 1;
        span.first_tick = span.first_tick.min(row.tick);
        span.last_tick = span.last_tick.max(row.tick);
    }

    let mut assignments: BTreeMap<u64, (u8, TeamSpan)> = BTreeMap::new();
    for (steam_id, spans) in players {
        let Some((&team, &span)) = spans
            .by_team
            .iter()
            .max_by_key(|(_, span)| (span.last_tick, span.rows))
        else {
            continue;
        };
        assignments.insert(steam_id, (team, span));
    }

    let mut start_tick = None::<i32>;
    let mut end_tick = None::<i32>;
    let mut t_players = 0_usize;
    let mut ct_players = 0_usize;
    for (team, span) in assignments.values() {
        start_tick = Some(start_tick.map_or(span.first_tick, |tick| tick.max(span.first_tick)));
        end_tick = Some(end_tick.map_or(span.last_tick, |tick| tick.max(span.last_tick)));
        match team {
            2 => t_players += 1,
            3 => ct_players += 1,
            _ => {}
        }
    }
    let start_tick = start_tick?;
    let end_tick = end_tick?;

    let mut valid_rows = 0_usize;
    for idx in indices {
        let row = &parsed.rows[*idx];
        if row.tick < start_tick || row.tick > end_tick || !row.is_alive {
            continue;
        }
        let Some((assigned_team, _)) = assignments.get(&row.steam_id) else {
            continue;
        };
        if row.team_num == *assigned_team {
            valid_rows += 1;
        }
    }

    Some(StableWindow {
        start_tick,
        end_tick,
        t_players,
        ct_players,
        valid_rows,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::ParsedPlayerTick;

    fn row(round: u32, tick: i32, team_num: u8, steam_id: u64) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id,
            name: steam_id.to_string(),
            team_num,
            is_alive: true,
            round,
            round_in_progress: true,
            is_freeze_period: false,
            game_time: None,
            origin: [0.0, 0.0, 0.0],
            velocity: [0.0, 0.0, 0.0],
            pitch: 0.0,
            yaw: 0.0,
            buttons: 0,
            item_def_idx: -1,
            inventory_as_ids: Vec::new(),
            entity_flags: 1,
            move_type: 2,
        }
    }

    #[test]
    fn flags_underfilled_round_as_suspicious() {
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            map: "de_test".to_string(),
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            rows: vec![row(1, 0, 2, 1), row(1, 640, 3, 2)],
        };
        let analysis = analyze_demo(&parsed, AnalysisOptions::default());
        assert_eq!(analysis.rounds[0].status, RoundStatus::Suspicious);
        assert!(analysis.rounds[0]
            .problems
            .iter()
            .any(|p| p.contains("available players")));
    }

    #[test]
    fn flags_round_without_active_window_as_suspicious() {
        let mut frozen = row(1, 0, 2, 1);
        frozen.round_in_progress = false;
        frozen.is_freeze_period = true;
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            map: "de_test".to_string(),
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            rows: vec![frozen],
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.rounds[0].status, RoundStatus::Suspicious);
        assert!(analysis.rounds[0]
            .problems
            .iter()
            .any(|p| p.contains("available players")));
    }

    #[test]
    fn recovers_halftime_side_swap_window_from_raw_rows() {
        let mut rows = Vec::new();
        let mut old_side = row(12, 0, 2, 1);
        old_side.round_in_progress = false;
        old_side.is_freeze_period = true;
        rows.push(old_side);

        for steam_id in 1..=5 {
            for tick in [100, 800] {
                let mut r = row(12, tick, 3, steam_id);
                r.round_in_progress = false;
                r.is_freeze_period = true;
                rows.push(r);
            }
        }
        for steam_id in 6..=10 {
            for tick in [100, 800] {
                let mut r = row(12, tick, 2, steam_id);
                r.round_in_progress = false;
                r.is_freeze_period = true;
                rows.push(r);
            }
        }

        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            map: "de_test".to_string(),
            tick_rate: 64.0,
            round_freeze_end_ticks: Vec::new(),
            rows,
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());
        let summary = &analysis.rounds[0];

        assert_eq!(summary.status, RoundStatus::Suspicious);
        assert_eq!(summary.start_tick, 100);
        assert_eq!(summary.t_players, 5);
        assert_eq!(summary.ct_players, 5);
    }

    #[test]
    fn uses_round_freeze_end_event_as_window_floor() {
        let rows = vec![
            row(1, 100, 2, 1),
            row(1, 100, 3, 2),
            row(1, 200, 2, 1),
            row(1, 200, 3, 2),
        ];
        let parsed = ParsedDemo {
            path: "x.dem".to_string(),
            stem: "x".to_string(),
            map: "de_test".to_string(),
            tick_rate: 64.0,
            round_freeze_end_ticks: vec![150],
            rows,
        };

        let analysis = analyze_demo(&parsed, AnalysisOptions::default());

        assert_eq!(analysis.rounds[0].start_tick, 200);
    }
}
