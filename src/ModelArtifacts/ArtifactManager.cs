using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelArtifacts;

public sealed class ArtifactManager : IDisposable
{
    private const string CandidateLeasesDirectoryName = "candidate-leases";

    private readonly IModelArtifactSource source;
    private readonly ArtifactManagerOptions options;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly string cacheIdentity;

    public ArtifactManager(IModelArtifactSource source, ArtifactManagerOptions? options = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.options = options ?? new ArtifactManagerOptions();
        this.options.Validate();
        this.httpClient = httpClient ?? new HttpClient();
        ownsHttpClient = httpClient is null;
        cacheIdentity = BuildCacheIdentity(source, this.options.CacheKey);
    }

    public async Task<ArtifactCandidate> ResolveCandidateAsync(bool forceRemoteCheck = false, CancellationToken cancellationToken = default)
    {
        var resolvedLocal = source.SourceKind == "local-directory"
            ? await source.ResolveAsync(httpClient, cancellationToken).ConfigureAwait(false)
            : null;
        if (resolvedLocal is not null)
            return await CreateLocalCandidateAsync(resolvedLocal, cancellationToken).ConfigureAwait(false);

        var cacheRoot = GetCacheRoot();
        Directory.CreateDirectory(cacheRoot);
        await using var cacheLock = await AcquireLockAsync(cacheRoot, cancellationToken).ConfigureAwait(false);
        CleanupStaging(cacheRoot);
        CleanupDanglingCandidateLeases(cacheRoot);
        var current = await TryLoadCurrentAsync(cacheRoot, cancellationToken).ConfigureAwait(false);

        if (!forceRemoteCheck && current is not null && options.UpdatePolicy is ArtifactUpdatePolicy.Never or ArtifactUpdatePolicy.Manual)
            return new ArtifactCandidate(current, false, false, cacheRoot, cacheIdentity);

        ResolvedArtifactSet remote;
        try
        {
            remote = await source.ResolveAsync(httpClient, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (current is not null && ex is not OperationCanceledException)
        {
            return new ArtifactCandidate(current, false, true, cacheRoot, cacheIdentity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ArtifactException)
        {
            throw new ArtifactSourceException($"Unable to resolve artifact source '{source.SourceIdentity}'.", ex);
        }

        if (current is not null && string.Equals(current.SourceRevision, remote.Revision, StringComparison.Ordinal))
            return new ArtifactCandidate(current, false, false, cacheRoot, cacheIdentity);

        ValidateResolvedAssets(remote);
        var snapshot = await DownloadCandidateAsync(cacheRoot, remote, cancellationToken).ConfigureAwait(false);
        return new ArtifactCandidate(snapshot, true, false, cacheRoot, cacheIdentity);
    }

    public Task<ArtifactCandidate> RefreshAsync(CancellationToken cancellationToken = default) =>
        ResolveCandidateAsync(forceRemoteCheck: true, cancellationToken);

    public async Task<ArtifactSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (source.SourceKind == "local-directory")
            return (await ResolveCandidateAsync(false, cancellationToken).ConfigureAwait(false)).Snapshot;

        var cacheRoot = GetCacheRoot();
        if (!Directory.Exists(cacheRoot))
            return null;

        await using var cacheLock = await AcquireLockAsync(cacheRoot, cancellationToken).ConfigureAwait(false);
        return await TryLoadCurrentAsync(cacheRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task PromoteAsync(
        ArtifactCandidate candidate,
        bool cleanupObsoleteSnapshots = true,
        CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        if (!candidate.RequiresPromotion || candidate.CacheRoot is null)
            return;

        var cacheRoot = candidate.CacheRoot;
        await using var cacheLock = await AcquireLockAsync(cacheRoot, cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(candidate.Snapshot.DirectoryPath))
            throw new ArtifactException("Candidate snapshot directory no longer exists.");

        var snapshotsRoot = Path.Combine(cacheRoot, "snapshots");
        var relative = Path.GetRelativePath(snapshotsRoot, candidate.Snapshot.DirectoryPath);
        var record = new CurrentCacheRecord(
            relative,
            candidate.Snapshot.ArtifactSetId,
            source.SourceKind,
            source.SourceIdentity,
            candidate.Snapshot.SourceRevision,
            candidate.Snapshot.ArtifactFingerprint,
            cacheIdentity,
            candidate.Snapshot.AssetPaths.ToArray());

        var currentPath = Path.Combine(cacheRoot, "current.json");
        var tempPath = currentPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(record, ArtifactJsonContext.Default.CurrentCacheRecord);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, currentPath, overwrite: true);

        DeleteCandidateLeaseBestEffort(cacheRoot, candidate.Snapshot.DirectoryPath);

        if (cleanupObsoleteSnapshots)
            await DeleteOtherSnapshotsBestEffortAsync(cacheRoot, candidate.Snapshot.DirectoryPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task DiscardAsync(ArtifactCandidate candidate, CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        if (!candidate.RequiresPromotion || candidate.CacheRoot is null)
            return;

        await using var cacheLock = await AcquireLockAsync(candidate.CacheRoot, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(candidate.Snapshot.DirectoryPath))
            await DeleteDirectoryWithRetriesAsync(candidate.Snapshot.DirectoryPath, cancellationToken, swallowFinalFailure: false).ConfigureAwait(false);
        DeleteCandidateLeaseBestEffort(candidate.CacheRoot, candidate.Snapshot.DirectoryPath);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (source.SourceKind == "local-directory")
            return;

        var cacheRoot = GetCacheRoot();
        if (!Directory.Exists(cacheRoot))
            return;

        await using var cacheLock = await AcquireLockAsync(cacheRoot, cancellationToken).ConfigureAwait(false);
        CleanupStaging(cacheRoot);
        CleanupDanglingCandidateLeases(cacheRoot);
        var current = await TryLoadCurrentAsync(cacheRoot, cancellationToken).ConfigureAwait(false);
        if (current is not null)
            await DeleteOtherSnapshotsBestEffortAsync(cacheRoot, current.DirectoryPath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (ownsHttpClient)
            httpClient.Dispose();
    }

    private async Task<ArtifactCandidate> CreateLocalCandidateAsync(ResolvedArtifactSet resolved, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(resolved.LocalDirectory ?? throw new ArtifactSourceException("Local source did not provide a directory."));
        var paths = resolved.Assets.Select(asset => NormalizeAssetPath(asset.Path)).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = await ComputeFingerprintAsync(directory, paths, cancellationToken).ConfigureAwait(false);
        var snapshot = new ArtifactSnapshot(
            resolved.ArtifactSetId,
            source.SourceKind,
            source.SourceIdentity,
            resolved.Revision,
            fingerprint,
            directory,
            paths,
            isManaged: false);
        return new ArtifactCandidate(snapshot, requiresPromotion: false, isOfflineFallback: false, cacheRoot: null, cacheIdentity);
    }

    private async Task<ArtifactSnapshot> DownloadCandidateAsync(
        string cacheRoot,
        ResolvedArtifactSet remote,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(cacheRoot, "staging", Guid.NewGuid().ToString("N"));
        string? finalDirectory = null;
        Directory.CreateDirectory(stagingRoot);

        try
        {
            var normalizedAssets = remote.Assets
                .Select(asset => (Asset: asset, Relative: NormalizeAssetPath(asset.Path)))
                .ToArray();

            foreach (var (_, relative) in normalizedAssets)
                EnsureContained(stagingRoot, relative);

            foreach (var (asset, relative) in normalizedAssets)
            {
                var destination = EnsureContained(stagingRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAssetAsync(asset, destination, cancellationToken).ConfigureAwait(false);
            }

            var paths = normalizedAssets
                .Select(item => item.Relative.Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var fingerprint = await ComputeFingerprintAsync(stagingRoot, paths, cancellationToken).ConfigureAwait(false);

            var snapshotsRoot = Path.Combine(cacheRoot, "snapshots");
            Directory.CreateDirectory(snapshotsRoot);

            var snapshotName = $"{ArtifactPathSafety.Sanitize(remote.Revision)}-{fingerprint[..12]}-{Guid.NewGuid():N}";
            finalDirectory = Path.Combine(snapshotsRoot, snapshotName);
            Directory.Move(stagingRoot, finalDirectory);
            await CreateCandidateLeaseAsync(cacheRoot, finalDirectory, cancellationToken).ConfigureAwait(false);

            return new ArtifactSnapshot(
                remote.ArtifactSetId,
                source.SourceKind,
                source.SourceIdentity,
                remote.Revision,
                fingerprint,
                finalDirectory,
                paths,
                isManaged: true);
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
                DeleteDirectoryBestEffort(stagingRoot);
            if (finalDirectory is not null && Directory.Exists(finalDirectory))
                DeleteDirectoryBestEffort(finalDirectory);
            if (finalDirectory is not null)
                DeleteCandidateLeaseBestEffort(cacheRoot, finalDirectory);
            throw;
        }
    }

    private async Task DownloadAssetAsync(ArtifactAsset asset, string destination, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= options.DownloadRetries; attempt++)
        {
            var partial = destination + ".partial";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.Uri);
                source.ApplyDownloadHeaders(request);
                using var response = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (IsTransient(response.StatusCode) && attempt < options.DownloadRetries)
                {
                    await DelayAsync(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new ArtifactDownloadException($"Downloading '{asset.Path}' returned HTTP {(int)response.StatusCode}.");

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                var length = new FileInfo(partial).Length;
                if (asset.Size is { } expectedSize && expectedSize >= 0 && length != expectedSize)
                    throw new ArtifactDownloadException($"Downloaded '{asset.Path}' is {length} bytes; expected {expectedSize}.");

                if (!string.IsNullOrWhiteSpace(asset.Sha256))
                {
                    var actual = await Sha256FileAsync(partial, cancellationToken).ConfigureAwait(false);
                    if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new ArtifactDownloadException($"SHA-256 mismatch for '{asset.Path}'.");
                }

                File.Move(partial, destination, overwrite: true);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < options.DownloadRetries)
            {
                lastError = ex;
                await DelayAsync(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (attempt < options.DownloadRetries)
            {
                lastError = ex;
                await DelayAsync(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(partial) && !File.Exists(destination))
                {
                    try { File.Delete(partial); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        throw new ArtifactDownloadException(
            $"Failed to download '{asset.Path}'.",
            lastError ?? new IOException("Download failed after retries."));
    }

    private async Task<ArtifactSnapshot?> TryLoadCurrentAsync(string cacheRoot, CancellationToken cancellationToken)
    {
        var currentPath = Path.Combine(cacheRoot, "current.json");
        if (!File.Exists(currentPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(currentPath, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize(json, ArtifactJsonContext.Default.CurrentCacheRecord);
            if (record is null || !string.Equals(record.CacheIdentity, cacheIdentity, StringComparison.Ordinal))
                return null;

            var snapshotsRoot = Path.Combine(cacheRoot, "snapshots");
            var directory = EnsureContained(snapshotsRoot, record.DirectoryName);
            if (!Directory.Exists(directory))
                return null;

            return new ArtifactSnapshot(
                record.ArtifactSetId,
                record.SourceKind,
                record.SourceIdentity,
                record.SourceRevision,
                record.Fingerprint,
                directory,
                record.AssetPaths,
                isManaged: true);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArtifactException)
        {
            return null;
        }
    }

    private string GetCacheRoot()
    {
        var root = string.IsNullOrWhiteSpace(options.CacheDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelArtifacts.NET", "artifacts")
            : Path.GetFullPath(options.CacheDirectory);
        var label = ArtifactPathSafety.Sanitize(options.CacheKey ?? $"{source.SourceKind}-{source.SourceIdentity}-{source.CacheVariant}");
        return Path.Combine(root, $"{label}-{cacheIdentity[..12]}");
    }

    private async Task<FileStream> AcquireLockAsync(string cacheRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheRoot);
        var path = Path.Combine(cacheRoot, ".lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await DelayAsync(options.LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void CleanupStaging(string cacheRoot)
    {
        var staging = Path.Combine(cacheRoot, "staging");
        if (!Directory.Exists(staging))
            return;

        foreach (var directory in Directory.EnumerateDirectories(staging))
            DeleteDirectoryBestEffort(directory);
    }

    private void CleanupDanglingCandidateLeases(string cacheRoot)
    {
        var leasesRoot = Path.Combine(cacheRoot, CandidateLeasesDirectoryName);
        if (!Directory.Exists(leasesRoot))
            return;

        var snapshotsRoot = Path.Combine(cacheRoot, "snapshots");
        foreach (var lease in Directory.EnumerateFiles(leasesRoot, "*.lease", SearchOption.TopDirectoryOnly))
        {
            var snapshotName = Path.GetFileNameWithoutExtension(lease);
            var snapshot = Path.Combine(snapshotsRoot, snapshotName);
            if (Directory.Exists(snapshot))
                continue;
            try { File.Delete(lease); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DeleteOtherSnapshotsBestEffortAsync(
        string cacheRoot,
        string activeDirectory,
        CancellationToken cancellationToken)
    {
        var snapshotsRoot = Path.Combine(cacheRoot, "snapshots");
        if (!Directory.Exists(snapshotsRoot))
            return;

        foreach (var directory in Directory.EnumerateDirectories(snapshotsRoot))
        {
            if (PathsEqual(directory, activeDirectory) || HasCandidateLease(cacheRoot, directory))
                continue;
            await DeleteDirectoryWithRetriesAsync(directory, cancellationToken, swallowFinalFailure: true).ConfigureAwait(false);
        }
    }

    private async Task DeleteDirectoryWithRetriesAsync(
        string directory,
        CancellationToken cancellationToken,
        bool swallowFinalFailure)
    {
        for (var attempt = 0; attempt <= options.LockedFileDeleteRetries; attempt++)
        {
            try
            {
                if (options.DeleteDirectoryOverride is not null)
                    await options.DeleteDirectoryOverride(directory, cancellationToken).ConfigureAwait(false);
                else
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= options.LockedFileDeleteRetries)
                {
                    if (swallowFinalFailure)
                        return;
                    throw;
                }
                await DelayAsync(options.LockedFileDeleteRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CreateCandidateLeaseAsync(string cacheRoot, string snapshotDirectory, CancellationToken cancellationToken)
    {
        var lease = GetCandidateLeasePath(cacheRoot, snapshotDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(lease)!);
        await File.WriteAllTextAsync(lease, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasCandidateLease(string cacheRoot, string snapshotDirectory) =>
        File.Exists(GetCandidateLeasePath(cacheRoot, snapshotDirectory));

    private static void DeleteCandidateLeaseBestEffort(string cacheRoot, string snapshotDirectory)
    {
        var lease = GetCandidateLeasePath(cacheRoot, snapshotDirectory);
        try
        {
            if (File.Exists(lease))
                File.Delete(lease);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetCandidateLeasePath(string cacheRoot, string snapshotDirectory)
    {
        var snapshotName = Path.GetFileName(Path.TrimEndingDirectorySeparator(snapshotDirectory));
        return Path.Combine(cacheRoot, CandidateLeasesDirectoryName, snapshotName + ".lease");
    }

    private static void DeleteDirectoryBestEffort(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        options.DelayOverride?.Invoke(delay, cancellationToken) ?? Task.Delay(delay, cancellationToken);

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait;
        }
        return TimeSpan.FromSeconds(attempt);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    private static string BuildCacheIdentity(IModelArtifactSource source, string? cacheKey)
    {
        var value = string.Join("\n", source.SourceKind, source.SourceIdentity, source.CacheVariant, cacheKey ?? string.Empty);
        return ArtifactPathSafety.StableHash(value);
    }

    private static void ValidateResolvedAssets(ResolvedArtifactSet remote)
    {
        if (string.IsNullOrWhiteSpace(remote.ArtifactSetId))
            throw new ArtifactSourceException("Resolved artifact set ID is empty.");
        if (string.IsNullOrWhiteSpace(remote.Revision))
            throw new ArtifactSourceException("Resolved artifact revision is empty.");
        if (remote.Assets.Count == 0)
            throw new ArtifactSourceException("Resolved artifact set contains no assets.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in remote.Assets)
        {
            var normalized = NormalizeAssetPath(asset.Path).Replace('\\', '/');
            if (!paths.Add(normalized))
                throw new ArtifactSourceException($"Resolved artifact set contains duplicate path '{asset.Path}'.");
        }
    }

    private static string NormalizeAssetPath(string path) => ArtifactPathSafety.ValidateRelativePath(path);

    private static string EnsureContained(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var destination = Path.GetFullPath(Path.Combine(rootFull, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destination.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            throw new ArtifactDownloadException($"Artifact path '{relative}' escapes its managed root.");
        return destination;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            comparison);
    }

    private static async Task<string> ComputeFingerprintAsync(
        string directory,
        IReadOnlyList<string> assetPaths,
        CancellationToken cancellationToken)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in assetPaths.Select(path => path.Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            var fullPath = EnsureContained(directory, ArtifactPathSafety.ValidateRelativePath(relative));
            if (!File.Exists(fullPath))
                throw new ArtifactException($"Managed artifact '{relative}' does not exist.");

            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
            aggregate.AppendData(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private void ValidateCandidate(ArtifactCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.CacheIdentity, cacheIdentity, StringComparison.Ordinal))
            throw new ArtifactException("Candidate belongs to a different artifact manager/cache identity.");
    }
}

internal sealed record CurrentCacheRecord(
    string DirectoryName,
    string ArtifactSetId,
    string SourceKind,
    string SourceIdentity,
    string SourceRevision,
    string Fingerprint,
    string CacheIdentity,
    string[] AssetPaths);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(CurrentCacheRecord))]
[JsonSerializable(typeof(HuggingFaceApiModel))]
[JsonSerializable(typeof(HttpManifestDto))]
internal sealed partial class ArtifactJsonContext : JsonSerializerContext
{
}
