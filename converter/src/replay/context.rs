use crate::model::{Cs2Rec, ParsedPlayerTick, ReplayLoadout};
use std::collections::BTreeSet;

pub(crate) fn first_weapon_def_index(rec: &Cs2Rec) -> i32 {
    rec.ticks
        .iter()
        .map(|tick| normalize_weapon_def_index(tick.weapon_def_index))
        .find(|def| is_known_weapon_def_index(*def))
        .unwrap_or(-1)
}

pub(crate) fn preload_weapon_def_indices_from_refs(
    rows: &[&ParsedPlayerTick],
    rec: &Cs2Rec,
) -> Vec<i32> {
    preload_weapon_def_indices_from_iter(rows.iter().copied(), rec)
}

fn preload_weapon_def_indices_from_iter<'a>(
    rows: impl IntoIterator<Item = &'a ParsedPlayerTick>,
    rec: &Cs2Rec,
) -> Vec<i32> {
    let mut seen = BTreeSet::new();
    let mut defs = Vec::new();
    for row in rows {
        for raw_def in &row.inventory_as_ids {
            let def = normalize_weapon_def_index(*raw_def);
            if is_preload_weapon_def_index(def) && seen.insert(def) {
                defs.push(def);
            }
        }
    }
    for tick in &rec.ticks {
        let def = normalize_weapon_def_index(tick.weapon_def_index);
        if is_preload_weapon_def_index(def) && seen.insert(def) {
            defs.push(def);
        }
    }
    defs
}

pub(crate) fn replay_loadout(row: &ParsedPlayerTick) -> ReplayLoadout {
    ReplayLoadout {
        weapon_def_indices: row
            .inventory_as_ids
            .iter()
            .map(|def| normalize_weapon_def_index(*def))
            .filter(|def| is_loadout_weapon_def_index(*def))
            .collect(),
        armor_value: row.armor_value,
        has_helmet: row.has_helmet,
        has_defuser: row.has_defuser,
    }
}

fn normalize_weapon_def_index(def: i32) -> i32 {
    if def == 42 || def == 59 || (500..600).contains(&def) {
        42
    } else {
        def
    }
}

fn is_known_weapon_def_index(def: i32) -> bool {
    matches!(
        def,
        1 | 2
            | 3
            | 4
            | 7
            | 8
            | 9
            | 10
            | 11
            | 13
            | 14
            | 16
            | 17
            | 19
            | 23
            | 24
            | 25
            | 26
            | 27
            | 28
            | 29
            | 30
            | 31
            | 32
            | 33
            | 34
            | 35
            | 36
            | 38
            | 39
            | 40
            | 42
            | 43
            | 44
            | 45
            | 46
            | 47
            | 48
            | 49
            | 60
            | 61
            | 63
            | 64
    )
}

fn is_preload_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 31 | 42 | 49)
}

fn is_loadout_weapon_def_index(def: i32) -> bool {
    is_known_weapon_def_index(def) && !matches!(def, 42 | 49)
}
