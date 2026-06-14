use crate::model::{Cs2Rec, Cs2RecHeader, ParsedPlayerTick, ReplayTick};
use crate::{Error, Result};

pub fn synthesize_player_rec(
    rows: &[ParsedPlayerTick],
    map: &str,
    tick_rate: f32,
    round: u32,
) -> Result<Cs2Rec> {
    if rows.len() < 2 {
        return Err(Error::InvalidDemo(
            "need at least two player rows to synthesize replay".to_string(),
        ));
    }
    let first = &rows[0];
    let mut ticks = Vec::with_capacity(rows.len().saturating_sub(1));
    for pair in rows.windows(2) {
        let pre = pair[0].snapshot();
        let post = pair[1].snapshot();
        ticks.push(ReplayTick {
            pre,
            post,
            weapon_def_index: pair[0].item_def_idx,
            num_subtick: 0,
        });
    }

    Ok(Cs2Rec {
        header: Cs2RecHeader {
            version: crate::model::CS2REC_VERSION,
            tick_rate,
            map: map.to_string(),
            round,
            side: first.team_num,
            steam_id: first.steam_id,
            player_name: first.name.clone(),
            flags: 0,
        },
        ticks,
        subticks: Vec::new(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn row(tick: i32, weapon: i32) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id: 42,
            name: "p".to_string(),
            team_num: 2,
            is_alive: true,
            round: 1,
            round_in_progress: true,
            is_freeze_period: false,
            game_time: Some(tick as f32 / 64.0),
            origin: [tick as f32, 0.0, 64.0],
            velocity: [1.0, 2.0, 3.0],
            pitch: 4.0,
            yaw: 5.0,
            buttons: 1,
            item_def_idx: weapon,
            inventory_as_ids: Vec::new(),
            entity_flags: 1,
            move_type: 2,
        }
    }

    #[test]
    fn synthesis_uses_adjacent_rows_as_pre_post() {
        let rec = synthesize_player_rec(&[row(10, 7), row(11, 7), row(12, 9)], "de_nuke", 64.0, 1)
            .unwrap();
        assert_eq!(rec.ticks.len(), 2);
        assert_eq!(rec.ticks[0].pre.origin[0], 10.0);
        assert_eq!(rec.ticks[0].post.origin[0], 11.0);
        assert_eq!(rec.ticks[1].weapon_def_index, 7);
        assert!(rec.subticks.is_empty());
    }
}
