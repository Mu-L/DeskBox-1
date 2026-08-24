//! Compact, read-only search index used by DeskBox stage 6 experiments.
//!
//! This crate deliberately ships as a separate DLL from `deskbox_native.dll`.
//! The product native ABI is already frozen by the x64 Native AOT gates; the
//! search ABI can therefore evolve without invalidating those completed gates.
//!
//! ABI rules:
//! - callers own every input and output buffer;
//! - the opaque index handle is destroyed only by this module;
//! - build operations and queries are serialized by the caller;
//! - `cancel` may run concurrently with a query;
//! - no Rust string, vector, or allocator-owned result crosses the boundary.

#![deny(unsafe_op_in_unsafe_fn)]

use std::cmp::Ordering;
use std::collections::{BinaryHeap, HashMap};
use std::ffi::c_void;
use std::mem::size_of;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering as AtomicOrdering};

mod dbix;

#[cfg(windows)]
#[link(name = "icuuc")]
unsafe extern "C" {
    fn u_toupper(value: i32) -> i32;
}

pub const DESKBOX_SEARCH_CORE_ABI_VERSION: u32 = 3;
pub const DESKBOX_SEARCH_CORE_STRUCT_VERSION_1: u32 = 1;

pub const DESKBOX_SEARCH_STATUS_OK: u32 = 0;
pub const DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT: u32 = 1;
pub const DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT: u32 = 2;
pub const DESKBOX_SEARCH_STATUS_BUFFER_TOO_SMALL: u32 = 3;
pub const DESKBOX_SEARCH_STATUS_INVALID_STATE: u32 = 4;
pub const DESKBOX_SEARCH_STATUS_CANCELLED: u32 = 5;
pub const DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED: u32 = 6;
pub const DESKBOX_SEARCH_STATUS_IO_ERROR: u32 = 7;
pub const DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT: u32 = 8;
pub const DESKBOX_SEARCH_STATUS_CORRUPT_DATA: u32 = 9;

pub const DESKBOX_SEARCH_ENTRY_DIRECTORY: u32 = 1 << 0;
const DESKBOX_SEARCH_ENTRY_FLAGS_MASK: u32 = DESKBOX_SEARCH_ENTRY_DIRECTORY;
pub const DESKBOX_SEARCH_MUTATION_UPSERT: u32 = 1;
pub const DESKBOX_SEARCH_MUTATION_REMOVE_EXACT: u32 = 2;
pub const DESKBOX_SEARCH_MUTATION_REMOVE_TREE: u32 = 3;
pub const DESKBOX_SEARCH_MUTATION_REMOVE_STALE_TREE: u32 = 4;
pub const DESKBOX_SEARCH_PROJECTION_RECENT_FILES: u32 = 1;
pub const DESKBOX_SEARCH_PROJECTION_FREQUENT_FOLDERS: u32 = 2;
const MAX_ENTRY_COUNT: usize = 300_000;
const MAX_MUTATION_COUNT: usize = 8_192;
const MAX_UTF16_CHARS: usize = 64 * 1024 * 1024;
const MAX_QUERY_CHARS: usize = 32_767;
const MAX_DBIX_FILE_BYTES: u64 = 128 * 1024 * 1024;
const DOTNET_KIND_UTC: u64 = 0x4000_0000_0000_0000;
const DOTNET_MAX_TICKS: i64 = 3_155_378_975_999_999_999;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchCreateRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub initial_entry_capacity: u32,
    pub initial_utf16_capacity_chars: u32,
    pub flags: u32,
    pub reserved0: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchCreateResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub reserved0: u32,
    pub handle: *mut c_void,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DeskBoxSearchOpenDbixRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub path: *const u16,
    pub path_length_chars: u32,
    pub max_entry_count: u32,
    pub flags: u32,
    pub reserved0: u32,
    pub cancel_event: *mut c_void,
    pub reserved: [u64; 4],
}

impl Default for DeskBoxSearchOpenDbixRequestV1 {
    fn default() -> Self {
        Self {
            struct_size: 0,
            struct_version: 0,
            path: ptr::null(),
            path_length_chars: 0,
            max_entry_count: 0,
            flags: 0,
            reserved0: 0,
            cancel_event: ptr::null_mut(),
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchOpenDbixResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub dbix_version: u32,
    pub handle: *mut c_void,
    pub persisted_utc_ticks: i64,
    pub source_file_bytes: u64,
    pub entry_count: u32,
    pub directory_count: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchEntryInputV1 {
    pub directory_offset_chars: u32,
    pub directory_length_chars: u32,
    pub file_name_offset_chars: u32,
    pub file_name_length_chars: u32,
    pub modified_utc_ticks: i64,
    pub flags: u32,
    pub reserved0: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DeskBoxSearchAddBatchRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub entries: *const DeskBoxSearchEntryInputV1,
    pub entry_count: u32,
    pub reserved0: u32,
    pub utf16_data: *const u16,
    pub utf16_length_chars: u32,
    pub reserved1: u32,
    pub reserved: [u64; 4],
}

impl Default for DeskBoxSearchAddBatchRequestV1 {
    fn default() -> Self {
        Self {
            struct_size: 0,
            struct_version: 0,
            entries: ptr::null(),
            entry_count: 0,
            reserved0: 0,
            utf16_data: ptr::null(),
            utf16_length_chars: 0,
            reserved1: 0,
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchAddBatchResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub added_entry_count: u32,
    pub total_entry_count: u32,
    pub directory_count: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchSealResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub entry_count: u32,
    pub directory_count: u32,
    pub reserved0: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchResultV1 {
    pub entry_id: u32,
    pub score: u32,
    pub modified_utc_ticks: i64,
    pub flags: u32,
    pub reserved0: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DeskBoxSearchQueryRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub query: *const u16,
    pub query_length_chars: u32,
    pub max_results: u32,
    pub results: *mut DeskBoxSearchResultV1,
    pub result_capacity: u32,
    pub flags: u32,
    pub reserved: [u64; 4],
}

impl Default for DeskBoxSearchQueryRequestV1 {
    fn default() -> Self {
        Self {
            struct_size: 0,
            struct_version: 0,
            query: ptr::null(),
            query_length_chars: 0,
            max_results: 0,
            results: ptr::null_mut(),
            result_capacity: 0,
            flags: 0,
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchQueryResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub scanned_entry_count: u32,
    pub matched_entry_count: u32,
    pub written_result_count: u32,
    pub required_utf16_chars: u32,
    pub reserved0: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchEntryTextV1 {
    pub entry_id: u32,
    pub directory_offset_chars: u32,
    pub directory_length_chars: u32,
    pub file_name_offset_chars: u32,
    pub file_name_length_chars: u32,
    pub flags: u32,
    pub modified_utc_ticks: i64,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DeskBoxSearchCopyEntriesRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub entry_ids: *const u32,
    pub entry_count: u32,
    pub reserved0: u32,
    pub entries: *mut DeskBoxSearchEntryTextV1,
    pub entry_capacity: u32,
    pub reserved1: u32,
    pub utf16_data: *mut u16,
    pub utf16_capacity_chars: u32,
    pub flags: u32,
    pub reserved: [u64; 4],
}

impl Default for DeskBoxSearchCopyEntriesRequestV1 {
    fn default() -> Self {
        Self {
            struct_size: 0,
            struct_version: 0,
            entry_ids: ptr::null(),
            entry_count: 0,
            reserved0: 0,
            entries: ptr::null_mut(),
            entry_capacity: 0,
            reserved1: 0,
            utf16_data: ptr::null_mut(),
            utf16_capacity_chars: 0,
            flags: 0,
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchCopyEntriesResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub copied_entry_count: u32,
    pub required_utf16_chars: u32,
    pub reserved0: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchStatsV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub sealed: u32,
    pub entry_count: u32,
    pub directory_count: u32,
    pub entry_capacity_bytes: u64,
    pub directory_descriptor_capacity_bytes: u64,
    pub directory_utf16_capacity_bytes: u64,
    pub file_name_utf16_capacity_bytes: u64,
    pub build_lookup_capacity_bytes: u64,
    pub total_tracked_capacity_bytes: u64,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchMutationInputV1 {
    pub operation: u32,
    pub flags: u32,
    pub path_offset_chars: u32,
    pub path_length_chars: u32,
    pub directory_offset_chars: u32,
    pub directory_length_chars: u32,
    pub file_name_offset_chars: u32,
    pub file_name_length_chars: u32,
    pub modified_utc_ticks: i64,
    pub modified_binary: i64,
    pub scan_generation: u32,
    pub reserved0: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DeskBoxSearchMutateBatchRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub mutations: *const DeskBoxSearchMutationInputV1,
    pub mutation_count: u32,
    pub reserved0: u32,
    pub utf16_data: *const u16,
    pub utf16_length_chars: u32,
    pub flags: u32,
    pub reserved: [u64; 4],
}

impl Default for DeskBoxSearchMutateBatchRequestV1 {
    fn default() -> Self {
        Self {
            struct_size: 0,
            struct_version: 0,
            mutations: ptr::null(),
            mutation_count: 0,
            reserved0: 0,
            utf16_data: ptr::null(),
            utf16_length_chars: 0,
            flags: 0,
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchMutateBatchResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub applied_mutation_count: u32,
    pub live_entry_count: u32,
    pub tombstone_count: u32,
    pub directory_count: u32,
    pub reserved0: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchProjectionItemV1 {
    pub path_offset_chars: u32,
    pub path_length_chars: u32,
    pub rank_value: u32,
    pub flags: u32,
    pub modified_utc_ticks: i64,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DeskBoxSearchProjectRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub projection_kind: u32,
    pub max_results: u32,
    pub items: *mut DeskBoxSearchProjectionItemV1,
    pub item_capacity: u32,
    pub reserved0: u32,
    pub utf16_data: *mut u16,
    pub utf16_capacity_chars: u32,
    pub flags: u32,
    pub reserved: [u64; 4],
}

impl Default for DeskBoxSearchProjectRequestV1 {
    fn default() -> Self {
        Self {
            struct_size: 0,
            struct_version: 0,
            projection_kind: 0,
            max_results: 0,
            items: ptr::null_mut(),
            item_capacity: 0,
            reserved0: 0,
            utf16_data: ptr::null_mut(),
            utf16_capacity_chars: 0,
            flags: 0,
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchProjectResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub written_item_count: u32,
    pub required_utf16_chars: u32,
    pub scanned_entry_count: u32,
    pub reserved0: u32,
    pub reserved1: u32,
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DeskBoxSearchSaveDbixRequestV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub path: *const u16,
    pub path_length_chars: u32,
    pub reserved0: u32,
    pub temp_path: *const u16,
    pub temp_path_length_chars: u32,
    pub flags: u32,
    pub cancel_event: *mut c_void,
    pub reserved: [u64; 4],
}

impl Default for DeskBoxSearchSaveDbixRequestV1 {
    fn default() -> Self {
        Self {
            struct_size: 0,
            struct_version: 0,
            path: ptr::null(),
            path_length_chars: 0,
            reserved0: 0,
            temp_path: ptr::null(),
            temp_path_length_chars: 0,
            flags: 0,
            cancel_event: ptr::null_mut(),
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DeskBoxSearchSaveDbixResultV1 {
    pub struct_size: u32,
    pub struct_version: u32,
    pub status: u32,
    pub dbix_version: u32,
    pub persisted_utc_ticks: i64,
    pub file_bytes: u64,
    pub entry_count: u32,
    pub directory_count: u32,
    pub reserved: [u64; 4],
}

#[derive(Clone, Copy, Debug)]
struct TextRange {
    offset: u32,
    length: u32,
}

#[derive(Clone, Copy, Debug)]
struct SearchEntry {
    modified_utc_ticks: i64,
    modified_binary: i64,
    directory_id: u32,
    file_name_offset: u32,
    file_name_length: u32,
    scan_generation: u32,
    flags: u32,
    tombstoned: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Candidate {
    entry_id: u32,
    score: u32,
    modified_utc_ticks: i64,
    flags: u32,
}

#[derive(Clone, Copy, Debug)]
struct PreparedUpsert {
    directory: TextRange,
    file_name: TextRange,
    directory_id: u32,
    modified_utc_ticks: i64,
    modified_binary: i64,
    scan_generation: u32,
    flags: u32,
}

#[derive(Clone, Copy, Debug)]
struct PreparedTreeRemoval {
    path: TextRange,
    retained_scan_generation: Option<u32>,
}

// BinaryHeap keeps its greatest item at the root. This ordering intentionally
// treats the least useful candidate as greatest so the bounded heap can evict
// the current worst result in O(log N).
impl Ord for Candidate {
    fn cmp(&self, other: &Self) -> Ordering {
        other
            .score
            .cmp(&self.score)
            .then_with(|| other.modified_utc_ticks.cmp(&self.modified_utc_ticks))
            .then_with(|| self.entry_id.cmp(&other.entry_id))
    }
}

impl PartialOrd for Candidate {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

struct SearchCore {
    entries: Vec<SearchEntry>,
    directories: Vec<TextRange>,
    directory_utf16: Vec<u16>,
    file_name_utf16: Vec<u16>,
    directory_lookup: Option<HashMap<u64, Vec<u32>>>,
    live_entry_count: usize,
    sealed: bool,
    cancelled: AtomicBool,
}

impl SearchCore {
    fn new(entry_capacity: usize, utf16_capacity: usize) -> Result<Self, u32> {
        let mut entries = Vec::new();
        let mut file_name_utf16 = Vec::new();
        entries
            .try_reserve_exact(entry_capacity)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        file_name_utf16
            .try_reserve_exact(utf16_capacity)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;

        Ok(Self {
            entries,
            directories: Vec::new(),
            directory_utf16: Vec::new(),
            file_name_utf16,
            directory_lookup: Some(HashMap::new()),
            live_entry_count: 0,
            sealed: false,
            cancelled: AtomicBool::new(false),
        })
    }

    fn add_batch(
        &mut self,
        inputs: &[DeskBoxSearchEntryInputV1],
        utf16: &[u16],
    ) -> Result<u32, u32> {
        if self.sealed {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_STATE);
        }
        if self.entries.len().saturating_add(inputs.len()) > MAX_ENTRY_COUNT {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }

        let mut required_file_name_chars = 0usize;
        for input in inputs {
            if input.reserved0 != 0
                || input.flags & !DESKBOX_SEARCH_ENTRY_FLAGS_MASK != 0
                || input.file_name_length_chars == 0
                || !(0..=DOTNET_MAX_TICKS).contains(&input.modified_utc_ticks)
            {
                return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
            }
            validate_range(
                input.directory_offset_chars,
                input.directory_length_chars,
                utf16.len(),
            )?;
            validate_range(
                input.file_name_offset_chars,
                input.file_name_length_chars,
                utf16.len(),
            )?;
            required_file_name_chars = required_file_name_chars
                .checked_add(input.file_name_length_chars as usize)
                .ok_or(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
        }

        if self
            .file_name_utf16
            .len()
            .saturating_add(required_file_name_chars)
            > MAX_UTF16_CHARS
        {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }
        self.entries
            .try_reserve(inputs.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        self.file_name_utf16
            .try_reserve(required_file_name_chars)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;

        let starting_count = self.entries.len();
        for input in inputs {
            let directory = read_range(
                utf16,
                input.directory_offset_chars,
                input.directory_length_chars,
            );
            let file_name = read_range(
                utf16,
                input.file_name_offset_chars,
                input.file_name_length_chars,
            );
            let directory_id = self.intern_directory(directory)?;
            let file_name_offset = u32::try_from(self.file_name_utf16.len())
                .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
            self.file_name_utf16.extend_from_slice(file_name);
            self.entries.push(SearchEntry {
                modified_utc_ticks: input.modified_utc_ticks,
                modified_binary: (input.modified_utc_ticks as u64 | DOTNET_KIND_UTC) as i64,
                directory_id,
                file_name_offset,
                file_name_length: input.file_name_length_chars,
                scan_generation: 0,
                flags: input.flags,
                tombstoned: 0,
            });
        }

        self.live_entry_count = self.entries.len();

        u32::try_from(self.entries.len() - starting_count)
            .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)
    }

    fn intern_directory(&mut self, directory: &[u16]) -> Result<u32, u32> {
        let hash = directory_hash(directory);
        if let Some(ids) = self
            .directory_lookup
            .as_ref()
            .and_then(|lookup| lookup.get(&hash))
        {
            for id in ids {
                let existing = self.directory(*id);
                if ordinal_ignore_case_equals(existing, directory) {
                    return Ok(*id);
                }
            }
        }

        if self.directory_utf16.len().saturating_add(directory.len()) > MAX_UTF16_CHARS {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }
        self.directory_utf16
            .try_reserve(directory.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        self.directories
            .try_reserve(1)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;

        let id = u32::try_from(self.directories.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
        let offset = u32::try_from(self.directory_utf16.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
        let length =
            u32::try_from(directory.len()).map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
        self.directory_utf16.extend_from_slice(directory);
        self.directories.push(TextRange { offset, length });
        self.directory_lookup
            .as_mut()
            .ok_or(DESKBOX_SEARCH_STATUS_INVALID_STATE)?
            .entry(hash)
            .or_default()
            .push(id);
        Ok(id)
    }

    fn mutate_batch(
        &mut self,
        inputs: &[DeskBoxSearchMutationInputV1],
        utf16: &[u16],
    ) -> Result<(u32, u32), u32> {
        if !self.sealed {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_STATE);
        }
        if inputs.is_empty() || inputs.len() > MAX_MUTATION_COUNT {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }

        let mut upserts = Vec::new();
        let mut exact_removals = Vec::new();
        let mut tree_removals = Vec::new();
        let mut staged_directories = Vec::new();
        upserts
            .try_reserve(inputs.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        exact_removals
            .try_reserve(inputs.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        tree_removals
            .try_reserve(inputs.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;

        for input in inputs {
            if input.reserved0 != 0 {
                return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
            }
            match input.operation {
                DESKBOX_SEARCH_MUTATION_UPSERT => {
                    if input.flags & !DESKBOX_SEARCH_ENTRY_FLAGS_MASK != 0
                        || input.path_offset_chars != 0
                        || input.path_length_chars != 0
                        || input.file_name_length_chars == 0
                        || !(0..=DOTNET_MAX_TICKS).contains(&input.modified_utc_ticks)
                        || dbix::decode_dotnet_datetime_binary(input.modified_binary)?
                            != input.modified_utc_ticks
                    {
                        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
                    }
                    validate_range(
                        input.directory_offset_chars,
                        input.directory_length_chars,
                        utf16.len(),
                    )?;
                    validate_range(
                        input.file_name_offset_chars,
                        input.file_name_length_chars,
                        utf16.len(),
                    )?;
                    let directory = TextRange {
                        offset: input.directory_offset_chars,
                        length: input.directory_length_chars,
                    };
                    let file_name = TextRange {
                        offset: input.file_name_offset_chars,
                        length: input.file_name_length_chars,
                    };
                    if read_text_range(utf16, directory).contains(&0)
                        || read_text_range(utf16, file_name).contains(&0)
                    {
                        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
                    }
                    if upserts.iter().any(|existing: &PreparedUpsert| {
                        ordinal_ignore_case_equals(
                            read_text_range(utf16, existing.directory),
                            read_text_range(utf16, directory),
                        ) && ordinal_ignore_case_equals(
                            read_text_range(utf16, existing.file_name),
                            read_text_range(utf16, file_name),
                        )
                    }) {
                        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
                    }

                    let directory_id = self.find_or_stage_directory(
                        read_text_range(utf16, directory),
                        utf16,
                        &mut staged_directories,
                    )?;
                    upserts.push(PreparedUpsert {
                        directory,
                        file_name,
                        directory_id,
                        modified_utc_ticks: input.modified_utc_ticks,
                        modified_binary: input.modified_binary,
                        scan_generation: input.scan_generation,
                        flags: input.flags,
                    });
                }
                DESKBOX_SEARCH_MUTATION_REMOVE_EXACT
                | DESKBOX_SEARCH_MUTATION_REMOVE_TREE
                | DESKBOX_SEARCH_MUTATION_REMOVE_STALE_TREE => {
                    if input.flags != 0
                        || input.path_length_chars == 0
                        || input.directory_offset_chars != 0
                        || input.directory_length_chars != 0
                        || input.file_name_offset_chars != 0
                        || input.file_name_length_chars != 0
                        || input.modified_utc_ticks != 0
                        || input.modified_binary != 0
                        || (input.operation != DESKBOX_SEARCH_MUTATION_REMOVE_STALE_TREE
                            && input.scan_generation != 0)
                    {
                        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
                    }
                    validate_range(
                        input.path_offset_chars,
                        input.path_length_chars,
                        utf16.len(),
                    )?;
                    let path = TextRange {
                        offset: input.path_offset_chars,
                        length: input.path_length_chars,
                    };
                    if read_text_range(utf16, path).contains(&0) {
                        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
                    }
                    if input.operation == DESKBOX_SEARCH_MUTATION_REMOVE_EXACT {
                        exact_removals.push(path);
                    } else {
                        tree_removals.push(PreparedTreeRemoval {
                            path,
                            retained_scan_generation: (input.operation
                                == DESKBOX_SEARCH_MUTATION_REMOVE_STALE_TREE)
                                .then_some(input.scan_generation),
                        });
                    }
                }
                _ => return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT),
            }
        }

        let exact_target_count = upserts.len().saturating_add(exact_removals.len());
        let mut exact_targets: HashMap<u64, Vec<(bool, usize)>> = HashMap::new();
        exact_targets
            .try_reserve(exact_target_count)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        for (index, upsert) in upserts.iter().enumerate() {
            exact_targets
                .entry(path_hash_from_parts(
                    read_text_range(utf16, upsert.directory),
                    read_text_range(utf16, upsert.file_name),
                ))
                .or_default()
                .push((true, index));
        }
        for (index, path) in exact_removals.iter().enumerate() {
            exact_targets
                .entry(path_hash(read_text_range(utf16, *path)))
                .or_default()
                .push((false, index));
        }

        let mut affected_entries = Vec::new();
        affected_entries
            .try_reserve(self.live_entry_count.min(self.entries.len()))
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        for (entry_id, entry) in self.entries.iter().enumerate() {
            if entry.tombstoned != 0 {
                continue;
            }
            let mut affected = false;
            if let Some(targets) = exact_targets.get(&self.entry_path_hash(entry)) {
                affected = targets.iter().any(|(is_upsert, index)| {
                    if *is_upsert {
                        self.entry_path_equals_parts(
                            entry,
                            read_text_range(utf16, upserts[*index].directory),
                            read_text_range(utf16, upserts[*index].file_name),
                        )
                    } else {
                        self.entry_path_equals_path(
                            entry,
                            read_text_range(utf16, exact_removals[*index]),
                        )
                    }
                });
            }
            if !affected {
                affected = tree_removals.iter().any(|removal| {
                    removal
                        .retained_scan_generation
                        .is_none_or(|generation| entry.scan_generation != generation)
                        && self.entry_is_same_or_descendant(
                            entry,
                            read_text_range(utf16, removal.path),
                        )
                });
            }
            if affected {
                affected_entries.push(entry_id as u32);
            }
        }

        let resulting_live_count = self
            .live_entry_count
            .saturating_sub(affected_entries.len())
            .saturating_add(upserts.len());
        if resulting_live_count > MAX_ENTRY_COUNT {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }

        let required_file_name_chars = upserts.iter().try_fold(0usize, |total, upsert| {
            total
                .checked_add(upsert.file_name.length as usize)
                .ok_or(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)
        })?;
        let required_directory_chars =
            staged_directories
                .iter()
                .try_fold(0usize, |total, range: &TextRange| {
                    total
                        .checked_add(range.length as usize)
                        .ok_or(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)
                })?;
        if self
            .file_name_utf16
            .len()
            .saturating_add(required_file_name_chars)
            > MAX_UTF16_CHARS
            || self
                .directory_utf16
                .len()
                .saturating_add(required_directory_chars)
                > MAX_UTF16_CHARS
        {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }

        self.entries
            .try_reserve(upserts.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        self.file_name_utf16
            .try_reserve(required_file_name_chars)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        self.directories
            .try_reserve(staged_directories.len())
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        self.directory_utf16
            .try_reserve(required_directory_chars)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;

        for range in &staged_directories {
            let offset = self.directory_utf16.len() as u32;
            self.directory_utf16
                .extend_from_slice(read_text_range(utf16, *range));
            self.directories.push(TextRange {
                offset,
                length: range.length,
            });
        }
        for entry_id in affected_entries.iter().copied() {
            self.entries[entry_id as usize].tombstoned = 1;
        }
        self.live_entry_count -= affected_entries.len();
        for upsert in upserts {
            let file_name_offset = self.file_name_utf16.len() as u32;
            self.file_name_utf16
                .extend_from_slice(read_text_range(utf16, upsert.file_name));
            self.entries.push(SearchEntry {
                modified_utc_ticks: upsert.modified_utc_ticks,
                modified_binary: upsert.modified_binary,
                directory_id: upsert.directory_id,
                file_name_offset,
                file_name_length: upsert.file_name.length,
                scan_generation: upsert.scan_generation,
                flags: upsert.flags,
                tombstoned: 0,
            });
            self.live_entry_count += 1;
        }

        Ok((
            affected_entries.len() as u32,
            (self.entries.len() - self.live_entry_count) as u32,
        ))
    }

    fn find_or_stage_directory(
        &self,
        directory: &[u16],
        source_utf16: &[u16],
        staged: &mut Vec<TextRange>,
    ) -> Result<u32, u32> {
        if let Some((id, _)) = self
            .directories
            .iter()
            .enumerate()
            .find(|(_, range)| ordinal_ignore_case_equals(self.text(**range), directory))
        {
            return Ok(id as u32);
        }
        if let Some((index, _)) = staged.iter().enumerate().find(|(_, range)| {
            ordinal_ignore_case_equals(read_text_range(source_utf16, **range), directory)
        }) {
            return u32::try_from(self.directories.len().saturating_add(index))
                .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }
        let offset = directory.as_ptr() as usize;
        let source_start = source_utf16.as_ptr() as usize;
        let byte_offset = offset
            .checked_sub(source_start)
            .ok_or(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
        let range = TextRange {
            offset: u32::try_from(byte_offset / size_of::<u16>())
                .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?,
            length: u32::try_from(directory.len())
                .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?,
        };
        staged
            .try_reserve(1)
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        staged.push(range);
        u32::try_from(self.directories.len().saturating_add(staged.len() - 1))
            .map_err(|_| DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)
    }

    fn text(&self, range: TextRange) -> &[u16] {
        read_text_range(&self.directory_utf16, range)
    }

    fn entry_path_hash(&self, entry: &SearchEntry) -> u64 {
        path_hash_from_parts(self.directory(entry.directory_id), self.file_name(entry))
    }

    fn entry_path_equals_parts(
        &self,
        entry: &SearchEntry,
        directory: &[u16],
        file_name: &[u16],
    ) -> bool {
        ordinal_ignore_case_equals(self.directory(entry.directory_id), directory)
            && ordinal_ignore_case_equals(self.file_name(entry), file_name)
    }

    fn entry_path_equals_path(&self, entry: &SearchEntry, path: &[u16]) -> bool {
        path_equals_parts(
            path,
            self.directory(entry.directory_id),
            self.file_name(entry),
        )
    }

    fn entry_is_same_or_descendant(&self, entry: &SearchEntry, parent: &[u16]) -> bool {
        path_is_same_or_descendant(
            self.directory(entry.directory_id),
            self.file_name(entry),
            parent,
        )
    }

    fn project(&self, kind: u32, max_results: usize) -> Result<(Vec<Candidate>, u32), u32> {
        if !self.sealed || max_results == 0 {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_STATE);
        }
        let mut heap = BinaryHeap::new();
        heap.try_reserve(max_results.min(self.live_entry_count))
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        let scanned = self.live_entry_count as u32;
        match kind {
            DESKBOX_SEARCH_PROJECTION_RECENT_FILES => {
                for (entry_id, entry) in self.entries.iter().enumerate() {
                    if entry.tombstoned != 0 || entry.flags & DESKBOX_SEARCH_ENTRY_DIRECTORY != 0 {
                        continue;
                    }
                    push_bounded_candidate(
                        &mut heap,
                        Candidate {
                            entry_id: entry_id as u32,
                            score: 0,
                            modified_utc_ticks: entry.modified_utc_ticks,
                            flags: entry.flags,
                        },
                        max_results,
                    );
                }
            }
            DESKBOX_SEARCH_PROJECTION_FREQUENT_FOLDERS => {
                let mut aggregates = Vec::new();
                aggregates
                    .try_reserve_exact(self.directories.len())
                    .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
                aggregates.resize(self.directories.len(), (0u32, i64::MIN));
                for entry in &self.entries {
                    if entry.tombstoned != 0
                        || entry.flags & DESKBOX_SEARCH_ENTRY_DIRECTORY != 0
                        || self.directory(entry.directory_id).is_empty()
                    {
                        continue;
                    }
                    let aggregate = &mut aggregates[entry.directory_id as usize];
                    aggregate.0 = aggregate.0.saturating_add(1);
                    aggregate.1 = aggregate.1.max(entry.modified_utc_ticks);
                }
                for (directory_id, (count, modified_utc_ticks)) in
                    aggregates.into_iter().enumerate()
                {
                    if count == 0 {
                        continue;
                    }
                    push_bounded_candidate(
                        &mut heap,
                        Candidate {
                            entry_id: directory_id as u32,
                            score: count,
                            modified_utc_ticks,
                            flags: DESKBOX_SEARCH_ENTRY_DIRECTORY,
                        },
                        max_results,
                    );
                }
            }
            _ => return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT),
        }
        let mut results = heap.into_vec();
        results.sort_unstable_by(|left, right| {
            right
                .score
                .cmp(&left.score)
                .then_with(|| right.modified_utc_ticks.cmp(&left.modified_utc_ticks))
                .then_with(|| left.entry_id.cmp(&right.entry_id))
        });
        Ok((results, scanned))
    }

    fn seal(&mut self) -> Result<(), u32> {
        if self.sealed {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_STATE);
        }
        self.entries.shrink_to_fit();
        self.directories.shrink_to_fit();
        self.directory_utf16.shrink_to_fit();
        self.file_name_utf16.shrink_to_fit();
        self.directory_lookup = None;
        self.sealed = true;
        Ok(())
    }

    fn directory(&self, id: u32) -> &[u16] {
        let range = self.directories[id as usize];
        read_range(&self.directory_utf16, range.offset, range.length)
    }

    fn file_name(&self, entry: &SearchEntry) -> &[u16] {
        read_range(
            &self.file_name_utf16,
            entry.file_name_offset,
            entry.file_name_length,
        )
    }

    fn search(&self, query: &[u16], max_results: usize) -> Result<(Vec<Candidate>, u32, u32), u32> {
        if !self.sealed {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_STATE);
        }
        if query.is_empty() || query.len() > MAX_QUERY_CHARS || max_results == 0 {
            return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
        }

        let mut heap = BinaryHeap::new();
        heap.try_reserve(max_results.min(self.entries.len()))
            .map_err(|_| DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED)?;
        let mut matched = 0u32;
        let mut scanned = 0u32;

        for (entry_id, entry) in self.entries.iter().enumerate() {
            if self.cancelled.load(AtomicOrdering::Relaxed) {
                return Err(DESKBOX_SEARCH_STATUS_CANCELLED);
            }
            if entry.tombstoned != 0 {
                continue;
            }
            scanned = scanned.saturating_add(1);
            let score = compute_relevance(self.file_name(entry), query);
            if score == 0 {
                continue;
            }
            matched = matched.saturating_add(1);
            let candidate = Candidate {
                entry_id: entry_id as u32,
                score,
                modified_utc_ticks: entry.modified_utc_ticks,
                flags: entry.flags,
            };
            if heap.len() < max_results {
                heap.push(candidate);
            } else if heap
                .peek()
                .is_some_and(|worst| candidate_is_better(&candidate, worst))
            {
                let _ = heap.pop();
                heap.push(candidate);
            }
        }

        let mut results = heap.into_vec();
        results.sort_unstable_by(|left, right| {
            right
                .score
                .cmp(&left.score)
                .then_with(|| right.modified_utc_ticks.cmp(&left.modified_utc_ticks))
                .then_with(|| left.entry_id.cmp(&right.entry_id))
        });
        Ok((results, matched, scanned))
    }

    fn tracked_stats(&self) -> DeskBoxSearchStatsV1 {
        let entry_capacity_bytes = capacity_bytes::<SearchEntry>(self.entries.capacity());
        let directory_range_bytes = capacity_bytes::<TextRange>(self.directories.capacity());
        let directory_utf16_capacity_bytes = capacity_bytes::<u16>(self.directory_utf16.capacity());
        let file_name_utf16_capacity_bytes = capacity_bytes::<u16>(self.file_name_utf16.capacity());
        let build_lookup_capacity_bytes = self.directory_lookup.as_ref().map_or(0, |lookup| {
            let bucket_bytes = capacity_bytes::<(u64, Vec<u32>)>(lookup.capacity());
            let id_bytes = lookup
                .values()
                .map(|ids| capacity_bytes::<u32>(ids.capacity()))
                .sum::<u64>();
            bucket_bytes.saturating_add(id_bytes)
        });
        let total = entry_capacity_bytes
            .saturating_add(directory_range_bytes)
            .saturating_add(directory_utf16_capacity_bytes)
            .saturating_add(file_name_utf16_capacity_bytes)
            .saturating_add(build_lookup_capacity_bytes);
        DeskBoxSearchStatsV1 {
            struct_size: size_of::<DeskBoxSearchStatsV1>() as u32,
            struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
            status: DESKBOX_SEARCH_STATUS_OK,
            sealed: u32::from(self.sealed),
            entry_count: self.live_entry_count as u32,
            directory_count: self.directories.len() as u32,
            entry_capacity_bytes,
            directory_descriptor_capacity_bytes: directory_range_bytes,
            directory_utf16_capacity_bytes,
            file_name_utf16_capacity_bytes,
            build_lookup_capacity_bytes,
            total_tracked_capacity_bytes: total,
            reserved: [0; 4],
        }
    }
}

fn candidate_is_better(candidate: &Candidate, current_worst: &Candidate) -> bool {
    candidate.score > current_worst.score
        || (candidate.score == current_worst.score
            && (candidate.modified_utc_ticks > current_worst.modified_utc_ticks
                || (candidate.modified_utc_ticks == current_worst.modified_utc_ticks
                    && candidate.entry_id < current_worst.entry_id)))
}

fn push_bounded_candidate(heap: &mut BinaryHeap<Candidate>, candidate: Candidate, maximum: usize) {
    if heap.len() < maximum {
        heap.push(candidate);
    } else if heap
        .peek()
        .is_some_and(|worst| candidate_is_better(&candidate, worst))
    {
        let _ = heap.pop();
        heap.push(candidate);
    }
}

fn validate_range(offset: u32, length: u32, total: usize) -> Result<(), u32> {
    let start = offset as usize;
    let end = start
        .checked_add(length as usize)
        .ok_or(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)?;
    if end > total {
        return Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT);
    }
    Ok(())
}

fn read_range(value: &[u16], offset: u32, length: u32) -> &[u16] {
    let start = offset as usize;
    &value[start..start + length as usize]
}

fn read_text_range(value: &[u16], range: TextRange) -> &[u16] {
    read_range(value, range.offset, range.length)
}

fn path_has_inserted_separator(directory: &[u16]) -> bool {
    !directory.is_empty()
        && !matches!(directory.last(), Some(unit) if *unit == b'\\' as u16 || *unit == b'/' as u16)
}

fn path_length_from_parts(directory: &[u16], file_name: &[u16]) -> usize {
    directory
        .len()
        .saturating_add(usize::from(path_has_inserted_separator(directory)))
        .saturating_add(file_name.len())
}

fn path_unit_from_parts(directory: &[u16], file_name: &[u16], index: usize) -> Option<u16> {
    if index < directory.len() {
        return Some(directory[index]);
    }
    let mut remaining = index - directory.len();
    if path_has_inserted_separator(directory) {
        if remaining == 0 {
            return Some(b'\\' as u16);
        }
        remaining -= 1;
    }
    file_name.get(remaining).copied()
}

fn path_equals_parts(path: &[u16], directory: &[u16], file_name: &[u16]) -> bool {
    if path.len() != path_length_from_parts(directory, file_name) {
        return false;
    }
    if !ordinal_ignore_case_equals(&path[..directory.len()], directory) {
        return false;
    }
    let mut offset = directory.len();
    if path_has_inserted_separator(directory) {
        if path[offset] != b'\\' as u16 && path[offset] != b'/' as u16 {
            return false;
        }
        offset += 1;
    }
    ordinal_ignore_case_equals(&path[offset..], file_name)
}

fn path_is_same_or_descendant(directory: &[u16], file_name: &[u16], parent: &[u16]) -> bool {
    let candidate_length = path_length_from_parts(directory, file_name);
    if parent.len() > candidate_length {
        return false;
    }

    let mut parent_offset = 0usize;
    let directory_part = parent.len().min(directory.len());
    if !ordinal_ignore_case_equals(&parent[..directory_part], &directory[..directory_part]) {
        return false;
    }
    parent_offset += directory_part;
    if parent_offset < parent.len() && directory_part == directory.len() {
        if path_has_inserted_separator(directory) {
            let unit = parent[parent_offset];
            if unit != b'\\' as u16 && unit != b'/' as u16 {
                return false;
            }
            parent_offset += 1;
        }
        let remaining = parent.len() - parent_offset;
        if remaining > file_name.len()
            || !ordinal_ignore_case_equals(&parent[parent_offset..], &file_name[..remaining])
        {
            return false;
        }
    }
    if parent.len() == candidate_length {
        return true;
    }
    if matches!(parent.last(), Some(unit) if *unit == b'\\' as u16 || *unit == b'/' as u16) {
        return true;
    }
    matches!(
        path_unit_from_parts(directory, file_name, parent.len()),
        Some(unit) if unit == b'\\' as u16 || unit == b'/' as u16
    )
}

fn path_hash(value: &[u16]) -> u64 {
    if value.iter().any(|unit| *unit > 0x7f) {
        return 0x5041_5448_0000_0000u64 ^ value.len() as u64;
    }
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for unit in value {
        let folded = if (*unit as u8).is_ascii_uppercase() {
            *unit + 32
        } else {
            *unit
        };
        hash ^= folded as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    hash
}

fn path_hash_from_parts(directory: &[u16], file_name: &[u16]) -> u64 {
    let total_length = path_length_from_parts(directory, file_name);
    if directory.iter().chain(file_name).any(|unit| *unit > 0x7f) {
        return 0x5041_5448_0000_0000u64 ^ total_length as u64;
    }
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for unit in directory {
        let folded = if (*unit as u8).is_ascii_uppercase() {
            *unit + 32
        } else {
            *unit
        };
        hash ^= folded as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    if path_has_inserted_separator(directory) {
        hash ^= b'\\' as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    for unit in file_name {
        let folded = if (*unit as u8).is_ascii_uppercase() {
            *unit + 32
        } else {
            *unit
        };
        hash ^= folded as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    hash
}

fn directory_hash(value: &[u16]) -> u64 {
    // ASCII paths get a useful case-insensitive FNV-1a hash. For any non-ASCII
    // path we deliberately collapse to a length bucket and confirm equality via
    // the same ICU simple-uppercase rule as query matching. This keeps the
    // hash/equality contract exact for Unicode casing edge cases without storing
    // a second normalized string.
    if value.iter().any(|unit| *unit > 0x7f) {
        return 0xD1EC_70A1_0000_0000u64 ^ value.len() as u64;
    }
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for unit in value {
        let folded = if (*unit as u8).is_ascii_uppercase() {
            unit + 32
        } else {
            *unit
        };
        hash ^= folded as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    hash
}

fn ordinal_ignore_case_equals(left: &[u16], right: &[u16]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    let mut offset = 0usize;
    while offset < left.len() {
        let (left_scalar, left_width) = decode_utf16_scalar(left, offset);
        let (right_scalar, right_width) = decode_utf16_scalar(right, offset);
        if left_width != right_width {
            return false;
        }
        if left_scalar == right_scalar {
            offset += left_width;
            continue;
        }

        let left_ascii = left_scalar <= 0x7f;
        let right_ascii = right_scalar <= 0x7f;
        if left_ascii != right_ascii {
            // .NET's OrdinalIgnoreCase deliberately prevents non-ASCII code
            // points such as Kelvin sign or dotless i from folding to ASCII.
            return false;
        }
        if left_ascii {
            let left_folded = (left_scalar as u8).to_ascii_uppercase();
            let right_folded = (right_scalar as u8).to_ascii_uppercase();
            if left_folded != right_folded {
                return false;
            }
        } else if simple_upper_scalar(left_scalar, left_width)
            != simple_upper_scalar(right_scalar, right_width)
        {
            return false;
        }
        offset += left_width;
    }
    true
}

fn decode_utf16_scalar(value: &[u16], offset: usize) -> (u32, usize) {
    let high = value[offset];
    if (0xD800..=0xDBFF).contains(&high) && offset + 1 < value.len() {
        let low = value[offset + 1];
        if (0xDC00..=0xDFFF).contains(&low) {
            let scalar = 0x1_0000 + (((high as u32 - 0xD800) << 10) | (low as u32 - 0xDC00));
            return (scalar, 2);
        }
    }
    (high as u32, 1)
}

fn simple_upper_scalar(value: u32, original_width: usize) -> u32 {
    #[cfg(windows)]
    let upper = {
        // SAFETY: u_toupper accepts every Unicode scalar value as an i32 and
        // returns one scalar without allocating or changing process state.
        unsafe { u_toupper(value as i32) as u32 }
    };

    #[cfg(not(windows))]
    let upper = char::from_u32(value).map_or(value, |character| {
        let mut mapped = character.to_uppercase();
        let first = mapped.next().unwrap_or(character);
        if mapped.next().is_none() {
            first as u32
        } else {
            value
        }
    });

    let upper_width = if upper <= 0xFFFF { 1 } else { 2 };
    if upper > 0x10_FFFF || upper_width != original_width {
        value
    } else {
        upper
    }
}

fn ordinal_ignore_case_starts_with(value: &[u16], prefix: &[u16]) -> bool {
    value.len() >= prefix.len() && ordinal_ignore_case_equals(&value[..prefix.len()], prefix)
}

fn ordinal_ignore_case_contains(value: &[u16], query: &[u16]) -> bool {
    query.len() <= value.len()
        && (0..=value.len() - query.len())
            .any(|start| ordinal_ignore_case_equals(&value[start..start + query.len()], query))
}

fn file_name_without_extension(value: &[u16]) -> &[u16] {
    value
        .iter()
        .rposition(|unit| *unit == b'.' as u16)
        .map_or(value, |position| &value[..position])
}

fn compute_relevance(file_name: &[u16], query: &[u16]) -> u32 {
    if ordinal_ignore_case_equals(file_name, query) {
        return 100;
    }
    if ordinal_ignore_case_starts_with(file_name, query) {
        return 80;
    }
    let stem = file_name_without_extension(file_name);
    if ordinal_ignore_case_equals(stem, query) {
        return 90;
    }
    if ordinal_ignore_case_starts_with(stem, query) {
        return 70;
    }
    if ordinal_ignore_case_contains(file_name, query) {
        return 50;
    }
    0
}

fn capacity_bytes<T>(capacity: usize) -> u64 {
    (capacity as u64).saturating_mul(size_of::<T>() as u64)
}

fn envelope_matches(size: u32, version: u32, expected_size: usize) -> bool {
    size == expected_size as u32 && version == DESKBOX_SEARCH_CORE_STRUCT_VERSION_1
}

fn reserved_is_zero(reserved: &[u64; 4]) -> bool {
    reserved.iter().all(|value| *value == 0)
}

unsafe fn core_from_handle<'a>(handle: *mut c_void) -> Option<&'a SearchCore> {
    // SAFETY: the ABI contract requires a live handle created by this module.
    unsafe { (handle as *mut SearchCore).as_ref() }
}

unsafe fn core_from_handle_mut<'a>(handle: *mut c_void) -> Option<&'a mut SearchCore> {
    // SAFETY: the ABI contract requires a live, exclusively used handle.
    unsafe { (handle as *mut SearchCore).as_mut() }
}

#[unsafe(no_mangle)]
pub extern "C" fn deskbox_search_core_abi_version() -> u32 {
    DESKBOX_SEARCH_CORE_ABI_VERSION
}

/// Opens a current DeskBox compact DBIX file directly into a sealed SearchCore.
///
/// No partially parsed handle escapes on failure. `cancel_event`, when non-null,
/// must be a waitable Windows event that remains live for the whole call.
///
/// # Safety
///
/// `request`, its UTF-16 path, and `result` must remain readable/writable for
/// their declared sizes for the duration of the call. A successful returned
/// handle must eventually be passed to `deskbox_search_core_destroy_v1` once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_open_dbix_v1(
    request: *const DeskBoxSearchOpenDbixRequestV1,
    result: *mut DeskBoxSearchOpenDbixResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: non-null pointers are required by the ABI contract.
    let request = unsafe { &*request };
    // SAFETY: non-null pointer is required by the ABI contract.
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchOpenDbixRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchOpenDbixResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchOpenDbixResultV1 {
        struct_size: size_of::<DeskBoxSearchOpenDbixResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    if request.path.is_null()
        || request.path_length_chars == 0
        || request.path_length_chars as usize > MAX_QUERY_CHARS
        || request.max_entry_count == 0
        || request.max_entry_count as usize > MAX_ENTRY_COUNT
        || request.flags != 0
        || request.reserved0 != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    // SAFETY: the request path points to its declared number of UTF-16 units.
    let path =
        unsafe { std::slice::from_raw_parts(request.path, request.path_length_chars as usize) };
    match dbix::load_dbix(path, request.max_entry_count as usize, request.cancel_event) {
        Ok((core, metadata)) => {
            output.status = DESKBOX_SEARCH_STATUS_OK;
            output.dbix_version = metadata.version;
            output.persisted_utc_ticks = metadata.persisted_utc_ticks;
            output.source_file_bytes = metadata.source_file_bytes;
            output.entry_count = core.entries.len() as u32;
            output.directory_count = core.directories.len() as u32;
            output.handle = Box::into_raw(Box::new(core)).cast();
        }
        Err(status) => output.status = status,
    }
    output.status
}

/// Creates an empty SearchCore handle.
///
/// # Safety
///
/// `request` and `result` must point to readable/writable ABI v1 structures for
/// the duration of the call. The returned handle must eventually be passed to
/// `deskbox_search_core_destroy_v1` exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_create_v1(
    request: *const DeskBoxSearchCreateRequestV1,
    result: *mut DeskBoxSearchCreateResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: non-null pointers are required by the ABI contract.
    let request = unsafe { &*request };
    // SAFETY: non-null pointer is required by the ABI contract.
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchCreateRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchCreateResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchCreateResultV1 {
        struct_size: size_of::<DeskBoxSearchCreateResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    if request.initial_entry_capacity as usize > MAX_ENTRY_COUNT
        || request.initial_utf16_capacity_chars as usize > MAX_UTF16_CHARS
        || request.flags != 0
        || request.reserved0 != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    match SearchCore::new(
        request.initial_entry_capacity as usize,
        request.initial_utf16_capacity_chars as usize,
    ) {
        Ok(core) => {
            output.handle = Box::into_raw(Box::new(core)).cast();
            output.status = DESKBOX_SEARCH_STATUS_OK;
        }
        Err(status) => output.status = status,
    }
    output.status
}

/// Adds one caller-owned, packed UTF-16 batch to an unsealed index.
///
/// # Safety
///
/// `handle` must be a live handle created by this module and used exclusively
/// for the build operation. Request pointers must reference buffers of their
/// declared lengths, and `result` must be writable for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_add_batch_v1(
    handle: *mut c_void,
    request: *const DeskBoxSearchAddBatchRequestV1,
    result: *mut DeskBoxSearchAddBatchResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: pointers are non-null and must reference ABI structures.
    let request = unsafe { &*request };
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchAddBatchRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchAddBatchResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchAddBatchResultV1 {
        struct_size: size_of::<DeskBoxSearchAddBatchResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    // SAFETY: a live, exclusively used handle is required for build operations.
    let Some(core) = (unsafe { core_from_handle_mut(handle) }) else {
        return output.status;
    };
    if request.entry_count == 0
        || request.entry_count as usize > MAX_ENTRY_COUNT
        || request.utf16_length_chars as usize > MAX_UTF16_CHARS
        || request.entries.is_null()
        || request.utf16_data.is_null()
        || request.reserved0 != 0
        || request.reserved1 != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    // SAFETY: validated non-null buffers contain the declared element counts.
    let inputs =
        unsafe { std::slice::from_raw_parts(request.entries, request.entry_count as usize) };
    let utf16 = unsafe {
        std::slice::from_raw_parts(request.utf16_data, request.utf16_length_chars as usize)
    };
    match core.add_batch(inputs, utf16) {
        Ok(added) => {
            output.status = DESKBOX_SEARCH_STATUS_OK;
            output.added_entry_count = added;
        }
        Err(status) => output.status = status,
    }
    output.total_entry_count = core.entries.len() as u32;
    output.directory_count = core.directories.len() as u32;
    output.status
}

/// Seals an index and releases build-only lookup storage.
///
/// # Safety
///
/// `handle` must be a live handle used exclusively for this mutation, and
/// `result` must point to a writable ABI v1 result structure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_seal_v1(
    handle: *mut c_void,
    result: *mut DeskBoxSearchSealResultV1,
) -> u32 {
    if result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: non-null pointer is required by the ABI contract.
    let output = unsafe { &mut *result };
    if !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchSealResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchSealResultV1 {
        struct_size: size_of::<DeskBoxSearchSealResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    // SAFETY: a live, exclusively used handle is required for sealing.
    let Some(core) = (unsafe { core_from_handle_mut(handle) }) else {
        return output.status;
    };
    output.status = core.seal().err().unwrap_or(DESKBOX_SEARCH_STATUS_OK);
    output.entry_count = core.entries.len() as u32;
    output.directory_count = core.directories.len() as u32;
    output.status
}

/// Clears the cooperative cancellation flag before a new query.
///
/// # Safety
///
/// `handle` must be a live handle created by this module. The caller must not
/// reset cancellation while a query on the same handle is still executing.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_reset_cancel_v1(handle: *mut c_void) -> u32 {
    // SAFETY: a live handle created by this module is required.
    let Some(core) = (unsafe { core_from_handle(handle) }) else {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    };
    core.cancelled.store(false, AtomicOrdering::Release);
    DESKBOX_SEARCH_STATUS_OK
}

/// Requests cooperative cancellation of an in-flight query.
///
/// # Safety
///
/// `handle` must remain live for the entire call. This is the only export that
/// may be invoked concurrently with `deskbox_search_core_query_v1`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_cancel_v1(handle: *mut c_void) -> u32 {
    // SAFETY: a live handle created by this module is required. The atomic is
    // the only state that may be changed concurrently with a query.
    let Some(core) = (unsafe { core_from_handle(handle) }) else {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    };
    core.cancelled.store(true, AtomicOrdering::Release);
    DESKBOX_SEARCH_STATUS_OK
}

/// Searches a sealed index and writes bounded result descriptors.
///
/// # Safety
///
/// `handle` must remain live and sealed. `request`, its query/result buffers,
/// and `result` must be valid for their declared sizes for the whole call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_query_v1(
    handle: *mut c_void,
    request: *const DeskBoxSearchQueryRequestV1,
    result: *mut DeskBoxSearchQueryResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: pointers are non-null and must reference ABI structures.
    let request = unsafe { &*request };
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchQueryRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchQueryResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchQueryResultV1 {
        struct_size: size_of::<DeskBoxSearchQueryResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    // SAFETY: a live handle created by this module is required.
    let Some(core) = (unsafe { core_from_handle(handle) }) else {
        return output.status;
    };
    if request.query_length_chars == 0
        || request.query_length_chars as usize > MAX_QUERY_CHARS
        || request.max_results == 0
        || request.max_results as usize > MAX_ENTRY_COUNT
        || request.result_capacity < request.max_results
        || request.query.is_null()
        || request.results.is_null()
        || request.flags != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    // SAFETY: validated buffers contain the declared element counts.
    let query =
        unsafe { std::slice::from_raw_parts(request.query, request.query_length_chars as usize) };
    let results = unsafe {
        std::slice::from_raw_parts_mut(request.results, request.result_capacity as usize)
    };
    match core.search(query, request.max_results as usize) {
        Ok((candidates, matched, scanned)) => {
            let mut required_utf16_chars = 0usize;
            for (destination, candidate) in results.iter_mut().zip(candidates.iter()) {
                let entry = core.entries[candidate.entry_id as usize];
                let directory = core.directories[entry.directory_id as usize];
                required_utf16_chars = required_utf16_chars
                    .saturating_add(directory.length as usize)
                    .saturating_add(entry.file_name_length as usize);
                *destination = DeskBoxSearchResultV1 {
                    entry_id: candidate.entry_id,
                    score: candidate.score,
                    modified_utc_ticks: candidate.modified_utc_ticks,
                    flags: candidate.flags,
                    reserved0: 0,
                };
            }
            let Ok(required_utf16_chars) = u32::try_from(required_utf16_chars) else {
                output.status = DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
                return output.status;
            };
            output.status = DESKBOX_SEARCH_STATUS_OK;
            output.scanned_entry_count = scanned;
            output.matched_entry_count = matched;
            output.written_result_count = candidates.len() as u32;
            output.required_utf16_chars = required_utf16_chars;
        }
        Err(status) => output.status = status,
    }
    output.status
}

/// Copies directory/name text for selected entry identifiers.
///
/// # Safety
///
/// `handle` must remain live and sealed. Every pointer in `request` must refer
/// to a buffer of its declared capacity, and `result` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_copy_entries_v1(
    handle: *mut c_void,
    request: *const DeskBoxSearchCopyEntriesRequestV1,
    result: *mut DeskBoxSearchCopyEntriesResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: pointers are non-null and must reference ABI structures.
    let request = unsafe { &*request };
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchCopyEntriesRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchCopyEntriesResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchCopyEntriesResultV1 {
        struct_size: size_of::<DeskBoxSearchCopyEntriesResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    // SAFETY: a live handle created by this module is required.
    let Some(core) = (unsafe { core_from_handle(handle) }) else {
        return output.status;
    };
    if !core.sealed
        || request.entry_count == 0
        || request.entry_capacity < request.entry_count
        || request.entry_ids.is_null()
        || request.entries.is_null()
        || request.utf16_data.is_null()
        || request.reserved0 != 0
        || request.reserved1 != 0
        || request.flags != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    // SAFETY: validated non-null buffers contain the declared counts.
    let entry_ids =
        unsafe { std::slice::from_raw_parts(request.entry_ids, request.entry_count as usize) };
    let descriptors =
        unsafe { std::slice::from_raw_parts_mut(request.entries, request.entry_capacity as usize) };

    let mut required_chars = 0usize;
    for entry_id in entry_ids {
        let Some(entry) = core.entries.get(*entry_id as usize) else {
            return output.status;
        };
        if entry.tombstoned != 0 {
            return output.status;
        }
        required_chars = required_chars
            .checked_add(core.directory(entry.directory_id).len())
            .and_then(|value| value.checked_add(entry.file_name_length as usize))
            .ok_or(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)
            .unwrap_or(usize::MAX);
        if required_chars == usize::MAX {
            return output.status;
        }
    }
    let Ok(required_chars_u32) = u32::try_from(required_chars) else {
        return output.status;
    };
    output.required_utf16_chars = required_chars_u32;
    if request.utf16_capacity_chars < required_chars_u32 {
        output.status = DESKBOX_SEARCH_STATUS_BUFFER_TOO_SMALL;
        return output.status;
    }
    // SAFETY: the output buffer has at least the validated required capacity.
    let utf16_output = unsafe {
        std::slice::from_raw_parts_mut(request.utf16_data, request.utf16_capacity_chars as usize)
    };
    let mut cursor = 0usize;
    for (descriptor, entry_id) in descriptors.iter_mut().zip(entry_ids.iter()) {
        let entry = core.entries[*entry_id as usize];
        let directory = core.directory(entry.directory_id);
        let file_name = core.file_name(&entry);
        let directory_offset = cursor;
        utf16_output[cursor..cursor + directory.len()].copy_from_slice(directory);
        cursor += directory.len();
        let file_name_offset = cursor;
        utf16_output[cursor..cursor + file_name.len()].copy_from_slice(file_name);
        cursor += file_name.len();
        *descriptor = DeskBoxSearchEntryTextV1 {
            entry_id: *entry_id,
            directory_offset_chars: directory_offset as u32,
            directory_length_chars: directory.len() as u32,
            file_name_offset_chars: file_name_offset as u32,
            file_name_length_chars: file_name.len() as u32,
            flags: entry.flags,
            modified_utc_ticks: entry.modified_utc_ticks,
        };
    }
    output.status = DESKBOX_SEARCH_STATUS_OK;
    output.copied_entry_count = request.entry_count;
    output.status
}

/// Applies one validated mutation transaction to a sealed index.
///
/// All validation and allocation reservations complete before any logical
/// entry state changes. Removals observe the pre-transaction snapshot and all
/// upserts are then appended, so an upsert wins over a removal of the same path.
///
/// # Safety
///
/// `handle` must be live and exclusively owned for this mutation. Every request
/// pointer must remain valid for its declared count and `result` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_mutate_batch_v1(
    handle: *mut c_void,
    request: *const DeskBoxSearchMutateBatchRequestV1,
    result: *mut DeskBoxSearchMutateBatchResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: non-null pointers are required by the ABI contract.
    let request = unsafe { &*request };
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchMutateBatchRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchMutateBatchResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchMutateBatchResultV1 {
        struct_size: size_of::<DeskBoxSearchMutateBatchResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    // SAFETY: a live, exclusively used handle is required for mutation.
    let Some(core) = (unsafe { core_from_handle_mut(handle) }) else {
        return output.status;
    };
    if request.mutation_count == 0
        || request.mutation_count as usize > MAX_MUTATION_COUNT
        || request.utf16_length_chars == 0
        || request.utf16_length_chars as usize > MAX_UTF16_CHARS
        || request.mutations.is_null()
        || request.utf16_data.is_null()
        || request.reserved0 != 0
        || request.flags != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    // SAFETY: validated pointers reference the declared element counts.
    let mutations =
        unsafe { std::slice::from_raw_parts(request.mutations, request.mutation_count as usize) };
    let utf16 = unsafe {
        std::slice::from_raw_parts(request.utf16_data, request.utf16_length_chars as usize)
    };
    match core.mutate_batch(mutations, utf16) {
        Ok((_removed, tombstones)) => {
            output.status = DESKBOX_SEARCH_STATUS_OK;
            output.applied_mutation_count = request.mutation_count;
            output.live_entry_count = core.live_entry_count as u32;
            output.tombstone_count = tombstones;
            output.directory_count = core.directories.len() as u32;
        }
        Err(status) => {
            output.status = status;
            output.live_entry_count = core.live_entry_count as u32;
            output.tombstone_count = (core.entries.len() - core.live_entry_count) as u32;
            output.directory_count = core.directories.len() as u32;
        }
    }
    output.status
}

/// Produces a bounded recent-file or frequent-folder projection directly from
/// the native resident index and copies full paths into caller-owned UTF-16.
///
/// # Safety
///
/// `handle` must remain live and sealed. Request buffers and `result` must
/// remain valid for their declared capacities for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_project_v1(
    handle: *mut c_void,
    request: *const DeskBoxSearchProjectRequestV1,
    result: *mut DeskBoxSearchProjectResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: non-null pointers are required by the ABI contract.
    let request = unsafe { &*request };
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchProjectRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchProjectResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchProjectResultV1 {
        struct_size: size_of::<DeskBoxSearchProjectResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    // SAFETY: a live handle created by this module is required.
    let Some(core) = (unsafe { core_from_handle(handle) }) else {
        return output.status;
    };
    if request.max_results == 0
        || request.max_results as usize > MAX_ENTRY_COUNT
        || request.item_capacity < request.max_results
        || request.items.is_null()
        || request.utf16_data.is_null()
        || request.reserved0 != 0
        || request.flags != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    let (candidates, scanned) =
        match core.project(request.projection_kind, request.max_results as usize) {
            Ok(value) => value,
            Err(status) => {
                output.status = status;
                return output.status;
            }
        };
    let required_chars = candidates.iter().try_fold(0usize, |total, candidate| {
        let length = if request.projection_kind == DESKBOX_SEARCH_PROJECTION_RECENT_FILES {
            let entry = &core.entries[candidate.entry_id as usize];
            path_length_from_parts(core.directory(entry.directory_id), core.file_name(entry))
        } else {
            core.directory(candidate.entry_id).len()
        };
        total
            .checked_add(length)
            .ok_or(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)
    });
    let Ok(required_chars) = required_chars else {
        return output.status;
    };
    let Ok(required_chars_u32) = u32::try_from(required_chars) else {
        return output.status;
    };
    output.required_utf16_chars = required_chars_u32;
    output.scanned_entry_count = scanned;
    if request.utf16_capacity_chars < required_chars_u32 {
        output.status = DESKBOX_SEARCH_STATUS_BUFFER_TOO_SMALL;
        return output.status;
    }

    // SAFETY: buffers were validated against the capacities above.
    let items =
        unsafe { std::slice::from_raw_parts_mut(request.items, request.item_capacity as usize) };
    let utf16 = unsafe {
        std::slice::from_raw_parts_mut(request.utf16_data, request.utf16_capacity_chars as usize)
    };
    let mut cursor = 0usize;
    for (item, candidate) in items.iter_mut().zip(candidates.iter()) {
        let start = cursor;
        if request.projection_kind == DESKBOX_SEARCH_PROJECTION_RECENT_FILES {
            let entry = &core.entries[candidate.entry_id as usize];
            let directory = core.directory(entry.directory_id);
            let file_name = core.file_name(entry);
            utf16[cursor..cursor + directory.len()].copy_from_slice(directory);
            cursor += directory.len();
            if path_has_inserted_separator(directory) {
                utf16[cursor] = b'\\' as u16;
                cursor += 1;
            }
            utf16[cursor..cursor + file_name.len()].copy_from_slice(file_name);
            cursor += file_name.len();
        } else {
            let directory = core.directory(candidate.entry_id);
            utf16[cursor..cursor + directory.len()].copy_from_slice(directory);
            cursor += directory.len();
        }
        *item = DeskBoxSearchProjectionItemV1 {
            path_offset_chars: start as u32,
            path_length_chars: (cursor - start) as u32,
            rank_value: candidate.score,
            flags: candidate.flags,
            modified_utc_ticks: candidate.modified_utc_ticks,
        };
    }
    output.status = DESKBOX_SEARCH_STATUS_OK;
    output.written_item_count = candidates.len() as u32;
    output.status
}

/// Persists all live entries to a current DBIX v1 file via a same-volume temp
/// path and replace-existing write-through rename.
///
/// # Safety
///
/// `handle` must remain live and exclusively used. Both UTF-16 paths and the
/// optional waitable event must remain valid for the entire call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_save_dbix_v1(
    handle: *mut c_void,
    request: *const DeskBoxSearchSaveDbixRequestV1,
    result: *mut DeskBoxSearchSaveDbixResultV1,
) -> u32 {
    if request.is_null() || result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: non-null pointers are required by the ABI contract.
    let request = unsafe { &*request };
    let output = unsafe { &mut *result };
    if !envelope_matches(
        request.struct_size,
        request.struct_version,
        size_of::<DeskBoxSearchSaveDbixRequestV1>(),
    ) || !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchSaveDbixResultV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    *output = DeskBoxSearchSaveDbixResultV1 {
        struct_size: size_of::<DeskBoxSearchSaveDbixResultV1>() as u32,
        struct_version: DESKBOX_SEARCH_CORE_STRUCT_VERSION_1,
        status: DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT,
        ..Default::default()
    };
    // SAFETY: a live handle created by this module is required.
    let Some(core) = (unsafe { core_from_handle(handle) }) else {
        return output.status;
    };
    if request.path.is_null()
        || request.temp_path.is_null()
        || request.path_length_chars == 0
        || request.temp_path_length_chars == 0
        || request.path_length_chars as usize > MAX_QUERY_CHARS
        || request.temp_path_length_chars as usize > MAX_QUERY_CHARS
        || request.reserved0 != 0
        || request.flags != 0
        || !reserved_is_zero(&request.reserved)
    {
        return output.status;
    }
    // SAFETY: validated path pointers contain the declared UTF-16 units.
    let path =
        unsafe { std::slice::from_raw_parts(request.path, request.path_length_chars as usize) };
    let temp_path = unsafe {
        std::slice::from_raw_parts(request.temp_path, request.temp_path_length_chars as usize)
    };
    match dbix::save_dbix(core, path, temp_path, request.cancel_event) {
        Ok(metadata) => {
            output.status = DESKBOX_SEARCH_STATUS_OK;
            output.dbix_version = metadata.version;
            output.persisted_utc_ticks = metadata.persisted_utc_ticks;
            output.file_bytes = metadata.file_bytes;
            output.entry_count = metadata.entry_count;
            output.directory_count = metadata.directory_count;
        }
        Err(status) => output.status = status,
    }
    output.status
}

/// Returns tracked native capacity statistics for an index.
///
/// # Safety
///
/// `handle` must be live and `result` must point to a writable ABI v1 stats
/// structure for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_stats_v1(
    handle: *mut c_void,
    result: *mut DeskBoxSearchStatsV1,
) -> u32 {
    if result.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: non-null pointer is required by the ABI contract.
    let output = unsafe { &mut *result };
    if !envelope_matches(
        output.struct_size,
        output.struct_version,
        size_of::<DeskBoxSearchStatsV1>(),
    ) {
        return DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT;
    }
    // SAFETY: a live handle created by this module is required.
    let Some(core) = (unsafe { core_from_handle(handle) }) else {
        output.status = DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
        return output.status;
    };
    *output = core.tracked_stats();
    output.status
}

/// Destroys a SearchCore handle.
///
/// # Safety
///
/// `handle` must be a unique live handle returned by create. It must not be in
/// use by any other thread and must never be passed to this function again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_search_core_destroy_v1(handle: *mut c_void) -> u32 {
    if handle.is_null() {
        return DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT;
    }
    // SAFETY: the ABI contract requires a unique live handle created by
    // Box::into_raw in deskbox_search_core_create_v1.
    unsafe { drop(Box::from_raw(handle as *mut SearchCore)) };
    DESKBOX_SEARCH_STATUS_OK
}

#[cfg(test)]
mod tests {
    use super::*;

    fn utf16(value: &str) -> Vec<u16> {
        value.encode_utf16().collect()
    }

    fn add(core: &mut SearchCore, directory: &str, name: &str, ticks: i64, flags: u32) {
        let directory = utf16(directory);
        let name = utf16(name);
        let mut packed = directory.clone();
        packed.extend_from_slice(&name);
        let input = DeskBoxSearchEntryInputV1 {
            directory_offset_chars: 0,
            directory_length_chars: directory.len() as u32,
            file_name_offset_chars: directory.len() as u32,
            file_name_length_chars: name.len() as u32,
            modified_utc_ticks: ticks,
            flags,
            reserved0: 0,
        };
        assert_eq!(core.add_batch(&[input], &packed), Ok(1));
    }

    #[test]
    fn abi_v3_x64_layout_and_statuses_are_frozen() {
        assert_eq!(DESKBOX_SEARCH_CORE_ABI_VERSION, 3);
        assert_eq!(size_of::<DeskBoxSearchOpenDbixRequestV1>(), 72);
        assert_eq!(size_of::<DeskBoxSearchOpenDbixResultV1>(), 80);
        assert_eq!(size_of::<DeskBoxSearchCreateRequestV1>(), 56);
        assert_eq!(size_of::<DeskBoxSearchCreateResultV1>(), 56);
        assert_eq!(size_of::<DeskBoxSearchMutationInputV1>(), 56);
        assert_eq!(size_of::<DeskBoxSearchMutateBatchRequestV1>(), 72);
        assert_eq!(size_of::<DeskBoxSearchMutateBatchResultV1>(), 64);
        assert_eq!(size_of::<DeskBoxSearchProjectionItemV1>(), 24);
        assert_eq!(size_of::<DeskBoxSearchProjectRequestV1>(), 80);
        assert_eq!(size_of::<DeskBoxSearchProjectResultV1>(), 64);
        assert_eq!(size_of::<DeskBoxSearchSaveDbixRequestV1>(), 80);
        assert_eq!(size_of::<DeskBoxSearchSaveDbixResultV1>(), 72);
        assert_eq!(DESKBOX_SEARCH_STATUS_IO_ERROR, 7);
        assert_eq!(DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT, 8);
        assert_eq!(DESKBOX_SEARCH_STATUS_CORRUPT_DATA, 9);
    }

    #[test]
    fn relevance_matches_managed_order() {
        assert_eq!(compute_relevance(&utf16("report"), &utf16("REPORT")), 100);
        // Managed code checks full-name prefix before exact stem.
        assert_eq!(
            compute_relevance(&utf16("report.txt"), &utf16("report")),
            80
        );
        assert_eq!(
            compute_relevance(&utf16("xreport.txt"), &utf16("report")),
            50
        );
        assert_eq!(compute_relevance(&utf16("other.txt"), &utf16("report")), 0);
    }

    #[test]
    fn unicode_ordinal_ignore_case_is_supported() {
        assert!(ordinal_ignore_case_equals(&utf16("ÄBC"), &utf16("äbc")));
        assert!(ordinal_ignore_case_contains(
            &utf16("项目ÄBC.txt"),
            &utf16("äbc")
        ));
        assert!(ordinal_ignore_case_equals(&utf16("ς"), &utf16("σ")));
        assert!(ordinal_ignore_case_equals(&utf16("𐐀"), &utf16("𐐨")));
        assert!(!ordinal_ignore_case_equals(&utf16("K"), &utf16("k")));
        assert!(!ordinal_ignore_case_equals(&utf16("ı"), &utf16("I")));
        assert!(!ordinal_ignore_case_equals(&utf16("ß"), &utf16("ẞ")));
    }

    #[test]
    fn subtree_matching_is_boundary_aware_for_exact_child_root_and_unicode_paths() {
        let directory = utf16(r"C:\Root\子目录");
        let file_name = utf16("Report.txt");

        assert!(path_is_same_or_descendant(
            &directory,
            &file_name,
            &utf16(r"c:\root")
        ));
        assert!(path_is_same_or_descendant(
            &directory,
            &file_name,
            &utf16(r"C:\Root\")
        ));
        assert!(path_is_same_or_descendant(
            &directory,
            &file_name,
            &utf16(r"C:\Root\子目录\report.TXT")
        ));
        assert!(path_is_same_or_descendant(
            &directory,
            &file_name,
            &utf16(r"C:\")
        ));
        assert!(!path_is_same_or_descendant(
            &directory,
            &file_name,
            &utf16(r"C:\Rooted")
        ));
        assert!(!path_is_same_or_descendant(
            &directory,
            &file_name,
            &utf16(r"C:\Root\子目录\Report.txt\child")
        ));
    }

    #[test]
    fn directory_pool_deduplicates_case_insensitively() {
        let mut core = SearchCore::new(2, 64).unwrap();
        add(&mut core, r"C:\Data", "one.txt", 1, 0);
        add(&mut core, r"c:\data", "two.txt", 2, 0);
        assert_eq!(core.directories.len(), 1);
        assert_eq!(String::from_utf16_lossy(core.directory(0)), r"C:\Data");
    }

    #[test]
    fn top_n_is_score_then_modified_then_stable_id() {
        let mut core = SearchCore::new(4, 64).unwrap();
        add(&mut core, r"C:\D", "xalpha.txt", 10, 0);
        add(&mut core, r"C:\D", "alpha.txt", 5, 0);
        add(&mut core, r"C:\D", "alpha.md", 20, 0);
        add(&mut core, r"C:\D", "xalpha.md", 30, 0);
        core.seal().unwrap();

        let (results, matched, scanned) = core.search(&utf16("alpha"), 3).unwrap();
        assert_eq!(matched, 4);
        assert_eq!(scanned, 4);
        assert_eq!(
            results.iter().map(|item| item.entry_id).collect::<Vec<_>>(),
            vec![2, 1, 3]
        );
        assert_eq!(
            results.iter().map(|item| item.score).collect::<Vec<_>>(),
            vec![80, 80, 50]
        );
    }

    #[test]
    fn cancellation_is_observed_without_mutating_index() {
        let mut core = SearchCore::new(1, 16).unwrap();
        add(&mut core, r"C:\D", "alpha.txt", 1, 0);
        core.seal().unwrap();
        core.cancelled.store(true, AtomicOrdering::Release);
        assert_eq!(
            core.search(&utf16("a"), 1),
            Err(DESKBOX_SEARCH_STATUS_CANCELLED)
        );
        core.cancelled.store(false, AtomicOrdering::Release);
        assert_eq!(core.search(&utf16("a"), 1).unwrap().0.len(), 1);
    }

    #[test]
    fn sealed_mutation_transaction_upserts_removes_and_reconciles_stale_entries() {
        let mut core = SearchCore::new(4, 128).unwrap();
        add(&mut core, r"C:\Root", "old.txt", 10, 0);
        add(&mut core, r"C:\Root", "stale.txt", 20, 0);
        add(&mut core, r"C:\Other", "keep.txt", 30, 0);
        core.seal().unwrap();

        let directory = utf16(r"C:\Root");
        let file_name = utf16("old.txt");
        let stale_root = utf16(r"C:\Root");
        let mut packed = directory.clone();
        packed.extend_from_slice(&file_name);
        let stale_offset = packed.len() as u32;
        packed.extend_from_slice(&stale_root);
        let mutations = [
            DeskBoxSearchMutationInputV1 {
                operation: DESKBOX_SEARCH_MUTATION_UPSERT,
                flags: 0,
                path_offset_chars: 0,
                path_length_chars: 0,
                directory_offset_chars: 0,
                directory_length_chars: directory.len() as u32,
                file_name_offset_chars: directory.len() as u32,
                file_name_length_chars: file_name.len() as u32,
                modified_utc_ticks: 100,
                modified_binary: (100u64 | DOTNET_KIND_UTC) as i64,
                scan_generation: 7,
                reserved0: 0,
            },
            DeskBoxSearchMutationInputV1 {
                operation: DESKBOX_SEARCH_MUTATION_REMOVE_STALE_TREE,
                flags: 0,
                path_offset_chars: stale_offset,
                path_length_chars: stale_root.len() as u32,
                directory_offset_chars: 0,
                directory_length_chars: 0,
                file_name_offset_chars: 0,
                file_name_length_chars: 0,
                modified_utc_ticks: 0,
                modified_binary: 0,
                scan_generation: 7,
                reserved0: 0,
            },
        ];

        assert_eq!(core.mutate_batch(&mutations, &packed), Ok((2, 2)));
        assert_eq!(core.live_entry_count, 2);
        let old_results = core.search(&utf16("old"), 10).unwrap().0;
        assert_eq!(old_results.len(), 1);
        assert_eq!(old_results[0].modified_utc_ticks, 100);
        assert!(core.search(&utf16("stale"), 10).unwrap().0.is_empty());
        assert_eq!(core.search(&utf16("keep"), 10).unwrap().0.len(), 1);
    }

    #[test]
    fn invalid_mutation_leaves_logical_state_unchanged() {
        let mut core = SearchCore::new(1, 32).unwrap();
        add(&mut core, r"C:\Root", "keep.txt", 10, 0);
        core.seal().unwrap();
        let directory = utf16(r"C:\Root");
        let file_name = utf16("bad.txt");
        let mut packed = directory.clone();
        packed.extend_from_slice(&file_name);
        let mutation = DeskBoxSearchMutationInputV1 {
            operation: DESKBOX_SEARCH_MUTATION_UPSERT,
            flags: 0,
            path_offset_chars: 0,
            path_length_chars: 0,
            directory_offset_chars: 0,
            directory_length_chars: directory.len() as u32,
            file_name_offset_chars: directory.len() as u32,
            file_name_length_chars: file_name.len() as u32,
            modified_utc_ticks: 20,
            modified_binary: (21u64 | DOTNET_KIND_UTC) as i64,
            scan_generation: 1,
            reserved0: 0,
        };
        let before_entries = core.entries.len();
        let before_names = core.file_name_utf16.len();
        assert_eq!(
            core.mutate_batch(&[mutation], &packed),
            Err(DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT)
        );
        assert_eq!(core.live_entry_count, 1);
        assert_eq!(core.entries.len(), before_entries);
        assert_eq!(core.file_name_utf16.len(), before_names);
        assert_eq!(core.search(&utf16("keep"), 10).unwrap().0.len(), 1);
    }

    #[test]
    fn recent_and_frequent_projections_are_bounded_and_deterministic() {
        let mut core = SearchCore::new(5, 128).unwrap();
        add(&mut core, r"C:\A", "one.txt", 10, 0);
        add(&mut core, r"C:\A", "two.txt", 30, 0);
        add(&mut core, r"C:\B", "three.txt", 40, 0);
        add(&mut core, r"C:\B", "four.txt", 20, 0);
        add(
            &mut core,
            r"C:\B",
            "folder",
            50,
            DESKBOX_SEARCH_ENTRY_DIRECTORY,
        );
        core.seal().unwrap();

        let recent = core
            .project(DESKBOX_SEARCH_PROJECTION_RECENT_FILES, 2)
            .unwrap()
            .0;
        assert_eq!(
            recent
                .iter()
                .map(|item| item.modified_utc_ticks)
                .collect::<Vec<_>>(),
            vec![40, 30]
        );
        let frequent = core
            .project(DESKBOX_SEARCH_PROJECTION_FREQUENT_FOLDERS, 2)
            .unwrap()
            .0;
        assert_eq!(frequent.len(), 2);
        assert_eq!(frequent[0].score, 2);
        assert_eq!(frequent[0].modified_utc_ticks, 40);
        assert_eq!(
            String::from_utf16_lossy(core.directory(frequent[0].entry_id)),
            r"C:\B"
        );
        assert_eq!(
            String::from_utf16_lossy(core.directory(frequent[1].entry_id)),
            r"C:\A"
        );
    }

    #[test]
    fn sealed_stats_drop_build_lookup_and_track_compact_storage() {
        let mut core = SearchCore::new(1000, 16_000).unwrap();
        for index in 0..1000 {
            add(
                &mut core,
                r"C:\Shared\LongDirectory",
                &format!("document-{index:04}.txt"),
                index,
                0,
            );
        }
        core.seal().unwrap();
        let stats = core.tracked_stats();
        assert_eq!(stats.entry_count, 1000);
        assert_eq!(stats.directory_count, 1);
        assert_eq!(stats.build_lookup_capacity_bytes, 0);
        let naive_full_path_utf16_bytes =
            1000u64 * (r"C:\Shared\LongDirectory".encode_utf16().count() as u64 + 17) * 2;
        assert!(stats.total_tracked_capacity_bytes < naive_full_path_utf16_bytes);
    }
}
