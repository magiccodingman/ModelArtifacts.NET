using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelArtifacts;

namespace ModelArtifacts.Native;

internal static unsafe class NativeRuntime
{
    public const uint AbiVersion = 1;
    private static readonly ConcurrentDictionary<nint, ArtifactManager> Managers = new();
    private static readonly ConcurrentDictionary<nint, ArtifactCandidate> Candidates = new();
    private static long nextHandle;

    [ThreadStatic]
    private static string? lastError;

    public static string LastError => lastError ?? string.Empty;
    public static void ClearError() => lastError = null;
    public static void SetError(Exception exception) => lastError = exception.Message;

    public static MaStatus MapException(Exception exception) => exception switch
    {
        ArgumentException => MaStatus.InvalidArgument,
        ArtifactSourceException => MaStatus.SourceError,
        ArtifactDownloadException => MaStatus.DownloadError,
        OperationCanceledException => MaStatus.Cancelled,
        IOException => MaStatus.IoError,
        OutOfMemoryException => MaStatus.OutOfMemory,
        _ => MaStatus.InternalError
    };

    public static nint AddManager(ArtifactManager manager)
    {
        var handle = NewHandle();
        if (!Managers.TryAdd(handle, manager)) throw new InvalidOperationException("Unable to allocate manager handle.");
        return handle;
    }

    public static ArtifactManager GetManager(nint handle) => Managers.TryGetValue(handle, out var manager) ? manager : throw new InvalidHandleException("Invalid artifact manager handle.");

    public static ArtifactManager RemoveManager(nint handle) => Managers.TryRemove(handle, out var manager) ? manager : throw new InvalidHandleException("Invalid artifact manager handle.");

    public static nint AddCandidate(ArtifactCandidate candidate)
    {
        var handle = NewHandle();
        if (!Candidates.TryAdd(handle, candidate)) throw new InvalidOperationException("Unable to allocate candidate handle.");
        return handle;
    }

    public static ArtifactCandidate GetCandidate(nint handle) => Candidates.TryGetValue(handle, out var candidate) ? candidate : throw new InvalidHandleException("Invalid artifact candidate handle.");

    public static ArtifactCandidate RemoveCandidate(nint handle) => Candidates.TryRemove(handle, out var candidate) ? candidate : throw new InvalidHandleException("Invalid artifact candidate handle.");

    public static string ReadUtf8(byte* value, nuint length)
    {
        if (value is null && length != 0) throw new ArgumentNullException(nameof(value));
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value, checked((int)length)));
    }

    public static void WriteBuffer(string value, MaBuffer* output) => WriteBuffer(Encoding.UTF8.GetBytes(value), output);

    public static void WriteBuffer(ReadOnlySpan<byte> bytes, MaBuffer* output)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        output->Data = null;
        output->Length = 0;
        if (bytes.Length == 0) return;
        var memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        if (memory is null) throw new OutOfMemoryException();
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        output->Data = memory;
        output->Length = (nuint)bytes.Length;
    }

    public static ArtifactManager CreateManager(string configJson)
    {
        var config = JsonSerializer.Deserialize(configJson, NativeJsonContext.Default.NativeManagerConfig)
            ?? throw new ArgumentException("Native manager configuration JSON is empty.");
        var source = CreateSource(config);
        var options = new ArtifactManagerOptions
        {
            CacheDirectory = config.CacheDirectory,
            CacheKey = config.CacheKey,
            UpdatePolicy = ParseUpdatePolicy(config.UpdatePolicy)
        };
        return new ArtifactManager(source, options);
    }

    public static string CandidateMetadataJson(ArtifactCandidate candidate)
    {
        var snapshot = candidate.Snapshot;
        var metadata = new NativeCandidateMetadata(
            snapshot.ArtifactSetId,
            snapshot.SourceKind,
            snapshot.SourceIdentity,
            snapshot.SourceRevision,
            snapshot.ArtifactFingerprint,
            snapshot.DirectoryPath,
            snapshot.AssetPaths.ToArray(),
            snapshot.IsManaged,
            candidate.RequiresPromotion,
            candidate.IsOfflineFallback);
        return JsonSerializer.Serialize(metadata, NativeJsonContext.Default.NativeCandidateMetadata);
    }

    private static IModelArtifactSource CreateSource(NativeManagerConfig config)
    {
        return config.SourceKind switch
        {
            "localDirectory" => new LocalDirectoryArtifactSource(Required(config.LocalDirectory, "localDirectory"), CreateSelection(config, defaultAll: true), config.SourceIdentity),
            "httpManifest" => new HttpManifestArtifactSource(new Uri(Required(config.ManifestUri, "manifestUri"), UriKind.Absolute), CreateOptionalSelection(config), identity: config.SourceIdentity),
            "huggingFace" => new HuggingFaceArtifactSource(Required(config.RepositoryId, "repositoryId"), CreateSelection(config, defaultAll: false), config.Revision ?? "main", config.AccessToken),
            _ => throw new ArgumentException($"Unsupported sourceKind '{config.SourceKind}'.")
        };
    }

    private static ArtifactSelection CreateSelection(NativeManagerConfig config, bool defaultAll)
    {
        if (config.AssetPaths is { Length: > 0 }) return ArtifactSelection.Explicit(config.SelectionId ?? "paths", config.AssetPaths);
        if (config.AssetPatterns is { Length: > 0 }) return ArtifactSelection.Patterns(config.SelectionId ?? "patterns", config.AssetPatterns);
        if (config.SelectAll || defaultAll) return ArtifactSelection.All(config.SelectionId ?? "all");
        throw new ArgumentException("Hugging Face sources require assetPaths, assetPatterns, or selectAll=true with a stable selectionId.");
    }

    private static ArtifactSelection? CreateOptionalSelection(NativeManagerConfig config)
    {
        if (config.AssetPaths is { Length: > 0 }) return ArtifactSelection.Explicit(config.SelectionId ?? "paths", config.AssetPaths);
        if (config.AssetPatterns is { Length: > 0 }) return ArtifactSelection.Patterns(config.SelectionId ?? "patterns", config.AssetPatterns);
        return config.SelectAll ? ArtifactSelection.All(config.SelectionId ?? "all") : null;
    }

    private static ArtifactUpdatePolicy ParseUpdatePolicy(string? value) => value?.ToLowerInvariant() switch
    {
        null or "onstartup" => ArtifactUpdatePolicy.OnStartup,
        "manual" => ArtifactUpdatePolicy.Manual,
        "never" => ArtifactUpdatePolicy.Never,
        _ => throw new ArgumentException($"Unsupported updatePolicy '{value}'.")
    };

    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"'{name}' is required.");

    private static nint NewHandle() => checked((nint)Interlocked.Increment(ref nextHandle));
}

internal sealed class InvalidHandleException(string message) : Exception(message);

internal sealed class NativeManagerConfig
{
    [JsonPropertyName("sourceKind")] public string? SourceKind { get; init; }
    [JsonPropertyName("sourceIdentity")] public string? SourceIdentity { get; init; }
    [JsonPropertyName("repositoryId")] public string? RepositoryId { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("accessToken")] public string? AccessToken { get; init; }
    [JsonPropertyName("manifestUri")] public string? ManifestUri { get; init; }
    [JsonPropertyName("localDirectory")] public string? LocalDirectory { get; init; }
    [JsonPropertyName("selectionId")] public string? SelectionId { get; init; }
    [JsonPropertyName("assetPaths")] public string[]? AssetPaths { get; init; }
    [JsonPropertyName("assetPatterns")] public string[]? AssetPatterns { get; init; }
    [JsonPropertyName("selectAll")] public bool SelectAll { get; init; }
    [JsonPropertyName("cacheDirectory")] public string? CacheDirectory { get; init; }
    [JsonPropertyName("cacheKey")] public string? CacheKey { get; init; }
    [JsonPropertyName("updatePolicy")] public string? UpdatePolicy { get; init; }
}

internal sealed record NativeCandidateMetadata(
    string ArtifactSetId,
    string SourceKind,
    string SourceIdentity,
    string SourceRevision,
    string ArtifactFingerprint,
    string DirectoryPath,
    string[] AssetPaths,
    bool IsManaged,
    bool RequiresPromotion,
    bool IsOfflineFallback);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(NativeManagerConfig))]
[JsonSerializable(typeof(NativeCandidateMetadata))]
internal sealed partial class NativeJsonContext : JsonSerializerContext;
