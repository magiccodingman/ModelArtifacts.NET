# Architecture

`ModelArtifacts.NET` treats every managed download as an **artifact snapshot**, never as a model.

The core boundary is intentionally simple:

```text
IModelArtifactSource
    ↓ resolve concrete revision/assets
ArtifactManager
    ↓ stage + download + verify
ArtifactCandidate
    ↓ consumer validates application semantics
PromoteAsync / DiscardAsync
    ↓
ArtifactSnapshot becomes current, or is removed
```

## Ownership

The artifact layer owns source resolution, artifact selection identity, safe paths, transfer integrity, cache locking, staging, fingerprinting, current metadata, promotion, rollback-by-non-promotion, offline fallback, and cleanup.

Consumers own required-file policy and all runtime semantics. There is no ONNX Runtime dependency and no tokenizer, tensor, embedding, reranker, prompt, context-window, or vector logic in the package.

## Current record

`current.json` is the only activation pointer. Downloading and moving a candidate into `snapshots/` does not change it. Promotion writes a temporary current record and atomically replaces `current.json`.

Because the old current record is untouched until promotion, consumer validation/load failure can safely discard the candidate.

## Cache identity

Cache identity hashes source kind, source identity, source cache variant/selection identity, and an optional explicit cache key. This lets one repository safely host multiple independently managed exports.

## Fingerprints

An artifact fingerprint hashes the ordered relative paths and SHA-256 file hashes of the selected managed artifact set. It is content-oriented and model-agnostic.

## Local directories

Local directories resolve to unmanaged snapshots in place. They are fingerprinted using the selected files but are not copied or promoted through `current.json`.
