#ifndef MODEL_ARTIFACTS_H
#define MODEL_ARTIFACTS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MA_ABI_VERSION 1u

typedef intptr_t ma_handle;

typedef enum ma_status {
    MA_OK = 0,
    MA_INVALID_ARGUMENT = 1,
    MA_INVALID_HANDLE = 2,
    MA_SOURCE_ERROR = 3,
    MA_DOWNLOAD_ERROR = 4,
    MA_IO_ERROR = 5,
    MA_CANCELLED = 6,
    MA_OUT_OF_MEMORY = 7,
    MA_INTERNAL_ERROR = 255
} ma_status;

typedef struct ma_buffer {
    uint8_t* data;
    size_t length;
} ma_buffer;

typedef struct ma_manager_options {
    uint32_t struct_size;
    uint32_t abi_version;
    const uint8_t* config_json;
    size_t config_json_length;
} ma_manager_options;

uint32_t ma_abi_version(void);
int ma_get_last_error(ma_buffer* output);
void ma_buffer_free(ma_buffer* buffer);
int ma_manager_create(const ma_manager_options* options, ma_handle* output_handle);
int ma_manager_destroy(ma_handle handle);
int ma_manager_resolve_candidate(ma_handle manager_handle, int force_remote_check, ma_handle* output_candidate_handle);
int ma_candidate_metadata_json(ma_handle candidate_handle, ma_buffer* output);
int ma_candidate_path(ma_handle candidate_handle, ma_buffer* output);
int ma_candidate_promote(ma_handle manager_handle, ma_handle candidate_handle, int cleanup_obsolete_snapshots);
int ma_candidate_discard(ma_handle manager_handle, ma_handle candidate_handle);
int ma_manager_cleanup(ma_handle manager_handle);

#ifdef __cplusplus
}
#endif

#endif
