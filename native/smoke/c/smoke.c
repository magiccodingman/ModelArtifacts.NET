#include "model_artifacts.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int fail_with_last_error(const char* operation, int status) {
    ma_buffer error = {0};
    ma_get_last_error(&error);
    fprintf(stderr, "%s failed (%d): %.*s\n", operation, status, (int)error.length, error.data ? (const char*)error.data : "");
    ma_buffer_free(&error);
    return 1;
}

int main(int argc, char** argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: smoke <local-artifact-directory>\n");
        return 2;
    }
    if (ma_abi_version() != MA_ABI_VERSION) {
        fprintf(stderr, "ABI mismatch\n");
        return 3;
    }

    char path[2048];
    size_t path_len = strlen(argv[1]);
    if (path_len >= sizeof(path)) return 4;
    memcpy(path, argv[1], path_len + 1);
    for (size_t i = 0; i < path_len; i++) if (path[i] == '\\') path[i] = '/';

    char config[4096];
    int config_len = snprintf(config, sizeof(config), "{\"sourceKind\":\"localDirectory\",\"localDirectory\":\"%s\",\"selectAll\":true,\"selectionId\":\"native-smoke\"}", path);
    if (config_len <= 0 || (size_t)config_len >= sizeof(config)) return 5;

    ma_manager_options options = {0};
    options.struct_size = (uint32_t)sizeof(options);
    options.abi_version = MA_ABI_VERSION;
    options.config_json = (const uint8_t*)config;
    options.config_json_length = (size_t)config_len;

    ma_handle manager = 0;
    int status = ma_manager_create(&options, &manager);
    if (status != MA_OK) return fail_with_last_error("ma_manager_create", status);

    ma_handle candidate = 0;
    status = ma_manager_resolve_candidate(manager, 0, &candidate);
    if (status != MA_OK) return fail_with_last_error("ma_manager_resolve_candidate", status);

    ma_buffer metadata = {0};
    status = ma_candidate_metadata_json(candidate, &metadata);
    if (status != MA_OK) return fail_with_last_error("ma_candidate_metadata_json", status);
    if (metadata.length == 0 || strstr((const char*)metadata.data, "artifactFingerprint") == NULL) {
        fprintf(stderr, "metadata missing fingerprint\n");
        return 6;
    }
    ma_buffer_free(&metadata);
    if (metadata.data != NULL || metadata.length != 0) return 7;

    ma_buffer local_path = {0};
    status = ma_candidate_path(candidate, &local_path);
    if (status != MA_OK || local_path.length == 0) return fail_with_last_error("ma_candidate_path", status);
    ma_buffer_free(&local_path);

    ma_buffer invalid = {0};
    status = ma_candidate_path((ma_handle)999999, &invalid);
    if (status != MA_INVALID_HANDLE) {
        fprintf(stderr, "expected invalid handle status\n");
        return 8;
    }
    ma_buffer error = {0};
    ma_get_last_error(&error);
    if (error.length == 0) return 9;
    ma_buffer_free(&error);

    status = ma_candidate_discard(manager, candidate);
    if (status != MA_OK) return fail_with_last_error("ma_candidate_discard", status);
    status = ma_manager_cleanup(manager);
    if (status != MA_OK) return fail_with_last_error("ma_manager_cleanup", status);
    status = ma_manager_destroy(manager);
    if (status != MA_OK) return fail_with_last_error("ma_manager_destroy", status);

    puts("ModelArtifacts.Native C smoke passed");
    return 0;
}
