# HTTP artifact manifest

The HTTP manifest format is generic and model-format agnostic.

```json
{
  "artifactSetId": "my-runtime-assets",
  "revision": "sha-or-version",
  "assets": [
    "relative/path.bin",
    {
      "path": "weights/file.bin",
      "url": "https://cdn.example.com/file.bin",
      "size": 1234,
      "sha256": "..."
    }
  ]
}
```

`artifactSetId` identifies the logical artifact set. `modelId` is accepted as a compatibility alias. `revision` should change whenever the source's selected managed contents change.

String assets resolve relative to the manifest URI. Object assets may use relative or absolute URLs and optional transfer-integrity metadata.

Asset paths are always treated as paths inside a staging/snapshot root. Rooted paths and traversal (`..`) are rejected.
