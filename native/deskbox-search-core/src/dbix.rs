use super::{
    DESKBOX_SEARCH_ENTRY_DIRECTORY, DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED,
    DESKBOX_SEARCH_STATUS_CANCELLED, DESKBOX_SEARCH_STATUS_CORRUPT_DATA,
    DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT, DESKBOX_SEARCH_STATUS_IO_ERROR,
    DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT, MAX_DBIX_FILE_BYTES, MAX_ENTRY_COUNT,
    MAX_UTF16_CHARS, SearchCore, SearchEntry, TextRange,
};
use std::ffi::{OsString, c_void};
use std::fs::{self, File};
use std::io::{BufReader, BufWriter, ErrorKind, Read, Write};
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::PathBuf;
use std::sync::atomic::AtomicBool;
use std::time::{SystemTime, UNIX_EPOCH};

const DBIX_MAGIC: u32 = 0x5849_4244;
const DBIX_VERSION: u32 = 1;
const MAX_DBIX_STRING_BYTES: usize = 1024 * 1024;
const CANCELLATION_CHECK_MASK: usize = 0xFF;

const DOTNET_TICKS_MASK: u64 = 0x3FFF_FFFF_FFFF_FFFF;
const DOTNET_FLAGS_MASK: u64 = 0xC000_0000_0000_0000;
const DOTNET_KIND_UTC: u64 = 0x4000_0000_0000_0000;
const DOTNET_KIND_LOCAL: u64 = 0x8000_0000_0000_0000;
const DOTNET_TICKS_CEILING: i64 = 0x4000_0000_0000_0000;
const DOTNET_TICKS_PER_DAY: i64 = 864_000_000_000;
const DOTNET_MAX_TICKS: i64 = 3_155_378_975_999_999_999;
const DOTNET_UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

const WAIT_OBJECT_0: u32 = 0;
const WAIT_TIMEOUT: u32 = 258;
const WAIT_FAILED: u32 = u32::MAX;
const MOVE_FILE_REPLACE_EXISTING: u32 = 0x1;
const MOVE_FILE_WRITE_THROUGH: u32 = 0x8;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn WaitForSingleObject(handle: *mut c_void, milliseconds: u32) -> u32;
    fn MoveFileExW(existing_file_name: *const u16, new_file_name: *const u16, flags: u32) -> i32;
    #[cfg(test)]
    fn CreateEventW(
        event_attributes: *const c_void,
        manual_reset: i32,
        initial_state: i32,
        name: *const u16,
    ) -> *mut c_void;
    #[cfg(test)]
    fn CloseHandle(handle: *mut c_void) -> i32;
}

pub(super) struct DbixLoadMetadata {
    pub version: u32,
    pub persisted_utc_ticks: i64,
    pub source_file_bytes: u64,
}

pub(super) struct DbixSaveMetadata {
    pub version: u32,
    pub persisted_utc_ticks: i64,
    pub file_bytes: u64,
    pub entry_count: u32,
    pub directory_count: u32,
}

pub(super) fn load_dbix(
    path_utf16: &[u16],
    max_entry_count: usize,
    cancel_event: *mut c_void,
) -> Result<(SearchCore, DbixLoadMetadata), u32> {
    if path_utf16.is_empty()
        || path_utf16.contains(&0)
        || max_entry_count == 0
        || max_entry_count > MAX_ENTRY_COUNT
    {
        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
    }

    check_cancelled(cancel_event)?;
    let path = PathBuf::from(OsString::from_wide(path_utf16));
    let file = File::open(path).map_err(map_open_error)?;
    let source_file_bytes = file.metadata().map_err(map_io_error)?.len();
    if source_file_bytes == 0 || source_file_bytes > MAX_DBIX_FILE_BYTES {
        return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
    }

    let mut reader = BufReader::with_capacity(64 * 1024, file);
    if read_u32(&mut reader)? != DBIX_MAGIC {
        return Err(DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT);
    }
    let version = read_u32(&mut reader)?;
    if version != DBIX_VERSION {
        return Err(DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT);
    }
    let persisted_utc_ticks = read_i64(&mut reader)?;
    if !(0..=DOTNET_MAX_TICKS).contains(&persisted_utc_ticks) {
        return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
    }

    let directory_count = read_nonnegative_count(&mut reader, MAX_ENTRY_COUNT)?;
    let mut directories = Vec::new();
    directories
        .try_reserve_exact(directory_count)
        .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
    let mut directory_utf16 = Vec::new();
    let mut scratch = Vec::new();
    for index in 0..directory_count {
        if index & CANCELLATION_CHECK_MASK == 0 {
            check_cancelled(cancel_event)?;
        }
        let byte_count = read_7_bit_encoded_length(&mut reader)?;
        let range = read_utf8_range(&mut reader, byte_count, &mut scratch, &mut directory_utf16)?;
        directories.push(range);
    }

    let entry_count = read_nonnegative_count(&mut reader, max_entry_count)?;
    if entry_count == 0 {
        return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
    }
    let mut entries = Vec::new();
    entries
        .try_reserve_exact(entry_count)
        .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
    let mut file_name_utf16 = Vec::new();
    for index in 0..entry_count {
        if index & CANCELLATION_CHECK_MASK == 0 {
            check_cancelled(cancel_event)?;
        }
        let directory_id = read_i32(&mut reader)?;
        if directory_id < 0 || directory_id as usize >= directories.len() {
            return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
        }
        let byte_count = read_i32(&mut reader)?;
        if byte_count < 0 {
            return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
        }
        let file_name = read_utf8_range(
            &mut reader,
            byte_count as usize,
            &mut scratch,
            &mut file_name_utf16,
        )?;
        let is_directory = read_u8(&mut reader)? != 0;
        let modified_binary = read_i64(&mut reader)?;
        let modified_utc_ticks = decode_dotnet_datetime_binary(modified_binary)?;
        entries.push(SearchEntry {
            modified_utc_ticks,
            modified_binary,
            directory_id: directory_id as u32,
            file_name_offset: file_name.offset,
            file_name_length: file_name.length,
            scan_generation: 0,
            flags: if is_directory {
                DESKBOX_SEARCH_ENTRY_DIRECTORY
            } else {
                0
            },
            tombstoned: 0,
        });
    }

    check_cancelled(cancel_event)?;
    let mut trailing = [0u8; 1];
    match reader.read(&mut trailing) {
        Ok(0) => {}
        Ok(_) => return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA),
        Err(error) => return Err(map_io_error(error)),
    }

    directories.shrink_to_fit();
    directory_utf16.shrink_to_fit();
    entries.shrink_to_fit();
    file_name_utf16.shrink_to_fit();
    Ok((
        SearchCore {
            entries,
            directories,
            directory_utf16,
            file_name_utf16,
            directory_lookup: None,
            live_entry_count: entry_count,
            sealed: true,
            cancelled: AtomicBool::new(false),
        },
        DbixLoadMetadata {
            version,
            persisted_utc_ticks,
            source_file_bytes,
        },
    ))
}

pub(super) fn save_dbix(
    core: &SearchCore,
    path_utf16: &[u16],
    temp_path_utf16: &[u16],
    cancel_event: *mut c_void,
) -> Result<DbixSaveMetadata, u32> {
    if path_utf16.is_empty()
        || temp_path_utf16.is_empty()
        || path_utf16.contains(&0)
        || temp_path_utf16.contains(&0)
        || path_utf16 == temp_path_utf16
        || !core.sealed
        || core.live_entry_count == 0
    {
        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
    }
    check_cancelled(cancel_event)?;
    let path = PathBuf::from(OsString::from_wide(path_utf16));
    let temp_path = PathBuf::from(OsString::from_wide(temp_path_utf16));
    let persisted_utc_ticks = current_dotnet_utc_ticks()?;
    let mut directory_remap = Vec::new();
    directory_remap
        .try_reserve_exact(core.directories.len())
        .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
    directory_remap.resize(core.directories.len(), u32::MAX);
    let mut live_directory_ids = Vec::new();
    live_directory_ids
        .try_reserve_exact(core.directories.len().min(core.live_entry_count))
        .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
    for (index, entry) in core.entries.iter().enumerate() {
        if entry.tombstoned != 0 {
            continue;
        }
        if index & CANCELLATION_CHECK_MASK == 0 {
            check_cancelled(cancel_event)?;
        }
        let old_id = entry.directory_id as usize;
        if old_id >= directory_remap.len() {
            return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
        }
        if directory_remap[old_id] == u32::MAX {
            directory_remap[old_id] = u32::try_from(live_directory_ids.len())
                .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
            live_directory_ids.push(entry.directory_id);
        }
    }

    let write_result = (|| -> Result<(), u32> {
        let file = File::create(&temp_path).map_err(map_open_error)?;
        let mut writer = BufWriter::with_capacity(64 * 1024, file);
        write_u32(&mut writer, DBIX_MAGIC)?;
        write_u32(&mut writer, DBIX_VERSION)?;
        write_i64(&mut writer, persisted_utc_ticks)?;
        write_i32(
            &mut writer,
            i32::try_from(live_directory_ids.len())
                .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?,
        )?;
        for (index, old_id) in live_directory_ids.iter().copied().enumerate() {
            if index & CANCELLATION_CHECK_MASK == 0 {
                check_cancelled(cancel_event)?;
            }
            write_dotnet_string(&mut writer, core.directory(old_id))?;
        }
        write_i32(
            &mut writer,
            i32::try_from(core.live_entry_count)
                .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?,
        )?;
        let mut written_entries = 0usize;
        for entry in &core.entries {
            if entry.tombstoned != 0 {
                continue;
            }
            if written_entries & CANCELLATION_CHECK_MASK == 0 {
                check_cancelled(cancel_event)?;
            }
            write_i32(
                &mut writer,
                i32::try_from(directory_remap[entry.directory_id as usize])
                    .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?,
            )?;
            write_raw_utf8_string(&mut writer, core.file_name(entry))?;
            writer
                .write_all(&[u8::from(entry.flags & DESKBOX_SEARCH_ENTRY_DIRECTORY != 0)])
                .map_err(map_io_error)?;
            write_i64(&mut writer, entry.modified_binary)?;
            written_entries += 1;
        }
        if written_entries != core.live_entry_count {
            return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
        }
        writer.flush().map_err(map_io_error)?;
        writer.get_ref().sync_all().map_err(map_io_error)?;
        check_cancelled(cancel_event)?;
        Ok(())
    })();
    if let Err(status) = write_result {
        let _ = fs::remove_file(&temp_path);
        return Err(status);
    }

    let mut source_wide: Vec<u16> = temp_path.as_os_str().encode_wide().collect();
    source_wide.push(0);
    let mut target_wide: Vec<u16> = path.as_os_str().encode_wide().collect();
    target_wide.push(0);
    // SAFETY: both buffers are NUL-terminated and remain live for the call.
    if unsafe {
        MoveFileExW(
            source_wide.as_ptr(),
            target_wide.as_ptr(),
            MOVE_FILE_REPLACE_EXISTING | MOVE_FILE_WRITE_THROUGH,
        )
    } == 0
    {
        let _ = fs::remove_file(&temp_path);
        return Err(DESKBOX_SEARCH_STATUS_IO_ERROR);
    }
    let file_bytes = fs::metadata(&path).map_err(map_io_error)?.len();
    Ok(DbixSaveMetadata {
        version: DBIX_VERSION,
        persisted_utc_ticks,
        file_bytes,
        entry_count: core.live_entry_count as u32,
        directory_count: live_directory_ids.len() as u32,
    })
}

fn current_dotnet_utc_ticks() -> Result<i64, u32> {
    let duration = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| DESKBOX_SEARCH_STATUS_IO_ERROR)?;
    let ticks = i128::from(DOTNET_UNIX_EPOCH_TICKS)
        + i128::from(duration.as_secs()) * 10_000_000
        + i128::from(duration.subsec_nanos() / 100);
    i64::try_from(ticks).map_err(|_| DESKBOX_SEARCH_STATUS_IO_ERROR)
}

fn write_dotnet_string<W: Write>(writer: &mut W, value: &[u16]) -> Result<(), u32> {
    let text = String::from_utf16(value).map_err(|_| DESKBOX_SEARCH_STATUS_CORRUPT_DATA)?;
    let bytes = text.as_bytes();
    if bytes.len() > MAX_DBIX_STRING_BYTES {
        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
    }
    write_7_bit_encoded_length(writer, bytes.len())?;
    writer.write_all(bytes).map_err(map_io_error)
}

fn write_raw_utf8_string<W: Write>(writer: &mut W, value: &[u16]) -> Result<(), u32> {
    let text = String::from_utf16(value).map_err(|_| DESKBOX_SEARCH_STATUS_CORRUPT_DATA)?;
    let bytes = text.as_bytes();
    if bytes.is_empty() || bytes.len() > MAX_DBIX_STRING_BYTES {
        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
    }
    write_i32(
        writer,
        i32::try_from(bytes.len()).map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?,
    )?;
    writer.write_all(bytes).map_err(map_io_error)
}

fn write_7_bit_encoded_length<W: Write>(writer: &mut W, length: usize) -> Result<(), u32> {
    let mut value = u32::try_from(length).map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
    while value >= 0x80 {
        writer
            .write_all(&[(value as u8) | 0x80])
            .map_err(map_io_error)?;
        value >>= 7;
    }
    writer.write_all(&[value as u8]).map_err(map_io_error)
}

fn write_i32<W: Write>(writer: &mut W, value: i32) -> Result<(), u32> {
    writer.write_all(&value.to_le_bytes()).map_err(map_io_error)
}

fn write_u32<W: Write>(writer: &mut W, value: u32) -> Result<(), u32> {
    writer.write_all(&value.to_le_bytes()).map_err(map_io_error)
}

fn write_i64<W: Write>(writer: &mut W, value: i64) -> Result<(), u32> {
    writer.write_all(&value.to_le_bytes()).map_err(map_io_error)
}

fn read_utf8_range<R: Read>(
    reader: &mut R,
    byte_count: usize,
    scratch: &mut Vec<u8>,
    destination: &mut Vec<u16>,
) -> Result<TextRange, u32> {
    if byte_count > MAX_DBIX_STRING_BYTES {
        return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
    }
    scratch.clear();
    scratch
        .try_reserve(byte_count)
        .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
    scratch.resize(byte_count, 0);
    read_exact(reader, scratch)?;
    let value = std::str::from_utf8(scratch).map_err(|_| DESKBOX_SEARCH_STATUS_CORRUPT_DATA)?;
    let utf16_length = value.encode_utf16().count();
    if destination.len().saturating_add(utf16_length) > MAX_UTF16_CHARS {
        return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
    }
    destination
        .try_reserve(utf16_length)
        .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
    let offset =
        u32::try_from(destination.len()).map_err(|_| DESKBOX_SEARCH_STATUS_CORRUPT_DATA)?;
    let length = u32::try_from(utf16_length).map_err(|_| DESKBOX_SEARCH_STATUS_CORRUPT_DATA)?;
    destination.extend(value.encode_utf16());
    Ok(TextRange { offset, length })
}

pub(super) fn decode_dotnet_datetime_binary(value: i64) -> Result<i64, u32> {
    let data = value as u64;
    let flags = data & DOTNET_FLAGS_MASK;
    let mut ticks = (data & DOTNET_TICKS_MASK) as i64;
    if data & DOTNET_KIND_LOCAL != 0 {
        if ticks > DOTNET_TICKS_CEILING - DOTNET_TICKS_PER_DAY {
            ticks -= DOTNET_TICKS_CEILING;
        }
        return if (0..=DOTNET_MAX_TICKS).contains(&ticks) {
            Ok(ticks)
        } else {
            Err(DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT)
        };
    }
    if flags == DOTNET_KIND_UTC {
        return if ticks <= DOTNET_MAX_TICKS {
            Ok(ticks)
        } else {
            Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA)
        };
    }
    // Current DeskBox DBIX entries originate from FileInfo.LastWriteTime and
    // are local. Zero is accepted for the existing MinValue sentinel; other
    // unspecified values require timezone conversion and deliberately trigger
    // the managed rebuild fallback instead of silently changing sort order.
    if flags == 0 && ticks == 0 {
        return Ok(0);
    }
    Err(DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT)
}

fn read_nonnegative_count<R: Read>(reader: &mut R, maximum: usize) -> Result<usize, u32> {
    let value = read_i32(reader)?;
    if value < 0 || value as usize > maximum {
        Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA)
    } else {
        Ok(value as usize)
    }
}

fn read_7_bit_encoded_length<R: Read>(reader: &mut R) -> Result<usize, u32> {
    let mut value = 0u32;
    for shift in [0, 7, 14, 21, 28] {
        let byte = read_u8(reader)?;
        if shift == 28 && byte > 0x0F {
            return Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA);
        }
        value |= u32::from(byte & 0x7F) << shift;
        if byte & 0x80 == 0 {
            let length = value as usize;
            return if length <= MAX_DBIX_STRING_BYTES {
                Ok(length)
            } else {
                Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA)
            };
        }
    }
    Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA)
}

fn read_u8<R: Read>(reader: &mut R) -> Result<u8, u32> {
    let mut bytes = [0u8; 1];
    read_exact(reader, &mut bytes)?;
    Ok(bytes[0])
}

fn read_i32<R: Read>(reader: &mut R) -> Result<i32, u32> {
    let mut bytes = [0u8; 4];
    read_exact(reader, &mut bytes)?;
    Ok(i32::from_le_bytes(bytes))
}

fn read_u32<R: Read>(reader: &mut R) -> Result<u32, u32> {
    let mut bytes = [0u8; 4];
    read_exact(reader, &mut bytes)?;
    Ok(u32::from_le_bytes(bytes))
}

fn read_i64<R: Read>(reader: &mut R) -> Result<i64, u32> {
    let mut bytes = [0u8; 8];
    read_exact(reader, &mut bytes)?;
    Ok(i64::from_le_bytes(bytes))
}

fn read_exact<R: Read>(reader: &mut R, buffer: &mut [u8]) -> Result<(), u32> {
    reader.read_exact(buffer).map_err(map_io_error)
}

fn map_open_error(_: std::io::Error) -> u32 {
    DESKBOX_SEARCH_STATUS_IO_ERROR
}

fn map_io_error(error: std::io::Error) -> u32 {
    if error.kind() == ErrorKind::UnexpectedEof {
        DESKBOX_SEARCH_STATUS_CORRUPT_DATA
    } else {
        DESKBOX_SEARCH_STATUS_IO_ERROR
    }
}

fn check_cancelled(cancel_event: *mut c_void) -> Result<(), u32> {
    if cancel_event.is_null() {
        return Ok(());
    }
    // SAFETY: the ABI requires the caller to keep a valid waitable event alive
    // for the duration of the load call.
    match unsafe { WaitForSingleObject(cancel_event, 0) } {
        WAIT_OBJECT_0 => Err(DESKBOX_SEARCH_STATUS_CANCELLED),
        WAIT_TIMEOUT => Ok(()),
        WAIT_FAILED => Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT),
        _ => Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::os::windows::ffi::OsStrExt;
    use std::sync::atomic::{AtomicU64, Ordering};

    static NEXT_FILE_ID: AtomicU64 = AtomicU64::new(1);
    const SAMPLE_TICKS: i64 = 638_900_000_000_000_000;

    #[test]
    fn current_dbix_loads_directly_into_a_sealed_core() {
        let path = write_fixture(valid_dbix(dotnet_utc_binary(SAMPLE_TICKS)));
        let utf16: Vec<u16> = path.as_os_str().encode_wide().collect();
        let (core, metadata) = load_dbix(&utf16, MAX_ENTRY_COUNT, std::ptr::null_mut())
            .expect("valid DBIX should load");

        assert!(core.sealed);
        assert_eq!(core.entries.len(), 2);
        assert_eq!(core.directories.len(), 1);
        assert_eq!(core.entries[0].modified_utc_ticks, SAMPLE_TICKS);
        assert_eq!(metadata.version, DBIX_VERSION);
        assert_eq!(
            metadata.source_file_bytes,
            fs::metadata(&path).unwrap().len()
        );
        let (results, matched, scanned) = core
            .search(&"report".encode_utf16().collect::<Vec<_>>(), 10)
            .unwrap();
        assert_eq!(matched, 1);
        assert_eq!(scanned, 2);
        assert_eq!(results.len(), 1);

        fs::remove_file(path).unwrap();
    }

    #[test]
    fn native_save_round_trips_live_entries_and_original_datetime_binary() {
        let source_path = write_fixture(valid_dbix(dotnet_utc_binary(SAMPLE_TICKS)));
        let source_utf16: Vec<u16> = source_path.as_os_str().encode_wide().collect();
        let (core, _) = load_dbix(&source_utf16, MAX_ENTRY_COUNT, std::ptr::null_mut())
            .expect("source DBIX should load");
        let target_path = write_fixture(Vec::new());
        let temp_path = target_path.with_extension("tmp");
        let target_utf16: Vec<u16> = target_path.as_os_str().encode_wide().collect();
        let temp_utf16: Vec<u16> = temp_path.as_os_str().encode_wide().collect();
        let saved = save_dbix(&core, &target_utf16, &temp_utf16, std::ptr::null_mut())
            .expect("native DBIX save should succeed");
        assert_eq!(saved.entry_count, 2);
        assert!(saved.file_bytes > 0);

        let (round_trip, _) = load_dbix(&target_utf16, MAX_ENTRY_COUNT, std::ptr::null_mut())
            .expect("saved DBIX should reload");
        assert_eq!(round_trip.live_entry_count, 2);
        assert_eq!(
            round_trip.entries[0].modified_binary,
            dotnet_utc_binary(SAMPLE_TICKS)
        );
        assert_eq!(round_trip.entries[0].modified_utc_ticks, SAMPLE_TICKS);

        fs::remove_file(source_path).unwrap();
        fs::remove_file(target_path).unwrap();
        let _ = fs::remove_file(temp_path);
    }

    #[test]
    fn local_datetime_binary_preserves_its_serialized_utc_ticks() {
        let encoded = (SAMPLE_TICKS as u64 | DOTNET_KIND_LOCAL) as i64;
        assert_eq!(decode_dotnet_datetime_binary(encoded), Ok(SAMPLE_TICKS));
    }

    #[test]
    fn unsupported_version_and_unspecified_time_are_explicit() {
        let mut unsupported_version = valid_dbix(dotnet_utc_binary(SAMPLE_TICKS));
        unsupported_version[4..8].copy_from_slice(&2u32.to_le_bytes());
        let version_path = write_fixture(unsupported_version);
        let version_utf16: Vec<u16> = version_path.as_os_str().encode_wide().collect();
        assert!(matches!(
            load_dbix(&version_utf16, MAX_ENTRY_COUNT, std::ptr::null_mut()),
            Err(DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT)
        ));
        fs::remove_file(version_path).unwrap();

        let unspecified_path = write_fixture(valid_dbix(SAMPLE_TICKS));
        let unspecified_utf16: Vec<u16> = unspecified_path.as_os_str().encode_wide().collect();
        assert!(matches!(
            load_dbix(&unspecified_utf16, MAX_ENTRY_COUNT, std::ptr::null_mut()),
            Err(DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT)
        ));
        fs::remove_file(unspecified_path).unwrap();
    }

    #[test]
    fn truncated_and_trailing_data_are_rejected_without_a_handle() {
        let mut truncated = valid_dbix(dotnet_utc_binary(SAMPLE_TICKS));
        truncated.pop();
        let truncated_path = write_fixture(truncated);
        let truncated_utf16: Vec<u16> = truncated_path.as_os_str().encode_wide().collect();
        assert!(matches!(
            load_dbix(&truncated_utf16, MAX_ENTRY_COUNT, std::ptr::null_mut()),
            Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA)
        ));
        fs::remove_file(truncated_path).unwrap();

        let mut trailing = valid_dbix(dotnet_utc_binary(SAMPLE_TICKS));
        trailing.push(0xAA);
        let trailing_path = write_fixture(trailing);
        let trailing_utf16: Vec<u16> = trailing_path.as_os_str().encode_wide().collect();
        assert!(matches!(
            load_dbix(&trailing_utf16, MAX_ENTRY_COUNT, std::ptr::null_mut()),
            Err(DESKBOX_SEARCH_STATUS_CORRUPT_DATA)
        ));
        fs::remove_file(trailing_path).unwrap();
    }

    #[test]
    fn signalled_event_cancels_before_file_io() {
        // SAFETY: the test creates a private unnamed event and closes it after
        // the synchronous cancellation check has returned.
        let event = unsafe { CreateEventW(std::ptr::null(), 1, 1, std::ptr::null()) };
        assert!(!event.is_null());
        let missing_path: Vec<u16> = "Z:\\deskbox-stage-6b-missing.dbix".encode_utf16().collect();
        assert!(matches!(
            load_dbix(&missing_path, MAX_ENTRY_COUNT, event),
            Err(DESKBOX_SEARCH_STATUS_CANCELLED)
        ));
        // SAFETY: event is the live handle returned above and is no longer used.
        assert_ne!(unsafe { CloseHandle(event) }, 0);
    }

    fn valid_dbix(modified_binary: i64) -> Vec<u8> {
        let mut bytes = Vec::new();
        push_u32(&mut bytes, DBIX_MAGIC);
        push_u32(&mut bytes, DBIX_VERSION);
        push_i64(&mut bytes, SAMPLE_TICKS);
        push_i32(&mut bytes, 1);
        push_7_bit_string(&mut bytes, "C:\\DeskBox\\Bench");
        push_i32(&mut bytes, 2);
        push_entry(&mut bytes, 0, "report_000001.pdf", false, modified_binary);
        push_entry(
            &mut bytes,
            0,
            "资料_000002",
            true,
            dotnet_utc_binary(SAMPLE_TICKS - 1),
        );
        bytes
    }

    fn push_entry(
        bytes: &mut Vec<u8>,
        directory_id: i32,
        file_name: &str,
        is_directory: bool,
        modified_binary: i64,
    ) {
        push_i32(bytes, directory_id);
        push_i32(bytes, file_name.len() as i32);
        bytes.extend_from_slice(file_name.as_bytes());
        bytes.push(u8::from(is_directory));
        push_i64(bytes, modified_binary);
    }

    fn push_7_bit_string(bytes: &mut Vec<u8>, value: &str) {
        let mut length = value.len() as u32;
        while length >= 0x80 {
            bytes.push((length as u8) | 0x80);
            length >>= 7;
        }
        bytes.push(length as u8);
        bytes.extend_from_slice(value.as_bytes());
    }

    fn push_u32(bytes: &mut Vec<u8>, value: u32) {
        bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn push_i32(bytes: &mut Vec<u8>, value: i32) {
        bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn push_i64(bytes: &mut Vec<u8>, value: i64) {
        bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn dotnet_utc_binary(ticks: i64) -> i64 {
        (ticks as u64 | DOTNET_KIND_UTC) as i64
    }

    fn write_fixture(bytes: Vec<u8>) -> PathBuf {
        let id = NEXT_FILE_ID.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "deskbox-search-core-dbix-{}-{id}.bin",
            std::process::id()
        ));
        fs::write(&path, bytes).unwrap();
        path
    }
}
