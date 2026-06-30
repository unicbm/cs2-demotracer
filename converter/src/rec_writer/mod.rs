use crate::model::{
    Cs2Rec, Cs2RecHeader, HighFidelityMetadata, MovementSnapshot, ProjectileKind,
    ReplayCommandFrame, ReplayMovementExtra, ReplayProjectile, ReplayTick, SubtickMove,
    DTR_FORMAT_VERSION,
};
use crate::{io_error, Error, Result};
use std::fs::File;
use std::io::{BufReader, BufWriter, Cursor, Read, Write};
use std::path::Path;

const MAGIC: &[u8; 8] = b"CSDTRREC";
const CODEC_NONE: u8 = 0;
const CODEC_BROTLI: u8 = 1;
const BROTLI_BUFFER_SIZE: usize = 4096;
const BROTLI_QUALITY: u32 = 6;
const BROTLI_LGWIN: u32 = 22;
const SNAPSHOT_BYTE_SIZE: usize = 92;
const TICK_METADATA_BYTE_SIZE: usize = 8;
const PROJECTILE_BYTE_SIZE: usize = 48;
const SUBTICK_BYTE_SIZE: usize = 28;
const COMMAND_FRAME_BYTE_SIZE: usize = 68;
const MOVEMENT_EXTRA_BYTE_SIZE: usize = 48;

const SECTION_SNAPSHOTS: u32 = 1;
const SECTION_TICK_METADATA: u32 = 2;
const SECTION_PROJECTILES: u32 = 3;
const SECTION_HIGH_FIDELITY_JSON: u32 = 4;
const SECTION_SUBTICKS: u32 = 5;
const SECTION_COMMAND_FRAMES: u32 = 6;
const SECTION_MOVEMENT_EXTRAS: u32 = 7;
const SECTION_VERSION_V1: u32 = 1;

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
    validate_optional_tick_aligned_count(
        "command frame count",
        rec.command_frames.len(),
        rec.ticks.len(),
    )?;
    validate_optional_tick_aligned_count(
        "movement extra count",
        rec.movement_extras.len(),
        rec.ticks.len(),
    )?;
    let tick_count = checked_u32_count("tick count", rec.ticks.len())?;
    let subtick_count = checked_u32_count("subtick count", rec.subticks.len())?;
    let projectile_count = checked_u32_count("projectile count", rec.projectiles.len())?;
    let metadata_json = optional_metadata_json_bytes(&rec.high_fidelity)?;
    let metadata_json_len = checked_u32_count("metadata json length", metadata_json.len())?;

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
    write_u32(writer, metadata_json_len)?;
    write_string(writer, &rec.header.map)?;
    write_string(writer, &rec.header.player_name)?;
    write_v7_sections(writer, rec, &metadata_json)?;

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
    let metadata_json_len = if version >= 6 {
        read_u32(reader)? as usize
    } else {
        0
    };
    validate_play_start_tick(tick_count, play_start_tick_index)?;
    let map = read_string(reader)?;
    let player_name = read_string(reader)?;
    if version >= 7 {
        let (ticks, projectiles, high_fidelity, subticks, command_frames, movement_extras) =
            read_v7_sections(
                reader,
                tick_count,
                subtick_count,
                projectile_count,
                metadata_json_len,
            )?;

        return Ok(Cs2Rec {
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
            high_fidelity,
            subticks,
            command_frames,
            movement_extras,
        });
    }

    let codec = read_u8(reader)?;
    if codec != CODEC_BROTLI {
        return Err(Error::InvalidRec(format!("unsupported codec {codec}")));
    }

    let body_uncompressed_len = checked_len(read_u64(reader)?, "body_uncompressed_len")?;
    let body_compressed_len = checked_len(read_u64(reader)?, "body_compressed_len")?;
    let expected_body_len = expected_body_len(
        tick_count,
        subtick_count,
        projectile_count,
        metadata_json_len,
    )?;
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
    let (ticks, projectiles, high_fidelity, subticks) = read_body(
        &body,
        tick_count,
        projectile_count,
        metadata_json_len,
        subtick_count,
    )?;

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
        high_fidelity,
        subticks,
        command_frames: Vec::new(),
        movement_extras: Vec::new(),
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

fn validate_optional_tick_aligned_count(name: &str, count: usize, tick_count: usize) -> Result<()> {
    if count != 0 && count != tick_count {
        return Err(Error::InvalidRec(format!(
            "{name} {count} must be 0 or match tick count {tick_count}"
        )));
    }
    Ok(())
}

fn write_v7_sections<W: Write>(writer: &mut W, rec: &Cs2Rec, metadata_json: &[u8]) -> Result<()> {
    let mut sections = Vec::new();
    sections.push((
        SECTION_SNAPSHOTS,
        if rec.ticks.is_empty() {
            0
        } else {
            rec.ticks.len() + 1
        },
        build_snapshot_section(rec)?,
    ));
    sections.push((
        SECTION_TICK_METADATA,
        rec.ticks.len(),
        build_tick_metadata_section(rec)?,
    ));
    sections.push((
        SECTION_SUBTICKS,
        rec.subticks.len(),
        build_subtick_section(rec)?,
    ));
    if !rec.projectiles.is_empty() {
        sections.push((
            SECTION_PROJECTILES,
            rec.projectiles.len(),
            build_projectile_section(rec)?,
        ));
    }
    if !metadata_json.is_empty() {
        sections.push((SECTION_HIGH_FIDELITY_JSON, 1, metadata_json.to_vec()));
    }
    if !rec.command_frames.is_empty() {
        sections.push((
            SECTION_COMMAND_FRAMES,
            rec.command_frames.len(),
            build_command_frame_section(rec)?,
        ));
    }
    if !rec.movement_extras.is_empty() {
        sections.push((
            SECTION_MOVEMENT_EXTRAS,
            rec.movement_extras.len(),
            build_movement_extra_section(rec)?,
        ));
    }

    write_u32(writer, checked_u32_count("section count", sections.len())?)?;
    for (section_id, element_count, payload) in sections {
        write_section(writer, section_id, element_count, &payload)?;
    }
    Ok(())
}

fn write_section<W: Write>(
    writer: &mut W,
    section_id: u32,
    element_count: usize,
    payload: &[u8],
) -> Result<()> {
    let compressed = compress_body(payload)?;
    write_u32(writer, section_id)?;
    write_u32(writer, SECTION_VERSION_V1)?;
    write_u8(writer, CODEC_BROTLI)?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    write_u32(writer, 0)?;
    write_u32(
        writer,
        checked_u32_count("section element count", element_count)?,
    )?;
    write_u64(writer, payload.len() as u64)?;
    write_u64(writer, compressed.len() as u64)?;
    writer
        .write_all(&compressed)
        .map_err(|e| Error::InvalidRec(e.to_string()))
}

fn build_snapshot_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let snapshot_count = if rec.ticks.is_empty() {
        0
    } else {
        rec.ticks.len() + 1
    };
    let mut body = Vec::with_capacity(snapshot_count * SNAPSHOT_BYTE_SIZE);
    if let Some(first) = rec.ticks.first() {
        write_snapshot(&mut body, &first.pre)?;
        for tick in &rec.ticks {
            write_snapshot(&mut body, &tick.post)?;
        }
    }
    Ok(body)
}

fn build_tick_metadata_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.ticks.len() * TICK_METADATA_BYTE_SIZE);
    for tick in &rec.ticks {
        write_i32(&mut body, tick.weapon_def_index)?;
        write_u32(&mut body, tick.num_subtick)?;
    }
    Ok(body)
}

fn build_projectile_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.projectiles.len() * PROJECTILE_BYTE_SIZE);
    for projectile in &rec.projectiles {
        write_projectile(&mut body, projectile)?;
    }
    Ok(body)
}

fn build_subtick_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.subticks.len() * SUBTICK_BYTE_SIZE);
    for subtick in &rec.subticks {
        write_subtick(&mut body, subtick)?;
    }
    Ok(body)
}

fn build_command_frame_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.command_frames.len() * COMMAND_FRAME_BYTE_SIZE);
    for frame in &rec.command_frames {
        write_command_frame(&mut body, frame)?;
    }
    Ok(body)
}

fn build_movement_extra_section(rec: &Cs2Rec) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(rec.movement_extras.len() * MOVEMENT_EXTRA_BYTE_SIZE);
    for extra in &rec.movement_extras {
        write_movement_extra(&mut body, extra)?;
    }
    Ok(body)
}

#[cfg(test)]
fn build_body(rec: &Cs2Rec, metadata_json: &[u8]) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(expected_body_len(
        rec.ticks.len(),
        rec.subticks.len(),
        rec.projectiles.len(),
        metadata_json.len(),
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
    body.write_all(metadata_json)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    for subtick in &rec.subticks {
        write_subtick(&mut body, subtick)?;
    }
    Ok(body)
}

fn read_body(
    body: &[u8],
    tick_count: usize,
    projectile_count: usize,
    metadata_json_len: usize,
    subtick_count: usize,
) -> Result<(
    Vec<ReplayTick>,
    Vec<ReplayProjectile>,
    HighFidelityMetadata,
    Vec<SubtickMove>,
)> {
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

    let high_fidelity = if metadata_json_len > 0 {
        let mut metadata_json = vec![0_u8; metadata_json_len];
        reader
            .read_exact(&mut metadata_json)
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
        serde_json::from_slice(&metadata_json)
            .map_err(|e| Error::InvalidRec(format!("invalid high_fidelity metadata JSON: {e}")))?
    } else {
        HighFidelityMetadata::default()
    };

    let mut subticks = Vec::with_capacity(subtick_count);
    for _ in 0..subtick_count {
        subticks.push(read_subtick(&mut reader)?);
    }
    if reader.position() != body.len() as u64 {
        return Err(Error::InvalidRec("trailing bytes in .dtr body".to_string()));
    }
    Ok((ticks, projectiles, high_fidelity, subticks))
}

type V7Sections = (
    Vec<ReplayTick>,
    Vec<ReplayProjectile>,
    HighFidelityMetadata,
    Vec<SubtickMove>,
    Vec<ReplayCommandFrame>,
    Vec<ReplayMovementExtra>,
);

fn read_v7_sections<R: Read>(
    reader: &mut R,
    tick_count: usize,
    subtick_count: usize,
    projectile_count: usize,
    metadata_json_len: usize,
) -> Result<V7Sections> {
    let section_count = checked_len(read_u32(reader)? as u64, "section_count")?;
    let snapshot_count = if tick_count == 0 { 0 } else { tick_count + 1 };

    let mut snapshots: Option<Vec<MovementSnapshot>> = None;
    let mut tick_metadata: Option<Vec<(i32, u32)>> = None;
    let mut subticks: Option<Vec<SubtickMove>> = None;
    let mut projectiles: Option<Vec<ReplayProjectile>> = None;
    let mut high_fidelity = HighFidelityMetadata::default();
    let mut saw_high_fidelity = false;
    let mut saw_command_frames = false;
    let mut saw_movement_extras = false;
    let mut command_frames = Vec::new();
    let mut movement_extras = Vec::new();

    for _ in 0..section_count {
        let header = read_section_header(reader)?;
        if !is_known_section(header.section_id) {
            skip_exact(reader, header.compressed_len)?;
            continue;
        }
        let compressed = read_exact_vec(reader, header.compressed_len)?;
        let body = decode_section_body(&compressed, header.codec, header.uncompressed_len)?;

        match header.section_id {
            SECTION_SNAPSHOTS => {
                reject_duplicate(snapshots.is_some(), "snapshots")?;
                require_section_shape(
                    "snapshots",
                    header.section_version,
                    header.element_count,
                    snapshot_count,
                    body.len(),
                    snapshot_count * SNAPSHOT_BYTE_SIZE,
                )?;
                snapshots = Some(read_snapshots_from_section(&body, snapshot_count)?);
            }
            SECTION_TICK_METADATA => {
                reject_duplicate(tick_metadata.is_some(), "tick metadata")?;
                require_section_shape(
                    "tick metadata",
                    header.section_version,
                    header.element_count,
                    tick_count,
                    body.len(),
                    tick_count * TICK_METADATA_BYTE_SIZE,
                )?;
                tick_metadata = Some(read_tick_metadata_from_section(&body, tick_count)?);
            }
            SECTION_SUBTICKS => {
                reject_duplicate(subticks.is_some(), "subticks")?;
                require_section_shape(
                    "subticks",
                    header.section_version,
                    header.element_count,
                    subtick_count,
                    body.len(),
                    subtick_count * SUBTICK_BYTE_SIZE,
                )?;
                subticks = Some(read_subticks_from_section(&body, subtick_count)?);
            }
            SECTION_PROJECTILES => {
                reject_duplicate(projectiles.is_some(), "projectiles")?;
                require_section_shape(
                    "projectiles",
                    header.section_version,
                    header.element_count,
                    projectile_count,
                    body.len(),
                    projectile_count * PROJECTILE_BYTE_SIZE,
                )?;
                projectiles = Some(read_projectiles_from_section(&body, projectile_count)?);
            }
            SECTION_HIGH_FIDELITY_JSON => {
                reject_duplicate(saw_high_fidelity, "high fidelity metadata")?;
                require_section_shape(
                    "high fidelity metadata",
                    header.section_version,
                    header.element_count,
                    if metadata_json_len == 0 { 0 } else { 1 },
                    body.len(),
                    metadata_json_len,
                )?;
                high_fidelity = serde_json::from_slice(&body).map_err(|e| {
                    Error::InvalidRec(format!("invalid high_fidelity metadata JSON: {e}"))
                })?;
                saw_high_fidelity = true;
            }
            SECTION_COMMAND_FRAMES => {
                reject_duplicate(saw_command_frames, "command frames")?;
                require_section_shape(
                    "command frames",
                    header.section_version,
                    header.element_count,
                    tick_count,
                    body.len(),
                    tick_count * COMMAND_FRAME_BYTE_SIZE,
                )?;
                command_frames = read_command_frames_from_section(&body, tick_count)?;
                saw_command_frames = true;
            }
            SECTION_MOVEMENT_EXTRAS => {
                reject_duplicate(saw_movement_extras, "movement extras")?;
                require_section_shape(
                    "movement extras",
                    header.section_version,
                    header.element_count,
                    tick_count,
                    body.len(),
                    tick_count * MOVEMENT_EXTRA_BYTE_SIZE,
                )?;
                movement_extras = read_movement_extras_from_section(&body, tick_count)?;
                saw_movement_extras = true;
            }
            _ => unreachable!(),
        }
    }

    let snapshots = snapshots
        .ok_or_else(|| Error::InvalidRec("missing required v7 section snapshots".to_string()))?;
    let tick_metadata = tick_metadata.ok_or_else(|| {
        Error::InvalidRec("missing required v7 section tick metadata".to_string())
    })?;
    let subticks = subticks
        .ok_or_else(|| Error::InvalidRec("missing required v7 section subticks".to_string()))?;
    let projectiles = projectiles.unwrap_or_default();
    if projectiles.len() != projectile_count {
        return Err(Error::InvalidRec(format!(
            "projectile section count {} != header projectile count {projectile_count}",
            projectiles.len()
        )));
    }
    if metadata_json_len > 0 && !saw_high_fidelity {
        return Err(Error::InvalidRec(
            "missing high fidelity metadata section".to_string(),
        ));
    }

    let mut expected_subticks = 0_usize;
    let mut ticks = Vec::with_capacity(tick_count);
    for i in 0..tick_count {
        let (weapon_def_index, num_subtick) = tick_metadata[i];
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

    Ok((
        ticks,
        projectiles,
        high_fidelity,
        subticks,
        command_frames,
        movement_extras,
    ))
}

struct SectionHeader {
    section_id: u32,
    section_version: u32,
    codec: u8,
    element_count: usize,
    uncompressed_len: usize,
    compressed_len: usize,
}

fn read_section_header<R: Read>(reader: &mut R) -> Result<SectionHeader> {
    let section_id = read_u32(reader)?;
    let section_version = read_u32(reader)?;
    let codec = read_u8(reader)?;
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    let _flags = read_u32(reader)?;
    let element_count = checked_len(read_u32(reader)? as u64, "section element count")?;
    let uncompressed_len = checked_len(read_u64(reader)?, "section uncompressed length")?;
    let compressed_len = checked_len(read_u64(reader)?, "section compressed length")?;
    Ok(SectionHeader {
        section_id,
        section_version,
        codec,
        element_count,
        uncompressed_len,
        compressed_len,
    })
}

fn is_known_section(section_id: u32) -> bool {
    matches!(
        section_id,
        SECTION_SNAPSHOTS
            | SECTION_TICK_METADATA
            | SECTION_PROJECTILES
            | SECTION_HIGH_FIDELITY_JSON
            | SECTION_SUBTICKS
            | SECTION_COMMAND_FRAMES
            | SECTION_MOVEMENT_EXTRAS
    )
}

fn decode_section_body(compressed: &[u8], codec: u8, expected_len: usize) -> Result<Vec<u8>> {
    match codec {
        CODEC_NONE => {
            if compressed.len() != expected_len {
                return Err(Error::InvalidRec(format!(
                    "uncompressed section length {} != expected {expected_len}",
                    compressed.len()
                )));
            }
            Ok(compressed.to_vec())
        }
        CODEC_BROTLI => decompress_body(compressed, expected_len),
        _ => Err(Error::InvalidRec(format!(
            "unsupported v7 section codec {codec}"
        ))),
    }
}

fn require_section_shape(
    name: &str,
    section_version: u32,
    element_count: usize,
    expected_elements: usize,
    byte_len: usize,
    expected_byte_len: usize,
) -> Result<()> {
    if section_version != SECTION_VERSION_V1 {
        return Err(Error::InvalidRec(format!(
            "unsupported {name} section version {section_version}"
        )));
    }
    if element_count != expected_elements {
        return Err(Error::InvalidRec(format!(
            "{name} element count {element_count} != expected {expected_elements}"
        )));
    }
    if byte_len != expected_byte_len {
        return Err(Error::InvalidRec(format!(
            "{name} byte length {byte_len} != expected {expected_byte_len}"
        )));
    }
    Ok(())
}

fn reject_duplicate(duplicate: bool, name: &str) -> Result<()> {
    if duplicate {
        return Err(Error::InvalidRec(format!("duplicate v7 section {name}")));
    }
    Ok(())
}

fn read_snapshots_from_section(body: &[u8], count: usize) -> Result<Vec<MovementSnapshot>> {
    let mut reader = Cursor::new(body);
    let mut snapshots = Vec::with_capacity(count);
    for _ in 0..count {
        snapshots.push(read_snapshot(&mut reader)?);
    }
    require_no_trailing(&reader, body, "snapshots")?;
    Ok(snapshots)
}

fn read_tick_metadata_from_section(body: &[u8], count: usize) -> Result<Vec<(i32, u32)>> {
    let mut reader = Cursor::new(body);
    let mut metadata = Vec::with_capacity(count);
    for _ in 0..count {
        metadata.push((read_i32(&mut reader)?, read_u32(&mut reader)?));
    }
    require_no_trailing(&reader, body, "tick metadata")?;
    Ok(metadata)
}

fn read_projectiles_from_section(body: &[u8], count: usize) -> Result<Vec<ReplayProjectile>> {
    let mut reader = Cursor::new(body);
    let mut projectiles = Vec::with_capacity(count);
    for _ in 0..count {
        projectiles.push(read_projectile(&mut reader)?);
    }
    require_no_trailing(&reader, body, "projectiles")?;
    Ok(projectiles)
}

fn read_subticks_from_section(body: &[u8], count: usize) -> Result<Vec<SubtickMove>> {
    let mut reader = Cursor::new(body);
    let mut subticks = Vec::with_capacity(count);
    for _ in 0..count {
        subticks.push(read_subtick(&mut reader)?);
    }
    require_no_trailing(&reader, body, "subticks")?;
    Ok(subticks)
}

fn read_command_frames_from_section(body: &[u8], count: usize) -> Result<Vec<ReplayCommandFrame>> {
    let mut reader = Cursor::new(body);
    let mut frames = Vec::with_capacity(count);
    for _ in 0..count {
        frames.push(read_command_frame(&mut reader)?);
    }
    require_no_trailing(&reader, body, "command frames")?;
    Ok(frames)
}

fn read_movement_extras_from_section(
    body: &[u8],
    count: usize,
) -> Result<Vec<ReplayMovementExtra>> {
    let mut reader = Cursor::new(body);
    let mut extras = Vec::with_capacity(count);
    for _ in 0..count {
        extras.push(read_movement_extra(&mut reader)?);
    }
    require_no_trailing(&reader, body, "movement extras")?;
    Ok(extras)
}

fn require_no_trailing(reader: &Cursor<&[u8]>, body: &[u8], name: &str) -> Result<()> {
    if reader.position() != body.len() as u64 {
        return Err(Error::InvalidRec(format!(
            "trailing bytes in v7 {name} section"
        )));
    }
    Ok(())
}

fn read_exact_vec<R: Read>(reader: &mut R, len: usize) -> Result<Vec<u8>> {
    let mut bytes = vec![0_u8; len];
    reader
        .read_exact(&mut bytes)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(bytes)
}

fn skip_exact<R: Read>(reader: &mut R, len: usize) -> Result<()> {
    let mut remaining = len;
    let mut buffer = [0_u8; 4096];
    while remaining > 0 {
        let take = remaining.min(buffer.len());
        reader
            .read_exact(&mut buffer[..take])
            .map_err(|e| Error::InvalidRec(e.to_string()))?;
        remaining -= take;
    }
    Ok(())
}

fn metadata_json_bytes(metadata: &HighFidelityMetadata) -> Result<Vec<u8>> {
    serde_json::to_vec(metadata)
        .map_err(|e| Error::InvalidRec(format!("invalid high_fidelity metadata: {e}")))
}

fn optional_metadata_json_bytes(metadata: &HighFidelityMetadata) -> Result<Vec<u8>> {
    if metadata.is_empty() {
        Ok(Vec::new())
    } else {
        metadata_json_bytes(metadata)
    }
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
    metadata_json_len: usize,
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
        .and_then(|value| value.checked_add(metadata_json_len))
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

fn write_command_frame<W: Write>(writer: &mut W, frame: &ReplayCommandFrame) -> Result<()> {
    write_f32(writer, frame.forward_move)?;
    write_f32(writer, frame.left_move)?;
    write_f32(writer, frame.up_move)?;
    write_f32(writer, frame.pitch)?;
    write_f32(writer, frame.yaw)?;
    write_f32(writer, frame.roll)?;
    write_u64(writer, frame.buttons)?;
    write_u64(writer, frame.buttons1)?;
    write_u64(writer, frame.buttons2)?;
    write_i32(writer, frame.mouse_dx)?;
    write_i32(writer, frame.mouse_dy)?;
    write_i32(writer, frame.weapon_select)?;
    write_u32(writer, frame.fields)?;
    write_u8(writer, frame.left_hand_desired)?;
    writer
        .write_all(&[0, 0, 0])
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(())
}

fn read_command_frame<R: Read>(reader: &mut R) -> Result<ReplayCommandFrame> {
    let forward_move = read_f32(reader)?;
    let left_move = read_f32(reader)?;
    let up_move = read_f32(reader)?;
    let pitch = read_f32(reader)?;
    let yaw = read_f32(reader)?;
    let roll = read_f32(reader)?;
    let buttons = read_u64(reader)?;
    let buttons1 = read_u64(reader)?;
    let buttons2 = read_u64(reader)?;
    let mouse_dx = read_i32(reader)?;
    let mouse_dy = read_i32(reader)?;
    let weapon_select = read_i32(reader)?;
    let fields = read_u32(reader)?;
    let left_hand_desired = read_u8(reader)?;
    let mut pad = [0_u8; 3];
    reader
        .read_exact(&mut pad)
        .map_err(|e| Error::InvalidRec(e.to_string()))?;
    Ok(ReplayCommandFrame {
        forward_move,
        left_move,
        up_move,
        pitch,
        yaw,
        roll,
        buttons,
        buttons1,
        buttons2,
        mouse_dx,
        mouse_dy,
        weapon_select,
        fields,
        left_hand_desired,
    })
}

fn write_movement_extra<W: Write>(writer: &mut W, extra: &ReplayMovementExtra) -> Result<()> {
    write_u32(writer, extra.fields)?;
    write_f32(writer, extra.jump_pressed_time)?;
    write_f32(writer, extra.last_duck_time)?;
    write_i32(writer, extra.last_actual_jump_press_tick)?;
    write_f32(writer, extra.last_actual_jump_press_frac)?;
    write_i32(writer, extra.last_usable_jump_press_tick)?;
    write_f32(writer, extra.last_usable_jump_press_frac)?;
    write_i32(writer, extra.last_landed_tick)?;
    write_f32(writer, extra.last_landed_frac)?;
    for value in extra.last_landed_velocity {
        write_f32(writer, value)?;
    }
    Ok(())
}

fn read_movement_extra<R: Read>(reader: &mut R) -> Result<ReplayMovementExtra> {
    let fields = read_u32(reader)?;
    let jump_pressed_time = read_f32(reader)?;
    let last_duck_time = read_f32(reader)?;
    let last_actual_jump_press_tick = read_i32(reader)?;
    let last_actual_jump_press_frac = read_f32(reader)?;
    let last_usable_jump_press_tick = read_i32(reader)?;
    let last_usable_jump_press_frac = read_f32(reader)?;
    let last_landed_tick = read_i32(reader)?;
    let last_landed_frac = read_f32(reader)?;
    let mut last_landed_velocity = [0.0_f32; 3];
    for value in &mut last_landed_velocity {
        *value = read_f32(reader)?;
    }
    Ok(ReplayMovementExtra {
        fields,
        jump_pressed_time,
        last_duck_time,
        last_actual_jump_press_tick,
        last_actual_jump_press_frac,
        last_usable_jump_press_tick,
        last_usable_jump_press_frac,
        last_landed_tick,
        last_landed_frac,
        last_landed_velocity,
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
        Cs2RecHeader, HighFidelityMetadata, MovementSnapshot, ProjectileKind, ReplayCommandFrame,
        ReplayMovementExtra, ReplayProjectile, ReplayTick, SubtickMove, COMMAND_FIELD_FORWARD_MOVE,
        COMMAND_FIELD_LEFT_MOVE, COMMAND_FIELD_MOUSE, COMMAND_FIELD_VIEW_ANGLES,
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
        let metadata_json = metadata_json_bytes(&rec.high_fidelity).unwrap();
        let mut body = build_body(&rec, &metadata_json).unwrap();
        let metadata_offset = SNAPSHOT_BYTE_SIZE * (rec.ticks.len() + 1);
        body[metadata_offset + 4..metadata_offset + 8].copy_from_slice(&2_u32.to_le_bytes());
        let bytes = test_file_bytes(
            &body,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            metadata_json.len(),
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
        assert_eq!(parsed.high_fidelity, rec.high_fidelity);
        assert_eq!(parsed.subticks.len(), rec.subticks.len());
        assert_eq!(parsed.command_frames, rec.command_frames);
        assert_eq!(parsed.movement_extras, rec.movement_extras);
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
    fn rec_v7_reader_skips_unknown_sections() {
        let mut bytes = encoded_sample_rec();
        let section_count_offset = v7_section_count_offset(&bytes);
        let section_count = u32::from_le_bytes(
            bytes[section_count_offset..section_count_offset + 4]
                .try_into()
                .unwrap(),
        );
        bytes[section_count_offset..section_count_offset + 4]
            .copy_from_slice(&(section_count + 1).to_le_bytes());
        let mut unknown = Vec::new();
        write_u32(&mut unknown, 999).unwrap();
        write_u32(&mut unknown, SECTION_VERSION_V1).unwrap();
        write_u8(&mut unknown, CODEC_NONE).unwrap();
        unknown.write_all(&[0, 0, 0]).unwrap();
        write_u32(&mut unknown, 0).unwrap();
        write_u32(&mut unknown, 1).unwrap();
        write_u64(&mut unknown, 4).unwrap();
        write_u64(&mut unknown, 4).unwrap();
        unknown.write_all(&[1, 2, 3, 4]).unwrap();
        let insert_at = section_count_offset + 4;
        bytes.splice(insert_at..insert_at, unknown);

        let parsed = read_rec(&mut &bytes[..]).unwrap();

        assert_eq!(
            parsed.command_frames.len(),
            sample_rec().command_frames.len()
        );
    }

    #[test]
    fn rec_v7_reader_rejects_missing_required_section() {
        let mut bytes = encoded_sample_rec();
        let section_count_offset = v7_section_count_offset(&bytes);
        bytes[section_count_offset..section_count_offset + 4].copy_from_slice(&0_u32.to_le_bytes());

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("missing required v7 section snapshots"));
    }

    #[test]
    fn rec_v7_reader_rejects_duplicate_command_frames_section() {
        let rec = sample_rec();
        let mut bytes = encoded_sample_rec();
        append_duplicate_v7_section(
            &mut bytes,
            SECTION_COMMAND_FRAMES,
            rec.command_frames.len(),
            &build_command_frame_section(&rec).unwrap(),
        );

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("duplicate v7 section command frames"));
    }

    #[test]
    fn rec_v7_reader_rejects_duplicate_movement_extras_section() {
        let mut rec = sample_rec();
        rec.movement_extras = vec![ReplayMovementExtra::default(); rec.ticks.len()];
        let mut bytes = Vec::new();
        write_rec(&mut bytes, &rec).unwrap();
        append_duplicate_v7_section(
            &mut bytes,
            SECTION_MOVEMENT_EXTRAS,
            rec.movement_extras.len(),
            &build_movement_extra_section(&rec).unwrap(),
        );

        let err = read_rec(&mut &bytes[..]).unwrap_err();

        assert!(err
            .to_string()
            .contains("duplicate v7 section movement extras"));
    }

    #[test]
    fn rec_reader_defaults_v4_play_start_to_zero() {
        let rec = sample_rec();
        let body = build_body(&rec, &[]).unwrap();
        let bytes = test_file_bytes_for_version(
            &body,
            4,
            0,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            0,
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
        let body = build_body(&rec, &metadata_json_bytes(&rec.high_fidelity).unwrap()).unwrap();
        let bytes = encoded_legacy_sample_rec();
        let (_, body_len_offset, _) = rec_header_offsets(&bytes);
        let body_uncompressed_len = u64::from_le_bytes(
            bytes[body_len_offset..body_len_offset + 8]
                .try_into()
                .unwrap(),
        );

        assert_eq!(body_uncompressed_len, body.len() as u64);
        assert_eq!(
            body.len(),
            expected_body_len(
                rec.ticks.len(),
                rec.subticks.len(),
                rec.projectiles.len(),
                metadata_json_bytes(&rec.high_fidelity).unwrap().len(),
            )
            .unwrap()
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
        let mut bytes = encoded_legacy_sample_rec();
        let (codec_offset, _, _) = rec_header_offsets(&bytes);
        bytes[codec_offset] = 9;
        let err = read_rec(&mut &bytes[..]).unwrap_err();
        assert!(err.to_string().contains("unsupported codec 9"));
    }

    #[test]
    fn rec_reader_rejects_body_length_mismatch() {
        let mut bytes = encoded_legacy_sample_rec();
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
            high_fidelity: HighFidelityMetadata::new(
                vec![crate::model::ReplayHifiEvent {
                    tick_index: 1,
                    tick: 101,
                    kind: crate::model::ReplayHifiEventKind::ItemPickup,
                    actor_steam_id: Some(76561198000000000),
                    target_steam_id: None,
                    weapon_def_index: Some(45),
                    item_name: Some("smokegrenade".to_string()),
                    entity_id: None,
                    actor_count_after: None,
                    target_count_after: Some(2),
                    damage: None,
                    health: None,
                }],
                Vec::new(),
            ),
            subticks: vec![SubtickMove {
                when: 0.5,
                button: 1,
                pressed: 1.0,
                analog_forward: -0.0,
                analog_left: 0.0,
                pitch_delta: 0.0,
                yaw_delta: 1.25,
            }],
            command_frames: vec![
                ReplayCommandFrame {
                    forward_move: 1.0,
                    left_move: -1.0,
                    pitch: 4.0,
                    yaw: 90.0,
                    buttons: 33,
                    buttons1: 1,
                    mouse_dx: 3,
                    mouse_dy: -2,
                    fields: COMMAND_FIELD_FORWARD_MOVE
                        | COMMAND_FIELD_LEFT_MOVE
                        | COMMAND_FIELD_VIEW_ANGLES
                        | COMMAND_FIELD_MOUSE,
                    weapon_select: -1,
                    ..ReplayCommandFrame::default()
                },
                ReplayCommandFrame {
                    forward_move: 0.5,
                    left_move: 0.25,
                    pitch: 5.0,
                    yaw: 91.0,
                    buttons: 65,
                    fields: COMMAND_FIELD_FORWARD_MOVE | COMMAND_FIELD_LEFT_MOVE,
                    weapon_select: -1,
                    ..ReplayCommandFrame::default()
                },
            ],
            movement_extras: Vec::new(),
        }
    }

    fn encoded_sample_rec() -> Vec<u8> {
        let mut bytes = Vec::new();
        write_rec(&mut bytes, &sample_rec()).unwrap();
        bytes
    }

    fn encoded_legacy_sample_rec() -> Vec<u8> {
        let rec = sample_rec();
        let metadata_json = metadata_json_bytes(&rec.high_fidelity).unwrap();
        let body = build_body(&rec, &metadata_json).unwrap();
        test_file_bytes_for_version(
            &body,
            6,
            rec.header.play_start_tick_index,
            rec.ticks.len(),
            rec.subticks.len(),
            rec.projectiles.len(),
            metadata_json.len(),
            CODEC_BROTLI,
            None,
        )
    }

    fn test_file_bytes(
        body: &[u8],
        tick_count: usize,
        subtick_count: usize,
        projectile_count: usize,
        metadata_json_len: usize,
        codec: u8,
        body_len: Option<u64>,
    ) -> Vec<u8> {
        test_file_bytes_for_version(
            body,
            6,
            0,
            tick_count,
            subtick_count,
            projectile_count,
            metadata_json_len,
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
        metadata_json_len: usize,
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
        if version >= 6 {
            write_u32(&mut bytes, metadata_json_len as u32).unwrap();
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
        if version >= 6 {
            offset += 4;
        }
        let map_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + map_len;
        let player_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + player_len;
        (offset, offset + 1, offset + 9)
    }

    fn v7_section_count_offset(bytes: &[u8]) -> usize {
        let mut offset = 8 + 4 + 4 + 4 + 1 + 4 + 8 + 4 + 4 + 4 + 4 + 4;
        let map_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + map_len;
        let player_len = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) as usize;
        offset += 2 + player_len;
        offset
    }

    fn append_duplicate_v7_section(
        bytes: &mut Vec<u8>,
        section_id: u32,
        element_count: usize,
        payload: &[u8],
    ) {
        let section_count_offset = v7_section_count_offset(bytes);
        let section_count = u32::from_le_bytes(
            bytes[section_count_offset..section_count_offset + 4]
                .try_into()
                .unwrap(),
        );
        bytes[section_count_offset..section_count_offset + 4]
            .copy_from_slice(&(section_count + 1).to_le_bytes());
        write_section(bytes, section_id, element_count, payload).unwrap();
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
