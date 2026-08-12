# Native C interop

`ModelArtifacts.Native` publishes the managed artifact manager as a Native AOT shared library with a small C ABI declared in `native/include/model_artifacts.h`.

## ABI rules

ABI v1 uses opaque `ma_handle` values for managers and candidates. `ma_manager_options` starts with `struct_size` and `abi_version`; callers should zero-initialize the structure, set both fields, then provide UTF-8 JSON plus an explicit byte length.

Returned `ma_buffer` memory belongs to the library. Always release it with `ma_buffer_free`. Do not use `free`, `delete`, `CoTaskMemFree`, or another foreign allocator.

No managed exception crosses the native boundary. Functions return `ma_status` values and store a thread-local error string retrievable with `ma_get_last_error`.

## Configuration JSON

Local directory:

```json
{
  "sourceKind": "localDirectory",
  "localDirectory": "/models/acme",
  "selectAll": true,
  "selectionId": "runtime-v1"
}
```

HTTP manifest:

```json
{
  "sourceKind": "httpManifest",
  "manifestUri": "https://models.example.com/acme/manifest.json",
  "updatePolicy": "onStartup"
}
```

Hugging Face:

```json
{
  "sourceKind": "huggingFace",
  "repositoryId": "org/repo",
  "revision": "main",
  "selectionId": "int8-runtime-v1",
  "assetPatterns": ["exports/int8/*"]
}
```

`updatePolicy` accepts `onStartup`, `manual`, or `never`.

## Candidate flow

1. `ma_manager_create`
2. `ma_manager_resolve_candidate`
3. inspect `ma_candidate_metadata_json` and/or `ma_candidate_path`
4. application validates the files
5. call `ma_candidate_promote` on success, or `ma_candidate_discard` on failure
6. `ma_manager_cleanup` when desired
7. `ma_manager_destroy`

The repository C smoke consumer follows the same lifecycle against a tiny local artifact directory.
