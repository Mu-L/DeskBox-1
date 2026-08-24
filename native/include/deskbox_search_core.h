#ifndef DESKBOX_SEARCH_CORE_H
#define DESKBOX_SEARCH_CORE_H

#include <stdint.h>

#if defined(_WIN32)
#define DESKBOX_SEARCH_CALL __cdecl
#else
#define DESKBOX_SEARCH_CALL
#endif

#if defined(__cplusplus)
extern "C" {
#endif

#define DESKBOX_SEARCH_CORE_ABI_VERSION 3u
#define DESKBOX_SEARCH_CORE_STRUCT_VERSION_1 1u

#define DESKBOX_SEARCH_STATUS_OK 0u
#define DESKBOX_SEARCH_STATUS_INVALID_ARGUMENT 1u
#define DESKBOX_SEARCH_STATUS_INCOMPATIBLE_STRUCT 2u
#define DESKBOX_SEARCH_STATUS_BUFFER_TOO_SMALL 3u
#define DESKBOX_SEARCH_STATUS_INVALID_STATE 4u
#define DESKBOX_SEARCH_STATUS_CANCELLED 5u
#define DESKBOX_SEARCH_STATUS_ALLOCATION_FAILED 6u
#define DESKBOX_SEARCH_STATUS_IO_ERROR 7u
#define DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT 8u
#define DESKBOX_SEARCH_STATUS_CORRUPT_DATA 9u

#define DESKBOX_SEARCH_ENTRY_DIRECTORY (1u << 0)
#define DESKBOX_SEARCH_MUTATION_UPSERT 1u
#define DESKBOX_SEARCH_MUTATION_REMOVE_EXACT 2u
#define DESKBOX_SEARCH_MUTATION_REMOVE_TREE 3u
#define DESKBOX_SEARCH_MUTATION_REMOVE_STALE_TREE 4u
#define DESKBOX_SEARCH_PROJECTION_RECENT_FILES 1u
#define DESKBOX_SEARCH_PROJECTION_FREQUENT_FOLDERS 2u

typedef struct DeskBoxSearchCreateRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t initial_entry_capacity;
    uint32_t initial_utf16_capacity_chars;
    uint32_t flags;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxSearchCreateRequestV1;

typedef struct DeskBoxSearchCreateResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t reserved0;
    void* handle;
    uint64_t reserved[4];
} DeskBoxSearchCreateResultV1;

typedef struct DeskBoxSearchOpenDbixRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    const uint16_t* path;
    uint32_t path_length_chars;
    uint32_t max_entry_count;
    uint32_t flags;
    uint32_t reserved0;
    void* cancel_event;
    uint64_t reserved[4];
} DeskBoxSearchOpenDbixRequestV1;

typedef struct DeskBoxSearchOpenDbixResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t dbix_version;
    void* handle;
    int64_t persisted_utc_ticks;
    uint64_t source_file_bytes;
    uint32_t entry_count;
    uint32_t directory_count;
    uint64_t reserved[4];
} DeskBoxSearchOpenDbixResultV1;

typedef struct DeskBoxSearchEntryInputV1 {
    uint32_t directory_offset_chars;
    uint32_t directory_length_chars;
    uint32_t file_name_offset_chars;
    uint32_t file_name_length_chars;
    int64_t modified_utc_ticks;
    uint32_t flags;
    uint32_t reserved0;
} DeskBoxSearchEntryInputV1;

typedef struct DeskBoxSearchAddBatchRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    const DeskBoxSearchEntryInputV1* entries;
    uint32_t entry_count;
    uint32_t reserved0;
    const uint16_t* utf16_data;
    uint32_t utf16_length_chars;
    uint32_t reserved1;
    uint64_t reserved[4];
} DeskBoxSearchAddBatchRequestV1;

typedef struct DeskBoxSearchAddBatchResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t added_entry_count;
    uint32_t total_entry_count;
    uint32_t directory_count;
    uint64_t reserved[4];
} DeskBoxSearchAddBatchResultV1;

typedef struct DeskBoxSearchSealResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t entry_count;
    uint32_t directory_count;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxSearchSealResultV1;

typedef struct DeskBoxSearchResultV1 {
    uint32_t entry_id;
    uint32_t score;
    int64_t modified_utc_ticks;
    uint32_t flags;
    uint32_t reserved0;
} DeskBoxSearchResultV1;

typedef struct DeskBoxSearchQueryRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    const uint16_t* query;
    uint32_t query_length_chars;
    uint32_t max_results;
    DeskBoxSearchResultV1* results;
    uint32_t result_capacity;
    uint32_t flags;
    uint64_t reserved[4];
} DeskBoxSearchQueryRequestV1;

typedef struct DeskBoxSearchQueryResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t scanned_entry_count;
    uint32_t matched_entry_count;
    uint32_t written_result_count;
    uint32_t required_utf16_chars;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxSearchQueryResultV1;

typedef struct DeskBoxSearchEntryTextV1 {
    uint32_t entry_id;
    uint32_t directory_offset_chars;
    uint32_t directory_length_chars;
    uint32_t file_name_offset_chars;
    uint32_t file_name_length_chars;
    uint32_t flags;
    int64_t modified_utc_ticks;
} DeskBoxSearchEntryTextV1;

typedef struct DeskBoxSearchCopyEntriesRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    const uint32_t* entry_ids;
    uint32_t entry_count;
    uint32_t reserved0;
    DeskBoxSearchEntryTextV1* entries;
    uint32_t entry_capacity;
    uint32_t reserved1;
    uint16_t* utf16_data;
    uint32_t utf16_capacity_chars;
    uint32_t flags;
    uint64_t reserved[4];
} DeskBoxSearchCopyEntriesRequestV1;

typedef struct DeskBoxSearchCopyEntriesResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t copied_entry_count;
    uint32_t required_utf16_chars;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxSearchCopyEntriesResultV1;

typedef struct DeskBoxSearchStatsV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t sealed;
    uint32_t entry_count;
    uint32_t directory_count;
    uint64_t entry_capacity_bytes;
    uint64_t directory_descriptor_capacity_bytes;
    uint64_t directory_utf16_capacity_bytes;
    uint64_t file_name_utf16_capacity_bytes;
    uint64_t build_lookup_capacity_bytes;
    uint64_t total_tracked_capacity_bytes;
    uint64_t reserved[4];
} DeskBoxSearchStatsV1;

typedef struct DeskBoxSearchMutationInputV1 {
    uint32_t operation;
    uint32_t flags;
    uint32_t path_offset_chars;
    uint32_t path_length_chars;
    uint32_t directory_offset_chars;
    uint32_t directory_length_chars;
    uint32_t file_name_offset_chars;
    uint32_t file_name_length_chars;
    int64_t modified_utc_ticks;
    int64_t modified_binary;
    uint32_t scan_generation;
    uint32_t reserved0;
} DeskBoxSearchMutationInputV1;

typedef struct DeskBoxSearchMutateBatchRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    const DeskBoxSearchMutationInputV1* mutations;
    uint32_t mutation_count;
    uint32_t reserved0;
    const uint16_t* utf16_data;
    uint32_t utf16_length_chars;
    uint32_t flags;
    uint64_t reserved[4];
} DeskBoxSearchMutateBatchRequestV1;

typedef struct DeskBoxSearchMutateBatchResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t applied_mutation_count;
    uint32_t live_entry_count;
    uint32_t tombstone_count;
    uint32_t directory_count;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxSearchMutateBatchResultV1;

typedef struct DeskBoxSearchProjectionItemV1 {
    uint32_t path_offset_chars;
    uint32_t path_length_chars;
    uint32_t rank_value;
    uint32_t flags;
    int64_t modified_utc_ticks;
} DeskBoxSearchProjectionItemV1;

typedef struct DeskBoxSearchProjectRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t projection_kind;
    uint32_t max_results;
    DeskBoxSearchProjectionItemV1* items;
    uint32_t item_capacity;
    uint32_t reserved0;
    uint16_t* utf16_data;
    uint32_t utf16_capacity_chars;
    uint32_t flags;
    uint64_t reserved[4];
} DeskBoxSearchProjectRequestV1;

typedef struct DeskBoxSearchProjectResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t written_item_count;
    uint32_t required_utf16_chars;
    uint32_t scanned_entry_count;
    uint32_t reserved0;
    uint32_t reserved1;
    uint64_t reserved[4];
} DeskBoxSearchProjectResultV1;

typedef struct DeskBoxSearchSaveDbixRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    const uint16_t* path;
    uint32_t path_length_chars;
    uint32_t reserved0;
    const uint16_t* temp_path;
    uint32_t temp_path_length_chars;
    uint32_t flags;
    void* cancel_event;
    uint64_t reserved[4];
} DeskBoxSearchSaveDbixRequestV1;

typedef struct DeskBoxSearchSaveDbixResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    uint32_t dbix_version;
    int64_t persisted_utc_ticks;
    uint64_t file_bytes;
    uint32_t entry_count;
    uint32_t directory_count;
    uint64_t reserved[4];
} DeskBoxSearchSaveDbixResultV1;

uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_abi_version(void);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_open_dbix_v1(
    const DeskBoxSearchOpenDbixRequestV1* request,
    DeskBoxSearchOpenDbixResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_create_v1(
    const DeskBoxSearchCreateRequestV1* request,
    DeskBoxSearchCreateResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_add_batch_v1(
    void* handle,
    const DeskBoxSearchAddBatchRequestV1* request,
    DeskBoxSearchAddBatchResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_seal_v1(
    void* handle,
    DeskBoxSearchSealResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_reset_cancel_v1(void* handle);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_cancel_v1(void* handle);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_query_v1(
    void* handle,
    const DeskBoxSearchQueryRequestV1* request,
    DeskBoxSearchQueryResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_copy_entries_v1(
    void* handle,
    const DeskBoxSearchCopyEntriesRequestV1* request,
    DeskBoxSearchCopyEntriesResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_mutate_batch_v1(
    void* handle,
    const DeskBoxSearchMutateBatchRequestV1* request,
    DeskBoxSearchMutateBatchResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_project_v1(
    void* handle,
    const DeskBoxSearchProjectRequestV1* request,
    DeskBoxSearchProjectResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_save_dbix_v1(
    void* handle,
    const DeskBoxSearchSaveDbixRequestV1* request,
    DeskBoxSearchSaveDbixResultV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_stats_v1(
    void* handle,
    DeskBoxSearchStatsV1* result);
uint32_t DESKBOX_SEARCH_CALL deskbox_search_core_destroy_v1(void* handle);

#if defined(__cplusplus)
}

#if defined(_WIN64)
static_assert(sizeof(DeskBoxSearchCreateRequestV1) == 56, "SearchCore create request layout changed");
static_assert(sizeof(DeskBoxSearchCreateResultV1) == 56, "SearchCore create result layout changed");
static_assert(sizeof(DeskBoxSearchOpenDbixRequestV1) == 72, "SearchCore DBIX request layout changed");
static_assert(sizeof(DeskBoxSearchOpenDbixResultV1) == 80, "SearchCore DBIX result layout changed");
static_assert(sizeof(DeskBoxSearchEntryInputV1) == 32, "SearchCore entry input layout changed");
static_assert(sizeof(DeskBoxSearchAddBatchRequestV1) == 72, "SearchCore add request layout changed");
static_assert(sizeof(DeskBoxSearchQueryRequestV1) == 72, "SearchCore query request layout changed");
static_assert(sizeof(DeskBoxSearchCopyEntriesRequestV1) == 88, "SearchCore copy request layout changed");
static_assert(sizeof(DeskBoxSearchStatsV1) == 104, "SearchCore stats layout changed");
static_assert(sizeof(DeskBoxSearchMutationInputV1) == 56, "SearchCore mutation input layout changed");
static_assert(sizeof(DeskBoxSearchMutateBatchRequestV1) == 72, "SearchCore mutate request layout changed");
static_assert(sizeof(DeskBoxSearchMutateBatchResultV1) == 64, "SearchCore mutate result layout changed");
static_assert(sizeof(DeskBoxSearchProjectionItemV1) == 24, "SearchCore projection item layout changed");
static_assert(sizeof(DeskBoxSearchProjectRequestV1) == 80, "SearchCore project request layout changed");
static_assert(sizeof(DeskBoxSearchProjectResultV1) == 64, "SearchCore project result layout changed");
static_assert(sizeof(DeskBoxSearchSaveDbixRequestV1) == 80, "SearchCore save request layout changed");
static_assert(sizeof(DeskBoxSearchSaveDbixResultV1) == 72, "SearchCore save result layout changed");
#endif
#endif

#endif
