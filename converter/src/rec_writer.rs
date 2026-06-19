use crate::model::{
    Cs2Rec, Cs2RecHeader, MovementSnapshot, ProjectileKind, ReplayProjectile, ReplayTick,
    SubtickMove, DTR_FORMAT_VERSION,
};
use crate::{io_error, Error, Result};
use std::fs::File;
use std::io::{BufReader, BufWriter, Cursor, Read, Write};
use std::path::Path;

const MAGIC: &[u8; 8] = b"CSDTRREC";
const CODEC_BROTLI: u8 = 1;
const BROTLI_BUFFER_SIZE: usize = 4096;
const BROTLI_QUALITY: u32 = 6;
const BROTLI_LGWIN: u32 = 22;
const SNAPSHOT_BYTE_SIZE: usize = 92;
const TICK_METADATA_BYTE_SIZE: usize = 8;
const PROJECTILE_BYTE_SIZE: usize = 48;
const SUBTICK_BYTE_SIZE: usize = 28;

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
    validate_subtick_count(rec)?;
    validate_play_start_tick(rec.ticks.len(), rec.header.play_start_tick_index)?;
    validate_snapshot_chain(rec)?;
    let tick_count = checked_u32_count("tick count", rec.ticks.len())?;
    let subtick_count = checked_u32_count("subtick count", rec.subticks.len())?;
    let projectile_count = checked_u32_count("projectile count", rec.projectiles.len())?;
    let body = build_body(rec)?;
    let compressed = compress_body(&body)?;

    writer
        .write_all(MAGIC)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    write_u32(writer, DTR_FORMAT_VERSION)?;
    write_f32(writer, rec.header.tick_rate)?;
    write_u32(writer, rec.header.round)?;
    write_u8(writer, rec.header.side)?;
    write_u32(writer, rec.header.flags)?;
    write_u64(writer, rec.header.steam_id)?;
    write_u32(writer, tick_count)?;
    write_u32(writer, subtick_count)?;
    write_u32(writer, projectile_count)?;
    write_u32(writer, rec.header.play_start_tick_index)?;
    write_string(writer, &rec.header.map)?;
    write_string(writer, &rec.header.player_name)?;
    write_u8(writer, CODEC_BROTLI)?;
    write_u64(writer, body.len() as u64)?;
    write_u64(writer, compressed.len() as u64)?;
    writer
        .write_all(&compressed)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;

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
    if !(3..=DTR_FORMAT_VERSION).contains(&version) {
        return Err(Error::InvalidRec(format!("unsupported version {version}")));
    }

    let tick_rate = read_f32(reader)?;
    let round = read_u32(reader)?;
    let side = read_u8(reader)?;
    let flags = read_u32(reader)?;
    let steam_id = read_u64(reader)?;
    let tick_count = read_u32(reader)? as usize;
    let subtick_count = read_u32(reader)? as usize;
    let projectile_count = if version >= 4 {
        read_u32(reader)? as usize
    } else {
        0
    };
    let play_start_tick_index = if version >= 5 { read_u32(reader)? } else { 0 };
    validate_play_start_tick(tick_count, play_start_tick_index)?;
    let map = read_string(reader)?;
    let player_name = read_string(reader)?;
    let codec = read_u8(reader)?;
    if codec != CODEC_BROTLI {
        return Err(Error::InvalidRec(format!("unsupported codec {codec}")));
    }

    let body_uncompressed_len = checked_len(read_u64(reader)?, "body_uncompressed_len")?;
    let body_compressed_len = checked_len(read_u64(reader)?, "body_compressed_len")?;
    let expected_body_len = expected_body_len(tick_count, subtick_count, projectile_count)?;
    if body_uncompressed_len != expected_body_len {
        return Err(Error::InvalidRec(format!(
            "body length {body_uncompressed_len} != expected {expected_body_len}"
        )));
    }

    let mut compressed = vec![0_u8; body_compressed_len];
    reader
        .read_exact(&mut compressed)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    let body = decompress_body(&compressed, body_uncompressed_len)?;
    let (ticks, projectiles, subticks) =
        read_body(&body, tick_count, projectile_count, subtick_count)?;

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
            play_start_tick_index,
        },
        ticks,
        projectiles,
        subticks,
    })
}

fn validate_subtick_count(rec: &Cs2Rec) -> Result<()> {
    let expected_subticks: usize = rec.ticks.iter().map(|tick| tick.num_subtick as usize).sum();
    if expected_subticks != rec.subticks.len() {
        return Err(Error::InvalidRec(format!(
            "tick subtick sum {expected_subticks} != header subtick count {}",
            rec.subticks.len()
        )));
    }
    Ok(())
}

fn validate_play_start_tick(tick_count: usize, play_start_tick_index: u32) -> Result<()> {
    if tick_count == 0 {
        if play_start_tick_index == 0 {
            return Ok(());
        }
        return Err(Error::InvalidRec(format!(
            "play_start_tick_index {play_start_tick_index} requires at least one tick"
        )));
    }
    if play_start_tick_index as usize >= tick_count {
        return Err(Error::InvalidRec(format!(
            "play_start_tick_index {play_start_tick_index} out of range for {tick_count} ticks"
        )));
    }
    Ok(())
}

fn validate_snapshot_chain(rec: &Cs2Rec) -> Result<()> {
    for (index, pair) in rec.ticks.windows(2).enumerate() {
        if !snapshot_bit_eq(&pair[0].post, &pair[1].pre) {
            return Err(Error::InvalidRec(format!(
                "discontinuous snapshot chain between ticks {index} and {}",
                index + 1
            )));
        }
    }
    Ok(())
}

fn build_body(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(expected_body_len(
        rec.ticks.len(),
        rec.subticks.len(),
        rec.projectiles.len(),
    )?);
    if let Some(first) = rec.ticks.first() {
        write_snapshot(&mut body, &first.pre)?;
        for tick in &rec.ticks {
            write_snapshot(&mut body, &tick.post)?;
        }
    }
    for tick in &rec.ticks {
        write_i32(&mut body, tick.weapon_def_index)?;
        write_u32(&mut body, tick.num_subtick)?;
    }
    for projectile in &rec.projectiles {
        write_projectile(&mut body, projectile)?;
    }
    for subtick in &rec.subticks {
        write_subtick(&mut body, subtick)?;
    }
    Ok(body)
}

fn read_body(
    body: &[u8],
    tick_count: usize,
    projectile_count: usize,
    subtick_count: usize,
) -> Result<(Vec<ReplayTick>, Vec<ReplayProjectile>, Vec<SubtickMove>)> {
    let mut reader = Cursor::new(body);
    let snapshot_count = if tick_count == 0 { 0 } else { tick_count + 1 };
    let mut snapshots = Vec::with_capacity(snapshot_count);
    for _ in 0..snapshot_count {
        snapshots.push(read_snapshot(&mut reader)?);
    }

    let mut ticks = Vec::with_capacity(tick_count);
    let mut expected_subticks = 0_usize;
    for i in 0..tick_count {
        let weapon_def_index = read_i32(&mut reader)?;
        let num_subtick = read_u32(&mut reader)?;
        expected_subticks += num_subtick as usize;
        ticks.push(ReplayTick {
            pre: snapshots[i].clone(),
            post: snapshots[i + 1].clone(),
            weapon_def_index,
            num_subtick,
        });
    }

    if expected_subticks != subtick_count {
        return Err(Error::InvalidRec(format!(
            "tick subtick sum {expected_subticks} != header subtick count {subtick_count}"
        )));
    }

    let mut projectiles = Vec::with_capacity(projectile_count);
    for _ in 0..projectile_count {
        projectiles.push(read_projectile(&mut reader)?);
    }

    let mut subticks = Vec::with_capacity(subtick_count);
    for _ in 0..subtick_count {
        subticks.push(read_subtick(&mut reader)?);
    }
    if reader.position() != body.len() as u64 {
        return Err(Error::InvalidRec("trailing bytes in .dtr body".to_string()));
    }
    Ok((ticks, projectiles, subticks))
}

fn compress_body(body: &[u8]) -> Result<Vec<u8>> {
    let mut compressed = Vec::new();
    {
        let mut compressor = brotli::CompressorWriter::new(
            &mut compressed,
            BROTLI_BUFFER_SIZE,
            BROTLI_QUALITY,
            BROTLI_LGWIN,
        );
        compressor
            .write_all(body)
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
        compressor
            .flush()
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
    }
    Ok(compressed)
}

fn decompress_body(compressed: &[u8], expected_len: usize) -> Result<Vec<u8>> {
    let mut decompressor = brotli::Decompressor::new(compressed, BROTLI_BUFFER_SIZE);
    let mut body = Vec::with_capacity(expected_len);
    decompressor
        .read_to_end(&mut body)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    if body.len() != expected_len {
        return Err(Error::InvalidRec(format!(
            "decompressed body length {} != expected {expected_len}",
            body.len()
        )));
    }
    Ok(body)
}

fn expected_body_len(
    tick_count: usize,
    subtick_count: usize,
    projectile_count: usize,
) -> Result<usize> {
    let snapshot_count = if tick_count == 0 { 0 } else { tick_count + 1 };
    let snapshot_bytes = snapshot_count
        .checked_mul(SNAPSHOT_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("snapshot body too large".to_string()))?;
    let tick_bytes = tick_count
        .checked_mul(TICK_METADATA_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("tick body too large".to_string()))?;
    let subtick_bytes = subtick_count
        .checked_mul(SUBTICK_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("subtick body too large".to_string()))?;
    let projectile_bytes = projectile_count
        .checked_mul(PROJECTILE_BYTE_SIZE)
        .ok_or_else(|| Error::InvalidRec("projectile body too large".to_string()))?;
    snapshot_bytes
        .checked_add(tick_bytes)
        .and_then(|value| value.checked_add(projectile_bytes))
        .and_then(|value| value.checked_add(subtick_bytes))
        .ok_or_else(|| Error::InvalidRec("body too large".to_string()))
}

fn checked_len(value: u64, name: &str) -> Result<usize> {
    usize::try_from(value).map_err(|_| Error::InvalidRec(format!("{name} too large: {value}")))
}

fn checked_u32_count(name: &str, value: usize) -> Result<u32> {
    u32::try_from(value).map_err(|_| Error::InvalidRec(format!("{name} too large: {value}")))
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

fn write_subtick<W: Write>(writer: &mut W, subtick: &SubtickMove) -> Result<()> {
    write_f32(writer, subtick.when)?;
    write_u32(writer, subtick.button)?;
    write_f32(writer, subtick.pressed)?;
    write_f32(writer, subtick.analog_forward)?;
    write_f32(writer, subtick.analog_left)?;
    write_f32(writer, subtick.pitch_delta)?;
    write_f32(writer, subtick.yaw_delta)?;
    Ok(())
}

fn read_subtick<R: Read>(reader: &mut R) -> Result<SubtickMove> {
    Ok(SubtickMove {
        when: read_f32(reader)?,
        button: read_u32(reader)?,
        pressed: read_f32(reader)?,
        analog_forward: read_f32(reader)?,
        analog_left: read_f32(reader)?,
        pitch_delta: read_f32(reader)?,
        yaw_delta: read_f32(reader)?,
    })
}

fn write_projectile<W: Write>(writer: &mut W, projectile: &ReplayProjectile) -> Result<()> {
    write_u32(writer, projectile.tick_index)?;
    write_i32(writer, projectile.weapon_def_index)?;
    write_u8(writer, projectile.kind.to_u8())?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    for value in projectile.initial_position {
        write_f32(writer, value)?;
    }
    for value in projectile.initial_velocity {
        write_f32(writer, value)?;
    }
    for value in projectile.detonation_position {
        write_f32(writer, value)?;
    }
    Ok(())
}

fn read_projectile<R: Read>(reader: &mut R) -> Result<ReplayProjectile> {
    let tick_index = read_u32(reader)?;
    let weapon_def_index = read_i32(reader)?;
    let kind = ProjectileKind::from_u8(read_u8(reader)?);
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    let mut initial_position = [0.0_f32; 3];
    let mut initial_velocity = [0.0_f32; 3];
    let mut detonation_position = [0.0_f32; 3];
    for value in &mut initial_position {
        *value = read_f32(reader)?;
    }
    for value in &mut initial_velocity {
        *value = read_f32(reader)?;
    }
    for value in &mut detonation_position {
        *value = read_f32(reader)?;
    }
    Ok(ReplayProjectile {
        tick_index,
        kind,
        weapon_def_index,
        initial_position,
        initial_velocity,
        detonation_position,
    })
}

fn snapshot_bit_eq(a: &MovementSnapshot, b: &MovementSnapshot) -> bool {
    f32_array_bit_eq(&a.origin, &b.origin)
        && f32_array_bit_eq(&a.velocity, &b.velocity)
        && f32_array_bit_eq(&a.angles, &b.angles)
        && a.entity_flags == b.entity_flags
        && a.move_type == b.move_type
        && a.buttons == b.buttons
        && a.buttons1 == b.buttons1
        && a.buttons2 == b.buttons2
        && a.duck_amount.to_bits() == b.duck_amount.to_bits()
        && a.duck_speed.to_bits() == b.duck_speed.to_bits()
        && f32_array_bit_eq(&a.ladder_normal, &b.ladder_normal)
        && a.ducked == b.ducked
        && a.ducking == b.ducking
        && a.desires_duck == b.desires_duck
        && a.actual_move_type == b.actual_move_type
}

fn f32_array_bit_eq<const N: usize>(a: &[f32; N], b: &[f32; N]) -> bool {
    a.iter()
        .zip(b.iter())
        .all(|(lhs, rhs)| lhs.to_bits() == rhs.to_bits())
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
    use crate::model::{
        Cs2RecHeader, MovementSnapshot, ProjectileKind, ReplayProjectile, ReplayTick, SubtickMove,
    };

    #[test]
    fn rec_writer_rejects_mismatched_subtick_count() {
        let mut rec = sample_rec();
        rec.ticks[0].num_subtick = 2;
        let mut bytes = Vec::new();
        let err = write_rec(&mut bytes, &rec).unwrap_err();
        assert!(err
            .to_string()
            .contains("tick subtick sum 2 != header subtick count 1"));
    }

    #[test]
    fn rec_reader_rejects_mismatched_subtick_count() {
        let rec = sample_rec();
        let mut body = build_body(&rec).unwrap();
        let metadata_offset = SNAPSHOT_BYTE_SIZE * (rec.ticks.len() + 1);
        body[metadata_offset + 4..metadata_offset + 8].copy_from_slice(&2_u32.to_le_bytes());
        let bytes = test_file_bytes(
            &body,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            CODEC_BROTLI,
            None,
        );
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err
            .to_string()
            .contains("tick subtick sum 2 != header subtick count 1"));
    }

    #[test]
    fn rec_roundtrip_is_bit_stable() {
        let mut rec = sample_rec();
        rec.header.play_start_tick_index = 1;

        let mut bytes = Vec::new();
        write_rec(&mut bytes, &rec).unwrap();
        assert_eq!(&bytes[0..8], MAGIC);
        assert_eq!(
            u32::from_le_bytes(bytes[8..12].try_into().unwrap()),
            DTR_FORMAT_VERSION
        );
        let parsed = read_rec(&mut &bytes[..]).unwrap();
        assert_eq!(parsed.header, rec.header);
        assert_eq!(parsed.ticks.len(), rec.ticks.len());
        assert_eq!(parsed.projectiles, rec.projectiles);
        assert_eq!(parsed.subticks.len(), rec.subticks.len());
        for (parsed_tick, tick) in parsed.ticks.iter().zip(rec.ticks.iter()) {
            assert!(snapshot_bit_eq(&parsed_tick.pre, &tick.pre));
            assert!(snapshot_bit_eq(&parsed_tick.post, &tick.post));
            assert_eq!(parsed_tick.weapon_def_index, tick.weapon_def_index);
            assert_eq!(parsed_tick.num_subtick, tick.num_subtick);
        }
        assert!(subtick_bit_eq(&parsed.subticks[0], &rec.subticks[0]));
        assert_eq!(
            parsed.ticks[0].pre.origin[1].to_bits(),
            (-0.0_f32).to_bits()
        );
        assert_eq!(
            parsed.subticks[0].analog_forward.to_bits(),
            (-0.0_f32).to_bits()
        );
    }

    #[test]
    fn rec_reader_defaults_v4_play_start_to_zero() {
        let rec = sample_rec();
        let body = build_body(&rec).unwrap();
        let bytes = test_file_bytes_for_version(
            &body,
            4,
            0,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            CODEC_BROTLI,
            None,
        );

        let parsed = read_rec(&mut &bytes[..]).unwrap();

        assert_eq!(parsed.header.version, 4);
        assert_eq!(parsed.header.play_start_tick_index, 0);
        assert_eq!(parsed.projectiles.len(), rec.projectiles.len());
    }

    #[test]
    fn rec_reader_rejects_out_of_range_play_start() {
        let mut bytes = encoded_sample_rec();
        let offset = play_start_offset();
        bytes[offset..offset + 4].copy_from_slice(&99_u32.to_le_bytes());

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("play_start_tick_index 99 out of range for 2 ticks"));
    }

    #[test]
    fn rec_v5_header_does_not_change_body_length() {
        let rec = sample_rec();
        let body = build_body(&rec).unwrap();
        let bytes = encoded_sample_rec();
        let (_, body_len_offset, _) = rec_header_offsets(&bytes);
        let body_uncompressed_len = u64::from_le_bytes(
            bytes[body_len_offset..body_len_offset + 8]
                .try_into()
                .unwrap(),
        );

        assert_eq!(body_uncompressed_len, body.len() as u64);
        assert_eq!(
            body.len(),
            expected_body_len(rec.ticks.len(), rec.subticks.len(), rec.projectiles.len()).unwrap()
        );
    }

    #[test]
    fn rec_writer_rejects_discontinuous_snapshot_chain() {
        let mut rec = sample_rec();
        rec.ticks[1].pre.origin[0] += 1.0;
        let mut bytes = Vec::new();
        let err = write_rec(&mut bytes, &rec).unwrap_err();
        assert!(err
            .to_string()
            .contains("discontinuous snapshot chain between ticks 0 and 1"));
    }

    #[test]
    fn rec_writer_rejects_count_overflow() {
        assert_eq!(
            checked_u32_count("tick count", u32::MAX as usize).unwrap(),
            u32::MAX
        );

        let Ok(too_large) = usize::try_from(u64::from(u32::MAX) + 1) else {
            return;
        };
        let err = checked_u32_count("tick count", too_large).unwrap_err();
        assert!(err.to_string().contains("tick count too large"));
    }

    #[test]
    fn rec_reader_rejects_bad_magic() {
        let err = read_rec(&mut &b"BAD"[..]).unwrap_err();
        assert!(err.to_string().contains("failed to fill whole buffer"));
    }

    #[test]
    fn rec_reader_rejects_unsupported_version() {
        let mut bytes = encoded_sample_rec();
        bytes[8..12].copy_from_slice(&2_u32.to_le_bytes());
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err.to_string().contains("unsupported version 2"));
    }

    #[test]
    fn rec_reader_rejects_unknown_codec() {
        let mut bytes = encoded_sample_rec();
        let (codec_offset, _, _) = rec_header_offsets(&bytes);
        bytes[codec_offset] = 9;
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err.to_string().contains("unsupported codec 9"));
    }

    #[test]
    fn rec_reader_rejects_body_length_mismatch() {
        let mut bytes = encoded_sample_rec();
        let (_, body_len_offset, _) = rec_header_offsets(&bytes);
        bytes[body_len_offset..body_len_offset + 8].copy_from_slice(&999_u64.to_le_bytes());
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err.to_string().contains("body length 999 != expected"));
    }

    fn sample_rec() -> Cs2Rec {
        let s0 = MovementSnapshot {
            origin: [1.0, -0.0, 3.0],
            velocity: [10.0, 20.0, 30.0],
            angles: [4.0, 90.0, -0.0],
            entity_flags: 1,
            move_type: 2,
            buttons: 33,
            buttons1: 1,
            buttons2: 2,
            duck_amount: 1.0,
            duck_speed: 8.0,
            ladder_normal: [-0.0, 0.0, 1.0],
            ducked: 1,
            ducking: 1,
            desires_duck: 1,
            actual_move_type: 2,
        };
        let s1 = MovementSnapshot {
            origin: [2.0, -0.0, 4.0],
            velocity: [11.0, 21.0, 31.0],
            angles: [5.0, 91.0, -0.0],
            buttons: 65,
            duck_amount: 0.5,
            duck_speed: 7.0,
            ..s0.clone()
        };
        let s2 = MovementSnapshot {
            origin: [3.0, -0.0, 5.0],
            velocity: [12.0, 22.0, 32.0],
            angles: [6.0, 92.0, -0.0],
            buttons: 129,
            duck_amount: 0.0,
            duck_speed: 0.0,
            ducked: 0,
            ducking: 0,
            desires_duck: 0,
            ..s1.clone()
        };
        Cs2Rec {
            header: Cs2RecHeader {
                version: DTR_FORMAT_VERSION,
                tick_rate: 64.0,
                map: "de_mirage".to_string(),
                round: 7,
                side: 2,
                steam_id: 76561198000000000,
                player_name: "player".to_string(),
                flags: 0,
                play_start_tick_index: 0,
            },
            ticks: vec![
                ReplayTick {
                    pre: s0,
                    post: s1.clone(),
                    weapon_def_index: 7,
                    num_subtick: 1,
                },
                ReplayTick {
                    pre: s1,
                    post: s2,
                    weapon_def_index: 9,
                    num_subtick: 0,
                },
            ],
            projectiles: vec![ReplayProjectile {
                tick_index: 1,
                kind: ProjectileKind::Smoke,
                weapon_def_index: 45,
                initial_position: [10.0, 20.0, 30.0],
                initial_velocity: [100.0, 200.0, 300.0],
                detonation_position: [40.0, 50.0, 60.0],
            }],
            subticks: vec![SubtickMove {
                when: 0.5,
                button: 1,
                pressed: 1.0,
                analog_forward: -0.0,
                analog_left: 0.0,
                pitch_delta: 0.0,
                yaw_delta: 1.25,
            }],
        }
    }

    fn encoded_sample_rec() -> Vec<u8> {
        let mut bytes = Vec::new();
        write_rec(&mut bytes, &sample_rec()).unwrap();
        bytes
    }

    fn test_file_bytes(
        body: &[u8],
        tick_count: usize,
        subtick_count: usize,
        projectile_count: usize,
        codec: u8,
        body_len: Option<u64>,
    ) -> Vec<u8> {
        test_file_bytes_for_version(
            body,
            DTR_FORMAT_VERSION,
            0,
            tick_count,
            subtick_count,
            projectile_count,
            codec,
            body_len,
        )
    }

    fn test_file_bytes_for_version(
        body: &[u8],
        version: u32,
        play_start_tick_index: u32,
        tick_count: usize,
        subtick_count: usize,
        projectile_count: usize,
        codec: u8,
        body_len: Option<u64>,
    ) -> Vec<u8> {
        let compressed = compress_body(body).unwrap();
        let mut bytes = Vec::new();
        bytes.write_all(MAGIC).unwrap();
        write_u32(&mut bytes, version).unwrap();
        write_f32(&mut bytes, 64.0).unwrap();
        write_u32(&mut bytes, 7).unwrap();
        write_u8(&mut bytes, 2).unwrap();
        write_u32(&mut bytes, 0).unwrap();
        write_u64(&mut bytes, 76561198000000000).unwrap();
        write_u32(&mut bytes, tick_count as u32).unwrap();
        write_u32(&mut bytes, subtick_count as u32).unwrap();
        if version >= 4 {
            write_u32(&mut bytes, projectile_count as u32).unwrap();
        }
        if version >= 5 {
            write_u32(&mut bytes, play_start_tick_index).unwrap();
        }
        write_string(&mut bytes, "de_mirage").unwrap();
        write_string(&mut bytes, "player").unwrap();
        write_u8(&mut bytes, codec).unwrap();
        write_u64(&mut bytes, body_len.unwrap_or(body.len() as u64)).unwrap();
        write_u64(&mut bytes, compressed.len() as u64).unwrap();
        bytes.write_all(&compressed).unwrap();
        bytes
    }

    fn play_start_offset() -> usize {
        8 + 4 + 4 + 4 + 1 + 4 + 8 + 4 + 4 + 4
    }

    fn rec_header_offsets(bytes: &[u8]) -> (usize, usize, usize) {
        let version = u32::from_le_bytes(bytes[8..12].try_into().unwrap());
        let mut offset = 8 + 4 + 4 + 4 + 1 + 4 + 8 + 4 + 4;
        if version >= 4 {
            offset += 4;
        }
        if version >= 5 {
            offset += 4;
        }
        let map_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + map_len;
        let player_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + player_len;
        (offset, offset + 1, offset + 9)
    }

    fn subtick_bit_eq(a: &SubtickMove, b: &SubtickMove) -> bool {
        a.when.to_bits() == b.when.to_bits()
            && a.button == b.button
            && a.pressed.to_bits() == b.pressed.to_bits()
            && a.analog_forward.to_bits() == b.analog_forward.to_bits()
            && a.analog_left.to_bits() == b.analog_left.to_bits()
            && a.pitch_delta.to_bits() == b.pitch_delta.to_bits()
            && a.yaw_delta.to_bits() == b.yaw_delta.to_bits()
    }
}
