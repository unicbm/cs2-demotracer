use crate::model::{
    Cs2Rec, Cs2RecHeader, MovementSnapshot, ReplayTick, SubtickMove, CS2REC_VERSION,
};
use crate::{io_error, Error, Result};
use std::fs::File;
use std::io::{BufReader, BufWriter, Read, Write};
use std::path::Path;

const MAGIC: &[u8; 8] = b"CS2BMREC";

pub fn write_rec_file(path: &Path, rec: &Cs2Rec) -> Result<()> {
    let file = File::create(path).map_err(|e| io_error(path, e))?;
    let mut writer = BufWriter::new(file);
    write_rec(&mut writer, rec)
}

pub fn read_rec_file(path: &Path) -> Result<Cs2Rec> {
    let file = File::open(path).map_err(|e| io_error(path, e))?;
    let mut reader = BufReader::new(file);
    read_rec(&mut reader)
}

pub fn write_rec<W: Write>(writer: &mut W, rec: &Cs2Rec) -> Result<()> {
    writer
        .write_all(MAGIC)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    write_u32(writer, rec.header.version)?;
    write_f32(writer, rec.header.tick_rate)?;
    write_u32(writer, rec.header.round)?;
    write_u8(writer, rec.header.side)?;
    write_u32(writer, rec.header.flags)?;
    write_u64(writer, rec.header.steam_id)?;
    write_u32(writer, rec.ticks.len() as u32)?;
    write_u32(writer, rec.subticks.len() as u32)?;
    write_string(writer, &rec.header.map)?;
    write_string(writer, &rec.header.player_name)?;

    for tick in &rec.ticks {
        write_snapshot(writer, &tick.pre)?;
        write_snapshot(writer, &tick.post)?;
        write_i32(writer, tick.weapon_def_index)?;
        write_u32(writer, tick.num_subtick)?;
    }

    for subtick in &rec.subticks {
        write_f32(writer, subtick.when)?;
        write_u32(writer, subtick.button)?;
        write_f32(writer, subtick.pressed)?;
        write_f32(writer, subtick.analog_forward)?;
        write_f32(writer, subtick.analog_left)?;
        write_f32(writer, subtick.pitch_delta)?;
        write_f32(writer, subtick.yaw_delta)?;
    }

    Ok(())
}

pub fn read_rec<R: Read>(reader: &mut R) -> Result<Cs2Rec> {
    let mut magic = [0_u8; 8];
    reader
        .read_exact(&mut magic)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    if &magic != MAGIC {
        return Err(Error::InvalidRec("bad magic".to_string()));
    }

    let version = read_u32(reader)?;
    if version != CS2REC_VERSION {
        return Err(Error::InvalidRec(format!("unsupported version {version}")));
    }

    let tick_rate = read_f32(reader)?;
    let round = read_u32(reader)?;
    let side = read_u8(reader)?;
    let flags = read_u32(reader)?;
    let steam_id = read_u64(reader)?;
    let tick_count = read_u32(reader)? as usize;
    let subtick_count = read_u32(reader)? as usize;
    let map = read_string(reader)?;
    let player_name = read_string(reader)?;

    let mut ticks = Vec::with_capacity(tick_count);
    let mut expected_subticks = 0_usize;
    for _ in 0..tick_count {
        let pre = read_snapshot(reader)?;
        let post = read_snapshot(reader)?;
        let weapon_def_index = read_i32(reader)?;
        let num_subtick = read_u32(reader)?;
        expected_subticks += num_subtick as usize;
        ticks.push(ReplayTick {
            pre,
            post,
            weapon_def_index,
            num_subtick,
        });
    }

    if expected_subticks != subtick_count {
        return Err(Error::InvalidRec(format!(
            "tick subtick sum {expected_subticks} != header subtick count {subtick_count}"
        )));
    }

    let mut subticks = Vec::with_capacity(subtick_count);
    for _ in 0..subtick_count {
        subticks.push(SubtickMove {
            when: read_f32(reader)?,
            button: read_u32(reader)?,
            pressed: read_f32(reader)?,
            analog_forward: read_f32(reader)?,
            analog_left: read_f32(reader)?,
            pitch_delta: read_f32(reader)?,
            yaw_delta: read_f32(reader)?,
        });
    }

    Ok(Cs2Rec {
        header: Cs2RecHeader {
            version,
            tick_rate,
            map,
            round,
            side,
            steam_id,
            player_name,
            flags,
        },
        ticks,
        subticks,
    })
}

fn write_snapshot<W: Write>(writer: &mut W, snapshot: &MovementSnapshot) -> Result<()> {
    for value in snapshot.origin {
        write_f32(writer, value)?;
    }
    for value in snapshot.velocity {
        write_f32(writer, value)?;
    }
    for value in snapshot.angles {
        write_f32(writer, value)?;
    }
    write_u32(writer, snapshot.entity_flags)?;
    write_u8(writer, snapshot.move_type)?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    write_u64(writer, snapshot.buttons)?;
    write_u64(writer, snapshot.buttons1)?;
    write_u64(writer, snapshot.buttons2)?;
    write_f32(writer, snapshot.duck_amount)?;
    write_f32(writer, snapshot.duck_speed)?;
    for value in snapshot.ladder_normal {
        write_f32(writer, value)?;
    }
    write_u8(writer, snapshot.ducked)?;
    write_u8(writer, snapshot.ducking)?;
    write_u8(writer, snapshot.desires_duck)?;
    write_u8(writer, snapshot.actual_move_type)?;
    Ok(())
}

fn read_snapshot<R: Read>(reader: &mut R) -> Result<MovementSnapshot> {
    let mut origin = [0.0_f32; 3];
    let mut velocity = [0.0_f32; 3];
    let mut angles = [0.0_f32; 3];
    for value in &mut origin {
        *value = read_f32(reader)?;
    }
    for value in &mut velocity {
        *value = read_f32(reader)?;
    }
    for value in &mut angles {
        *value = read_f32(reader)?;
    }
    let entity_flags = read_u32(reader)?;
    let move_type = read_u8(reader)?;
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    let buttons = read_u64(reader)?;
    let buttons1 = read_u64(reader)?;
    let buttons2 = read_u64(reader)?;
    let duck_amount = read_f32(reader)?;
    let duck_speed = read_f32(reader)?;
    let mut ladder_normal = [0.0_f32; 3];
    for value in &mut ladder_normal {
        *value = read_f32(reader)?;
    }
    let ducked = read_u8(reader)?;
    let ducking = read_u8(reader)?;
    let desires_duck = read_u8(reader)?;
    let actual_move_type = read_u8(reader)?;
    Ok(MovementSnapshot {
        origin,
        velocity,
        angles,
        entity_flags,
        move_type,
        buttons,
        buttons1,
        buttons2,
        duck_amount,
        duck_speed,
        ladder_normal,
        ducked,
        ducking,
        desires_duck,
        actual_move_type,
    })
}

fn write_string<W: Write>(writer: &mut W, value: &str) -> Result<()> {
    let bytes = value.as_bytes();
    if bytes.len() > u16::MAX as usize {
        return Err(Error::InvalidRec(format!(
            "string too long: {} bytes",
            bytes.len()
        )));
    }
    write_u16(writer, bytes.len() as u16)?;
    writer
        .write_all(bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn read_string<R: Read>(reader: &mut R) -> Result<String> {
    let len = read_u16(reader)? as usize;
    let mut bytes = vec![0_u8; len];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    String::from_utf8(bytes).map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u8<W: Write>(writer: &mut W, value: u8) -> Result<()> {
    writer
        .write_all(&[value])
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u16<W: Write>(writer: &mut W, value: u16) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u32<W: Write>(writer: &mut W, value: u32) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_i32<W: Write>(writer: &mut W, value: i32) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_u64<W: Write>(writer: &mut W, value: u64) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn write_f32<W: Write>(writer: &mut W, value: f32) -> Result<()> {
    writer
        .write_all(&value.to_le_bytes())
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn read_u8<R: Read>(reader: &mut R) -> Result<u8> {
    let mut bytes = [0_u8; 1];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(bytes[0])
}

fn read_u16<R: Read>(reader: &mut R) -> Result<u16> {
    let mut bytes = [0_u8; 2];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(u16::from_le_bytes(bytes))
}

fn read_u32<R: Read>(reader: &mut R) -> Result<u32> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(u32::from_le_bytes(bytes))
}

fn read_i32<R: Read>(reader: &mut R) -> Result<i32> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(i32::from_le_bytes(bytes))
}

fn read_u64<R: Read>(reader: &mut R) -> Result<u64> {
    let mut bytes = [0_u8; 8];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(u64::from_le_bytes(bytes))
}

fn read_f32<R: Read>(reader: &mut R) -> Result<f32> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(f32::from_le_bytes(bytes))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Cs2RecHeader, MovementSnapshot, ReplayTick, SubtickMove};

    #[test]
    fn rec_reader_rejects_mismatched_subtick_count() {
        let rec = Cs2Rec {
            ticks: vec![ReplayTick {
                num_subtick: 1,
                ..ReplayTick::default()
            }],
            subticks: Vec::new(),
            ..Cs2Rec::default()
        };

        let mut bytes = Vec::new();
        write_rec(&mut bytes, &rec).unwrap();
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err
            .to_string()
            .contains("tick subtick sum 1 != header subtick count 0"));
    }

    #[test]
    fn rec_roundtrip_is_stable() {
        let snapshot = MovementSnapshot {
            origin: [1.0, 2.0, 3.0],
            velocity: [10.0, 20.0, 30.0],
            angles: [4.0, 90.0, 0.0],
            entity_flags: 1,
            move_type: 2,
            buttons: 33,
            buttons1: 1,
            buttons2: 2,
            duck_amount: 1.0,
            duck_speed: 8.0,
            ladder_normal: [0.0, 0.0, 1.0],
            ducked: 1,
            ducking: 1,
            desires_duck: 1,
            actual_move_type: 2,
        };
        let rec = Cs2Rec {
            header: Cs2RecHeader {
                version: CS2REC_VERSION,
                tick_rate: 64.0,
                map: "de_mirage".to_string(),
                round: 7,
                side: 2,
                steam_id: 76561198000000000,
                player_name: "player".to_string(),
                flags: 0,
            },
            ticks: vec![ReplayTick {
                pre: snapshot.clone(),
                post: snapshot,
                weapon_def_index: 7,
                num_subtick: 1,
            }],
            subticks: vec![SubtickMove {
                when: 0.5,
                button: 1,
                pressed: 1.0,
                analog_forward: 0.0,
                analog_left: 0.0,
                pitch_delta: 0.0,
                yaw_delta: 1.25,
            }],
        };

        let mut bytes = Vec::new();
        write_rec(&mut bytes, &rec).unwrap();
        assert_eq!(&bytes[0..8], MAGIC);
        let parsed = read_rec(&mut &bytes[..]).unwrap();
        assert_eq!(parsed, rec);
    }
}
