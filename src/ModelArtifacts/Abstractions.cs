using System.Security.Cryptography;
using System.Text;

namespace ModelArtifacts;

public enum ArtifactUpdatePolicy
{
    OnStartup = 1,
    Manual = 2,
    Never = 3
}

public sealed record ArtifactAsset(string Path, Uri Uri, long? Size = null, string? Sha256 = null);

public sealed record ResolvedArtifactSet(
    string ArtifactSetId,
    string Revision,
    IReadOnlyList<ArtifactAsset> Assets,
    string? LocalDirectory = null);

public interface IModelArtifactSource
{
    string SourceKind { get; }
    string SourceIdentity { get; }
    string CacheVariant { get; }
    Task<ResolvedArtifactSet> ResolveAsync(HttpClient httpClient, CancellationToken cancellationToken);
    void ApplyDownloadHeaders(HttpRequestMessage request) { }
}

public sealed class ArtifactSelection
{
    private readonly string[] patterns;
    private readonly string[] paths;
    private readonly bool selectAll;

    private ArtifactSelection(string identity, string[] paths, string[] patterns, bool selectAll)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        Identity = identity;
        this.paths = paths;
        this.patterns = patterns;
        this.selectAll = selectAll;
    }

    public string Identity { get; }

    public static ArtifactSelection Explicit(string identity, params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0) throw new ArgumentException("At least one asset path is required.", nameof(paths));
        return new ArtifactSelection(identity, Normalize(paths), [], false);
    }

    public static ArtifactSelection Patterns(string identity, params string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Length == 0) throw new ArgumentException("At least one asset pattern is required.", nameof(patterns));
        return new ArtifactSelection(identity, [], Normalize(patterns), false);
    }

    public static ArtifactSelection All(string identity = "all") => new(identity, [], [], true);

    internal bool Matches(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (selectAll) return true;
        if (paths.Any(x => string.Equals(x, normalized, StringComparison.Ordinal))) return true;
        return patterns.Any(pattern => GlobMatch(pattern, normalized));
    }

    private static string[] Normalize(IEnumerable<string> values) => values.Select(value =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Replace('\\', '/').TrimStart('/');
    }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool GlobMatch(string pattern, string value)
    {
        var p = 0;
        var v = 0;
        var star = -1;
        var checkpoint = -1;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v])) { p++; v++; continue; }
            if (p < pattern.Length && pattern[p] == '*') { star = p++; checkpoint = v; continue; }
            if (star >= 0) { p = star + 1; v = ++checkpoint; continue; }
            return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

public sealed class ArtifactManagerOptions
{
    public string? CacheDirectory { get; set; }
    public string? CacheKey { get; set; }
    public ArtifactUpdatePolicy UpdatePolicy { get; set; } = ArtifactUpdatePolicy.OnStartup;
    public int DownloadRetries { get; set; } = 3;
    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public int LockedFileDeleteRetries { get; set; } = 5;
    public TimeSpan LockedFileDeleteRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    internal Func<TimeSpan, CancellationToken, Task>? DelayOverride { get; set; }
    internal Func<string, CancellationToken, Task>? DeleteDirectoryOverride { get; set; }

    internal void Validate()
    {
        if (DownloadRetries < 1) throw new ArgumentOutOfRangeException(nameof(DownloadRetries));
        if (LockRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(LockRetryDelay));
        if (LockedFileDeleteRetries < 0) throw new ArgumentOutOfRangeException(nameof(LockedFileDeleteRetries));
        if (LockedFileDeleteRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(LockedFileDeleteRetryDelay));
    }
}

public sealed class ArtifactSnapshot
{
    internal ArtifactSnapshot(string artifactSetId, string sourceKind, string sourceIdentity, string sourceRevision, string artifactFingerprint, string directoryPath, IReadOnlyList<string> assetPaths, bool isManaged)
    {
        ArtifactSetId = artifactSetId;
        SourceKind = sourceKind;
        SourceIdentity = sourceIdentity;
        SourceRevision = sourceRevision;
        ArtifactFingerprint = artifactFingerprint;
        DirectoryPath = directoryPath;
        AssetPaths = assetPaths;
        IsManaged = isManaged;
    }

    public string ArtifactSetId { get; }
    public string SourceKind { get; }
    public string SourceIdentity { get; }
    public string SourceRevision { get; }
    public string ArtifactFingerprint { get; }
    public string DirectoryPath { get; }
    public IReadOnlyList<string> AssetPaths { get; }
    public bool IsManaged { get; }

    public string GetAssetPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = ArtifactPathSafety.ValidateRelativePath(relativePath);
        return Path.GetFullPath(Path.Combine(DirectoryPath, normalized));
    }
}

public sealed class ArtifactCandidate
{
    internal ArtifactCandidate(ArtifactSnapshot snapshot, bool requiresPromotion, bool isOfflineFallback, string? cacheRoot, string cacheIdentity)
    {
        Snapshot = snapshot;
        RequiresPromotion = requiresPromotion;
        IsOfflineFallback = isOfflineFallback;
        CacheRoot = cacheRoot;
        CacheIdentity = cacheIdentity;
    }

    public ArtifactSnapshot Snapshot { get; }
    public bool RequiresPromotion { get; }
    public bool IsOfflineFallback { get; }
    internal string? CacheRoot { get; }
    internal string CacheIdentity { get; }
}

public class ArtifactException : Exception
{
    public ArtifactException(string message) : base(message) { }
    public ArtifactException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ArtifactSourceException : ArtifactException
{
    public ArtifactSourceException(string message) : base(message) { }
    public ArtifactSourceException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ArtifactDownloadException : ArtifactException
{
    public ArtifactDownloadException(string message) : base(message) { }
    public ArtifactDownloadException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class ArtifactPathSafety
{
    internal static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArtifactDownloadException($"Invalid artifact path '{path}'.");
        var normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(x => x == ".."))
            throw new ArtifactDownloadException($"Invalid artifact path '{path}'.");
        return normalized;
    }

    internal static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var result = new string(chars);
        return result.Length <= 80 ? result : result[..80];
    }
}
