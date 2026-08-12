# ModelArtifacts.NET

[![NuGet](https://img.shields.io/nuget/v/ModelArtifacts.NET.svg)](https://www.nuget.org/packages/ModelArtifacts.NET)

`ModelArtifacts.NET` is a reusable .NET 10 library for safely acquiring, versioning, caching, verifying, activating, rolling back, and cleaning up AI/model artifacts.

It deliberately **does not know how to run a model**. It manages files and artifact snapshots. Your embedding library, reranker, LLM host, tokenizer package, classifier, or application decides what those files mean and whether a candidate snapshot is valid.

That boundary is the whole point.

## Why this exists

Model consumers repeatedly need the same infrastructure:

- resolve a source revision;
- discover the files that belong to a runtime artifact set;
- download into isolated staging;
- verify sizes and SHA-256 hashes;
- keep partial downloads away from active files;
- retain a known-good current snapshot;
- survive remote outages by continuing from cache;
- prevent concurrent processes from corrupting the same cache;
- promote an update only after the consuming application proves it can actually use it.

`ModelArtifacts.NET` centralizes that machinery without importing ONNX Runtime, tokenizers, inference queues, vector semantics, prompts, context-window rules, or model-family presets.

## The lifecycle: source → candidate → validation → promotion

A newly downloaded revision **does not become current just because the transfer succeeded**.

```text
resolve source revision
        ↓
download to isolated staging
        ↓
verify transfer integrity
        ↓
create candidate snapshot
        ↓
RETURN CANDIDATE TO YOUR APPLICATION
        ↓
your application validates / loads it
        ↓
PromoteAsync(candidate)
        ↓
new snapshot becomes current atomically
        ↓
old snapshots may be cleaned
```

If application-specific validation fails, call `DiscardAsync(candidate)`. The previous current snapshot remains untouched.

This is intentionally a two-phase activation protocol. A valid ZIP/download/hash is not necessarily a valid embedding model, reranker, tokenizer, GGUF, ONNX graph, or application-specific artifact set.

## Install

```bash
dotnet add package ModelArtifacts.NET
```

The managed package targets .NET 10, enables nullable reference types, is trimming-friendly, and declares `IsAotCompatible=true`.

## Candidate-first usage

```csharp
using ModelArtifacts;

var source = new HuggingFaceArtifactSource(
    repositoryId: "my-org/my-model",
    selection: ArtifactSelection.Explicit(
        "runtime-int8-v1",
        "model.int8.onnx",
        "tokenizer.json",
        "config.json"));

using var artifacts = new ArtifactManager(source, new ArtifactManagerOptions
{
    UpdatePolicy = ArtifactUpdatePolicy.OnStartup
});

var candidate = await artifacts.ResolveCandidateAsync();

try
{
    // This is intentionally application-owned policy.
    // Example: construct your tokenizer, parse config, create an ONNX session,
    // validate tensor names, load a GGUF file, etc.
    await ValidateForMyApplicationAsync(candidate.Snapshot.DirectoryPath);

    await artifacts.PromoteAsync(candidate);
}
catch
{
    await artifacts.DiscardAsync(candidate);
    throw;
}
```

If `candidate.RequiresPromotion` is `false`, the manager returned an already-current cached snapshot or an unmanaged local directory. Calling `PromoteAsync`/`DiscardAsync` is harmless in those cases.

## Artifact snapshots

`ArtifactSnapshot` exposes generic information only:

- `ArtifactSetId`
- `SourceKind`
- `SourceIdentity`
- `SourceRevision`
- `ArtifactFingerprint`
- `DirectoryPath`
- selected `AssetPaths`
- whether the directory is managed by the cache

Use `snapshot.GetAssetPath("relative/path")` to get an absolute path rooted inside the snapshot.

The artifact fingerprint is deterministic over the selected managed artifact paths and file contents. Changing a managed artifact changes the fingerprint. The library makes no claim about embedding-space identity; a consumer may incorporate `ArtifactFingerprint` into a higher-level identity if useful.

## Hugging Face

Hugging Face sources resolve the requested revision (default `main`) through the model API and use the concrete source SHA when Hugging Face provides it.

```csharp
var source = new HuggingFaceArtifactSource(
    "org/repository",
    ArtifactSelection.Patterns(
        "onnx-int8-runtime-v1",
        "onnx/int8/*",
        "tokenizer/*"),
    revision: "main",
    accessToken: Environment.GetEnvironmentVariable("HF_TOKEN"));
```

Bearer credentials are attached to both repository-resolution and asset-download requests when configured.

### Asset selection is explicit

The Hugging Face source requires an `ArtifactSelection`. It does **not** blindly download an entire repository and it does not assume that `.onnx` or `tokenizer.json` are special.

Selections can be explicit paths:

```csharp
ArtifactSelection.Explicit(
    "fp32-runtime",
    "model.onnx",
    "model.onnx_data",
    "tokenizer.json");
```

or patterns:

```csharp
ArtifactSelection.Patterns("int4-export", "exports/int4/*");
```

`ArtifactSelection.All("stable-selection-id")` is available when downloading the full repository is genuinely intended.

The selection identity is part of cache identity. Two variants from the same repository and revision therefore remain isolated:

```text
same repository
  ├── INT4 selection
  ├── INT8 selection
  └── FP32 selection
```

## HTTP manifests

`HttpManifestArtifactSource` supports a generic JSON manifest. It is not ONNX-specific.

```json
{
  "artifactSetId": "acme-reranker-int8",
  "revision": "2026-08-12.1",
  "assets": [
    "tokenizer.json",
    {
      "path": "weights/model.bin",
      "url": "https://cdn.example.com/releases/model.bin",
      "size": 12345678,
      "sha256": "0123456789abcdef..."
    }
  ]
}
```

String assets resolve relative to the manifest URL. Object assets may use relative or absolute URLs and may supply expected `size` and `sha256` values.

For compatibility with earlier consumers, `modelId` is also accepted when `artifactSetId` is absent.

Custom request headers can be provided to the manifest source for private/internal HTTP stores.

## Local directories

Existing local directories are not copied into managed cache snapshots by default:

```csharp
var source = new LocalDirectoryArtifactSource("/models/my-model");
using var manager = new ArtifactManager(source);
var candidate = await manager.ResolveCandidateAsync();

Console.WriteLine(candidate.Snapshot.DirectoryPath); // original directory
Console.WriteLine(candidate.Snapshot.IsManaged);     // false
```

A selection can be supplied when only part of the directory should participate in artifact identity.

## Custom sources

Implement `IModelArtifactSource` to add S3, Azure Blob, GitHub Releases, ModelScope, internal registries, or another distribution mechanism:

```csharp
public sealed class MyArtifactSource : IModelArtifactSource
{
    public string SourceKind => "my-store";
    public string SourceIdentity => "models/acme";
    public string CacheVariant => "runtime-v2";

    public Task<ResolvedArtifactSet> ResolveAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        // Resolve a concrete revision and return generic ArtifactAsset records.
        throw new NotImplementedException();
    }

    public void ApplyDownloadHeaders(HttpRequestMessage request)
    {
        // Optional authentication for asset downloads.
    }
}
```

No changes to `ModelArtifacts.NET` are required for a custom source.

## Cache identity and layout

The managed cache identity includes:

```text
source kind
source identity
source cache variant / artifact selection identity
optional explicit cache key
```

A managed cache root contains approximately:

```text
<cache-root>/<stable-source-key-hash>/
  .lock
  current.json
  staging/
  snapshots/
    <revision>-<fingerprint-prefix>/
      ... selected artifacts ...
```

`current.json` is small and replaced atomically. It records the active snapshot, source/cache identity, source revision, selected asset paths, and artifact fingerprint.

Candidate snapshots live under `snapshots/` but are not referenced by `current.json` until promotion succeeds.

## Update policies

`ArtifactUpdatePolicy` supports:

- `OnStartup` — resolve the source revision when the manager resolves its startup candidate;
- `Manual` — use an existing current snapshot without checking remotely unless explicitly refreshed;
- `Never` — never check for an update while a current snapshot exists.

A first acquisition still resolves the source when no current snapshot exists.

Force a remote resolution with:

```csharp
var candidate = await manager.RefreshAsync();
```

or:

```csharp
var candidate = await manager.ResolveCandidateAsync(forceRemoteCheck: true);
```

## Offline cached fallback

If remote resolution fails and a valid current snapshot already exists, `ResolveCandidateAsync` returns the current snapshot with:

```csharp
candidate.IsOfflineFallback == true
```

This lets services continue starting and serving with their known-good cached artifacts during Hugging Face/CDN/network outages.

If no current snapshot exists, the source failure is surfaced normally.

## Download and cache safety

Managed downloads use the following protections:

- isolated staging directories;
- path normalization and traversal rejection;
- `.partial` files while transfer is incomplete;
- atomic move into the final asset path only after transfer succeeds;
- expected-size validation when supplied;
- SHA-256 verification when supplied;
- bounded retries for transient HTTP/network/I/O failures;
- transient HTTP handling for 408, 429, 500, 502, 503, and 504;
- `Retry-After` support;
- cross-process cache-directory locking with cancellation-aware waits;
- atomic `current.json` replacement;
- best-effort cleanup of old snapshots only after successful promotion;
- retry behavior for temporarily locked files;
- abandoned staging cleanup.

A cleanup failure does not roll back or invalidate a successfully promoted current snapshot.

## What the consuming model library should own

A consumer such as an embedding package should provide its own model policy:

```text
which repository/export to select
which files are required
precision/flavor presets
model configuration interpretation
tokenizer requirements
model/session loading
tensor contracts
context-window semantics
embedding-space identity
```

`ModelArtifacts.NET` should provide the rest:

```text
source resolution
downloads
retries
hash/size validation
staging
cache identity
candidate snapshots
promotion/discard
current snapshot metadata
offline fallback
locking
cleanup
artifact fingerprints
```

That means a future `OnnxTextEmbeddings.NET` refactor can load and validate an embedding candidate, then promote it only after its tokenizer and ONNX runtime initialize successfully—without the artifact package knowing what either of those things are.

The same flow works unchanged for rerankers, classifiers, GGUF files, tokenizers, ONNX LLMs, or auxiliary assets.

## Native AOT

The managed package is designed for Native AOT and uses source-generated `System.Text.Json` metadata for its internal persisted/source records.

The repository also contains `src/ModelArtifacts.Native`, which publishes as a Native AOT shared library exposing a stable C ABI.

The public header is:

```text
native/include/model_artifacts.h
```

The ABI uses:

- explicit ABI version discovery;
- `struct_size` + `abi_version` on creation options;
- opaque manager/candidate handles;
- UTF-8 pointer + byte length for JSON configuration;
- stable integer status codes;
- library-owned returned buffers;
- `ma_buffer_free` for allocator-safe release;
- last-error retrieval;
- managed exception containment at every export.

Complex candidate metadata is returned as versionable JSON to keep the ABI small and evolvable.

See [`docs/native-interop.md`](docs/native-interop.md).

## CI and release

CI validates managed build/tests and Native AOT integration on Linux, Windows, and macOS. The Native AOT integration publishes the shared library, compiles a standalone C consumer against the public header, and exercises real local artifact-manager behavior including buffer/error handling.

Merging package-affecting changes into `release` triggers `.github/workflows/publish-nuget.yml`. It detects package changes, resolves the next version from both Git tags and NuGet.org, validates across major OSes, packs the exact version, validates the `.nupkg`, authenticates with NuGet trusted publishing, tags `vX.Y.Z`, and creates a GitHub Release.

README, icon, license, source, and build-property changes are considered package-affecting because they are shipped by the package.

## License

Apache-2.0.
